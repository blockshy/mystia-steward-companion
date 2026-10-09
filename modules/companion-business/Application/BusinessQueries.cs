using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Orders;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using MystiaStewardCompanion.Business.Domain.Support;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using System.Text.RegularExpressions;
using MystiaStewardCompanion.Contracts;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Application;

/// <summary>
/// 业务查询组合根。所有上下文来自宿主冻结值，外部页面只选择地点、客人和标签；
/// 同一订单结果同时供展示、自动化和游戏辅助消费，页面是否打开不会改变执行主方案。
/// </summary>
public static class BusinessQueries
{
    /// <summary>
    /// 将当前帧的经营代次和特殊目标修订绑定到普通订单目标。候选选择本身没有执行权，只有本方法确认
    /// 快照内存在唯一、完整且仍未结算的订单身份后，目标才可同时供详情、调度和游戏辅助使用。
    /// 返回独立副本；旧结果、重复订单或缺失身份绝不通过重新盖章取得当前目标资格。
    /// </summary>
    public static JsonObject BindCurrentNormalTargets(JsonObject result, JsonObject snapshot)
    {
        var bound = Obj(Clone(result));
        var orders = Objects(Obj(snapshot["normalBusiness"])["orders"])
            .GroupBy(OrderIdentityAndSorting.NormalKey).ToDictionary(group => group.Key, group => group.ToArray());
        var selections = Objects(result["normalExecutionTargets"]).ToArray();
        var duplicateKeys = selections.GroupBy(item => Str(item["orderKey"]))
            .Where(group => group.Count() != 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var targets = new JsonArray();
        foreach (var selection in selections)
        {
            var row = Obj(Clone(selection));
            if (selection["target"] is not JsonObject target) { targets.Add(row); continue; }
            var key = Str(selection["orderKey"]);
            var valid = !duplicateKeys.Contains(key) && orders.TryGetValue(key, out var matches) && matches.Length == 1;
            var order = valid ? orders[key][0] : null;
            valid = valid && order != null && !Bool(order["hasEvaluated"])
                && SafeInteger(order["orderLifecycleSequence"]) && Num(order["orderLifecycleSequence"]) > 0
                && Integer(order["deskCode"]) && Num(order["deskCode"]) >= 0
                && Regex.IsMatch(Str(order["traceId"]), @"^N-[0-9]{1,16}$")
                && Regex.IsMatch(Str(order["orderKey"]), @"^ptr:[0-9a-f]{1,16}$")
                && Regex.IsMatch(Str(order["orderKey"])[4..], "[1-9a-f]")
                && Integer(order["foodId"]) && Integer(order["beverageId"])
                && Num(target["matchFoodId"], -1) == Num(order["foodId"])
                && Num(target["matchBeverageId"], -1) == Num(order["beverageId"])
                && SafeInteger(snapshot["nightBusinessGeneration"]) && Num(snapshot["nightBusinessGeneration"]) > 0;
            if (!valid)
            {
                row["target"] = null;
                row["message"] = "普通订单身份或经营代次已变化，等待当前快照重新选择执行目标。";
            }
            else
            {
                var next = Obj(Clone(target));
                foreach (var field in SpecialBusinessRules.BuildWirePolicy(snapshot["specialBusiness"],
                    Str(order!["specialBusinessRole"]), Num(snapshot["nightBusinessGeneration"]))) next[field.Key] = Clone(field.Value);
                row["target"] = next;
            }
            targets.Add(row);
        }
        bound["normalExecutionTargets"] = targets;
        return bound;
    }

    public static JsonObject BuildOrderPayload(JsonObject snapshot, JsonObject data, JsonObject preferences,
        JsonObject favorites, JsonObject customRecipes, JsonArray rejectedRecipeKeys) => Object(
        ("orders", Obj(snapshot["nightBusiness"])["orders"] ?? new JsonArray()),
        ("normalOrders", Obj(snapshot["normalBusiness"])["orders"] ?? new JsonArray()),
        ("activeRareGuests", Obj(snapshot["nightBusiness"])["activeRareGuests"] ?? new JsonArray()),
        ("runtime", snapshot["recommendationState"]), ("specialBusiness", snapshot["specialBusiness"]),
        ("favorites", favorites), ("customRecipes", customRecipes), ("preferences", preferences), ("data", data),
        ("specialBusinessRejectedRecipeKeys", rejectedRecipeKeys),
        ("includeNormalOrderDetails", true), ("includeNormalExecutionTargets", true), ("usage", "display"));

    /// <summary>在服务端权威上下文中执行只读页面查询，缺失输入时返回明确空结果。</summary>
    public static JsonObject EvaluatePage(JsonObject query, JsonObject snapshot, JsonObject data, JsonObject preferences,
        JsonObject favorites, JsonObject customRecipes, OrderCandidateCache? cache = null)
    {
        cache ??= new OrderCandidateCache();
        query = BusinessProtocol.ValidatePageQuery(query);
        // 页面推荐只读取下列六组值。经营观察时间、订单送达状态、当前输入版本和设备许可不参与纯页面规则；
        // 宿主必须在每次返回/执行时重新绑定它们。完整保留runtime及目录等值，避免遗漏库存、偏好或收藏变更。
        var input = Object(("query", query), ("runtime", snapshot["recommendationState"]), ("data", data),
            ("preferences", preferences), ("favorites", favorites), ("customRecipes", customRecipes));
        return cache.Page(input, () => EvaluatePageCore(query, snapshot["recommendationState"], data,
            preferences, favorites, customRecipes, cache));
    }

    /// <summary>生成页面纯展示投影；所有高成本配对和排序都位于完整输入缓存内。</summary>
    private static JsonObject EvaluatePageCore(JsonObject query, JsonNode? runtime, JsonObject data,
        JsonObject preferences, JsonObject favorites, JsonObject customRecipes, OrderCandidateCache cache)
    {
        var kind = Str(query["kind"]);
        var sets = Cookers.BuildRuntimeSets(runtime, data);
        if (sets == null || Str(data["source"]) != "runtime") return Empty(kind);
        if (kind == "normal")
        {
            var context = Object(("availableRecipeIds", sets["recipeIds"]), ("availableBeverageIds", sets["beverageIds"]),
                ("disabledIngredientIds", sets["unavailableIngredientIds"]), ("popularFoodTag", runtime?["popularFoodTag"]),
                ("popularHateFoodTag", runtime?["popularHateFoodTag"]), ("famousShopEnabled", runtime?["famousShopEnabled"]),
                ("tagPriorityRules", data["tagPriorityRules"]));
            var options = Object(("data", data), ("place", query["selectedPlace"]), ("context", context));
            return Object(("kind", kind), ("recipes", Array(RecommendationEngine.BuildNormalFoodRecommendations(options).Take(8))),
                ("beverages", Array(RecommendationEngine.BuildNormalBeverageRecommendations(options).Take(8))));
        }
        var customer = Objects(data["rareCustomers"]).SingleOrDefault(item => Num(item["id"]) == Num(query["customerId"], -1));
        var foodTag = Str(query["foodTag"]); var beverageTag = Str(query["beverageTag"]);
        if (customer == null || foodTag.Length == 0 || beverageTag.Length == 0) return Empty(kind);
        var candidateContext = RuntimeRecommendationSupport.BuildRecommendationRuntimeContext(Obj(runtime), sets, preferences, data);
        var sortContext = RuntimeRecommendationSupport.BuildRecommendationPlanSortContext(favorites, (int)Num(customer["id"]), foodTag, beverageTag, preferences);
        var demand = Object(("type", "rare-tag-order"), ("customer", customer), ("requiredFoodTag", foodTag), ("requiredBeverageTag", beverageTag));
        var foods = cache.Food(data, demand, candidateContext);
        var custom = CustomRecipes.BuildCustomFoodCandidates(Object(("customRecipes", customRecipes), ("data", data),
            ("customer", customer), ("requiredFoodTag", foodTag), ("requiredBeverageTag", beverageTag), ("context", candidateContext)));
        foods = CustomRecipes.MergeCustomFoodCandidates(foods, custom);
        var beverages = cache.Beverage(data, demand, candidateContext);
        var rowOptions = Object(("variantLimitPerBase", preferences["recipeVariantLimitPerBase"]), ("limit", 8),
            ("budget", candidateContext["budget"]), ("budgetPolicy", candidateContext["budgetPolicy"]),
            ("sortProfile", preferences["recommendationSortProfile"]), ("sortContext", sortContext));
        return Object(("kind", kind), ("recipes", OrderRecommendationService.DeriveRecipeRowsFromCandidates(foods, beverages, rowOptions)),
            ("beverages", OrderRecommendationService.DeriveBeverageRowsFromCandidates(beverages, foods, rowOptions)));
    }
    private static JsonObject Empty(string kind) => Object(("kind", kind), ("recipes", new JsonArray()), ("beverages", new JsonArray()));
}
