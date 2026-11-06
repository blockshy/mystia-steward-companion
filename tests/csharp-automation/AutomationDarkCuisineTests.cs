using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application.Automation;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>验证特殊成品与真实配方/保温箱供给的调度边界，不模拟或调用游戏。</summary>
internal static class AutomationDarkCuisineTests
{
    internal static void Run(Action<bool, string> check, Func<JsonObject> createInput)
    {
        var input = createInput();
        var order = J.Objects(J.Obj(J.Obj(input["snapshot"])["nightBusiness"])["orders"]).Single();
        order["missionRecipePriority"] = J.Object(("productKind", "dark-cuisine"), ("foodId", -1),
            ("recipeId", null), ("missionGeneration", 2), ("businessGeneration", 1));
        var item = J.Objects(J.Obj(input["recommendations"])["recommendations"]).Single();
        item["order"] = J.Clone(order);
        var food = J.Obj(J.Objects(item["executionPlans"]).Single()["food"]);
        food["productKind"] = "dark-cuisine"; food["supplySource"] = "stored";
        var recipe = J.Obj(food["recipe"]);
        recipe["id"] = -1; recipe["name"] = "黑暗料理"; recipe["recipeId"] = null; recipe["cooker"] = "";
        food["extraIngredients"] = new JsonArray();
        food["productionRecipe"] = null;
        var runtime = J.Obj(J.Obj(input["snapshot"])["recommendationState"]);
        runtime["placedCookers"] = new JsonArray(); runtime["placedCookerTypeIds"] = new JsonArray();
        runtime["storedDarkCuisineCount"] = 1;
        // 原客户端明确让收藏限定优先于任务置顶；特殊成品不可收藏时保留该限制并解释原因。
        var favoriteOnly = J.Obj(J.Clone(input));
        J.Obj(favoriteOnly["preferences"])["autoPrepRecipeFavoritesOnly"] = true;
        var favoriteResult = new AutomationCoordinator().Advance(favoriteOnly, 1500);
        check(J.Arr(favoriteResult["commands"]).Count == 0
            && J.Obj(favoriteResult["states"]).Any(pair => J.Str(J.Obj(pair.Value)["detailMessage"]).Contains("黑暗料理无法收藏")),
            "任务黑暗料理不绕过收藏限定，等待信息明确指出可解决的配置冲突");
        var engine = new AutomationCoordinator();
        var command = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
        var payload = J.Obj(command["payload"]);
        check(J.Num(payload["foodId"]) == -1 && J.Str(payload["foodProductionKind"]) == "dark-cuisine"
            && J.Str(payload["foodSupplySource"]) == "stored" && J.Num(payload["missionGeneration"]) == 2,
            "特殊产物和任务代际被完整传递，缺省负ID不代替种类");
        check(J.Num(payload["cookerControllerIndex"], -1) == -1 && J.Num(payload["recipeId"], -1) == -1,
            "保温箱供给没有伪造配方0或预约厨具");
        engine.Complete((long)J.Num(command["requestId"]), J.Object(("ok", true),
            ("automation", J.Object(("outcome", "waiting"), ("stage", "cooking-start"), ("retryAfterMs", 1000))),
            ("steps", new JsonArray())), 1600);
        var refreshed = createInput();
        var refreshedRuntime = J.Obj(J.Obj(refreshed["snapshot"])["recommendationState"]);
        J.Obj(input["snapshot"])["recommendationState"] = J.Clone(refreshedRuntime);
        food["supplySource"] = "cook"; recipe["recipeId"] = 200; recipe["cooker"] = "煮锅";
        food["productionRecipe"] = J.Object(("id", 100), ("recipeId", 200));
        food["extraIngredients"] = J.Array(new[] { J.Object(("id", 3)) });
        command = J.Objects(engine.Advance(input, 3500)["commands"]).Single();
        payload = J.Obj(command["payload"]);
        check(J.Str(payload["foodSupplySource"]) == "cook" && J.Num(payload["foodId"]) == -1
            && J.Num(payload["recipeId"]) == 200 && J.Num(payload["cookerControllerIndex"], -1) >= 0,
            "现成成品被抢先取走后，未提交目标随新方案改为真实配方并重新预约厨具");
        engine.Complete((long)J.Num(command["requestId"]), J.Object(("ok", false),
            ("automation", J.Object(("outcome", "blocked"), ("stage", "cooking-delivery"), ("reasonCode", "food-delivery-commit-uncertain"))),
            ("steps", J.Array(new[] { J.Object(("code", "food-delivery-commit-uncertain"), ("ok", false), ("message", "取餐结果未知")) }))), 3600);
        check(J.Arr(engine.Advance(input, 6000)["commands"]).Count == 0,
            "特殊成品取餐/送达结果未知后不会自动重复取餐或开锅");
    }
}