namespace MystiaStewardCompanion.Save;

internal readonly record struct RareOrderIdentity(
    int? DeskCode,
    int? RuntimeGuestId,
    int? FoodTagId,
    int? BeverageTagId);

internal static class RareOrderIdentityMatcher
{
    public static bool IsSameCookingTarget(
        string? leftTraceId,
        int leftFoodId,
        RareOrderIdentity leftIdentity,
        string? rightTraceId,
        int rightFoodId,
        RareOrderIdentity rightIdentity)
    {
        if (leftFoodId != rightFoodId) return false;

        var leftHasTrace = !string.IsNullOrWhiteSpace(leftTraceId);
        var rightHasTrace = !string.IsNullOrWhiteSpace(rightTraceId);
        if (leftHasTrace != rightHasTrace)
        {
            return false;
        }

        if (leftHasTrace && !string.Equals(leftTraceId, rightTraceId, StringComparison.Ordinal))
        {
            return false;
        }

        return Matches(leftIdentity, rightIdentity, out _);
    }

    public static bool Matches(
        RareOrderIdentity expected,
        RareOrderIdentity candidate,
        out string rejectReason)
    {
        if (!MatchesRequiredValue("request desk", expected.DeskCode, out rejectReason)
            || !MatchesRequiredValue("candidate desk", candidate.DeskCode, out rejectReason))
        {
            return false;
        }

        if (expected.DeskCode != candidate.DeskCode)
        {
            rejectReason = $"desk mismatch candidate={Format(candidate.DeskCode)}, expected={Format(expected.DeskCode)}";
            return false;
        }

        if (!MatchesRequiredValue("request runtime guest id", expected.RuntimeGuestId, out rejectReason)
            || !MatchesRequiredValue("candidate runtime guest id", candidate.RuntimeGuestId, out rejectReason))
        {
            return false;
        }

        if (expected.RuntimeGuestId != candidate.RuntimeGuestId)
        {
            rejectReason = $"runtime guest id mismatch candidate={Format(candidate.RuntimeGuestId)}, expected={Format(expected.RuntimeGuestId)}";
            return false;
        }

        if (!MatchesRequiredValue("request food tag id", expected.FoodTagId, out rejectReason)
            || !MatchesRequiredValue("candidate food tag id", candidate.FoodTagId, out rejectReason))
        {
            return false;
        }

        if (expected.FoodTagId != candidate.FoodTagId)
        {
            rejectReason = $"food tag id mismatch candidate={Format(candidate.FoodTagId)}, expected={Format(expected.FoodTagId)}";
            return false;
        }

        if (!MatchesRequiredValue("request beverage tag id", expected.BeverageTagId, out rejectReason)
            || !MatchesRequiredValue("candidate beverage tag id", candidate.BeverageTagId, out rejectReason))
        {
            return false;
        }

        if (expected.BeverageTagId != candidate.BeverageTagId)
        {
            rejectReason = $"beverage tag id mismatch candidate={Format(candidate.BeverageTagId)}, expected={Format(expected.BeverageTagId)}";
            return false;
        }

        rejectReason = "identity matched";
        return true;
    }

    public static bool IsExecutableCapturedOrder(
        bool hasOrderObject,
        bool hasControllerObject,
        bool? isFulfilled,
        bool isOwnedByController,
        bool allowFulfilled,
        out string rejectReason)
    {
        if (!hasOrderObject)
        {
            rejectReason = "captured order object missing";
            return false;
        }

        if (!hasControllerObject)
        {
            rejectReason = "captured controller object missing";
            return false;
        }

        if (!isFulfilled.HasValue)
        {
            rejectReason = "captured fulfilled state missing";
            return false;
        }

        if (isFulfilled.Value && !allowFulfilled)
        {
            rejectReason = "captured order fulfilled";
            return false;
        }

        if (!isOwnedByController)
        {
            rejectReason = "captured order is not owned by controller";
            return false;
        }

        rejectReason = "captured order executable";
        return true;
    }

    public static string Format(RareOrderIdentity identity)
    {
        return $"desk={Format(identity.DeskCode)},runtimeGuestId={Format(identity.RuntimeGuestId)},foodTagId={Format(identity.FoodTagId)},beverageTagId={Format(identity.BeverageTagId)}";
    }

    private static bool MatchesRequiredValue(string label, int? value, out string rejectReason)
    {
        if (value.HasValue)
        {
            rejectReason = "";
            return true;
        }

        rejectReason = $"{label} missing";
        return false;
    }

    private static string Format(int? value)
    {
        return value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing";
    }
}
