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
            error = "血池地狱低收益推进方案只适用于已确认的 BOSS 普客订单。";
            return false;
        }

        var hasRequestPolicy = HasRequestedSpecialFoodTargetPolicy(request);
        var requiresActivePolicy = RequestRequiresActiveSpecialFoodTargetPolicy(request);
        if (!hasRequestPolicy)
        {
            if (!requiresActivePolicy) return true;

            error = "当前特殊经营订单缺少完整的料理目标信息：特殊经营类型、目标归属、本场经营编号、目标版本、标签、匹配方式或目标签名不完整，请等待推荐刷新。";
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
            error = $"特殊料理目标信息无效：{parseError}";
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
                error = "血池地狱料理目标缺少有效版本，请等待推荐刷新。";
                return false;
            }

            if (!RuntimeSpecialBusinessContextService.TryGetActiveYuumaFoodTargetState(
                    out var activeYuumaPolicy,
                    out var activeYuumaRevision)
                || activeYuumaPolicy == null
                || activeYuumaRevision <= 0)
            {
                error = "当前游戏没有本场经营所需的完整血池地狱目标信息或有效目标版本。";
                return false;
            }

            if (!policy.HasSameIdentity(activeYuumaPolicy)
                || request.SpecialTargetRevision != activeYuumaRevision)
            {
                error = $"血池地狱料理目标已经变化，请等待推荐刷新。详细原因：请求={DescribeSpecialFoodTargetPolicy(policy)}; "
                    + $"目标版本={request.SpecialTargetRevision}；当前={DescribeSpecialFoodTargetPolicy(activeYuumaPolicy)}; "
                    + $"目标版本={activeYuumaRevision}。";
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
            error = "当前特殊经营订单携带了不适用的目标版本。";
            return false;
        }

        if (!RuntimeSpecialBusinessContextService.TryGetActiveSpecialFoodTargetPolicy(out var activePolicy)
            || activePolicy == null)
        {
            error = "当前游戏没有本场经营所需的完整料理目标信息。";
            return false;
        }

        if (!policy.HasSameIdentity(activePolicy))
        {
            error = $"特殊料理目标已经变化，请等待推荐刷新。详细原因：请求={DescribeSpecialFoodTargetPolicy(policy)}；"
                + $"当前={DescribeSpecialFoodTargetPolicy(activePolicy)}。";
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

            error = $"血池地狱料理目标只适用于已确认的 BOSS 订单；要求角色={SpecialBusinessOrderRoles.YuumaBoss}，"
                + $"当前角色={specialBusinessRole}。";
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

        error = $"特殊料理目标与当前订单不匹配：特殊经营类型={policy.ChallengeType}; 订单角色={specialBusinessRole}。";
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
                error = "自动料理任务缺少有效的血池地狱目标版本。";
                return false;
            }

            if (!RuntimeSpecialBusinessContextService.TryGetActiveYuumaFoodTargetState(
                    out currentPolicy,
                    out var currentRevision)
                || currentPolicy == null
                || currentRevision <= 0)
            {
                error = "当前游戏不再提供完整的血池地狱目标信息或有效目标版本。";
                return false;
            }

            if (!expectedPolicy.HasSameIdentity(currentPolicy)
                || target.SpecialFoodTargetRevision != currentRevision)
            {
                error = $"血池地狱料理目标在开锅后发生变化：开锅时={DescribeSpecialFoodTargetPolicy(expectedPolicy)}; "
                    + $"目标版本={target.SpecialFoodTargetRevision}；当前={DescribeSpecialFoodTargetPolicy(currentPolicy)}; "
                    + $"目标版本={currentRevision}。";
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
            error = "当前自动料理任务携带了不适用的目标版本。";
            return false;
        }

        if (!RuntimeSpecialBusinessContextService.TryGetActiveSpecialFoodTargetPolicy(out currentPolicy)
            || currentPolicy == null)
        {
            error = "当前游戏不再提供完整的特殊料理目标信息。";
            return false;
        }

        if (!expectedPolicy.HasSameIdentity(currentPolicy))
        {
            error = $"特殊料理目标在开锅后发生变化：开锅时={DescribeSpecialFoodTargetPolicy(expectedPolicy)}；"
                + $"当前={DescribeSpecialFoodTargetPolicy(currentPolicy)}。";
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
            error = "自动料理任务缺少完整的血池地狱双标签目标，已在执行游戏写操作前停止。";
            return false;
        }

        if (!IsValidYuumaFoodTargetPolicy(expectedPolicy, out var expectedPolicyError))
        {
            error = $"{expectedPolicyError} 已在执行游戏写操作前停止。";
            return false;
        }

        var expectedRevision = target.SpecialFoodTargetRevision;
        if (expectedRevision <= 0)
        {
            error = "自动料理任务缺少有效的血池地狱目标版本，已在执行游戏写操作前停止。";
            return false;
        }

        if (!RuntimeSpecialBusinessContextService.TryGetActiveYuumaFoodTargetState(
                out var currentPolicy,
                out var currentRevision)
            || currentPolicy == null
            || currentRevision <= 0)
        {
            error = "当前血池地狱目标版本尚不可用，已在执行游戏写操作前停止。";
            return false;
        }

        if (!expectedPolicy.HasSameIdentity(currentPolicy)
            || expectedRevision != currentRevision)
        {
            error = "当前血池地狱目标已变化，已在执行游戏写操作前停止并等待重新推荐。";
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
            error = $"血池地狱 BOSS 自动化需要正确的特殊经营类型、目标归属、全部匹配方式和两个完整目标标签；"
                + $"当前={DescribeSpecialFoodTargetPolicy(policy)}。";
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
            error = "血池地狱低收益推进方案只适用于目标信息完整的 BOSS 普客订单。";
            return false;
        }

        if (request.MatchFoodId < 0
            || request.MatchBeverageId < 0
            || request.FoodId != request.MatchFoodId
            || request.BeverageId != request.MatchBeverageId)
        {
            error = "血池地狱低收益推进方案必须使用原订单料理和酒水，不能替换订单项目。";
            return false;
        }

        if (!request.PredictedFoodTagsProvided)
        {
            error = "血池地狱低收益推进方案缺少完整的预计料理标签。";
            return false;
        }

        var normalizedPredictedTags = SpecialFoodTargetPolicy.NormalizeTags(
            request.PredictedFoodTags.Select(tag => FoodTags.NormalizeName(tag) ?? tag));
        if (policy.Matches(normalizedPredictedTags))
        {
            error = "预计料理标签已满足当前双标签，无需使用低收益推进方案。";
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

        message = $"{target.FoodName} 的预计标签（{string.Join("、", normalizedPredictedTags)}）不满足当前特殊目标 "
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
