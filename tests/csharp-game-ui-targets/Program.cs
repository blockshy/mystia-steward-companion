using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.GameUi;
using MystiaStewardCompanion.Business.Domain.Orders;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

// 本专项直接调用生产目标服务：除对照旧 JSON 入口之外，还断言精确目标、拒绝条件和
// 输入/输出所有权，防止两个入口同时走错分支却仅凭“结果彼此相等”误报通过。
var checks = 0;
var root = Fixture();
var rare = Input(root, "rare");
var normal = Input(root, "normal");
AssertEquivalent(rare, "R-0001", 1501, 21);
AssertEquivalent(normal, "N-0001", 1501, 21);
Check(J.Str(GameUiTargetService.Evaluate(normal)?["orderKey"]) == "ptr:11", "普客输出保留精确原生订单键。");
Check(J.Str(GameUiTargetService.Evaluate(rare)?["orderKey"]) == "", "稀客输出不发明普客订单键。");
var rareTarget = GameUiTargetService.Evaluate(rare)!;
Check(rareTarget["ingredientIds"]!.ToJsonString() == "[1,2]", "目标基础/加料聚合仍按原规则去重排序。");
Check(rareTarget["extraIngredientIds"]!.ToJsonString() == "[2,2]", "原加料序列及重复项不得因新入口丢失。");

// 任务排序必须验证完整的订单来源，并且不能让硬失败的任务料理越过正常第一单。
AssertEquivalent(rare with { PrioritizeMissionRecipe = true }, "R-0002", 1502, 21);
foreach (var mutation in new[] { "trace", "generation", "food", "hard-failure", "beverage" })
{
    var changed = Fixture();
    var second = J.Objects(changed["recommendations"]).ElementAt(1);
    var priority = J.Obj(second["order"]?["missionRecipePriority"]);
    if (mutation == "trace") priority["traceId"] = "R-0099";
    if (mutation == "generation") priority["businessGeneration"] = 0;
    if (mutation == "food") priority["foodId"] = 999;
    if (mutation == "hard-failure")
        second["executionPlans"]![0]!["conditionResults"] = new JsonArray(J.Object(("status", "fail"), ("severity", "hard")));
    if (mutation == "beverage") second["executionPlans"]![0]!["beverage"]!["meetsRequiredBeverage"] = false;
    AssertEquivalent(Input(changed, "rare") with { PrioritizeMissionRecipe = true }, "R-0001", 1501, 21);
}

// 强身份与送达条件仍逐次读取，不保留首次调用的选择结果。非法身份不能通过引用复用复活。
foreach (var kind in new[] { "rare", "normal" })
foreach (var mutation in new[] { "invalid-trace", "missing-lifecycle", "invalid-desk", "served-food", "served-beverage", "served-both" })
{
    var changed = Fixture();
    var order = kind == "rare"
        ? J.Obj(J.Objects(changed["recommendations"]).First()["order"])
        : J.Objects(changed["orders"]).Single();
    if (kind == "rare") J.Arr(changed["recommendations"]).RemoveAt(1);
    if (mutation == "invalid-trace") order["traceId"] = "invalid";
    if (mutation == "missing-lifecycle") order["orderLifecycleSequence"] = 0;
    if (mutation == "invalid-desk") order["deskCode"] = -1;
    if (mutation is "served-food" or "served-both") order["hasServedFood"] = true;
    if (mutation is "served-beverage" or "served-both") order["hasServedBeverage"] = true;
    var rejected = mutation is "invalid-trace" or "missing-lifecycle" or "invalid-desk" or "served-both";
    AssertEquivalent(Input(changed, kind), rejected ? null : kind == "rare" ? "R-0001" : "N-0001",
        mutation == "served-food" ? -1 : 1501, mutation == "served-beverage" ? -1 : 21);
}
foreach (var mutation in new[] { "evaluated", "zero-pointer", "bad-pointer", "missing-ingredient" })
{
    var changed = Fixture(); var order = J.Objects(changed["orders"]).Single();
    if (mutation == "evaluated") order["hasEvaluated"] = true;
    if (mutation == "zero-pointer") order["orderKey"] = "ptr:0";
    if (mutation == "bad-pointer") order["orderKey"] = "ptr:xyz";
    if (mutation == "missing-ingredient") J.Arr(J.Obj(changed["data"])["ingredients"]).Clear();
    AssertEquivalent(Input(changed, "normal"), null, 0, 0);
}

// 特殊普客目标必须匹配原点单和本轮线策略，不能把当前目录中的同名配方当作合法替身。
var specialRoot = Fixture();
var special = J.Object(("active", true), ("challengeTypeAvailable", true),
    ("challengeType", "Story_WackyCookingCompetition"), ("phase", "Phase 1"), ("foodTargetTags", new JsonArray("鲜")));
var specialOrder = J.Objects(specialRoot["orders"]).Single();
specialOrder["specialBusinessRole"] = "wacky-target-order";
var selectedTarget = J.Object(("matchFoodId", 501), ("matchBeverageId", 21),
    ("foodId", 502), ("recipeId", 1502), ("beverageId", 21), ("extraIngredientIds", new JsonArray(2)));
foreach (var field in SpecialBusinessRules.BuildWirePolicy(special, "wacky-target-order", 7))
    selectedTarget[field.Key] = J.Clone(field.Value);
