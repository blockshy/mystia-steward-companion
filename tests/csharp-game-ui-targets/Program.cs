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
