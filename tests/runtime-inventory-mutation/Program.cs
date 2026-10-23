using MystiaStewardCompanion.Save;

var checks = 0;
void Check(bool value, string message)
{
    checks++;
    if (!value) throw new InvalidOperationException(message);
}

// 模拟的读取与差量委托记录实际副作用次数；不是仅断言文案或复制实现公式。
foreach (var count in new[] { -1, -2 })
{
    var writes = 0;
    var result = RuntimeInventoryMutation.SetQuantity("beverage", 0, 99, () => count, _ => writes++);
    Check(writes == 0 && !result.Changed && result.Quantity == count, "不可写库存不能调用原生写入。");
    Check(result.Error == (count == -1 ? RuntimeInventoryMutation.InfiniteMessage : RuntimeInventoryMutation.UnknownMessage),
        "无限与未知必须给出准确原因。");
}
foreach (var value in new object?[] { null, "0", 0.0, 0L, new object() })
{
    var rejected = false;
    try { RuntimeInventoryMutation.ReadQuantity(value); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "非Int32读取不能伪装为零库存。");
}
Check(RuntimeInventoryMutation.ReadQuantity(0) == 0, "原生明确零库存保持可编辑。");
Check(RuntimeInventoryMutation.ReadQuantity(-1) == -1, "无限哨兵必须原样保留。");
foreach (var pair in new[] { (Before: 0, Target: 99), (Before: 99, Target: 0), (Before: 5, Target: 5) })
{
    var current = pair.Before;
    var writes = 0;
    var reads = 0;
    var result = RuntimeInventoryMutation.SetQuantity("ingredient", 7, pair.Target,
        () => { reads++; return current; }, delta => { writes++; current += delta; });
    Check(result.Error == null && result.Quantity == pair.Target && result.ItemId == 7, "有限库存必须回读准确目标。");
    Check(writes == (pair.Before == pair.Target ? 0 : 1) && reads == writes + 1, "未变不写、变更仅写一次并回读。");
}
{
    var reads = 0;
    var writes = 0;
    var result = RuntimeInventoryMutation.SetQuantity("beverage", 8, 99,
        () => ++reads == 1 ? 2 : 3, _ => writes++);
    Check(result.Changed && result.Error != null && result.Quantity == 3, "部分执行不能误报成功或虚构回滚。");
    Check(writes == 1, "未知写入结果不得自动重试。");
}
{
    var writes = 0;
    var rejected = false;
    try { RuntimeInventoryMutation.SetQuantity("ingredient", 1, 8,
        () => RuntimeInventoryMutation.ReadQuantity(null), _ => writes++); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected && writes == 0, "读取异常必须先于任何库存写入。");
}
{
    var reads = 0;
    var writes = 0;
    string? error = null;
    try { RuntimeInventoryMutation.SetQuantity("beverage", 2, 99,
        () => ++reads == 1 ? 3 : RuntimeInventoryMutation.ReadQuantity(null), _ => writes++); }
    catch (InvalidOperationException ex) { error = ex.Message; }
    Check(reads == 2 && writes == 1, "回读失败保留一次已执行写入，不重试。");
    Check(error?.Contains("库存可能已改变") == true && !error.Contains("本次未修改"),
        "写后读取异常不能假报未修改或回滚。");
}
// 快捷加减必须比较主线程实读值；模拟客户端20之后游戏消耗/补充，拒绝覆盖新数量。
// 特别覆盖当前量已等于旧目标：不能绕过比较而误报本次快捷操作成功。
foreach (var live in new[] { 15, 25, 30 })
{
    var reads = 0;
    var writes = 0;
    var result = RuntimeInventoryMutation.SetQuantity("ingredient", 7, 30,
        () => { reads++; return live; }, _ => writes++, expectedQuantity: 20);
    Check(writes == 0 && reads == 1 && !result.Changed && result.Quantity == live,
        "过期数量必须在写入前拒绝，不能夹带多增/多减，也不能当作相等目标成功。");
    Check(result.Error?.Contains("本次未修改") == true && result.Error.Contains("请刷新后再调整"),
        "数量冲突给出明确刷新指引，不建议自动重试。");
}
foreach (var delta in new[] { -10, 10 })
{
    var current = 20;
    var writes = new List<int>();
    var result = RuntimeInventoryMutation.SetQuantity("beverage", 7, 20 + delta,
        () => current, value => { writes.Add(value); current += value; }, expectedQuantity: 20);
    Check(result.Error == null && result.Quantity == 20 + delta && result.Changed,
        "比较成功后仍使用原精确库存读写链确认结果。");
    Check(writes.SequenceEqual(new[] { delta }), "单项加减必须恰好一次±10副作用。");
}
foreach (var live in new[] { -1, -2 })
{
    var writes = 0;
    var result = RuntimeInventoryMutation.SetQuantity("beverage", 8, 30,
        () => live, _ => writes++, expectedQuantity: 20);
    Check(writes == 0 && result.Error == (live == -1
        ? RuntimeInventoryMutation.InfiniteMessage : RuntimeInventoryMutation.UnknownMessage),
        "比较前提不能绕过无限/未知库存保护。");
}
{
    var reads = 0;
    var writes = 0;
    var rejected = false;
    try { RuntimeInventoryMutation.SetQuantity("ingredient", 7, 10,
        () => { reads++; return 0; }, _ => writes++, expectedQuantity: -1); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected && reads == 0 && writes == 0, "非法数量前提不能退回无条件写入。");
}
Console.WriteLine($"runtime-inventory-mutation: PASS {checks} assertions");