specialRoot["specialBusiness"] = special;
specialRoot["executionTargets"] = new JsonArray(J.Object(("orderKey", OrderIdentityAndSorting.NormalKey(specialOrder)), ("target", selectedTarget)));
var currentSpecial = Input(specialRoot, "normal") with { ExecutionTargetsCurrent = true, BusinessGeneration = 7 };
AssertEquivalent(currentSpecial, "N-0001", 1502, 21);
AssertEquivalent(currentSpecial with { ExecutionTargetsCurrent = false }, null, 0, 0);
AssertEquivalent(currentSpecial with { BusinessGeneration = 8 }, null, 0, 0);
var staleSpecialRoot = J.Obj(J.Clone(specialRoot));
J.Obj(J.Objects(staleSpecialRoot["executionTargets"]).Single()["target"])["matchFoodId"] = 999;
AssertEquivalent(Input(staleSpecialRoot, "normal") with { ExecutionTargetsCurrent = true, BusinessGeneration = 7 }, null, 0, 0);

// JSON 边界的 options 覆盖优先级、来源/协调操作和异常行为保持原契约。
var legacyRare = Envelope(rare);
legacyRare["specialBusiness"] = J.Object(("active", true), ("challengeTypeAvailable", true), ("challengeType", "Story_Mizuchi"));
J.Obj(J.Objects(legacyRare["recommendations"]).First()["order"])["specialBusinessRole"] = "mizuchi-story-ordinary-order";
J.Obj(J.Objects(legacyRare["recommendations"]).ElementAt(1)["order"])["specialBusinessRole"] = "mizuchi-story-possessed-order";
J.Obj(legacyRare["options"])["specialBusiness"] = new JsonObject();
Check(J.Str(GameUiTargetService.Evaluate(legacyRare)?["traceId"]) == "R-0001", "稀客 options 的显式空对象仍优先于外层规则。");
J.Obj(legacyRare["options"])["specialBusiness"] = null;
Check(J.Str(GameUiTargetService.Evaluate(legacyRare)?["traceId"]) == "R-0002", "稀客 options 为 JSON null 时仍回退外层特殊经营排序。");
foreach (var kind in new[] { "rare", "normal" })
{
    var order = kind == "rare" ? J.Obj(J.Objects(root["recommendations"]).First()["order"]) : J.Objects(root["orders"]).Single();
    var source = GameUiTargetService.Evaluate(J.Object(("operation", kind + "-source"), ("order", order)))!;
    Check(J.Str(source["kind"]) == kind, "旧来源操作继续输出对应通道。");
    var target = GameUiTargetService.Evaluate(Input(root, kind));
    var reconciled = GameUiTargetService.Evaluate(J.Object(("operation", "reconcile"), ("target", target), ("sources", new JsonArray(J.Clone(source)))));
    Check(Json(reconciled) == Json(target), "旧 reconcile 保留未送达当前目标。");
}
// 库存黑暗料理没有配方，酒水已送达后仍必须保留订单/座位目标，直到同一订单确认送餐。
var storedRoot = J.Obj(J.Clone(root));
var storedRow = J.Objects(storedRoot["recommendations"]).First();
var storedOrder = J.Obj(storedRow["order"]);
storedOrder["hasServedBeverage"] = true;
var storedPlan = J.Objects(storedRow["executionPlans"]).First();
storedPlan["food"] = J.Object(("productKind", "dark-cuisine"), ("supplySource", "stored"),
    ("recipe", J.Object(("id", -1), ("recipeId", null), ("name", "黑暗料理"))), ("extraIngredients", new JsonArray()));
var storedTarget = GameUiTargetService.Evaluate(Input(storedRoot, "rare"));
Check(storedTarget != null && J.Num(storedTarget["recipeId"]) == -1 && J.Num(storedTarget["beverageId"]) == -1,
    "库存黑暗料理不伪造配方或未需要的酒水目标。");
Check(GameUiTargetService.Reconcile(storedTarget, new[] { GameUiTargetService.Source(storedOrder, false) }) != null,
    "没有配方的库存成品仍有待送餐订单，不得提前撤销高亮。");
storedOrder["hasServedFood"] = true;
Check(GameUiTargetService.Reconcile(storedTarget, new[] { GameUiTargetService.Source(storedOrder, false) }) == null,
    "精确来源确认料理和酒水均已送达后撤销库存成品目标。");

Throws(() => GameUiTargetService.Evaluate(J.Object(("operation", "unknown"))), "payload");
Throws(() => GameUiTargetService.Evaluate(rare with { Operation = "unknown" }), "input");

// 构造器与服务都不能从原父节点移走输入，输出被消费者修改后也不能反向污染冻结快照。
var before = root.ToJsonString();
var dataParent = rare.Data.Parent; var rowsParent = rare.Recommendations!.Parent;
var result = GameUiTargetService.Evaluate(rare)!;
J.Obj(result["features"])["orderHighlightEnabled"] = false;
J.Arr(result["extraIngredientIds"]).Add(999);
result["recipeName"] = "调用者修改";
Check(before == root.ToJsonString(), "修改输出不能改变任何输入节点。");
Check(ReferenceEquals(dataParent, rare.Data.Parent) && ReferenceEquals(rowsParent, rare.Recommendations.Parent), "只读入口保持父节点所有权。");
var expected = Json(GameUiTargetService.Evaluate(rare));
var outputs = new string[24];
Parallel.For(0, outputs.Length, index => outputs[index] = Json(GameUiTargetService.Evaluate(rare)));
Check(outputs.All(value => value == expected), "同一冻结输入可被并行读取，结果彼此独立且稳定。");
Check(before == root.ToJsonString(), "并行只读不得修改输入。");

