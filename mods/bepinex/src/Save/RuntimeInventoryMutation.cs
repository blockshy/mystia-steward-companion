namespace MystiaStewardCompanion.Save;

/// <summary>
/// 库存修改的纯托管结果规则。外层负责精确条目类型、主线程及写入许可；本类仅在
/// 一次操作内读取当前数量、调用一次差量写入并回读，绝不重试或把未知读取当成零。
/// </summary>
internal static class RuntimeInventoryMutation
{
    internal const string InfiniteMessage = "该条目为无限库存，不能修改数量。";
    internal const string UnknownMessage = "未能读取有效的库存数量，本次未修改。";

    /// <summary>
    /// 当前 metadata 与 interop 的两个精确库存 getter 均返回 System.Int32。
    /// 空值、字符串、浮点数或其它类型表示协议/反射读取不匹配，不能宽松转换后写入。
    /// </summary>
    internal static int ReadQuantity(object? value) => value is int quantity
        ? quantity
        : throw new InvalidOperationException(UnknownMessage);

    internal static RuntimeInventoryEditResult SetQuantity(
        string itemType, int itemId, int quantity, Func<int> read, Action<int> applyDelta,
        int? expectedQuantity = null)
    {
        if (itemId < 0) throw new ArgumentOutOfRangeException(nameof(itemId));
        if (expectedQuantity < 0) throw new ArgumentOutOfRangeException(nameof(expectedQuantity));
        var target = Math.Clamp(quantity, 0, 9999);
        var previous = read();
        if (previous < 0) return Result(previous, previous,
            previous == -1 ? InfiniteMessage : UnknownMessage);
        // 快捷加减以客户端最后看到的数量为前提。比较必须与原生差量写入位于同一次
        // 主线程操作内；否则 HTTP 排队期间的游戏消耗会被旧的绝对目标覆盖。失配只
        // 返回当前实读值，不自动重算/重放，既有不带前提的绝对设置仍保持原语义。
        if (expectedQuantity.HasValue && previous != expectedQuantity.Value)
            return Result(previous, previous,
                $"库存数量已变化（原为 {expectedQuantity.Value}，当前为 {previous}），本次未修改。请刷新后再调整。");
        if (previous == target) return Result(previous, previous, null);

        // 不因客户端曾显示有限库存就跳过当前值验证；执行差量后必须回读目标数量。
        // 回读不同表示部分执行或未生效，不能因 changed=true 就包装为成功。
        applyDelta(target - previous);
        int current;
        try { current = read(); }
        catch (Exception ex)
        {
            // 写入已调用，读取异常不能复用前置读取的“本次未修改”结论。
            // 不猜测实际数量、不宣称回滚，交由用户刷新核对，且绝不重放差量。
            throw new InvalidOperationException(
                "库存写入后未能确认当前数量，库存可能已改变。请刷新核对，不要重复提交。", ex);
        }
        return Result(previous, current, current == target ? null
            : "库存写入未完整确认："
                + (current == -1 ? "当前为无限库存。"
                    : current < -1 ? "当前数量未知。"
                    : $"目标数量 {target}，实际数量 {current}。")
                + "请刷新后核对，不要重复提交。否定结果不代表已回滚此前变更。");

        RuntimeInventoryEditResult Result(int before, int after, string? error) => new()
        {
            ItemType = itemType, ItemId = itemId, RequestedQuantity = target,
            PreviousQuantity = before, Quantity = after, Changed = before != after, Error = error,
        };
    }
}

public sealed class RuntimeInventoryEditResult
{
    public string ItemType { get; init; } = "";
    public int ItemId { get; init; }
    public int RequestedQuantity { get; init; }
    public int PreviousQuantity { get; init; }
    public int Quantity { get; init; }
    public bool Changed { get; init; }
    public string? Error { get; init; }
}

public sealed class RuntimeInventoryBulkEditResult
{
    public string ItemType { get; init; } = "";
    public int RequestedQuantity { get; init; }
    public int Total { get; init; }
    public int Changed { get; init; }
    public int Unchanged { get; init; }
    public int Failed { get; init; }
    public List<string> Errors { get; init; } = new();
}
