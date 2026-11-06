using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using MystiaStewardCompanion.Business.Domain.Orders;
using MystiaStewardCompanion.Business.Domain.Support;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

// 本用例只验证托管业务：真实游戏的 IL2CPP 副作用由运行时保护控制，不以离线结果冒充实机验证。
var checks = 0;
void Check(bool valid, string detail) { checks++; if (!valid) throw new InvalidOperationException(detail); }
JsonObject Parse(string value) => JsonNode.Parse(value)!.AsObject();
var data = Parse("""
{"specialProducts":[{"id":-1,"productKind":"dark-cuisine","name":"黑暗料理","positiveTags":["黑暗料理"],"level":0,"price":0}],
 "recipes":[{"id":100,"recipeId":200,"name":"基础料理","ingredients":["基础食材","基础食材"],"ingredientIds":[1,1],"positiveTags":["甜"],"negativeTags":["肉"],"cooker":"煮锅","level":1,"price":20}],
 "ingredients":[{"id":1,"name":"基础食材","tags":["素"],"price":2},{"id":2,"name":"禁忌加料","tags":["肉","甜"],"price":3}],
 "beverages":[{"id":5,"name":"酒水","tags":["低酒精"],"price":10,"level":1}],
 "rareCustomers":[{"id":3,"name":"任务稀客","positiveTags":["甜"],"negativeTags":[],"beverageTags":["低酒精"]}]}
""");
var context = Parse("""
{"availableRecipeIds":[100],"availableIngredientIds":[1,2],"availableBeverageIds":[5],"ownedIngredientQty":{"1":2,"2":1},"ownedBeverageQty":{"5":10},"disabledIngredientIds":[],"excludedIngredientIds":[],"placedCookerNames":["煮锅"],"hasCookerSnapshot":true,"maxExtraIngredients":4}
""");
var demand = Object(("customer", Objects(data["rareCustomers"]).Single()), ("requiredFoodTag", "甜"), ("requiredBeverageTag", "低酒精"));
// 覆盖实际 HTTP 宿主先标准化目录再送入推荐的路径，防止新增字段被白名单静默丢弃。
var catalog = Obj(Clone(data)); catalog["isComplete"] = true;
Objects(catalog["rareCustomers"]).Single()["places"] = Array(new[] { "妖怪兽道" });
catalog["normalCustomers"] = Array(new[] { Parse("""{"id":1,"name":"普通客群","positiveTags":["甜"],"beverageTags":["低酒精"],"places":["妖怪兽道"]}""") });
var normalized = RuntimeDataNormalizer.Build(catalog);
Check(Objects(normalized["specialProducts"]).Count() == 1
    && RecommendationEngine.BuildDarkCuisineCandidates(normalized, demand, context).Count == 1,
    "实际目录标准化丢失特殊产物，导致运行时没有黑暗料理方案。");
Obj(Arr(catalog["specialProducts"])[0]).Remove("id");
var missingIdentity = RuntimeDataNormalizer.Build(catalog);
Check(Arr(missingIdentity["specialProducts"]).Count == 0 && Str(missingIdentity["source"]) == "runtime",
    "特殊产物缺失身份不应伪造 -1 或污染普通目录。");
var before = data.ToJsonString() + context.ToJsonString();
var candidates = RecommendationEngine.BuildDarkCuisineCandidates(data, demand, context);
Check(candidates.Count == 1, "未产生真实配方+禁忌加料方案。");
var food = Objects(candidates).Single();
Check(Num(Obj(food["recipe"])["id"]) == -1 && Num(Obj(food["productionRecipe"])["id"]) == 100
    && Num(Obj(food["recipe"])["recipeId"]) == 200, "特殊成品与真实配方身份混淆。");
Check(Strings(food["activeTags"]).Contains("黑暗料理") && !Strings(food["activeTags"]).Contains("甜"), "黑暗料理继承了不应保留的基底/加料标签。");
Check(Num(food["baseCost"]) == 4 && Num(food["extraCost"]) == 3, "重复基础材料成本错误。");
Check(before == data.ToJsonString() + context.ToJsonString(), "候选污染原始目录/运行时。");
var noMaterial = Obj(Clone(context)); Obj(noMaterial["ownedIngredientQty"])["1"] = 1;
Check(RecommendationEngine.BuildDarkCuisineCandidates(data, demand, noMaterial).Count == 0, "重复材料份数不足仍计划开锅。");
var noSlot = Obj(Clone(context)); noSlot["maxExtraIngredients"] = 0;
Check(RecommendationEngine.BuildDarkCuisineCandidates(data, demand, noSlot).Count == 0, "加料槽不足仍计划黑暗料理。");
var excluded = Obj(Clone(context)); excluded["excludedIngredientIds"] = Array(new[] { 2d });
Check(RecommendationEngine.BuildDarkCuisineCandidates(data, demand, excluded).Count == 0, "排除材料被黑暗料理绕过。");
var noProduct = Obj(Clone(data)); noProduct["specialProducts"] = new JsonArray();
Check(RecommendationEngine.BuildDarkCuisineCandidates(noProduct, demand, context).Count == 0, "特殊元数据缺失时伪造成品。");
var storedContext = Obj(Clone(noMaterial)); storedContext["storedDarkCuisineCount"] = 1;
storedContext["placedCookerNames"] = new JsonArray(); storedContext["availableRecipeIds"] = new JsonArray();
var stored = Objects(RecommendationEngine.BuildDarkCuisineCandidates(data, demand, storedContext)).Single();
Check(Str(stored["supplySource"]) == "stored" && Obj(stored["recipe"])["recipeId"] is null
    && Arr(stored["extraIngredients"]).Count == 0, "已有成品不应依赖配方/材料/厨具。");
