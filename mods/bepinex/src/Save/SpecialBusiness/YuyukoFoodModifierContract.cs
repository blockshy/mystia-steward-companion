namespace MystiaStewardCompanion.Save;

/// <summary>
/// Defines the semantic food-modifier identity used by the Yuyuko retake NormalOrder gate.
/// SparrowSeries adds tag -30 to the finished Sellable and consumes it through its own cooker
/// evaluation path. It is therefore infrastructure provenance, not a guest-facing modifier.
/// </summary>
internal static class YuyukoFoodModifierContract
{
    internal const int SparrowSeriesCookerMarkerTagId = -30;

    internal static IReadOnlyList<int> BuildRetakeNormalOrderModifierTagIds(
        IEnumerable<int> finalTagIds,
        IEnumerable<int> rawTagIds)
    {
        ArgumentNullException.ThrowIfNull(finalTagIds);
        ArgumentNullException.ThrowIfNull(rawTagIds);

        var rawTagSet = rawTagIds.ToHashSet();
        return finalTagIds
            .Where(tagId => !rawTagSet.Contains(tagId))
            .Where(tagId => tagId != SparrowSeriesCookerMarkerTagId)
            .Distinct()
            .OrderBy(tagId => tagId)
            .ToArray();
    }

    internal static bool HasAddedSparrowSeriesCookerMarker(
        IReadOnlyCollection<int> finalTagIds,
        IReadOnlyCollection<int> rawTagIds)
    {
        ArgumentNullException.ThrowIfNull(finalTagIds);
        ArgumentNullException.ThrowIfNull(rawTagIds);
        return finalTagIds.Contains(SparrowSeriesCookerMarkerTagId)
            && !rawTagIds.Contains(SparrowSeriesCookerMarkerTagId);
    }
}