// 多订单从完整主方案投影，八个同类订单不能被种类去重；身份重复必须仅拒绝歧义目标。
var multi = Fixture();
var sourceRare = J.Obj(J.Arr(multi["recommendations"])[0]);
var manyRare = new JsonArray(); var manyNormal = new JsonArray();
for (var index = 0; index < 8; index++)
{
    var row = J.Obj(J.Clone(sourceRare)); var order = J.Obj(row["order"]);
    order["traceId"] = $"R-{index + 10}"; order["orderLifecycleSequence"] = index + 20;
    order["deskCode"] = index; manyRare.Add(row);
    var normalOrder = J.Obj(J.Clone(J.Arr(multi["orders"])[0]));
    normalOrder["traceId"] = $"N-{index + 10}"; normalOrder["orderLifecycleSequence"] = index + 40;
    normalOrder["orderKey"] = $"ptr:{index + 100:x}"; normalOrder["deskCode"] = index; manyNormal.Add(normalOrder);
}
multi["recommendations"] = manyRare; multi["orders"] = manyNormal;
Check(GameUiTargetService.EvaluateAll(Input(multi, "rare")).Count == 8, "八个稀客均保留独立目标。");
Check(GameUiTargetService.EvaluateAll(Input(multi, "normal")).Count == 8, "八个普客均保留独立目标。");
manyRare.Add(J.Clone(manyRare[0]));
Check(GameUiTargetService.EvaluateAll(Input(multi, "rare")).Count == 7, "重复稀客身份不能任选第一条。");
manyRare.RemoveAt(8);
// 列表置顶消费全部未送达酒水，不能只传一笔订单或误把相同酒水理解为多个库存条目。
// 使用各自不同的真实目录ID，避免「八笔订单恰好都点同一种酒」掩盖单目标截断。
var distinctDrinks = J.Obj(J.Clone(multi));
J.Obj(distinctDrinks["data"])["beverages"] = J.Array(Enumerable.Range(21, 8)
    .Select(id => J.Object(("id", id), ("name", "测试酒水" + id))));
for (var index = 0; index < 8; index++)
{
    J.Obj(J.Arr(distinctDrinks["orders"])[index])["beverageId"] = 21 + index;
    J.Obj(J.Arr(J.Arr(distinctDrinks["recommendations"])[index]?["executionPlans"])[0]?["beverage"])["beverage"]
        = J.Object(("id", 21 + index), ("name", "测试酒水" + (21 + index)));
}
foreach (var kind in new[] { "rare", "normal" })
{
    Check(GameUiTargetService.EvaluateAll(Input(distinctDrinks, kind))
        .Select(target => (int)J.Num(target["beverageId"])).OrderBy(id => id).SequenceEqual(Enumerable.Range(21, 8)),
        "多笔" + kind + "订单的待取酒水未全部进入游戏辅助。");
}
J.Obj(J.Arr(distinctDrinks["orders"])[1])["hasServedBeverage"] = true;
J.Obj(J.Arr(distinctDrinks["recommendations"])[1]?["order"])["hasServedBeverage"] = true;
foreach (var kind in new[] { "rare", "normal" })
{
    var targetsForDrinks = GameUiTargetService.EvaluateAll(Input(distinctDrinks, kind));
    Check(targetsForDrinks.Count == 8 && targetsForDrinks.Count(target => J.Num(target["beverageId"]) == -1) == 1
        && targetsForDrinks.Where(target => J.Num(target["beverageId"]) >= 0)
            .Select(target => (int)J.Num(target["beverageId"])).OrderBy(id => id)
            .SequenceEqual(Enumerable.Range(21, 8).Where(id => id != 22)),
        "送酒只撤回对应酒水声明，不截断其他订单或其待做料理。");
}
var palette = J.Object(("rareOrderHighlightPalette", new JsonArray("#F2CA5C", "#EF856D", "#E785B4")),
    ("normalOrderHighlightPalette", new JsonArray("#5AA9E6", "#57C7B6", "#9A8FE8")));
var allocator = new OrderHighlightAssignmentService();
var rareOrders = J.Array(J.Objects(manyRare).Select(row => row["order"]));
var assigned = allocator.Project(100, rareOrders, new JsonArray(), palette, true, true);
Check(assigned.Count == 3 && J.Objects(assigned).Select(row => J.Num(row["colorSlot"])).Distinct().Count() == 3,
    "八个同类订单仅前三笔获得三色辅助，不得复用颜色扩展到后续订单。");
Check(J.Objects(assigned).Select(row => J.Str(row["color"])).Distinct().Count() == 3,
    "每类一种颜色只能对应一笔辅助订单。");
var originalSlots = J.Objects(assigned).ToDictionary(row => J.Str(row["traceId"]), row => J.Num(row["colorSlot"]));
var reversed = allocator.Project(100, J.Array(J.Objects(rareOrders).Reverse()), new JsonArray(), palette, true, true);
Check(J.Objects(reversed).All(row => originalSlots[J.Str(row["traceId"])] == J.Num(row["colorSlot"])), "排序改变不换色。");
var retained = J.Array(J.Objects(rareOrders).Skip(1));
var old = J.Obj(J.Clone(J.Arr(rareOrders)[0])); old["traceId"] = "R-99"; old["orderLifecycleSequence"] = 999;
retained.Add(old);
var replaced = allocator.Project(100, retained, new JsonArray(), palette, true, true);
Check(J.Objects(replaced).All(row => J.Str(row["traceId"]) != "R-10")
    && J.Objects(replaced).Where(row => originalSlots.ContainsKey(J.Str(row["traceId"])))
        .All(row => originalSlots[J.Str(row["traceId"])] == J.Num(row["colorSlot"])), "旧单结束只释放其槽位，新同桌订单不改变其他订单。");
