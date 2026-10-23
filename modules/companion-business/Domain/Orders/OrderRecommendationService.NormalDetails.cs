using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Orders;

public static partial class OrderRecommendationService
{
    /// <summary>将原订单和选择结果分别投影为详情，不用展示字段反向驱动执行。</summary>
    public static JsonObject BuildNormalDetail(JsonObject payload, JsonObject order, OrderCandidateCache? cache = null)
    {
        var data = Obj(payload["data"]); var runtime = Obj(payload["runtime"]); var originalRecipe = ResolveRecipe(data, Num(order["foodId"]), null);
        var originalBeverage = Objects(data["beverages"]).LastOrDefault(x => Num(x["id"]) == Num(order["beverageId"]));
        var originalFood = FoodDetail(data, runtime, originalRecipe, Num(order["foodId"]), originalRecipe?["recipeId"], SpecialBusinessRules.TextOr(Str(order["foodName"]), SpecialBusinessRules.TextOr(Str(originalRecipe?["name"]), $"料理 #{Key(Num(order["foodId"]))}")), new JsonArray(), originalRecipe?["positiveTags"], new JsonArray());
        var originalDrink = BeverageDetail(data, originalBeverage, Num(order["beverageId"]), SpecialBusinessRules.TextOr(Str(order["beverageName"]), SpecialBusinessRules.TextOr(Str(originalBeverage?["name"]), $"酒水 #{Key(Num(order["beverageId"]))}")), originalBeverage?["tags"]);
        var selection = NormalTargetSelector.Select(NormalArgs(payload, order), cache); var target = selection["target"] as JsonObject;
        var executionFood = originalFood; var executionBeverage = originalDrink;
        if (target != null)
        {
            var recipe = ResolveRecipe(data, Num(target["foodId"]), NullableNumber(target["recipeId"])); var beverage = Objects(data["beverages"]).LastOrDefault(x => Num(x["id"]) == Num(target["beverageId"]));
            executionFood = FoodDetail(data, runtime, recipe, Num(target["foodId"]), target["recipeId"], SpecialBusinessRules.TextOr(Str(target["recipeName"]), SpecialBusinessRules.TextOr(Str(recipe?["name"]), $"料理 #{Key(Num(target["foodId"]))}")), Arr(target["extraIngredientIds"]), target["foodTags"], target["specialTargetFoodTags"]);
            executionBeverage = BeverageDetail(data, beverage, Num(target["beverageId"]), SpecialBusinessRules.TextOr(Str(target["beverageName"]), SpecialBusinessRules.TextOr(Str(beverage?["name"]), $"酒水 #{Key(Num(target["beverageId"]))}")), target["beverageTags"]);
        }
        return Object(("order", order), ("originalFood", originalFood), ("originalBeverage", originalDrink), ("executionFood", executionFood), ("executionBeverage", executionBeverage), ("executionReason", Str(target?["reason"])), ("selectionMessage", selection["message"]), ("usesSpecialExecution", target != null), ("hasExecutionOverride", target != null && (Num(executionFood["foodId"]) != Num(order["foodId"]) || Num(executionBeverage["beverageId"]) != Num(order["beverageId"]) || Count(target, "extraIngredientIds") > 0)));
    }
    private static JsonObject? ResolveRecipe(JsonObject data, double foodId, double? recipeId) => Objects(data["recipes"]).LastOrDefault(x => Num(x["id"]) == foodId) ?? (recipeId == null ? null : Objects(data["recipes"]).FirstOrDefault(x => Num(x["recipeId"]) == recipeId));
    private static JsonObject FoodDetail(JsonObject data, JsonObject runtime, JsonObject? recipe, double id, JsonNode? recipeId, string name, JsonArray extraIds, JsonNode? fallback, JsonNode? target)
    {
        var byId = Objects(data["ingredients"]).GroupBy(x => Num(x["id"])).ToDictionary(x => x.Key, x => x.Last()); var extras = Array(Numbers(extraIds).Where(byId.ContainsKey).Select(x => byId[x]));
        var resolved = recipe == null ? Object(("activeTags", Array(Unique(Strings(fallback)))), ("suppressedTags", new JsonArray())) : RecommendationEngine.ResolveFoodTags(Object(("recipe", recipe), ("extraIngredients", extras), ("popularFoodTag", runtime["popularFoodTag"]), ("popularHateFoodTag", runtime["popularHateFoodTag"]), ("famousShopEnabled", Bool(runtime["famousShopEnabled"])), ("tagPriorityRules", data["tagPriorityRules"])));
        return Object(("recipe", recipe), ("foodId", id), ("recipeId", recipeId), ("name", name), ("cookerName", Str(recipe?["cooker"])), ("baseIngredientNames", recipe?["ingredients"] ?? new JsonArray()), ("extraIngredients", extras), ("extraIngredientIds", extraIds), ("activeTags", resolved["activeTags"]), ("suppressedTags", resolved["suppressedTags"]), ("targetTags", Array(Unique(Strings(target)))));
    }
    private static JsonObject BeverageDetail(JsonObject data, JsonObject? beverage, double id, string name, JsonNode? fallback)
    { var resolved = RecommendationEngine.ResolveTagPriority(Arr(beverage?["tags"] ?? fallback), Arr(data["tagPriorityRules"])); return Object(("beverage", beverage), ("beverageId", id), ("name", name), ("activeTags", resolved["activeTags"]), ("suppressedTags", resolved["suppressedTags"])); }
}
