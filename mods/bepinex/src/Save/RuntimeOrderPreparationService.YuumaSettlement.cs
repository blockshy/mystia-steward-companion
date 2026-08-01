using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save.SpecialBusiness;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private const string EventManagerTypeName = "NightScene.EventUtility.EventManager";
    private const string StatusTrackerTypeName = "GameData.RunTime.Common.StatusTracker";
    private const string YuumaSellableTypeName = "GameData.Core.Collections.Sellable";
    private const string YuumaOrderBaseTypeName =
        "NightScene.GuestManagementUtility.GuestsManager+OrderBase";
    private const string YuumaGuestGroupControllerTypeName =
        "NightScene.GuestManagementUtility.GuestGroupController";
    private const string YuumaEvaluationResultTypeName =
        "NightScene.GuestManagementUtility.GuestGroupController+EvaluationResult";
    private const string YuumaOrderChangeContextTypeName =
        "NightScene.PartnerUtility.PartnerManager+OrderChangeContext";
    private const string Il2CppActionGenericTypeName = "Il2CppSystem.Action`1";

    private enum YuumaOrderEvaluationRoute
    {
        Standard,
        ManualControlled,
    }

    private sealed record YuumaSettlementContext(
        long BusinessGeneration,
        nint OrderPointer,
        nint ControllerPointer,
        YuumaOrderEvaluationRoute EvaluationRoute,
        object? ManualEvaluationCallback);

    private sealed record YuumaDeliveryBookkeepingContext(
        RuntimeDeliveryItemKind Kind,
        object StatusTracker,
        MethodInfo ConsumeMethod,
        object PartnerManager,
        MethodInfo StatusMethod,
        MethodInfo DeskMethod,
        object Order,
        object ConsumeIds,
        object StatusContext,
        int DeskCode);

    private sealed record YuumaCookerExtractionContext(
        MethodInfo AvailabilityMethod,
        MethodInfo ExtractionMethod);

    private sealed record YuumaBeverageStorageContext(
        MethodInfo BeverageOutMethod,
        MethodInfo BeverageInRangeMethod,
        MethodInfo BeverageOutRangeMethod);

    /// <summary>
    /// Completes one Blood Pond Hell cooking job through the exact non-throw native delivery route.
    /// </summary>
    private static (bool Remove, string Message, string Code) TryFinalizeYuumaCookingJob(
        AutomationCookingJob job,
        object cookedFood)
    {
        if (!IsYuumaBossTarget(job.Target))
        {
            return (false, "非血池地狱订单不会进入专用结算事务。", OrderPreparationStepCodes.CookingPending);
        }

        if (!job.AutoDeliverFood)
        {
            return EnterManualHandoff(job, DateTime.UtcNow);
        }

        if (!job.AutoCompleteOrder)
        {
            return EnterManualHandoff(job, DateTime.UtcNow);
        }

        if (!TryCaptureActiveNightBusinessGeneration(out var businessGeneration)
            || businessGeneration != job.Target.SpecialFoodTargetPolicy?.BusinessGeneration)
        {
            return StopAutomationFoodDeliveryForEndedSession(job, resolveCommit: false);
        }

        if (job.YuumaSettlementTracker.Stage == YuumaSettlementTransactionStage.Completed)
        {
            return (
                true,
                $"{job.RecipeName} 已完成血池地狱订单送达、评价和状态通知。",
                OrderPreparationStepCodes.FoodDelivered);
        }

        if (job.YuumaSettlementTracker.Stage == YuumaSettlementTransactionStage.Uncertain)
        {
            return BlockUncertainYuumaSettlement(job, "结算事务此前已进入不确定状态。", Array.Empty<string>(), Array.Empty<string>());
        }

        if (job.YuumaSettlementTracker.Stage != YuumaSettlementTransactionStage.Ready)
        {
            job.YuumaSettlementTracker.MarkUncertain();
            return BlockUncertainYuumaSettlement(
                job,
                $"结算事务停留在不可重放阶段 {job.YuumaSettlementTracker.Stage}。",
                Array.Empty<string>(),
                Array.Empty<string>());
        }

        if (!TryValidateCurrentYuumaFoodTarget(job, out var targetDiagnostic))
        {
            return ContinueOrBlockAutomationDelivery(job, targetDiagnostic);
        }

        var request = BuildOrderRequestFromCookingTarget(job.Target);
        var runtimeOrder = FindYuumaRuntimeOrder(job.Target, request);
        if (!TryValidateYuumaSettlementOrder(
                job,
                runtimeOrder,
                cookedFood,
                out var targetTags,
                out var actualTags,
                out var settlementContext,
                out var validationDiagnostic))
        {
            return ContinueOrBlockAutomationDelivery(job, validationDiagnostic);
        }

        if (!TryPreflightYuumaSettlement(
                job,
                runtimeOrder,
                settlementContext,
                cookedFood,
                out var finalFoodSetter,
                out var bookkeepingContext,
                out var extractionContext,
                out var preflightDiagnostic))
        {
            return ContinueOrBlockAutomationDelivery(job, preflightDiagnostic);
        }

        if (!TryValidateYuumaCookerBeforeFoodCommit(
                job,
                cookedFood,
                out _,
                out var preCommitCookerDiagnostic))
        {
            return ContinueOrBlockAutomationDelivery(
                job,
                $"料理最终提交前厨具身份无法严格确认：{preCommitCookerDiagnostic}");
        }

        if (!job.YuumaSettlementTracker.TryBeginFoodCommit())
        {
            job.YuumaSettlementTracker.MarkUncertain();
            return BlockUncertainYuumaSettlement(
                job,
                "无法唯一锁定料理送达事务。",
                targetTags,
                actualTags);
        }

        try
        {
            finalFoodSetter.Invoke(runtimeOrder.Order, new[] { cookedFood });

            if (!job.YuumaSettlementTracker.MarkFoodCommitted())
            {
                throw new InvalidOperationException("料理送达完成后事务状态无法推进。");
            }

            if (!TryValidateCurrentYuumaFoodTarget(job, out var committedTargetDiagnostic))
            {
                throw new InvalidOperationException(
                    $"料理提交后特殊目标已失效：{committedTargetDiagnostic}");
            }

            var committedOrder = FindYuumaRuntimeOrder(job.Target, request);
            if (!TryValidateReacquiredYuumaSettlementOrder(
                    job,
                    committedOrder,
                    settlementContext,
                    cookedFood,
                    out var reacquireDiagnostic))
            {
                throw new InvalidOperationException($"料理提交后无法重新取得同一订单：{reacquireDiagnostic}");
            }

            if (!TryResetCookControllerAfterCommittedSideEffect(job, cookedFood, out var resetDiagnostic))
            {
                throw new InvalidOperationException($"料理已送达，但同一锅次无法严格复位：{resetDiagnostic}");
            }

            if (!TryCompleteYuumaCookerExtraction(
                    job,
                    extractionContext,
                    out var extractionDiagnostic))
            {
                throw new InvalidOperationException(extractionDiagnostic);
            }

            if (!job.YuumaSettlementTracker.MarkCleanupCommitted())
            {
                throw new InvalidOperationException("厨具清理完成后事务状态无法推进。");
            }

            if (!TryValidateCurrentYuumaFoodTarget(job, out var evaluationTargetDiagnostic))
            {
                throw new InvalidOperationException(
                    $"厨具出锅回调后特殊目标已失效：{evaluationTargetDiagnostic}");
            }

            var evaluationOrder = FindYuumaRuntimeOrder(job.Target, request);
            if (!TryValidateReacquiredYuumaSettlementOrder(
                    job,
                    evaluationOrder,
                    settlementContext,
                    cookedFood,
                    out var evaluationReacquireDiagnostic))
            {
                throw new InvalidOperationException(
                    $"厨具出锅回调后无法重新取得同一订单：{evaluationReacquireDiagnostic}");
            }

            if (!job.YuumaSettlementTracker.TryBeginEvaluation())
            {
                throw new InvalidOperationException("无法唯一锁定订单评价入口。");
            }

            if (!TryInvokeYuumaEvaluation(evaluationOrder, settlementContext, out var evaluationDiagnostic))
            {
                throw new InvalidOperationException(evaluationDiagnostic);
            }

            if (!job.YuumaSettlementTracker.MarkEvaluationCommitted())
            {
                throw new InvalidOperationException("订单评价返回后事务状态无法推进。");
            }

            if (!job.YuumaSettlementTracker.TryBeginBookkeeping())
            {
                throw new InvalidOperationException("无法唯一锁定送达状态通知入口。");
            }

            if (!TryApplyYuumaDeliveryBookkeeping(bookkeepingContext, out var bookkeepingDiagnostic))
            {
                throw new InvalidOperationException(bookkeepingDiagnostic);
            }

            if (!job.YuumaSettlementTracker.MarkBookkeepingCommitted())
            {
                throw new InvalidOperationException("送达状态通知完成后事务状态无法推进。");
            }

            var actualFoodId = job.Target.FoodId;
            var evaluationRoute = settlementContext.EvaluationRoute
                == YuumaOrderEvaluationRoute.ManualControlled
                    ? "manual-controlled"
                    : "standard";
            var controlledProgression = job.AllowYuumaControlledProgression;
            var progressionMessage = controlledProgression
                ? "本订单按受控推进执行：仍精确使用原订单料理和酒水，但不承诺成品满足当前双 Tag；伤害与狂暴由游戏原生规则结算。"
                : "";
            var message = $"{job.Target.FoodName} 已送达血池地狱订单，并按订单原生路由完成评价与状态通知。"
                + resetDiagnostic
                + extractionDiagnostic
                + $"评价路由={evaluationRoute}。"
                + progressionMessage;
            AppendSpecialFoodTargetCookingJobDiagnostic(
                controlledProgression
                    ? "yuuma-controlled-progression-settlement-completed"
                    : "yuuma-settlement-completed",
                job,
                controlledProgression
                    ? "evaluated-and-notified-controlled-progression"
                    : "evaluated-and-notified",
                actualFoodId,
                targetTags,
                actualTags,
                message);
            RecordAutomationRuntimeEvent(
                OrderPreparationStepCodes.FoodDelivered,
                job,
                message,
                actualFoodId,
                targetTags,
                actualTags,
                outcome: "completed",
                reasonCode: controlledProgression
                    ? "yuuma-order-settled-controlled-progression"
                    : "yuuma-order-settled",
                terminal: true);
            return (true, message, OrderPreparationStepCodes.FoodDelivered);
        }
        catch (Exception ex)
        {
            job.YuumaSettlementTracker.MarkUncertain();
            return BlockUncertainYuumaSettlement(
                job,
                ex.GetBaseException().Message,
                targetTags,
                actualTags);
        }
    }

    private static (bool Ok, string Message, string Code) TryDeliverYuumaOrderBeverage(
        CookingCollectionTarget target,
        int beverageId,
        string beverageName,
        string orderLabel)
    {
        if (!IsYuumaBossTarget(target))
        {
            return (false, "非血池地狱订单不能进入专用酒水送达。", "");
        }

        var targetPolicy = target.SpecialFoodTargetPolicy;
        if (targetPolicy == null)
        {
            return (false, "血池地狱酒水目标缺少经营代际与规范策略。", "");
        }

        if (!TryValidateCurrentYuumaTarget(target, out var currentTargetDiagnostic))
        {
            return (
                false,
                $"血池地狱酒水目标已失效，本轮未执行副作用：{currentTargetDiagnostic}",
                OrderPreparationStepCodes.CookingPending);
        }

        var request = BuildOrderRequestFromCookingTarget(target);
        var runtimeOrder = FindYuumaRuntimeOrder(target, request);
        if (runtimeOrder.Order == null || runtimeOrder.Controller == null || runtimeOrder.Manager == null)
        {
            return (
                false,
                $"血池地狱专用结算查询无法取得完整订单，本轮未送达酒水：{runtimeOrder.Diagnostic}",
                "");
        }

        if (!TryCreateYuumaSettlementContext(
                runtimeOrder,
                targetPolicy.BusinessGeneration,
                out var settlementContext,
                out var settlementContextDiagnostic))
        {
            return (
                false,
                $"血池地狱酒水订单身份无法严格确认：{settlementContextDiagnostic}",
                "");
        }

        if (!TryReadYuumaOrderDeliveryState(
                runtimeOrder.Order,
                out var servedFood,
                out var foodInAir,
                out var servedBeverage,
                out var beverageInAir,
                out var deliveryStateDiagnostic))
        {
            return (false, $"无法精确读取血池地狱订单状态：{deliveryStateDiagnostic}", "");
        }

        if (beverageInAir != null)
        {
            return (
                false,
                "血池地狱订单已有酒水正在由游戏原生流程送达；等待该流程结束后再处理。",
                OrderPreparationStepCodes.CookingPending);
        }

        if (servedBeverage != null)
        {
            if (!TryValidateYuumaDeliveredItemAgainstOriginalOrder(
                    target,
                    servedBeverage,
                    RuntimeDeliveryItemKind.Beverage,
                    out var existingBeverageDiagnostic))
            {
                return (
                    false,
                    $"血池地狱订单已有酒水，但它不满足当前原订单：{existingBeverageDiagnostic}",
                    "");
            }

            return (true, $"{beverageName} 已存在于{orderLabel}，本次未重复送达。", "");
        }

        if (servedFood != null || foodInAir != null)
        {
            return (
                false,
                "血池地狱订单已有料理或料理正在送达；不自动把酒水作为最终结算项。",
                OrderPreparationStepCodes.CookingPending);
        }

        var currentQuantity = GetBeverageQuantity(beverageId);
        if (currentQuantity < -1)
        {
            return (false, $"{beverageName} 当前库存值 {currentQuantity} 非法，本轮未送达。", "");
        }

        if (currentQuantity == 0)
        {
            return (false, $"{beverageName} 当前库存为 0，无法送达{orderLabel}。", "");
        }

        var sellable = InvokeStatic(DataBaseCoreTypeName, "AsNewBeverage", new object?[] { beverageId });
        if (sellable == null || !IsSellable(sellable, sellableType: 1, id: beverageId))
        {
            return (false, $"无法创建并确认酒水对象：{beverageName} #{beverageId}。", "");
        }

        if (!TryValidateYuumaDeliveredItemAgainstOriginalOrder(
                target,
                sellable,
                RuntimeDeliveryItemKind.Beverage,
                out var orderIdentityDiagnostic))
        {
            return (false, orderIdentityDiagnostic, "");
        }

        if (!TryReadYuumaBeverageCostPolicy(
                out var isFreeBeverage,
                out var extraCostBeverages,
                out var costPolicyDiagnostic))
        {
            return (false, costPolicyDiagnostic, "");
        }

        var requiredQuantity = isFreeBeverage ? 1 : extraCostBeverages;
        if (currentQuantity > 0 && currentQuantity < requiredQuantity)
        {
            return (
                false,
                $"{beverageName} 当前库存 {currentQuantity}，但游戏效果要求本次占用 {requiredQuantity}，本轮未送达。",
                "");
        }

        if (!TryResolveYuumaFinalSetter(
                runtimeOrder.Order,
                sellable,
                RuntimeDeliveryItemKind.Beverage,
                out _,
                out var setterDiagnostic))
        {
            return (false, $"血池地狱酒水送达预检失败：{setterDiagnostic}", "");
        }

        if (!TryCreateYuumaBookkeepingContext(
                runtimeOrder,
                sellable,
                RuntimeDeliveryItemKind.Beverage,
                out _,
                out var preflightDiagnostic))
        {
            return (false, $"血池地狱酒水送达预检失败：{preflightDiagnostic}", "");
        }

        if (!TryCreateYuumaBeverageStorageContext(
                out var storageContext,
                out var storageDiagnostic))
        {
            return (false, $"血池地狱酒水送达预检失败：{storageDiagnostic}", "");
        }

        try
        {
            storageContext.BeverageOutMethod.Invoke(
                null,
                new object?[] { beverageId, false });

            if (!TryValidateCurrentYuumaTarget(target, out var deductedTargetDiagnostic))
            {
                return (
                    false,
                    $"{beverageName} 的基础库存已扣除，但特殊目标无法继续确认：{deductedTargetDiagnostic}",
                    OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
            }

            var deductedOrder = FindYuumaRuntimeOrder(target, request);
            if (!TryValidateReacquiredYuumaBeverageOrder(
                    deductedOrder,
                    settlementContext,
                    sellable,
                    expectCommitted: false,
                    out var deductedReacquireDiagnostic))
            {
                return (
                    false,
                    $"{beverageName} 的基础库存已扣除，但无法重新确认同一未提交订单：{deductedReacquireDiagnostic}",
                    OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
            }

            if (!TryResolveYuumaFinalSetter(
                    deductedOrder.Order!,
                    sellable,
                    RuntimeDeliveryItemKind.Beverage,
                    out var freshFinalBeverageSetter,
                    out var freshSetterDiagnostic))
            {
                return (
                    false,
                    $"{beverageName} 的基础库存已扣除，但 fresh 订单 setter 无法确认：{freshSetterDiagnostic}",
                    OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
            }

            freshFinalBeverageSetter.Invoke(deductedOrder.Order, new[] { sellable });

            if (!TryValidateCurrentYuumaTarget(target, out var committedTargetDiagnostic))
            {
                return (
                    false,
                    $"{beverageName} 的库存与最终 setter 已执行，但特殊目标无法继续确认：{committedTargetDiagnostic}",
                    OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
            }

            var committedOrder = FindYuumaRuntimeOrder(target, request);
            if (!TryValidateReacquiredYuumaBeverageOrder(
                    committedOrder,
                    settlementContext,
                    sellable,
                    expectCommitted: true,
                    out var committedReacquireDiagnostic))
            {
                return (
                    false,
                    $"{beverageName} 的基础库存和最终 setter 已执行，但无法重新确认同一订单：{committedReacquireDiagnostic}",
                    OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
            }

            if (!TryRecoverPatientAfterPartialDelivery(
                    committedOrder,
                    deliveredItemCount: 1,
                    out var patientRecoveryMessage))
            {
                return (
                    false,
                    $"{beverageName} 已送达，但无法严格完成原生部分送达耐心恢复：{patientRecoveryMessage}",
                    OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
            }

            if (!TryValidateCurrentYuumaTarget(target, out var recoveredTargetDiagnostic))
            {
                return (
                    false,
                    $"{beverageName} 已送达并处理耐心恢复，但特殊目标无法继续确认：{recoveredTargetDiagnostic}",
                    OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
            }

            var recoveredOrder = FindYuumaRuntimeOrder(target, request);
            if (!TryValidateReacquiredYuumaBeverageOrder(
                    recoveredOrder,
                    settlementContext,
                    sellable,
                    expectCommitted: true,
                    out var recoveredReacquireDiagnostic))
            {
                return (
                    false,
                    $"{beverageName} 已送达并处理耐心恢复，但无法重新确认同一订单：{recoveredReacquireDiagnostic}",
                    OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
            }

            ApplyYuumaBeverageCostPolicy(
                storageContext,
                beverageId,
                isFreeBeverage,
                extraCostBeverages);

            if (!TryValidateCurrentYuumaTarget(target, out var adjustedTargetDiagnostic))
            {
                return (
                    false,
                    $"{beverageName} 的库存调整与最终 setter 已执行，但特殊目标无法继续确认：{adjustedTargetDiagnostic}",
                    OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
            }

            var adjustedOrder = FindYuumaRuntimeOrder(target, request);
            if (!TryValidateReacquiredYuumaBeverageOrder(
                    adjustedOrder,
                    settlementContext,
                    sellable,
                    expectCommitted: true,
                    out var adjustedReacquireDiagnostic))
            {
                return (
                    false,
                    $"{beverageName} 的库存调整与最终 setter 已执行，但无法重新确认同一订单：{adjustedReacquireDiagnostic}",
                    OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
            }

            if (!TryCreateYuumaBookkeepingContext(
                    adjustedOrder,
                    sellable,
                    RuntimeDeliveryItemKind.Beverage,
                    out var freshBookkeepingContext,
                    out var freshBookkeepingDiagnostic))
            {
                return (
                    false,
                    $"{beverageName} 已送达，但 fresh 原生送达状态上下文无法确认：{freshBookkeepingDiagnostic}",
                    OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
            }

            if (!TryApplyYuumaDeliveryBookkeeping(freshBookkeepingContext, out var bookkeepingDiagnostic))
            {
                return (
                    false,
                    $"{beverageName} 已送达，但原生送达状态通知无法确认：{bookkeepingDiagnostic}",
                    OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
            }

            var quantityText = currentQuantity < 0
                ? "无限库存"
                : $"库存剩余 {Math.Max(0, currentQuantity - (isFreeBeverage ? 0 : extraCostBeverages))}";
            return (
                true,
                $"{beverageName} 已送达{orderLabel}，并完成血池地狱酒水消耗、部分送达耐心恢复与状态通知（{quantityText}）。"
                + (string.IsNullOrWhiteSpace(patientRecoveryMessage) ? "" : patientRecoveryMessage),
                "");
        }
        catch (Exception ex)
        {
            return (
                false,
                $"{beverageName} 的血池地狱送达事务已开始，但结果无法确认：{ex.GetBaseException().Message}",
                OrderPreparationStepCodes.BeverageDeliveryCommitUncertain);
        }
    }

    private static RuntimeOrderMatch FindYuumaRuntimeOrder(
        CookingCollectionTarget target,
        OrderPreparationRequest request)
    {
        var policy = target.SpecialFoodTargetPolicy;
        if (policy == null || !IsNightBusinessGenerationActive(policy.BusinessGeneration))
        {
            return new RuntimeOrderMatch
            {
                Diagnostic = "Blood Pond Hell settlement generation is no longer active",
            };
        }

        return target.Kind == CookingCollectionTargetKind.NormalOrder
            ? FindRuntimeNormalOrder(request, RuntimeOrderLookupPurpose.YuumaSettlement)
            : FindRuntimeOrder(request, RuntimeOrderLookupPurpose.YuumaSettlement);
    }

    private static bool TryValidateYuumaSettlementOrder(
        AutomationCookingJob job,
        RuntimeOrderMatch runtimeOrder,
        object cookedFood,
        out IReadOnlyList<string> targetTags,
        out IReadOnlyList<string> actualTags,
        out YuumaSettlementContext context,
        out string diagnostic)
    {
        targetTags = job.Target.SpecialTargetFoodTags.ToArray();
        actualTags = ReadFoodTagNames(cookedFood).ToArray();
        context = null!;
        diagnostic = "";

        if (runtimeOrder.Order == null || runtimeOrder.Controller == null || runtimeOrder.Manager == null)
        {
            diagnostic = $"血池地狱结算前无法取得完整订单对象：{runtimeOrder.Diagnostic}";
            return false;
        }

        if (!TryValidateCurrentYuumaFoodTarget(job, out diagnostic))
        {
            return false;
        }

        if (!TryValidateYuumaDeliveredItemAgainstOriginalOrder(
                job.Target,
                cookedFood,
                RuntimeDeliveryItemKind.Food,
                out diagnostic))
        {
            return false;
        }

        if (!TryReadYuumaOrderDeliveryState(
                runtimeOrder.Order,
                out var servedFood,
                out var foodInAir,
                out var servedBeverage,
                out var beverageInAir,
                out var stateDiagnostic))
        {
            diagnostic = $"无法读取血池地狱最终送达字段：{stateDiagnostic}";
            return false;
        }

        if (servedFood != null || foodInAir != null)
        {
            diagnostic = "血池地狱订单已有料理或料理正在送达，拒绝覆盖。";
            return false;
        }

        if (beverageInAir != null)
        {
            diagnostic = "血池地狱订单仍有酒水正在由游戏原生流程送达，料理不能成为最终自动结算项。";
            return false;
        }

        if (servedBeverage == null)
        {
            diagnostic = "血池地狱订单尚未送达酒水，料理不能成为最终自动结算项。";
            return false;
        }

        if (!TryValidateYuumaDeliveredItemAgainstOriginalOrder(
                job.Target,
                servedBeverage,
                RuntimeDeliveryItemKind.Beverage,
                out diagnostic))
        {
            return false;
        }

        return TryCreateYuumaSettlementContext(
            runtimeOrder,
            job.Target.SpecialFoodTargetPolicy!.BusinessGeneration,
            out context,
            out diagnostic);
    }

    private static bool TryCreateYuumaSettlementContext(
        RuntimeOrderMatch runtimeOrder,
        long businessGeneration,
        out YuumaSettlementContext context,
        out string diagnostic)
    {
        context = null!;
        diagnostic = "";
        if (runtimeOrder.Order == null || runtimeOrder.Controller == null || runtimeOrder.Manager == null)
        {
            diagnostic = $"血池地狱结算前无法取得完整订单对象：{runtimeOrder.Diagnostic}";
            return false;
        }

        if (!IsNightBusinessGenerationActive(businessGeneration))
        {
            diagnostic = "血池地狱订单所属经营代际已失效。";
            return false;
        }

        if (!TryReadNativeObjectPointer(runtimeOrder.Order, out var orderPointer)
            || !TryReadNativeObjectPointer(runtimeOrder.Controller, out var controllerPointer))
        {
            diagnostic = "血池地狱订单或控制器缺少精确原生身份。";
            return false;
        }

        var identity = YuumaChallengeOrderIdentity.Read(runtimeOrder.Order, runtimeOrder.Controller);
        if (!identity.Verified
            || identity.OrderGuestId != SpecialBusinessGuestIds.YuumaBoss
            || identity.ControllerGuestId != SpecialBusinessGuestIds.YuumaBoss)
        {
            diagnostic = $"血池地狱订单身份复核失败：{identity.Reason}";
            return false;
        }

        var route = runtimeOrder.ManualOrder
            ? YuumaOrderEvaluationRoute.ManualControlled
            : YuumaOrderEvaluationRoute.Standard;
        if (route == YuumaOrderEvaluationRoute.ManualControlled
            && runtimeOrder.ManualEvaluationCallback == null)
        {
            diagnostic = "手动控制订单缺少同一 SetManualControllerOrderInternal 捕获的评价回调。";
            return false;
        }

        context = new YuumaSettlementContext(
            businessGeneration,
            orderPointer,
            controllerPointer,
            route,
            runtimeOrder.ManualEvaluationCallback);
        return true;
    }

    private static bool TryValidateReacquiredYuumaSettlementOrder(
        AutomationCookingJob job,
        RuntimeOrderMatch runtimeOrder,
        YuumaSettlementContext context,
        object cookedFood,
        out string diagnostic)
    {
        diagnostic = "";
        if (runtimeOrder.Order == null || runtimeOrder.Controller == null || runtimeOrder.Manager == null)
        {
            diagnostic = $"重新取得的订单上下文不完整：{runtimeOrder.Diagnostic}";
            return false;
        }

        if (!TryValidateCurrentYuumaFoodTarget(job, out diagnostic))
        {
            return false;
        }

        if (!TryReadNativeObjectPointer(runtimeOrder.Order, out var orderPointer)
            || orderPointer != context.OrderPointer
            || !TryReadNativeObjectPointer(runtimeOrder.Controller, out var controllerPointer)
            || controllerPointer != context.ControllerPointer)
        {
            diagnostic = "重新取得的订单或控制器不是送达前锁定的同一原生对象。";
            return false;
        }

        var currentRoute = runtimeOrder.ManualOrder
            ? YuumaOrderEvaluationRoute.ManualControlled
            : YuumaOrderEvaluationRoute.Standard;
        if (currentRoute != context.EvaluationRoute)
        {
            diagnostic = "订单 ManualOrder 状态在送达边界后发生变化。";
            return false;
        }

        if (currentRoute == YuumaOrderEvaluationRoute.ManualControlled
            && (runtimeOrder.ManualEvaluationCallback == null
                || !ReferenceEquals(runtimeOrder.ManualEvaluationCallback, context.ManualEvaluationCallback)))
        {
            diagnostic = "重新取得的手动订单没有绑定同一原生评价回调。";
            return false;
        }

        if (!TryReadYuumaOrderDeliveryState(
                runtimeOrder.Order,
                out var servedFood,
                out var foodInAir,
                out var servedBeverage,
                out var beverageInAir,
                out var deliveryStateDiagnostic))
        {
            diagnostic = $"重新取得的订单送达状态无法完整读取：{deliveryStateDiagnostic}";
            return false;
        }

        if (foodInAir != null || beverageInAir != null)
        {
            diagnostic = "重新取得的订单仍有料理或酒水处于游戏原生送达流程。";
            return false;
        }

        if (servedFood == null
            || CompareObjectIdentity(servedFood, cookedFood) != RuntimeObjectIdentityComparison.Same)
        {
            diagnostic = "重新取得的订单最终料理不是本 cooking job 的精确成品。";
            return false;
        }

        var beverageDiagnostic = "";
        if (servedBeverage == null
            || !TryValidateYuumaDeliveredItemAgainstOriginalOrder(
                job.Target,
                servedBeverage,
                RuntimeDeliveryItemKind.Beverage,
                out beverageDiagnostic))
        {
            diagnostic = servedBeverage == null
                ? "重新取得的订单缺少最终酒水。"
                : $"重新取得的订单最终酒水不再满足原订单：{beverageDiagnostic}";
            return false;
        }

        var identity = YuumaChallengeOrderIdentity.Read(runtimeOrder.Order, runtimeOrder.Controller);
        if (!identity.Verified
            || identity.OrderGuestId != SpecialBusinessGuestIds.YuumaBoss
            || identity.ControllerGuestId != SpecialBusinessGuestIds.YuumaBoss)
        {
            diagnostic = $"重新取得的血池地狱订单身份无效：{identity.Reason}";
            return false;
        }

        return IsNightBusinessGenerationActive(context.BusinessGeneration);
    }

    private static bool TryValidateCurrentYuumaFoodTarget(
        AutomationCookingJob job,
        out string diagnostic)
    {
        diagnostic = "";
        if (TryDetectSpecialFoodTargetPolicyChanged(
                job,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out var comparisonAvailable))
        {
            diagnostic = "血池地狱料理目标已变化。";
            return false;
        }

        if (!comparisonAvailable)
        {
            diagnostic = "血池地狱料理目标暂不可读。";
            return false;
        }

        return true;
    }

    private static bool TryValidateCurrentYuumaTarget(
        CookingCollectionTarget target,
        out string diagnostic)
    {
        diagnostic = "";
        var expectedPolicy = target.SpecialFoodTargetPolicy;
        if (expectedPolicy == null
            || !IsNightBusinessGenerationActive(expectedPolicy.BusinessGeneration))
        {
            diagnostic = "经营代际已失效或目标缺少规范策略。";
            return false;
        }

        if (!RuntimeSpecialBusinessContextService.TryGetActiveYuumaFoodTargetState(
                out var currentPolicy,
                out var currentRevision)
            || currentPolicy == null
            || currentRevision <= 0)
        {
            diagnostic = "当前双 Tag 策略或 revision 暂不可读。";
            return false;
        }

        if (target.SpecialFoodTargetRevision <= 0
            || !expectedPolicy.HasSameIdentity(currentPolicy)
            || target.SpecialFoodTargetRevision != currentRevision)
        {
            diagnostic = $"当前双 Tag 策略或 revision 已变化（请求={target.SpecialFoodTargetRevision}; 当前={currentRevision}）。";
            return false;
        }

        return true;
    }

    private static bool TryValidateReacquiredYuumaBeverageOrder(
        RuntimeOrderMatch runtimeOrder,
        YuumaSettlementContext context,
        object deliveredBeverage,
        bool expectCommitted,
        out string diagnostic)
    {
        diagnostic = "";
        if (runtimeOrder.Order == null || runtimeOrder.Controller == null || runtimeOrder.Manager == null)
        {
            diagnostic = $"重新取得的酒水订单上下文不完整：{runtimeOrder.Diagnostic}";
            return false;
        }

        if (!TryReadNativeObjectPointer(runtimeOrder.Order, out var orderPointer)
            || orderPointer != context.OrderPointer
            || !TryReadNativeObjectPointer(runtimeOrder.Controller, out var controllerPointer)
            || controllerPointer != context.ControllerPointer)
        {
            diagnostic = "重新取得的酒水订单或控制器不是提交前锁定的同一原生对象。";
            return false;
        }

        var currentRoute = runtimeOrder.ManualOrder
            ? YuumaOrderEvaluationRoute.ManualControlled
            : YuumaOrderEvaluationRoute.Standard;
        if (currentRoute != context.EvaluationRoute
            || (currentRoute == YuumaOrderEvaluationRoute.ManualControlled
                && (runtimeOrder.ManualEvaluationCallback == null
                    || !ReferenceEquals(runtimeOrder.ManualEvaluationCallback, context.ManualEvaluationCallback))))
        {
            diagnostic = "酒水提交后订单评价路由或手动评价回调发生变化。";
            return false;
        }

        var identity = YuumaChallengeOrderIdentity.Read(runtimeOrder.Order, runtimeOrder.Controller);
        if (!identity.Verified
            || identity.OrderGuestId != SpecialBusinessGuestIds.YuumaBoss
            || identity.ControllerGuestId != SpecialBusinessGuestIds.YuumaBoss)
        {
            diagnostic = $"重新取得的血池地狱酒水订单身份无效：{identity.Reason}";
            return false;
        }

        if (!TryReadYuumaOrderDeliveryState(
                runtimeOrder.Order,
                out var servedFood,
                out var foodInAir,
                out var servedBeverage,
                out var beverageInAir,
                out var stateDiagnostic))
        {
            diagnostic = $"酒水提交后无法读取订单最终字段：{stateDiagnostic}";
            return false;
        }

        if (beverageInAir != null)
        {
            diagnostic = "酒水事务期间出现游戏原生待送达酒水，不能继续执行后续副作用。";
            return false;
        }

        if (servedFood != null || foodInAir != null)
        {
            diagnostic = "酒水提交期间料理状态发生变化，不能继续补写库存与经营通知。";
            return false;
        }

        if (!expectCommitted && servedBeverage != null)
        {
            diagnostic = "基础扣库回调后订单酒水字段已被其他流程写入。";
            return false;
        }

        if (expectCommitted
            && (servedBeverage == null
                || CompareObjectIdentity(servedBeverage, deliveredBeverage)
                    != RuntimeObjectIdentityComparison.Same))
        {
            diagnostic = "重新取得的订单最终酒水不是本次提交的精确对象。";
            return false;
        }

        return IsNightBusinessGenerationActive(context.BusinessGeneration);
    }

    private static bool TryResolveYuumaFinalSetter(
        object order,
        object deliveredItem,
        RuntimeDeliveryItemKind kind,
        out MethodInfo setter,
        out string diagnostic)
    {
        setter = null!;
        diagnostic = "";
        var setterName = kind == RuntimeDeliveryItemKind.Food
            ? "set_ServFood"
            : "set_ServBeverage";
        var candidates = order
            .GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method =>
            {
                if (!string.Equals(method.Name, setterName, StringComparison.Ordinal)
                    || method.ReturnType != typeof(void))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1
                    && string.Equals(
                        parameters[0].ParameterType.FullName,
                        YuumaSellableTypeName,
                        StringComparison.Ordinal)
                    && parameters[0].ParameterType.IsInstanceOfType(deliveredItem);
            })
            .ToArray();
        if (candidates.Length != 1)
        {
            diagnostic = candidates.Length == 0
                ? $"未找到唯一精确 {setterName}(Sellable)"
                : $"发现 {candidates.Length} 个可接受当前 Sellable 的 {setterName}，无法确定入口";
            return false;
        }

        setter = candidates[0];
        return true;
    }

    private static bool TryInvokeYuumaEvaluation(
        RuntimeOrderMatch runtimeOrder,
        YuumaSettlementContext context,
        out string diagnostic)
    {
        diagnostic = "";
        if (runtimeOrder.Order == null || runtimeOrder.Controller == null || runtimeOrder.Manager == null)
        {
            diagnostic = "血池地狱评价上下文不完整。";
            return false;
        }

        var fulfilledMethod = FindExactInstanceMethod(
            runtimeOrder.Order.GetType(),
            "get_IsFullfilled",
            parameterCount: 0,
            returnType: typeof(bool));
        if (fulfilledMethod == null || fulfilledMethod.Invoke(runtimeOrder.Order, null) is not bool fulfilled || !fulfilled)
        {
            diagnostic = "get_IsFullfilled 未确认最终料理与酒水已经完整送达。";
            return false;
        }

        if (context.EvaluationRoute == YuumaOrderEvaluationRoute.ManualControlled)
        {
            if (runtimeOrder.ManualEvaluationCallback == null)
            {
                diagnostic = "手动控制订单缺少精确评价回调，禁止降级到标准评价。";
                return false;
            }

            if (!TryResolveYuumaEvaluationMethod(
                    runtimeOrder,
                    context,
                    out var manualMethod,
                    out diagnostic))
            {
                return false;
            }

            manualMethod.Invoke(
                runtimeOrder.Manager,
                new[] { runtimeOrder.Controller, runtimeOrder.ManualEvaluationCallback });
            return true;
        }

        if (context.EvaluationRoute == YuumaOrderEvaluationRoute.Standard)
        {
            if (!TryResolveYuumaEvaluationMethod(
                    runtimeOrder,
                    context,
                    out var standardMethod,
                    out diagnostic))
            {
                return false;
            }

            standardMethod.Invoke(runtimeOrder.Manager, new object?[] { runtimeOrder.Controller, false, null });
            return true;
        }

        diagnostic = "订单评价路由无效。";
        return false;
    }

    private static bool TryResolveYuumaEvaluationMethod(
        RuntimeOrderMatch runtimeOrder,
        YuumaSettlementContext context,
        out MethodInfo method,
        out string diagnostic)
    {
        method = null!;
        diagnostic = "";
        if (runtimeOrder.Manager == null || runtimeOrder.Controller == null)
        {
            diagnostic = "GuestsManager 或订单控制器不可用。";
            return false;
        }

        var methodName = context.EvaluationRoute == YuumaOrderEvaluationRoute.ManualControlled
            ? "EvaulateManualOrder"
            : "EvaluateOrder";
        var candidates = runtimeOrder.Manager
            .GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(candidate =>
            {
                if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                    || candidate.ReturnType != typeof(void))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                if (parameters.Length == 0
                    || !string.Equals(
                        parameters[0].ParameterType.FullName,
                        YuumaGuestGroupControllerTypeName,
                        StringComparison.Ordinal)
                    || !parameters[0].ParameterType.IsInstanceOfType(runtimeOrder.Controller))
                {
                    return false;
                }

                if (context.EvaluationRoute == YuumaOrderEvaluationRoute.ManualControlled)
                {
                    return parameters.Length == 2
                        && context.ManualEvaluationCallback != null
                        && parameters[1].ParameterType == context.ManualEvaluationCallback.GetType()
                        && IsExactYuumaManualEvaluationCallbackType(parameters[1].ParameterType);
                }

                return parameters.Length == 3
                    && parameters[1].ParameterType == typeof(bool)
                    && parameters[2].ParameterType == typeof(Il2CppSystem.Action);
            })
            .ToArray();
        if (candidates.Length != 1)
        {
            diagnostic = candidates.Length == 0
                ? $"未找到符合 BepInEx 783 精确声明的 {methodName} 入口。"
                : $"发现 {candidates.Length} 个符合参数形态的 {methodName} 入口，无法唯一选择。";
            return false;
        }

        method = candidates[0];
        return true;
    }

    private static bool TryApplyYuumaDeliveryBookkeeping(
        YuumaDeliveryBookkeepingContext context,
        out string diagnostic)
    {
        diagnostic = "";
        var expectedConsumeMethod = context.Kind == RuntimeDeliveryItemKind.Food
            ? "AddBussinessFoodConsumes"
            : "AddBussinessBeverageConsumes";
        var expectedStatusContext = context.Kind == RuntimeDeliveryItemKind.Food
            ? "FoodDelivered"
            : "BeverageDelivered";
        if (!string.Equals(context.ConsumeMethod.Name, expectedConsumeMethod, StringComparison.Ordinal)
            || !string.Equals(context.StatusMethod.Name, "OnOrderBaseStatusUpdate", StringComparison.Ordinal)
            || !string.Equals(context.DeskMethod.Name, "TryAddPlayerOccupiedDeskCode", StringComparison.Ordinal)
            || !string.Equals(context.StatusContext.ToString(), expectedStatusContext, StringComparison.Ordinal))
        {
            diagnostic = "评价前缓存的送达记账入口或枚举上下文不一致。";
            return false;
        }

        context.ConsumeMethod.Invoke(context.StatusTracker, new[] { context.ConsumeIds });
        context.StatusMethod.Invoke(
            context.PartnerManager,
            new[] { context.Order, context.StatusContext, (object)-1 });
        context.DeskMethod.Invoke(context.PartnerManager, new object?[] { context.DeskCode });
        return true;
    }

    private static bool TryPreflightYuumaSettlement(
        AutomationCookingJob job,
        RuntimeOrderMatch runtimeOrder,
        YuumaSettlementContext context,
        object cookedFood,
        out MethodInfo finalFoodSetter,
        out YuumaDeliveryBookkeepingContext bookkeepingContext,
        out YuumaCookerExtractionContext extractionContext,
        out string diagnostic)
    {
        finalFoodSetter = null!;
        bookkeepingContext = null!;
        extractionContext = null!;
        diagnostic = "";
        if (runtimeOrder.Order == null || runtimeOrder.Manager == null)
        {
            diagnostic = "订单或 GuestsManager 不可用。";
            return false;
        }

        if (!TryResolveYuumaFinalSetter(
                runtimeOrder.Order,
                cookedFood,
                RuntimeDeliveryItemKind.Food,
                out finalFoodSetter,
                out var setterDiagnostic)
            || FindExactInstanceMethod(runtimeOrder.Order.GetType(), "get_IsFullfilled", 0, typeof(bool)) == null)
        {
            diagnostic = $"OrderBase 最终料理 setter 或 fulfilled getter 形态不匹配：{setterDiagnostic}";
            return false;
        }

        if (context.EvaluationRoute == YuumaOrderEvaluationRoute.ManualControlled
            && runtimeOrder.ManualEvaluationCallback == null)
        {
            diagnostic = "手动控制订单缺少精确评价回调。";
            return false;
        }

        if (!TryResolveYuumaEvaluationMethod(
                runtimeOrder,
                context,
                out _,
                out var evaluationDiagnostic))
        {
            diagnostic = $"订单评价入口形态不匹配：{evaluationDiagnostic}";
            return false;
        }

        if (!TryCreateYuumaBookkeepingContext(
                runtimeOrder,
                cookedFood,
                RuntimeDeliveryItemKind.Food,
                out bookkeepingContext,
                out diagnostic))
        {
            return false;
        }

        return TryCreateYuumaCookerExtractionContext(
            job,
            cookedFood,
            out extractionContext,
            out diagnostic);
    }

    private static bool TryCreateYuumaCookerExtractionContext(
        AutomationCookingJob job,
        object cookedFood,
        out YuumaCookerExtractionContext context,
        out string diagnostic)
    {
        context = null!;
        diagnostic = "";
        var partnerManager = GetExactSingletonInstance(PartnerManagerTypeName);
        if (partnerManager == null)
        {
            diagnostic = "PartnerManager 单例不可用。";
            return false;
        }

        var availabilityMethods = partnerManager
            .GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return string.Equals(method.Name, "OnCookerAvailabilityUpdate", StringComparison.Ordinal)
                    && method.ReturnType == typeof(void)
                    && parameters.Length == 1
                    && parameters[0].ParameterType == typeof(int);
            })
            .ToArray();
        if (!TryValidateYuumaCookerBeforeFoodCommit(
                job,
                cookedFood,
                out var cookerBinding,
                out diagnostic))
        {
            return false;
        }

        var extractionMethods = cookerBinding.Controller
            .GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => string.Equals(method.Name, "AfterPlayerExtract", StringComparison.Ordinal)
                && method.ReturnType == typeof(void)
                && method.GetParameters().Length == 0)
            .ToArray();
        if (availabilityMethods.Length != 1 || extractionMethods.Length != 1)
        {
            diagnostic = "厨具可用性通知或出锅回调与 BepInEx 783 精确形态不一致。";
            return false;
        }

        context = new YuumaCookerExtractionContext(
            availabilityMethods[0],
            extractionMethods[0]);
        return true;
    }

    private static bool TryCompleteYuumaCookerExtraction(
        AutomationCookingJob job,
        YuumaCookerExtractionContext context,
        out string diagnostic)
    {
        diagnostic = "";
        try
        {
            if (!TryReacquireAutomationCooker(
                    job,
                    out _,
                    out var bindingFailure,
                    out var bindingDiagnostic))
            {
                diagnostic = "厨具可用性通知执行前无法从当前物理目录重新取得同一厨具，"
                    + $"不会进入原生出锅事务（{bindingFailure}）：{bindingDiagnostic}";
                return false;
            }

            var partnerManager = GetExactSingletonInstance(PartnerManagerTypeName);
            if (partnerManager == null
                || !context.AvailabilityMethod.DeclaringType!.IsInstanceOfType(partnerManager))
            {
                diagnostic = "厨具可用性通知执行前 PartnerManager 已失效。";
                return false;
            }

            context.AvailabilityMethod.Invoke(
                partnerManager,
                new object?[] { -1 });

            if (!TryReacquireAutomationCooker(
                    job,
                    out var cookerBinding,
                    out bindingFailure,
                    out bindingDiagnostic))
            {
                diagnostic = "厨具可用性通知后无法从当前物理目录重新取得同一厨具，"
                    + $"未调用 AfterPlayerExtract（{bindingFailure}）：{bindingDiagnostic}";
                return false;
            }

            if (!context.ExtractionMethod.DeclaringType!.IsInstanceOfType(cookerBinding.Controller))
            {
                diagnostic = "重新取得的厨具类型与 preflight 的 AfterPlayerExtract 声明类型不一致。";
                return false;
            }

            context.ExtractionMethod.Invoke(cookerBinding.Controller, null);
            diagnostic = "厨具可用性通知与出锅回调已正常返回；后续将复核经营代际和同一订单。";
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = $"厨具出锅事务已经开始，但原生回调结果无法确认：{ex.GetBaseException().Message}";
            return false;
        }
    }

    private static bool TryValidateYuumaCookerBeforeFoodCommit(
        AutomationCookingJob job,
        object cookedFood,
        out RuntimeAutomationCookerBinding binding,
        out string diagnostic)
    {
        binding = null!;
        if (!TryReacquireAutomationCooker(
                job,
                out var current,
                out var failureKind,
                out var bindingDiagnostic))
        {
            diagnostic = $"fresh cooker binding failed ({failureKind}): {bindingDiagnostic}";
            return false;
        }

        if (current.State.Phase != 3)
        {
            diagnostic = $"cook phase changed; expected=3; actual={current.State.Phase}; {bindingDiagnostic}";
            return false;
        }

        if (current.State.Result == null
            || !IsSameObject(current.State.Result, cookedFood))
        {
            diagnostic = $"cooked result identity changed; resultEmpty={current.State.Result == null}; {bindingDiagnostic}";
            return false;
        }

        if (current.State.ChosenRecipe == null
            || !TryReadNativeObjectPointer(current.State.ChosenRecipe, out var recipePointer)
            || recipePointer != job.ChosenRecipePointer)
        {
            diagnostic = "chosen recipe identity changed; "
                + $"expected=0x{(long)job.ChosenRecipePointer:X}; {bindingDiagnostic}";
            return false;
        }

        binding = current;
        diagnostic = bindingDiagnostic;
        return true;
    }

    private static bool TryCreateYuumaBookkeepingContext(
        RuntimeOrderMatch runtimeOrder,
        object deliveredItem,
        RuntimeDeliveryItemKind kind,
        out YuumaDeliveryBookkeepingContext context,
        out string diagnostic)
    {
        context = null!;
        diagnostic = "";
        if (runtimeOrder.Order == null)
        {
            diagnostic = "订单对象不可用。";
            return false;
        }

        var statusTracker = GetExactSingletonInstance(StatusTrackerTypeName);
        var partnerManager = GetExactSingletonInstance(PartnerManagerTypeName);
        var consumeMethodName = kind == RuntimeDeliveryItemKind.Food
            ? "AddBussinessFoodConsumes"
            : "AddBussinessBeverageConsumes";
        if (statusTracker == null || partnerManager == null)
        {
            diagnostic = "StatusTracker 或 PartnerManager 单例不可用。";
            return false;
        }

        var consumeMethods = statusTracker
            .GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return string.Equals(method.Name, consumeMethodName, StringComparison.Ordinal)
                    && method.ReturnType == typeof(void)
                    && parameters.Length == 1
                    && parameters[0].ParameterType
                        == typeof(Il2CppSystem.Collections.Generic.IEnumerable<int>);
            })
            .ToArray();
        var statusMethods = partnerManager
            .GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return string.Equals(method.Name, "OnOrderBaseStatusUpdate", StringComparison.Ordinal)
                    && method.ReturnType == typeof(void)
                    && parameters.Length == 3
                    && string.Equals(
                        parameters[0].ParameterType.FullName,
                        YuumaOrderBaseTypeName,
                        StringComparison.Ordinal)
                    && parameters[0].ParameterType.IsInstanceOfType(runtimeOrder.Order)
                    && string.Equals(
                        parameters[1].ParameterType.FullName,
                        YuumaOrderChangeContextTypeName,
                        StringComparison.Ordinal)
                    && parameters[2].ParameterType == typeof(int);
            })
            .ToArray();
        var deskMethods = partnerManager
            .GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return string.Equals(method.Name, "TryAddPlayerOccupiedDeskCode", StringComparison.Ordinal)
                    && method.ReturnType == typeof(void)
                    && parameters.Length == 1
                    && parameters[0].ParameterType == typeof(int);
            })
            .ToArray();
        var deskCodeMethod = FindExactInstanceMethod(
            runtimeOrder.Order.GetType(),
            "get_DeskCode",
            parameterCount: 0,
            returnType: typeof(int));
        if (consumeMethods.Length != 1
            || statusMethods.Length != 1
            || deskMethods.Length != 1
            || deskCodeMethod == null)
        {
            diagnostic = "送达消费统计、订单状态或桌位通知入口与 BepInEx 783 精确形态不一致。";
            return false;
        }

        var statusMethod = statusMethods[0];
        var parameters = statusMethod.GetParameters();
        var itemId = ReadSellableId(deliveredItem);
        if (itemId < 0
            || deskCodeMethod.Invoke(runtimeOrder.Order, null) is not int deskCode)
        {
            diagnostic = "评价前无法缓存送达项目 ID 或桌位编号。";
            return false;
        }

        var consumeMethod = consumeMethods[0];
        var deskMethod = deskMethods[0];
        var ids = BuildIl2CppIntArray(new[] { itemId })
            .Cast<Il2CppSystem.Collections.Generic.IEnumerable<int>>();
        var updateContext = Enum.ToObject(
            parameters[1].ParameterType,
            kind == RuntimeDeliveryItemKind.Food ? 3 : 4);
        var expectedContextName = kind == RuntimeDeliveryItemKind.Food
            ? "FoodDelivered"
            : "BeverageDelivered";
        if (!string.Equals(updateContext.ToString(), expectedContextName, StringComparison.Ordinal))
        {
            diagnostic = $"PartnerManager.OrderChangeContext 缺少精确 {expectedContextName} 枚举值。";
            return false;
        }

        context = new YuumaDeliveryBookkeepingContext(
            kind,
            statusTracker,
            consumeMethod,
            partnerManager,
            statusMethod,
            deskMethod,
            runtimeOrder.Order,
            ids,
            updateContext,
            deskCode);
        return true;
    }

    private static bool TryValidateYuumaDeliveredItemAgainstOriginalOrder(
        CookingCollectionTarget target,
        object item,
        RuntimeDeliveryItemKind kind,
        out string diagnostic)
    {
        diagnostic = "";
        var expectedType = kind == RuntimeDeliveryItemKind.Food ? 0 : 1;
        if (!TryReadSellableIdentity(item, out var actualType, out var actualId) || actualType != expectedType)
        {
            diagnostic = $"血池地狱{(kind == RuntimeDeliveryItemKind.Food ? "料理" : "酒水")}对象类型无法精确确认。";
            return false;
        }

        if (target.Kind == CookingCollectionTargetKind.NormalOrder)
        {
            var expectedId = kind == RuntimeDeliveryItemKind.Food
                ? target.MatchFoodId
                : target.MatchBeverageId;
            if (expectedId < 0 || actualId != expectedId)
            {
                diagnostic = $"血池地狱普通订单原始项目 #{expectedId} 与实际项目 #{actualId} 不一致。";
                return false;
            }

            return true;
        }

        var expectedTagId = kind == RuntimeDeliveryItemKind.Food
            ? target.FoodTagId
            : target.BeverageTagId;
        if (!expectedTagId.HasValue)
        {
            diagnostic = "血池地狱稀客订单缺少原始 Tag ID。";
            return false;
        }

        if (!TryReadExactSellableTagIds(item, out var tagIds, out var tagDiagnostic))
        {
            diagnostic = $"无法读取实际送达项目的原始 Tag：{tagDiagnostic}";
            return false;
        }

        if (!tagIds.Contains(expectedTagId.Value))
        {
            diagnostic = $"实际送达项目不包含原订单要求的 Tag #{expectedTagId.Value}。";
            return false;
        }

        return true;
    }

    private static bool TryReadExactSellableTagIds(
        object item,
        out IReadOnlyList<int> tagIds,
        out string diagnostic)
    {
        tagIds = Array.Empty<int>();
        diagnostic = "";
        var method = FindExactInstanceMethod(
            item.GetType(),
            "get_Tags",
            0,
            typeof(Il2CppStructArray<int>));
        if (method == null)
        {
            diagnostic = "get_Tags() 入口不存在";
            return false;
        }

        var rawTags = method.Invoke(item, null);
        if (rawTags is Il2CppStructArray<int> il2CppTags)
        {
            if (il2CppTags.Length > 256)
            {
                diagnostic = $"Tag 数量 {il2CppTags.Length} 超出上限";
                return false;
            }

            var values = new int[il2CppTags.Length];
            for (var index = 0; index < il2CppTags.Length; index++) values[index] = il2CppTags[index];
            tagIds = values;
            return true;
        }

        diagnostic = $"get_Tags() 返回未验证的容器 {rawTags?.GetType().FullName ?? "null"}";
        return false;
    }

    private static bool TryReadYuumaBeverageCostPolicy(
        out bool isFreeBeverage,
        out int extraCostBeverages,
        out string diagnostic)
    {
        isFreeBeverage = false;
        extraCostBeverages = 0;
        diagnostic = "";
        try
        {
            var eventManager = GetExactSingletonInstance(EventManagerTypeName);
            var freeMethod = eventManager == null
                ? null
                : FindExactInstanceMethod(eventManager.GetType(), "get_IsFreeBevServe", 0, typeof(bool));
            var extraMethod = eventManager == null
                ? null
                : FindExactInstanceMethod(eventManager.GetType(), "get_ExtraCostBevs", 0, typeof(int));
            if (eventManager == null
                || freeMethod?.Invoke(eventManager, null) is not bool freeValue
                || extraMethod?.Invoke(eventManager, null) is not int extraValue
                || extraValue < 1
                || extraValue > 64)
            {
                diagnostic = "EventManager 酒水免费/额外消耗规则形态或值域无效。";
                return false;
            }

            isFreeBeverage = freeValue;
            extraCostBeverages = extraValue;
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = ex.GetBaseException().Message;
            return false;
        }
    }

    private static void ApplyYuumaBeverageCostPolicy(
        YuumaBeverageStorageContext storageContext,
        int beverageId,
        bool isFreeBeverage,
        int extraCostBeverages)
    {
        if (isFreeBeverage)
        {
            InvokeExactRuntimeStorageRange(
                storageContext.BeverageInRangeMethod,
                beverageId,
                1);
            return;
        }

        var additionalCost = extraCostBeverages - 1;
        if (additionalCost > 0)
        {
            InvokeExactRuntimeStorageRange(
                storageContext.BeverageOutRangeMethod,
                beverageId,
                additionalCost);
        }
    }

    private static bool TryCreateYuumaBeverageStorageContext(
        out YuumaBeverageStorageContext context,
        out string diagnostic)
    {
        context = null!;
        diagnostic = "";
        var storageType = FindType(RuntimeStorageTypeName);
        if (storageType == null)
        {
            diagnostic = "RunTimeStorage 类型不可用。";
            return false;
        }

        var beverageOutMethods = storageType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return string.Equals(method.Name, "BeverageOut", StringComparison.Ordinal)
                    && method.ReturnType == typeof(void)
                    && parameters.Length == 2
                    && parameters[0].ParameterType == typeof(int)
                    && parameters[1].ParameterType == typeof(bool);
            })
            .ToArray();
        var beverageInRangeMethods = FindYuumaBeverageRangeMethods(
            storageType,
            "BeverageInRange");
        var beverageOutRangeMethods = FindYuumaBeverageRangeMethods(
            storageType,
            "BeverageOutRange");
        if (beverageOutMethods.Length != 1
            || beverageInRangeMethods.Length != 1
            || beverageOutRangeMethods.Length != 1)
        {
            diagnostic = "RunTimeStorage 酒水单项/批量入口与 BepInEx 783 精确形态不一致。";
            return false;
        }

        context = new YuumaBeverageStorageContext(
            beverageOutMethods[0],
            beverageInRangeMethods[0],
            beverageOutRangeMethods[0]);
        return true;
    }

    private static MethodInfo[] FindYuumaBeverageRangeMethods(Type storageType, string methodName)
    {
        return storageType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return string.Equals(method.Name, methodName, StringComparison.Ordinal)
                    && method.ReturnType == typeof(void)
                    && parameters.Length == 2
                    && parameters[0].ParameterType
                        == typeof(Il2CppSystem.Collections.Generic.IEnumerable<int>)
                    && parameters[1].ParameterType == typeof(bool);
            })
            .ToArray();
    }

    private static void InvokeExactRuntimeStorageRange(
        MethodInfo method,
        int itemId,
        int count)
    {
        var ids = new Il2CppStructArray<int>(count);
        for (var index = 0; index < count; index++) ids[index] = itemId;
        var enumerable = ids.Cast<Il2CppSystem.Collections.Generic.IEnumerable<int>>();
        method.Invoke(null, new object?[] { enumerable, false });
    }

    private static object? GetExactSingletonInstance(string typeName)
    {
        var type = FindType(typeName);
        return type == null ? null : RuntimeReflectionUtility.GetSingletonInstance(type);
    }

    private static MethodInfo? FindExactInstanceMethod(
        Type type,
        string name,
        int parameterCount,
        Type? returnType)
    {
        var candidates = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal)
                && method.GetParameters().Length == parameterCount
                && (returnType == null || method.ReturnType == returnType))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool IsExactYuumaManualEvaluationCallbackType(Type type)
    {
        if (!type.IsGenericType
            || !string.Equals(
                type.GetGenericTypeDefinition().FullName,
                Il2CppActionGenericTypeName,
                StringComparison.Ordinal))
        {
            return false;
        }

        var arguments = type.GetGenericArguments();
        return arguments.Length == 1
            && string.Equals(
                arguments[0].FullName,
                YuumaEvaluationResultTypeName,
                StringComparison.Ordinal);
    }

    private static (bool Remove, string Message, string Code) BlockUncertainYuumaSettlement(
        AutomationCookingJob job,
        string detail,
        IReadOnlyList<string> targetTags,
        IReadOnlyList<string> actualTags)
    {
        var message = $"{job.RecipeName} 的血池地狱最终事务已经开始，但送达、评价或状态通知结果无法完整确认；"
            + $"为避免重复评价，自动化已停止并等待人工 ACK。{detail}";
        RecordAutomationRuntimeEvent(
            OrderPreparationStepCodes.OrderEvaluationCommitUncertain,
            job,
            message,
            job.Target.FoodId,
            targetTags,
            actualTags,
            outcome: "blocked",
            reasonCode: "yuuma-settlement-uncertain",
            terminal: true);
        return (true, message, OrderPreparationStepCodes.OrderEvaluationCommitUncertain);
    }
}
