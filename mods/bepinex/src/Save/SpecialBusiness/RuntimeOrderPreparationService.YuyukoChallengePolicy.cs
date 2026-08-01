using System.Reflection;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private const int YuyukoStoryPhase3ProgressEvaluationMinLevelSum = 5;
    private const string YuyukoNormalExecutionModeRefresh = "refresh";
    private static readonly Dictionary<string, DateTime> RecentYuyukoRuntimeDiagnostics = new(StringComparer.Ordinal);
    private static readonly TimeSpan YuyukoRuntimeDiagnosticThrottle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan YuyukoNativeEvaluationAttemptDiagnosticThrottle = TimeSpan.FromSeconds(30);

    private enum YuyukoPhase3EvaluationContract
    {
        None,
        Story,
        Retake,
    }

    private enum YuyukoRetakePhase3EvaluationRoute
    {
        ManualBoss,
        StandardGroup,
    }

    private static void AppendYuyukoRequestDiagnostic(
        string eventName,
        OrderPreparationRequest request,
        string traceId,
        string orderKind)
    {
        if (!IsYuyukoRequestContext(request)) return;

        SpecialBusinessDiagnostics.AppendYuyukoSnapshot(
            "Yuyuko Challenge Automation Request",
            new[]
            {
                $"event: {eventName}",
                $"orderKind: {orderKind}",
                $"traceId: {traceId}",
                $"orderKey: {request.OrderKey}",
                $"desk: {(request.DeskCode >= 0 ? request.DeskCode + 1 : -1)}",
                $"guestId: {request.GuestId?.ToString() ?? ""}",
                $"guestName: {request.GuestName}",
                $"specialBusinessRole: {request.SpecialBusinessRole}",
                $"foodTag: {request.FoodTag}",
                $"beverageTag: {request.BeverageTag}",
                $"matchFoodId: {request.MatchFoodId}",
                $"matchBeverageId: {request.MatchBeverageId}",
                $"foodId: {request.FoodId}",
                $"recipeId: {request.RecipeId}",
                $"recipeName: {request.RecipeName}",
                $"extraIngredientIds: {SpecialBusinessDiagnostics.FormatIds(request.ExtraIngredientIds)}",
                $"predictedFoodTags: {SpecialBusinessDiagnostics.FormatTags(request.PredictedFoodTags)}",
                $"expectedFoodModifierTags: {SpecialBusinessDiagnostics.FormatTags(request.ExpectedFoodModifierTags)}",
                $"beverageId: {request.BeverageId}",
                $"beverageName: {request.BeverageName}",
                $"executionMode: {NormalizeYuyukoNormalExecutionMode(request.ExecutionMode)}",
                $"executionReason: {request.ExecutionReason}",
                $"autoTakeBeverage: {request.AutoTakeBeverage}",
                $"autoStartCooking: {request.AutoStartCooking}",
                $"autoCollectCooking: {request.AutoCollectCooking}",
                $"autoDeliverFood: {request.AutoDeliverFood}",
                $"autoCompleteOrder: {request.AutoCompleteOrder}",
                $"requiresLiveController: {RequiresLiveYuyukoPhase3BossController(request)}",
                $"challengeType: {RuntimeSpecialBusinessContextService.CurrentChallengeType}",
                $"rawChallengeType: {RuntimeSpecialBusinessContextService.CurrentRawChallengeType}",
                $"phase3EvaluationContract: {ResolveYuyukoPhase3EvaluationContract(request)}",
                $"phase3Active: {RuntimeSpecialBusinessContextService.IsActiveYuyukoPhase("Phase3")}",
                $"yuyukoProgress: {RuntimeSpecialBusinessContextService.DescribeYuyukoProgressForDiagnostics()}",
                $"specialBusinessStatus: {RuntimeSpecialBusinessContextService.Status}",
                $"nativeEvaluationTracker: {YuyukoChallengeEvaluationTracker.Status}",
            });
    }

    private static bool IsYuyukoRequestContext(OrderPreparationRequest request)
    {
        if (IsYuyukoBossRequest(request)) return true;

        var challengeType = RuntimeSpecialBusinessContextService.CurrentChallengeType;
        if (!IsYuyukoChallengeType(challengeType)) return false;

        return request.GuestId is 23 or 40
            || request.GuestName.Contains("幽幽子", StringComparison.Ordinal)
            || request.GuestName.Contains("Yuyuko", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsYuyukoBossRequest(OrderPreparationRequest request)
    {
        return string.Equals(request.SpecialBusinessRole?.Trim(), SpecialBusinessOrderRoles.YuyukoBoss, StringComparison.Ordinal);
    }

    private static bool IsYuyukoBossTarget(CookingCollectionTarget target)
    {
        return string.Equals(target.SpecialBusinessRole?.Trim(), SpecialBusinessOrderRoles.YuyukoBoss, StringComparison.Ordinal);
    }

    private static bool RequiresLiveYuyukoPhase3BossController(OrderPreparationRequest request)
    {
        return IsYuyukoBossRequest(request)
            && RuntimeSpecialBusinessContextService.IsActiveYuyukoPhase("Phase3");
    }

    private static bool IsYuyukoPhase3NormalRefreshRequest(OrderPreparationRequest request)
    {
        return RequiresLiveYuyukoPhase3BossController(request)
            && string.Equals(
                NormalizeYuyukoNormalExecutionMode(request.ExecutionMode),
                YuyukoNormalExecutionModeRefresh,
                StringComparison.Ordinal);
    }

    private static string NormalizeYuyukoNormalExecutionMode(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }

    private static bool IsYuyukoChallengeType(string challengeType)
    {
        return string.Equals(challengeType, SpecialBusinessChallengeTypes.StoryYuyuko, StringComparison.Ordinal)
            || string.Equals(challengeType, SpecialBusinessChallengeTypes.RetakeYuyuko, StringComparison.Ordinal);
    }

    private static YuyukoPhase3EvaluationContract ResolveYuyukoPhase3EvaluationContract(OrderPreparationRequest request)
    {
        if (!RequiresLiveYuyukoPhase3BossController(request)) return YuyukoPhase3EvaluationContract.None;

        var challengeType = RuntimeSpecialBusinessContextService.CurrentChallengeType;
        if (string.Equals(challengeType, SpecialBusinessChallengeTypes.RetakeYuyuko, StringComparison.Ordinal)) return YuyukoPhase3EvaluationContract.Retake;
        if (string.Equals(challengeType, SpecialBusinessChallengeTypes.StoryYuyuko, StringComparison.Ordinal)) return YuyukoPhase3EvaluationContract.Story;
        return YuyukoPhase3EvaluationContract.None;
    }

    private static bool TryEvaluateYuyukoChallengeOrderIfReady(
        OrderPreparationResult result,
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string stepName,
        string orderLabel,
        CookingCollectionTarget safetyTarget,
        bool reacquireLiveOrder = true,
        bool allowControllerMissing = false)
    {
        var evaluation = TryEvaluateYuyukoChallengeRuntimeOrderIfReady(request, runtimeOrder, orderLabel, reacquireLiveOrder, allowControllerMissing);
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

    private static RuntimeOrderEvaluationResult TryEvaluateYuyukoChallengeRuntimeOrderIfReady(
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string orderLabel,
        bool reacquireLiveOrder = true,
        bool allowControllerMissing = false)
    {
        if (!TryCaptureActiveNightBusinessGeneration(out var sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        var evaluationContract = ResolveYuyukoPhase3EvaluationContract(request);
        var requiresNativeEvaluationMatch = evaluationContract != YuyukoPhase3EvaluationContract.None;
        if (requiresNativeEvaluationMatch)
        {
            if (runtimeOrder.Manager == null)
            {
                return new(false, false, false,
                    $"客人管理器不可用，无法检查{orderLabel}是否已满足。诊断：{runtimeOrder.Diagnostic}");
            }

            if (runtimeOrder.Order == null)
            {
                return new(false, false, false,
                    $"当前精确匹配的{orderLabel}对象不可用，无法进入幽幽子评价流程。诊断：{runtimeOrder.Diagnostic}");
            }

            var deliveryOrderFullfilledValue = TryInvokeInstanceValue(runtimeOrder.Order, "get_IsFullfilled")
                ?? ReadMember(runtimeOrder.Order, "IsFullfilled");
            if (deliveryOrderFullfilledValue == null)
            {
                return new(false, false, false,
                    $"无法读取当前精确匹配的{orderLabel}满足状态，已停止幽幽子评价。诊断：{runtimeOrder.Diagnostic}");
            }

            var deliveryOrderFullfilled = ReadBool(deliveryOrderFullfilledValue);
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
            }

            if (!deliveryOrderFullfilled)
            {
                var waiting = new RuntimeOrderEvaluationResult(
                    true,
                    false,
                    true,
                    "订单尚未同时满足料理和酒水，等待下一轮补齐。");
                AppendYuyukoRuntimeDiagnostic(
                    "yuyuko-native-evaluate-after",
                    request,
                    runtimeOrder,
                    "native-evaluate-entry-skipped",
                    waiting.Message);
                return waiting;
            }
        }

        var evaluationOrder = requiresNativeEvaluationMatch
            ? reacquireLiveOrder
                ? FindRuntimeOrder(request, RuntimeOrderLookupPurpose.NativeEvaluation)
                : FindRuntimeNormalOrder(request, RuntimeOrderLookupPurpose.NativeEvaluation)
            : runtimeOrder;
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        var executionMode = NormalizeYuyukoNormalExecutionMode(request.ExecutionMode);
        AppendYuyukoRuntimeDiagnostic(
            "yuyuko-native-evaluate-before",
            request,
            evaluationOrder,
            "call-native-evaluate-entry",
            evaluationContract == YuyukoPhase3EvaluationContract.Story
                ? $"Yuyuko story phase3 evaluation uses EvaulateManualOrder after validating the manual progress callback chain. executionMode={executionMode}; freshMatch={requiresNativeEvaluationMatch}; deliveryMatch={runtimeOrder.Diagnostic}"
                : evaluationContract == YuyukoPhase3EvaluationContract.Retake
                    ? $"Yuyuko retake phase3 evaluation selects one native entry from the exact captured manual binding and the _50/_70 controller callback. executionMode={executionMode}; freshMatch={requiresNativeEvaluationMatch}; deliveryMatch={runtimeOrder.Diagnostic}"
                    : $"Yuyuko challenge order is checking whether the game evaluation path is ready before consuming the order. executionMode={executionMode}");
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        RuntimeOrderEvaluationResult evaluation;
        var normalOrderTargetValid = TryValidateYuyukoPhase3NormalOrderTargetInvariant(
            request,
            evaluationOrder,
            out var normalOrderTargetDiagnostic);
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (!normalOrderTargetValid)
        {
            evaluation = new(
                false,
                false,
                false,
                "幽幽子三阶段普客订单执行目标未满足原订单料理/酒水，已暂停自动提交，避免触发原生差评。"
                + $"诊断：{normalOrderTargetDiagnostic}。请提供 aggregate-mod.log。");
        }
        else
        {
            if (IsYuyukoPhase3NormalRefreshRequest(request))
            {
                evaluation = TryEvaluateYuyukoPhase3RefreshOrderIfReady(
                    request,
                    evaluationOrder,
                    orderLabel,
                    allowControllerMissing,
                    sessionGeneration);
            }
            else
            {
                evaluation = evaluationContract switch
                {
                    YuyukoPhase3EvaluationContract.Story => TryEvaluateStoryYuyukoPhase3OrderIfReady(
                        evaluationOrder,
                        orderLabel,
                        sessionGeneration),
                    YuyukoPhase3EvaluationContract.Retake => TryEvaluateRetakeYuyukoPhase3OrderIfReady(
                        request,
                        evaluationOrder,
                        orderLabel,
                        allowControllerMissing,
                        sessionGeneration),
                    _ => TryEvaluateRuntimeOrderIfReady(
                        evaluationOrder,
                        orderLabel,
                        allowControllerMissing,
                        sessionGeneration),
                };
            }
        }

        var decision = evaluation.Ok
            ? evaluation.Skipped
                ? "native-evaluate-entry-skipped"
                : "native-evaluate-entry-called"
            : evaluationContract != YuyukoPhase3EvaluationContract.None
                ? "native-evaluate-entry-blocked"
                : "native-evaluate-entry-failed";
        if (IsNightBusinessGenerationActive(sessionGeneration))
        {
            AppendYuyukoRuntimeDiagnostic(
                "yuyuko-native-evaluate-after",
                request,
                evaluationOrder,
                decision,
                evaluation.Message);
        }
        return evaluation;
    }

    private static RuntimeOrderEvaluationResult TryEvaluateYuyukoPhase3RefreshOrderIfReady(
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string orderLabel,
        bool allowControllerMissing,
        long sessionGeneration)
    {
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (runtimeOrder.Manager == null)
        {
            return new(false, false, false,
                $"客人管理器不可用，无法调用游戏评价流程。诊断：{runtimeOrder.Diagnostic}");
        }

        if (runtimeOrder.Order == null)
        {
            return new(false, false, false,
                $"未找到与当前原始身份一致的幽幽子订单，无法调用游戏评价流程。诊断：{runtimeOrder.Diagnostic}");
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
                return new(true, false, true, "幽幽子三阶段清理订单已满足，但暂未读取到客人控制器，等待下一轮触发评价。");
            }

            return new(false, false, false, "已匹配幽幽子三阶段清理订单，但未找到对应客人控制器，无法确认原生评价回调。");
        }

        var evaluationReadable = TryReadRuntimeOrderEvaluated(
            runtimeOrder.Controller,
            out var evaluated,
            out var evaluatedDiagnostic);
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (!evaluationReadable)
        {
            return new(false, false, false,
                $"无法严格读取 {orderLabel} 的 HasEvaluated，已停止幽幽子评价：{evaluatedDiagnostic}",
                OrderPreparationStepCodes.OrderEvaluationStateUnreadable);
        }

        if (evaluated)
        {
            return new(true, true, true, $"{orderLabel}已触发过评价，本次不重复调用。");
        }

        var targetValid = TryValidateYuyukoPhase3ServedExactTarget(request, runtimeOrder, out var targetDiagnostic);
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (!targetValid)
        {
            return new(
                false,
                false,
                false,
                "幽幽子三阶段清理订单已送齐，但送达成品与请求目标不一致，已暂停自动提交，避免触发原生差评。"
                + $"诊断：{targetDiagnostic}。请提供 aggregate-mod.log。");
        }

        var evaluationContract = ResolveYuyukoPhase3EvaluationContract(request);
        if (evaluationContract == YuyukoPhase3EvaluationContract.Story)
        {
            var storyEvaluationValid = TryValidateYuyukoStoryPhase3RefreshEvaluation(
                runtimeOrder,
                out var storyDiagnostic);
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
            }

            if (!storyEvaluationValid)
            {
                return new(
                    false,
                    false,
                    false,
                    "剧情版幽幽子三阶段清理订单已送齐，但未确认当前 live 订单具备原生手动评价回调链路，已暂停自动提交。"
                    + $"诊断：{storyDiagnostic}; {targetDiagnostic}。请提供 aggregate-mod.log。");
            }

            var manualEvaluation = TryInvokeRuntimeOrderEvaluationOnce(
                runtimeOrder.Manager,
                runtimeOrder.Controller,
                "EvaulateManualOrder",
                new object?[] { runtimeOrder.Controller, runtimeOrder.ManualEvaluationCallback },
                orderLabel,
                sessionGeneration);
            return manualEvaluation.Ok && manualEvaluation.Completed && !manualEvaluation.Skipped
                ? manualEvaluation with
                {
                    Message = $"已按幽幽子三阶段清理模式调用剧情版手动评价流程完成{orderLabel}，该订单不承诺推进进度。诊断：{targetDiagnostic}。",
                }
                : manualEvaluation;
        }

        if (evaluationContract == YuyukoPhase3EvaluationContract.Retake)
        {
            var retakeEvaluationValid = TryResolveYuyukoRetakePhase3EvaluationRoute(
                runtimeOrder,
                out var evaluationRoute,
                out var manualEvaluationCallback,
                out var retakeDiagnostic);
            if (!IsNightBusinessGenerationActive(sessionGeneration))
            {
                return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
            }

            if (!retakeEvaluationValid)
            {
                return new(
                    false,
                    false,
                    false,
                    "重修版幽幽子三阶段清理订单已送齐，但当前订单的原生评价入口无法严格确认，已暂停自动提交。"
                    + $"诊断：{retakeDiagnostic}; {targetDiagnostic}。请提供 aggregate-mod.log。");
            }

            var evaluation = evaluationRoute == YuyukoRetakePhase3EvaluationRoute.ManualBoss
                ? TryInvokeRuntimeOrderEvaluationOnce(
                    runtimeOrder.Manager,
                    runtimeOrder.Controller,
                    "EvaulateManualOrder",
                    new object?[] { runtimeOrder.Controller, manualEvaluationCallback },
                    orderLabel,
                    sessionGeneration)
                : TryEvaluateRuntimeOrderIfReady(
                    runtimeOrder,
                    orderLabel,
                    allowControllerMissing,
                    sessionGeneration);
            return evaluation.Ok && evaluation.Completed && !evaluation.Skipped
                ? evaluation with
                {
                    Message = $"已按幽幽子三阶段清理模式和逐订单路由 {evaluationRoute} 调用唯一原生评价入口完成{orderLabel}，该订单不承诺推进进度。{evaluation.Message} 诊断：{retakeDiagnostic}; {targetDiagnostic}。",
                }
                : evaluation;
        }

        return TryEvaluateRuntimeOrderIfReady(
            runtimeOrder,
            orderLabel,
            allowControllerMissing,
            sessionGeneration);
    }

    private static RuntimeOrderEvaluationResult TryEvaluateStoryYuyukoPhase3OrderIfReady(
        RuntimeOrderMatch runtimeOrder,
        string orderLabel,
        long sessionGeneration)
    {
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (runtimeOrder.Manager == null)
        {
            return new(false, false, false,
                $"客人管理器不可用，无法调用游戏手动评价流程。诊断：{runtimeOrder.Diagnostic}");
        }

        if (runtimeOrder.Order == null)
        {
            return new(false, false, false,
                $"未找到与当前原始身份一致的幽幽子订单，无法调用游戏手动评价流程。诊断：{runtimeOrder.Diagnostic}");
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
            return new(false, false, false, "已匹配幽幽子三阶段订单，但未找到对应客人控制器，无法确认手动评价回调链路。");
        }

        var evaluationReadable = TryReadRuntimeOrderEvaluated(
            runtimeOrder.Controller,
            out var evaluated,
            out var evaluatedDiagnostic);
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (!evaluationReadable)
        {
            return new(false, false, false,
                $"无法严格读取 {orderLabel} 的 HasEvaluated，已停止幽幽子手动评价：{evaluatedDiagnostic}",
                OrderPreparationStepCodes.OrderEvaluationStateUnreadable);
        }

        if (evaluated)
        {
            return new(true, true, true, $"{orderLabel}已触发过评价，本次不重复调用。");
        }

        var progressEvaluationValid = TryValidateYuyukoStoryPhase3ProgressEvaluation(
            runtimeOrder,
            out var progressDiagnostic);
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (!progressEvaluationValid)
        {
            return new(
                false,
                false,
                false,
                "剧情版幽幽子三阶段订单已送齐，但未确认当前 live 订单具备原生手动评价回调链路，已暂停自动提交，避免订单被消耗但进度不涨。"
                + $"诊断：{progressDiagnostic}。请手动提交一笔能涨进度的订单后提供 aggregate-mod.log。");
        }

        var evaluation = TryInvokeRuntimeOrderEvaluationOnce(
            runtimeOrder.Manager,
            runtimeOrder.Controller,
            "EvaulateManualOrder",
            new object?[] { runtimeOrder.Controller, runtimeOrder.ManualEvaluationCallback },
            orderLabel,
            sessionGeneration);
        return evaluation.Ok && evaluation.Completed && !evaluation.Skipped
            ? evaluation with { Message = $"已确认剧情版幽幽子三阶段手动进度回调并调用游戏手动评价流程完成{orderLabel}。" }
            : evaluation;
    }

    private static RuntimeOrderEvaluationResult TryEvaluateRetakeYuyukoPhase3OrderIfReady(
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string orderLabel,
        bool allowControllerMissing,
        long sessionGeneration)
    {
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (runtimeOrder.Manager == null)
        {
            return new(false, false, false,
                $"客人管理器不可用，无法调用游戏评价流程。诊断：{runtimeOrder.Diagnostic}");
        }

        if (runtimeOrder.Order == null)
        {
            return new(false, false, false,
                $"未找到与当前原始身份一致的幽幽子订单，无法调用游戏评价流程。诊断：{runtimeOrder.Diagnostic}");
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
                return new(true, false, true, "重修版幽幽子订单已满足，但暂未读取到客人控制器，等待下一轮触发评价。");
            }

            return new(false, false, false, "已匹配重修版幽幽子三阶段订单，但未找到对应客人控制器，无法确认原生进度回调。");
        }

        var evaluationReadable = TryReadRuntimeOrderEvaluated(
            runtimeOrder.Controller,
            out var evaluated,
            out var evaluatedDiagnostic);
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (!evaluationReadable)
        {
            return new(false, false, false,
                $"无法严格读取 {orderLabel} 的 HasEvaluated，已停止幽幽子评价：{evaluatedDiagnostic}",
                OrderPreparationStepCodes.OrderEvaluationStateUnreadable);
        }

        if (evaluated)
        {
            return new(true, true, true, $"{orderLabel}已触发过评价，本次不重复调用。");
        }

        var targetValid = TryValidateYuyukoPhase3ServedExactTarget(
            request,
            runtimeOrder,
            out var targetDiagnostic);
        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        if (!targetValid)
        {
            return new(
                false,
                false,
                false,
                "重修版幽幽子三阶段订单已送齐，但送达目标不精确，已暂停自动提交，避免错误消耗订单。"
                + $"诊断：{targetDiagnostic}。请提供 aggregate-mod.log。");
        }

        if (!TryResolveYuyukoRetakePhase3EvaluationRoute(
                runtimeOrder,
                out var evaluationRoute,
                out var manualEvaluationCallback,
                out var routeDiagnostic))
        {
            return new(
                false,
                false,
                false,
                "重修版幽幽子三阶段订单已送齐，但当前订单的原生评价入口无法严格确认，已暂停自动提交。"
                + $"诊断：{routeDiagnostic}; {targetDiagnostic}。请提供 aggregate-mod.log。");
        }

        if (!IsNightBusinessGenerationActive(sessionGeneration))
        {
            return BuildEndedNightBusinessEvaluation(orderLabel, commitMayHaveStarted: false);
        }

        var evaluation = evaluationRoute == YuyukoRetakePhase3EvaluationRoute.ManualBoss
            ? TryInvokeRuntimeOrderEvaluationOnce(
                runtimeOrder.Manager,
                runtimeOrder.Controller,
                "EvaulateManualOrder",
                new object?[] { runtimeOrder.Controller, manualEvaluationCallback },
                orderLabel,
                sessionGeneration)
            : TryEvaluateRuntimeOrderIfReady(
                runtimeOrder,
                orderLabel,
                allowControllerMissing,
                sessionGeneration);
        return evaluation.Ok && evaluation.Completed && !evaluation.Skipped
            ? evaluation with
            {
                Message = $"已按重修版幽幽子三阶段逐订单路由 {evaluationRoute} 调用唯一原生评价入口完成{orderLabel}。{evaluation.Message} 诊断：{routeDiagnostic}",
            }
            : evaluation;
    }

    private static bool TryValidateYuyukoStoryPhase3ProgressEvaluation(RuntimeOrderMatch runtimeOrder, out string diagnostic)
    {
        var manualEvaluationCallback = runtimeOrder.ManualEvaluationCallback;
        if (manualEvaluationCallback == null)
        {
            diagnostic = $"ManualEvaluationCallback 为空；runtime={runtimeOrder.Diagnostic}; tracker={YuyukoChallengeEvaluationTracker.Status}";
            return false;
        }

        var hasManualProgressCallback = YuyukoChallengeEvaluationTracker.TryFindYuyukoPhase3ManualProgressCallback(manualEvaluationCallback, out var manualProgressDetail);
        if (!hasManualProgressCallback)
        {
            diagnostic = $"ManualEvaluationCallback 不是可识别的幽幽子三阶段剧情进度回调；manualCallback={manualProgressDetail}; runtime={runtimeOrder.Diagnostic}; tracker={YuyukoChallengeEvaluationTracker.Status}";
            return false;
        }

        var evaluationCallback = ReadControllerCallback(runtimeOrder.Controller, "OverrideEvaluationCallback");
        if (evaluationCallback == null)
        {
            diagnostic = $"OverrideEvaluationCallback 为空；manualCallback={manualProgressDetail}; tracker={YuyukoChallengeEvaluationTracker.Status}";
            return false;
        }

        if (!TryValidateYuyukoStoryPhase3ServedProgressTarget(runtimeOrder, out var targetDiagnostic))
        {
            diagnostic = $"{targetDiagnostic}; manualCallback={manualProgressDetail}; scoreCallback={YuyukoChallengeEvaluationTracker.DescribeCallback(evaluationCallback)}; tracker={YuyukoChallengeEvaluationTracker.Status}";
            return false;
        }

        if (!YuyukoChallengeEvaluationTracker.TryFindYuyukoStoryPhase3ScoreCallback(evaluationCallback, out var scoreDetail))
        {
            diagnostic = $"OverrideEvaluationCallback 未识别到幽幽子三阶段评分回调；scoreCallback={scoreDetail}; manualCallback={manualProgressDetail}; {targetDiagnostic}; tracker={YuyukoChallengeEvaluationTracker.Status}";
            return false;
        }

        diagnostic = $"manualCallback={manualProgressDetail}; scoreCallback={scoreDetail}; {targetDiagnostic}";
        return true;
    }

    private static bool TryResolveYuyukoRetakePhase3EvaluationRoute(
        RuntimeOrderMatch runtimeOrder,
        out YuyukoRetakePhase3EvaluationRoute route,
        out object? manualEvaluationCallback,
        out string diagnostic)
    {
        route = default;
        manualEvaluationCallback = null;
        if (runtimeOrder.Controller == null || runtimeOrder.Order == null)
        {
            diagnostic = $"route=unresolved, order/controller missing; runtime={runtimeOrder.Diagnostic}";
            return false;
        }

        if (!runtimeOrder.YuyukoManualBindingResolved)
        {
            diagnostic = $"route=unresolved, exact ManualOrderSet binding state was not read from the current capture generation; runtime={runtimeOrder.Diagnostic}";
            return false;
        }

        var currentOrder = TryInvokeInstanceValue(runtimeOrder.Controller, "PeekOrders");
        if (currentOrder == null
            || CompareObjectIdentity(currentOrder, runtimeOrder.Order)
                != RuntimeObjectIdentityComparison.Same)
        {
            diagnostic = $"route=unresolved, controller no longer owns the exact order at PeekOrders; current={SpecialBusinessDiagnostics.DescribeObject(currentOrder)}; matched={SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Order)}";
            return false;
        }

        var resolution = RuntimeOrderTypeResolver.Resolve(runtimeOrder.Order);
        if (!resolution.Resolved || resolution.ReadableOrder == null)
        {
            diagnostic = $"route=unresolved, concrete order type unavailable: {resolution.Reason}";
            return false;
        }

        if (resolution.Kind != RuntimeOrderKind.Normal
            && resolution.Kind != RuntimeOrderKind.Special)
        {
            diagnostic = $"route=unresolved, unsupported concrete order type {resolution.Kind}";
            return false;
        }

        var evaluationCallback = ReadControllerCallback(runtimeOrder.Controller, "OverrideEvaluationCallback");
        if (evaluationCallback == null)
        {
            diagnostic = $"route=unresolved, OverrideEvaluationCallback missing; runtime={runtimeOrder.Diagnostic}; tracker={YuyukoChallengeEvaluationTracker.Status}";
            return false;
        }

        var hasBossProgress = YuyukoChallengeEvaluationTracker.TryFindYuyukoRetakePhase3BossProgressCallback(
            evaluationCallback,
            out var bossProgressDetail);
        var hasGroupProgress = YuyukoChallengeEvaluationTracker.TryFindYuyukoRetakePhase3GroupProgressCallback(
            evaluationCallback,
            out var groupProgressDetail);
        if (hasBossProgress == hasGroupProgress)
        {
            diagnostic = $"route=unresolved, controller progress callback is missing or ambiguous; boss={hasBossProgress} ({bossProgressDetail}); group={hasGroupProgress} ({groupProgressDetail}); runtime={runtimeOrder.Diagnostic}";
            return false;
        }

        if (runtimeOrder.YuyukoManualBindingCaptured)
        {
            manualEvaluationCallback = runtimeOrder.ManualEvaluationCallback;
            if (manualEvaluationCallback == null)
            {
                diagnostic = $"route=unresolved, captured manual binding has no callback; runtime={runtimeOrder.Diagnostic}";
                return false;
            }

            if (!YuyukoChallengeEvaluationTracker.TryFindYuyukoRetakePhase3ManualProgressCallback(
                    manualEvaluationCallback,
                    out var manualProgressDetail))
            {
                diagnostic = $"route=unresolved, ManualOrderSet callback is not the exact b__77/b__78 retake callback; manual={manualProgressDetail}; boss={bossProgressDetail}; group={groupProgressDetail}; runtime={runtimeOrder.Diagnostic}";
                return false;
            }

            if (!hasBossProgress || hasGroupProgress)
            {
                diagnostic = $"route=conflict, manual binding requires the exact boss _50 callback; manual={manualProgressDetail}; boss={bossProgressDetail}; group={groupProgressDetail}; runtime={runtimeOrder.Diagnostic}";
                return false;
            }

            route = YuyukoRetakePhase3EvaluationRoute.ManualBoss;
            diagnostic = $"route={route}, orderType={resolution.KindName}, manual={manualProgressDetail}, boss={bossProgressDetail}";
            return true;
        }

        if (runtimeOrder.ManualEvaluationCallback != null)
        {
            diagnostic = $"route=conflict, binding is absent but a manual callback was propagated; callback={YuyukoChallengeEvaluationTracker.DescribeCallback(runtimeOrder.ManualEvaluationCallback)}; runtime={runtimeOrder.Diagnostic}";
            return false;
        }

        if (hasBossProgress || !hasGroupProgress)
        {
            diagnostic = $"route=unresolved, standard evaluation requires an exact resolved order + group _70 + no boss _50 + no manual binding; orderType={resolution.KindName}; boss={bossProgressDetail}; group={groupProgressDetail}; runtime={runtimeOrder.Diagnostic}";
            return false;
        }

        route = YuyukoRetakePhase3EvaluationRoute.StandardGroup;
        diagnostic = $"route={route}, orderType={resolution.KindName}, manualBinding=absent, group={groupProgressDetail}";
        return true;
    }

    private static bool TryValidateYuyukoStoryPhase3RefreshEvaluation(RuntimeOrderMatch runtimeOrder, out string diagnostic)
    {
        var manualEvaluationCallback = runtimeOrder.ManualEvaluationCallback;
        if (manualEvaluationCallback == null)
        {
            diagnostic = $"ManualEvaluationCallback 为空；runtime={runtimeOrder.Diagnostic}; tracker={YuyukoChallengeEvaluationTracker.Status}";
            return false;
        }

        var hasManualProgressCallback = YuyukoChallengeEvaluationTracker.TryFindYuyukoPhase3ManualProgressCallback(manualEvaluationCallback, out var manualProgressDetail);
        if (!hasManualProgressCallback)
        {
            diagnostic = $"ManualEvaluationCallback 不是可识别的幽幽子三阶段剧情进度回调；manualCallback={manualProgressDetail}; runtime={runtimeOrder.Diagnostic}; tracker={YuyukoChallengeEvaluationTracker.Status}";
            return false;
        }

        var evaluationCallback = ReadControllerCallback(runtimeOrder.Controller, "OverrideEvaluationCallback");
        if (evaluationCallback == null)
        {
            diagnostic = $"OverrideEvaluationCallback 为空；manualCallback={manualProgressDetail}; tracker={YuyukoChallengeEvaluationTracker.Status}";
            return false;
        }

        if (!YuyukoChallengeEvaluationTracker.TryFindYuyukoStoryPhase3ScoreCallback(evaluationCallback, out var scoreDetail))
        {
            diagnostic = $"OverrideEvaluationCallback 未识别到幽幽子三阶段评分回调；scoreCallback={scoreDetail}; manualCallback={manualProgressDetail}; tracker={YuyukoChallengeEvaluationTracker.Status}";
            return false;
        }

        diagnostic = $"manualCallback={manualProgressDetail}; scoreCallback={scoreDetail}";
        return true;
    }

    private static bool TryValidateYuyukoPhase3NormalOrderTargetInvariant(
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        out string diagnostic)
    {
        var invariant = BuildYuyukoPhase3NormalOrderTargetInvariant(request, runtimeOrder);
        diagnostic = invariant.Diagnostic;
        return !invariant.Applies || (invariant.FoodMatched && invariant.BeverageMatched);
    }

    private static (bool Applies, bool FoodMatched, bool BeverageMatched, string Diagnostic) BuildYuyukoPhase3NormalOrderTargetInvariant(
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder)
    {
        if (!RequiresLiveYuyukoPhase3BossController(request))
        {
            return (false, true, true, "not yuyuko phase3 boss request");
        }

        var resolution = RuntimeOrderTypeResolver.Resolve(runtimeOrder.Order);
        if (!resolution.Resolved || resolution.ReadableOrder == null)
        {
            return (true, false, false, $"runtime order type unavailable: {resolution.Reason}");
        }

        if (resolution.Kind == RuntimeOrderKind.Special)
        {
            return (false, true, true, "special order target uses tag evaluation");
        }

        var readableOrder = resolution.ReadableOrder;
        var servedFood = ReadOrderServedFood(readableOrder);
        var servedBeverage = ReadOrderServedBeverage(readableOrder);
        var servedFoodId = servedFood == null ? -1 : ReadSellableId(servedFood);
        var servedBeverageId = servedBeverage == null ? -1 : ReadSellableId(servedBeverage);
        var foodMatched = request.MatchFoodId >= 0
            && request.FoodId >= 0
            && request.MatchFoodId == request.FoodId;
        var beverageMatched = request.MatchBeverageId >= 0
            && request.BeverageId >= 0
            && request.MatchBeverageId == request.BeverageId;
        var diagnostic =
            $"normalOrderTargetInvariant original={request.MatchFoodId}/{request.MatchBeverageId}; "
            + $"target={request.FoodId}/{request.BeverageId}; "
            + $"served={servedFoodId}/{servedBeverageId}; "
            + $"originalFoodMatched={foodMatched}; "
            + $"originalBeverageMatched={beverageMatched}; "
            + $"servFood={DescribeSellableForYuyukoDiagnostics(servedFood)}; "
            + $"servBeverage={DescribeSellableForYuyukoDiagnostics(servedBeverage)}";
        return (true, foodMatched, beverageMatched, diagnostic);
    }

    private static bool TryValidateYuyukoPhase3ServedExactTarget(
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        out string diagnostic)
    {
        var resolution = RuntimeOrderTypeResolver.Resolve(runtimeOrder.Order);
        if (!resolution.Resolved || resolution.ReadableOrder == null)
        {
            diagnostic = $"served target order type unavailable: {resolution.Reason}";
            return false;
        }

        var orderKind = resolution.Kind;
        var readableOrder = resolution.ReadableOrder;
        var servedFood = ReadOrderServedFood(readableOrder);
        var servedBeverage = ReadOrderServedBeverage(readableOrder);
        if (servedFood == null || servedBeverage == null)
        {
            diagnostic = $"served target missing; food={SpecialBusinessDiagnostics.DescribeObject(servedFood)}; beverage={SpecialBusinessDiagnostics.DescribeObject(servedBeverage)}";
            return false;
        }

        var servedFoodId = ReadSellableId(servedFood);
        var servedBeverageId = ReadSellableId(servedBeverage);
        var foodMatched = request.FoodId >= 0 && servedFoodId == request.FoodId;
        var beverageMatched = request.BeverageId >= 0 && servedBeverageId == request.BeverageId;
        var retakeContractMatched = true;
        var retakeContractDiagnostic = "not required";
        if (ResolveYuyukoPhase3EvaluationContract(request) == YuyukoPhase3EvaluationContract.Retake)
        {
            retakeContractMatched = orderKind == RuntimeOrderKind.Special
                ? TryValidateYuyukoRetakeSpecialOrderServedContract(
                    request,
                    servedFood,
                    servedBeverage,
                    out retakeContractDiagnostic)
                : TryValidateYuyukoRetakeNormalOrderServedContract(
                    request,
                    servedFood,
                    out retakeContractDiagnostic);
        }

        var servedFoodLevel = ReadSellableLevel(servedFood);
        var servedBeverageLevel = ReadSellableLevel(servedBeverage);
        var levelSum = servedFoodLevel >= 0 && servedBeverageLevel >= 0
            ? (servedFoodLevel + servedBeverageLevel).ToString()
            : "";
        diagnostic =
            $"served target exact requested={request.FoodId}/{request.BeverageId}; "
            + $"served={servedFoodId}/{servedBeverageId}; "
            + $"foodMatched={foodMatched}; "
            + $"beverageMatched={beverageMatched}; "
            + $"retakeOrderType={(orderKind == RuntimeOrderKind.Special ? "SpecialOrder" : "NormalOrder")}; "
            + $"retakeContractMatched={retakeContractMatched}; "
            + $"retakeContract={retakeContractDiagnostic}; "
            + $"levelSum={levelSum}; "
            + $"food={DescribeSellableForYuyukoDiagnostics(servedFood)}; "
            + $"beverage={DescribeSellableForYuyukoDiagnostics(servedBeverage)}";
        return foodMatched && beverageMatched && retakeContractMatched;
    }

    private static bool TryValidateYuyukoRetakeSpecialOrderServedContract(
        OrderPreparationRequest request,
        object servedFood,
        object servedBeverage,
        out string diagnostic)
    {
        if (!TryValidateYuyukoRetakeServedExtraIngredients(
                request,
                servedFood,
                out var actualExtraIngredientIds,
                out var ingredientDiagnostic))
        {
            diagnostic = ingredientDiagnostic;
            return false;
        }

        if (!request.FoodTagId.HasValue)
        {
            diagnostic = "special order request FoodTagId is missing";
            return false;
        }

        if (!request.BeverageTagId.HasValue)
        {
            diagnostic = "special order request BeverageTagId is missing";
            return false;
        }

        if (!TryReadYuyukoSellableTagIds(
                servedFood,
                "food",
                out var actualFoodTagIds,
                out var foodTagDiagnostic))
        {
            diagnostic =
                $"extraIngredients={SpecialBusinessDiagnostics.FormatIds(actualExtraIngredientIds)}; "
                + foodTagDiagnostic;
            return false;
        }

        if (!TryReadYuyukoSellableTagIds(
                servedBeverage,
                "beverage",
                out var actualBeverageTagIds,
                out var beverageTagDiagnostic))
        {
            diagnostic =
                $"extraIngredients={SpecialBusinessDiagnostics.FormatIds(actualExtraIngredientIds)}; "
                + beverageTagDiagnostic;
            return false;
        }

        var foodTagMatched = actualFoodTagIds.Contains(request.FoodTagId.Value);
        var beverageTagMatched = actualBeverageTagIds.Contains(request.BeverageTagId.Value);
        diagnostic =
            $"extraIngredients={SpecialBusinessDiagnostics.FormatIds(actualExtraIngredientIds)}; "
            + $"requestedTags={request.FoodTagId.Value}/{request.BeverageTagId.Value}; "
            + $"actualFoodTags={SpecialBusinessDiagnostics.FormatIds(actualFoodTagIds)}; "
            + $"actualBeverageTags={SpecialBusinessDiagnostics.FormatIds(actualBeverageTagIds)}; "
            + $"foodTagMatched={foodTagMatched}; "
            + $"beverageTagMatched={beverageTagMatched}";
        return foodTagMatched && beverageTagMatched;
    }

    private static bool TryValidateYuyukoRetakeNormalOrderServedContract(
        OrderPreparationRequest request,
        object servedFood,
        out string diagnostic)
    {
        if (!TryValidateYuyukoRetakeServedExtraIngredients(
                request,
                servedFood,
                out var actualExtraIngredientIds,
                out var ingredientDiagnostic))
        {
            diagnostic = ingredientDiagnostic;
            return false;
        }

        var expectedModifierTags = request.ExpectedFoodModifierTags
            .Select(tag => FoodTags.NormalizeName(tag) ?? tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();
        if (expectedModifierTags.Length == 0)
        {
            diagnostic = "normal order ExpectedFoodModifierTags is empty";
            return false;
        }

        if (!TryReadYuyukoNormalOrderFoodModifierTags(servedFood, out var actualModifierTags, out var tagDiagnostic))
        {
            diagnostic =
                $"extra ingredients matched={SpecialBusinessDiagnostics.FormatIds(actualExtraIngredientIds)}; "
                + $"actual modifier tags unreadable: {tagDiagnostic}";
            return false;
        }

        if (!actualModifierTags.SequenceEqual(expectedModifierTags))
        {
            diagnostic =
                $"modifier tags mismatch; expected={SpecialBusinessDiagnostics.FormatTags(expectedModifierTags)}; "
                + $"actual={SpecialBusinessDiagnostics.FormatTags(actualModifierTags)}; "
                + $"extraIngredients={SpecialBusinessDiagnostics.FormatIds(actualExtraIngredientIds)}";
            return false;
        }

        diagnostic =
            $"extraIngredients={SpecialBusinessDiagnostics.FormatIds(actualExtraIngredientIds)}; "
            + $"modifierTags={SpecialBusinessDiagnostics.FormatTags(actualModifierTags)}";
        return true;
    }

    private static bool TryValidateYuyukoRetakeServedExtraIngredients(
        OrderPreparationRequest request,
        object servedFood,
        out IReadOnlyList<int> actualExtraIngredientIds,
        out string diagnostic)
    {
        actualExtraIngredientIds = Array.Empty<int>();
        if (!TryReadExactMemberValue(
                servedFood,
                out var rawModifier,
                out var modifierReadDiagnostic,
                "Modifier")
            || rawModifier == null)
        {
            diagnostic = $"actual extra ingredients unreadable; member={modifierReadDiagnostic}";
            return false;
        }

        if (!RuntimeConcreteCollectionReader.TryReadIntArray(
                rawModifier,
                out var rawActualExtraIngredientIds,
                out var modifierArrayFailure))
        {
            diagnostic = $"actual extra ingredients array unreadable: {modifierArrayFailure}";
            return false;
        }

        if (rawActualExtraIngredientIds.Any(id => id < 0)
            || rawActualExtraIngredientIds.Distinct().Count() != rawActualExtraIngredientIds.Count)
        {
            diagnostic =
                $"actual extra ingredients contain invalid or duplicate ids: "
                + $"{SpecialBusinessDiagnostics.FormatIds(rawActualExtraIngredientIds)}";
            return false;
        }

        if (request.ExtraIngredientIds.Any(id => id < 0)
            || request.ExtraIngredientIds.Distinct().Count() != request.ExtraIngredientIds.Count)
        {
            diagnostic =
                $"requested extra ingredients contain invalid or duplicate ids: "
                + $"{SpecialBusinessDiagnostics.FormatIds(request.ExtraIngredientIds)}";
            return false;
        }

        var expectedExtraIngredientIds = request.ExtraIngredientIds
            .OrderBy(id => id)
            .ToArray();
        var normalizedActualExtraIngredientIds = rawActualExtraIngredientIds
            .OrderBy(id => id)
            .ToArray();
        if (!normalizedActualExtraIngredientIds.SequenceEqual(expectedExtraIngredientIds))
        {
            diagnostic =
                $"extra ingredients mismatch; expected={SpecialBusinessDiagnostics.FormatIds(expectedExtraIngredientIds)}; "
                + $"actual={SpecialBusinessDiagnostics.FormatIds(normalizedActualExtraIngredientIds)}";
            return false;
        }

        actualExtraIngredientIds = normalizedActualExtraIngredientIds;
        diagnostic = $"extraIngredients={SpecialBusinessDiagnostics.FormatIds(actualExtraIngredientIds)}";
        return true;
    }

    private static bool TryReadYuyukoSellableTagIds(
        object sellable,
        string sellableLabel,
        out IReadOnlyList<int> tagIds,
        out string diagnostic)
    {
        tagIds = Array.Empty<int>();
        if (!TryReadExactMemberValue(sellable, out var rawTags, out var readDiagnostic, "Tags")
            || rawTags == null)
        {
            diagnostic = $"{sellableLabel} Tags unreadable; member={readDiagnostic}";
            return false;
        }

        if (!RuntimeConcreteCollectionReader.TryReadIntArray(
                rawTags,
                out var actualTagIds,
                out var arrayFailure))
        {
            diagnostic = $"{sellableLabel} Tags array unreadable: {arrayFailure}";
            return false;
        }

        tagIds = actualTagIds;
        diagnostic = $"{sellableLabel}TagIds={SpecialBusinessDiagnostics.FormatIds(tagIds)}";
        return true;
    }

    private static bool TryReadYuyukoNormalOrderFoodModifierTags(
        object servedFood,
        out IReadOnlyList<string> modifierTags,
        out string diagnostic)
    {
        modifierTags = Array.Empty<string>();
        diagnostic = "";
        if (!TryReadExactMemberValue(servedFood, out var rawFinalTags, out var finalReadDiagnostic, "Tags")
            || rawFinalTags == null)
        {
            diagnostic = $"Tags unreadable; member={finalReadDiagnostic}";
            return false;
        }

        if (!RuntimeConcreteCollectionReader.TryReadIntArray(
                rawFinalTags,
                out var finalTagIds,
                out var finalArrayFailure))
        {
            diagnostic = $"Tags array unreadable: {finalArrayFailure}";
            return false;
        }

        if (!TryReadExactMemberValue(servedFood, out var rawBaseTags, out var baseReadDiagnostic, "RawTags")
            || rawBaseTags == null)
        {
            diagnostic = $"RawTags unreadable; member={baseReadDiagnostic}";
            return false;
        }

        if (!RuntimeConcreteCollectionReader.TryReadIntArray(
                rawBaseTags,
                out var baseTagIds,
                out var baseArrayFailure))
        {
            diagnostic = $"RawTags array unreadable: {baseArrayFailure}";
            return false;
        }

        var modifierTagIds = finalTagIds
            .Except(baseTagIds)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var normalizedTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tagId in modifierTagIds)
        {
            if (!TryReadFoodTagName(tagId, out var tagName))
            {
                diagnostic = $"GetFoodTag({tagId}) returned no readable name";
                return false;
            }

            var normalized = FoodTags.NormalizeName(tagName) ?? tagName.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                diagnostic = $"GetFoodTag({tagId}) normalized to an empty name";
                return false;
            }

            normalizedTags.Add(normalized);
        }

        modifierTags = normalizedTags
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();
        diagnostic =
            $"finalTagIds={SpecialBusinessDiagnostics.FormatIds(finalTagIds)}; "
            + $"rawTagIds={SpecialBusinessDiagnostics.FormatIds(baseTagIds)}; "
            + $"modifierTagIds={SpecialBusinessDiagnostics.FormatIds(modifierTagIds)}";
        return true;
    }

    private static bool TryValidateYuyukoStoryPhase3ServedProgressTarget(RuntimeOrderMatch runtimeOrder, out string diagnostic)
    {
        var servedFood = runtimeOrder.Order == null ? null : ReadOrderServedFood(runtimeOrder.Order);
        var servedBeverage = runtimeOrder.Order == null ? null : ReadOrderServedBeverage(runtimeOrder.Order);
        if (servedFood == null || servedBeverage == null)
        {
            diagnostic = $"served target missing; food={SpecialBusinessDiagnostics.DescribeObject(servedFood)}; beverage={SpecialBusinessDiagnostics.DescribeObject(servedBeverage)}";
            return false;
        }

        var servedFoodLevel = ReadSellableLevel(servedFood);
        var servedBeverageLevel = ReadSellableLevel(servedBeverage);
        if (servedFoodLevel < 0 || servedBeverageLevel < 0)
        {
            diagnostic = $"served target level unreadable; foodLevel={servedFoodLevel}; beverageLevel={servedBeverageLevel}; food={DescribeSellableForYuyukoDiagnostics(servedFood)}; beverage={DescribeSellableForYuyukoDiagnostics(servedBeverage)}";
            return false;
        }

        var levelSum = servedFoodLevel + servedBeverageLevel;
        if (levelSum < YuyukoStoryPhase3ProgressEvaluationMinLevelSum)
        {
            diagnostic = $"served target level sum below story progress threshold; levelSum={levelSum}; threshold={YuyukoStoryPhase3ProgressEvaluationMinLevelSum}; food={DescribeSellableForYuyukoDiagnostics(servedFood)}; beverage={DescribeSellableForYuyukoDiagnostics(servedBeverage)}";
            return false;
        }

        diagnostic = $"story served target ready; levelSum={levelSum}; threshold={YuyukoStoryPhase3ProgressEvaluationMinLevelSum}; food={DescribeSellableForYuyukoDiagnostics(servedFood)}; beverage={DescribeSellableForYuyukoDiagnostics(servedBeverage)}";
        return true;
    }

    private static void AppendYuyukoRuntimeDiagnostic(
        string eventName,
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string decision,
        string detail = "")
    {
        if (!AggregateModLogService.Enabled) return;

        try
        {
            AppendYuyukoRuntimeDiagnosticCore(eventName, request, runtimeOrder, decision, detail);
        }
        catch
        {
            // Diagnostics must never interrupt native order evaluation.
        }
    }

    private static void AppendYuyukoRuntimeDiagnosticCore(
        string eventName,
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string decision,
        string detail)
    {
        if (!IsYuyukoRequestContext(request)) return;
        if (ShouldSkipRoutineYuyukoRuntimeDiagnostic(eventName, runtimeOrder, decision)) return;
        if (ShouldThrottleYuyukoRuntimeDiagnostic(eventName, request, runtimeOrder, decision, detail)) return;
        var onceKey = BuildYuyukoRuntimeDiagnosticOnceKey(
            eventName,
            request,
            runtimeOrder,
            decision,
            detail);
        if (!ShouldWriteFullYuyukoRuntimeDiagnostic(eventName, decision))
        {
            AppendYuyukoRuntimeSummaryDiagnostic(eventName, request, runtimeOrder, decision, detail);
            return;
        }

        var servedFood = runtimeOrder.Order == null ? null : ReadOrderServedFood(runtimeOrder.Order);
        var servedBeverage = runtimeOrder.Order == null ? null : ReadOrderServedBeverage(runtimeOrder.Order);
        var servedFoodLevel = ReadSellableLevel(servedFood);
        var servedBeverageLevel = ReadSellableLevel(servedBeverage);
        var normalOrderTargetInvariant = BuildYuyukoPhase3NormalOrderTargetInvariant(request, runtimeOrder);
        var levelSum = servedFoodLevel >= 0 && servedBeverageLevel >= 0
            ? (servedFoodLevel + servedBeverageLevel).ToString()
            : "";
        var orderGenerationCallback = ReadControllerCallback(runtimeOrder.Controller, "OverrideOrderGenerationCallback");
        var evaluationCallback = ReadControllerCallback(runtimeOrder.Controller, "OverrideEvaluationCallback");
        var onEvalFinishCallback = ReadControllerCallback(runtimeOrder.Controller, "OnEvalFinishCallback");
        var onExtraFinishEvaluationCallback = ReadControllerCallback(runtimeOrder.Controller, "OnExtraFinishEvaluationCallback");
        var onFinishEatingCallbackWithParam = ReadControllerCallback(runtimeOrder.Controller, "OnFinishEatingCallbackWithParam");
        var onFinishOrderCallback = ReadControllerCallback(runtimeOrder.Controller, "OnFinishOrderCallback");
        var manualEvaluationCallback = runtimeOrder.ManualEvaluationCallback;
        var evaluationContract = ResolveYuyukoPhase3EvaluationContract(request);
        var hasYuyukoScoreCallback = YuyukoChallengeEvaluationTracker.TryFindYuyukoStoryPhase3ScoreCallback(evaluationCallback, out var yuyukoScoreCallbackDetail);
        var hasYuyukoRetakeBossProgressCallback = YuyukoChallengeEvaluationTracker.TryFindYuyukoRetakePhase3BossProgressCallback(evaluationCallback, out var yuyukoRetakeBossProgressCallbackDetail);
        var hasYuyukoRetakeGroupProgressCallback = YuyukoChallengeEvaluationTracker.TryFindYuyukoRetakePhase3GroupProgressCallback(evaluationCallback, out var yuyukoRetakeGroupProgressCallbackDetail);
        var hasYuyukoStoryManualProgressCallback = YuyukoChallengeEvaluationTracker.TryFindYuyukoPhase3ManualProgressCallback(manualEvaluationCallback, out var yuyukoStoryManualProgressCallbackDetail);
        var hasYuyukoRetakeManualProgressCallback = YuyukoChallengeEvaluationTracker.TryFindYuyukoRetakePhase3ManualProgressCallback(manualEvaluationCallback, out var yuyukoRetakeManualProgressCallbackDetail);

        SpecialBusinessDiagnostics.AppendYuyukoSnapshot(
            "Yuyuko Challenge Runtime Diagnostic",
            new[]
            {
                $"event: {eventName}",
                $"decision: {decision}",
                $"detail: {detail}",
                $"challengeType: {RuntimeSpecialBusinessContextService.CurrentChallengeType}",
                $"rawChallengeType: {RuntimeSpecialBusinessContextService.CurrentRawChallengeType}",
                $"phase3EvaluationContract: {evaluationContract}",
                $"phase3Active: {RuntimeSpecialBusinessContextService.IsActiveYuyukoPhase("Phase3")}",
                $"desk: {(request.DeskCode >= 0 ? request.DeskCode + 1 : -1)}",
                $"orderKey: {request.OrderKey}",
                $"traceId: {request.TraceId}",
                $"guestId: {request.GuestId?.ToString() ?? ""}",
                $"guestName: {request.GuestName}",
                $"specialBusinessRole: {request.SpecialBusinessRole}",
                $"foodTag: {request.FoodTag}",
                $"beverageTag: {request.BeverageTag}",
                $"matchFoodId: {request.MatchFoodId}",
                $"matchBeverageId: {request.MatchBeverageId}",
                $"targetFood: {SpecialBusinessDiagnostics.FormatIdName(request.FoodId, request.RecipeName)}",
                $"targetBeverage: {SpecialBusinessDiagnostics.FormatIdName(request.BeverageId, request.BeverageName)}",
                $"executionMode: {NormalizeYuyukoNormalExecutionMode(request.ExecutionMode)}",
                $"normalOrderTargetApplies: {normalOrderTargetInvariant.Applies}",
                $"originalFoodMatched: {normalOrderTargetInvariant.FoodMatched}",
                $"originalBeverageMatched: {normalOrderTargetInvariant.BeverageMatched}",
                $"normalOrderTargetInvariant: {normalOrderTargetInvariant.Diagnostic}",
                $"extraIngredientIds: {SpecialBusinessDiagnostics.FormatIds(request.ExtraIngredientIds)}",
                $"predictedFoodTags: {SpecialBusinessDiagnostics.FormatTags(request.PredictedFoodTags)}",
                $"expectedFoodModifierTags: {SpecialBusinessDiagnostics.FormatTags(request.ExpectedFoodModifierTags)}",
                $"executionReason: {request.ExecutionReason}",
                $"controller: {SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Controller)}",
                $"order: {SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Order)}",
                $"runtimeDiagnostic: {runtimeOrder.Diagnostic}",
                $"yuyukoManualBindingResolved: {runtimeOrder.YuyukoManualBindingResolved}",
                $"yuyukoManualBindingCaptured: {runtimeOrder.YuyukoManualBindingCaptured}",
                $"manualEvaluationCallback: {SpecialBusinessDiagnostics.DescribeObject(manualEvaluationCallback)}",
                $"manualEvaluationCallbackDetail: {DescribeCallbackForYuyukoDiagnostics(manualEvaluationCallback)}",
                $"hasYuyukoStoryManualProgressCallback: {hasYuyukoStoryManualProgressCallback}",
                $"yuyukoStoryManualProgressCallbackDetail: {yuyukoStoryManualProgressCallbackDetail}",
                $"hasYuyukoRetakeManualProgressCallback: {hasYuyukoRetakeManualProgressCallback}",
                $"yuyukoRetakeManualProgressCallbackDetail: {yuyukoRetakeManualProgressCallbackDetail}",
                $"orderGenerationCallback: {SpecialBusinessDiagnostics.DescribeObject(orderGenerationCallback)}",
                $"orderGenerationCallbackDetail: {DescribeCallbackForYuyukoDiagnostics(orderGenerationCallback)}",
                $"evaluationCallback: {SpecialBusinessDiagnostics.DescribeObject(evaluationCallback)}",
                $"evaluationCallbackDetail: {DescribeCallbackForYuyukoDiagnostics(evaluationCallback)}",
                $"hasYuyukoScoreCallback: {hasYuyukoScoreCallback}",
                $"yuyukoScoreCallbackDetail: {yuyukoScoreCallbackDetail}",
                $"hasYuyukoRetakeBossProgressCallback: {hasYuyukoRetakeBossProgressCallback}",
                $"yuyukoRetakeBossProgressCallbackDetail: {yuyukoRetakeBossProgressCallbackDetail}",
                $"hasYuyukoRetakeGroupProgressCallback: {hasYuyukoRetakeGroupProgressCallback}",
                $"yuyukoRetakeGroupProgressCallbackDetail: {yuyukoRetakeGroupProgressCallbackDetail}",
                $"onEvalFinishCallback: {SpecialBusinessDiagnostics.DescribeObject(onEvalFinishCallback)}",
                $"onEvalFinishCallbackDetail: {DescribeCallbackForYuyukoDiagnostics(onEvalFinishCallback)}",
                $"onExtraFinishEvaluationCallback: {SpecialBusinessDiagnostics.DescribeObject(onExtraFinishEvaluationCallback)}",
                $"onExtraFinishEvaluationCallbackDetail: {DescribeCallbackForYuyukoDiagnostics(onExtraFinishEvaluationCallback)}",
                $"onFinishEatingCallbackWithParam: {SpecialBusinessDiagnostics.DescribeObject(onFinishEatingCallbackWithParam)}",
                $"onFinishEatingCallbackWithParamDetail: {DescribeCallbackForYuyukoDiagnostics(onFinishEatingCallbackWithParam)}",
                $"onFinishOrderCallback: {SpecialBusinessDiagnostics.DescribeObject(onFinishOrderCallback)}",
                $"onFinishOrderCallbackDetail: {DescribeCallbackForYuyukoDiagnostics(onFinishOrderCallback)}",
                $"controllerDeskCode: {ReadRuntimeInt(runtimeOrder.Controller, "DeskCode")}",
                $"isControlled: {ReadRuntimeText(runtimeOrder.Controller, "IsControlled")}",
                $"isHerself: {ReadRuntimeText(runtimeOrder.Controller, "IsHerself")}",
                $"remainOrderCount: {ReadRuntimeText(runtimeOrder.Controller, "RemainOrderCount")}",
                $"freeOrderCount: {ReadRuntimeText(runtimeOrder.Controller, "FreeOrderCount")}",
                $"currentPatient: {ReadRuntimeText(runtimeOrder.Controller, "CurrentPatient")}",
                $"maxPatient: {ReadRuntimeText(runtimeOrder.Controller, "MaxPatient")}",
                $"hasEvaluated: {ReadRuntimeText(runtimeOrder.Controller, "HasEvaluated")}",
                $"orderFullfilled: {ReadRuntimeText(runtimeOrder.Order, "IsFullfilled")}",
                $"orderGuest: {DescribeRuntimeGuest(ReadOrderSpecialGuest(runtimeOrder.Order))}",
                $"controllerGuest: {DescribeRuntimeGuest(ReadControllerSpecialGuest(runtimeOrder.Controller))}",
                $"servFood: {DescribeSellableForYuyukoDiagnostics(servedFood)}",
                $"servBeverage: {DescribeSellableForYuyukoDiagnostics(servedBeverage)}",
                $"servedLevelSum: {levelSum}",
                $"servedFoodInAir: {SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Order == null ? null : ReadMember(runtimeOrder.Order, "ServedFoodInAir"))}",
                $"servedBeverageInAir: {SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Order == null ? null : ReadMember(runtimeOrder.Order, "ServedBeverageInAir"))}",
                $"yuyukoProgress: {RuntimeSpecialBusinessContextService.DescribeYuyukoProgressForDiagnostics()}",
                $"specialBusinessStatus: {RuntimeSpecialBusinessContextService.Status}",
                $"nativeEvaluationTracker: {YuyukoChallengeEvaluationTracker.Status}",
            },
            onceKey);
    }

    private static bool ShouldSkipRoutineYuyukoRuntimeDiagnostic(
        string eventName,
        RuntimeOrderMatch runtimeOrder,
        string decision)
    {
        if (string.Equals(decision, "native-evaluate-entry-skipped", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(eventName, "yuyuko-native-evaluate-before", StringComparison.Ordinal)
            && !IsRuntimeOrderFulfilledForYuyukoDiagnostic(runtimeOrder))
        {
            return true;
        }

        if (string.Equals(decision, "reject", StringComparison.Ordinal)
            && (string.Equals(eventName, "yuyuko-live-evaluation-candidate", StringComparison.Ordinal)
                || string.Equals(eventName, "rare-live-candidate-rejected", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private static bool ShouldWriteFullYuyukoRuntimeDiagnostic(string eventName, string decision)
    {
        return string.Equals(eventName, "yuyuko-native-evaluate-before", StringComparison.Ordinal)
            || string.Equals(eventName, "yuyuko-normal-target-invariant", StringComparison.Ordinal)
            || string.Equals(decision, "native-evaluate-entry-called", StringComparison.Ordinal)
            || string.Equals(decision, "blocked-normal-target", StringComparison.Ordinal)
            || string.Equals(decision, "native-evaluate-entry-blocked", StringComparison.Ordinal)
            || string.Equals(decision, "native-evaluate-entry-failed", StringComparison.Ordinal);
    }

    private static bool IsRuntimeOrderFulfilledForYuyukoDiagnostic(RuntimeOrderMatch runtimeOrder)
    {
        if (runtimeOrder.Order == null) return false;

        try
        {
            return ReadBool(InvokeInstance(runtimeOrder.Order, "get_IsFullfilled", Array.Empty<object?>()));
        }
        catch
        {
            return false;
        }
    }

    private static void AppendYuyukoRuntimeSummaryDiagnostic(
        string eventName,
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string decision,
        string detail)
    {
        SpecialBusinessDiagnostics.AppendYuyukoSnapshot(
            "Yuyuko Challenge Runtime Summary",
            new[]
            {
                $"event: {eventName}",
                $"decision: {decision}",
                $"detail: {RuntimeReflectionUtility.Trim(detail, 240)}",
                $"challengeType: {RuntimeSpecialBusinessContextService.CurrentChallengeType}",
                $"rawChallengeType: {RuntimeSpecialBusinessContextService.CurrentRawChallengeType}",
                $"phase3EvaluationContract: {ResolveYuyukoPhase3EvaluationContract(request)}",
                $"phase3Active: {RuntimeSpecialBusinessContextService.IsActiveYuyukoPhase("Phase3")}",
                $"desk: {(request.DeskCode >= 0 ? request.DeskCode + 1 : -1)}",
                $"traceId: {request.TraceId}",
                $"guestId: {request.GuestId?.ToString() ?? ""}",
                $"guestName: {request.GuestName}",
                $"specialBusinessRole: {request.SpecialBusinessRole}",
                $"executionMode: {NormalizeYuyukoNormalExecutionMode(request.ExecutionMode)}",
                $"targetFood: {SpecialBusinessDiagnostics.FormatIdName(request.FoodId, request.RecipeName)}",
                $"targetBeverage: {SpecialBusinessDiagnostics.FormatIdName(request.BeverageId, request.BeverageName)}",
                $"runtimeDiagnostic: {RuntimeReflectionUtility.Trim(runtimeOrder.Diagnostic, 240)}",
                $"yuyukoManualBindingResolved: {runtimeOrder.YuyukoManualBindingResolved}",
                $"yuyukoManualBindingCaptured: {runtimeOrder.YuyukoManualBindingCaptured}",
                $"manualEvaluationCallback: {SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.ManualEvaluationCallback)}",
                $"controller: {SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Controller)}",
                $"order: {SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Order)}",
                $"yuyukoProgress: {RuntimeSpecialBusinessContextService.DescribeYuyukoProgressForDiagnostics()}",
                $"nativeEvaluationTracker: {YuyukoChallengeEvaluationTracker.Status}",
            });
    }

    private static bool ShouldThrottleYuyukoRuntimeDiagnostic(
        string eventName,
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string decision,
        string detail)
    {
        var now = DateTime.UtcNow;
        var isNativeEvaluationAttempt = string.Equals(
            eventName,
            "yuyuko-native-evaluate-before",
            StringComparison.Ordinal);
        var throttle = isNativeEvaluationAttempt
            ? YuyukoNativeEvaluationAttemptDiagnosticThrottle
            : YuyukoRuntimeDiagnosticThrottle;
        var nativeEvaluationEvidence = isNativeEvaluationAttempt
            ? BuildYuyukoNativeEvaluationDiagnosticEvidence(runtimeOrder)
            : "";
        var key = string.Join(
            "|",
            eventName,
            decision,
            request.TraceId,
            request.OrderKey,
            request.DeskCode,
            request.GuestId?.ToString() ?? "",
            NormalizeYuyukoNormalExecutionMode(request.ExecutionMode),
            detail,
            RuntimeNightBusinessLifecycle.Generation,
            nativeEvaluationEvidence);

        lock (AutomationCookingJobLock)
        {
            if (RecentYuyukoRuntimeDiagnostics.TryGetValue(key, out var last)
                && now - last < throttle)
            {
                return true;
            }

            RecentYuyukoRuntimeDiagnostics[key] = now;
            if (RecentYuyukoRuntimeDiagnostics.Count > 256)
            {
                foreach (var staleKey in RecentYuyukoRuntimeDiagnostics
                    .Where(pair => now - pair.Value > TimeSpan.FromMinutes(2))
                    .Select(pair => pair.Key)
                    .ToList())
                {
                    RecentYuyukoRuntimeDiagnostics.Remove(staleKey);
                }
            }
        }

        return false;
    }

    private static string BuildYuyukoNativeEvaluationDiagnosticEvidence(RuntimeOrderMatch runtimeOrder)
    {
        try
        {
            return string.Join(
                "|",
                SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Order),
                SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Controller),
                runtimeOrder.YuyukoManualBindingResolved,
                runtimeOrder.YuyukoManualBindingCaptured,
                SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.ManualEvaluationCallback),
                SpecialBusinessDiagnostics.DescribeObject(
                    ReadControllerCallback(runtimeOrder.Controller, "OverrideEvaluationCallback")),
                IsRuntimeOrderFulfilledForYuyukoDiagnostic(runtimeOrder),
                ReadRuntimeText(runtimeOrder.Controller, "HasEvaluated"));
        }
        catch (Exception ex)
        {
            return $"evidence-unreadable:{ex.GetType().FullName ?? ex.GetType().Name}";
        }
    }

    private static string? BuildYuyukoRuntimeDiagnosticOnceKey(
        string eventName,
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string decision,
        string detail)
    {
        var isBlockedNativeEvaluation = string.Equals(
            decision,
            "native-evaluate-entry-blocked",
            StringComparison.Ordinal);
        if (!isBlockedNativeEvaluation) return null;

        return string.Join(
            "|",
            "runtime-evaluation",
            RuntimeNightBusinessLifecycle.Generation,
            eventName,
            decision,
            request.TraceId,
            request.OrderKey,
            request.DeskCode,
            request.GuestId?.ToString() ?? "",
            NormalizeYuyukoNormalExecutionMode(request.ExecutionMode),
            SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Order),
            SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Controller),
            LocalApiSnapshotSignature.Compute(detail));
    }

    private static object? ReadControllerCallback(object? controller, string name)
    {
        if (controller == null) return null;
        return ReadMember(controller, name)
            ?? TryInvokeInstanceValue(controller, $"get_{name}")
            ?? ReadMember(controller, $"<{name}>k__BackingField")
            ?? ReadMember(controller, $"_{name}_k__BackingField");
    }

    private static string DescribeCallbackForYuyukoDiagnostics(object? callback)
    {
        if (callback == null) return "null";

        var parts = new List<string>
        {
            DescribeCallbackEntryForYuyukoDiagnostics(callback),
        };
        var invocationList = DescribeCallbackInvocationListForYuyukoDiagnostics(callback);
        if (!string.IsNullOrWhiteSpace(invocationList))
        {
            parts.Add($"invocations=[{invocationList}]");
        }

        return string.Join("; ", parts);
    }

    private static string DescribeCallbackEntryForYuyukoDiagnostics(object? callback)
    {
        if (callback == null) return "null";

        var parts = new List<string>
        {
            SpecialBusinessDiagnostics.DescribeObject(callback),
        };
        if (callback.GetType().FullName is { Length: > 0 } typeName)
        {
            parts.Add($"runtimeType={typeName}");
        }

        if (callback is Delegate managedDelegate)
        {
            parts.Add($"method={FormatCallbackMethodInfoForYuyukoDiagnostics(managedDelegate.Method)}");
            parts.Add($"target={SpecialBusinessDiagnostics.DescribeObject(managedDelegate.Target)}");
        }
        else
        {
            AppendCallbackMemberForYuyukoDiagnostics(parts, "method", callback, "Method", "method", "method_info", "method_name");
            AppendCallbackMemberForYuyukoDiagnostics(parts, "target", callback, "Target", "target", "m_target", "_target");
        }

        AppendCallbackMemberForYuyukoDiagnostics(parts, "methodPtr", callback, "method_ptr", "methodPtr", "m_methodPtr");
        AppendCallbackMemberForYuyukoDiagnostics(parts, "invokeImpl", callback, "invoke_impl", "invokeImpl", "m_invokeImpl");
        AppendCallbackMemberForYuyukoDiagnostics(parts, "methodCode", callback, "method_code", "methodCode");
        AppendCallbackMemberForYuyukoDiagnostics(parts, "delegateTrampoline", callback, "delegate_trampoline", "delegateTrampoline");
        return string.Join(" ", parts);
    }

    private static string DescribeCallbackInvocationListForYuyukoDiagnostics(object callback)
    {
        var entries = new List<string>();
        if (callback is Delegate managedDelegate)
        {
            foreach (var entry in managedDelegate.GetInvocationList())
            {
                entries.Add(DescribeCallbackEntryForYuyukoDiagnostics(entry));
                if (entries.Count >= 6) break;
            }
        }
        else
        {
            var invocationList = TryInvokeInstanceValue(callback, "GetInvocationList")
                ?? TryInvokeInstanceValue(callback, "get_InvocationList")
                ?? ReadMember(callback, "delegates")
                ?? ReadMember(callback, "invocationList")
                ?? ReadMember(callback, "m_invocationList");
            foreach (var entry in ReadObjectEnumerable(invocationList))
            {
                entries.Add(DescribeCallbackEntryForYuyukoDiagnostics(entry));
                if (entries.Count >= 6) break;
            }
        }

        return entries.Count <= 1 ? "" : string.Join(" | ", entries);
    }

    private static void AppendCallbackMemberForYuyukoDiagnostics(
        ICollection<string> parts,
        string label,
        object callback,
        params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadMember(callback, name) ?? TryInvokeInstanceValue(callback, $"get_{name}");
            if (value == null) continue;

            parts.Add($"{label}={(string.Equals(label, "method", StringComparison.Ordinal) ? FormatCallbackMethodMemberForYuyukoDiagnostics(value) : FormatCallbackMemberForYuyukoDiagnostics(value))}");
            return;
        }
    }

    private static string FormatCallbackMethodMemberForYuyukoDiagnostics(object value)
    {
        if (value is MethodInfo methodInfo)
        {
            return FormatCallbackMethodInfoForYuyukoDiagnostics(methodInfo);
        }

        var name = ReadRuntimeText(value, "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ReadRuntimeText(value, "name");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var declaringType = ReadMember(value, "DeclaringType")
                ?? TryInvokeInstanceValue(value, "get_DeclaringType")
                ?? ReadMember(value, "declaring_type");
            var declaringTypeName = ReadRuntimeText(declaringType, "FullName");
            if (string.IsNullOrWhiteSpace(declaringTypeName))
            {
                declaringTypeName = ReadRuntimeText(declaringType, "Name");
            }

            return string.IsNullOrWhiteSpace(declaringTypeName) ? name : $"{declaringTypeName}.{name}";
        }

        return FormatCallbackMemberForYuyukoDiagnostics(value);
    }

    private static string FormatCallbackMemberForYuyukoDiagnostics(object value)
    {
        if (value is MethodInfo methodInfo)
        {
            return FormatCallbackMethodInfoForYuyukoDiagnostics(methodInfo);
        }

        if (value is Delegate callback)
        {
            return DescribeCallbackEntryForYuyukoDiagnostics(callback);
        }

        if (value is IntPtr pointer)
        {
            return FormatCallbackPointerForYuyukoDiagnostics(pointer);
        }

        if (value is UIntPtr unsignedPointer)
        {
            return $"0x{unsignedPointer.ToUInt64():X}";
        }

        if (value is string text)
        {
            return text.Trim();
        }

        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Enum)
        {
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }

        return SpecialBusinessDiagnostics.DescribeObject(value);
    }

    private static string FormatCallbackMethodInfoForYuyukoDiagnostics(MethodInfo methodInfo)
    {
        try
        {
            var declaringType = methodInfo.DeclaringType?.FullName ?? "";
            var parameters = string.Join(",", methodInfo.GetParameters().Select(parameter => parameter.ParameterType.Name));
            return $"{declaringType}.{methodInfo.Name}({parameters})";
        }
        catch
        {
            return methodInfo.ToString() ?? "";
        }
    }

    private static string FormatCallbackPointerForYuyukoDiagnostics(IntPtr pointer)
    {
        return $"0x{pointer.ToInt64():X}";
    }

    private static object? ReadOrderSpecialGuest(object? order)
    {
        return order == null
            ? null
            : ReadMember(order, "SpecialGuests") ?? TryInvokeInstanceValue(order, "get_SpecialGuests");
    }

    private static object? ReadControllerSpecialGuest(object? controller)
    {
        return controller == null
            ? null
            : ReadMember(controller, "SpecialGuest") ?? TryInvokeInstanceValue(controller, "get_SpecialGuest");
    }

    private static string DescribeRuntimeGuest(object? guest)
    {
        if (guest == null) return "null";
        var id = ToInt(
            TryInvokeInstanceValue(guest, "get_id")
            ?? TryInvokeInstanceValue(guest, "get_Id")
            ?? TryInvokeInstanceValue(guest, "get_CharacterID")
            ?? ReadMember(guest, "id")
            ?? ReadMember(guest, "Id")
            ?? ReadMember(guest, "CharacterID"),
            -1);
        var name = ReadRuntimeText(guest, "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ReadRuntimeText(guest, "ShowName");
        }

        return $"{SpecialBusinessDiagnostics.DescribeObject(guest)} id={(id >= 0 ? id.ToString() : "")} name={name}";
    }

    private static string DescribeSellableForYuyukoDiagnostics(object? sellable)
    {
        if (sellable == null) return "null";

        var id = ReadSellableId(sellable);
        var type = ReadSellableType(sellable);
        var level = ReadSellableLevel(sellable);
        var name = ReadSellableName(sellable);
        var tags = type == 1
            ? ReadBeverageTagNames(sellable).ToArray()
            : ReadFoodTagNames(sellable).ToArray();
        return $"{SpecialBusinessDiagnostics.DescribeObject(sellable)} id={(id >= 0 ? id.ToString() : "")} type={type} name={name} level={(level >= 0 ? level.ToString() : "")} tags={SpecialBusinessDiagnostics.FormatTags(tags)}";
    }

    private static int ReadSellableLevel(object? sellable)
    {
        if (sellable == null) return -1;
        foreach (var member in new[] { "level", "Level", "Lv", "lv" })
        {
            var value = ReadMember(sellable, member) ?? TryInvokeInstanceValue(sellable, $"get_{member}");
            var parsed = ToInt(value, int.MinValue);
            if (parsed != int.MinValue) return parsed;
        }

        return -1;
    }

    private static string ReadSellableName(object sellable)
    {
        var direct = ReadRuntimeText(sellable, "Name");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        var text = ReadMember(sellable, "Text") ?? TryInvokeInstanceValue(sellable, "get_Text");
        foreach (var member in new[] { "Name", "ShowName", "DisplayName", "text", "Text" })
        {
            var value = ReadRuntimeText(text, member);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return sellable.ToString()?.Trim() ?? "";
    }

    private static IEnumerable<string> ReadBeverageTagNames(object beverage)
    {
        var rawTags = TryInvokeInstanceValue(beverage, "get_Tags")
            ?? ReadMember(beverage, "Tags")
            ?? TryInvokeInstanceValue(beverage, "get_RawTags")
            ?? ReadMember(beverage, "RawTags");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawTag in ReadIntEnumerable(rawTags))
        {
            if (rawTag < 0) continue;

            var tagName = TryReadBeverageTagName(rawTag);
            if (string.IsNullOrWhiteSpace(tagName)) continue;

            if (seen.Add(tagName)) yield return tagName;
        }
    }

    private static string TryReadBeverageTagName(int tagId)
    {
        try
        {
            return InvokeStatic(DataBaseLanguageTypeName, "GetBeverageTag", new object?[] { tagId })?.ToString()?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
