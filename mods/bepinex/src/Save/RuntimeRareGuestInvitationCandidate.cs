namespace MystiaStewardCompanion.Save;

internal sealed record RuntimeRareGuestInvitationCandidate(
    object Guest,
    int CanonicalGuestId,
    int RuntimeId,
    string RuntimeName,
    string DisplayName,
    IReadOnlyList<string> SceneLabels,
    IReadOnlyList<string> SceneNames,
    bool IsCurrentScene,
    bool AvailabilityKnown,
    bool RuntimeAvailable,
    string AvailabilityReason,
    bool KizunaStateKnown,
    int KizunaLevel);

internal static class RuntimeRareGuestInvitationCandidates
{
    public static IReadOnlyList<RuntimeRareGuestInvitationCandidate> Deduplicate(
        IEnumerable<RuntimeRareGuestInvitationCandidate> candidates)
    {
        return candidates
            .GroupBy(candidate => candidate.CanonicalGuestId)
            .Select(MergeGroup)
            .OrderBy(candidate => candidate.CanonicalGuestId)
            .ThenBy(candidate => candidate.RuntimeName, StringComparer.Ordinal)
            .ToList();
    }

    private static RuntimeRareGuestInvitationCandidate MergeGroup(
        IGrouping<int, RuntimeRareGuestInvitationCandidate> group)
    {
        var items = group.ToList();
        var best = items
            .OrderByDescending(candidate => candidate.IsCurrentScene && candidate.RuntimeAvailable)
            .ThenByDescending(candidate => candidate.RuntimeAvailable)
            .ThenBy(candidate => candidate.AvailabilityKnown && !candidate.RuntimeAvailable ? 1 : 0)
            .ThenBy(candidate => candidate.RuntimeId == candidate.CanonicalGuestId ? 0 : 1)
            .ThenBy(candidate => candidate.RuntimeId)
            .ThenBy(candidate => candidate.RuntimeName, StringComparer.Ordinal)
            .First();
        var knownCandidates = items
            .Where(candidate => candidate.AvailabilityKnown)
            .ToList();
        var runtimeAvailable = knownCandidates.Count == 0
            || knownCandidates.Any(candidate => candidate.RuntimeAvailable);

        return best with
        {
            SceneLabels = items
                .SelectMany(candidate => candidate.SceneLabels)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SceneNames = items
                .SelectMany(candidate => candidate.SceneNames)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList(),
            IsCurrentScene = items.Any(candidate => candidate.IsCurrentScene),
            AvailabilityKnown = knownCandidates.Count > 0,
            RuntimeAvailable = runtimeAvailable,
            AvailabilityReason = runtimeAvailable ? "" : best.AvailabilityReason,
        };
    }
}
