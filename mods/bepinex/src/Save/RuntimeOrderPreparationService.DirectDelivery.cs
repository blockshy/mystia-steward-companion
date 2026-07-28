using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
using System.Reflection;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private enum WackyTagValidation
    {
        NotRequired,
        Matched,
        Mismatched,
        Unreadable,
    }

    /// <summary>
    /// 直接为订单创建并写入酒水。
    /// </summary>
    /// <remarks>
    /// 只有桌面显示和订单状态都提交成功后才扣减库存，避免库存与订单状态不一致。
    /// </remarks>
    private static (bool Ok, string Message, string Code) TryDeliverOrderBeverage(
        RuntimeOrderMatch runtimeOrder,
        int beverageId,
        string beverageName,
        string orderLabel)
    {
        if (!TryCaptureActiveNightBusinessGeneration(out var sessionGeneration))
        {
            return (false, "夜间经营会话已结束，未执行酒水送达。", OrderPreparationStepCodes.NightBusinessLifecycleUnavailable);
        }

        var currentQuantity = GetBeverageQuantity(beverageId);
        if (currentQuantity == 0)
        {
            return (false, $"{beverageName} 当前库存为 0，无法送达{orderLabel}。", "");
        }

        var sellable = InvokeStatic(DataBaseCoreTypeName, "AsNewBeverage", new object?[] { beverageId });
        if (sellable == null)
        {
            return (false, $"无法从游戏数据库创建酒水对象：{beverageName} #{beverageId}。", "");
        }

        var delivery = TryCommitRuntimeDelivery(runtimeOrder, sellable, RuntimeDeliveryItemKind.Beverage, beverageName);
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return (
                false,
                $"{beverageName} 的订单送达入口执行期间夜间经营会话已结束，已跳过库存扣减。",
                OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
        }

        if (!delivery.Ok)
        {
            return (false, delivery.Message, delivery.Code);
        }

        if (currentQuantity > 0)
        {
            try
            {
                InvokeRuntimeStorageOut("BeverageOut", beverageId);
            }
            catch (Exception ex)
            {
                return (
                    false,
                    $"{beverageName} 已确认送达{orderLabel}，但库存扣减结果无法确认；为避免重复扣库，自动化已停止：{ex.GetBaseException().Message}",
                    OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
            }
        }

        var quantityText = currentQuantity < 0 ? "无限库存" : $"剩余 {Math.Max(0, currentQuantity - 1)}";
        return (true, $"{beverageName} 已送达{orderLabel}（{quantityText}）。", "");
    }

    private static object? ReadOrderServedFood(object order)
    {
        return ReadMember(order, "ServFood")
            ?? TryInvokeInstanceValue(order, "get_ServFood");
    }

    private static object? ReadOrderServedBeverage(object order)
    {
        return ReadMember(order, "ServBeverage")
            ?? TryInvokeInstanceValue(order, "get_ServBeverage");
    }

    private static bool IsSellable(object? item, int sellableType, int id)
    {
        return item != null && ReadSellableType(item) == sellableType && ReadSellableId(item) == id;
    }

    private static bool IsFoodSellable(object? item)
    {
        return TryReadSellableIdentity(item, out var sellableType, out var id)
            && sellableType == 0
            && id >= 0;
    }

    private static bool TryReadSellableIdentity(object? item, out int sellableType, out int id)
    {
        sellableType = -1;
        id = -1;
        if (item == null) return false;

        sellableType = ReadSellableType(item);
        id = ReadSellableId(item);
        return sellableType >= 0 && id >= 0;
    }

    private static int ReadSellableType(object item)
    {
        var value = TryInvokeInstanceValue(item, "get_Type") ?? ReadMember(item, "Type");
        return ToInt(value, -1);
    }

    private static int ReadSellableId(object item)
    {
        var value = TryInvokeInstanceValue(item, "get_id")
            ?? TryInvokeInstanceValue(item, "get_Id")
            ?? ReadMember(item, "id")
            ?? ReadMember(item, "Id");
        return ToInt(value, -1);
    }

    /// <summary>
    /// 将已经出锅的料理直接送达给登记的目标订单。
    /// </summary>
    /// <remarks>
    /// 订单或桌面对象暂不可写时保留成品供下一轮重试；非目标成品会放入游戏料理暂存容器，送达成功后会执行厨具清理。
    /// </remarks>
    private static (bool Remove, string Message, string Code) TryDeliverAutomationCookedFood(AutomationCookingJob job, object cookedFood)
    {
        if (!TryCaptureActiveNightBusinessGeneration(out var sessionGeneration))
        {
            return StopAutomationFoodDeliveryForEndedSession(job, resolveCommit: false);
        }

        if (job.FoodDeliveryCommitUncertain)
        {
            return BlockUncertainFoodDelivery(job, "料理送达提交状态已锁定为无法确认。");
        }

        if (job.FoodDeliveryCommitted)
        {
            return TryCompleteCommittedFoodDeliveryCleanup(job);
        }

        if (!IsAutomationCookingJobOwned(job, out var ownershipDiagnostic))
        {
            var ownershipMessage = $"{job.RecipeName} 自动料理任务在送达前检测到厨具已开始新一锅，旧任务已退出且不会操作当前成品。{ownershipDiagnostic}";
            RecordAutomationRuntimeEvent(
                OrderPreparationStepCodes.CookingControllerReused,
                job,
                ownershipMessage,
                outcome: "interrupted",
                reasonCode: "cooking-controller-reused",
                terminal: true);
            return (true, ownershipMessage, OrderPreparationStepCodes.CookingControllerReused);
        }

        var target = job.Target;
        if (target.FoodId >= 0 && !IsSellable(cookedFood, sellableType: 0, id: target.FoodId))
        {
            var actualFoodId = ReadSellableId(cookedFood);
            var actualText = actualFoodId >= 0 ? $"料理 #{actualFoodId}" : "未知成品";
            var actualFoodTags = ReadFoodTagNames(cookedFood).ToArray();
            var activeTargetTags = target.WackyTargetFoodTags;
            AppendWackyCookingJobDiagnostic(
                "cooked-food-id-mismatch",
                job,
                "store-mismatched-food",
                actualFoodId,
                activeTargetTags,
                actualFoodTags,
                $"actual={actualText}; expected={target.FoodName}({target.FoodId})");
            var completion = new AutomationWarmerCompletion(
                OrderPreparationStepCodes.CookingMismatchStored,
                "interrupted",
                "cooking-food-mismatch-stored",
                $"{job.RecipeName} 已完成，但成品 {actualText} 不是目标料理 {target.FoodName}（料理 #{target.FoodId}），已放入保温箱并释放该自动料理任务，将在下一轮重试目标料理。",
                actualFoodId,
                activeTargetTags.ToArray(),
                actualFoodTags,
                "cooked-food-id-mismatch-stored");
            if (TryStoreMismatchedCookResultInWarmer(
                    job,
                    cookedFood,
                    completion,
                    out var storeMessage,
                    out var storeCommitted))
            {
                return CompleteCommittedWarmerStore(job, storeMessage);
            }

            AppendWackyCookingJobDiagnostic(
                storeCommitted ? "cooked-food-id-mismatch-reset-job" : "cooked-food-id-mismatch-store-failed",
                job,
                storeCommitted ? "retry-cooker-reset" : "keep-on-cooker",
                actualFoodId,
                activeTargetTags,
                actualFoodTags,
                storeMessage);
            return ContinueOrBlockAutomationDelivery(
                job,
                $"{job.RecipeName} 已完成，但成品 {actualText} 不是目标料理 {target.FoodName}（料理 #{target.FoodId}），{FormatWarmerStoreJobState(storeCommitted, storeMessage)}");
        }

        if (TryDetectWackyTargetSignatureChanged(job, out var originalTargetSignature, out var currentTargetSignature, out var originalTargetTags, out var currentTargetTags))
        {
            var actualFoodId = ReadSellableId(cookedFood);
            var actualTagsForSignature = ReadFoodTagNames(cookedFood).ToArray();
            var signatureMessage = $"{job.RecipeName} 已完成，但怪诞料理目标已变化：开锅时 {FormatWackyTargetForMessage(originalTargetSignature, originalTargetTags)}，当前 {FormatWackyTargetForMessage(currentTargetSignature, currentTargetTags)}";
            AppendWackyCookingJobDiagnostic(
                "wacky-target-signature-changed",
                job,
                "store-stale-target",
                actualFoodId,
                currentTargetTags,
                actualTagsForSignature,
                signatureMessage);
            var signatureCompletion = new AutomationWarmerCompletion(
                OrderPreparationStepCodes.CookingMismatchStored,
                "interrupted",
                "cooking-target-changed-stored",
                $"{signatureMessage}，已放入保温箱并释放该自动料理任务，将在下一轮按当前目标重新推荐并开锅。",
                actualFoodId,
                currentTargetTags.ToArray(),
                actualTagsForSignature,
                "wacky-target-signature-changed-stored");
            if (TryStoreMismatchedCookResultInWarmer(
                    job,
                    cookedFood,
                    signatureCompletion,
                    out var signatureStoreMessage,
                    out var signatureStoreCommitted))
            {
                return CompleteCommittedWarmerStore(job, signatureStoreMessage);
            }

            AppendWackyCookingJobDiagnostic(
                signatureStoreCommitted ? "wacky-target-signature-changed-reset-job" : "wacky-target-signature-changed-store-failed",
                job,
                signatureStoreCommitted ? "retry-cooker-reset" : "keep-on-cooker",
                actualFoodId,
                currentTargetTags,
                actualTagsForSignature,
                signatureStoreMessage);
            return ContinueOrBlockAutomationDelivery(
                job,
                $"{signatureMessage}，{FormatWarmerStoreJobState(signatureStoreCommitted, signatureStoreMessage)}");
        }

        var wackyTagValidation = ValidateWackyTags(target, cookedFood, out var targetTags, out var actualTags);
        if (wackyTagValidation is WackyTagValidation.Mismatched or WackyTagValidation.Unreadable)
        {
            var actualFoodId = ReadSellableId(cookedFood);
            var unreadableTags = wackyTagValidation == WackyTagValidation.Unreadable;
            var resultCode = unreadableTags
                ? OrderPreparationStepCodes.CookingTagsUnreadableStored
                : OrderPreparationStepCodes.CookingMismatchStored;
            var diagnosticPrefix = unreadableTags ? "cooked-food-tags-unreadable" : "cooked-food-tag-mismatch";
            var tagMessage = unreadableTags
                ? $"{job.RecipeName} 已完成，但无法读取成品 Tag，不能确认满足当前怪诞料理目标 Tag（{string.Join("、", targetTags)}）"
                : $"{job.RecipeName} 已完成，但成品 Tag（{string.Join("、", actualTags)}）不含当前怪诞料理目标 Tag（{string.Join("、", targetTags)}）";
            AppendWackyCookingJobDiagnostic(
                diagnosticPrefix,
                job,
                unreadableTags ? "store-unreadable-tags" : "store-tag-mismatch",
                actualFoodId,
                targetTags,
                actualTags,
                tagMessage);
            var nextAction = unreadableTags
                ? "已放入保温箱并暂停该订单自动化，请检查运行时 Tag 读取后再继续。"
                : "已放入保温箱并释放该自动料理任务，将在下一轮重新推荐并重试。";
            var tagCompletion = new AutomationWarmerCompletion(
                resultCode,
                unreadableTags ? "blocked" : "interrupted",
                unreadableTags ? "cooking-tags-unreadable-stored" : "cooking-tags-mismatch-stored",
                $"{tagMessage}，{nextAction}",
                actualFoodId,
                targetTags.ToArray(),
                actualTags.ToArray(),
                $"{diagnosticPrefix}-stored",
                RememberRejectedRecipe: !unreadableTags);
            if (TryStoreMismatchedCookResultInWarmer(
                    job,
                    cookedFood,
                    tagCompletion,
                    out var wackyStoreMessage,
                    out var wackyStoreCommitted))
            {
                return CompleteCommittedWarmerStore(job, wackyStoreMessage);
            }

            AppendWackyCookingJobDiagnostic(
                wackyStoreCommitted ? $"{diagnosticPrefix}-reset-job" : $"{diagnosticPrefix}-store-failed",
                job,
                wackyStoreCommitted ? "retry-cooker-reset" : "keep-on-cooker",
                actualFoodId,
                targetTags,
                actualTags,
                wackyStoreMessage);
            return ContinueOrBlockAutomationDelivery(
                job,
                $"{tagMessage}，{FormatWarmerStoreJobState(wackyStoreCommitted, wackyStoreMessage)}");
        }

        if (wackyTagValidation == WackyTagValidation.Matched)
        {
            AppendWackyCookingJobDiagnostic(
                "cooked-food-tag-match",
                job,
                "continue-delivery",
                ReadSellableId(cookedFood),
                targetTags,
                actualTags,
                "cooked food tags matched active wacky target tags");
        }

        var request = BuildOrderRequestFromCookingTarget(target);
        var runtimeOrder = target.Kind == CookingCollectionTargetKind.NormalOrder
            ? FindRuntimeNormalOrder(request)
            : FindRuntimeOrder(request);
        if (runtimeOrder.Order == null || runtimeOrder.Manager == null)
        {
            return HandleUndeliverableCookedFood(
                job,
                cookedFood,
                AutomationDeliveryFailureKind.MissingOrder,
                $"未找到目标订单对象。{runtimeOrder.Diagnostic}");
        }

        if (runtimeOrder.Controller == null)
        {
            return HandleUndeliverableCookedFood(
                job,
                cookedFood,
                AutomationDeliveryFailureKind.MissingController,
                $"已找到目标订单，但未读取到可执行客人控制器；该订单可能只残留在 HUD 中。{runtimeOrder.Diagnostic}");
        }

        job.ResetDeliveryFailures();

        if (!TryReadOrderServedItem(
                runtimeOrder.Order,
                RuntimeDeliveryItemKind.Food,
                out var servedFood,
                out var servedFoodDiagnostic))
        {
            return ContinueOrBlockAutomationDelivery(
                job,
                $"无法确认目标订单的 ServFood，当前不执行送达或入保温箱：{servedFoodDiagnostic}");
        }

        var servedFoodIdentity = servedFood == null
            ? RuntimeObjectIdentityComparison.Different
            : CompareObjectIdentity(servedFood, cookedFood);
        if (servedFoodIdentity == RuntimeObjectIdentityComparison.Same)
        {
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return StopAutomationFoodDeliveryForEndedSession(job, resolveCommit: false);
            }

            if (!job.FoodDeliveryCleanupTracker.TryBeginCommit())
            {
                return BlockUncertainFoodDelivery(job, "同一成品已在订单中，但 job 无法锁定提交状态。");
            }

            job.FoodDeliveryCleanupTracker.ResolveCommit(AutomationCommitResolution.Committed);
            job.DeliveredFood = cookedFood;
            job.FoodDeliveryCompletion = BuildFoodDeliveryCompletionSafely(
                job,
                runtimeOrder,
                request,
                cookedFood,
                targetTags,
                actualTags,
                $"{target.FoodName} 已存在于订单最终送达字段，本次未重复调用 setter。");
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return StopAutomationFoodDeliveryForEndedSession(job, resolveCommit: false);
            }
            return TryCompleteCommittedFoodDeliveryCleanup(job);
        }

        if (servedFoodIdentity == RuntimeObjectIdentityComparison.Unknown)
        {
            return BlockUncertainFoodDelivery(
                job,
                "订单已有料理，但无法确认它是否为本 job 成品；未重复送达、写入保温箱或清理厨具。");
        }

        if (servedFood != null)
        {
            var actualFoodId = ReadSellableId(cookedFood);
            var completion = new AutomationWarmerCompletion(
                OrderPreparationStepCodes.CookingTargetAlreadyServedStored,
                "completed",
                "cooking-target-already-served",
                $"{job.RecipeName} 已完成，但目标订单已有料理；自动化成品已放入保温箱。",
                actualFoodId,
                Array.Empty<string>(),
                Array.Empty<string>());
            if (TryStoreMismatchedCookResultInWarmer(
                    job,
                    cookedFood,
                    completion,
                    out var storeMessage,
                    out var storeCommitted))
            {
                return CompleteCommittedWarmerStore(job, storeMessage);
            }

            return ContinueOrBlockAutomationDelivery(
                job,
                $"目标订单已有料理，但{FormatWarmerStoreJobState(storeCommitted, storeMessage)}");
        }

        if (!TryReadOrderInAirItem(
                runtimeOrder.Order,
                RuntimeDeliveryItemKind.Food,
                out var pendingFood,
                out var pendingFoodDiagnostic))
        {
            return ContinueOrBlockAutomationDelivery(
                job,
                $"无法确认订单待送达料理，本轮未执行送达副作用：{pendingFoodDiagnostic}");
        }

        if (pendingFood != null && !IsSameObject(pendingFood, cookedFood))
        {
            return ContinueOrBlockAutomationDelivery(
                job,
                "订单已有其他待送达料理；即使料理 ID 相同也不会覆盖非本 job 的对象。");
        }

        if (IsWackyKoishiBossTarget(target))
        {
            AppendWackyBossRuntimeDiagnostic(
                "cooking-job-delivery-before",
                request,
                runtimeOrder,
                "commit-food",
                $"food={target.FoodName}; cookedFood={SpecialBusinessDiagnostics.DescribeObject(cookedFood)}");
        }
        if (IsYuyukoBossTarget(target))
        {
            AppendYuyukoRuntimeDiagnostic(
                "cooking-job-delivery-before",
                request,
                runtimeOrder,
                "commit-food",
                $"food={target.FoodName}; cookedFood={SpecialBusinessDiagnostics.DescribeObject(cookedFood)}");
        }

        if (!job.FoodDeliveryCleanupTracker.TryBeginCommit())
        {
            return BlockUncertainFoodDelivery(job, "料理送达提交状态已锁定，拒绝重复调用订单 setter。");
        }

        RuntimeDeliveryCommitResult delivery;
        try
        {
            delivery = TryCommitRuntimeDelivery(runtimeOrder, cookedFood, RuntimeDeliveryItemKind.Food, target.FoodName);
        }
        catch (Exception ex)
        {
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return StopAutomationFoodDeliveryForEndedSession(job, resolveCommit: true);
            }

            job.FoodDeliveryCleanupTracker.ResolveCommit(AutomationCommitResolution.Uncertain);
            return BlockUncertainFoodDelivery(
                job,
                $"料理送达调用发生未分类异常，无法确认最终字段：{ex.GetBaseException().Message}");
        }
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return StopAutomationFoodDeliveryForEndedSession(job, resolveCommit: true);
        }

        if (delivery.State == RuntimeDeliveryCommitState.NotCommitted)
        {
            job.FoodDeliveryCleanupTracker.ResolveCommit(AutomationCommitResolution.NotCommitted);
            return ContinueOrBlockAutomationDelivery(job, delivery.Message);
        }

        if (delivery.CommitUncertain)
        {
            job.FoodDeliveryCleanupTracker.ResolveCommit(AutomationCommitResolution.Uncertain);
            return BlockUncertainFoodDelivery(job, delivery.Message);
        }

        job.FoodDeliveryCleanupTracker.ResolveCommit(AutomationCommitResolution.Committed);
        job.DeliveredFood = cookedFood;
        job.FoodDeliveryCompletion = BuildFoodDeliveryCompletionSafely(
            job,
            runtimeOrder,
            request,
            cookedFood,
            targetTags,
            actualTags,
            delivery.Message);
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return StopAutomationFoodDeliveryForEndedSession(job, resolveCommit: false);
        }
        return TryCompleteCommittedFoodDeliveryCleanup(job);
    }

    private static (bool Remove, string Message, string Code) StopAutomationFoodDeliveryForEndedSession(
        AutomationCookingJob job,
        bool resolveCommit)
    {
        if (resolveCommit)
        {
            job.FoodDeliveryCleanupTracker.ResolveCommit(AutomationCommitResolution.Uncertain);
        }

        return (
            true,
            "夜间经营会话已结束，已停止料理送达和厨具后处理。",
            OrderPreparationStepCodes.NightBusinessLifecycleUnavailable);
    }

    private static AutomationFoodDeliveryCompletion BuildFoodDeliveryCompletionSafely(
        AutomationCookingJob job,
        RuntimeOrderMatch runtimeOrder,
        OrderPreparationRequest request,
        object cookedFood,
        IReadOnlyList<string> targetTags,
        IReadOnlyList<string> actualTags,
        string commitDetail)
    {
        try
        {
            return BuildFoodDeliveryCompletion(
                job,
                runtimeOrder,
                request,
                cookedFood,
                targetTags,
                actualTags,
                commitDetail);
        }
        catch (Exception ex)
        {
            return new AutomationFoodDeliveryCompletion(
                $"{job.Target.FoodName} 已确认送达订单；提交后处理发生异常且不会重复执行：{ex.GetBaseException().Message}。",
                job.Target.FoodId,
                targetTags.ToArray(),
                actualTags.ToArray());
        }
    }

    private static AutomationFoodDeliveryCompletion BuildFoodDeliveryCompletion(
        AutomationCookingJob job,
        RuntimeOrderMatch runtimeOrder,
        OrderPreparationRequest request,
        object cookedFood,
        IReadOnlyList<string> targetTags,
        IReadOnlyList<string> actualTags,
        string commitDetail)
    {
        var target = job.Target;
        var recoverSuffix = "";
        try
        {
            recoverSuffix = TryRecoverPatientAfterPartialDelivery(runtimeOrder, 1, out var recoverMessage)
                && !string.IsNullOrWhiteSpace(recoverMessage)
                    ? recoverMessage
                    : "";
        }
        catch (Exception ex)
        {
            recoverSuffix = $"料理已提交，但恢复顾客耐心时发生异常且不会重复执行：{ex.GetBaseException().Message}。";
        }

        var label = target.Kind == CookingCollectionTargetKind.NormalOrder ? "普客订单" : "稀客订单";

        var message = $"{target.FoodName} 已直接送达{label}。{commitDetail}";
        if (!string.IsNullOrWhiteSpace(recoverSuffix))
        {
            message += recoverSuffix;
        }

        if (IsYuyukoBossTarget(target))
        {
            AppendYuyukoRuntimeDiagnostic(
                "cooking-job-delivery-after",
                request,
                runtimeOrder,
                "food-delivered-committed",
                message);
        }

        return new AutomationFoodDeliveryCompletion(
            message,
            ReadSellableId(cookedFood),
            targetTags.ToArray(),
            actualTags.ToArray());
    }

    private static (bool Remove, string Message, string Code) TryCompleteCommittedFoodDeliveryCleanup(
        AutomationCookingJob job)
    {
        if (job.DeliveredFood == null || job.FoodDeliveryCompletion == null)
        {
            return BlockCommittedFoodDeliveryCleanup(job, "料理送达提交上下文不完整，无法安全复位厨具。");
        }

        if (!IsAutomationCookingJobOwned(job, out var ownershipDiagnostic))
        {
            return BlockCommittedFoodDeliveryCleanup(
                job,
                $"厨具已进入其他锅次，禁止清理当前厨具。{ownershipDiagnostic}");
        }

        if (!job.FoodDeliveryCleanupTracker.TryBeginAttempt(eligible: true))
        {
            return job.FoodDeliveryCleanupTracker.Exhausted
                ? BlockCommittedFoodDeliveryCleanup(job, "厨具复位重试次数已耗尽。")
                : (false, "", OrderPreparationStepCodes.CookingPending);
        }

        var resetSucceeded = TryResetCookControllerAfterCommittedSideEffect(
            job,
            job.DeliveredFood,
            out var resetMessage);
        if (!resetSucceeded)
        {
            if (job.FoodDeliveryCleanupTracker.Exhausted)
            {
                return BlockCommittedFoodDeliveryCleanup(job, resetMessage);
            }

            return (
                false,
                $"{job.RecipeName} 已确认送达订单且不会重复送达，正在重试同一锅次厨具复位（{job.FoodDeliveryCleanupAttempts}/{job.FoodDeliveryCleanupTracker.MaxAttempts}）：{resetMessage}",
                OrderPreparationStepCodes.CookingPending);
        }

        job.FoodDeliveryCleanupTracker.Complete();
        var completion = job.FoodDeliveryCompletion;
        var postResetMessage = CompleteCookerExtractionAfterReset(job);
        var message = $"{completion.Message}{resetMessage}{postResetMessage}";
        AppendWackyCookingJobDiagnostic(
            "cooked-food-delivered",
            job,
            "delivered-to-order",
            completion.ActualFoodId,
            completion.TargetTags,
            completion.ActualTags,
            message);
        RecordAutomationRuntimeEvent(
            OrderPreparationStepCodes.FoodDelivered,
            job,
            message,
            outcome: "completed",
            reasonCode: "food-delivered",
            terminal: true);
        return (true, message, OrderPreparationStepCodes.FoodDelivered);
    }

    private static string CompleteCookerExtractionAfterReset(AutomationCookingJob job)
    {
        var parts = new List<string>();
        try
        {
            var partnerManager = GetSingletonInstance(PartnerManagerTypeName);
            if (partnerManager == null
                || !TryInvokeInstance(partnerManager, "OnCookerAvailabilityUpdate", new object?[] { -1 }))
            {
                parts.Add("厨具已复位，但伙伴厨具可用性通知返回异常；该通知不会重复执行。");
            }
        }
        catch (Exception ex)
        {
            parts.Add($"厨具已复位，但伙伴厨具可用性通知发生异常且不会重复执行：{ex.GetBaseException().Message}。");
        }

        try
        {
            if (!TryInvokeInstance(job.CookController, "AfterPlayerExtract", Array.Empty<object?>()))
            {
                parts.Add("厨具出锅回调返回异常；为避免重复触发特殊厨具副作用，本 job 不会再次调用该回调。");
            }
        }
        catch (Exception ex)
        {
            parts.Add($"厨具出锅回调发生异常且不会重复执行：{ex.GetBaseException().Message}。");
        }

        return string.Concat(parts);
    }

    private static (bool Remove, string Message, string Code) BlockCommittedFoodDeliveryCleanup(
        AutomationCookingJob job,
        string detail)
    {
        var message = $"{job.RecipeName} 已确认送达订单且不会重复送达，但同一锅次厨具无法严格复位；自动料理任务已停止并保留现场。{detail}";
        RecordAutomationRuntimeEvent(
            OrderPreparationStepCodes.CookingDeliveryCleanupBlocked,
            job,
            message,
            job.FoodDeliveryCompletion?.ActualFoodId ?? -1,
            job.FoodDeliveryCompletion?.TargetTags,
            job.FoodDeliveryCompletion?.ActualTags,
            outcome: "blocked",
            reasonCode: "cooking-delivery-cleanup-failed",
            terminal: true);
        return (true, message, OrderPreparationStepCodes.CookingDeliveryCleanupBlocked);
    }

    private static (bool Remove, string Message, string Code) BlockUncertainFoodDelivery(
        AutomationCookingJob job,
        string detail)
    {
        var message = $"{job.RecipeName} 的订单送达提交状态无法确认；为避免重复送达或误清厨具，自动料理任务已停止并保留现场。{detail}";
        RecordAutomationRuntimeEvent(
            OrderPreparationStepCodes.CookingDeliveryCommitUncertain,
            job,
            message,
            job.DeliveredFood == null ? -1 : ReadSellableId(job.DeliveredFood),
            outcome: "blocked",
            reasonCode: "cooking-delivery-commit-uncertain",
            terminal: true);
        return (true, message, OrderPreparationStepCodes.CookingDeliveryCommitUncertain);
    }

    private static (bool Remove, string Message, string Code) ContinueOrBlockAutomationDelivery(AutomationCookingJob job, string message)
    {
        if (job.WarmerStoreCommitUncertain)
        {
            return BlockUncertainWarmerStore(job, message);
        }

        if (job.FoodDeliveryCommitUncertain)
        {
            return BlockUncertainFoodDelivery(job, message);
        }

        if (job.WarmerStoreCommitted)
        {
            return (
                false,
                $"{job.RecipeName} 的成品已写入保温箱，等待同一锅次厨具复位：{message}",
                OrderPreparationStepCodes.CookingPending);
        }

        if (job.DeliveryTimeoutClock.Elapsed >= CookingDeliveryTimeout)
        {
            var blockedMessage = $"{job.RecipeName} 自动送达达到有界重试上限：{message} 成品保留在厨具中，Mod 已释放所有权。";
            RecordAutomationRuntimeEvent(
                OrderPreparationStepCodes.CookingDeliveryBlocked,
                job,
                blockedMessage,
                outcome: "blocked",
                reasonCode: "cooking-delivery-timeout",
                terminal: true);
            return (true, blockedMessage, OrderPreparationStepCodes.CookingDeliveryBlocked);
        }

        return (false, $"{job.RecipeName} 已完成，等待直接送达：{message}", OrderPreparationStepCodes.CookingPending);
    }

    private static (bool Remove, string Message, string Code) HandleUndeliverableCookedFood(
        AutomationCookingJob job,
        object cookedFood,
        AutomationDeliveryFailureKind failureKind,
        string reason)
    {
        var failure = job.RecordDeliveryFailure(failureKind, job.DeliveryTimeoutClock.Elapsed);
        var threshold = GetDeliveryFailureRetireAttempts(failureKind);
        var delay = GetDeliveryFailureRetireDelay(failureKind);
        var failureAge = failure.EffectiveAge;
        var failureDetail = FormatDeliveryFailureDetail(failureKind, failure.Count, threshold, failureAge, delay);
        if (failure.Count < threshold || failureAge < delay)
        {
            return ContinueOrBlockAutomationDelivery(job, $"{reason}{failureDetail}");
        }

        var actualFoodId = ReadSellableId(cookedFood);
        var actualTags = ReadFoodTagNames(cookedFood).ToArray();
        if (IsWackyTargetContext(job.Target))
        {
            AppendWackyCookingJobDiagnostic(
                "wacky-undeliverable-target",
                job,
                "store-undeliverable-food",
                actualFoodId,
                job.Target.WackyTargetFoodTags,
                actualTags,
                $"{reason}{failureDetail}");
        }

        var targetLabel = failureKind == AutomationDeliveryFailureKind.MissingController
            ? "目标订单暂不可执行"
            : "目标订单已不存在、已切换或暂不可达";
        var completion = new AutomationWarmerCompletion(
            OrderPreparationStepCodes.CookingTargetUnavailableStored,
            "interrupted",
            failureKind == AutomationDeliveryFailureKind.MissingController
                ? "cooking-order-controller-unavailable-stored"
                : "cooking-order-unavailable-stored",
            $"{job.RecipeName} 已完成，但{targetLabel}，已放入保温箱并释放该自动料理任务。原因：{reason}{failureDetail} ",
            actualFoodId,
            job.Target.WackyTargetFoodTags.ToArray(),
            actualTags,
            IsWackyTargetContext(job.Target) ? "wacky-undeliverable-target-stored" : "");
        if (TryStoreMismatchedCookResultInWarmer(
                job,
                cookedFood,
                completion,
                out var storeMessage,
                out var storeCommitted))
        {
            return CompleteCommittedWarmerStore(job, storeMessage);
        }

        if (IsWackyTargetContext(job.Target))
        {
            AppendWackyCookingJobDiagnostic(
                storeCommitted ? "wacky-undeliverable-target-reset-job" : "wacky-undeliverable-target-store-failed",
                job,
                storeCommitted ? "retry-cooker-reset" : "keep-on-cooker",
                actualFoodId,
                job.Target.WackyTargetFoodTags,
                actualTags,
                storeMessage);
        }

        return ContinueOrBlockAutomationDelivery(
            job,
            $"{reason}{failureDetail} 已达到自动释放阈值，但{FormatWarmerStoreJobState(storeCommitted, storeMessage)}");
    }

    private static int GetDeliveryFailureRetireAttempts(AutomationDeliveryFailureKind failureKind)
    {
        return failureKind == AutomationDeliveryFailureKind.MissingController
            ? MissingControllerRetireAttempts
            : MissingTargetRetireAttempts;
    }

    private static TimeSpan GetDeliveryFailureRetireDelay(AutomationDeliveryFailureKind failureKind)
    {
        return failureKind == AutomationDeliveryFailureKind.MissingController
            ? MissingControllerRetireDelay
            : MissingTargetRetireDelay;
    }

    private static string FormatDeliveryFailureDetail(
        AutomationDeliveryFailureKind failureKind,
        int count,
        int threshold,
        TimeSpan age,
        TimeSpan delay)
    {
        var reason = failureKind == AutomationDeliveryFailureKind.MissingController
            ? "控制器不可达"
            : "订单不可达";
        return $"（{reason}连续 {count}/{threshold} 次，持续 {age.TotalSeconds:F1}/{delay.TotalSeconds:F0}s）";
    }

    private static WackyTagValidation ValidateWackyTags(
        CookingCollectionTarget target,
        object cookedFood,
        out IReadOnlyList<string> targetTags,
        out IReadOnlyList<string> actualTags)
    {
        var activeTargetTags = target.WackyTargetFoodTags;
        targetTags = activeTargetTags;
        actualTags = Array.Empty<string>();
        if (targetTags.Count == 0) return WackyTagValidation.NotRequired;

        if (!TryReadFoodTagNames(cookedFood, out actualTags)) return WackyTagValidation.Unreadable;
        return actualTags.Any(tag => activeTargetTags.Contains(tag, StringComparer.Ordinal))
            ? WackyTagValidation.Matched
            : WackyTagValidation.Mismatched;
    }

    private static bool TryDetectWackyTargetSignatureChanged(
        AutomationCookingJob job,
        out string originalSignature,
        out string currentSignature,
        out IReadOnlyList<string> originalTags,
        out IReadOnlyList<string> currentTags)
    {
        originalSignature = job.Target.WackyTargetSignature;
        originalTags = job.Target.WackyTargetFoodTags;
        currentSignature = "";
        currentTags = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(originalSignature)) return false;

        RuntimeSpecialBusinessContextService.TryGetActiveWackyTargetSignature(out currentSignature, out currentTags);
        return !string.Equals(originalSignature, currentSignature, StringComparison.Ordinal);
    }

    private static string FormatWackyTargetForMessage(string signature, IReadOnlyList<string> tags)
    {
        if (string.IsNullOrWhiteSpace(signature)) return "非怪诞料理目标";
        return tags.Count == 0 ? signature : $"{signature}（Tag {string.Join("、", tags)}）";
    }

    private static IEnumerable<string> ReadFoodTagNames(object cookedFood)
    {
        if (!TryReadFoodTagNames(cookedFood, out var tags)) yield break;
        foreach (var tag in tags) yield return tag;
    }

    private static bool TryReadFoodTagNames(object cookedFood, out IReadOnlyList<string> tags)
    {
        tags = Array.Empty<string>();
        var rawTags = TryInvokeInstanceValue(cookedFood, "get_Tags")
            ?? ReadMember(cookedFood, "Tags")
            ?? TryInvokeInstanceValue(cookedFood, "get_RawTags")
            ?? ReadMember(cookedFood, "RawTags");
        if (rawTags == null) return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var rawTag in ReadIntEnumerable(rawTags))
            {
                if (rawTag < 0) continue;

                if (!TryReadFoodTagName(rawTag, out var tagName)) return false;

                var normalized = FoodTags.NormalizeName(tagName) ?? tagName;
                seen.Add(normalized);
            }
        }
        catch
        {
            return false;
        }

        tags = seen.ToArray();
        return true;
    }

    private static bool TryReadFoodTagName(int tagId, out string tagName)
    {
        try
        {
            tagName = InvokeStatic(DataBaseLanguageTypeName, "GetFoodTag", new object?[] { tagId })?.ToString()?.Trim() ?? "";
            return !string.IsNullOrWhiteSpace(tagName);
        }
        catch
        {
            tagName = "";
            return false;
        }
    }

    private static bool TryStoreMismatchedCookResultInWarmer(
        AutomationCookingJob job,
        object cookedFood,
        AutomationWarmerCompletion completion,
        out string message,
        out bool storeCommitted)
    {
        storeCommitted = job.WarmerStoreCommitted;
        try
        {
            if (!IsAutomationCookingJobOwned(job, out var ownershipDiagnostic))
            {
                message = $"厨具锅次所有权已变化，拒绝操作当前成品。{ownershipDiagnostic}";
                return false;
            }

            if (job.WarmerResetTracker.CanCommit)
            {
                var configure = GetSingletonInstance(IzakayaConfigureTypeName);
                if (configure == null)
                {
                    message = "当前料理暂存容器不可用";
                    return false;
                }

                if (!job.WarmerResetTracker.TryBeginCommit())
                {
                    message = "保温箱提交状态已锁定，拒绝重复调用 StoreFood。";
                    return false;
                }

                job.WarmerStoredFood = cookedFood;
                job.WarmerCompletion = completion;

                if (!TryInspectStoredFoodIdentity(configure, cookedFood, out var alreadyStored, out var beforeDiagnostic))
                {
                    job.WarmerResetTracker.ResolveCommit(AutomationCommitResolution.NotCommitted);
                    message = $"调用 StoreFood 前无法读取 StoredFoods，本轮未执行任何保温箱或厨具副作用，将在有界送达时钟内重试：{beforeDiagnostic}";
                    return false;
                }

                if (alreadyStored)
                {
                    job.WarmerResetTracker.ResolveCommit(AutomationCommitResolution.Committed);
                    storeCommitted = true;
                    job.WarmerStoreStatus = "同一成品对象已存在于保温箱，未重复调用 StoreFood。";
                }
                else
                {
                    // IDA: IzakayaConfigure.StoreFood adds to StoredFoods before UI and partner callbacks.
                    // If a later callback throws, exact object identity is the only safe commit proof.
                    var storeReturned = TryInvokeStoreFood(
                        configure,
                        cookedFood,
                        out var invocationAttempted,
                        out var storeDiagnostic);
                    if (storeReturned)
                    {
                        job.WarmerResetTracker.ResolveCommit(AutomationCommitResolution.Committed);
                        storeCommitted = true;
                        job.WarmerStoreStatus = "StoreFood 已正常返回并确认提交。";
                    }
                    else if (!invocationAttempted)
                    {
                        job.WarmerResetTracker.ResolveCommit(AutomationCommitResolution.NotCommitted);
                        message = storeDiagnostic;
                        return false;
                    }
                    else if (!TryInspectStoredFoodIdentity(configure, cookedFood, out var storedAfterException, out var afterDiagnostic))
                    {
                        job.WarmerResetTracker.ResolveCommit(AutomationCommitResolution.Uncertain);
                        message = $"{storeDiagnostic}；调用异常后无法读取 StoredFoods 确认同一成品对象，已禁止再次调用 StoreFood 或清理厨具：{afterDiagnostic}";
                        return false;
                    }
                    else if (storedAfterException)
                    {
                        job.WarmerResetTracker.ResolveCommit(AutomationCommitResolution.Committed);
                        storeCommitted = true;
                        job.WarmerStoreStatus = $"StoreFood 后半段异常，但 StoredFoods 已包含同一成品对象，提交已确认。{storeDiagnostic}";
                    }
                    else
                    {
                        job.WarmerResetTracker.ResolveCommit(AutomationCommitResolution.Uncertain);
                        message = $"{storeDiagnostic}；StoreFood 已实际进入原生方法。即使 StoredFoods 当前不含同一成品对象，也无法证明 Add、界面、伙伴或额外回调均未发生，已禁止再次调用 StoreFood 或清理厨具。";
                        return false;
                    }
                }
            }

            if (!job.WarmerResetTracker.TryBeginAttempt(eligible: true))
            {
                message = $"{job.WarmerStoreStatus}厨具复位重试已达到 {job.WarmerResetAttempts}/{job.WarmerResetTracker.MaxAttempts} 次。";
                return false;
            }

            var resetSucceeded = TryResetCookControllerAfterCommittedSideEffect(
                job,
                cookedFood,
                out var resetMessage);
            message = $"{job.WarmerStoreStatus}{resetMessage}";
            if (!resetSucceeded) return false;

            job.WarmerResetTracker.Complete();
            return true;
        }
        catch (Exception ex)
        {
            if (job.WarmerResetTracker.CommitAttemptInProgress)
            {
                job.WarmerResetTracker.ResolveCommit(AutomationCommitResolution.Uncertain);
            }

            message = job.WarmerStoreCommitUncertain
                ? $"保温箱提交期间发生未分类异常且无法确认提交边界，已禁止再次调用 StoreFood：{ex.GetBaseException().Message}"
                : ex.GetBaseException().Message;
            storeCommitted = job.WarmerStoreCommitted;
            return false;
        }
    }

    private static (bool Remove, string Message, string Code) TryCompleteCommittedWarmerReset(
        AutomationCookingJob job)
    {
        if (job.WarmerStoredFood == null || job.WarmerCompletion == null)
        {
            return BlockCommittedWarmerReset(
                job,
                "保温箱提交上下文不完整，无法安全继续复位厨具。");
        }

        if (!job.WarmerResetTracker.TryBeginAttempt(eligible: true))
        {
            return job.WarmerResetTracker.Exhausted
                ? BlockCommittedWarmerReset(job, "厨具复位重试次数已耗尽。")
                : (false, "", OrderPreparationStepCodes.CookingPending);
        }

        var resetSucceeded = TryResetCookControllerAfterCommittedSideEffect(
            job,
            job.WarmerStoredFood,
            out var resetMessage);
        var detail = $"{job.WarmerStoreStatus}{resetMessage}";
        if (resetSucceeded)
        {
            job.WarmerResetTracker.Complete();
            return CompleteCommittedWarmerStore(job, detail);
        }

        if (job.WarmerResetTracker.Exhausted)
        {
            return BlockCommittedWarmerReset(job, detail);
        }

        return (
            false,
            $"{job.RecipeName} 的成品已写入保温箱，正在重试同一锅次厨具复位（{job.WarmerResetAttempts}/{job.WarmerResetTracker.MaxAttempts}）：{detail}",
            OrderPreparationStepCodes.CookingPending);
    }

    private static (bool Remove, string Message, string Code) CompleteCommittedWarmerStore(
        AutomationCookingJob job,
        string detail)
    {
        var completion = job.WarmerCompletion
            ?? throw new InvalidOperationException("Warmer completion context is missing after StoreFood committed.");
        var postResetMessage = CompleteCookerExtractionAfterReset(job);
        var message = $"{completion.MessagePrefix}{detail}{postResetMessage}";
        if (!string.IsNullOrWhiteSpace(completion.DiagnosticEvent))
        {
            AppendWackyCookingJobDiagnostic(
                completion.DiagnosticEvent,
                job,
                "stored-in-warmer",
                completion.ActualFoodId,
                completion.TargetTags,
                completion.ActualTags,
                detail);
        }

        if (completion.RememberRejectedRecipe)
        {
            RememberRecentWackyRejectedRecipe(job.Target, completion.TargetTags);
        }

        RecordAutomationRuntimeEvent(
            completion.Code,
            job,
            message,
            completion.ActualFoodId,
            completion.TargetTags,
            completion.ActualTags,
            outcome: completion.Outcome,
            reasonCode: completion.ReasonCode,
            terminal: true);
        return (true, message, completion.Code);
    }

    private static (bool Remove, string Message, string Code) BlockCommittedWarmerReset(
        AutomationCookingJob job,
        string detail)
    {
        var message = $"{job.RecipeName} 的成品已写入保温箱，但同一锅次厨具连续无法复位；自动料理任务已停止，且不会再次写入保温箱。{detail}";
        RecordAutomationRuntimeEvent(
            OrderPreparationStepCodes.CookingWarmerResetBlocked,
            job,
            message,
            job.WarmerCompletion?.ActualFoodId ?? -1,
            job.WarmerCompletion?.TargetTags,
            job.WarmerCompletion?.ActualTags,
            outcome: "blocked",
            reasonCode: "cooking-warmer-reset-failed",
            terminal: true);
        return (true, message, OrderPreparationStepCodes.CookingWarmerResetBlocked);
    }

    private static (bool Remove, string Message, string Code) BlockUncertainWarmerStore(
        AutomationCookingJob job,
        string detail)
    {
        var message = $"{job.RecipeName} 的保温箱提交状态无法确认；为避免重复写入或误清厨具，自动料理任务已停止并保留现场。{detail}";
        RecordAutomationRuntimeEvent(
            OrderPreparationStepCodes.CookingWarmerCommitUncertain,
            job,
            message,
            job.WarmerCompletion?.ActualFoodId ?? -1,
            job.WarmerCompletion?.TargetTags,
            job.WarmerCompletion?.ActualTags,
            outcome: "blocked",
            reasonCode: "cooking-warmer-commit-uncertain",
            terminal: true);
        return (true, message, OrderPreparationStepCodes.CookingWarmerCommitUncertain);
    }

    private static string FormatWarmerStoreJobState(bool storeCommitted, string detail)
    {
        return storeCommitted
            ? $"成品已写入保温箱，但厨具复位尚未完成且不会重复写入：{detail}"
            : $"写入保温箱失败：{detail}";
    }

    private static bool TryInvokeStoreFood(
        object configure,
        object cookedFood,
        out bool invocationAttempted,
        out string diagnostic)
    {
        invocationAttempted = false;
        diagnostic = "";
        var methods = configure.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method =>
            {
                if (!string.Equals(method.Name, "StoreFood", StringComparison.Ordinal)) return false;
                var parameters = method.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType.IsInstanceOfType(cookedFood)
                    && parameters[1].ParameterType == typeof(int);
            })
            .ToArray();
        if (methods.Length != 1)
        {
            diagnostic = methods.Length == 0
                ? "未找到精确 StoreFood(Sellable, int) 方法"
                : $"发现 {methods.Length} 个 StoreFood(Sellable, int) 方法，无法确定唯一原生入口";
            return false;
        }

        invocationAttempted = true;
        try
        {
            methods[0].Invoke(configure, new object?[] { cookedFood, -1 });
            return true;
        }
        catch (Exception ex)
        {
            var root = ex.GetBaseException();
            diagnostic = $"StoreFood(Sellable, int) 调用失败：{root.GetType().Name}: {root.Message}";
            return false;
        }
    }

    private static object? ReadStoredFoodList(object configure)
    {
        return ReadMember(configure, "StoredFoods")
            ?? TryInvokeInstanceValue(configure, "get_StoredFoods")
            ?? TryInvokeInstanceValue(configure, "GetStoredFoods");
    }

    private static bool TryInspectStoredFoodIdentity(
        object configure,
        object cookedFood,
        out bool found,
        out string diagnostic)
    {
        found = false;
        diagnostic = "";
        try
        {
            var storedFoods = ReadStoredFoodList(configure);
            if (storedFoods == null)
            {
                diagnostic = "无法读取 IzakayaConfigure.StoredFoods";
                return false;
            }

            if (!TryReadExactMemberValue(storedFoods, out var rawCount, out var countDiagnostic, "Count", "_size")
                || !TryReadIntValue(rawCount, out var count)
                || count < 0
                || count > 4096)
            {
                diagnostic = $"无法可靠读取 StoredFoods.Count：{countDiagnostic}";
                return false;
            }

            for (var index = 0; index < count; index++)
            {
                var item = InvokeInstance(storedFoods, "get_Item", new object?[] { index });
                if (item == null) continue;

                var identity = CompareObjectIdentity(item, cookedFood);
                if (identity == RuntimeObjectIdentityComparison.Same)
                {
                    found = true;
                    return true;
                }

                if (identity == RuntimeObjectIdentityComparison.Unknown)
                {
                    diagnostic = $"StoredFoods[{index}] 与目标成品的原生身份无法确认";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            diagnostic = ex.GetBaseException().Message;
            return false;
        }
    }

    private static bool TryResetCookControllerAfterCommittedSideEffect(
        AutomationCookingJob job,
        object cookedFood,
        out string message)
    {
        try
        {
            if (!IsAutomationCookingJobOwned(job, out var ownershipDiagnostic))
            {
                message = $"厨具已进入新锅次，未清理当前厨具。{ownershipDiagnostic}";
                return false;
            }

            if (!TryReadCookControllerResetState(
                    job.CookController,
                    out var phaseBefore,
                    out var resultBefore,
                    out var chosenRecipeBefore,
                    out var beforeDiagnostic))
            {
                message = $"无法在复位前可靠读取厨具状态：{beforeDiagnostic}";
                return false;
            }

            if (phaseBefore == 0 && resultBefore == null && chosenRecipeBefore == null)
            {
                message = "厨具已严格确认处于空闲状态（Phase=0，Result/ChosenRecipe=null）。";
                return true;
            }

            if (resultBefore != null && !IsSameObject(resultBefore, cookedFood))
            {
                message = "同一锅次厨具当前 Result 已变为其他对象，拒绝清理。";
                return false;
            }

            if (!TryInvokeInstance(job.CookController, "CloseCookingVisual", Array.Empty<object?>()))
            {
                message = "无法关闭 CookController 料理视觉，厨具复位未执行。";
                return false;
            }
            if (!WriteMember(job.CookController, "LastResult", cookedFood))
            {
                message = "无法写入 CookController.LastResult，厨具复位未确认。";
                return false;
            }

            if (!WriteMember(job.CookController, "Result", null))
            {
                message = "无法清空 CookController.Result，厨具复位未确认。";
                return false;
            }

            if (!WriteMember(job.CookController, "ChosenRecipe", null))
            {
                message = "无法清空 CookController.ChosenRecipe，厨具复位未确认。";
                return false;
            }

            if (!TryCreateIdleCookPhaseValue(job.CookController, out var phaseValue))
            {
                message = "无法解析 CookController.Phase 的运行时类型，厨具复位未确认。";
                return false;
            }

            if (!WriteMember(job.CookController, "Phase", phaseValue))
            {
                message = "无法写入 CookController.Phase=Idle，厨具复位未确认。";
                return false;
            }

            if (!IsAutomationCookingJobOwned(job, out ownershipDiagnostic))
            {
                message = $"复位后厨具锅次所有权已变化，无法确认清理边界。{ownershipDiagnostic}";
                return false;
            }

            if (!TryReadCookControllerResetState(
                    job.CookController,
                    out var phaseAfter,
                    out var resultAfter,
                    out var chosenRecipeAfter,
                    out var afterDiagnostic))
            {
                message = $"复位写入后无法可靠读取厨具状态：{afterDiagnostic}";
                return false;
            }

            if (phaseAfter != 0 || resultAfter != null || chosenRecipeAfter != null)
            {
                message = $"厨具复位严格校验失败（phase={phaseAfter}; resultNull={resultAfter == null}; chosenRecipeNull={chosenRecipeAfter == null}）。";
                return false;
            }

            message = "厨具已严格复位（Phase=0，Result/ChosenRecipe=null）。";
            return true;
        }
        catch (Exception ex)
        {
            var attemptCount = Math.Max(job.WarmerResetAttempts, job.FoodDeliveryCleanupAttempts);
            message = $"厨具复位诊断（尝试 {attemptCount} 次）：{ex.GetBaseException().Message}。";
            return false;
        }
    }

    private static bool TryReadCookControllerResetState(
        object cookController,
        out int phase,
        out object? result,
        out object? chosenRecipe,
        out string diagnostic)
    {
        phase = -1;
        result = null;
        chosenRecipe = null;
        diagnostic = "";
        if (!TryReadExactMemberValue(
                cookController,
                out var rawPhase,
                out var phaseDiagnostic,
                "Phase",
                "<Phase>k__BackingField")
            || rawPhase == null
            || (phase = ToInt(rawPhase, -1)) < 0)
        {
            diagnostic = $"Phase 不可读：{phaseDiagnostic}";
            return false;
        }

        if (!TryReadExactMemberValue(
                cookController,
                out result,
                out var resultDiagnostic,
                "Result",
                "<Result>k__BackingField"))
        {
            diagnostic = $"Result 不可读：{resultDiagnostic}";
            return false;
        }

        if (!TryReadExactMemberValue(
                cookController,
                out chosenRecipe,
                out var recipeDiagnostic,
                "ChosenRecipe",
                "<ChosenRecipe>k__BackingField"))
        {
            diagnostic = $"ChosenRecipe 不可读：{recipeDiagnostic}";
            return false;
        }

        return true;
    }

    private static bool TryCreateIdleCookPhaseValue(object cookController, out object value)
    {
        for (var type = cookController.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(
                "Phase",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property != null)
            {
                value = property.PropertyType.IsEnum
                    ? Enum.ToObject(property.PropertyType, 0)
                    : Convert.ChangeType(0, property.PropertyType);
                return true;
            }

            var field = type.GetField(
                "<Phase>k__BackingField",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null) continue;
            value = field.FieldType.IsEnum
                ? Enum.ToObject(field.FieldType, 0)
                : Convert.ChangeType(0, field.FieldType);
            return true;
        }

        value = 0;
        return false;
    }

    private static OrderPreparationRequest BuildOrderRequestFromCookingTarget(CookingCollectionTarget target)
    {
        return new OrderPreparationRequest
        {
            TraceId = target.TraceId,
            OrderKey = target.OrderKey,
            DeskCode = target.DeskCode,
            GuestId = target.GuestId,
            RuntimeGuestId = target.RuntimeGuestId,
            GuestName = target.GuestName,
            SpecialBusinessRole = target.SpecialBusinessRole,
            FoodTagId = target.FoodTagId,
            FoodTag = target.FoodTag,
            BeverageTagId = target.BeverageTagId,
            BeverageTag = target.BeverageTag,
            MatchFoodId = target.MatchFoodId,
            MatchBeverageId = target.MatchBeverageId,
            FoodId = target.FoodId,
            RecipeId = target.RecipeId,
            RecipeName = target.FoodName,
            ExtraIngredientIds = target.ExtraIngredientIds,
            PredictedFoodTags = target.PredictedFoodTags,
            ExpectedFoodModifierTags = target.ExpectedFoodModifierTags,
            WackyTargetFoodTags = target.WackyTargetFoodTags,
            ExecutionMode = target.ExecutionMode,
            ExecutionReason = target.ExecutionReason,
            BeverageId = target.BeverageId,
            BeverageName = target.BeverageName,
            AutoCompleteOrder = target.AutoCompleteOrder,
        };
    }

    private static bool IsAutomationCookingJobOwned(AutomationCookingJob job, out string diagnostic)
    {
        if (!RuntimeCookingGenerationTracker.TryGetGeneration(job.CookController, out var generation, out diagnostic))
        {
            return false;
        }

        if (generation == job.Generation) return true;
        diagnostic = $"expectedGeneration={job.Generation}; actualGeneration={generation}; {diagnostic}";
        return false;
    }
}
