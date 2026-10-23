using MystiaStewardCompanion.Save;

/// <summary>
/// 验证酒水图鉴完整读取的纯托管边界，不创建或调用游戏 IL2CPP 对象。
/// 使用会抛错和改变数量的读取委托模拟真实集合不完整，确保不会发布半份“已解锁”。
/// </summary>
internal static class BeverageUnlockProjectionChecks
{
    private static int _assertions;

    public static void Run()
    {
        _assertions = 0;
        var reads = 0;
        Check(!RuntimeBeverageUnlockProjection.TryRead(false,
            () => { reads++; return 1; }, _ => { reads++; return 1; }, out var ids),
            "未就绪必须拒绝读取");
        Check(reads == 0 && ids.Length == 0, "未就绪不触碰原生读取委托");

        var source = new[] { 7, 0, 3, 7 };
        Check(RuntimeBeverageUnlockProjection.TryRead(true, () => source.Length, index => source[index], out ids),
            "完整基础/DLC 列表可以读取");
        Check(ids.SequenceEqual(new[] { 0, 3, 7 }), "ID 保留零、去重并稳定排序");
        source[0] = 99;
        Check(ids.SequenceEqual(new[] { 0, 3, 7 }), "结果不得引用原列表");
        ids[0] = 88;
        Check(source[1] == 0, "调用者修改结果不得污染原列表");

        reads = 0;
        Check(RuntimeBeverageUnlockProjection.TryRead(true, () => 0,
            _ => { reads++; return 1; }, out ids) && ids.Length == 0,
            "真正初始化后的空图鉴是完整结果");
        Check(reads == 0, "空列表不访问不存在的索引");

        Check(RuntimeBeverageUnlockProjection.TryRead(true, () => 4096, index => index, out ids)
            && ids.Length == 4096 && ids[4095] == 4095, "允许有界最大完整列表");
        reads = 0;
        Check(!RuntimeBeverageUnlockProjection.TryRead(true, () => 4097,
            _ => { reads++; return 1; }, out ids), "超上限不得截断为成功");
        Check(reads == 0 && ids.Length == 0, "超限在读取元素前停止");
        Check(!RuntimeBeverageUnlockProjection.TryRead(true, () => -1, _ => 1, out ids)
            && ids.Length == 0, "负数量是未知而非空图鉴");
        Check(!RuntimeBeverageUnlockProjection.TryRead(true, () => 2, index => index == 0 ? 5 : -1, out ids)
            && ids.Length == 0, "负 ID 撤销此前复制的完整性");

        Check(!RuntimeBeverageUnlockProjection.TryRead(true,
            () => throw new InvalidOperationException("count unavailable"), _ => 1, out ids)
            && ids.Length == 0, "初始数量读取失败不抛到库存链");
        Check(!RuntimeBeverageUnlockProjection.TryRead(true, () => 3,
            index => index == 2 ? throw new InvalidOperationException("entry unavailable") : index,
            out ids) && ids.Length == 0, "中途索引失败不发布部分数据");

        var countReads = 0;
        Check(!RuntimeBeverageUnlockProjection.TryRead(true,
            () => ++countReads == 1 ? 2 : 3, index => index, out ids) && ids.Length == 0,
            "读取期间增长视为不完整");
        countReads = 0;
        Check(!RuntimeBeverageUnlockProjection.TryRead(true,
            () => ++countReads == 1 ? 2 : 1, index => index, out ids) && ids.Length == 0,
            "读取期间缩小视为不完整");
        countReads = 0;
        Check(!RuntimeBeverageUnlockProjection.TryRead(true,
            () => ++countReads == 1 ? 2 : throw new InvalidOperationException("final count unavailable"),
            index => index, out ids) && ids.Length == 0, "结束确认失败不得发布结果");

        // 同一个收录 ID 可以完全没有库存；投影根本不接收数量，不能因库存清零而丢解锁。
        var quantities = new Dictionary<int, int> { [7] = 0, [8] = -1 };
        Check(RuntimeBeverageUnlockProjection.TryRead(true, () => 1, _ => 7, out ids)
            && ids.SequenceEqual(new[] { 7 }), "收录事实不依赖零库存或无限库存");
        Check(quantities[7] == 0 && quantities[8] == -1, "只读图鉴不得修改库存");
        Console.WriteLine($"PASS: beverage unlock projection {_assertions} assertions.");
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
