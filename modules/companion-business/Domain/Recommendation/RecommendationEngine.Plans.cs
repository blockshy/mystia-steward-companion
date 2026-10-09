using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Recommendation;

public static partial class RecommendationEngine
{
    /// <summary>从原始需求搜索候选，保持料理 80、酒水 40 的组合上限。</summary>
    public static JsonArray BuildRareOrderPlans(JsonObject options)
    {
        var demand = Demand(options); var data = Obj(options["data"]); var context = Obj(options["context"]);
        var expanded = (JsonObject)Clone(options)!;
        expanded["foodCandidates"] = Array(Objects(BuildRareFoodCandidates(data, demand, context)).Take(80));
        expanded["beverageCandidates"] = Array(Objects(BuildRareBeverageCandidates(data, demand, context)).Take(40));
        return BuildRareOrderPlansFromCandidates(expanded);
    }
    /// <summary>组合预先筛选的候选，允许上层统一注入收藏、自定义料理和任务候选。</summary>
    public static JsonArray BuildRareOrderPlansFromCandidates(JsonObject options)
    {
        var demand = Demand(options); var context = Obj(options["context"]);
        var foods = Objects(options["foodCandidates"]).ToArray(); var drinks = Objects(options["beverageCandidates"]).ToArray();
        if (foods.Length == 0 || drinks.Length == 0) return new JsonArray(BlockedPlan(demand, foods.FirstOrDefault(), drinks.FirstOrDefault(), context, Obj(options["data"])));
        var plans = Array(foods.SelectMany(food => drinks.Select(drink => RarePlan(demand, food, drink, context))));
        var sorted = SortRareOrderPlans(plans, options["sortProfile"] as JsonObject, options["sortContext"] as JsonObject);
        var limit = NullableNumber(options["limit"]);
        return limit is null ? sorted : Array(Objects(sorted).Take((int)Math.Clamp(Math.Truncate(limit.Value), 0, int.MaxValue)));
    }
    private static JsonObject Demand(JsonObject options)
    {
        var demand = Object(("type", "rare-tag-order"), ("customer", options["customer"]), ("requiredFoodTag", Str(options["requiredFoodTag"])), ("requiredBeverageTag", Str(options["requiredBeverageTag"])));
        if (options["specialFoodTarget"] is not null) demand["specialFoodTarget"] = Clone(options["specialFoodTarget"]);
        return demand;
    }
    private static JsonObject RarePlan(JsonObject demand, JsonObject food, JsonObject beverage, JsonObject context)
    {
        var price = Math.Max(0, Num(Obj(food["recipe"])["price"])) + Math.Max(0, Num(Obj(beverage["beverage"])["price"]));
        var budget = BuildBudgetResult(price, context["budget"] as JsonObject, Str(context["budgetPolicy"], "block"));
        var condition = BuildBudgetCondition(budget);
        var conditions = Array(Objects(food["conditionResults"]).Concat(Objects(beverage["conditionResults"])));
        if (condition is not null) conditions.Add(condition);
        var bucket = HasHardFailures(conditions) ? "blocked" : Bool(food["meetsRequiredFood"]) && Bool(beverage["meetsRequiredBeverage"])
            ? Objects(conditions).Any(x => Str(x["status"]) == "warn") ? "tradeoff" : "complete" : "preference";
        var reasons = new List<string>();
        if (Bool(food["meetsRequiredFood"])) reasons.Add($"料理满足 {Str(demand["requiredFoodTag"])}");
        if (Arr(food["matchedSpecialFoodTargetTags"]).Count > 0) reasons.Add($"特殊目标 {string.Join("、", Strings(food["matchedSpecialFoodTargetTags"]))}");
        if (Bool(beverage["meetsRequiredBeverage"])) reasons.Add($"酒水满足 {Str(demand["requiredBeverageTag"])}");
        if (Arr(food["matchedPositiveTags"]).Count > 0) reasons.Add($"料理偏好 {string.Join("、", Strings(food["matchedPositiveTags"]))}");
        if (Arr(beverage["matchedTags"]).Count > 0) reasons.Add($"酒水偏好 {string.Join("、", Strings(beverage["matchedTags"]))}");
        return Object(("demand", demand), ("food", food), ("beverage", beverage), ("bucket", bucket), ("estimatedPrice", price), ("budget", budget),
            ("conditionResults", conditions), ("reasons", Array(reasons)), ("warnings", Array(Objects(conditions).Where(x => Str(x["status"]) is "warn" or "fail").Select(x => Str(x["detail"])))));
    }
    /// <summary>构建预算结果，保留未知和显式不付款；缺失 willPayMoney 时不额外输出字段。</summary>
    public static JsonObject? BuildBudgetResult(double estimatedPrice, JsonObject? budget, string policy)
    {
        if (budget is null) return null;
        var remaining = NullableNonNegativeInt(budget["remainingBudget"]);
        var result = Object(("estimatedPrice", estimatedPrice), ("remainingBudget", remaining), ("overBudget", remaining is null ? 0 : Math.Max(0, estimatedPrice - remaining.Value)), ("policy", policy), ("source", budget["source"]));
        if (budget.ContainsKey("willPayMoney")) result["willPayMoney"] = Clone(budget["willPayMoney"]);
        return result;
    }
    /// <summary>预算解释与方案准入共用结果，不额外维护一套诊断算法。</summary>
    public static JsonObject? BuildBudgetCondition(JsonObject? budget)
    {
        if (budget is null) return null;
        var policy = Str(budget["policy"]); var status = "pass"; var severity = "hard"; string detail;
        string BudgetDetail(string prefix) => $"{prefix}，预估 {Key(Num(budget["estimatedPrice"]))} / 剩余预算 {(budget["remainingBudget"] is null ? "未知" : Key(Num(budget["remainingBudget"])))}。";
        if (policy == "ignore") { status = "info"; severity = "info"; detail = BudgetDetail("预算约束未启用"); }
        else if (budget["willPayMoney"] is not null && !Bool(budget["willPayMoney"], true)) { status = policy == "block" ? "fail" : "warn"; severity = policy == "block" ? "hard" : "soft"; detail = "稀客当前不会付款。"; }
        else if (budget["remainingBudget"] is null) { status = "info"; severity = "info"; detail = $"预算未知，方案预估 {Key(Num(budget["estimatedPrice"]))}。"; }
        else if (Num(budget["overBudget"]) > 0) { status = policy == "block" ? "fail" : "warn"; severity = policy == "block" ? "hard" : "soft"; detail = BudgetDetail($"超出预算 {Key(Num(budget["overBudget"]))}"); }
        else detail = BudgetDetail("未超预算");
        return Condition("plan.budget", "plan", status, severity, "预算", detail);
    }
    private static JsonObject BlockedPlan(JsonObject demand, JsonObject? food, JsonObject? beverage, JsonObject context, JsonObject data)
    {
        var conditions = new JsonArray();
        if (food is null)
        {
            var excluded = Numbers(context["excludedIngredientIds"]);
            if (excluded.Length > 0)
            {
                var byId = Objects(data["ingredients"]).GroupBy(x => Num(x["id"])).ToDictionary(x => x.Key, x => x.Last());
                var names = excluded.Select(id => byId.TryGetValue(id, out var item) ? Str(item["name"]) : $"#{Key(id)}").Where(x => x.Length > 0);
                conditions.Add(Condition("food.excluded-ingredients", "food", "fail", "hard", "排除材料", $"没有可用料理能避开排除材料：{string.Join("、", names)}。"));
            }
            conditions.Add(Condition("plan.missing-food", "plan", "fail", "hard", "料理方案", "没有可执行的料理候选。"));
        }
        if (beverage is null)
        {
            var excluded = Numbers(context["excludedBeverageIds"]);
            if (excluded.Length > 0)
            {
                var byId = Objects(data["beverages"]).GroupBy(x => Num(x["id"])).ToDictionary(x => x.Key, x => x.Last());
                var names = excluded.Select(id => byId.TryGetValue(id, out var item) ? Str(item["name"]) : $"#{Key(id)}").Where(x => x.Length > 0);
                conditions.Add(Condition("beverage.excluded", "beverage", "fail", "hard", "排除酒水", $"没有可用酒水能避开排除酒水：{string.Join("、", names)}。"));
            }
            conditions.Add(Condition("plan.missing-beverage", "plan", "fail", "hard", "酒水方案", "没有可执行的酒水候选。"));
        }
        return Object(("demand", demand), ("food", food), ("beverage", beverage), ("bucket", "blocked"), ("estimatedPrice", 0), ("budget", null),
            ("conditionResults", conditions), ("reasons", new JsonArray()), ("warnings", Array(Objects(conditions).Select(x => Str(x["detail"])))));
    }
}