Check(replaced.Count == 3 && J.Objects(replaced).Any(row => J.Str(row["traceId"]) == "R-13"),
    "旧单结束后按原优先顺序补入下一单，不能跳过更早的候选。");
var nextPalette = J.Obj(J.Clone(palette)); nextPalette["rareOrderHighlightPalette"]![2] = "#112233";
var recolored = allocator.Project(100, retained, new JsonArray(), nextPalette, true, true);
Check(J.Objects(recolored).Single(row => J.Num(row["colorSlot"]) == 2)["color"]!.GetValue<string>() == "#112233", "编辑调色板仅改变对应槽位。");
Check(allocator.Project(0, retained, null, palette, true, true).Count == 0, "离开经营代次清空颜色声明。");
var newRound = allocator.Project(101, null, new JsonArray(J.Clone(manyNormal[0])), palette, true, true);
Check(newRound.Count == 1 && J.Num(newRound[0]?["colorSlot"]) == 1, "新经营代次重新建立普客颜色身份。");

// 颜色通道与原生声明槽分离：插入另一类订单、改变该类颜色数量，都不能影响已在场订单。
var independent = new OrderHighlightAssignmentService();
var twoPalettes = J.Object(("rareOrderHighlightPalette", new JsonArray("#112233", "#445566")),
    ("normalOrderHighlightPalette", new JsonArray("#778899", "#AABBCC")));
var mixedRare = J.Array(J.Objects(rareOrders).Take(4));
var mixedNormal = J.Array(J.Objects(manyNormal).Take(4));
var mixed = independent.Project(400, mixedRare, mixedNormal, twoPalettes, true, true);
Check(mixed.Count == 4 && J.Objects(mixed).Select(row => J.Num(row["colorSlot"])).Distinct().Count() == 4,
    "两类各两色只能分别辅助两单，全局声明槽仍不能冲突。");
foreach (var kind in new[] { "rare", "normal" })
{
    var expectedColors = kind == "rare" ? new[] { "#112233", "#445566" } : new[] { "#778899", "#AABBCC" };
    Check(J.Objects(mixed).Where(row => J.Str(row["kind"]) == kind).Select(row => J.Str(row["color"]))
        .SequenceEqual(expectedColors), "同类每色必须仅分配一单，不能循环复用。");
}
var rareBefore = J.Objects(mixed).Where(row => J.Str(row["kind"]) == "rare")
    .ToDictionary(row => J.Str(row["traceId"]), row => J.Str(row["color"]));
twoPalettes["normalOrderHighlightPalette"] = new JsonArray("#112233"); // 跨类别同色合法。
var normalOnlyChanged = independent.Project(400, mixedRare, mixedNormal, twoPalettes, true, true);
Check(J.Objects(normalOnlyChanged).Count(row => J.Str(row["kind"]) == "normal") == 1
    && J.Objects(normalOnlyChanged).Where(row => J.Str(row["kind"]) == "normal").All(row => J.Str(row["color"]) == "#112233")
    && J.Objects(normalOnlyChanged).Where(row => J.Str(row["kind"]) == "rare")
        .All(row => rareBefore[J.Str(row["traceId"])] == J.Str(row["color"])), "修改普客颜色或数量污染了稀客颜色。");
var afterComplete = independent.Project(400, J.Array(J.Objects(mixedRare).Skip(1)), mixedNormal, twoPalettes, true, true);
Check(J.Objects(afterComplete).Where(row => J.Str(row["kind"]) == "rare" && rareBefore.ContainsKey(J.Str(row["traceId"])))
    .All(row => rareBefore[J.Str(row["traceId"])] == J.Str(row["color"])), "一笔订单结束后仍入选的其他订单不得换色。");
Check(independent.ReadKnown(400, mixedRare, mixedNormal, twoPalettes).Count == 3,
    "只读旧帧不能恢复已完成的颜色声明。");

// 较早订单结束后，剩余两单保色，新订单使用释放的颜色，而不是挤动原有分配。
var churn = new OrderHighlightAssignmentService();
var fullColors = churn.Project(500, rareOrders, new JsonArray(), palette, true, true);
var laterOrders = J.Array(J.Objects(rareOrders).Skip(1).Take(2));
var remainingColors = churn.Project(500, laterOrders, new JsonArray(), palette, true, true);
var joined = J.Array(J.Objects(laterOrders));
joined.Add(J.Clone(old));
var joinedColors = churn.Project(500, joined, new JsonArray(), palette, true, true);
Check(J.Objects(joinedColors).Select(row => J.Str(row["color"])).Distinct().Count() == 3,
    "旧订单的高位颜色序号仍在时，新订单必须优先使用空闲色。");
Check(J.Objects(remainingColors).All(row => J.Objects(fullColors).Single(before => J.Str(before["traceId"]) == J.Str(row["traceId"]))["color"]!.GetValue<string>() == J.Str(row["color"]))
    && J.Objects(remainingColors).All(row => J.Objects(joinedColors).Single(after => J.Str(after["traceId"]) == J.Str(row["traceId"]))["color"]!.GetValue<string>() == J.Str(row["color"])),
    "释放和补入订单不能改变其他订单已经分配的颜色。");

// 合法辅助候选与原始订单分离：暂停、无方案或已送齐订单不能抢占有限颜色。
// 调用方提供的顺序代表已有任务/特殊经营/人工调度优先级，不能再次按追踪号重排。
var eligibleAllocator = new OrderHighlightAssignmentService();
var prioritized = J.Objects(rareOrders).Skip(3).Reverse().ToArray();
var selected = eligibleAllocator.Project(600, rareOrders, new JsonArray(), twoPalettes, true, true,
    rareCandidates: prioritized);
