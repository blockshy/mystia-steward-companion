using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private static readonly List<string> RecentWackyRejectedRecipeKeys = new();
    private static readonly Dictionary<string, DateTime> RecentWackyBossRuntimeDiagnostics = new(StringComparer.Ordinal);
    private static readonly TimeSpan WackyBossRuntimeDiagnosticThrottle = TimeSpan.FromSeconds(3);

    private static void AppendWackyRequestDiagnostic(
        string eventName,
        OrderPreparationRequest request,
        string traceId,
        string orderKind)
    {
        if (!IsWackyRequestContext(request)) return;

        RuntimeSpecialBusinessContextService.TryGetActiveSpecialFoodTargetPolicy(out var activeTargetPolicy);
        var targetTags = activeTargetPolicy?.FoodTags ?? Array.Empty<string>();
        SpecialBusinessDiagnostics.AppendWackySnapshot(
            "Wacky Cooking Automation Request",
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
                $"requestedWackyTargetTags: {SpecialBusinessDiagnostics.FormatTags(request.SpecialTargetFoodTags)}",
                $"beverageId: {request.BeverageId}",
                $"beverageName: {request.BeverageName}",
                $"executionReason: {request.ExecutionReason}",
                $"autoTakeBeverage: {request.AutoTakeBeverage}",
                $"autoStartCooking: {request.AutoStartCooking}",
                $"autoCollectCooking: {request.AutoCollectCooking}",
                $"autoDeliverFood: {request.AutoDeliverFood}",
                $"autoCompleteOrder: {request.AutoCompleteOrder}",
                $"koishiExecutionMode: {DescribeWackyKoishiExecutionMode(request)}",
                $"koishiEvaluationMode: {DescribeWackyKoishiEvaluationMode(request)}",
                $"requiresLiveController: {RequiresLiveWackyKoishiBossController(request)}",
                $"activeTargetTags: {SpecialBusinessDiagnostics.FormatTags(targetTags)}",
                $"activeTargetSignature: {activeTargetPolicy?.Signature ?? ""}",
                $"specialBusinessStatus: {RuntimeSpecialBusinessContextService.Status}",
            });
    }

    private static bool IsWackyRequestContext(OrderPreparationRequest request)
    {
        return WackyCookingCompetitionRuntimePolicy.IsChallengeContext(
            RuntimeSpecialBusinessContextService.CurrentChallengeType,
            request.ExecutionReason,
            targetSignature: "");
    }

    private static bool IsWackyKoishiBossRequest(OrderPreparationRequest request)
    {
        return WackyCookingCompetitionRuntimePolicy.IsKoishiBossRole(request.SpecialBusinessRole);
    }

    private static bool RequiresLiveWackyKoishiBossController(OrderPreparationRequest request)
    {
        return WackyCookingCompetitionRuntimePolicy.RequiresLiveKoishiBossController(
            request.SpecialBusinessRole,
            RuntimeSpecialBusinessContextService.IsActiveWackyPhase("Phase3"),
            RuntimeSpecialBusinessContextService.IsWackyKoishiShieldBroken);
    }

    private static bool RequiresNativeWackyKoishiBossEvaluationEntry(OrderPreparationRequest request)
    {
        return RequiresLiveWackyKoishiBossController(request);
    }

    private static string DescribeWackyKoishiExecutionMode(OrderPreparationRequest request)
    {
        return WackyCookingCompetitionRuntimePolicy.DescribeKoishiExecutionMode(
            request.SpecialBusinessRole,
            RuntimeSpecialBusinessContextService.IsActiveWackyPhase("Phase3"),
            RuntimeSpecialBusinessContextService.IsWackyKoishiShieldBroken);
    }

    private static string DescribeWackyKoishiEvaluationMode(OrderPreparationRequest request)
    {
        return WackyCookingCompetitionRuntimePolicy.DescribeKoishiEvaluationMode(
            request.SpecialBusinessRole,
            RequiresNativeWackyKoishiBossEvaluationEntry(request));
    }

    private static string BuildWackyKoishiCaptureSkippedDiagnostic(string prefix)
    {
        return WackyCookingCompetitionRuntimePolicy.BuildCaptureSkippedDiagnostic(
            prefix,
            DescribeWackyKoishiExecutionMode(new OrderPreparationRequest { SpecialBusinessRole = WackyCookingCompetitionRuntimePolicy.KoishiBossRole }));
    }

    private static bool IsWackyKoishiBossTarget(CookingCollectionTarget target)
    {
        return WackyCookingCompetitionRuntimePolicy.IsKoishiBossRole(target.SpecialBusinessRole);
    }

    private static bool IsExecutableWackyKoishiBossRuntimeOrder(object? controller, object? order, out string diagnostic)
    {
        if (controller == null)
        {
            diagnostic = "controller missing";
            return false;
        }

        if (order == null)
        {
            diagnostic = "order missing";
            return false;
        }

        if (!RuntimeSpecialBusinessContextService.IsActiveWackyPhase("Phase3"))
        {
            diagnostic = $"not active wacky koishi phase3: {RuntimeSpecialBusinessContextService.Status}";
            return false;
        }

        var orderGenerationCallback = ReadKoishiBossControllerCallback(controller, "OverrideOrderGenerationCallback");
        var evaluationCallback = ReadKoishiBossControllerCallback(controller, "OverrideEvaluationCallback");
        if (evaluationCallback == null)
        {
            diagnostic = $"missing boss evaluation callback: orderGeneration={DescribeOptionalKoishiBossOrderGenerationCallback(orderGenerationCallback)}, evaluation=missing";
            return false;
        }

        diagnostic = $"live controller has boss evaluation callback; orderGeneration={DescribeOptionalKoishiBossOrderGenerationCallback(orderGenerationCallback)}, evaluation=ok";
        return true;
    }

    private static string DescribeOptionalKoishiBossOrderGenerationCallback(object? callback)
    {
        return callback == null ? "missing(optional)" : "ok";
    }

    private static object? ReadKoishiBossControllerCallback(object? controller, string name)
    {
        if (controller == null) return null;
        return ReadMember(controller, name)
            ?? TryInvokeInstanceValue(controller, $"get_{name}")
            ?? ReadMember(controller, $"<{name}>k__BackingField")
            ?? ReadMember(controller, $"_{name}_k__BackingField");
    }

    private static void AppendWackyBossRuntimeDiagnostic(
        string eventName,
        OrderPreparationRequest request,
        RuntimeOrderMatch runtimeOrder,
        string decision,
        string detail = "")
    {
        if (!IsWackyKoishiBossRequest(request)) return;
        if (ShouldThrottleWackyBossRuntimeDiagnostic(eventName, request, decision, detail)) return;

        SpecialBusinessDiagnostics.AppendWackySnapshot(
            "Wacky Cooking Koishi Boss Runtime Diagnostic",
            new[]
            {
                $"event: {eventName}",
                $"decision: {decision}",
                $"detail: {detail}",
                $"desk: {(request.DeskCode >= 0 ? request.DeskCode + 1 : -1)}",
                $"orderKey: {request.OrderKey}",
                $"traceId: {request.TraceId}",
                $"guestId: {request.GuestId?.ToString() ?? ""}",
                $"guestName: {request.GuestName}",
                $"foodTag: {request.FoodTag}",
                $"beverageTag: {request.BeverageTag}",
                $"matchFoodId: {request.MatchFoodId}",
                $"matchBeverageId: {request.MatchBeverageId}",
                $"targetFood: {SpecialBusinessDiagnostics.FormatIdName(request.FoodId, request.RecipeName)}",
                $"targetBeverage: {SpecialBusinessDiagnostics.FormatIdName(request.BeverageId, request.BeverageName)}",
                $"controller: {SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Controller)}",
                $"order: {SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Order)}",
                $"runtimeDiagnostic: {runtimeOrder.Diagnostic}",
                $"orderGenerationCallback: {SpecialBusinessDiagnostics.DescribeObject(ReadKoishiBossControllerCallback(runtimeOrder.Controller, "OverrideOrderGenerationCallback"))}",
                $"evaluationCallback: {SpecialBusinessDiagnostics.DescribeObject(ReadKoishiBossControllerCallback(runtimeOrder.Controller, "OverrideEvaluationCallback"))}",
                $"controllerDeskCode: {ReadRuntimeInt(runtimeOrder.Controller, "DeskCode")}",
                $"isControlled: {ReadRuntimeText(runtimeOrder.Controller, "IsControlled")}",
                $"isHerself: {ReadRuntimeText(runtimeOrder.Controller, "IsHerself")}",
                $"remainOrderCount: {ReadRuntimeText(runtimeOrder.Controller, "RemainOrderCount")}",
                $"freeOrderCount: {ReadRuntimeText(runtimeOrder.Controller, "FreeOrderCount")}",
                $"fund: {ReadRuntimeText(runtimeOrder.Controller, "GetFund")}",
                $"baseFundCarry: {ReadRuntimeText(runtimeOrder.Controller, "BaseFundCarry")}",
                $"maxFundCarry: {ReadRuntimeText(runtimeOrder.Controller, "MaxFundCarry")}",
                $"extraFundByBuff: {ReadRuntimeText(runtimeOrder.Controller, "ExtraFundByBuff")}",
                $"willPayMoney: {ReadRuntimeText(runtimeOrder.Controller, "WillPayMoney")}",
                $"hasEvaluated: {ReadRuntimeText(runtimeOrder.Controller, "HasEvaluated")}",
                $"servFood: {SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Order == null ? null : ReadOrderServedFood(runtimeOrder.Order))}",
                $"servBeverage: {SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Order == null ? null : ReadOrderServedBeverage(runtimeOrder.Order))}",
                $"servedFoodInAir: {SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Order == null ? null : ReadMember(runtimeOrder.Order, "ServedFoodInAir"))}",
                $"servedBeverageInAir: {SpecialBusinessDiagnostics.DescribeObject(runtimeOrder.Order == null ? null : ReadMember(runtimeOrder.Order, "ServedBeverageInAir"))}",
                $"orderFullfilled: {ReadRuntimeText(runtimeOrder.Order, "IsFullfilled")}",
                $"koishiExecutionMode: {DescribeWackyKoishiExecutionMode(request)}",
                $"koishiEvaluationMode: {DescribeWackyKoishiEvaluationMode(request)}",
                $"requiresLiveController: {RequiresLiveWackyKoishiBossController(request)}",
                $"shieldBroken: {RuntimeSpecialBusinessContextService.IsWackyKoishiShieldBroken}",
                $"specialBusinessStatus: {RuntimeSpecialBusinessContextService.Status}",
            });
    }

    private static bool ShouldThrottleWackyBossRuntimeDiagnostic(
        string eventName,
        OrderPreparationRequest request,
        string decision,
        string detail)
    {
        var now = DateTime.UtcNow;
        var key = string.Join(
            "|",
            eventName,
            decision,
            request.TraceId,
            request.OrderKey,
            request.DeskCode,
            detail,
            DescribeWackyKoishiExecutionMode(request),
            DescribeWackyKoishiEvaluationMode(request));

        lock (AutomationCookingJobLock)
        {
            if (RecentWackyBossRuntimeDiagnostics.TryGetValue(key, out var last)
                && now - last < WackyBossRuntimeDiagnosticThrottle)
            {
                return true;
            }

            RecentWackyBossRuntimeDiagnostics[key] = now;
            if (RecentWackyBossRuntimeDiagnostics.Count > 256)
            {
                foreach (var staleKey in RecentWackyBossRuntimeDiagnostics
                    .Where(pair => now - pair.Value > TimeSpan.FromMinutes(2))
                    .Select(pair => pair.Key)
                    .ToList())
                {
                    RecentWackyBossRuntimeDiagnostics.Remove(staleKey);
                }
            }
        }

        return false;
    }

    private static string ReadRuntimeInt(object? value, string member)
    {
        var raw = value == null ? null : ReadMember(value, member) ?? TryInvokeInstanceValue(value, $"get_{member}");
        var parsed = ToInt(raw, int.MinValue);
        return parsed == int.MinValue ? "" : parsed.ToString();
    }

    private static string ReadRuntimeText(object? value, string member)
    {
        if (value == null) return "";
        var raw = ReadMember(value, member)
            ?? TryInvokeInstanceValue(value, $"get_{member}")
            ?? TryInvokeInstanceValue(value, member);
        return raw?.ToString()?.Trim() ?? "";
    }

    private static bool IsWackyTargetContext(CookingCollectionTarget target)
    {
        return string.Equals(
            target.SpecialFoodTargetPolicy?.ChallengeType,
            SpecialBusinessChallengeTypes.WackyCookingCompetition,
            StringComparison.Ordinal);
    }

    private static bool TryGetSpecialTargetCookingDeferral(CookingCollectionTarget target, out string message)
    {
        message = "";
        if (target.SpecialFoodTargetPolicy != null
            && !SpecialTargetMatchesPredictedFoodTags(target, out message))
        {
            AppendAutomationLog("special-target-predicted-tags-mismatch-skip", target, message);
            return true;
        }

        if (!IsWackyTargetContext(target)) return false;
        var targetTags = target.SpecialTargetFoodTags;
        if (targetTags.Count == 0) return false;

        var key = BuildWackyRejectedRecipeKey(target, targetTags);
        if (string.IsNullOrWhiteSpace(key)) return false;

        lock (AutomationCookingJobLock)
        {
            if (!RecentWackyRejectedRecipeKeys.Contains(key, StringComparer.Ordinal)) return false;
        }

        message = $"当前怪诞料理目标 Tag（{string.Join("、", targetTags)}）下，{target.FoodName} 的当前配方/加料组合已被实机判定不匹配，等待推荐刷新或目标 Tag 更新后再开锅。";
        AppendAutomationLog("wacky-rejected-skip", target, message);
        return true;
    }

    private static void RememberRecentWackyRejectedRecipe(CookingCollectionTarget target, IReadOnlyList<string> targetTags)
    {
        if (!IsWackyTargetContext(target)) return;
        var key = BuildWackyRejectedRecipeKey(target, targetTags);
        if (string.IsNullOrWhiteSpace(key)) return;

        lock (AutomationCookingJobLock)
        {
            if (RecentWackyRejectedRecipeKeys.Contains(key, StringComparer.Ordinal)) return;
            RecentWackyRejectedRecipeKeys.Add(key);
            if (RecentWackyRejectedRecipeKeys.Count > WackyCookingCompetitionRuntimePolicy.MaxRecentRejectedRecipeKeys)
            {
                RecentWackyRejectedRecipeKeys.RemoveRange(
                    0,
                    RecentWackyRejectedRecipeKeys.Count - WackyCookingCompetitionRuntimePolicy.MaxRecentRejectedRecipeKeys);
            }
        }

        AppendAutomationLog("wacky-rejected-remember", target, $"key={key}; targetTags={string.Join("、", targetTags)}");
    }

    private static string BuildWackyRejectedRecipeKey(CookingCollectionTarget target, IReadOnlyList<string> targetTags)
    {
        return WackyCookingCompetitionRuntimePolicy.BuildRejectedRecipeKey(
            target.FoodId,
            target.RecipeId,
            target.ExtraIngredientIds,
            targetTags);
    }

    private static void AppendSpecialFoodTargetCookingJobDiagnostic(
        string eventName,
        AutomationCookingJob job,
        string decision,
        int actualFoodId = -1,
        IReadOnlyList<string>? targetTags = null,
        IReadOnlyList<string>? actualTags = null,
        string detail = "")
    {
        var targetPolicy = job.Target.SpecialFoodTargetPolicy;
        if (targetPolicy == null) return;
        RuntimeSpecialBusinessContextService.TryGetActiveSpecialFoodTargetPolicy(out var activeTargetPolicy);
        var activeTargetTags = activeTargetPolicy?.FoodTags ?? Array.Empty<string>();
        var context = job.Target.ToLogContext();
        var onceKey = string.Join(
            "|",
            "cooking-job",
            RuntimeNightBusinessLifecycle.Generation,
            job.JobId,
            eventName,
            decision,
            job.SpecialFoodTargetRevision,
            job.YuumaSettlementTracker.Stage,
            actualFoodId);
        var lines = new[]
            {
                $"event: {eventName}",
                $"decision: {decision}",
                $"detail: {detail}",
                $"target: {SpecialBusinessDiagnostics.FormatOrderContext(context)}",
                $"cookingJobRecipeName: {job.RecipeName}",
                $"specialBusinessRole: {job.Target.SpecialBusinessRole}",
                $"matchFood: {SpecialBusinessDiagnostics.FormatIdName(job.Target.MatchFoodId, "")}",
                $"matchBeverage: {SpecialBusinessDiagnostics.FormatIdName(job.Target.MatchBeverageId, "")}",
                $"targetFood: {SpecialBusinessDiagnostics.FormatIdName(job.Target.FoodId, job.Target.FoodName)}",
                $"targetRecipeId: {job.Target.RecipeId}",
                $"targetExtraIngredientIds: {SpecialBusinessDiagnostics.FormatIds(job.Target.ExtraIngredientIds)}",
                $"predictedFoodTags: {SpecialBusinessDiagnostics.FormatTags(job.Target.PredictedFoodTags)}",
                $"targetBeverage: {SpecialBusinessDiagnostics.FormatIdName(job.Target.BeverageId, job.Target.BeverageName)}",
                $"actualFoodId: {actualFoodId}",
                $"activeTargetTags: {SpecialBusinessDiagnostics.FormatTags(activeTargetTags)}",
                $"activeTargetSignature: {activeTargetPolicy?.Signature ?? ""}",
                $"cookingJobTargetTags: {SpecialBusinessDiagnostics.FormatTags(job.Target.SpecialTargetFoodTags)}",
                $"cookingJobTargetSignature: {targetPolicy.Signature}",
                $"cookingJobTargetMatchMode: {targetPolicy.MatchModeValue}",
                $"expectedTargetTags: {SpecialBusinessDiagnostics.FormatTags(targetTags)}",
                $"actualFoodTags: {SpecialBusinessDiagnostics.FormatTags(actualTags)}",
                $"cookControllerReservation: {job.CookerReservation}",
                $"cookControllerPointer: 0x{(long)job.ControllerPointer:X}",
                $"cookingJobAgeSeconds: {(DateTime.UtcNow - job.CreatedAtUtc).TotalSeconds:0.0}",
                $"specialBusinessStatus: {RuntimeSpecialBusinessContextService.Status}",
            };
        if (string.Equals(targetPolicy.ChallengeType, SpecialBusinessChallengeTypes.BloodPondHell, StringComparison.Ordinal))
        {
            SpecialBusinessDiagnostics.AppendYuumaSnapshot(
                "Blood Pond Hell Cooking Job Diagnostic",
                lines,
                onceKey);
            return;
        }

        SpecialBusinessDiagnostics.AppendWackySnapshot(
            "Wacky Cooking Job Diagnostic",
            lines,
            onceKey);
    }
}
