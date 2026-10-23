namespace MystiaStewardCompanion.Save;

/// <summary>
/// 将游戏图鉴的完整整数列表复制为纯托管快照。这里只确认收录事实，不读取或修改库存，
/// 也不根据名称、数量、目录是否存在推断已解锁。所有读取必须在同一次主线程调用内完成。
/// </summary>
internal static class RuntimeBeverageUnlockProjection
{
    private const int MaximumRecordedBeverages = 4096;

    /// <summary>
    /// 就绪必须由调用方已完成的运行态读取证明。数量超界、负 ID、任一索引失败或前后
    /// 数量改变，都返回未知并丢弃整个临时结果；不得把前半部分误报为完整的未解锁集合。
    /// 游戏会合并基础存档与 DLC 收录列表，因此重复 ID 在完整读取后按集合语义去重。
    /// </summary>
    public static bool TryRead(
        bool runtimeReadReady,
        Func<int> readCount,
        Func<int, int> readAt,
        out int[] ids)
    {
        ids = Array.Empty<int>();
        if (!runtimeReadReady) return false;

        try
        {
            var count = readCount();
            if (count < 0 || count > MaximumRecordedBeverages) return false;

            var recorded = new HashSet<int>();
            for (var index = 0; index < count; index++)
            {
                var id = readAt(index);
                if (id < 0) return false;
                recorded.Add(id);
            }

            if (readCount() != count) return false;
            ids = recorded.OrderBy(id => id).ToArray();
            return true;
        }
        catch (Exception)
        {
            // 图鉴是独立的只读补充信息。异常意味着“未知”，不能使已成功的库存读取失败，
            // 更不能把失败前复制的部分列表交给客户端作为完整的解锁事实。
            return false;
        }
    }
}
