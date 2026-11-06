namespace MystiaStewardCompanion.Save;

/// <summary>
/// Maps a deferred rare cooking job back to the exact participation lifecycle that owns its
/// native order binding.
/// </summary>
/// <remarks>
/// Cooker ownership generations identify physical cooker contents and are deliberately absent
/// from this policy. Participation authority is scoped only by the immutable business generation
/// carried by the exact native order binding.
/// </remarks>
internal static class RuntimeRareCookingJobParticipationPolicy
{
    public static RuntimeRareGuestParticipationOrderIdentity CreateIdentityFromExactBinding(
        RuntimeOrderBindingToken exactBinding,
        string traceId,
        int guestId)
    {
        return new RuntimeRareGuestParticipationOrderIdentity(
            exactBinding.BusinessGeneration,
            traceId,
            exactBinding.LifecycleSequence,
            guestId);
    }

    public static bool IsRosterAlignedWithExactBinding(
        RuntimeRareGuestParticipationSnapshot participation,
        RuntimeOrderBindingToken exactBinding,
        IReadOnlyList<int> configuredManagedGuestIds)
    {
        ArgumentNullException.ThrowIfNull(participation);
        ArgumentNullException.ThrowIfNull(configuredManagedGuestIds);

        return participation.IsActive
            && participation.BusinessGeneration == exactBinding.BusinessGeneration
            && participation.ManagedGuestIds.Count == configuredManagedGuestIds.Count
            && configuredManagedGuestIds.All(participation.ManagedGuestIds.Contains);
    }
}
