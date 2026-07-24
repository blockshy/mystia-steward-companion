namespace MystiaStewardCompanion.Save;

internal readonly record struct RuntimeTrackedNpcAvailabilityInput(
    bool HasOverridePosition,
    bool HasNormalIdentity,
    bool ShouldShowSpecialGuestsInDay,
    string CurrentSpawnMarker,
    string HiddenSpawnMarker,
    bool OpenStatus,
    int RestDays,
    int ShowTimeStart,
    int ShowTimeEnd,
    int RemainActions);

internal static class RuntimeTrackedNpcAvailability
{
    public static bool Evaluate(RuntimeTrackedNpcAvailabilityInput input)
    {
        if (input.HasOverridePosition) return true;
        if (!input.HasNormalIdentity && !input.ShouldShowSpecialGuestsInDay) return false;
        if (string.Equals(input.CurrentSpawnMarker, input.HiddenSpawnMarker, StringComparison.Ordinal)
            || !input.OpenStatus
            || input.RestDays != 0)
        {
            return false;
        }

        var firstVisibleAction = 2 * (18 - input.ShowTimeStart);
        var lastVisibleAction = 2 * (18 - input.ShowTimeEnd);
        return input.RemainActions <= firstVisibleAction
            && input.RemainActions >= lastVisibleAction;
    }
}
