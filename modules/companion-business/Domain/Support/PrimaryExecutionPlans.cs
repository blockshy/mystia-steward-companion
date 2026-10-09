using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Support.SupportJson;

namespace MystiaStewardCompanion.Business.Domain.Support;

/// <summary>统一主执行方案规则，确保界面、自动化和游戏辅助读取同一排序首项。</summary>
public static class PrimaryExecutionPlans
{
    /// <summary>收藏限定只在相应自动化阶段实际启用且订单允许自动化时生效。</summary>
    public static JsonObject BuildPrimaryExecutionPlanPolicy(JsonObject preferences, bool automationAllowed = true)
    {
        var enabled = automationAllowed && Bool(preferences["automationEnabled"]) && Bool(preferences["autoRareOrderEnabled"]);
        return new JsonObject
        {
            ["requireRecipeFavorite"] = enabled && Bool(preferences["autoPrepStartCooking"]) && Bool(preferences["autoPrepRecipeFavoritesOnly"]),
            ["requireBeverageFavorite"] = enabled && Bool(preferences["autoPrepTakeBeverage"]) && Bool(preferences["autoPrepBeverageFavoritesOnly"]),
        };
    }

    /// <summary>只提升合法主方案，其余候选保持原有相对顺序；任务优先也不能绕过收藏限定。</summary>
    public static JsonArray NormalizePrimaryExecutionPlans(JsonArray plans, JsonObject sortContext, JsonObject policy)
    {
        var ordered = plans.OfType<JsonObject>().ToList();
        var missionIndex = ordered.FindIndex(plan => IsMissionRecipeExecutionPlan(plan, sortContext) && Satisfies(plan, sortContext, policy));
        var index = missionIndex;
        if (index < 0 && (Bool(policy["requireRecipeFavorite"]) || Bool(policy["requireBeverageFavorite"])))
            index = ordered.FindIndex(plan => Satisfies(plan, sortContext, policy));
        if (index > 0) { var primary = ordered[index]; ordered.RemoveAt(index); ordered.Insert(0, primary); }
        return Array(ordered);
    }

    /// <summary>读取唯一首选，返回副本避免消费者修改共享计划。</summary>
    public static JsonObject? GetPrimaryExecutionPlan(JsonArray plans) => Clone(plans.FirstOrDefault()) as JsonObject;

    /// <summary>任务料理必须同时匹配两个配方标识，并完整通过料理、酒水和组合硬约束。</summary>
    public static bool IsMissionRecipeExecutionPlan(JsonObject plan, JsonObject context)
        => Recommendation.RecommendationEngine.IsMissionRecipeExecutionPlan(plan, context);

    /// <summary>标准化加料集合用于收藏身份，禁止借助显示名称匹配配方。</summary>
    public static string BuildPlanRecipeKey(JsonObject plan)
    {
        if (plan["food"] is not JsonObject food) return "";
        var extras = Items(food["extraIngredients"]).Select(v => Number(v?["id"], -1)).Where(v => double.IsFinite(v) && v >= 0).Select(v => Math.Truncate(v)).Distinct().OrderBy(v => v);
        return $"{Int(food["recipe"]?["id"])}:{string.Join(",", extras)}";
    }

    private static bool Satisfies(JsonObject plan, JsonObject context, JsonObject policy)
        => Text(plan["bucket"]) != "blocked"
            && (!Bool(policy["requireRecipeFavorite"]) || plan["food"] != null && StringSet(context["favoriteRecipeKeys"]).Contains(BuildPlanRecipeKey(plan)))
            && (!Bool(policy["requireBeverageFavorite"]) || plan["beverage"] != null && IntSet(context["favoriteBeverageIds"]).Contains(Int(plan["beverage"]?["beverage"]?["id"], -1)));
}
