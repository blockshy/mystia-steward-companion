using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using MystiaStewardCompanion.Business.Domain.Support;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Orders;

/// <summary>
/// 订单推荐的纯业务入口。快照、目录、收藏、自定义配方和设置构成完整输入；输出同时包含主执行方案、
/// 展示行、阻断诊断和普通订单目标，保证展示与自动化不各自重新选优。缓存由宿主按完整输入签名维护。
/// </summary>
public static partial class OrderRecommendationService
{
    /// <summary>兼容原订单 Worker 的 payload/result 形状；结果不含游戏对象和客户端状态。</summary>
    public static JsonObject Evaluate(JsonObject payload, OrderCandidateCache? cache = null)
    {
        cache ??= new OrderCandidateCache();
        var result = BuildRareRecommendations(payload, cache); var normal = OrderIdentityAndSorting.SortNormal(Objects(payload["normalOrders"])).ToArray();
        result["normalOrderDetailPlans"] = Bool(payload["includeNormalOrderDetails"]) ? Array(normal.Select(o => BuildNormalDetail(payload, o, cache))) : new JsonArray();
        result["normalExecutionTargets"] = Bool(payload["includeNormalExecutionTargets"]) ? Array(normal.Where(o => !Bool(o["hasEvaluated"])).Select(o =>
        { var selection = NormalTargetSelector.Select(NormalArgs(payload, o), cache); return Object(("orderKey", OrderIdentityAndSorting.NormalKey(o)), ("target", selection["target"]), ("message", selection["message"])); })) : new JsonArray();
        return result;
    }
    private static JsonObject NormalArgs(JsonObject payload, JsonObject order) => Object(("order", order), ("specialBusiness", payload["specialBusiness"]), ("runtime", payload["runtime"]), ("preferences", payload["preferences"]), ("data", payload["data"]), ("dataSignature", payload["dataSignature"]), ("rejectedRecipeKeys", payload["specialBusinessRejectedRecipeKeys"] ?? new JsonArray()));

