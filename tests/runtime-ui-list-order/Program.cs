using MystiaStewardCompanion.Save;

// 本专项运行正式列表排序器、正式目标声明及生命周期优先级查询。
// 预期次序直接来自手工列出的订单，不调用排序器自身生成预期结果。
var assertions = 0;
void Check(bool value, string message)
{
    assertions++;
    if (!value) throw new InvalidOperationException(message);
}
RuntimeUiTargetSnapshot Target(int slot, long lifecycle, int recipe, int beverage, int[] ingredients,
    bool listEnabled = true, bool variant = false) => new(
    slot % 2 == 0 ? RuntimeUiTargetKind.Rare : RuntimeUiTargetKind.Normal,
    new RuntimeTargetHighlightColor((byte)(20 + slot * 20), 100, 200),
    listEnabled, variant, false, false, !listEnabled,
    (slot % 2 == 0 ? "R-" : "N-") + lifecycle,
    slot % 2 == 0 ? "" : "ptr:" + lifecycle.ToString("x"),
    lifecycle, slot, recipe, ingredients, variant ? ingredients.TakeLast(1) : Array.Empty<int>(),
    beverage, 1, "fixture:" + lifecycle, slot);
RuntimeUiTargetSetSnapshot Set(long generation, params RuntimeUiTargetSnapshot[] targets) => new(generation, 1, targets);
bool Reorder(ProbeList list, RuntimeUiTargetSetSnapshot targets,
    Func<RuntimeUiTargetSetSnapshot, int, RuntimeUiTargetKinds> claims, out string failure)
    => RuntimeUiListOrderMutation.TryReorder(list,
        item => targets.GetOrderPriority(claims(targets, item.EntityId)), () => true, out failure);
void SameRows(ProbeList list, RuntimeUiListOrderItem[] original, string message)
{
    Check(list.Items.Count == original.Length, message + "：数量改变");
    foreach (var row in original)
        Check(list.Items.Count(item => item.EntityPointer == row.EntityPointer
            && item.EntityId == row.EntityId && item.Quantity == row.Quantity
            && ReferenceEquals(item.Value, row.Value)) == 1, message + "：原对象、ID或库存数量改变");
}
void Order(ProbeList list, int[] expected, string message)
    => Check(list.Items.Select(item => item.EntityId).SequenceEqual(expected), message);

// 八笔订单的颜色槽、客人种类、商品ID、输入数组均刻意不与点单顺序一致。
var ids = new[] { 92, 14, 81, 13, 75, 61, 37, 20 };
var slots = new[] { 5, 0, 7, 2, 1, 6, 3, 4 };
var all = Enumerable.Range(0, 8).Select(index => Target(slots[index], 100 + index,
    ids[index], ids[index], new[] { ids[index] })).ToArray();
var sourceIds = new[] { 600, 37, 13, 14, 500, 75, 92, 20, 81, 61, 700 };
foreach (var category in new[] { "料理", "食材", "酒水" })
{
    Func<RuntimeUiTargetSetSnapshot, int, RuntimeUiTargetKinds> claims = category switch
    {
        "料理" => (target, id) => target.GetRecipeClaims(id),
        "食材" => (target, id) => target.GetIngredientClaims(id),
        _ => (target, id) => target.GetBeverageClaims(id),
    };
    var list = ProbeList.From(sourceIds); var original = list.Items.ToArray();
    var targets = Set(1, all.Reverse().ToArray());
    Check(Reorder(list, targets, claims, out var failure), category + "八单排序失败：" + failure);
    Order(list, new[] { 92, 14, 81, 13, 75, 61, 37, 20, 600, 500, 700 }, category + "未按跨客类点单顺序置顶");
    SameRows(list, original, category + "排序修改了数据");

    // 第九笔复用第一笔的槽位和物品；按固定色槽/ID排列的旧实现会把92留在第一位。
    var replacement = Target(slots[0], 109, 92, 92, new[] { 92 });
    var next = Set(2, all.Skip(1).Append(replacement).Reverse().ToArray());
    Check(Reorder(list, next, claims, out failure), category + "复用空槽排序失败：" + failure);
    Order(list, new[] { 14, 81, 13, 75, 61, 37, 20, 92, 600, 500, 700 }, category + "新单复用色槽后没有排在旧单之后");
    var writes = list.WriteCount;
    Check(Reorder(list, Set(3, all.Skip(1).Append(replacement).ToArray()), claims, out failure)
        && list.WriteCount == writes, category + "重复刷新或输入数组重排导致重复写入");
    SameRows(list, original, category + "复用颜色后的对象/库存改变");
}

