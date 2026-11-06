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

/// <summary>依据调度器已读取的日间可见性事实判定 NPC 是否可出现，不读取或修改运行时对象。</summary>
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

        // 时间窗口转换为距 18 点的剩余半小时行动数；两个边界都包含在可见范围内。
        var firstVisibleAction = 2 * (18 - input.ShowTimeStart);
        var lastVisibleAction = 2 * (18 - input.ShowTimeEnd);
        return input.RemainActions <= firstVisibleAction
            && input.RemainActions >= lastVisibleAction;
    }
}
