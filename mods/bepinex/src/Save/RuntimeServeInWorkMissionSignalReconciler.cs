namespace MystiaStewardCompanion.Save;

internal sealed record RuntimeServeInWorkMissionReconcileDefinition(
    string Receiver,
    IReadOnlyList<int> FoodIds,
    bool Fulfilled);

internal static class RuntimeServeInWorkMissionSignalReconciler
{
    public static bool TryBuildActiveSignalKeys(
        IReadOnlyCollection<RuntimeServeInWorkMissionReconcileDefinition> definitions,
        Func<string, int?> resolveCanonicalGuestId,
        out IReadOnlyCollection<RuntimeServeInWorkMissionSignalKey> activeSignals)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(resolveCanonicalGuestId);
        activeSignals = Array.Empty<RuntimeServeInWorkMissionSignalKey>();

        var keys = new HashSet<RuntimeServeInWorkMissionSignalKey>();
        foreach (var definition in definitions)
        {
            if (definition == null)
            {
                return false;
            }
            if (definition.Fulfilled)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(definition.Receiver)
                || definition.FoodIds == null)
            {
                return false;
            }

            var canonicalGuestId = resolveCanonicalGuestId(definition.Receiver);
            if (canonicalGuestId is not >= 0)
            {
                return false;
            }

            foreach (var foodId in definition.FoodIds)
            {
                if (foodId < 0)
                {
                    return false;
                }
                keys.Add(new RuntimeServeInWorkMissionSignalKey(
                    canonicalGuestId.Value,
                    foodId));
            }
        }

        activeSignals = keys.ToArray();
        return true;
    }
}
