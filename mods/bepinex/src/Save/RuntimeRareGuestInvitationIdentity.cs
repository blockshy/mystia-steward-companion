namespace MystiaStewardCompanion.Save;

/// <summary>区分邀请列表运行时编号与稀客目录编号；只有来源客人和 NPC 角色编号一致时才能建立映射。</summary>
internal readonly record struct RuntimeRareGuestInvitationIdentity(
    int RuntimeId,
    int CanonicalGuestId)
{
    public static RuntimeRareGuestInvitationIdentity Resolve(
        int runtimeId,
        int sourceGuestId,
        int npcCharacterId,
        string runtimeStringId)
    {
        if (runtimeId < 0) throw new ArgumentOutOfRangeException(nameof(runtimeId));
        if (sourceGuestId < 0) throw new ArgumentOutOfRangeException(nameof(sourceGuestId));
        if (npcCharacterId < 0) throw new ArgumentOutOfRangeException(nameof(npcCharacterId));
        if (string.IsNullOrWhiteSpace(runtimeStringId))
        {
            throw new ArgumentException("Runtime StringId is required.", nameof(runtimeStringId));
        }

        if (npcCharacterId != sourceGuestId)
        {
            throw new InvalidOperationException(
                $"NPC '{runtimeStringId}' character ID {npcCharacterId} "
                + $"does not match source guest {sourceGuestId} (runtime ID {runtimeId}).");
        }

        return new RuntimeRareGuestInvitationIdentity(runtimeId, npcCharacterId);
    }
}
