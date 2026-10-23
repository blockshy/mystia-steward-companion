using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Support.SupportJson;

namespace MystiaStewardCompanion.Business.Domain.Support;

/// <summary>将经过宿主验证的游戏快照、生效配置与目录投影为纯推荐上下文。</summary>
public static class RuntimeRecommendationSupport
{
    public static JsonObject? BuildRuntimeSets(JsonNode? runtime, JsonObject data) => Cookers.BuildRuntimeSets(runtime, data);

    /// <summary>
    /// 预算由调用方明确提供；集合统一复制，不把请求方持有的 JsonNode 直接放入返回值。
    /// 厨具类型读取不完整时沿用目录证据的拒绝规则，不以 UI 是否可见推断可开锅。
    /// </summary>
    public static JsonObject BuildRecommendationRuntimeContext(JsonObject runtime, JsonObject runtimeSets, JsonObject preferences, JsonObject data, JsonObject? options = null)
    {
        var unavailable = Bool(runtimeSets["hasCookerSnapshot"]) && Items(runtimeSets["runtimeUnavailableCookerNames"]).Any();
        return new JsonObject
        {
            ["availableRecipeIds"] = Clone(runtimeSets["recipeIds"]),
            ["availableIngredientIds"] = Clone(runtimeSets["ingredientIds"]),
            ["availableBeverageIds"] = Clone(runtimeSets["beverageIds"]),
            ["disabledIngredientIds"] = new JsonArray(),
            ["excludedIngredientIds"] = Clone(preferences["recommendationExclusions"]?["excludedIngredientIds"]) ?? new JsonArray(),
            ["excludedBeverageIds"] = Clone(preferences["recommendationExclusions"]?["excludedBeverageIds"]) ?? new JsonArray(),
            ["ownedIngredientQty"] = Clone(runtimeSets["ownedIngredientQty"]) ?? new JsonObject(),
            ["ownedBeverageQty"] = Clone(runtimeSets["ownedBeverageQty"]) ?? new JsonObject(),
            ["placedCookerNames"] = Cookers.BuildRecommendationCookerNameSet(runtimeSets, Bool(preferences["filterMissingCookers"])),
            ["hasCookerSnapshot"] = Bool(runtimeSets["hasCookerSnapshot"]),
            ["popularFoodTag"] = Clone(runtime["popularFoodTag"]),
            ["popularHateFoodTag"] = Clone(runtime["popularHateFoodTag"]),
            ["famousShopEnabled"] = Bool(runtime["famousShopEnabled"]),
            ["tagPriorityRules"] = Clone(data["tagPriorityRules"]) ?? new JsonArray(),
            ["maxExtraIngredients"] = 4,
            ["filterMissingCookers"] = Bool(preferences["filterMissingCookers"]) || unavailable,
            ["budget"] = Clone(options?["budget"]),
            ["budgetPolicy"] = Text(preferences["recommendationBudgetPolicy"], "block"),
        };
    }

    /// <summary>根据客人及点单标签构造收藏排序信号，收藏不会在此处绕过候选硬约束。</summary>
    public static JsonObject BuildRecommendationPlanSortContext(JsonObject favorites, int customerId, string foodTag, string beverageTag, JsonObject preferences)
    {
        return new JsonObject
        {
            ["favoriteRecipeKeys"] = Strings(Items(favorites["recipes"]).OfType<JsonObject>()
                .Where(f => Int(f["customerId"], -1) == customerId && Text(f["foodTag"]) == foodTag)
                .Select(f => $"{Int(f["recipeId"])}:{string.Join(",", CustomRecipes.NormalizeIdList(f["extraIngredientIds"]))}").Distinct()),
            ["favoriteBeverageIds"] = Integers(Items(favorites["beverages"]).OfType<JsonObject>()
                .Where(f => Int(f["customerId"], -1) == customerId && Text(f["beverageTag"]) == beverageTag)
                .Select(f => Int(f["beverageId"])).Distinct()),
            ["pinFavoriteRecipe"] = Bool(preferences["pinFavoriteRecipeEnabled"]),
            ["pinFavoriteBeverage"] = Bool(preferences["pinFavoriteBeverageEnabled"]),
        };
    }
}