// 同一物品被多个订单使用时，位置由最早仍需该物品的订单决定；送达一单不删库存行。
var sharedList = ProbeList.From(300, 11, 94, 200);
var sharedEarly = Target(0, 10, 94, 94, new[] { 94 });
var sharedMiddle = Target(5, 20, 11, 11, new[] { 11 });
var sharedLate = Target(2, 30, 94, 94, new[] { 94 });
Check(Reorder(sharedList, Set(4, sharedLate, sharedMiddle, sharedEarly), (set, id) => set.GetBeverageClaims(id), out var sharedFailure), sharedFailure);
Order(sharedList, new[] { 94, 11, 300, 200 }, "共享物品没有采用最早声明");
Check(Reorder(sharedList, Set(5, sharedLate, sharedMiddle), (set, id) => set.GetBeverageClaims(id), out sharedFailure), sharedFailure);
Order(sharedList, new[] { 11, 94, 300, 200 }, "最早声明完成后共享物品未按剩余订单调整位置");

// 一单的多个食材优先级相同，保留游戏原来的相对次序；无目标材料仍保留任务/收藏次序。
var sameOrderList = ProbeList.From(888, 23, 999, 22, 21, 111);
Check(Reorder(sameOrderList, Set(6, Target(0, 40, 2, 3, new[] { 21, 22, 23 })),
    (set, id) => set.GetIngredientClaims(id), out var sameFailure), sameFailure);
Order(sameOrderList, new[] { 23, 22, 21, 888, 999, 111 }, "同单食材或无目标项原生相对顺序改变");

// 基础配方与不同加料变体允许拥有同一个料理ID；优先级通过精确行声明传入，不能按ID去重。
var recipeObjects = ProbeList.From(50, 50, 60, 50);
var recipeOriginal = recipeObjects.Items.ToArray();
var recipeTargets = Set(7, Target(0, 30, 50, 1, new[] { 1 }),
    Target(1, 10, 50, 1, new[] { 1, 2 }, variant: true), Target(2, 20, 60, 1, new[] { 1 }));
var rowClaims = new Dictionary<nint, RuntimeUiTargetKinds>
{
    [recipeOriginal[0].EntityPointer] = RuntimeUiTargetKinds.Rare,
    [recipeOriginal[1].EntityPointer] = RuntimeUiTargetKinds.Normal,
    [recipeOriginal[2].EntityPointer] = RuntimeUiTargetKinds.Slot2,
    [recipeOriginal[3].EntityPointer] = RuntimeUiTargetKinds.None,
};
Check(RuntimeUiListOrderMutation.TryReorder(recipeObjects,
    item => recipeTargets.GetOrderPriority(rowClaims[item.EntityPointer]), () => true, out var recipeFailure), recipeFailure);
Check(recipeObjects.Items.Select(item => item.EntityPointer).SequenceEqual(new[]
    { recipeOriginal[1].EntityPointer, recipeOriginal[2].EntityPointer, recipeOriginal[0].EntityPointer, recipeOriginal[3].EntityPointer }),
    "相同料理ID的精确变体声明被混合或跨配方顺序错误");
SameRows(recipeObjects, recipeOriginal, "配方变体排序修改原对象");

