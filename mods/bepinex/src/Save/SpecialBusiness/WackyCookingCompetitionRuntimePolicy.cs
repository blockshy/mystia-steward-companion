using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal static class WackyCookingCompetitionRuntimePolicy
{
    public const string ChallengeType = SpecialBusinessChallengeTypes.WackyCookingCompetition;
    public const string KoishiBossRole = SpecialBusinessOrderRoles.WackyKoishiBoss;
    public const int MaxRecentRejectedRecipeKeys = 128;

    public static bool IsChallengeContext(string challengeType, string executionReason, string targetSignature)
    {
        return string.Equals(challengeType, ChallengeType, StringComparison.Ordinal)
            || executionReason.IndexOf("怪诞", StringComparison.OrdinalIgnoreCase) >= 0
            || !string.IsNullOrWhiteSpace(targetSignature);
    }

    public static bool IsKoishiBossRole(string role)
    {
        return string.Equals(role, KoishiBossRole, StringComparison.Ordinal);
    }

    public static bool RequiresLiveKoishiBossController(string role, bool isPhaseThree, bool shieldBroken)
    {
        return IsKoishiBossRole(role)
            && isPhaseThree
            && shieldBroken;
    }

    public static string DescribeKoishiExecutionMode(string role, bool isPhaseThree, bool shieldBroken)
    {
        if (!IsKoishiBossRole(role)) return "koishiExecutionMode=none";
        if (!isPhaseThree)
        {
            return $"koishiExecutionMode=inactive; capturePolicy=standard; shieldBroken={shieldBroken}";
        }

        return shieldBroken
            ? "koishiExecutionMode=full-feed; capturePolicy=live-controller-only; shieldBroken=True"
            : "koishiExecutionMode=clue-stage; capturePolicy=validated-capture-allowed; shieldBroken=False";
    }

    public static string DescribeKoishiEvaluationMode(string role, bool requiresNativeEvaluation)
    {
        if (!IsKoishiBossRole(role)) return "koishiEvaluationMode=standard";
        return requiresNativeEvaluation
            ? "koishiEvaluationMode=native-evaluate-entry"
            : "koishiEvaluationMode=generic-evaluate";
    }

    public static string BuildCaptureSkippedDiagnostic(string prefix, string executionMode)
    {
        return $"{prefix}={SpecialBusinessOrderRoles.WackyKoishiBoss} full-feed requires live controller; {executionMode}";
    }

    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags)
    {
        return (tags ?? Array.Empty<string>())
            .Select(tag => FoodTags.NormalizeName(tag) ?? tag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();
    }

    public static string BuildRejectedRecipeKey(
        int foodId,
        int recipeId,
        IEnumerable<int> extraIngredientIds,
        IEnumerable<string> targetTags)
    {
        if (foodId < 0) return "";

        var normalizedTags = NormalizeTags(targetTags);
        if (normalizedTags.Count == 0) return "";

        var normalizedExtras = extraIngredientIds
            .Where(id => id >= 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var resolvedRecipeId = recipeId >= 0 ? recipeId : foodId;
        return $"{string.Join("&", normalizedTags)}|food:{foodId}|recipe:{resolvedRecipeId}|extra:{string.Join(",", normalizedExtras)}";
    }
}