Check(J.Objects(selected).Select(row => J.Str(row["traceId"]))
    .SequenceEqual(prioritized.Take(2).Select(row => J.Str(row["traceId"]))),
    "辅助颜色必须分给合法候选的前两笔，不能被较早但不参与的原始订单抢占。");
var priorColors = J.Objects(selected).ToDictionary(row => J.Str(row["traceId"]), row => J.Str(row["color"]));
var priorityReversed = eligibleAllocator.Project(600, rareOrders, new JsonArray(), twoPalettes, true, true,
    rareCandidates: prioritized.Take(2).Reverse().ToArray());
Check(J.Objects(priorityReversed).All(row => priorColors[J.Str(row["traceId"])] == J.Str(row["color"])),
    "同一入选集合改变顺序不能交换已有订单的颜色。");
var oneColor = J.Obj(J.Clone(twoPalettes)); oneColor["rareOrderHighlightPalette"] = new JsonArray("#112233");
var shrunk = eligibleAllocator.Project(600, rareOrders, new JsonArray(), oneColor, true, true,
    rareCandidates: prioritized);
Check(shrunk.Count == 1 && J.Str(shrunk[0]?["color"]) == "#112233",
    "颜色缩减必须立即收紧辅助数，不能把越界颜色取模后复用。");
Check(eligibleAllocator.Project(600, rareOrders, new JsonArray(), oneColor, true, true,
    rareCandidates: Array.Empty<JsonObject>()).Count == 0,
    "全部候选暂停或失效时必须撤销颜色，不能从原始订单回填辅助。");
var bothThree = new OrderHighlightAssignmentService().Project(700, rareOrders, manyNormal, palette, true, true);
Check(bothThree.Count == 6 && J.Objects(bothThree).Select(row => J.Num(row["colorSlot"])).Distinct().Count() == 6,
    "两类各三色最多辅助六笔，全局声明必须保持唯一。");
// 模拟经营期间连续调整两类显示数：每次配置变化必须替换整个有效集合，不能遗留第三色。
var resizing = new OrderHighlightAssignmentService();
foreach (var count in new[] { 3, 2, 1, 2, 3, 2 })
{
    var resizedPalette = J.Object(("rareOrderHighlightPalette", J.Array(J.Strings(palette["rareOrderHighlightPalette"]).Take(count))),
        ("normalOrderHighlightPalette", J.Array(J.Strings(palette["normalOrderHighlightPalette"]).Take(count))));
    var resized = resizing.Project(701, rareOrders, manyNormal, resizedPalette, true, true);
    Check(resized.Count == count * 2 && J.Objects(resized).Select(row => J.Num(row["colorSlot"])).Distinct().Count() == count * 2,
        "两类同时收缩或扩大辅助数后，声明数量或身份不一致。");
    foreach (var kind in new[] { "rare", "normal" })
        Check(J.Objects(resized).Where(row => J.Str(row["kind"]) == kind).Select(row => J.Str(row["color"]))
            .OrderBy(color => color, StringComparer.Ordinal).SequenceEqual(
                J.Strings(resizedPalette[kind + "OrderHighlightPalette"]).OrderBy(color => color, StringComparer.Ordinal)),
            "数量调整后仍发布旧颜色，或借用了另一类别的颜色。");
    Check(resizing.ReadKnown(701, rareOrders, manyNormal, resizedPalette).ToJsonString() == resized.ToJsonString(),
        "数量调整后的只读展示与当前辅助集合不一致。");
}
foreach (var kind in new[] { "rare", "normal" })
{
    // 旧四组配置须先经配置仓迁移，不能绕过新协议直接进入业务分配器。
    var invalid = J.Obj(J.Clone(palette)); J.Arr(invalid[kind + "OrderHighlightPalette"]).Add("#123456");
    var rejected = false;
    try { new OrderHighlightAssignmentService().Project(700, rareOrders, manyNormal, invalid, true, true); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "当前业务协议不能接受未迁移的四组颜色。");
}

// 尚未发布的候选只能修改独立副本。让副本真正释放 A/B 并把两色分给 C/D，
// 再模拟最终当前性检查失败；不能仅用相同候选验证 Fork，以免漏掉字典共享问题。
var committedAssignments = new OrderHighlightAssignmentService();
var stagedRawOrders = J.Array(J.Objects(rareOrders).Take(4));
var stagedFirstPair = J.Objects(stagedRawOrders).Take(2).ToArray();
var stagedNextPair = J.Objects(stagedRawOrders).Skip(2).ToArray();
committedAssignments.Project(800, stagedRawOrders, new JsonArray(), twoPalettes, true, true,
    rareCandidates: stagedFirstPair);
var committedBeforeStage = committedAssignments.ReadKnown(800, stagedRawOrders, new JsonArray(), twoPalettes).ToJsonString();
var reorderedCommitted = committedAssignments.Project(800, stagedRawOrders, new JsonArray(), twoPalettes, true, true,
    rareCandidates: stagedFirstPair.Reverse().ToArray());
Check(reorderedCommitted.Count == 2
    && committedAssignments.ReadKnown(800, stagedRawOrders, new JsonArray(), twoPalettes).ToJsonString() == committedBeforeStage,
    "暂存前即使辅助队列反转，已提交订单的颜色和声明槽也应保持不变。");
var discardedAssignments = committedAssignments.Fork();
var discardedProjection = discardedAssignments.Project(800, stagedRawOrders, new JsonArray(), twoPalettes, true, true,
    rareCandidates: stagedNextPair);