    public static JsonObject BuildRareRecommendations(JsonObject payload, OrderCandidateCache? cache = null)
    {
        cache ??= new OrderCandidateCache();
        var recommendations = new JsonArray(); var issues = new JsonArray(); var result = Object(("recommendations", recommendations), ("recommendationIssues", issues));
        // Object 会复制节点，因此后续直接从返回树取得要填充的数组。
        recommendations = Arr(result["recommendations"]); issues = Arr(result["recommendationIssues"]);
        var preferences = Obj(payload["preferences"]); var special = payload["specialBusiness"]; var data = Obj(payload["data"]);
        var orders = OrderIdentityAndSorting.SortRare(Objects(payload["orders"])).ToArray();
        // 没有稀客订单时结果明确为空，无需构造库存集合、复制目录或生成缓存键。
        // 普客计算仍由Evaluate在此函数返回后独立执行，不用稀客状态替普客判定可用性。
        if (orders.Length == 0) return result;
        if (payload["runtime"] is not JsonObject runtime)
        { foreach (var o in orders) issues.Add(Object(("order", o), ("message", "运行时推荐数据暂不可用。"))); return result; }
        var sets = RuntimeRecommendationSupport.BuildRuntimeSets(runtime, data); if (sets == null) return result;
        var candidateContext = RuntimeRecommendationSupport.BuildRecommendationRuntimeContext(runtime, sets, preferences, data);
        var rejected = Bool(Obj(special)["active"]) && Bool(Obj(special)["challengeTypeAvailable"]) && Str(Obj(special)["challengeType"]) == SpecialBusinessRules.WackyChallenge ? Strings(payload["specialBusinessRejectedRecipeKeys"]).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        var automation = Str(payload["usage"]) == "automation"; var rowLimit = automation ? 4 : 20; var planLimit = automation ? 32 : 80;
        var favorites = Obj(payload["favorites"]); var custom = Obj(payload["customRecipes"]);
        // 纯投影键只保存下方计算闭包真正读取的组件，而不是原始快照参数袋。物理厨具的忙闲、
        // 指针、网格位置以及特殊经营观察时间属于当前执行事实：它们仍使宿主帧失效并重新验证
        // 动作许可，但在候选资源集合/业务规则不变时，不能迫使每桌重新进行相同的组合搜索。
        // candidateContext 已完整投影目录可用性、库存、流行标签、排除、厨具限制和预算策略；
        // 每单的 customer/demand/rule/sort/policy/planning 与精确预算仍在下方单独加入缓存键。
        // 无方案诊断另读取真实摆放/可用/锁定厨具名称，不能只保留 context 中的筛选名称集合。
        var sharedInput = Object(("data", data), ("candidateContext", candidateContext),
            ("customRecipes", custom), ("sortProfile", preferences["recommendationSortProfile"]),
            ("variantLimitPerBase", preferences["recipeVariantLimitPerBase"]),
            ("rowLimit", rowLimit), ("planLimit", planLimit),
            ("rejectedRecipeKeys", Array(rejected.OrderBy(value => value, StringComparer.Ordinal))),
            ("diagnosticCookers", Object(("placedCookerNames", sets["placedCookerNames"]),
                ("usableCookerNames", sets["usableCookerNames"]),
                ("runtimeUnavailableCookerNames", sets["runtimeUnavailableCookerNames"]))));
        NormalizeProjectionIngredientPressure(Obj(sharedInput["candidateContext"]));
        var sharedSignature = sharedInput.ToJsonString();
        var activeGuests = Objects(payload["activeRareGuests"]).ToArray();
        foreach (var order in orders)
        {
            var customer = FindCustomer(order, data); var foodTag = Str(order["foodTag"]).Trim(); var beverageTag = Str(order["beverageTag"]).Trim();
            if (customer == null) { issues.Add(Object(("order", order), ("message", "无法把该稀客映射到本地稀客数据。"))); continue; }
            if (foodTag.Length == 0 || beverageTag.Length == 0) { issues.Add(Object(("order", order), ("message", "该点单缺少料理 Tag 或酒水 Tag。"))); continue; }
            // 先用当前订单验证任务身份、权限、特殊营业和预算，再缓存纯计算输入。
            // 桌号/trace/lifecycle本身不会改变料理组合，同需求新订单可复用计算；它们仍用于上方
            // 当前订单排序、下面任务身份验证、最终order投影及主线程写入门禁，绝不复用执行许可。
            var budget = BudgetContext(order, activeGuests);
            var rule = SpecialBusinessRules.BuildOrderRule(special, Str(order["specialBusinessRole"])); var target = Str(Obj(rule["foodTarget"])["enforcement"]) == "none" ? null : rule["foodTarget"];
            var demand = NormalTargetSelector.Demand(customer, foodTag, beverageTag, target);
            var sort = BuildSortContext(RuntimeRecommendationSupport.BuildRecommendationPlanSortContext(favorites, (int)Num(customer["id"]), foodTag, beverageTag, preferences), special, rule, order, foodTag, beverageTag, Bool(preferences["missionRecipePriorityEnabled"]));
            var policy = PrimaryExecutionPlans.BuildPrimaryExecutionPlanPolicy(preferences, Bool(order["automationAllowed"], true));
            var planning = Bool(rule["preferKoishiDamage"])
                ? Object(("remainingBudget", Obj(budget)["remainingBudget"] ?? order["fund"]), ("remainingScore", KoishiEvaluation.RemainingScore(special)), ("remainingOrderCount", order["remainingOrderCount"]))
                : null;
            var semanticInput = Object(("customer", customer), ("demand", demand), ("rule", rule),
                ("sort", sort), ("policy", policy), ("planning", planning));
            var projection = cache.Recommendation(sharedSignature, semanticInput, budget, () =>
            {
            var generated = cache.Food(data, demand, candidateContext, Object(("requiredExtraIngredientIds", rule["requiredExtraIngredientIds"]), ("forbiddenExtraIngredientIds", rule["forbiddenExtraIngredientIds"])));
            var beverages = Objects(cache.Beverage(data, NormalTargetSelector.Demand(customer, foodTag, beverageTag), candidateContext)).ToArray();
            var customs = CustomRecipes.BuildCustomFoodCandidates(Object(("customRecipes", custom), ("data", data), ("customer", customer), ("requiredFoodTag", foodTag), ("requiredBeverageTag", beverageTag), ("specialFoodTarget", target), ("requiredExtraIngredientIds", rule["requiredExtraIngredientIds"]), ("forbiddenExtraIngredientIds", rule["forbiddenExtraIngredientIds"]), ("context", candidateContext)));
            var merged = CustomRecipes.MergeCustomFoodCandidateReferences(generated, customs).ToArray();
            var foods = merged.Where(f => FoodAllowed(f, rule, rejected, foodTag)).ToArray();
            beverages = beverages.Where(b => Str(rule["blockingReason"]).Length == 0 && ((!Bool(rule["requiresBaseOrderMatch"]) && !Bool(rule["requiresHighEvaluation"])) || Bool(b["meetsRequiredBeverage"]))).ToArray();
            var context = RuntimeRecommendationSupport.BuildRecommendationRuntimeContext(runtime, sets, preferences, data, Object(("budget", budget)));
            var planContext = Obj(Clone(context)); if (Bool(rule["preferKoishiDamage"])) planContext["budgetPolicy"] = "warn";
            var mission = MissionPair(foods, beverages, planContext["budget"], Str(planContext["budgetPolicy"]), sort, policy);
            var executionFoods = ExecutionCandidates(foods, beverages, true, planContext["budget"], Str(planContext["budgetPolicy"]), sort, mission?.Food);
            var executionBeverages = ExecutionCandidates(beverages, foods, false, planContext["budget"], Str(planContext["budgetPolicy"]), sort, mission?.Beverage);
            var raw = Objects(RecommendationEngine.BuildRareOrderPlansFromCandidates(Object(("data", data), ("customer", customer), ("requiredFoodTag", foodTag), ("requiredBeverageTag", beverageTag), ("context", planContext), ("foodCandidates", Array(executionFoods)), ("beverageCandidates", Array(executionBeverages)), ("specialFoodTarget", target), ("sortProfile", preferences["recommendationSortProfile"]), ("sortContext", sort)))).Select(p => AddReasons(p, rule, sort)).ToArray();
            var safe = FilterPlans(raw, rule).ToArray();
            var mode = Str(rule["yuyukoProgressEvaluationMode"]);
            if (mode != "none") safe = YuyukoEvaluation.SortPlanReferences(safe, mode).ToArray();
            else if (Bool(rule["preferYuyukoPositiveSpell"])) safe = YuyukoEvaluation.SortPlanReferences(safe, "none", true).ToArray();
            else if (Bool(rule["preferKoishiDamage"]))
            {
                safe = safe.OrderBy(p => p, Comparer<JsonObject>.Create((a, b) => KoishiEvaluation.ComparePlans(a, b, planning!))).ToArray();
            }
            // 完整的 raw/safe 集合继续参与特殊经营排序、任务提升、收藏准入和阻断诊断；
            // 仅重排引用，不能在这些步骤之前截取80/32项，否则后位合法主方案可能被误丢弃。
            // 每个最终保留方案到输出边界才复制一次，避免对384/4608项中间组合连续深复制。
            var execution = PrimaryExecutionPlans.NormalizePrimaryExecutionPlanReferences(
                safe.Where(p => Str(p["bucket"]) != "blocked"), sort, policy);
            var primary = execution.FirstOrDefault();
            var variantLimit = Num(preferences["recipeVariantLimitPerBase"]);
            var recipeRows = DeriveRows(foods, beverages, true, budget, Str(context["budgetPolicy"]), sort, preferences["recommendationSortProfile"], rowLimit, variantLimit);
            var beverageRows = DeriveRows(beverages, foods, false, budget, Str(context["budgetPolicy"]), sort, preferences["recommendationSortProfile"], rowLimit, double.PositiveInfinity);
            ProjectPrimary(ref recipeRows, ref beverageRows, primary, rowLimit, variantLimit, sort);
            var diagnostic = execution.Count == 0 ? BlockedDiagnostic(data, demand, context, sets, generated, merged, foods, beverages, raw, safe, execution, rule) : null;
            var messages = (diagnostic == null ? System.Array.Empty<string>() : new[] { Str(diagnostic["message"]) }).Concat(SpecialBlockedMessages(raw, safe, rule)).Concat(BlockedPlanMessages(safe, budget, Str(context["budgetPolicy"])));
            // 料理/酒水行与诊断均由本次调用新建且尚无父节点，可直接移交给最终投影。
            // customer、budget以及完整方案仍来自只读输入或raw树，必须显式复制，不能移动其父级。
            // 使用普通JsonObject组装独占的新节点，避免通用Object为这些新数组再深复制一遍。
            return new JsonObject
            {
                ["customer"] = Clone(customer),
                ["executionPlans"] = Array(execution.Take(planLimit)),
                ["budget"] = Clone(primary?["budget"] ?? safe.FirstOrDefault(p => p["budget"] != null)?["budget"]),
                ["blockedMessages"] = Array(Unique(messages)),
                ["blockedDiagnostic"] = diagnostic,
                ["recipes"] = new JsonArray(recipeRows.Cast<JsonNode?>().ToArray()),
                ["beverages"] = new JsonArray(beverageRows.Cast<JsonNode?>().ToArray()),
            };
            });
            // 当前订单始终来自本轮输入，缓存命中不能延长输入版本或改变后续主线程许可检查。
            projection["order"] = Clone(order); recommendations.Add(projection);
        }
        return result;
    }

