namespace MystiaStewardCompanion.Save;

/// <summary>
/// 当前地图的只读位置快照。只接受已经过类型校验的 trackedNPCs 精确地图键结果，
/// 不枚举地图或场景对象，也不从角色目录、名称或可能目的地推测实时位置。
/// 全部范围的展示读取允许失败降级；当前范围仍必须严格成功，不能扩大原有候选集合。
/// </summary>
internal sealed record RuntimeRareGuestInvitationLocationSnapshot(bool IsKnown, object? Map, string Error)
{
    public static RuntimeRareGuestInvitationLocationSnapshot Capture(
        Func<(bool Found, object? Map)> readCurrentMap,
        bool required)
    {
        try
        {
            var (found, map) = readCurrentMap();
            if (found && map == null)
                throw new InvalidOperationException("Current tracked-NPC map returned null.");
            return new(true, found ? map : null, "");
        }
        catch (Exception ex) when (!required)
        {
            return new(false, null, ex.GetType().Name);
        }
    }

    /// <summary>
    /// 以映射目录提供的 runtimeStringId 查询已捕获地图。未命中仅表示当前地图没有
    /// 登记，不证明客人在其它地图；读取失败单独标为 unavailable，不伪造否定结论。
    /// 返回的 TrackedNpc 仅供当前范围既有可见性检查使用，不产生任何写入。
    /// </summary>
    public RuntimeRareGuestInvitationLocation Locate(
        string runtimeStringId,
        Func<object, string, (bool Found, object? Npc)> lookup,
        bool required)
    {
        if (!IsKnown) return new(false, false, null);
        if (Map == null) return new(true, false, null);
        try
        {
            var (found, npc) = lookup(Map, runtimeStringId);
            if (found && npc == null)
                throw new InvalidOperationException("Current tracked-NPC entry returned null.");
            return new(true, found, found ? npc : null);
        }
        catch (Exception) when (!required)
        {
            return new(false, false, null);
        }
    }
}

internal readonly record struct RuntimeRareGuestInvitationLocation(
    bool IsKnown, bool IsCurrentScene, object? TrackedNpc)
{
    public string Status => !IsKnown ? "unavailable"
        : IsCurrentScene ? "current-map" : "not-in-current-map";
}