// 无目标、列表辅助关闭、空列表均不写入；游戏下一轮自身重建列表时自然恢复原生次序。
foreach (var targets in new[] { Set(8), Set(9, Target(0, 41, 2, 2, new[] { 2 }, listEnabled: false)) })
{
    var untouched = ProbeList.From(3, 2, 1);
    Check(Reorder(untouched, targets, (set, id) => set.GetRecipeClaims(id), out var failure)
        && untouched.WriteCount == 0, "无目标或关闭列表辅助仍写入：" + failure);
    Order(untouched, new[] { 3, 2, 1 }, "无目标时破坏原生顺序");
}
var empty = ProbeList.From();
Check(RuntimeUiListOrderMutation.TryReorder(empty, _ => long.MaxValue, () => true, out var emptyFailure)
    && empty.WriteCount == 0, "空列表应保持无写入：" + emptyFailure);

// 异常输入必须在第一次写入之前拒绝；身份不明时不猜测替代条目。
var invalidCases = new List<ProbeList>();
var zeroIdentity = ProbeList.From(3, 2, 1); zeroIdentity.Identity = 0; invalidCases.Add(zeroIdentity);
var negativeCount = ProbeList.From(3, 2, 1); negativeCount.CountOverride = -1; invalidCases.Add(negativeCount);
var tooMany = ProbeList.From(3, 2, 1); tooMany.CountOverride = 513; invalidCases.Add(tooMany);
var missingObject = ProbeList.From(3, 2, 1); missingObject.Items[1] = missingObject.Items[1] with { Value = null! }; invalidCases.Add(missingObject);
var missingPointer = ProbeList.From(3, 2, 1); missingPointer.Items[1] = missingPointer.Items[1] with { EntityPointer = 0 }; invalidCases.Add(missingPointer);
var negativeId = ProbeList.From(3, 2, 1); negativeId.Items[1] = negativeId.Items[1] with { EntityId = -1 }; invalidCases.Add(negativeId);
var duplicatedPointer = ProbeList.From(3, 2, 1); duplicatedPointer.Items[1] = duplicatedPointer.Items[0]; invalidCases.Add(duplicatedPointer);
foreach (var invalid in invalidCases)
    Check(!RuntimeUiListOrderMutation.TryReorder(invalid, item => item.EntityId, () => true, out var failure)
        && failure.Length > 0 && invalid.WriteCount == 0, "非法列表未在写入前拒绝");
var incompleteSnapshot = ProbeList.From(3, 2, 1);
incompleteSnapshot.Items[2] = incompleteSnapshot.Items[2] with { EntityPointer = 0 };
var priorityReads = 0;
Check(!RuntimeUiListOrderMutation.TryReorder(incompleteSnapshot, item => { priorityReads++; return item.EntityId; },
    () => true, out _) && priorityReads == 0, "尚未核定完整列表便调用配方优先级映射");
foreach (var invalidPriority in new[] { 0L, -1L })
{
    var invalidRank = ProbeList.From(3, 2, 1);
    Check(!RuntimeUiListOrderMutation.TryReorder(invalidRank, _ => invalidPriority, () => true, out _)
        && invalidRank.WriteCount == 0, "非法生命周期优先级仍写列表");
}

var failedRead = ProbeList.From(3, 2, 1); failedRead.BeforeRead = (_, _) => throw new InvalidOperationException("read failure");
Check(!RuntimeUiListOrderMutation.TryReorder(failedRead, item => item.EntityId, () => true, out _)
    && failedRead.WriteCount == 0, "读取异常仍写列表");
var failedPriority = ProbeList.From(3, 2, 1);
Check(!RuntimeUiListOrderMutation.TryReorder(failedPriority, _ => throw new InvalidOperationException("priority failure"),
    () => true, out _) && failedPriority.WriteCount == 0, "优先级解析异常仍写列表");

// 目标在读取后过期、列表身份或数量在读取途中改变时，不能把旧排列写回新表面。
var stale = ProbeList.From(3, 2, 1); var currentChecks = 0;
Check(!RuntimeUiListOrderMutation.TryReorder(stale, item => item.EntityId, () => ++currentChecks == 1, out _)
    && stale.WriteCount == 0, "目标在读取后过期仍写入");
var replaced = ProbeList.From(3, 2, 1); replaced.BeforeRead = (list, index) => { if (index == 0) list.Identity = 777; };
Check(!RuntimeUiListOrderMutation.TryReorder(replaced, item => item.EntityId, () => true, out _)
    && replaced.WriteCount == 0, "列表指针变化仍写入");
