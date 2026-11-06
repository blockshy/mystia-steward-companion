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
                // 此处仅接收已验证 ServeInWork 定义；-1 是明确的黑暗料理目标，
                // 并非缺失字段的默认值。未知负数仍拒绝整个信号集合，避免错误任务写入。
                if (foodId < -1)
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