Check(discardedProjection.Count == 2
    && J.Objects(discardedProjection).Select(row => J.Str(row["traceId"]))
        .SequenceEqual(stagedNextPair.Select(row => J.Str(row["traceId"])))
    && discardedAssignments.ReadKnown(800, J.Array(stagedFirstPair), new JsonArray(), twoPalettes).Count == 0,
    "被丢弃的暂存帧必须实际完成候选替换，不能用未发生变更的空夹具验证隔离。");
Check(committedAssignments.ReadKnown(800, stagedRawOrders, new JsonArray(), twoPalettes).ToJsonString() == committedBeforeStage,
    "丢弃过期候选副本后，原实例的已有颜色、声明槽和身份必须完全不变。");
var acceptedAssignments = committedAssignments.Fork();
var acceptedProjection = acceptedAssignments.Project(800, stagedRawOrders, new JsonArray(), twoPalettes, true, true,
    rareCandidates: stagedNextPair);
committedAssignments = acceptedAssignments; // 模拟宿主最终帧检查通过后采用暂存实例。
Check(committedAssignments.ReadKnown(800, stagedRawOrders, new JsonArray(), twoPalettes).ToJsonString() == acceptedProjection.ToJsonString()
    && committedAssignments.ReadKnown(800, J.Array(stagedFirstPair), new JsonArray(), twoPalettes).Count == 0,
    "只有显式采用副本，C/D 才能成为已提交分配，A/B 的旧颜色应同时释放。");
var committedBeforeGeneration = committedAssignments.ReadKnown(800, stagedRawOrders, new JsonArray(), twoPalettes).ToJsonString();
var discardedGeneration = committedAssignments.Fork();
Check(discardedGeneration.Project(801, stagedRawOrders, new JsonArray(), twoPalettes, true, true,
    rareCandidates: stagedFirstPair).Count == 2
    && discardedGeneration.ReadKnown(800, stagedRawOrders, new JsonArray(), twoPalettes).Count == 0,
    "新代次暂存必须确实切换自身代次并清除旧代次映射。");
Check(committedAssignments.ReadKnown(800, stagedRawOrders, new JsonArray(), twoPalettes).ToJsonString() == committedBeforeGeneration
    && committedAssignments.ReadKnown(801, stagedRawOrders, new JsonArray(), twoPalettes).Count == 0,
    "丢弃新代次副本不得清除已提交旧代次，也不得让原实例提前接受新代次。");

// 原始订单完整不代表人工调度集合完整。队列先反转而颜色不变，再模拟未知调度空集；
// 恢复后的队列若被重新分色会交换 A/B，因而此夹具能发现错误释放后重建的行为。
var participationAssignments = new OrderHighlightAssignmentService();
var participationRawContext = J.Object(("orders", stagedRawOrders), ("ordersReadComplete", true));
var participationEmptyContext = J.Object(("orders", new JsonArray()), ("ordersReadComplete", true));
participationAssignments.ProjectSnapshot(900, participationRawContext, participationEmptyContext, twoPalettes,
    rareCandidates: stagedFirstPair);
var participationBeforeUnknown = participationAssignments.ReadKnown(900, stagedRawOrders, new JsonArray(), twoPalettes).ToJsonString();
participationAssignments.ProjectSnapshot(900, participationRawContext, participationEmptyContext, twoPalettes,
    rareCandidates: stagedFirstPair.Reverse().ToArray());
var unknownParticipation = participationAssignments.ProjectSnapshot(900, participationRawContext, participationEmptyContext, twoPalettes,
    rareCandidates: Array.Empty<JsonObject>(), rareCandidatesReadComplete: false);
Check(unknownParticipation.Count == 0,
    "调度集合未知时必须隐藏本轮辅助目标，不能把保留的旧映射直接作为目标返回。");
Check(participationAssignments.ReadKnown(900, stagedRawOrders, new JsonArray(), twoPalettes).ToJsonString() == participationBeforeUnknown,
    "原始订单完整但调度候选未知时，只保留原有映射，不得将空候选误判为全部暂停。");
var recoveredParticipation = participationAssignments.ProjectSnapshot(900, participationRawContext, participationEmptyContext, twoPalettes,
    rareCandidates: stagedFirstPair.Reverse().ToArray(), rareCandidatesReadComplete: true);
Check(recoveredParticipation.Count == 2
    && participationAssignments.ReadKnown(900, stagedRawOrders, new JsonArray(), twoPalettes).ToJsonString() == participationBeforeUnknown,
    "未知调度恢复后应沿用 A/B 原有颜色和声明槽，不能按反转后的队列重新分色。");
var confirmedPaused = participationAssignments.Project(900, stagedRawOrders, new JsonArray(), twoPalettes, true, true,
    rareCandidates: Array.Empty<JsonObject>(), rareCandidatesReadComplete: true);
Check(confirmedPaused.Count == 0
    && participationAssignments.ReadKnown(900, stagedRawOrders, new JsonArray(), twoPalettes).Count == 0,
    "完整候选明确全部暂停时必须释放映射，未知保护不得令已暂停订单永久占用颜色。");
var afterConfirmedPause = participationAssignments.Project(900, stagedRawOrders, new JsonArray(), twoPalettes, true, true,
    rareCandidates: stagedNextPair, rareCandidatesReadComplete: true);
Check(afterConfirmedPause.Count == 2
    && J.Objects(afterConfirmedPause).Select(row => J.Str(row["color"])).SequenceEqual(new[] { "#112233", "#445566" }),
    "明确暂停释放的两色应立即供后续 C/D 使用，不能继续被旧订单占用。");

