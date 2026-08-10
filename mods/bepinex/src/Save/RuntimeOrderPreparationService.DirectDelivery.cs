using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
using System.Reflection;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private enum SpecialFoodTargetTagValidation
    {
        NotRequired,
        Matched,
        ControlledProgression,
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
        string orderLabel,
        CookingCollectionTarget? target = null)
    {
        if (target != null && IsYuumaBossTarget(target))
        {
            return TryDeliverYuumaOrderBeverage(
                target,
                beverageId,
                beverageName,
                orderLabel);
        }

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

        if (target != null
            && !TryValidateMizuchiRuntimeOrder(
                target,
                runtimeOrder,
                "before-beverage-setter",
                out var mizuchiBeverageDiagnostic))
        {
            return (
                false,
                "瑞灵特殊经营订单角色或评价闭包在酒水送达前已漂移，未调用订单 setter："
                + mizuchiBeverageDiagnostic,
                OrderPreparationStepCodes.MizuchiContractMismatch);
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
            return TryCompleteCommittedFoodDeliveryTransaction(job);
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
        if (!TryReadCookControllerFoodResultIdentity(
                cookedFood,
                "CookController.Result",
                out var cookedFoodIdentity,
                out var cookedFoodIdentityDiagnostic))
        {
            return ContinueOrBlockAutomationDelivery(
                job,
                $"成品身份在送达前无法安全复核，本轮未执行送达、入箱或厨具复位：{cookedFoodIdentityDiagnostic}");
        }

        if (target.FoodId < 0)
        {
            return ContinueOrBlockAutomationDelivery(
                job,
                $"目标料理 ID 无效（{target.FoodId}），本轮未执行送达、入箱或厨具复位。");
        }

        if (cookedFoodIdentity.FoodId != target.FoodId)
        {
            var actualFoodId = cookedFoodIdentity.FoodId;
            var actualText = cookedFoodIdentity.IsDarkCuisine
                ? "黑暗料理（料理 #-1）"
                : $"料理 #{actualFoodId}";
            var actualFoodTags = ReadFoodTagNames(cookedFood).ToArray();
            var activeTargetTags = target.SpecialTargetFoodTags;
            AppendSpecialFoodTargetCookingJobDiagnostic(
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

            AppendSpecialFoodTargetCookingJobDiagnostic(
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

        if (!TryValidateMizuchiFoodModifier(
                target,
                cookedFood,
                "cooked-result-before-delivery",
                out var mizuchiCookedResultDiagnostic))
        {
            return BlockMizuchiCookingJob(
                job,
                "成品送达前",
                mizuchiCookedResultDiagnostic);
        }

        var specialTargetChanged = TryDetectSpecialFoodTargetPolicyChanged(
                job,
                out var originalTargetSignature,
                out var currentTargetSignature,
                out var originalTargetTags,
                out var currentTargetTags,
                out var originalTargetRevision,
                out var currentTargetRevision,
                out var specialTargetComparisonAvailable);
        if (!specialTargetComparisonAvailable)
        {
            return ContinueOrBlockAutomationDelivery(
                job,
                $"{job.RecipeName} 已完成，但当前特殊料理目标暂不可用；"
                + $"开锅目标 revision={originalTargetRevision} "
                + $"{FormatSpecialFoodTargetForMessage(originalTargetSignature, originalTargetTags)}。"
                + "本轮未送达、入箱或复位厨具，等待权威目标恢复。");
        }

        if (specialTargetChanged)
        {
            var actualFoodId = ReadSellableId(cookedFood);
            var actualTagsForSignature = ReadFoodTagNames(cookedFood).ToArray();
            var signatureMessage = $"{job.RecipeName} 已完成，但特殊料理目标已变化："
                + $"开锅时 revision={originalTargetRevision} "
                + $"{FormatSpecialFoodTargetForMessage(originalTargetSignature, originalTargetTags)}，"
                + $"当前 revision={currentTargetRevision} "
                + $"{FormatSpecialFoodTargetForMessage(currentTargetSignature, currentTargetTags)}";
            AppendSpecialFoodTargetCookingJobDiagnostic(
                "special-target-signature-changed",
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
                "special-target-signature-changed-stored");
            if (TryStoreMismatchedCookResultInWarmer(
                    job,
                    cookedFood,
                    signatureCompletion,
                    out var signatureStoreMessage,
                    out var signatureStoreCommitted))
            {
                return CompleteCommittedWarmerStore(job, signatureStoreMessage);
            }

            AppendSpecialFoodTargetCookingJobDiagnostic(
                signatureStoreCommitted ? "special-target-signature-changed-reset-job" : "special-target-signature-changed-store-failed",
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

        var specialTagValidation = ValidateSpecialFoodTargetTags(target, cookedFood, out var targetTags, out var actualTags);
        if (specialTagValidation is SpecialFoodTargetTagValidation.Mismatched or SpecialFoodTargetTagValidation.Unreadable)
        {
            var actualFoodId = ReadSellableId(cookedFood);
            var unreadableTags = specialTagValidation == SpecialFoodTargetTagValidation.Unreadable;
            var resultCode = unreadableTags
                ? OrderPreparationStepCodes.CookingTagsUnreadableStored
                : OrderPreparationStepCodes.CookingMismatchStored;
            var diagnosticPrefix = unreadableTags ? "cooked-food-tags-unreadable" : "cooked-food-tag-mismatch";
            var tagMessage = unreadableTags
                ? $"{job.RecipeName} 已完成，但无法读取成品 Tag，不能确认满足当前特殊料理目标 Tag（{string.Join("、", targetTags)}）"
                : $"{job.RecipeName} 已完成，但成品 Tag（{string.Join("、", actualTags)}）不满足当前特殊料理目标 {target.SpecialFoodTargetPolicy?.MatchModeValue}（{string.Join("、", targetTags)}）";
            AppendSpecialFoodTargetCookingJobDiagnostic(
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
                    out var targetStoreMessage,
                    out var targetStoreCommitted))
            {
                return CompleteCommittedWarmerStore(job, targetStoreMessage);
            }

            AppendSpecialFoodTargetCookingJobDiagnostic(
                targetStoreCommitted ? $"{diagnosticPrefix}-reset-job" : $"{diagnosticPrefix}-store-failed",
                job,
                targetStoreCommitted ? "retry-cooker-reset" : "keep-on-cooker",
                actualFoodId,
                targetTags,
                actualTags,
                targetStoreMessage);
            return ContinueOrBlockAutomationDelivery(
                job,
                $"{tagMessage}，{FormatWarmerStoreJobState(targetStoreCommitted, targetStoreMessage)}");
        }

        if (specialTagValidation == SpecialFoodTargetTagValidation.Matched)
        {
            AppendSpecialFoodTargetCookingJobDiagnostic(
                "cooked-food-tag-match",
                job,
                "continue-delivery",
                ReadSellableId(cookedFood),
                targetTags,
                actualTags,
                "cooked food tags matched the active special-food target policy");
        }
        else if (specialTagValidation == SpecialFoodTargetTagValidation.ControlledProgression)
        {
            AppendSpecialFoodTargetCookingJobDiagnostic(
                "yuuma-controlled-progression-actual-tag-bypass",
                job,
                "continue-delivery-with-original-order-items",
                ReadSellableId(cookedFood),
                targetTags,
                actualTags,
                "explicit controlled progression accepted an exact original-order food whose readable tags do not satisfy the current dual-Tag target");
        }

        var request = BuildOrderRequestFromCookingJob(job);
        var yuumaTarget = IsYuumaBossTarget(target);
        var runtimeOrder = yuumaTarget
            ? FindYuumaRuntimeOrder(target, request)
            : target.Kind == CookingCollectionTargetKind.NormalOrder
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

        if (!TryMatchRuntimeOrderBinding(
                target.OrderBinding,
                runtimeOrder.Order,
                runtimeOrder.Controller,
                out var bindingDiagnostic))
        {
            return HandleUndeliverableCookedFood(
                job,
                cookedFood,
                AutomationDeliveryFailureKind.MissingOrder,
                "fresh order/controller 与开锅前 exact identity 不一致，禁止向后继或并发订单送达："
                + $"{bindingDiagnostic}；{runtimeOrder.Diagnostic}");
        }

        if (!TryValidateMizuchiFoodDeliveryPreflight(
                target,
                runtimeOrder,
                cookedFood,
                "fresh-order-before-food-state",
                out var mizuchiFoodStateDiagnostic))
        {
            return BlockMizuchiCookingJob(
                job,
                "订单状态读取前",
                mizuchiFoodStateDiagnostic);
        }

        job.ResetDeliveryFailures();
        if (yuumaTarget)
        {
            if (!TryReadYuumaOrderDeliveryState(
                    runtimeOrder.Order,
                    out var yuumaServedFood,
                    out var yuumaFoodInAir,
                    out var yuumaServedBeverage,
                    out _,
                    out var yuumaDeliveryStateDiagnostic))
            {
                return ContinueOrBlockAutomationDelivery(
                    job,
                    "无法精确确认血池地狱订单当前料理、待送达料理与酒水状态，"
                    + $"本轮未进入手动交接：{yuumaDeliveryStateDiagnostic}");
            }

            var activeOrderFood = yuumaServedFood ?? yuumaFoodInAir;
            if (job.YuumaSettlementTracker.Stage
                != SpecialBusiness.YuumaSettlementTransactionStage.Ready)
            {
                return TryFinalizeYuumaCookingJob(job, cookedFood);
            }

            if (activeOrderFood != null)
            {
                var activeFoodIdentity = CompareObjectIdentity(activeOrderFood, cookedFood);
                if (activeFoodIdentity == RuntimeObjectIdentityComparison.Unknown)
                {
                    return ContinueOrBlockAutomationDelivery(
                        job,
                        "血池地狱订单已有料理或料理正在送达，但无法确认是否为本 job 成品；"
                        + "本轮未进入手动交接、入箱或复位厨具。");
                }

                if (activeFoodIdentity == RuntimeObjectIdentityComparison.Same)
                {
                    var deliveryStartedMessage =
                        $"{job.RecipeName} 的成品已进入游戏原生订单送达流程；"
                        + "旧 cooking job 已释放，Mod 未送达、入箱或复位厨具。";
                    RecordAutomationRuntimeEvent(
                        OrderPreparationStepCodes.CookingOwnershipLost,
                        job,
                        deliveryStartedMessage,
                        outcome: "interrupted",
                        reasonCode: "cooking-native-food-delivery-started",
                        terminal: true);
                    return (
                        true,
                        deliveryStartedMessage,
                        OrderPreparationStepCodes.CookingOwnershipLost);
                }

                return StoreCookedFoodForAlreadyHandledTarget(
                    job,
                    cookedFood,
                    "血池地狱订单已有其他料理或料理正在由游戏送达");
            }

            return TryFinalizeYuumaCookingJob(job, cookedFood);
        }

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
            return DeferCommittedFoodDeliveryFollowUp(job);
        }

        if (servedFoodIdentity == RuntimeObjectIdentityComparison.Unknown)
        {
            return BlockUncertainFoodDelivery(
                job,
                "订单已有料理，但无法确认它是否为本 job 成品；未重复送达、写入保温箱或清理厨具。");
        }

        if (servedFood != null)
        {
            return StoreCookedFoodForAlreadyHandledTarget(
                job,
                cookedFood,
                "目标订单已有料理");
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

        if (!TryValidateMizuchiFoodDeliveryPreflight(
                target,
                runtimeOrder,
                cookedFood,
                "immediately-before-food-setter",
                out var mizuchiFoodCommitDiagnostic))
        {
            return BlockMizuchiCookingJob(
                job,
                "料理 setter 前",
                mizuchiFoodCommitDiagnostic);
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
        return DeferCommittedFoodDeliveryFollowUp(job);
    }

    /// <summary>
    /// Ends the food-delivery permit before cooker cleanup and order evaluation are processed.
    /// </summary>
    /// <remarks>
    /// The setter and its commit verification are one atomic side-effect boundary. Cleanup is
    /// mandatory after a committed setter, while evaluation is a separate configurable boundary
    /// and must acquire a fresh permit on the next job poll.
    /// </remarks>
    private static (bool Remove, string Message, string Code) DeferCommittedFoodDeliveryFollowUp(
        AutomationCookingJob job)
    {
        var message = job.FoodDeliveryCompletion?.Message
            ?? $"{job.Target.FoodName} 已确认送达订单。";
        return (
            false,
            $"{message}等待清理原厨具并按当前生效配置确认订单评价。",
            OrderPreparationStepCodes.CookingPending);
    }

    private static (bool Remove, string Message, string Code) StoreCookedFoodForAlreadyHandledTarget(
        AutomationCookingJob job,
        object cookedFood,
        string targetState)
    {
        var actualFoodId = ReadSellableId(cookedFood);
        var completion = new AutomationWarmerCompletion(
            OrderPreparationStepCodes.CookingTargetAlreadyServedStored,
            "completed",
            "cooking-target-already-served",
            $"{job.RecipeName} 已完成，但{targetState}；自动化成品已放入保温箱。",
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
            $"{targetState}，但{FormatWarmerStoreJobState(storeCommitted, storeMessage)}");
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

    private static (bool Remove, string Message, string Code) TryCompleteCommittedFoodDeliveryTransaction(
        AutomationCookingJob job)
    {
        if (IsYuumaBossTarget(job.Target))
        {
            return ContinueOrBlockAutomationDelivery(
                job,
                "血池地狱料理必须继续由专用结算事务处理，通用直送事务不会接管评价。");
        }

        var cleanup = TryCompleteCommittedFoodDeliveryCleanup(job);
        if (!job.FoodDeliveryCleanupCompleted && !job.FoodDeliveryCleanupTerminal)
        {
            return cleanup;
        }

        if (!TryResolveCommittedFoodDeliveryEvaluation(job, out var evaluationMessage, out var evaluationCode))
        {
            return (
                false,
                $"{job.FoodDeliveryCompletion?.Message}{cleanup.Message}{evaluationMessage}",
                string.IsNullOrWhiteSpace(evaluationCode)
                    ? OrderPreparationStepCodes.CookingPending
                    : evaluationCode);
        }

        var completion = job.FoodDeliveryCompletion;
        var message = $"{completion?.Message}{cleanup.Message}{job.FoodDeliveryEvaluationMessage}";
        var evaluationUncertain = job.FoodDeliveryEvaluationState
            == AutomationFoodDeliveryEvaluationState.CommitUncertain;
        var evaluationBlocked = job.FoodDeliveryEvaluationState is
            AutomationFoodDeliveryEvaluationState.TargetMismatch
            or AutomationFoodDeliveryEvaluationState.CloseoutUnresolved;
        var orderTerminated = job.FoodDeliveryEvaluationState
            == AutomationFoodDeliveryEvaluationState.OrderTerminated;
        var terminalCode = evaluationUncertain
            ? OrderPreparationStepCodes.OrderEvaluationCommitUncertain
            : evaluationBlocked || orderTerminated
                ? job.FoodDeliveryEvaluationCode
            : job.FoodDeliveryCleanupTerminal
                ? job.FoodDeliveryCleanupTerminalCode
                : OrderPreparationStepCodes.FoodDelivered;
        AppendSpecialFoodTargetCookingJobDiagnostic(
            "cooked-food-delivered",
            job,
            evaluationUncertain
                ? "evaluation-uncertain"
                : evaluationBlocked
                    ? "evaluation-blocked"
                    : orderTerminated
                        ? "order-terminated"
                        : "delivered-and-evaluated",
            completion?.ActualFoodId ?? -1,
            completion?.TargetTags,
            completion?.ActualTags,
            message);
        if (!evaluationUncertain
            && !evaluationBlocked
            && !orderTerminated
            && !job.FoodDeliveryCleanupTerminal)
        {
            RecordAutomationRuntimeEvent(
                terminalCode,
                job,
                message,
                outcome: "completed",
                reasonCode: "food-delivered",
                terminal: true);
        }
        else if (orderTerminated)
        {
            RecordAutomationRuntimeEvent(
                terminalCode,
                job,
                message,
                outcome: "interrupted",
                reasonCode: terminalCode,
                terminal: true);
        }
        return (true, message, terminalCode);
    }

    private static bool TryResolveCommittedFoodDeliveryEvaluation(
        AutomationCookingJob job,
        out string message,
        out string code)
    {
        message = job.FoodDeliveryEvaluationMessage;
        code = job.FoodDeliveryEvaluationCode;
        if (job.FoodDeliveryEvaluationState != AutomationFoodDeliveryEvaluationState.Pending)
        {
            return true;
        }

        using var permit = AcquireAutomationCookingJobControlPermit(
            job,
            RuntimeAutomationControlStage.OrderEvaluation,
            DateTime.UtcNow);
        if (!permit.Allowed)
        {
            job.FoodDeliveryEvaluationCloseoutTracker?.Suspend(DateTime.UtcNow);
            message = permit.Decision.Message;
            code = OrderPreparationStepCodes.CookingPending;
            return false;
        }

        if (!job.Target.OrderBinding.HasValue)
        {
            return ContinueOrCloseCommittedFoodDeliveryEvaluation(
                job,
                "cooking job 缺少开锅前锁定的 exact order/controller receipt identity。",
                OrderPreparationStepCodes.OrderEvaluationStateUnreadable,
                out message,
                out code);
        }

        if (RuntimeOrderTerminalReceiptStore.TryFind(
                job.Target.OrderBinding.Value,
                out var terminalReceipt))
        {
            if (terminalReceipt.Disposition == RuntimeOrderTerminalDisposition.Evaluated)
            {
                job.FoodDeliveryEvaluationState = AutomationFoodDeliveryEvaluationState.Completed;
                job.FoodDeliveryEvaluationMessage = "已收到同 generation/order/controller 的原生评价终态回执"
                    + $"（source={terminalReceipt.Source}; sequence={terminalReceipt.Sequence}），"
                    + "不会重新读取订单 wrapper 或重复评价。";
                job.FoodDeliveryEvaluationCode = OrderPreparationStepCodes.FoodDelivered;
            }
            else
            {
                job.FoodDeliveryEvaluationState = AutomationFoodDeliveryEvaluationState.OrderTerminated;
                job.FoodDeliveryEvaluationMessage = "已收到同 generation/order/controller 的原生订单终止回执"
                    + $"（source={terminalReceipt.Source}; sequence={terminalReceipt.Sequence}）；"
                    + "订单已不再可评价，旧 cooking receipt 将退休且不会猜测为评价成功。";
                job.FoodDeliveryEvaluationCode = OrderPreparationStepCodes.OrderTerminatedBeforeEvaluation;
            }

            message = job.FoodDeliveryEvaluationMessage;
            code = job.FoodDeliveryEvaluationCode;
            return true;
        }

        var target = job.Target;
        var request = BuildOrderRequestFromCookingJob(job);
        RuntimeOrderMatch runtimeOrder;
        try
        {
            runtimeOrder = target.Kind == CookingCollectionTargetKind.NormalOrder
                ? FindRuntimeNormalOrder(request)
                : FindRuntimeOrder(request, RuntimeOrderLookupPurpose.Completion);
        }
        catch (Exception ex)
        {
            return ContinueOrCloseCommittedFoodDeliveryEvaluation(
                job,
                $"精确重取目标订单时发生异常：{ex.GetBaseException().Message}。",
                OrderPreparationStepCodes.CookingPending,
                out message,
                out code);
        }

        if (runtimeOrder.Order == null || runtimeOrder.Manager == null)
        {
            return ContinueOrCloseCommittedFoodDeliveryEvaluation(
                job,
                $"目标订单或客人管理器暂不可用：{runtimeOrder.Diagnostic}。",
                OrderPreparationStepCodes.CookingPending,
                out message,
                out code);
        }

        if (!TryMatchRuntimeOrderBinding(
                job.Target.OrderBinding,
                runtimeOrder.Order,
                runtimeOrder.Controller,
                out var bindingDiagnostic))
        {
            return ContinueOrCloseCommittedFoodDeliveryEvaluation(
                job,
                $"fresh order/controller 与开锅前 exact identity 不一致，禁止读取满足状态或触发评价：{bindingDiagnostic}。",
                OrderPreparationStepCodes.OrderEvaluationStateUnreadable,
                out message,
                out code);
        }

        object? fulfilledValue;
        try
        {
            fulfilledValue = TryInvokeInstanceValue(runtimeOrder.Order, "get_IsFullfilled");
        }
        catch (Exception ex)
        {
            return ContinueOrCloseCommittedFoodDeliveryEvaluation(
                job,
                $"读取订单满足状态时发生异常：{ex.GetBaseException().Message}。",
                OrderPreparationStepCodes.OrderEvaluationStateUnreadable,
                out message,
                out code);
        }

        if (fulfilledValue == null)
        {
            return ContinueOrCloseCommittedFoodDeliveryEvaluation(
                job,
                "get_IsFullfilled 未返回布尔状态。",
                OrderPreparationStepCodes.OrderEvaluationStateUnreadable,
                out message,
                out code);
        }

        if (!ReadBool(fulfilledValue))
        {
            job.FoodDeliveryEvaluationState = AutomationFoodDeliveryEvaluationState.NotRequired;
            job.FoodDeliveryEvaluationMessage = "订单在本次料理送达后尚未同时满足料理和酒水，本 cooking job 不触发评价。";
            message = job.FoodDeliveryEvaluationMessage;
            return true;
        }

        RuntimeOrderEvaluationResult evaluation;
        try
        {
            evaluation = TryEvaluateMatchedAutomationOrderRuntimeIfReady(
                request,
                runtimeOrder,
                target.Kind == CookingCollectionTargetKind.NormalOrder ? "当前普客订单" : "当前订单",
                target);
        }
        catch (Exception ex)
        {
            evaluation = new RuntimeOrderEvaluationResult(
                false,
                false,
                false,
                $"订单评价入口执行期间发生未分类异常，提交结果无法确认：{ex.GetBaseException().Message}",
                OrderPreparationStepCodes.OrderEvaluationCommitUncertain);
        }

        if (!evaluation.Ok)
        {
            message = evaluation.Message;
            code = evaluation.Code;
            if (evaluation.Code == OrderPreparationStepCodes.OrderEvaluationTargetMismatch)
            {
                RecordOrderSafetyBarrierIfNeeded(evaluation.Code, target, evaluation.Message);
                job.FoodDeliveryEvaluationState = AutomationFoodDeliveryEvaluationState.TargetMismatch;
                job.FoodDeliveryEvaluationMessage = evaluation.Message;
                job.FoodDeliveryEvaluationCode = evaluation.Code;
                return true;
            }

            if (!IsAutomationSafetyBarrierCode(evaluation.Code))
            {
                return ContinueOrCloseCommittedFoodDeliveryEvaluation(
                    job,
                    $"精确评价路由尚不可提交：{evaluation.Message}。",
                    string.IsNullOrWhiteSpace(evaluation.Code)
                        ? OrderPreparationStepCodes.CookingPending
                        : evaluation.Code,
                    out message,
                    out code);
            }

            RecordOrderSafetyBarrierIfNeeded(evaluation.Code, target, evaluation.Message);
            job.FoodDeliveryEvaluationState = AutomationFoodDeliveryEvaluationState.CommitUncertain;
            job.FoodDeliveryEvaluationMessage = evaluation.Message;
            job.FoodDeliveryEvaluationCode = evaluation.Code;
            return true;
        }

        if (!evaluation.Completed)
        {
            return ContinueOrCloseCommittedFoodDeliveryEvaluation(
                job,
                $"订单已满足，但精确评价路由尚未完成提交：{evaluation.Message}。",
                string.IsNullOrWhiteSpace(evaluation.Code)
                    ? OrderPreparationStepCodes.CookingPending
                    : evaluation.Code,
                out message,
                out code);
        }

        job.FoodDeliveryEvaluationState = AutomationFoodDeliveryEvaluationState.Completed;
        job.FoodDeliveryEvaluationMessage = evaluation.Message;
        job.FoodDeliveryEvaluationCode = evaluation.Code;
        message = evaluation.Message;
        code = evaluation.Code;
        return true;
    }

    private static bool ContinueOrCloseCommittedFoodDeliveryEvaluation(
        AutomationCookingJob job,
        string detail,
        string transientCode,
        out string message,
        out string code)
    {
        var observedAtUtc = DateTime.UtcNow;
        job.FoodDeliveryEvaluationCloseoutTracker ??= new AutomationOrderEvaluationCloseoutTracker(
            observedAtUtc,
            EvaluationCloseoutAttemptLimit,
            EvaluationCloseoutMinimumAttemptWindow,
            EvaluationCloseoutMaximumEffectiveDuration);
        var tracker = job.FoodDeliveryEvaluationCloseoutTracker;
        if (!tracker.RecordFailure(observedAtUtc, eligible: true))
        {
            message = $"{detail}评价回执将在精确身份边界内有限重试"
                + $"（{tracker.AttemptCount}/{tracker.MaxAttempts}；"
                + $"有效等待 {tracker.EffectiveElapsed.TotalSeconds:F1}s/"
                + $"{tracker.MaxEffectiveDuration.TotalSeconds:F0}s）。";
            code = transientCode;
            return false;
        }

        code = OrderPreparationStepCodes.OrderEvaluationCloseoutUnresolved;
        message = $"{job.Target.FoodName} 已确认送达且 controller lease 已释放，但在有界窗口内"
            + "既未取得可安全评价的精确订单，也未收到同 generation/order/controller 的原生终态回执；"
            + "该评价回执已退休，不会猜测评价成功或重放送达/评价。"
            + $"最后诊断：{detail}尝试={tracker.AttemptCount}；"
            + $"有效等待={tracker.EffectiveElapsed.TotalSeconds:F1}s。";
        job.FoodDeliveryEvaluationState = AutomationFoodDeliveryEvaluationState.CloseoutUnresolved;
        job.FoodDeliveryEvaluationMessage = message;
        job.FoodDeliveryEvaluationCode = code;
        RecordOrderSafetyBarrierIfNeeded(code, job.Target, message);
        return true;
    }

    private static (bool Remove, string Message, string Code) TryCompleteCommittedFoodDeliveryCleanup(
        AutomationCookingJob job)
    {
        if (job.FoodDeliveryCleanupCompleted)
        {
            return (false, job.FoodDeliveryCleanupMessage, OrderPreparationStepCodes.CookingPending);
        }

        if (job.FoodDeliveryCleanupTerminal)
        {
            return (false, job.FoodDeliveryCleanupMessage, job.FoodDeliveryCleanupTerminalCode);
        }

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
        var postResetMessage = CompleteCookerExtractionAfterReset(job);
        job.FoodDeliveryCleanupMessage = $"{resetMessage}{postResetMessage}";
        EnterCommittedFoodDeliveryEvaluationReceipt(
            job,
            AutomationCookingControllerLeaseReleaseReason.DeliveryCleanupCompleted,
            "cooking-evaluation-after-cleanup");
        return (false, job.FoodDeliveryCleanupMessage, OrderPreparationStepCodes.CookingPending);
    }

    private static string CompleteCookerExtractionAfterReset(AutomationCookingJob job)
    {
        var parts = new List<string>();
        if (!TryReacquireAutomationCooker(
                job,
                out _,
                out var bindingFailure,
                out var bindingDiagnostic))
        {
            return "厨具已复位，但可用性通知执行前无法从当前物理目录重新取得同一厨具，"
                + $"不会进入原生出锅事务（{bindingFailure}）：{bindingDiagnostic}。";
        }

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
            if (!TryReacquireAutomationCooker(
                    job,
                    out var binding,
                    out bindingFailure,
                    out bindingDiagnostic))
            {
                parts.Add("厨具可用性通知后无法从当前物理目录重新取得同一厨具，"
                    + $"未执行出锅回调（{bindingFailure}）：{bindingDiagnostic}。");
                return string.Concat(parts);
            }

            var extractionMethods = binding.Controller
                .GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(method => string.Equals(method.Name, "AfterPlayerExtract", StringComparison.Ordinal)
                    && method.ReturnType == typeof(void)
                    && method.GetParameters().Length == 0)
                .ToArray();
            if (extractionMethods.Length != 1)
            {
                parts.Add("厨具出锅回调与 BepInEx 783 精确形态不一致，本 job 不会尝试调用。");
                return string.Concat(parts);
            }

            extractionMethods[0].Invoke(binding.Controller, Array.Empty<object?>());
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
        var message = $"{job.RecipeName} 已确认送达订单且不会重复送达，但同一锅次厨具无法严格复位；"
            + $"厨具清理已停止并保留现场，锁存的订单评价意图仍会在不访问该厨具的边界继续确认。{detail}";
        job.FoodDeliveryCleanupTerminal = true;
        job.FoodDeliveryCleanupMessage = message;
        job.FoodDeliveryCleanupTerminalCode = OrderPreparationStepCodes.CookingDeliveryCleanupBlocked;
        EnterCommittedFoodDeliveryEvaluationReceipt(
            job,
            AutomationCookingControllerLeaseReleaseReason.DeliveryCleanupTerminated,
            "cooking-evaluation-after-cleanup-terminal");
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
        return (false, message, OrderPreparationStepCodes.CookingDeliveryCleanupBlocked);
    }

    private static void EnterCommittedFoodDeliveryEvaluationReceipt(
        AutomationCookingJob job,
        AutomationCookingControllerLeaseReleaseReason releaseReason,
        string reasonCode)
    {
        var observedAtUtc = DateTime.UtcNow;
        var leaseReleased = job.ControllerLease.Release(releaseReason, observedAtUtc);

        // Evaluation re-acquires the order from exact stable identity. It must never retain or
        // revisit the delivered IL2CPP food wrapper after cooker cleanup has ended.
        job.DeliveredFood = null;
        job.CurrentResultPointer = 0;
        job.FoodDeliveryEvaluationCloseoutTracker ??= new AutomationOrderEvaluationCloseoutTracker(
            observedAtUtc,
            EvaluationCloseoutAttemptLimit,
            EvaluationCloseoutMinimumAttemptWindow,
            EvaluationCloseoutMaximumEffectiveDuration);
        job.Tracker.EnterEvaluationPending(observedAtUtc, reasonCode);

        if (leaseReleased)
        {
            AppendAutomationLog(
                "controller-lease-release",
                job.Target,
                job.FormatLogContext($"reason={job.ControllerLeaseReleaseReason}"));
        }
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
        if (job.Target.SpecialFoodTargetPolicy != null)
        {
            AppendSpecialFoodTargetCookingJobDiagnostic(
                "special-target-undeliverable",
                job,
                "store-undeliverable-food",
                actualFoodId,
                job.Target.SpecialTargetFoodTags,
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
            job.Target.SpecialTargetFoodTags.ToArray(),
            actualTags,
            job.Target.SpecialFoodTargetPolicy != null ? "special-target-undeliverable-stored" : "");
        if (TryStoreMismatchedCookResultInWarmer(
                job,
                cookedFood,
                completion,
                out var storeMessage,
                out var storeCommitted))
        {
            return CompleteCommittedWarmerStore(job, storeMessage);
        }

        if (job.Target.SpecialFoodTargetPolicy != null)
        {
            AppendSpecialFoodTargetCookingJobDiagnostic(
                storeCommitted ? "special-target-undeliverable-reset-job" : "special-target-undeliverable-store-failed",
                job,
                storeCommitted ? "retry-cooker-reset" : "keep-on-cooker",
                actualFoodId,
                job.Target.SpecialTargetFoodTags,
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

    private static SpecialFoodTargetTagValidation ValidateSpecialFoodTargetTags(
        CookingCollectionTarget target,
        object cookedFood,
        out IReadOnlyList<string> targetTags,
        out IReadOnlyList<string> actualTags)
    {
        var policy = target.SpecialFoodTargetPolicy;
        targetTags = policy?.FoodTags ?? Array.Empty<string>();
        actualTags = Array.Empty<string>();
        if (policy == null) return SpecialFoodTargetTagValidation.NotRequired;

        if (!TryReadFoodTagNames(cookedFood, out actualTags)) return SpecialFoodTargetTagValidation.Unreadable;
        if (policy.Matches(actualTags)) return SpecialFoodTargetTagValidation.Matched;
        return IsYuumaControlledProgressionTarget(target)
            ? SpecialFoodTargetTagValidation.ControlledProgression
            : SpecialFoodTargetTagValidation.Mismatched;
    }

    private static bool TryDetectSpecialFoodTargetPolicyChanged(
        AutomationCookingJob job,
        out string originalSignature,
        out string currentSignature,
        out IReadOnlyList<string> originalTags,
        out IReadOnlyList<string> currentTags,
        out long originalRevision,
        out long currentRevision,
        out bool comparisonAvailable)
    {
        var expectedPolicy = job.Target.SpecialFoodTargetPolicy;
        originalSignature = expectedPolicy?.Signature ?? "";
        originalTags = expectedPolicy?.FoodTags ?? Array.Empty<string>();
        currentSignature = "";
        currentTags = Array.Empty<string>();
        originalRevision = job.SpecialFoodTargetRevision;
        currentRevision = 0;
        comparisonAvailable = true;
        if (expectedPolicy == null) return false;

        if (IsYuumaBossTarget(job.Target))
        {
            if (!RuntimeSpecialBusinessContextService.TryGetActiveYuumaFoodTargetState(
                    out var currentYuumaPolicy,
                    out currentRevision)
                || currentYuumaPolicy == null
                || currentYuumaPolicy.BusinessGeneration != expectedPolicy.BusinessGeneration
                || originalRevision <= 0
                || currentRevision <= 0)
            {
                comparisonAvailable = false;
                return false;
            }

            currentSignature = currentYuumaPolicy.Signature;
            currentTags = currentYuumaPolicy.FoodTags;
            return !expectedPolicy.HasSameIdentity(currentYuumaPolicy)
                || currentRevision != originalRevision;
        }

        RuntimeSpecialBusinessContextService.TryGetActiveSpecialFoodTargetPolicy(out var currentPolicy);
        currentSignature = currentPolicy?.Signature ?? "";
        currentTags = currentPolicy?.FoodTags ?? Array.Empty<string>();
        if (currentPolicy == null)
        {
            comparisonAvailable = false;
            return false;
        }

        return !expectedPolicy.HasSameIdentity(currentPolicy);
    }

    private static string FormatSpecialFoodTargetForMessage(string signature, IReadOnlyList<string> tags)
    {
        if (string.IsNullOrWhiteSpace(signature)) return "无有效特殊料理目标";
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
            AppendSpecialFoodTargetCookingJobDiagnostic(
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
            if (!TryReacquireAutomationCooker(
                    job,
                    out var bindingBefore,
                    out var bindingFailure,
                    out var ownershipDiagnostic))
            {
                message = $"无法从当前物理厨具目录重新取得同一锅次，未清理当前厨具"
                    + $"（{bindingFailure}）：{ownershipDiagnostic}";
                return false;
            }

            var cookController = bindingBefore.Controller;

            if (!TryReadCookControllerResetState(
                    cookController,
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

            if (!TryInvokeInstance(cookController, "CloseCookingVisual", Array.Empty<object?>()))
            {
                message = "无法关闭 CookController 料理视觉，厨具复位未执行。";
                return false;
            }
            if (!WriteMember(cookController, "LastResult", cookedFood))
            {
                message = "无法写入 CookController.LastResult，厨具复位未确认。";
                return false;
            }

            if (!WriteMember(cookController, "Result", null))
            {
                message = "无法清空 CookController.Result，厨具复位未确认。";
                return false;
            }

            if (!WriteMember(cookController, "ChosenRecipe", null))
            {
                message = "无法清空 CookController.ChosenRecipe，厨具复位未确认。";
                return false;
            }

            if (!TryCreateIdleCookPhaseValue(cookController, out var phaseValue))
            {
                message = "无法解析 CookController.Phase 的运行时类型，厨具复位未确认。";
                return false;
            }

            if (!WriteMember(cookController, "Phase", phaseValue))
            {
                message = "无法写入 CookController.Phase=Idle，厨具复位未确认。";
                return false;
            }

            if (!TryReacquireAutomationCooker(
                    job,
                    out var bindingAfter,
                    out bindingFailure,
                    out ownershipDiagnostic))
            {
                message = $"复位后无法从当前物理目录严格确认同一厨具"
                    + $"（{bindingFailure}）：{ownershipDiagnostic}";
                return false;
            }

            if (!TryReadCookControllerResetState(
                    bindingAfter.Controller,
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

    private static OrderPreparationRequest BuildOrderRequestFromCookingJob(AutomationCookingJob job)
    {
        var observedAtUtc = DateTime.UtcNow;
        var delivery = ObserveAutomationCookingJobControl(
            job,
            RuntimeAutomationControlStage.FoodDelivery,
            observedAtUtc);
        var completion = ObserveAutomationCookingJobControl(
            job,
            RuntimeAutomationControlStage.OrderEvaluation,
            observedAtUtc);
        return BuildOrderRequestFromCookingTarget(
            job.Target,
            delivery.Allowed,
            completion.Allowed);
    }

    private static OrderPreparationRequest BuildOrderRequestFromCookingTarget(
        CookingCollectionTarget target,
        bool autoDeliverFood,
        bool autoCompleteOrder)
    {
        return new OrderPreparationRequest
        {
            TraceId = target.TraceId,
            OrderKey = target.OrderKey,
            OrderLifecycleSequence = target.OrderBinding?.LifecycleSequence
                ?? target.RequestedOrderLifecycleSequence,
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
            PredictedFoodTagsProvided = true,
            ExpectedFoodModifierTags = target.ExpectedFoodModifierTags,
            SpecialTargetChallenge = target.SpecialFoodTargetPolicy?.ChallengeType ?? "",
            SpecialTargetOwner = target.SpecialFoodTargetPolicy?.Owner ?? "",
            SpecialTargetGeneration = target.SpecialFoodTargetPolicy?.BusinessGeneration ?? 0,
            SpecialTargetRevision = target.SpecialFoodTargetRevision,
            SpecialTargetFoodTags = target.SpecialTargetFoodTags,
            SpecialTargetMatchMode = target.SpecialFoodTargetPolicy?.MatchModeValue ?? "",
            SpecialTargetSignature = target.SpecialFoodTargetPolicy?.Signature ?? "",
            AllowYuumaControlledProgression = target.AllowYuumaControlledProgression,
            ExecutionMode = target.ExecutionMode,
            ExecutionReason = target.ExecutionReason,
            BeverageId = target.BeverageId,
            BeverageName = target.BeverageName,
            AutoCollectCooking = autoDeliverFood,
            AutoDeliverFood = autoDeliverFood,
            AutoCompleteOrder = autoCompleteOrder,
        };
    }

    private static bool IsAutomationCookingJobOwned(AutomationCookingJob job, out string diagnostic)
    {
        if (!TryReacquireAutomationCooker(
                job,
                out _,
                out var failureKind,
                out diagnostic))
        {
            diagnostic = $"fresh cooker rebind failed ({failureKind}): {diagnostic}";
            return false;
        }

        return true;
    }
}
