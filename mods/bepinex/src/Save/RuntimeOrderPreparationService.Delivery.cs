using MystiaStewardCompanion.LocalApi;
using System.Reflection;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private const int PatientRecoverPerDeliveredItem = 15;

    private enum RuntimeDeliveryItemKind
    {
        Food,
        Beverage,
    }

    private enum RuntimeDeliveryCommitState
    {
        NotCommitted,
        Committed,
        Uncertain,
    }

    private readonly record struct RuntimeDeliveryCommitResult(
        RuntimeDeliveryCommitState State,
        string Message,
        string Code = "")
    {
        public bool Ok => State == RuntimeDeliveryCommitState.Committed;
        public bool CommitUncertain => State == RuntimeDeliveryCommitState.Uncertain;
    }

    private static bool TryCaptureActiveNightBusinessGeneration(out long generation)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        generation = lifecycle.Generation;
        return lifecycle.IsActive;
    }

    private static bool IsNightBusinessGenerationActive(long expectedGeneration)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        return lifecycle.IsActive && lifecycle.Generation == expectedGeneration;
    }

    /// <summary>
    /// 按游戏原生上菜顺序提交一项料理或酒水。
    /// </summary>
    /// <param name="runtimeOrder">已匹配到的运行时订单、客人控制器和客人管理器。</param>
    /// <param name="sellable">准备送达的游戏 Sellable 对象。</param>
    /// <param name="kind">送达对象类型，用于选择订单字段和桌面显示器入口。</param>
    /// <param name="itemName">用户可读名称，仅用于错误信息。</param>
    /// <returns>是否完成送达提交，以及可展示给前端的诊断消息。</returns>
    /// <remarks>
    /// 旧实现直接写入 <c>ServFood</c> / <c>ServBeverage</c>，会绕过桌面 Sprite 和原生“空中待送达”状态。
    /// 这里先验证桌面显示器和 Sprite，再设置 <c>Served*InAir</c>、更新桌面显示并提交最终字段。
    /// 如果中途失败，会尽力清理空中状态，避免订单残留半提交数据。
    /// </remarks>
    private static RuntimeDeliveryCommitResult TryCommitRuntimeDelivery(
        RuntimeOrderMatch runtimeOrder,
        object sellable,
        RuntimeDeliveryItemKind kind,
        string itemName)
    {
        if (!TryCaptureActiveNightBusinessGeneration(out var sessionGeneration))
        {
            return NotCommittedDelivery($"无法送达 {itemName}：夜间经营会话已结束。");
        }

        if (runtimeOrder.Order == null)
        {
            return NotCommittedDelivery($"无法送达 {itemName}：订单对象不可用。");
        }

        if (runtimeOrder.Controller == null)
        {
            return NotCommittedDelivery($"无法送达 {itemName}：客人控制器不可用。");
        }

        if (!TryReadOrderServedItem(runtimeOrder.Order, kind, out var existingServedItem, out var existingDiagnostic))
        {
            return NotCommittedDelivery(
                $"无法送达 {itemName}：无法确认订单当前最终送达字段，本轮未执行送达副作用。{existingDiagnostic}");
        }

        if (existingServedItem != null)
        {
            return CompareObjectIdentity(existingServedItem, sellable) switch
            {
                RuntimeObjectIdentityComparison.Same =>
                    CommittedDelivery($"{itemName} 已存在于订单最终送达字段，本次未重复调用 setter。"),
                RuntimeObjectIdentityComparison.Different =>
                    NotCommittedDelivery($"无法送达 {itemName}：订单最终送达字段已有其他对象。"),
                _ => NotCommittedDelivery(
                    $"无法送达 {itemName}：订单最终送达对象身份不可确认，本轮未执行送达副作用。"),
            };
        }

        if (!TryReadSellableSprite(sellable, out var sprite, out var spriteMessage))
        {
            return NotCommittedDelivery($"无法送达 {itemName}：{spriteMessage}");
        }

        if (!TryFindGuestTableDisplayer(runtimeOrder.Order, runtimeOrder.Controller, out var tableDisplayer, out var tableMessage))
        {
            return NotCommittedDelivery($"无法送达 {itemName}：{tableMessage}");
        }

        if (!TryReadOrderInAirItem(runtimeOrder.Order, kind, out var existingInAirItem, out var inAirDiagnostic))
        {
            return NotCommittedDelivery(
                $"无法送达 {itemName}：无法确认订单待送达字段，本轮未执行送达副作用。{inAirDiagnostic}");
        }

        if (existingInAirItem != null
            && CompareObjectIdentity(existingInAirItem, sellable) != RuntimeObjectIdentityComparison.Same)
        {
            return NotCommittedDelivery($"无法送达 {itemName}：订单待送达字段已有其他对象，拒绝覆盖。");
        }

        if (existingInAirItem == null)
        {
            var inAirSetterName = kind == RuntimeDeliveryItemKind.Food
                ? "set_ServedFoodInAir"
                : "set_ServedBeverageInAir";
            var inAirSetterReturned = TryInvokeDeliverySetter(
                runtimeOrder.Order,
                inAirSetterName,
                sellable,
                out var inAirSetterAttempted,
                out var inAirSetterDiagnostic);
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return UncertainDelivery(
                    kind,
                    $"{itemName} 的待送达 setter 执行期间夜间经营会话已结束，已停止后续订单对象访问。");
            }
            if (!inAirSetterAttempted)
            {
                return NotCommittedDelivery($"无法送达 {itemName}：{inAirSetterDiagnostic}");
            }
            if (!TryReadOrderInAirItem(
                    runtimeOrder.Order,
                    kind,
                    out var writtenInAirItem,
                    out var writtenInAirDiagnostic))
            {
                return UncertainDelivery(
                    kind,
                    $"{itemName} 的待送达 setter 已执行，但无法确认字段状态，已禁止重复写入。{writtenInAirDiagnostic}");
            }

            if (writtenInAirItem == null)
            {
                return UncertainDelivery(
                    kind,
                    inAirSetterReturned
                        ? $"{itemName} 的待送达 setter 已执行，但字段仍为空，无法确认回调副作用，已禁止重试。"
                        : $"{itemName} 的待送达 setter 已进入后发生异常且字段为空，无法确认回调副作用，已禁止重试。{inAirSetterDiagnostic}");
            }

            var writtenIdentity = CompareObjectIdentity(writtenInAirItem, sellable);
            if (writtenIdentity == RuntimeObjectIdentityComparison.Unknown)
            {
                return UncertainDelivery(
                    kind,
                    $"{itemName} 的待送达 setter 已执行，但写入对象身份无法确认，已禁止重复写入。");
            }

            if (writtenIdentity == RuntimeObjectIdentityComparison.Different)
            {
                return UncertainDelivery(
                    kind,
                    $"{itemName} 的待送达 setter 已执行，但字段变为其他对象，无法确认副作用边界，已禁止重试。");
            }
        }

        var finalSetterAttempted = false;
        try
        {
            var visualUpdated = TryUpdateGuestTableVisual(tableDisplayer, kind, sprite, out var visualMessage);
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return UncertainDelivery(
                    kind,
                    $"{itemName} 的桌面显示更新期间夜间经营会话已结束，已停止后续订单对象访问。");
            }

            if (!visualUpdated)
            {
                var cleared = TryClearOrderInAirAndVerify(
                    runtimeOrder.Order,
                    kind,
                    sessionGeneration,
                    out var clearDiagnostic);
                if (!IsNightBusinessGenerationActive(sessionGeneration))
                {
                    return UncertainDelivery(
                        kind,
                        $"{itemName} 的待送达字段清理期间夜间经营会话已结束，已停止后续订单对象访问。");
                }

                if (!cleared)
                {
                    return UncertainDelivery(
                        kind,
                        $"无法送达 {itemName}：{visualMessage}；待送达字段清理状态无法确认，已禁止重试。{clearDiagnostic}");
                }

                return NotCommittedDelivery($"无法送达 {itemName}：{visualMessage}");
            }

            var clearedBeforeCommit = TryClearOrderInAirAndVerify(
                runtimeOrder.Order,
                kind,
                sessionGeneration,
                out var clearBeforeCommitDiagnostic);
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return UncertainDelivery(
                    kind,
                    $"{itemName} 的最终提交准备期间夜间经营会话已结束，已停止后续订单对象访问。");
            }

            if (!clearedBeforeCommit)
            {
                return UncertainDelivery(
                    kind,
                    $"无法送达 {itemName}：最终提交前无法确认待送达字段已清空，已禁止继续。{clearBeforeCommitDiagnostic}");
            }

            var setterName = kind == RuntimeDeliveryItemKind.Food ? "set_ServFood" : "set_ServBeverage";
            var setterReturned = TryInvokeDeliverySetter(
                runtimeOrder.Order,
                setterName,
                sellable,
                out finalSetterAttempted,
                out var setterDiagnostic);
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return UncertainDelivery(
                    kind,
                    $"{itemName} 的订单 setter 执行期间夜间经营会话已结束，已停止后续订单对象访问。");
            }
            if (!finalSetterAttempted)
            {
                TryUpdateGuestTableVisual(tableDisplayer, kind, null, out _);
                if (!IsNightBusinessGenerationActive(sessionGeneration))
                {
                    return UncertainDelivery(
                        kind,
                        $"{itemName} 的桌面显示回收期间夜间经营会话已结束，已停止后续订单对象访问。");
                }
                return NotCommittedDelivery($"无法送达 {itemName}：{setterDiagnostic}");
            }

            // IDA: both OrderBase setters write servFood/servBeverage before invoking the visual callback.
            // A callback exception therefore cannot be treated as a failed commit or retried blindly.
            if (!TryReadOrderServedItem(runtimeOrder.Order, kind, out var servedItem, out var servedDiagnostic))
            {
                return UncertainDelivery(kind,
                    $"{itemName} 的订单 setter 已执行，但无法确认最终字段，已禁止重复送达。{servedDiagnostic}");
            }

            var servedIdentity = servedItem == null
                ? RuntimeObjectIdentityComparison.Different
                : CompareObjectIdentity(servedItem, sellable);
            if (servedIdentity == RuntimeObjectIdentityComparison.Same)
            {
                var callbackSuffix = setterReturned ? "" : "视觉回调返回异常，但最终字段已确认写入；";
                return CommittedDelivery($"{itemName} 已按游戏送达流程提交。{callbackSuffix}");
            }

            if (servedItem != null)
            {
                return UncertainDelivery(kind,
                    servedIdentity == RuntimeObjectIdentityComparison.Unknown
                        ? $"{itemName} 的订单 setter 已执行，但最终对象身份无法确认，已禁止重复送达。"
                        : $"{itemName} 的订单 setter 已执行，但最终字段为其他对象，无法确认副作用边界，已禁止重复送达。");
            }

            TryUpdateGuestTableVisual(tableDisplayer, kind, null, out _);
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return UncertainDelivery(
                    kind,
                    $"{itemName} 的桌面显示回收期间夜间经营会话已结束，已停止后续订单对象访问。");
            }
            return UncertainDelivery(
                kind,
                setterReturned
                    ? $"{itemName} 的订单 setter 已执行，但最终字段仍为空，无法确认回调副作用，已禁止重复送达。"
                    : $"{itemName} 的订单 setter 已进入后发生异常且最终字段为空，无法确认回调副作用，已禁止重复送达。{setterDiagnostic}");
        }
        catch (Exception ex)
        {
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return UncertainDelivery(
                    kind,
                    $"{itemName} 的送达入口执行期间夜间经营会话已结束，已跳过订单清理并停止后续对象访问。");
            }

            TryClearOrderInAirAndVerify(runtimeOrder.Order, kind, sessionGeneration, out _);
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return UncertainDelivery(
                    kind,
                    $"{itemName} 的异常清理期间夜间经营会话已结束，已停止后续订单对象访问。");
            }
            return finalSetterAttempted
                ? UncertainDelivery(
                    kind,
                    $"{itemName} 的订单 setter 已开始执行，但发生未分类异常且无法确认最终字段；已禁止重复送达。{ex.GetBaseException().Message}")
                : NotCommittedDelivery($"无法送达 {itemName}：{ex.GetBaseException().Message}");
        }
    }

    private static RuntimeDeliveryCommitResult NotCommittedDelivery(string message)
    {
        return new RuntimeDeliveryCommitResult(RuntimeDeliveryCommitState.NotCommitted, message);
    }

    private static RuntimeDeliveryCommitResult CommittedDelivery(string message)
    {
        return new RuntimeDeliveryCommitResult(RuntimeDeliveryCommitState.Committed, message);
    }

    private static RuntimeDeliveryCommitResult UncertainDelivery(
        RuntimeDeliveryItemKind kind,
        string message)
    {
        var code = kind == RuntimeDeliveryItemKind.Food
            ? OrderPreparationStepCodes.FoodDeliveryCommitUncertain
            : OrderPreparationStepCodes.BeverageDeliveryCommitUncertain;
        return new RuntimeDeliveryCommitResult(RuntimeDeliveryCommitState.Uncertain, message, code);
    }

    private static bool TryReadOrderServedItem(
        object order,
        RuntimeDeliveryItemKind kind,
        out object? value,
        out string diagnostic)
    {
        return kind == RuntimeDeliveryItemKind.Food
            ? TryReadExactMemberValue(order, out value, out diagnostic, "ServFood", "servFood")
            : TryReadExactMemberValue(order, out value, out diagnostic, "ServBeverage", "servBeverage");
    }

    private static bool TryReadOrderInAirItem(
        object order,
        RuntimeDeliveryItemKind kind,
        out object? value,
        out string diagnostic)
    {
        return kind == RuntimeDeliveryItemKind.Food
            ? TryReadExactMemberValue(order, out value, out diagnostic, "ServedFoodInAir", "m_ServedFoodInAir")
            : TryReadExactMemberValue(order, out value, out diagnostic, "ServedBeverageInAir", "m_ServedBeverageInAir");
    }

    private static bool TryClearOrderInAirAndVerify(
        object order,
        RuntimeDeliveryItemKind kind,
        long sessionGeneration,
        out string diagnostic)
    {
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            diagnostic = "夜间经营会话已结束，未访问订单待送达字段";
            return false;
        }

        if (!TryReadOrderInAirItem(order, kind, out var before, out var beforeDiagnostic))
        {
            diagnostic = $"清理前字段不可读：{beforeDiagnostic}";
            return false;
        }

        if (before == null)
        {
            diagnostic = "";
            return true;
        }

        var setterName = kind == RuntimeDeliveryItemKind.Food ? "set_ServedFoodInAir" : "set_ServedBeverageInAir";
        var setterReturned = TryInvokeDeliverySetter(
            order,
            setterName,
            null,
            out var setterAttempted,
            out var setterDiagnostic);
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            diagnostic = $"{setterName} 执行期间夜间经营会话已结束，未继续读取订单字段";
            return false;
        }
        if (!setterAttempted)
        {
            diagnostic = setterDiagnostic;
            return false;
        }
        if (!TryReadOrderInAirItem(order, kind, out var current, out var readDiagnostic))
        {
            diagnostic = $"{setterName} 后字段不可读：{readDiagnostic}";
            return false;
        }

        if (current != null)
        {
            diagnostic = $"{setterName} 后字段仍非空";
            return false;
        }

        diagnostic = setterReturned
            ? ""
            : $"{setterName} 的视觉回调返回异常，但字段已确认清空：{setterDiagnostic}";
        return true;
    }

    private static bool TryInvokeDeliverySetter(
        object target,
        string methodName,
        object? value,
        out bool invocationAttempted,
        out string diagnostic)
    {
        invocationAttempted = false;
        diagnostic = "";
        var methods = target.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(candidate =>
            {
                if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal)) return false;
                var parameters = candidate.GetParameters();
                if (parameters.Length != 1) return false;
                return value == null
                    ? !parameters[0].ParameterType.IsValueType
                    : parameters[0].ParameterType.IsInstanceOfType(value);
            })
            .ToArray();
        if (methods.Length != 1)
        {
            diagnostic = methods.Length == 0
                ? $"未找到精确 {methodName}(Sellable) setter"
                : $"发现 {methods.Length} 个 {methodName}(Sellable) setter，无法确定唯一入口";
            return false;
        }

        invocationAttempted = true;
        try
        {
            methods[0].Invoke(target, new[] { value });
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = ex.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>
    /// 在订单未满足时恢复顾客耐心，等价于原生上菜面板的 onRecoverPatient 闭包。
    /// </summary>
    /// <remarks>
    /// 原游戏在一轮上菜后若订单仍未满足，会按成功提交的料理/酒水数量恢复耐心，每项固定 15。
    /// 自动化可能一轮内同时提交料理和酒水，因此恢复动作必须由调用方在本轮全部提交后统一触发。
    /// </remarks>
    private static bool TryRecoverPatientAfterPartialDelivery(RuntimeOrderMatch runtimeOrder, int deliveredItemCount, out string message)
    {
        if (deliveredItemCount <= 0)
        {
            message = "";
            return true;
        }

        if (runtimeOrder.Order == null || runtimeOrder.Controller == null)
        {
            message = "订单或客人控制器不可用，无法恢复顾客耐心。";
            return false;
        }

        if (ReadBool(InvokeInstance(runtimeOrder.Order, "get_IsFullfilled", Array.Empty<object?>())))
        {
            message = "";
            return true;
        }

        if (IsManualControlledOrder(runtimeOrder.Order, runtimeOrder.Controller))
        {
            message = "";
            return true;
        }

        if (!TryReadPatientBounds(runtimeOrder.Controller, out var currentPatient, out var maxPatient, out var patientMessage))
        {
            message = $"订单尚未补齐，但{patientMessage}，已跳过恢复顾客耐心以避免耐心条越界。";
            return true;
        }

        if (maxPatient <= 0)
        {
            message = $"订单尚未补齐，但顾客耐心上限异常（{maxPatient}），已跳过恢复顾客耐心。";
            return true;
        }

        if (currentPatient > maxPatient)
        {
            if (TryInvokeInstance(runtimeOrder.Controller, "SetPatient", new object?[] { maxPatient }))
            {
                message = $"订单尚未补齐，检测到顾客耐心 {currentPatient}/{maxPatient} 已超过上限，已校正为上限值。";
                return true;
            }

            message = $"订单尚未补齐，检测到顾客耐心 {currentPatient}/{maxPatient} 已超过上限，但无法调用 GuestGroupController.SetPatient 校正。";
            return false;
        }

        var requestedRecoverValue = PatientRecoverPerDeliveredItem * deliveredItemCount;
        var remainingPatient = maxPatient - Math.Max(0, currentPatient);
        if (remainingPatient <= 0)
        {
            message = "订单尚未补齐，顾客耐心已满，本轮不恢复耐心。";
            return true;
        }

        var recoverValue = Math.Min(requestedRecoverValue, remainingPatient);
        if (TryInvokeInstance(runtimeOrder.Controller, "AddPatient", new object?[] { recoverValue }))
        {
            message = recoverValue == requestedRecoverValue
                ? $"订单尚未补齐，已按游戏规则恢复顾客耐心 {recoverValue}。"
                : $"订单尚未补齐，已按耐心上限恢复顾客耐心 {recoverValue}（原计划 {requestedRecoverValue}）。";
            return true;
        }

        message = "订单尚未补齐，但无法调用 GuestGroupController.AddPatient 恢复顾客耐心。";
        return false;
    }

    private static bool TryReadPatientBounds(object controller, out int currentPatient, out int maxPatient, out string message)
    {
        currentPatient = 0;
        maxPatient = 0;

        var currentValue = ReadMember(controller, "CurrentPatient") ?? TryInvokeInstanceValue(controller, "get_CurrentPatient");
        if (!TryReadIntValue(currentValue, out currentPatient))
        {
            message = "无法读取 GuestGroupController.CurrentPatient";
            return false;
        }

        var maxValue = ReadMember(controller, "MaxPatient") ?? TryInvokeInstanceValue(controller, "get_MaxPatient");
        if (!TryReadIntValue(maxValue, out maxPatient))
        {
            message = "无法读取 GuestGroupController.MaxPatient";
            return false;
        }

        message = "";
        return true;
    }

    private static bool TryReadIntValue(object? value, out int number)
    {
        number = 0;
        if (value == null) return false;

        number = ToInt(value, int.MinValue);
        return number != int.MinValue;
    }

    private static bool AddPatientRecoveryStepIfNeeded(
        OrderPreparationResult result,
        RuntimeOrderMatch runtimeOrder,
        int deliveredItemCount)
    {
        if (!TryRecoverPatientAfterPartialDelivery(runtimeOrder, deliveredItemCount, out var message))
        {
            AddFailure(result, "恢复顾客耐心", message);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            result.Steps.Add(new OrderPreparationStep
            {
                Name = "恢复顾客耐心",
                Ok = true,
                Message = message,
            });
        }

        return true;
    }

    private static bool TryEvaluateOrderIfReady(
        OrderPreparationResult result,
        RuntimeOrderMatch runtimeOrder,
        string stepName,
        string orderLabel,
        CookingCollectionTarget safetyTarget,
        bool allowControllerMissing = false)
    {
        var evaluation = TryEvaluateRuntimeOrderIfReady(runtimeOrder, orderLabel, allowControllerMissing);
        if (!evaluation.Ok)
        {
            AddFailure(result, stepName, evaluation.Message, evaluation.Code);
            RecordOrderSafetyBarrierIfNeeded(evaluation.Code, safetyTarget, evaluation.Message);
            return false;
        }

        if (evaluation.Completed)
        {
            result.CompletedOrder = true;
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = stepName,
            Ok = true,
            Skipped = evaluation.Skipped,
            Message = evaluation.Message,
        });
        return true;
    }

    private static bool TryEvaluateMatchedAutomationOrderIfReady(
        OrderPreparationResult result,
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string stepName,
        string orderLabel,
        CookingCollectionTarget safetyTarget)
    {
        var evaluation = TryEvaluateMatchedAutomationOrderRuntimeIfReady(
            request,
            runtimeOrder,
            orderLabel,
            safetyTarget);
        if (!evaluation.Ok)
        {
            AddFailure(result, stepName, evaluation.Message, evaluation.Code);
            RecordOrderSafetyBarrierIfNeeded(evaluation.Code, safetyTarget, evaluation.Message);
            return false;
        }

        if (evaluation.Completed)
        {
            result.CompletedOrder = true;
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = stepName,
            Ok = true,
            Skipped = evaluation.Skipped,
            Message = evaluation.Message,
        });
        return true;
    }

    private static RuntimeOrderEvaluationResult TryEvaluateMatchedAutomationOrderRuntimeIfReady(
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string orderLabel,
        CookingCollectionTarget target)
    {
        if (IsYuumaBossTarget(target))
        {
            return new(
                false,
                false,
                false,
                "血池地狱订单只能由精确料理锅次结算事务触发评价。",
                OrderPreparationStepCodes.CookingPending);
        }

        if (RequiresNativeWackyKoishiBossEvaluationEntry(request))
        {
            return TryEvaluateWackyKoishiBossRuntimeOrderIfReady(request, runtimeOrder, orderLabel);
        }

        if (IsWackyKoishiBossRequest(request))
        {
            AppendWackyBossRuntimeDiagnostic(
                "automation-evaluate-generic",
                request,
                runtimeOrder,
                "call-generic-evaluate",
                "Koishi boss clue-stage order uses regular order evaluation.");
        }

        if (IsYuyukoBossRequest(request))
        {
            return TryEvaluateYuyukoChallengeRuntimeOrderIfReady(
                request,
                runtimeOrder,
                orderLabel,
                reacquireLiveOrder: target.Kind == CookingCollectionTargetKind.RareOrder);
        }

        return TryEvaluateRuntimeOrderIfReady(runtimeOrder, orderLabel);
    }

    private static bool TryEvaluateWackyKoishiBossOrderIfReady(
        OrderPreparationResult result,
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string stepName,
        string orderLabel,
        CookingCollectionTarget safetyTarget)
    {
        var evaluation = TryEvaluateWackyKoishiBossRuntimeOrderIfReady(request, runtimeOrder, orderLabel);
        if (!evaluation.Ok)
        {
            AddFailure(result, stepName, evaluation.Message, evaluation.Code);
            RecordOrderSafetyBarrierIfNeeded(evaluation.Code, safetyTarget, evaluation.Message);
            return false;
        }

        if (evaluation.Completed)
        {
            result.CompletedOrder = true;
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = stepName,
            Ok = true,
            Skipped = evaluation.Skipped,
            Message = evaluation.Message,
        });
        return true;
    }

    private static RuntimeOrderEvaluationResult TryEvaluateWackyKoishiBossRuntimeOrderIfReady(
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string orderLabel)
    {
        if (!TryCaptureActiveNightBusinessGeneration(out var sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        AppendWackyBossRuntimeDiagnostic(
            "koishi-native-evaluate-before",
            request,
            runtimeOrder,
            "call-native-evaluate-entry",
            "Koishi boss full-feed order enters the game EvaluateOrder pipeline so the boss OverrideEvaluationCallback can score it.");
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        var executable = IsExecutableWackyKoishiBossRuntimeOrder(
            runtimeOrder.Controller,
            runtimeOrder.Order,
            out var diagnostic);
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (!executable)
        {
            AppendWackyBossRuntimeDiagnostic(
                "koishi-native-evaluate-after",
                request,
                runtimeOrder,
                "blocked-native-evaluate-entry",
                diagnostic);
            return new(false, false, false, $"怪诞料理三阶段古明地恋本体订单缺少可执行原生评价条件：{diagnostic}");
        }

        var evaluation = TryEvaluateRuntimeOrderIfReady(
            runtimeOrder,
            orderLabel,
            expectedSessionGeneration: sessionGeneration);
        var decision = evaluation.Ok
            ? evaluation.Skipped
                ? "native-evaluate-entry-skipped"
                : "native-evaluate-entry-called"
            : "native-evaluate-entry-failed";
        if (IsNightBusinessGenerationActive(sessionGeneration))
        {
            AppendWackyBossRuntimeDiagnostic(
                "koishi-native-evaluate-after",
                request,
                runtimeOrder,
                decision,
                evaluation.Message);
        }
        return evaluation;
    }

    private static RuntimeOrderEvaluationResult TryEvaluateRuntimeOrderIfReady(
        RuntimeOrderMatch runtimeOrder,
        string orderLabel,
        bool allowControllerMissing = false,
        long? expectedSessionGeneration = null)
    {
        var sessionGeneration = expectedSessionGeneration ?? RuntimeNightBusinessLifecycle.Generation;
        if (expectedSessionGeneration.HasValue
            ? !IsNightBusinessGenerationActive(sessionGeneration)
            : !TryCaptureActiveNightBusinessGeneration(out sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (runtimeOrder.Order == null || runtimeOrder.Manager == null)
        {
            return new(false, false, false, "订单或客人管理器不可用，无法调用游戏评价流程。");
        }

        var isFullfilled = ReadBool(InvokeInstance(runtimeOrder.Order, "get_IsFullfilled", Array.Empty<object?>()));
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (!isFullfilled)
        {
            return new(true, false, true, "订单尚未同时满足料理和酒水，等待下一轮补齐。");
        }

        if (runtimeOrder.Controller == null)
        {
            if (allowControllerMissing)
            {
                return new(true, false, true, "订单已满足，但暂未读取到客人控制器，等待下一轮触发评价。");
            }

            return new(false, false, false, "已匹配订单，但未找到对应客人控制器，无法调用游戏评价流程。");
        }

        return TryInvokeRuntimeOrderEvaluationOnce(
            runtimeOrder.Manager,
            runtimeOrder.Controller,
            "EvaluateOrder",
            new object?[] { runtimeOrder.Controller, false, null },
            orderLabel,
            sessionGeneration);
    }

    private static RuntimeOrderEvaluationResult TryInvokeRuntimeOrderEvaluationOnce(
        object manager,
        object controller,
        string methodName,
        object?[] args,
        string orderLabel,
        long? expectedSessionGeneration = null)
    {
        var sessionGeneration = expectedSessionGeneration ?? RuntimeNightBusinessLifecycle.Generation;
        if (expectedSessionGeneration.HasValue
            ? !IsNightBusinessGenerationActive(sessionGeneration)
            : !TryCaptureActiveNightBusinessGeneration(out sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        var readBefore = TryReadRuntimeOrderEvaluated(controller, out var evaluatedBefore, out var beforeDiagnostic);
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (!readBefore)
        {
            return new(
                false,
                false,
                false,
                $"无法严格读取 {orderLabel} 的 HasEvaluated，已在调用游戏评价流程前停止：{beforeDiagnostic}",
                OrderPreparationStepCodes.OrderEvaluationStateUnreadable);
        }

        if (evaluatedBefore)
        {
            return new(true, true, true, $"{orderLabel}已触发过评价，本次不重复调用。");
        }

        try
        {
            InvokeInstance(manager, methodName, args);
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: true);
            }

            return new(true, true, false, $"已调用游戏评价流程完成{orderLabel}。");
        }
        catch (Exception ex)
        {
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: true, ex);
            }

            var readAfter = TryReadRuntimeOrderEvaluated(controller, out var evaluatedAfter, out var afterDiagnostic);
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: true, ex);
            }

            if (readAfter && evaluatedAfter)
            {
                return new(
                    true,
                    true,
                    false,
                    $"{orderLabel} 的评价调用后半段发生异常，但 HasEvaluated=true 已确认评价提交；不会重复调用。异常：{ex.GetBaseException().Message}");
            }

            var confirmation = string.IsNullOrWhiteSpace(afterDiagnostic)
                ? "HasEvaluated 仍为 false"
                : $"HasEvaluated 无法严格回读：{afterDiagnostic}";
            return new(
                false,
                false,
                false,
                $"{orderLabel} 的评价调用已开始但发生异常，且无法确认是否提交（{confirmation}）。为避免重复结算，已禁止自动重试，请人工确认订单状态：{ex.GetBaseException().Message}",
                OrderPreparationStepCodes.OrderEvaluationCommitUncertain);
        }
    }

    private static RuntimeOrderEvaluationResult BuildEndedNightBusinessEvaluation(
        string orderLabel,
        bool commitMayHaveStarted,
        Exception? exception = null)
    {
        var exceptionSuffix = exception == null
            ? ""
            : $" 原始异常：{exception.GetBaseException().Message}";
        if (commitMayHaveStarted)
        {
            return new(
                false,
                false,
                false,
                $"{orderLabel} 的评价调用期间夜间经营会话已结束，提交结果无法确认。已停止访问订单对象并禁止自动重试，请人工确认订单状态。{exceptionSuffix}",
                OrderPreparationStepCodes.OrderEvaluationCommitUncertain);
        }

        return new(
            false,
            false,
            false,
            $"夜间经营会话已结束，未访问 {orderLabel} 的评价对象。",
            OrderPreparationStepCodes.NightBusinessLifecycleUnavailable);
    }

    private static bool TryReadRuntimeOrderEvaluated(
        object controller,
        out bool evaluated,
        out string diagnostic)
    {
        evaluated = false;
        if (!TryReadExactMemberValue(
                controller,
                out var rawValue,
                out diagnostic,
                "HasEvaluated",
                "<HasEvaluated>k__BackingField"))
        {
            return false;
        }

        if (rawValue is bool boolean)
        {
            evaluated = boolean;
            diagnostic = "";
            return true;
        }

        diagnostic = rawValue == null
            ? "HasEvaluated 返回 null"
            : $"HasEvaluated 返回了非布尔类型 {rawValue.GetType().FullName}";
        return false;
    }

    private static bool IsManualControlledOrder(object order, object controller)
    {
        return ReadBool(ReadMember(order, "ManualOrder") ?? TryInvokeInstanceValue(order, "get_ManualOrder"))
            || ReadBool(ReadMember(controller, "IsControlled") ?? TryInvokeInstanceValue(controller, "get_IsControlled"));
    }

    private static bool TryReadSellableSprite(object sellable, out object? sprite, out string message)
    {
        var text = ReadMember(sellable, "Text") ?? TryInvokeInstanceValue(sellable, "get_Text");
        if (text != null)
        {
            sprite = ReadMember(text, "Visual")
                ?? TryInvokeInstanceValue(text, "get_Visual")
                ?? ReadMember(text, "_Visual_k__BackingField")
                ?? ReadMember(text, "<Visual>k__BackingField");
            if (sprite != null)
            {
                message = "";
                return true;
            }
        }

        try
        {
            // 该 helper 依赖运行时赋值的 BGGetter；未初始化时会返回 null。
            // 原生上菜面板的桌面显示直接读取 sellable.Text.Visual，因此这里只作为兜底。
            sprite = InvokeStatic(SellablePropertyHelperTypeName, "GetSellabeBGSprite", new object?[] { sellable });
            if (sprite != null)
            {
                message = "";
                return true;
            }

            message = "游戏未返回该料理或酒水的桌面 Sprite。";
            return false;
        }
        catch (Exception ex)
        {
            sprite = null;
            message = $"读取桌面 Sprite 失败：{ex.GetBaseException().Message}";
            return false;
        }
    }

    private static bool TryFindGuestTableDisplayer(object order, object controller, out object tableDisplayer, out string message)
    {
        tableDisplayer = new object();
        var deskCode = ToInt(ReadMember(order, "DeskCode")
            ?? TryInvokeInstanceValue(order, "get_DeskCode")
            ?? ReadMember(controller, "DeskCode")
            ?? TryInvokeInstanceValue(controller, "get_DeskCode"), -1);
        if (deskCode < 0)
        {
            message = "未读取到订单桌号，无法定位桌面显示器。";
            return false;
        }

        object? tileManager;
        try
        {
            tileManager = GetSingletonInstance(TileManagerTypeName);
        }
        catch (Exception ex)
        {
            message = $"读取 TileManager 失败：{ex.GetBaseException().Message}";
            return false;
        }

        if (tileManager == null)
        {
            message = "当前 TileManager 不可用，请确认已进入夜晚经营场景。";
            return false;
        }

        var guestTables = ReadMember(tileManager, "GuestTables") ?? TryInvokeInstanceValue(tileManager, "get_GuestTables");
        if (guestTables == null)
        {
            message = "未读取到 TileManager.GuestTables。";
            return false;
        }

        object? tableData = null;
        try
        {
            tableData = InvokeInstance(guestTables, "get_Item", new object?[] { deskCode });
        }
        catch
        {
            // 字典中没有该桌号时保持 null，由下方返回可诊断错误。
        }

        if (tableData == null)
        {
            message = $"TileManager.GuestTables 中没有桌 {deskCode + 1} 的数据。";
            return false;
        }

        var displayer = ReadMember(tableData, "tableDisplayer") ?? ReadMember(tableData, "TableDisplayer");
        if (displayer == null)
        {
            message = $"桌 {deskCode + 1} 的 GuestTableData 未包含 tableDisplayer。";
            return false;
        }

        tableDisplayer = displayer;
        message = "";
        return true;
    }

    private static bool TryUpdateGuestTableVisual(object tableDisplayer, RuntimeDeliveryItemKind kind, object? sprite, out string message)
    {
        var methodName = kind == RuntimeDeliveryItemKind.Food ? "SetFoodVisual" : "SetBeverageVisual";
        if (TryInvokeInstance(tableDisplayer, methodName, new[] { sprite }))
        {
            message = "";
            return true;
        }

        message = $"无法调用 GuestTableDisplayer.{methodName}。";
        return false;
    }

}