// 缺失/失败/部分订单不是“确定无人”；旧槽位仅保留身份，旧订单不参与本轮显示或游戏目标。
var preservedAllocator = new OrderHighlightAssignmentService();
var threeRare = J.Array(J.Objects(rareOrders).Take(3));
var completeRare = J.Object(("orders", threeRare), ("ordersReadComplete", true));
var completeEmpty = J.Object(("orders", new JsonArray()), ("ordersReadComplete", true));
var known = preservedAllocator.ProjectSnapshot(200, completeRare, completeEmpty, palette);
var knownSlots = J.Objects(known).ToDictionary(row => J.Str(row["traceId"]), row => J.Num(row["colorSlot"]));
foreach (var missing in new JsonNode?[] { null, new JsonObject(), new JsonArray(),
    J.Object(("orders", new JsonArray()), ("ordersReadComplete", false)),
    J.Object(("orders", new JsonArray()), ("error", "读取失败")),
    J.Object(("orders", new JsonArray(new JsonObject())), ("ordersReadComplete", true)),
    J.Object(("orders", "unavailable"), ("ordersReadComplete", true)) })
{
    Check(preservedAllocator.ProjectSnapshot(200, missing, completeEmpty, palette).Count == 0,
        "未知/异常上下文不可重新显示旧订单。");
    var recovered = preservedAllocator.ProjectSnapshot(200,
        J.Object(("orders", J.Array(J.Objects(threeRare).Reverse())), ("ordersReadComplete", true)), completeEmpty, palette);
    Check(J.Objects(recovered).All(row => knownSlots[J.Str(row["traceId"])] == J.Num(row["colorSlot"])),
        "未知上下文恢复后必须沿用原颜色。");
}
var duplicateRows = J.Array(J.Objects(threeRare)); duplicateRows.Add(J.Clone(threeRare[0]));
var ambiguous = preservedAllocator.ProjectSnapshot(200,
    J.Object(("orders", duplicateRows), ("ordersReadComplete", true)), completeEmpty, palette);
Check(ambiguous.Count == 2 && J.Objects(ambiguous).All(row => J.Str(row["traceId"]) != J.Str(threeRare[0]?["traceId"])),
    "重复身份不展示，不能任选一条。");
var extraNormal = J.Object(("orders", new JsonArray(J.Clone(manyNormal[0]))), ("ordersReadComplete", true));
var partial = preservedAllocator.ProjectSnapshot(200,
    J.Object(("orders", new JsonArray()), ("ordersReadComplete", false)), extraNormal, palette);
Check(partial.Count == 1 && J.Num(partial[0]?["colorSlot"]) == 3,
    "普客不能抢占暂时未知的三个稀客颜色槽。");
var completeAgain = preservedAllocator.ProjectSnapshot(200, completeRare, extraNormal, palette);
Check(J.Objects(completeAgain).Where(row => J.Str(row["kind"]) == "rare")
    .All(row => knownSlots[J.Str(row["traceId"])] == J.Num(row["colorSlot"])), "重复与未知恢复后旧稀客仍保色。");
preservedAllocator.ProjectSnapshot(200, completeEmpty, extraNormal, palette);
var confirmedEmptyRecovered = preservedAllocator.ProjectSnapshot(200,
    J.Object(("orders", new JsonArray(J.Clone(threeRare[2]))), ("ordersReadComplete", true)), extraNormal, palette);
Check(J.Num(J.Objects(confirmedEmptyRecovered).Single(row => J.Str(row["kind"]) == "rare")["colorSlot"]) == 0,
    "明确完整空集必须释放已结束稀客的槽位，不得无限保留。");
var nextGeneration = preservedAllocator.ProjectSnapshot(201,
    J.Object(("orders", new JsonArray(J.Clone(threeRare[2]))), ("ordersReadComplete", false)), null, palette);
Check(nextGeneration.Count == 1 && J.Num(nextGeneration[0]?["colorSlot"]) == 0,
    "新经营代次不继承上一轮未知订单的槽位。");

// 过期只读展示不能分配新订单或倒退代次，也不能让当前颜色在刷新时消失。
var knownDisplay = preservedAllocator.ReadKnown(201, new JsonArray(J.Clone(threeRare[2])), new JsonArray(), palette);
Check(knownDisplay.Count == 1 && J.Num(knownDisplay[0]?["colorSlot"]) == 0, "只读完成结果保留已有订单颜色。");
Check(preservedAllocator.ReadKnown(200, threeRare, new JsonArray(), palette).Count == 0, "旧代次只读结果不能倒退分配器。");
Check(preservedAllocator.ReadKnown(201, new JsonArray(J.Clone(threeRare[0])), new JsonArray(), palette).Count == 0,
    "只读结果不能为过期名单中的新身份分配颜色。");
Check(preservedAllocator.ReadKnown(201, new JsonArray(J.Clone(threeRare[2])), new JsonArray(), palette).Count == 1,
    "未知或旧代次只读查询不得释放当前颜色。");

