namespace MystiaStewardCompanion.Save;

/// <summary>
/// 原生界面列表的一项完整值。Value只在本次主线程同步事务内借用，用于原样回写；
/// 身份、编号与数量用于前后核验，不把库存数量或配方内容改造成排序数据。
/// </summary>
internal readonly record struct RuntimeUiListOrderItem(object Value, nint EntityPointer, int EntityId, int Quantity);

/// <summary>仅允许对已确认的原列表读取及替换索引项，不提供清空、增删或替换整个列表的接口。</summary>
internal interface IRuntimeUiListOrderAccess
{
    nint Identity { get; }
    int Count { get; }
    RuntimeUiListOrderItem Read(int index);
    void Write(int index, RuntimeUiListOrderItem item);
}

/// <summary>
/// 对有界原列表执行稳定置换。目标项按最早关联订单排序，其余项保持原生相对顺序；
/// 所有验证和写入都在调用方持有的发布租约内同步完成，不持有跨帧游戏对象。
/// </summary>
internal static class RuntimeUiListOrderMutation
{
    internal const int MaximumCount = 512;

    internal static bool TryReorder(IRuntimeUiListOrderAccess access,
        Func<RuntimeUiListOrderItem, long> readPriority, Func<bool> isCurrent, out string failure)
    {
        failure = "";
        RuntimeUiListOrderItem[]? original = null;
        nint identity = 0;
        var wrote = false;
        try
        {
            if (!isCurrent()) throw new InvalidOperationException("list order context is stale before read");
            identity = access.Identity;
            var count = access.Count;
            if (identity == 0 || count is < 0 or > MaximumCount)
                throw new InvalidOperationException("list order identity/count is invalid");
            original = new RuntimeUiListOrderItem[count];
            var ranks = new long[count];
            var order = new int[count];
            var seen = new HashSet<nint>();
            for (var index = 0; index < count; index++)
            {
                var item = access.Read(index);
                if (item.Value == null || item.EntityPointer == 0 || item.EntityId < 0 || !seen.Add(item.EntityPointer))
                    throw new InvalidOperationException("list order contains missing or ambiguous item identity");
                original[index] = item;
                order[index] = index;
            }
            // 配方行映射以整张列表已证实完整为前提，不能在后续未知行尚未读取时先查询部分方案。
            for (var index = 0; index < count; index++)
            {
                var priority = readPriority(original[index]);
                if (priority <= 0) throw new InvalidOperationException("list order priority is not a positive lifecycle");
                ranks[index] = priority;
            }
            // 原索引作为第二排序键，确保同一订单内材料与非目标项沿用游戏既有顺序。
            Array.Sort(order, (left, right) =>
            {
                var comparison = ranks[left].CompareTo(ranks[right]);
                return comparison != 0 ? comparison : left.CompareTo(right);
            });
            if (!isCurrent() || !Matches(access, identity, original, null))
                throw new InvalidOperationException("list order context or original list changed before write");
            var changed = false;
            for (var index = 0; index < count; index++) changed |= order[index] != index;
            if (!changed) return true;
            for (var index = 0; index < count; index++)
            {
                if (order[index] == index) continue;
                // 标记在调用前设置；原生setter可能在完成写入后抛异常，也必须进入回退核验。
                wrote = true;
                access.Write(index, original[order[index]]);
            }
            if (!isCurrent() || !Matches(access, identity, original, order))
                throw new InvalidOperationException("list order publication or written list changed during commit");
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.GetBaseException().Message;
            if (wrote && original != null)
            {
                try { Restore(access, identity, original); }
                catch (Exception restore) { failure += "; restore refused/failed: " + restore.GetBaseException().Message; }
            }
            return false;
        }
    }

    private static bool Same(RuntimeUiListOrderItem left, RuntimeUiListOrderItem right)
        => left.EntityPointer == right.EntityPointer && left.EntityId == right.EntityId && left.Quantity == right.Quantity;

    private static bool Matches(IRuntimeUiListOrderAccess access, nint identity,
        RuntimeUiListOrderItem[] original, int[]? order)
    {
        if (access.Identity != identity || access.Count != original.Length) return false;
        for (var index = 0; index < original.Length; index++)
            if (!Same(access.Read(index), original[order == null ? index : order[index]])) return false;
        return access.Identity == identity && access.Count == original.Length;
    }

    private static void Restore(IRuntimeUiListOrderAccess access, nint identity, RuntimeUiListOrderItem[] original)
    {
        if (access.Identity != identity || access.Count != original.Length)
            throw new InvalidOperationException("native list identity/count changed; old items must not be restored");
        // 置换尚未完成时可以暂时重复元素，但不得覆盖外部插入的陌生项或修改后的库存数量。
        // 仅全部当前项仍属于本次原始快照时恢复；否则停止写入，交由下一次原生刷新重建。
        for (var index = 0; index < original.Length; index++)
        {
            var current = access.Read(index);
            if (!original.Any(item => Same(item, current)))
                throw new InvalidOperationException("native list has foreign or changed items during restore");
        }
        for (var index = 0; index < original.Length; index++) access.Write(index, original[index]);
        if (!Matches(access, identity, original, null)) throw new InvalidOperationException("native list restore readback mismatch");
    }
}