storedContext["storedDarkCuisineCount"] = null;
Check(RecommendationEngine.BuildDarkCuisineCandidates(data, demand, storedContext).Count == 0, "未知保温箱数量当作现成供给。");

var order = Parse("""{"traceId":"R1","orderLifecycleSequence":1,"deskCode":0,"guestId":3,"runtimeGuestId":1003,"guestName":"任务稀客","foodTag":"甜","beverageTag":"低酒精","missionRecipePriority":{"traceId":"R1","deskCode":0,"guestId":3,"runtimeGuestId":1003,"foodId":-1,"recipeId":null,"productKind":"dark-cuisine","missionGeneration":2,"businessGeneration":3}}""");
var sort = RecommendationEngine.GetVerifiedMissionRecipeSortContext(order);
Check(sort != null && RecommendationEngine.IsMissionRecipeFoodCandidate(food, sort), "特殊任务候选未进入任务优先。");
Obj(order["missionRecipePriority"]).Remove("productKind");
Check(RecommendationEngine.GetVerifiedMissionRecipeSortContext(order) == null, "缺少明确种类的-1被当作特殊任务。");
Obj(order["missionRecipePriority"])["productKind"] = "dark-cuisine";
var second = Obj(Clone(order)); second["traceId"] = "R2"; second["deskCode"] = 1; second.Remove("missionRecipePriority");
var runtime = Obj(Clone(context)); runtime["placedCookerTypeIds"] = Array(new[] { 1d }); runtime["placedCookerSnapshotComplete"] = true;
var payload = Object(("orders", Array(new[] { order, second })), ("runtime", runtime), ("data", data),
    ("preferences", Object(("missionRecipePriorityEnabled", true), ("recommendationBudgetPolicy", "warn"))),
    ("favorites", new JsonObject()), ("customRecipes", new JsonObject()), ("specialBusiness", Object(("active", false), ("challengeTypeAvailable", true), ("challengeType", "NotChallenge"))));
var rows = Objects(OrderRecommendationService.Evaluate(payload)["recommendations"]).ToArray();
Check(rows.Length == 2, "黑暗料理任务导致整个订单推荐失败。");
Check(Num(Obj(Obj(Objects(rows[0]["executionPlans"]).First()["food"])["recipe"])["id"]) == -1, "任务优先仍选择普通料理。");
Check(Num(Obj(Obj(Objects(rows[1]["executionPlans"]).First()["food"])["recipe"])["id"]) == 100, "普通订单被特殊任务污染。");
Obj(payload["data"])["specialProducts"] = new JsonArray();
rows = Objects(OrderRecommendationService.Evaluate(payload)["recommendations"]).ToArray();
Check(Arr(rows[0]["executionPlans"]).Count == 0 && Arr(rows[1]["executionPlans"]).Count > 0, "特殊能力不可用未隔离到单个订单。");
// 复用同一缓存，目录特殊产物或库存供给变化都必须失效；不能只校验全新实例的直接求解。
var cache = new OrderCandidateCache();
payload["data"] = Clone(data);
rows = Objects(OrderRecommendationService.Evaluate(payload, cache)["recommendations"]).ToArray();
Check(Arr(rows[0]["executionPlans"]).Count > 0, "缓存首轮未生成特殊任务候选。");
Obj(payload["data"])["specialProducts"] = new JsonArray();
rows = Objects(OrderRecommendationService.Evaluate(payload, cache)["recommendations"]).ToArray();
Check(Arr(rows[0]["executionPlans"]).Count == 0 && Arr(rows[1]["executionPlans"]).Count > 0,
    "特殊产物能力消失后错误复用了原候选。");
payload["data"] = Clone(data); Obj(payload["runtime"])["storedDarkCuisineCount"] = 1;
rows = Objects(OrderRecommendationService.Evaluate(payload, cache)["recommendations"]).ToArray();
Check(Str(Obj(Objects(rows[0]["executionPlans"]).First()["food"])["supplySource"]) == "stored",
    "保温箱库存新增后错误沿用生产方案。");
Obj(payload["runtime"])["storedDarkCuisineCount"] = 0;
rows = Objects(OrderRecommendationService.Evaluate(payload, cache)["recommendations"]).ToArray();
Check(Str(Obj(Objects(rows[0]["executionPlans"]).First()["food"])["supplySource"]) == "cook",
    "保温箱成品消耗后错误沿用库存方案。");
Console.WriteLine($"PASS: {checks} dark-cuisine task recommendation checks.");