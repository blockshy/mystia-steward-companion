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
        bound["normalExecutionTargets"] = BindNormalTargetRows(result["normalExecutionTargets"], snapshot);
        return bound;
    }

    /// <summary>
    /// 仅复制并重绑当前普客执行目标。宿主可同时保留冻结的推荐结果引用，无需为了这一小组目标
    /// 复制所有稀客配方与展示行；本方法不修改输入，返回的数组及其子节点由调用方独立拥有。
    /// 身份、重复目标、经营代次和特殊营业检查与完整 JSON 入口共用，不能通过复用跳过校验。
    /// </summary>
    public static JsonArray BindNormalTargetRows(JsonNode? selectionsNode, JsonObject snapshot)
    {
        var orders = Objects(Obj(snapshot["normalBusiness"])["orders"])
            .GroupBy(OrderIdentityAndSorting.NormalKey).ToDictionary(group => group.Key, group => group.ToArray());
        var selections = Objects(selectionsNode).ToArray();
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
        return targets;
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
        // 页面缓存只保存纯展示结果。原生厨具的忙闲、位置、对象身份和诊断文字不参与下面的页面计算，
        // 不能把整份运行时快照作为键，否则每次观察都会新建一个最终投影并挤占经营订单的缓存。
        // 这里完整保留两类页面实际消费的派生集合/上下文；库存、锁定类型、流行标签和预算策略等仍会失效。
        // 实际求解继续接收原始输入，宿主也仍逐次验证输入版本、来源和权限，不由缓存命中取得当前资格。
        var input = Object(("query", query), ("runtime", BuildPageCacheRuntimeInput(snapshot["recommendationState"], data, preferences)), ("data", data),
            ("preferences", preferences), ("favorites", favorites), ("customRecipes", customRecipes));
        return cache.Page(input, () => EvaluatePageCore(query, snapshot["recommendationState"], data,
            preferences, favorites, customRecipes, cache));
    }

    /// <summary>
    /// 将运行时快照投影到页面规则的完整读取闭包。稀客使用统一推荐上下文，普客额外保留其直接读取的
    /// 原始流行/厌恶/名店值，避免把两类入口的默认值或类型转换语义混为一谈。集合中的数量不做压力
    /// 归一化，真实库存变化仍产生新页面键；缺失运行时与有效但为空的运行时也保持区分。
    /// 返回节点仅供本次私有键拥有，不修改调用方的快照、目录或配置。
    /// </summary>
    private static JsonNode? BuildPageCacheRuntimeInput(JsonNode? runtime, JsonObject data, JsonObject preferences)
    {
        var sets = Cookers.BuildRuntimeSets(runtime, data);
        if (sets == null) return null;
        return Object(("sets", sets),
            ("rareContext", RuntimeRecommendationSupport.BuildRecommendationRuntimeContext(Obj(runtime), sets, preferences, data)),
            ("normalContext", Object(("popularFoodTag", runtime?["popularFoodTag"]),
                ("popularHateFoodTag", runtime?["popularHateFoodTag"]), ("famousShopEnabled", runtime?["famousShopEnabled"]))));
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
