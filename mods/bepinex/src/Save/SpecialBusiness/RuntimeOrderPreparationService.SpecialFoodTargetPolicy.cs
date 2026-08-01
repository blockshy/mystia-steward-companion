using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private static bool TryValidateRequestedSpecialFoodTargetPolicy(
        OrderPreparationRequest request,
        CookingCollectionTargetKind requestKind,
        out SpecialFoodTargetPolicy? policy,
        out string error)
    {
        policy = null;
        error = "";
        if (request.AllowYuumaControlledProgression
            && (requestKind != CookingCollectionTargetKind.NormalOrder || !IsYuumaBossRequest(request)))
        {
            error = "血池地狱受控推进只允许精确的 Yuuma BOSS 普客订单。";
            return false;
        }

        var hasRequestPolicy = HasRequestedSpecialFoodTargetPolicy(request);
        var requiresActivePolicy = RequestRequiresActiveSpecialFoodTargetPolicy(request);
        if (!hasRequestPolicy)
        {
            if (!requiresActivePolicy) return true;

            error = "当前特殊经营订单要求完整的特殊料理目标策略，但请求未携带 challenge、owner、generation、revision、Tag、matchMode 和 signature。";
            return false;
        }

        if (!SpecialFoodTargetPolicy.TryCreate(
                request.SpecialTargetChallenge,
                request.SpecialTargetOwner,
                request.SpecialTargetGeneration,
                request.SpecialTargetFoodTags.Select(tag => FoodTags.NormalizeName(tag) ?? tag),
                request.SpecialTargetMatchMode,
                request.SpecialTargetSignature,
                out policy,
                out var parseError)
            || policy == null)
        {
            error = $"特殊料理目标策略无效：{parseError}";
            return false;
        }

        if (!IsSpecialFoodTargetRoleAllowed(request.SpecialBusinessRole, policy, out var roleError))
        {
            error = roleError;
            return false;
        }

        if (IsYuumaBossRequest(request))
        {
            if (!IsValidYuumaFoodTargetPolicy(policy, out var yuumaError))
            {
                error = yuumaError;
                return false;
            }

            if (request.SpecialTargetRevision <= 0)
            {
                error = "血池地狱特殊料理目标请求缺少正 revision。";
                return false;
            }

            if (!RuntimeSpecialBusinessContextService.TryGetActiveYuumaFoodTargetState(
                    out var activeYuumaPolicy,
                    out var activeYuumaRevision)
                || activeYuumaPolicy == null
                || activeYuumaRevision <= 0)
            {
                error = "当前游戏运行时没有完整且属于本经营代际的血池地狱目标策略与 revision。";
                return false;
            }

            if (!policy.HasSameIdentity(activeYuumaPolicy)
                || request.SpecialTargetRevision != activeYuumaRevision)
            {
                error = $"血池地狱特殊料理目标已经变化：请求={DescribeSpecialFoodTargetPolicy(policy)}; revision={request.SpecialTargetRevision}；"
                    + $"当前={DescribeSpecialFoodTargetPolicy(activeYuumaPolicy)}; revision={activeYuumaRevision}。";
                return false;
            }

            if (request.AllowYuumaControlledProgression
                && !TryValidateYuumaControlledProgressionRequest(request, requestKind, policy, out error))
            {
                return false;
            }

            return true;
        }

        if (request.SpecialTargetRevision != 0)
        {
            error = "非血池地狱特殊料理目标不能携带 target revision。";
            return false;
        }

        if (!RuntimeSpecialBusinessContextService.TryGetActiveSpecialFoodTargetPolicy(out var activePolicy)
            || activePolicy == null)
        {
            error = "当前游戏运行时没有完整且属于本经营代际的特殊料理目标策略。";
            return false;
        }

        if (!policy.HasSameIdentity(activePolicy))
        {
            error = $"特殊料理目标策略已经变化：请求={DescribeSpecialFoodTargetPolicy(policy)}；当前={DescribeSpecialFoodTargetPolicy(activePolicy)}。";
            return false;
        }

        return true;
    }

    private static bool IsSpecialFoodTargetRoleAllowed(
        string specialBusinessRole,
        SpecialFoodTargetPolicy policy,
        out string error)
    {
        if (string.Equals(policy.ChallengeType, SpecialBusinessChallengeTypes.BloodPondHell, StringComparison.Ordinal))
        {
            if (string.Equals(specialBusinessRole, SpecialBusinessOrderRoles.YuumaBoss, StringComparison.Ordinal))
            {
                error = "";
                return true;
            }

            error = $"血池地狱特殊料理目标只允许精确角色 {SpecialBusinessOrderRoles.YuumaBoss}，实际角色为 {specialBusinessRole}。";
            return false;
        }

        if (string.Equals(policy.ChallengeType, SpecialBusinessChallengeTypes.WackyCookingCompetition, StringComparison.Ordinal)
            && specialBusinessRole is SpecialBusinessOrderRoles.WackyGhost
                or SpecialBusinessOrderRoles.WackyKoishiBoss
                or SpecialBusinessOrderRoles.WackyTarget)
        {
            error = "";
            return true;
        }

        error = $"特殊料理目标与订单角色不匹配：challenge={policy.ChallengeType}; role={specialBusinessRole}。";
        return false;
    }

    private static bool TryValidateCurrentSpecialFoodTargetPolicy(
        CookingCollectionTarget target,
        out SpecialFoodTargetPolicy? currentPolicy,
        out string error)
    {
        currentPolicy = null;
        error = "";
        var expectedPolicy = target.SpecialFoodTargetPolicy;
        if (expectedPolicy == null) return true;

        if (IsYuumaBossTarget(target))
        {
            if (target.SpecialFoodTargetRevision <= 0)
            {
                error = "自动料理目标缺少正的血池地狱 target revision。";
                return false;
            }

            if (!RuntimeSpecialBusinessContextService.TryGetActiveYuumaFoodTargetState(
                    out currentPolicy,
                    out var currentRevision)
                || currentPolicy == null
                || currentRevision <= 0)
            {
                error = "当前游戏运行时不再提供完整的血池地狱目标策略与 revision。";
                return false;
            }

            if (!expectedPolicy.HasSameIdentity(currentPolicy)
                || target.SpecialFoodTargetRevision != currentRevision)
            {
                error = $"血池地狱特殊料理目标已经变化：开锅={DescribeSpecialFoodTargetPolicy(expectedPolicy)}; revision={target.SpecialFoodTargetRevision}；"
                    + $"当前={DescribeSpecialFoodTargetPolicy(currentPolicy)}; revision={currentRevision}。";
                return false;
            }

            if (!IsValidYuumaFoodTargetPolicy(currentPolicy, out var yuumaError))
            {
                error = yuumaError;
                return false;
            }

            return true;
        }

        if (target.SpecialFoodTargetRevision != 0)
        {
            error = "非血池地狱自动料理目标不能携带 target revision。";
            return false;
        }

        if (!RuntimeSpecialBusinessContextService.TryGetActiveSpecialFoodTargetPolicy(out currentPolicy)
            || currentPolicy == null)
        {
            error = "当前游戏运行时不再提供特殊料理目标策略。";
            return false;
        }

        if (!expectedPolicy.HasSameIdentity(currentPolicy))
        {
            error = $"特殊料理目标策略已经变化：开锅={DescribeSpecialFoodTargetPolicy(expectedPolicy)}；当前={DescribeSpecialFoodTargetPolicy(currentPolicy)}。";
            return false;
        }

        return true;
    }

    private static bool TryCaptureYuumaFoodTargetRevision(
        CookingCollectionTarget target,
        out long revision,
        out string error)
    {
        revision = 0;
        error = "";
        if (!IsYuumaBossTarget(target)) return true;

        var expectedPolicy = target.SpecialFoodTargetPolicy;
        if (expectedPolicy == null)
        {
            error = "自动料理目标缺少完整的血池地狱双 Tag policy，已在开锅副作用前停止。";
            return false;
        }

        if (!IsValidYuumaFoodTargetPolicy(expectedPolicy, out var expectedPolicyError))
        {
            error = $"{expectedPolicyError} 已在开锅副作用前停止。";
            return false;
        }

        var expectedRevision = target.SpecialFoodTargetRevision;
        if (expectedRevision <= 0)
        {
            error = "自动料理目标缺少正的血池地狱 target revision，已在开锅副作用前停止。";
            return false;
        }

        if (!RuntimeSpecialBusinessContextService.TryGetActiveYuumaFoodTargetState(
                out var currentPolicy,
                out var currentRevision)
            || currentPolicy == null
            || currentRevision <= 0)
        {
            error = "当前血池地狱目标 revision 尚不可用，已在开锅副作用前停止。";
            return false;
        }

        if (!expectedPolicy.HasSameIdentity(currentPolicy)
            || expectedRevision != currentRevision)
        {
            error = "当前血池地狱目标已变化，已在开锅副作用前停止并等待重新推荐。";
            return false;
        }

        revision = expectedRevision;
        return true;
    }

    private static bool HasRequestedSpecialFoodTargetPolicy(OrderPreparationRequest request)
    {
        return request.SpecialTargetChallenge.Length > 0
            || request.SpecialTargetOwner.Length > 0
            || request.SpecialTargetGeneration != 0
            || request.SpecialTargetRevision != 0
            || request.SpecialTargetFoodTags.Count > 0
            || request.SpecialTargetMatchMode.Length > 0
            || request.SpecialTargetSignature.Length > 0;
    }

    private static bool RequestRequiresActiveSpecialFoodTargetPolicy(OrderPreparationRequest request)
    {
        if (IsYuumaBossRequest(request)) return true;

        if (!RuntimeSpecialBusinessContextService.TryGetActiveSpecialFoodTargetPolicy(out var activePolicy)
            || activePolicy == null)
        {
            return false;
        }

        if (string.Equals(activePolicy.ChallengeType, SpecialBusinessChallengeTypes.BloodPondHell, StringComparison.Ordinal))
        {
            return IsYuumaBossRequest(request);
        }

        if (!string.Equals(
                activePolicy.ChallengeType,
                SpecialBusinessChallengeTypes.WackyCookingCompetition,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (request.SpecialBusinessRole == SpecialBusinessOrderRoles.WackyKoishiBoss
            && RuntimeSpecialBusinessContextService.IsActiveWackyPhase("Phase3"))
        {
            return false;
        }

        return request.SpecialBusinessRole is SpecialBusinessOrderRoles.WackyGhost
            or SpecialBusinessOrderRoles.WackyKoishiBoss
            or SpecialBusinessOrderRoles.WackyTarget;
    }

    private static bool IsYuumaBossRequest(OrderPreparationRequest request)
    {
        return string.Equals(
            request.SpecialBusinessRole,
            SpecialBusinessOrderRoles.YuumaBoss,
            StringComparison.Ordinal);
    }

    private static bool IsYuumaBossTarget(CookingCollectionTarget target)
    {
        return string.Equals(
            target.SpecialBusinessRole,
            SpecialBusinessOrderRoles.YuumaBoss,
            StringComparison.Ordinal);
    }

    private static bool IsValidYuumaFoodTargetPolicy(
        SpecialFoodTargetPolicy policy,
        out string error)
    {
        if (!string.Equals(policy.ChallengeType, SpecialBusinessChallengeTypes.BloodPondHell, StringComparison.Ordinal)
            || !string.Equals(policy.Owner, "yuuma", StringComparison.Ordinal)
            || policy.MatchMode != SpecialFoodTargetMatchMode.All
            || policy.FoodTags.Count != 2)
        {
            error = $"血池地狱 BOSS 自动化要求精确挑战、owner=yuuma、All 和两个完整目标 Tag；实际={DescribeSpecialFoodTargetPolicy(policy)}。";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryValidateYuumaControlledProgressionRequest(
        OrderPreparationRequest request,
        CookingCollectionTargetKind requestKind,
        SpecialFoodTargetPolicy policy,
        out string error)
    {
        if (requestKind != CookingCollectionTargetKind.NormalOrder
            || !IsYuumaBossRequest(request)
            || !IsValidYuumaFoodTargetPolicy(policy, out _))
        {
            error = "血池地狱受控推进只允许携带完整当前目标策略的 Yuuma BOSS 普客订单。";
            return false;
        }

        if (request.MatchFoodId < 0
            || request.MatchBeverageId < 0
            || request.FoodId != request.MatchFoodId
            || request.BeverageId != request.MatchBeverageId)
        {
            error = "血池地狱受控推进必须精确使用原订单料理和酒水，不能替换订单项目。";
            return false;
        }

        if (!request.PredictedFoodTagsProvided)
        {
            error = "血池地狱受控推进请求未显式携带完整预测 Tag 列表。";
            return false;
        }

        var normalizedPredictedTags = SpecialFoodTargetPolicy.NormalizeTags(
            request.PredictedFoodTags.Select(tag => FoodTags.NormalizeName(tag) ?? tag));
        if (policy.Matches(normalizedPredictedTags))
        {
            error = "血池地狱请求的预测 Tag 已满足当前双 Tag，不能标记为受控推进。";
            return false;
        }

        error = "";
        return true;
    }

    private static bool IsYuumaControlledProgressionTarget(CookingCollectionTarget target)
    {
        var policy = target.SpecialFoodTargetPolicy;
        var normalizedPredictedTags = SpecialFoodTargetPolicy.NormalizeTags(
            target.PredictedFoodTags.Select(tag => FoodTags.NormalizeName(tag) ?? tag));
        return target.AllowYuumaControlledProgression
            && target.Kind == CookingCollectionTargetKind.NormalOrder
            && IsYuumaBossTarget(target)
            && policy != null
            && IsValidYuumaFoodTargetPolicy(policy, out _)
            && !policy.Matches(normalizedPredictedTags)
            && target.MatchFoodId >= 0
            && target.MatchBeverageId >= 0
            && target.FoodId == target.MatchFoodId
            && target.BeverageId == target.MatchBeverageId;
    }

    private static bool SpecialTargetMatchesPredictedFoodTags(
        CookingCollectionTarget target,
        out string message)
    {
        message = "";
        var policy = target.SpecialFoodTargetPolicy;
        if (policy == null) return true;

        if (!TryValidateCurrentSpecialFoodTargetPolicy(target, out _, out var validationError))
        {
            message = validationError;
            return false;
        }

        var normalizedPredictedTags = SpecialFoodTargetPolicy.NormalizeTags(
            target.PredictedFoodTags.Select(tag => FoodTags.NormalizeName(tag) ?? tag));
        if (policy.Matches(normalizedPredictedTags)) return true;

        if (IsYuumaControlledProgressionTarget(target))
        {
            AppendAutomationLog(
                "yuuma-controlled-progression-predicted-tag-bypass",
                target,
                $"{target.FoodName} uses the exact original-order food/beverage while predicted tags "
                + $"({string.Join(",", normalizedPredictedTags)}) do not satisfy the current dual-Tag target "
                + $"({string.Join(",", policy.FoodTags)}); continuing under the explicit controlled progression policy.");
            return true;
        }

        message = $"{target.FoodName} 的预测 Tag（{string.Join("、", normalizedPredictedTags)}）不满足当前特殊目标 "
            + $"{policy.MatchModeValue}（{string.Join("、", policy.FoodTags)}）。";
        return false;
    }

    private static string DescribeSpecialFoodTargetPolicy(SpecialFoodTargetPolicy? policy)
    {
        if (policy == null) return "none";
        return $"{policy.Signature}; tags={string.Join(",", policy.FoodTags)}";
    }

    private static SpecialFoodTargetPolicy? ReadRequestedSpecialFoodTargetPolicy(OrderPreparationRequest request)
    {
        return SpecialFoodTargetPolicy.TryCreate(
            request.SpecialTargetChallenge,
            request.SpecialTargetOwner,
            request.SpecialTargetGeneration,
            request.SpecialTargetFoodTags.Select(tag => FoodTags.NormalizeName(tag) ?? tag),
            request.SpecialTargetMatchMode,
            request.SpecialTargetSignature,
            out var policy,
            out _)
            ? policy
            : null;
    }

    private static void AppendYuumaAutomationDiagnostic(
        string eventName,
        OrderPreparationRequest request,
        string traceId,
        string decision,
        string detail = "")
    {
        if (!IsYuumaBossRequest(request)) return;

        RuntimeSpecialBusinessContextService.TryGetActiveYuumaFoodTargetState(
            out var activePolicy,
            out var activeRevision);
        SpecialBusinessDiagnostics.AppendYuumaSnapshot(
            "Blood Pond Hell Automation",
            new[]
            {
                $"event: {eventName}",
                $"decision: {decision}",
                $"detail: {detail}",
                $"traceId: {traceId}",
                $"orderKey: {request.OrderKey}",
                $"desk: {(request.DeskCode >= 0 ? request.DeskCode + 1 : -1)}",
                $"guestId: {request.GuestId?.ToString() ?? ""}",
                $"runtimeGuestId: {request.RuntimeGuestId?.ToString() ?? ""}",
                $"specialBusinessRole: {request.SpecialBusinessRole}",
                $"foodTagId: {request.FoodTagId?.ToString() ?? ""}",
                $"beverageTagId: {request.BeverageTagId?.ToString() ?? ""}",
                $"foodId: {request.FoodId}",
                $"recipeId: {request.RecipeId}",
                $"autoDeliverFood: {request.AutoDeliverFood}",
                $"autoCompleteOrder: {request.AutoCompleteOrder}",
                $"allowYuumaControlledProgression: {request.AllowYuumaControlledProgression}",
                $"predictedFoodTags: {SpecialBusinessDiagnostics.FormatTags(request.PredictedFoodTags)}",
                $"requestedTarget: {request.SpecialTargetSignature}",
                $"requestedTargetRevision: {request.SpecialTargetRevision}",
                $"activeTarget: {DescribeSpecialFoodTargetPolicy(activePolicy)}",
                $"activeTargetRevision: {activeRevision}",
                $"specialBusinessStatus: {RuntimeSpecialBusinessContextService.Status}",
            },
            $"{RuntimeNightBusinessLifecycle.Generation}|automation|{eventName}|{traceId}|{decision}|"
            + $"{request.AutoDeliverFood}|{request.AutoCompleteOrder}|"
            + $"{request.AllowYuumaControlledProgression}|"
            + $"{request.SpecialTargetSignature}|{request.SpecialTargetRevision}|{detail}");
    }
}