var enlarged = ProbeList.From(3, 2, 1); enlarged.BeforeRead = (list, index) =>
{
    if (index != 0) return;
    list.BeforeRead = null; list.Items.Add(ProbeList.Row(99));
};
Check(!RuntimeUiListOrderMutation.TryReorder(enlarged, item => item.EntityId, () => true, out _)
    && enlarged.WriteCount == 0 && enlarged.Items.Count == 4, "列表新增条目被旧排序覆盖");

// 原生set_Item可以在写入前抛错、写完后抛错或静默漏写；所有情形都必须读回核验并恢复原序。
foreach (var mode in new[] { "before", "after", "silent" })
{
    var failing = ProbeList.From(3, 2, 1); var original = failing.Items.ToArray();
    failing.FailureMode = mode;
    Check(!RuntimeUiListOrderMutation.TryReorder(failing, item => item.EntityId, () => true, out var failure)
        && failure.Length > 0, mode + "写失败被报告成成功");
    Check(failing.Items.SequenceEqual(original), mode + "写失败未恢复完整原序");
}
var invalidatedDuringWrite = ProbeList.From(3, 2, 1); var beforeInvalidated = invalidatedDuringWrite.Items.ToArray();
var writeCurrent = true; invalidatedDuringWrite.AfterWrite = (_, _) => writeCurrent = false;
Check(!RuntimeUiListOrderMutation.TryReorder(invalidatedDuringWrite, item => item.EntityId, () => writeCurrent, out _)
    && invalidatedDuringWrite.Items.SequenceEqual(beforeInvalidated), "写入期间目标失效没有撤回本次排序");

// 外部列表新增成员后不允许无条件回滚，以免恢复旧序时删除用户刚产生的新数据。
var externalMutation = ProbeList.From(3, 2, 1); var foreign = ProbeList.Row(99);
externalMutation.AfterWrite = (list, index) =>
{
    list.AfterWrite = null; list.Items[1] = foreign;
    throw new InvalidOperationException("external replacement");
};
Check(!RuntimeUiListOrderMutation.TryReorder(externalMutation, item => item.EntityId, () => true, out var externalFailure)
    && externalFailure.Length > 0 && externalMutation.Items.Contains(foreign), "外部替换后回滚覆盖了新成员");

NativeBridgeChecks.Run(Check);
Console.WriteLine($"PASS runtime UI list order: {assertions} assertions; production mutation, target priority and direct native-shaped postfix bridges; no Harmony/native game execution.");

/// <summary>可观测列表边界：只实现读、写、身份与数量；故障注入不含任何排序逻辑。</summary>
internal sealed class ProbeList : IRuntimeUiListOrderAccess
{
    private static long _nextPointer = 100;
    internal List<RuntimeUiListOrderItem> Items { get; } = new();
    public nint Identity { get; set; } = (nint)Interlocked.Increment(ref _nextPointer);
    public int Count => CountOverride ?? Items.Count;
    internal int? CountOverride;
    internal int WriteCount;
    internal string FailureMode = "";
    internal Action<ProbeList, int>? BeforeRead;
    internal Action<ProbeList, int>? AfterWrite;
    internal static RuntimeUiListOrderItem Row(int id)
        => new(new object(), (nint)Interlocked.Increment(ref _nextPointer), id, id % 5 + 1);
    internal static ProbeList From(params int[] ids)
    {
        var result = new ProbeList(); result.Items.AddRange(ids.Select(Row)); return result;
    }
    public RuntimeUiListOrderItem Read(int index)
    {
        BeforeRead?.Invoke(this, index);
        return Items[index];
    }
    public void Write(int index, RuntimeUiListOrderItem item)
    {
        WriteCount++;
        var failure = FailureMode; FailureMode = "";
        if (failure == "before") throw new InvalidOperationException("write before failure");
        if (failure != "silent") Items[index] = item;
        if (failure == "after") throw new InvalidOperationException("write after failure");
        AfterWrite?.Invoke(this, index);
    }
}
