using MystiaStewardCompanion.Save;

/// <summary>
/// 仅在显式合成媒体宿主中使用的库存夹具。复用生产读写回读规则与生产 HTTP 包装，
/// 无真实游戏类型、文件或原生调用；9902无限、9903有限、9904已达目标，身份固定可审计。
/// </summary>
internal static class SyntheticInventoryFixture
{
    /// <summary>实读旗标必须影响正式快照签名，否则客户端可能沿用旧的可写性。</summary>
    internal static void VerifySnapshotSignature()
    {
        var method = typeof(MystiaStewardCompanion.Ui.StewardOverlayController).GetMethod(
            "BuildLocalApiSnapshotContentSignature", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing production snapshot signature.");
        string Signature(bool ready) => (string)method.Invoke(null, new object[]
        {
            new MystiaStewardCompanion.LocalApi.LocalApiSnapshot
            {
                RecommendationState = new MystiaStewardCompanion.LocalApi.RecommendationStateSnapshot
                {
                    InventoryReadReady = ready,
                    OwnedBeverageQty = new Dictionary<int, int> { [0] = 99 },
                },
            },
        })!;
        if (Signature(false) == Signature(true))
            throw new InvalidOperationException("库存来源改变未使正式snapshot签名失效。");
    }

    internal static bool Contains(int id) => id is 9902 or 9903 or 9904;

    internal static RuntimeInventoryEditResult Set(string type, int id, int quantity, int? expectedQuantity = null)
    {
        if (!Contains(id)) throw new ArgumentOutOfRangeException(nameof(id));
        var current = id == 9902 ? -1 : id == 9904 ? 99 : 3;
        return RuntimeInventoryMutation.SetQuantity(type, id, quantity, () => current,
            delta => current += delta, expectedQuantity);
    }

    internal static RuntimeInventoryBulkEditResult SetAll(string type, IReadOnlyList<int> ids, int quantity)
    {
        var results = ids.Select(id => Set(type, id, quantity)).ToArray();
        return new RuntimeInventoryBulkEditResult
        {
            ItemType = type, RequestedQuantity = quantity, Total = results.Length,
            Changed = results.Count(result => result.Error == null && result.Changed),
            Unchanged = results.Count(result => result.Error == null && !result.Changed),
            Failed = results.Count(result => result.Error != null),
            Errors = results.Where(result => result.Error != null)
                .Select(result => $"#{result.ItemId}: {result.Error}").ToList(),
        };
    }
}