    /// <summary>
    /// 仅在稀客纯推荐投影的私有缓存键中合并资源压力等价的数量，不修改真实输入或返回库存。
    /// </summary>
    /// <remarks>
    /// 普通加料搜索与自定义配方都仅以 quantity == -1 ? 0 : max(0, 5 - quantity)
    /// 消费食材数量；数量本身不进入该投影，实际可用性仍由完整 availableIngredientIds 等
    /// 集合决定。因此 20→19 及 -1 与 >=5 的压力相同，不应每次扣料都重做所有桌的搜索。
    /// 此处接收刚复制的私有键上下文，数量键已经经过 BuildRuntimeSets 的原有规范化；
    /// 不能修改实际 candidateContext、runtime 或求解参数。低于5、缺失、非数值原样保留，
    /// 酒水数量、可用集合、预算、厨具、规则及配置全部保留。
    /// 这不是执行缓存：最终订单、当前库存和动作许可继续由当前帧绑定并在主线程复核。
    /// </remarks>
    private static void NormalizeProjectionIngredientPressure(JsonObject keyContext)
    {
        if (keyContext["ownedIngredientQty"] is not JsonObject quantities) return;
        foreach (var key in quantities.Select(pair => pair.Key).ToArray())
        {
            var quantity = NullableNumber(quantities[key]);
            if (quantity == -1 || quantity >= 5) quantities[key] = 5;
        }
    }
    private static JsonObject? FindCustomer(JsonObject order, JsonObject data)
    {
        if (SpecialBusinessRules.IsSpecialRole(Str(order["specialBusinessRole"]))) return order["guestId"] == null ? null : NormalTargetSelector.ExactCustomer(data, Num(order["guestId"]));
        var customers = Objects(data["rareCustomers"]).GroupBy(x => Num(x["id"])).Select(x => x.Last()).ToArray();
        return (order["guestId"] == null ? null : customers.FirstOrDefault(c => Num(c["id"]) == Num(order["guestId"]))) ?? customers.FirstOrDefault(c => Str(c["name"]) == Str(order["guestName"]));
    }
    private static JsonObject? BudgetContext(JsonObject order, JsonObject[] guests)
    {
        if (Bool(order["isFreeOrder"])) return null;
        var guest = order["guestId"] == null ? null : guests.FirstOrDefault(g => Num(g["guestId"]) == Num(order["guestId"]));
        guest ??= guests.FirstOrDefault(g => Num(g["deskCode"]) == Num(order["deskCode"]) && Str(g["guestName"]).Trim() == Str(order["guestName"]).Trim());
        var desk = guests.Where(g => Num(g["deskCode"]) == Num(order["deskCode"])).ToArray(); if (guest == null && desk.Length == 1) guest = desk[0];
        var remaining = KoishiEvaluation.NonNegativeInt(guest?["fund"] ?? order["fund"]);
        if (remaining == null && guest?["willPayMoney"] == null && order["willPayMoney"] == null) return null;
        return Object(("remainingBudget", remaining), ("source", guest == null ? "unknown" : "runtime-active-guest"), ("willPayMoney", guest?["willPayMoney"] ?? order["willPayMoney"]));
    }
    private static JsonObject BuildSortContext(JsonObject basis, JsonNode? special, JsonObject rule, JsonObject order, string food, string beverage, bool missionEnabled)
    {
        var s = Obj(special); var target = Obj(rule["foodTarget"]); var foodTags = Strings(target["tags"]); var beverageTags = SpecialBusinessRules.NormalizeTags(s["beverageTargetTags"]); var mode = Str(rule["yuyukoProgressEvaluationMode"]);
        if (Bool(s["active"]) && (foodTags.Length > 0 || beverageTags.Length > 0 || Bool(rule["preferHighFoodLevel"]) || Bool(rule["preferHighBeverageLevel"]) || Bool(rule["preferKoishiDamage"]) || Bool(rule["preferYuyukoPositiveSpell"]) || mode != "none"))
        {
            if (foodTags.Length > 0) basis["specialTargetFoodTags"] = Array(foodTags); if (beverageTags.Length > 0) basis["specialTargetBeverageTags"] = Array(beverageTags);
            basis["specialPreferHighFoodLevel"] = Clone(rule["preferHighFoodLevel"]); basis["specialPreferHighBeverageLevel"] = Clone(rule["preferHighBeverageLevel"]); basis["specialPreferDamageLevel"] = Clone(rule["preferKoishiDamage"]); basis["specialPreferYuyukoPositiveSpell"] = Clone(rule["preferYuyukoPositiveSpell"]);
            if (mode != "none") basis["specialYuyukoProgressEvaluationMode"] = mode;
            if (Bool(rule["preferYuyukoPositiveSpell"]) || mode == "retake-tag-order") { basis["specialYuyukoRequiredFoodTag"] = food; basis["specialYuyukoRequiredBeverageTag"] = beverage; }
            basis["specialKoishiRemainingScore"] = JsonValue.Create(Bool(rule["preferKoishiDamage"]) ? KoishiEvaluation.RemainingScore(special) : null);
            basis["specialKoishiRemainingOrderCount"] = JsonValue.Create(Bool(rule["preferKoishiDamage"]) ? KoishiEvaluation.NonNegativeInt(order["remainingOrderCount"]) : null);
        }
        if (missionEnabled && !Bool(s["active"])) { var mission = RecommendationEngine.GetVerifiedMissionRecipeSortContext(order); if (mission != null) foreach (var pair in mission) basis[pair.Key] = Clone(pair.Value); }
        return basis;
    }
    private static int Count(JsonObject value, string field) => Arr(value[field]).Count;
    private static double Field(JsonObject value, string owner, string field) => Num(Obj(value[owner])[field]);
    private static string RecipeKey(JsonObject food) => $"{Key(Field(food, "recipe", "id"))}:{string.Join(",", Objects(food["extraIngredients"]).Select(x => Num(x["id"], -1)).Where(x => double.IsFinite(x) && x >= 0).Select(Math.Truncate).Distinct().OrderBy(x => x).Select(Key))}";
    private static IEnumerable<string> Unique(IEnumerable<string> values) => values.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal);
}
