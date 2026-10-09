using MystiaStewardCompanion.Business.Domain.Orders;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.SpecialBusiness;

public static partial class NormalTargetSelector
{
    /// <summary>血池地狱坚持原订单，目标标签不足时仅允许明确标记的受控推进。</summary>
    private static JsonObject SelectYuuma(JsonObject args, OrderCandidateCache cache)
    {
        var s = Obj(args["specialBusiness"]); var o = Obj(args["order"]); var data = Obj(args["data"]); var role = Str(o["specialBusinessRole"]).Trim(); var label = SpecialBusinessRules.TextOr(Str(s["displayName"]).Trim(), Str(s["challengeType"]));
        if (role == "yuuma-order-unverified") return Empty($"{label}订单角色身份尚未确认，自动化目标已暂停。");
        if (role != SpecialBusinessRules.YuumaRole) return Empty();
        if (NullableNumber(o["runtimeGuestId"]) != 1003) return Empty($"{label}订单运行时角色身份不完整：需要 runtimeGuestId=1003，当前为 {(o["runtimeGuestId"] == null ? "missing" : Key(Num(o["runtimeGuestId"])))}。");
        var rule = SpecialBusinessRules.BuildOrderRule(s, role); if (Str(rule["blockingReason"]).Length > 0) return Empty(Str(rule["blockingReason"]));
        if (args["runtime"] == null || Str(data["source"]) != "runtime") return Empty($"{label}等待完整运行时推荐数据后再计算料理目标。");
        var context = Context(args); if (context == null) return Empty($"{label}缺少完整库存、厨具或菜单运行时数据，暂不计算料理目标。");
        var recipe = Recipe(o, data); if (recipe == null) return Empty($"{label}无法找到原订单料理 {FoodLabel(o)} 的配方数据。");
        var drink = Beverage(o, data); if (drink == null) return Empty($"{label}无法找到原订单酒水 {BeverageLabel(o)} 的数据。");
        var customer = ExactCustomer(data, 1003); if (customer == null) return Empty($"{label}缺少运行时 characterId=1003 的完整料理、酒水喜好档案，暂不计算料理目标。");
        var demand = Demand(customer, target: rule["foodTarget"]); var exact = ExactData(data, recipe, drink);
        var foods = Objects(cache.Food(exact, demand, context, Object(("preserveTwoTagSpecialTargetReachability", true)))).Where(x => Number(x, "recipe", "id") == Num(recipe["id"])).ToArray();
        var beverages = Objects(cache.Beverage(exact, demand, context)).Where(x => NoHardFailures(x) && Number(x, "beverage", "id") == Num(drink["id"])).ToArray();
        double FoodScore(JsonObject f) => Count(f, "matchedSpecialFoodTargetTags") * 10000 + Count(f, "matchedPositiveTags") * 100 - Count(f, "matchedNegativeTags") * 1000 - Count(f, "extraIngredients") * 10 - Num(f["resourcePressure"]);
        double BeverageScore(JsonObject b) => Count(b, "matchedTags") * 100 + Math.Min(99, Math.Max(0, Num(b["ownedQuantity"])));
        var best = BestPair(foods.Where(x => NoHardFailures(x)), beverages, FoodScore, BeverageScore, (f, b) => FoodScore(f) + BeverageScore(b));
        var tags = Obj(rule["foodTarget"])["tags"]; var text = string.Join("、", Strings(tags));
        if (best != null) return Target(o, best.Value.Food, best.Value.Beverage, $"保持原订单料理与酒水，并同时满足{label}目标 Tag：{text}", Object(("specialTargetFoodTags", tags)));
        best = BestPair(foods.Where(x => NoHardFailures(x, "food.special-target-tag")), beverages, FoodScore, BeverageScore, (f, b) => FoodScore(f) + BeverageScore(b));
        if (best == null) return Empty(YuumaBlockMessage(label, recipe, drink, context, data, tags));
        var matched = Strings(best.Value.Food["matchedSpecialFoodTargetTags"]); var matchText = matched.Length > 0 ? $"仅命中 {matched.Length}/{Arr(tags).Count} 个目标 Tag：{string.Join("、", matched)}" : $"未命中当前目标 Tag：{text}";
        return Target(o, best.Value.Food, best.Value.Beverage, $"保持原订单料理与酒水；当前无法同时满足{label}目标 Tag，改用受控推进方案（{matchText}）。该方案会交由游戏原生低收益结算，可能造成较低伤害并增加狂暴。", Object(("allowYuumaControlledProgression", true), ("specialTargetFoodTags", tags)));
    }
    private static string YuumaBlockMessage(string label, JsonObject recipe, JsonObject beverage, JsonObject context, JsonObject data, JsonNode? tags)
    {
        var name = Str(recipe["name"]); var drink = Str(beverage["name"]);
        if (!Numbers(context["availableRecipeIds"]).Contains(Num(recipe["id"]))) return $"{label}原订单料理 {name} 尚未解锁，不能生成受控推进方案。";
        var byName = Objects(data["ingredients"]).GroupBy(x => Str(x["name"])).ToDictionary(x => x.Key, x => x.Last());
        var unavailable = Strings(recipe["ingredients"]).Where(n => !byName.TryGetValue(n, out var ingredient) || !Numbers(context["availableIngredientIds"]).Contains(Num(ingredient["id"])) || Numbers(context["disabledIngredientIds"]).Contains(Num(ingredient["id"])) || Numbers(context["excludedIngredientIds"]).Contains(Num(ingredient["id"]))).Distinct(StringComparer.Ordinal).ToArray();
        if (unavailable.Length > 0) return $"{label}原订单料理 {name} 的基础材料当前不可用或已排除：{string.Join("、", unavailable)}。";
        if (Bool(context["hasCookerSnapshot"]) && !Strings(context["placedCookerNames"]).Contains(Str(recipe["cooker"]))) return $"{label}原订单料理 {name} 所需厨具 {SpecialBusinessRules.TextOr(Str(recipe["cooker"]), "未知")} 当前不可用，不能生成受控推进方案。";
        if (!Numbers(context["availableBeverageIds"]).Contains(Num(beverage["id"]))) return $"{label}原订单酒水 {drink} 当前不可用，不能生成受控推进方案。";
        if (Numbers(context["excludedBeverageIds"]).Contains(Num(beverage["id"]))) return $"{label}原订单酒水 {drink} 已被排除，不能生成受控推进方案。";
        return $"{label}原订单 {name} / {drink} 没有通过料理、酒水与厨具硬门禁的受控推进方案；当前目标 Tag：{string.Join("、", Strings(tags))}。";
    }
}
