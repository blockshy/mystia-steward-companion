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
        if (RareGuestParticipationPolicy.IsBlocked(Obj(payload["preferences"])))
        {
            // 候选仍供手动查看；每行携带服务端决定的辅助阻断标记，避免下游另选主方案绕过历史配置。
            result["rareGuestParticipation"] = RareGuestParticipationPolicy.Diagnostic();
            foreach (var row in Objects(result["recommendations"])) row["rareGuestParticipation"] = RareGuestParticipationPolicy.Diagnostic();
        }
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
        var orders = OrderIdentityAndSorting.SortRare(Objects(payload["orders"]), Str(preferences["serviceOrderSortMode"], "ordered"), special).ToArray();
        if (payload["runtime"] is not JsonObject runtime)
        { foreach (var o in orders) issues.Add(Object(("order", o), ("message", "运行时推荐数据暂不可用。"))); return result; }
        var sets = RuntimeRecommendationSupport.BuildRuntimeSets(runtime, data); if (sets == null) return result;
        var candidateContext = RuntimeRecommendationSupport.BuildRecommendationRuntimeContext(runtime, sets, preferences, data);
        var rejected = Bool(Obj(special)["active"]) && Bool(Obj(special)["challengeTypeAvailable"]) && Str(Obj(special)["challengeType"]) == SpecialBusinessRules.WackyChallenge ? Strings(payload["specialBusinessRejectedRecipeKeys"]).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        var automation = Str(payload["usage"]) == "automation"; var rowLimit = automation ? 4 : 20; var planLimit = automation ? 32 : 80;
        var favorites = Obj(payload["favorites"]); var custom = Obj(payload["customRecipes"]);
        foreach (var order in orders)
        {
            var customer = FindCustomer(order, data); var foodTag = Str(order["foodTag"]).Trim(); var beverageTag = Str(order["beverageTag"]).Trim();
            if (customer == null) { issues.Add(Object(("order", order), ("message", "无法把该稀客映射到本地稀客数据。"))); continue; }
            if (foodTag.Length == 0 || beverageTag.Length == 0) { issues.Add(Object(("order", order), ("message", "该点单缺少料理 Tag 或酒水 Tag。"))); continue; }
            // 排序已使用本轮完整订单完成；下面三项只投影给消费者，不参与候选、预算、任务验证或方案排序。
            // 完整payload的其他字段全部保留，单订单缓存不含其他订单，避免无关到店事件使已有计算重复。
            var semanticOrder = Obj(Clone(order));
            foreach (var field in new[] { "lastSeenAtUtc", "hasServedFood", "hasServedBeverage" }) semanticOrder.Remove(field);
            var semanticInput = Obj(Clone(payload)); semanticInput["orders"] = Array(new[] { semanticOrder }); semanticInput.Remove("normalOrders");
            var projection = cache.Recommendation(semanticInput, () =>
            {
            var rule = SpecialBusinessRules.BuildOrderRule(special, Str(order["specialBusinessRole"])); var target = Str(Obj(rule["foodTarget"])["enforcement"]) == "none" ? null : rule["foodTarget"];
            var demand = NormalTargetSelector.Demand(customer, foodTag, beverageTag, target); var budget = BudgetContext(order, Objects(payload["activeRareGuests"]).ToArray());
            var sort = BuildSortContext(RuntimeRecommendationSupport.BuildRecommendationPlanSortContext(favorites, (int)Num(customer["id"]), foodTag, beverageTag, preferences), special, rule, order, foodTag, beverageTag, Bool(preferences["missionRecipePriorityEnabled"]));
            var policy = PrimaryExecutionPlans.BuildPrimaryExecutionPlanPolicy(preferences, Bool(order["automationAllowed"], true));
            var generated = cache.Food(data, demand, candidateContext, Object(("requiredExtraIngredientIds", rule["requiredExtraIngredientIds"]), ("forbiddenExtraIngredientIds", rule["forbiddenExtraIngredientIds"])));
            var beverages = Objects(cache.Beverage(data, NormalTargetSelector.Demand(customer, foodTag, beverageTag), candidateContext)).ToArray();
            var customs = CustomRecipes.BuildCustomFoodCandidates(Object(("customRecipes", custom), ("data", data), ("customer", customer), ("requiredFoodTag", foodTag), ("requiredBeverageTag", beverageTag), ("specialFoodTarget", target), ("requiredExtraIngredientIds", rule["requiredExtraIngredientIds"]), ("forbiddenExtraIngredientIds", rule["forbiddenExtraIngredientIds"]), ("context", candidateContext)));
            var merged = Objects(CustomRecipes.MergeCustomFoodCandidates(generated, customs)).ToArray();
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
            if (mode != "none") safe = safe.OrderBy(p => p, Comparer<JsonObject>.Create((a, b) => YuyukoEvaluation.ComparePlans(a, b, mode))).ToArray();
            else if (Bool(rule["preferYuyukoPositiveSpell"])) safe = safe.OrderBy(p => p, Comparer<JsonObject>.Create(YuyukoEvaluation.ComparePositivePlans)).ToArray();
            else if (Bool(rule["preferKoishiDamage"]))
            {
                var planning = Object(("remainingBudget", Obj(budget)["remainingBudget"] ?? order["fund"]), ("remainingScore", KoishiEvaluation.RemainingScore(special)), ("remainingOrderCount", order["remainingOrderCount"]));
                safe = safe.OrderBy(p => p, Comparer<JsonObject>.Create((a, b) => KoishiEvaluation.ComparePlans(a, b, planning))).ToArray();
            }
            var execution = PrimaryExecutionPlans.NormalizePrimaryExecutionPlans(Array(safe.Where(p => Str(p["bucket"]) != "blocked")), sort, policy); var primary = Objects(execution).FirstOrDefault();
            var variantLimit = Num(preferences["recipeVariantLimitPerBase"]);
            var recipeRows = DeriveRows(foods, beverages, true, budget, Str(context["budgetPolicy"]), sort, preferences["recommendationSortProfile"], rowLimit, variantLimit);
            var beverageRows = DeriveRows(beverages, foods, false, budget, Str(context["budgetPolicy"]), sort, preferences["recommendationSortProfile"], rowLimit, double.PositiveInfinity);
            ProjectPrimary(ref recipeRows, ref beverageRows, primary, rowLimit, variantLimit, sort);
            var diagnostic = execution.Count == 0 ? BlockedDiagnostic(data, demand, context, sets, generated, merged, foods, beverages, raw, safe, execution, rule) : null;
            var messages = (diagnostic == null ? System.Array.Empty<string>() : new[] { Str(diagnostic["message"]) }).Concat(SpecialBlockedMessages(raw, safe, rule)).Concat(BlockedPlanMessages(safe, budget, Str(context["budgetPolicy"])));
            return Object(("customer", customer), ("executionPlans", Array(Objects(execution).Take(planLimit))), ("budget", primary?["budget"] ?? safe.FirstOrDefault(p => p["budget"] != null)?["budget"]),
                ("blockedMessages", Array(Unique(messages))), ("blockedDiagnostic", diagnostic), ("recipes", Array(recipeRows)), ("beverages", Array(beverageRows)));
            });
            // 当前订单始终来自本轮输入，缓存命中不能延长输入版本或改变后续主线程许可检查。
            projection["order"] = Clone(order); recommendations.Add(projection);
        }
        return result;
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