// 只测同线程托管分配：额外目录字段模拟真实未被本目标读取的巨树，不代表实际游戏 FPS。
// 旧参数袋必须复制这些节点；新入口不应因目录内无关字段增加而同比扩张分配。
var large = Fixture(); var padding = new JsonArray();
for (var index = 0; index < 600; index++) padding.Add(J.Object(("id", index), ("name", "无关目录"), ("tags", new JsonArray("鲜", "肉", "高级"))));
J.Obj(large["data"])["unusedCatalog"] = padding;
var frozen = Input(large, "rare");
for (var index = 0; index < 8; index++) { _ = GameUiTargetService.Evaluate(frozen); _ = GameUiTargetService.Evaluate(Envelope(frozen)); }
var frozenBytes = Measure(() => GameUiTargetService.Evaluate(frozen));
var envelopeBytes = Measure(() => GameUiTargetService.Evaluate(Envelope(frozen)));
Check(frozenBytes * 3 < envelopeBytes, "只读参数应消除无关目录参数袋的主要分配，且保留真实目标构造。");
Console.WriteLine($"GAME-UI-INPUT checks={checks} frozenBytes={frozenBytes} envelopeBytes={envelopeBytes} loops=16");

void AssertEquivalent(FrozenGameUiTargetInput input, string? trace, int recipe, int beverage)
{
    var actual = GameUiTargetService.Evaluate(input);
    Check(Json(actual) == Json(GameUiTargetService.Evaluate(Envelope(input))), "强类型和 JSON 入口输出完整相同。");
    if (trace == null) { Check(actual == null, "无法证明当前目标时必须返回 null。"); return; }
    Check(actual != null && J.Str(actual["traceId"]) == trace, "选择符合排序、任务和身份规则的精确订单。");
    Check(J.Num(actual?["recipeId"]) == recipe && J.Num(actual?["beverageId"]) == beverage, "料理与酒水保持同一方案及送达语义。");
}
void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
void Throws(Action action, string parameter)
{
    try { action(); } catch (ArgumentException exception) { Check(exception.ParamName == parameter, "保留各入口的异常参数定位。"); return; }
    throw new InvalidOperationException("未知操作未拒绝。");
}
static string Json(JsonNode? value) => value?.ToJsonString() ?? "null";
static long Measure(Func<JsonNode?> action)
{
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var index = 0; index < 16; index++) _ = action();
    return GC.GetAllocatedBytesForCurrentThread() - before;
}

// 借用节点构造只读输入；测试中节点已挂在原始根对象上，能检验意外的移植/写入。
static FrozenGameUiTargetInput Input(JsonObject root, string kind) => new(kind, J.Obj(root["data"]),
    Recommendations: root["recommendations"], Orders: root["orders"], ExecutionTargets: root["executionTargets"],
    Color: "#ABCDEF", Features: root["features"], SpecialBusiness: root["specialBusiness"]);

// 复现宿主旧写法，包括构造参数袋时的递归复制；仅用于行为和分配对照。
static JsonObject Envelope(FrozenGameUiTargetInput input) => J.Object(("operation", input.Operation), ("data", input.Data),
    ("recommendations", input.Recommendations), ("orders", input.Orders), ("executionTargets", input.ExecutionTargets),
    ("executionTargetsCurrent", input.ExecutionTargetsCurrent),
    ("color", input.Color), ("features", input.Features), ("specialBusiness", input.SpecialBusiness),
    ("businessGeneration", input.BusinessGeneration),
    ("options", J.Object(("prioritizeMissionRecipe", input.PrioritizeMissionRecipe), ("specialBusiness", input.SpecialBusiness))));

// 纯合成目录保留 foodId/recipeId 的不同身份，不读取本机游戏、文件或连接凭据。
static JsonObject Fixture()
{
    JsonObject Recipe(int food, int recipe) => J.Object(("id", food), ("recipeId", recipe), ("name", "料理" + food),
        ("ingredients", new JsonArray("材料甲")), ("cooker", "煮锅"));
    JsonObject Order(string trace, int desk) => J.Object(("traceId", trace), ("orderLifecycleSequence", desk + 1),
        ("deskCode", desk), ("guestId", desk + 10), ("runtimeGuestId", desk + 100),
        ("firstSeenAtUtc", desk == 0 ? "2026-10-12T00:00:00Z" : "2026-10-12T00:00:01Z"),
        ("foodId", 501), ("beverageId", 21), ("foodTagId", 1), ("beverageTagId", 2),
        ("hasServedFood", false), ("hasServedBeverage", false));
    JsonObject Recommendation(int index)
    {
        var order = Order(index == 0 ? "R-0001" : "R-0002", index);
        if (index == 1) order["missionRecipePriority"] = J.Object(("traceId", "R-0002"),
            ("deskCode", 1), ("guestId", 11), ("runtimeGuestId", 101), ("foodId", 502), ("recipeId", 1502),
            ("missionGeneration", 1), ("businessGeneration", 7));
        var plan = J.Object(("bucket", "perfect"),
            ("food", J.Object(("recipe", Recipe(501 + index, 1501 + index)),
                ("extraIngredients", new JsonArray(J.Object(("id", 2)), J.Object(("id", 2)))))),
            ("beverage", J.Object(("beverage", J.Object(("id", 21), ("name", "酒水甲"))), ("meetsRequiredBeverage", true))));
        return J.Object(("order", order), ("executionPlans", new JsonArray(plan)));
    }
    var normal = Order("N-0001", 0); normal["orderKey"] = "ptr:11";
    return J.Object(("data", J.Object(("recipes", new JsonArray(Recipe(501, 1501), Recipe(502, 1502))),
            ("beverages", new JsonArray(J.Object(("id", 21), ("name", "酒水甲")))),
            ("ingredients", new JsonArray(J.Object(("id", 1), ("name", "材料甲")), J.Object(("id", 2), ("name", "材料乙")))))),
        ("recommendations", new JsonArray(Recommendation(0), Recommendation(1))), ("orders", new JsonArray(normal)),
        ("executionTargets", new JsonArray()), ("features", J.Object(("orderHighlightEnabled", true))));
}
