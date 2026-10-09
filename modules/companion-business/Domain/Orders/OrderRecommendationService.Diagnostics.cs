using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Orders;

public static partial class OrderRecommendationService
{
    /// <summary>按首个断开的阶段说明无方案原因，保留原阶段计数及签名供自动化对账。</summary>
    private static JsonObject BlockedDiagnostic(JsonObject data, JsonObject demand, JsonObject context, JsonObject sets, JsonArray generated, JsonObject[] merged, JsonObject[] foods, JsonObject[] beverages, JsonObject[] raw, JsonObject[] safe, JsonArray execution, JsonObject rule)
    {
        var fs = RecommendationEngine.DiagnoseRareFoodCandidateSearch(data, demand, context, generated); var bs = RecommendationEngine.DiagnoseRareBeverageCandidateSearch(data, demand, context);
        var baseMatched = merged.Where(f => BaseMatch(f, rule)).ToArray();
        var counts = Object(("foodRecipeEligibility", Object(("catalog", fs["catalogRecipeCount"]), ("requiredTagReachable", fs["requiredTagReachableRecipeCount"]), ("requiredTagReachableUnlocked", fs["requiredTagReachableUnlockedRecipeCount"]), ("requiredTagReachableBaseIngredientsReady", fs["requiredTagReachableBaseIngredientsReadyRecipeCount"]), ("requiredTagReachableCookerReady", fs["requiredTagReachableCookerReadyRecipeCount"]))),
            ("foodCandidates", Object(("generated", fs["generatedCandidateCount"]), ("generatedRequiredTagMatched", fs["generatedRequiredTagMatchedCandidateCount"]), ("merged", merged.Length), ("baseOrderMatched", baseMatched.Length), ("negativeSafe", baseMatched.Count(f => NegativeSafe(f, rule, Str(demand["requiredFoodTag"])))), ("specialRuleMatched", foods.Length), ("executable", foods.Count(f => NormalTargetSelector.NoHardFailures(f))))),
            ("beverageCandidates", Object(("catalog", bs["catalogBeverageCount"]), ("available", bs["availableBeverageCount"]), ("allowed", bs["allowedBeverageCount"]), ("requiredTagMatched", bs["requiredTagBeverageCount"]), ("specialRuleMatched", beverages.Length))),
            ("plans", Object(("rawExecutable", raw.Count(p => Str(p["bucket"]) != "blocked")), ("specialRuleSafe", safe.Count(p => Str(p["bucket"]) != "blocked")), ("executable", execution.Count))));
        var prices = from f in foods where NormalTargetSelector.NoHardFailures(f) from b in beverages where NormalTargetSelector.NoHardFailures(b) select Math.Max(0, Field(f, "recipe", "price")) + Math.Max(0, Field(b, "beverage", "price"));
        var minimum = prices.Any() ? (double?)prices.Min() : null; var remaining = KoishiEvaluation.NonNegativeInt(Obj(context["budget"])["remainingBudget"]);
        var unavailable = Strings(fs["missingCookerNames"]).Where(Strings(sets["runtimeUnavailableCookerNames"]).Contains).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var usable = Strings(sets["usableCookerNames"]).OrderBy(x => x, StringComparer.Ordinal).ToArray(); var placed = Strings(sets["placedCookerNames"]).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var diagnostic = BlockedReason(demand, context, counts, rule, Strings(fs["missingIngredientNames"]), Strings(fs["missingCookerNames"]), placed, usable, unavailable, remaining, minimum);
        diagnostic["counts"] = counts; diagnostic["missingIngredientNames"] = Clone(fs["missingIngredientNames"]); diagnostic["requiredCookerNames"] = Clone(fs["missingCookerNames"]);
        diagnostic["placedCookerNames"] = Array(placed); diagnostic["usableCookerNames"] = Array(usable); diagnostic["runtimeUnavailableCookerNames"] = Array(unavailable); diagnostic["remainingBudget"] = JsonValue.Create(remaining); diagnostic["minimumPairPrice"] = JsonValue.Create(minimum);
        string CountSignature(string key) => string.Join(",", Obj(counts[key]).Select(x => $"{x.Key}:{Key(Num(x.Value))}"));
        diagnostic["stateSignature"] = string.Join("|", Str(diagnostic["code"]), Str(diagnostic["firstEmptyStage"]), $"foodRecipes:{CountSignature("foodRecipeEligibility")}", $"foodCandidates:{CountSignature("foodCandidates")}", $"beverageCandidates:{CountSignature("beverageCandidates")}", $"plans:{CountSignature("plans")}",
            $"ingredients:{string.Join(",", Strings(diagnostic["missingIngredientNames"]))}", $"requiredCookers:{string.Join(",", Strings(diagnostic["requiredCookerNames"]))}", $"placedCookers:{string.Join(",", placed)}", $"usableCookers:{string.Join(",", usable)}", $"runtimeUnavailableCookers:{string.Join(",", unavailable)}", $"budget:{(remaining == null ? "" : Key(remaining.Value))}", $"minimum:{(minimum == null ? "" : Key(minimum.Value))}");
        return diagnostic;
    }
    private static JsonObject BlockedReason(JsonObject demand, JsonObject context, JsonObject counts, JsonObject rule, string[] ingredients, string[] cookers, string[] placed, string[] usable, string[] unavailable, double? remaining, double? minimum)
    {
        var recipes = Obj(counts["foodRecipeEligibility"]); var foods = Obj(counts["foodCandidates"]); var beverages = Obj(counts["beverageCandidates"]); var plans = Obj(counts["plans"]); var tag = Str(demand["requiredFoodTag"]); var reason = SpecialBusinessRules.TextOr(Str(rule["reason"]), "当前经营规则");
        JsonObject Make(string code, string stage, string message) => Object(("code", code), ("firstEmptyStage", stage), ("message", message));
        JsonObject CookerReason() => unavailable.Length > 0
            ? Make("food-cooker-runtime-unavailable", "food-cooker", $"满足{(tag.Trim().Length > 0 ? $"料理点单 Tag「{tag}」" : "当前订单")}所需的已摆放厨具当前被游戏机制锁定{Names(unavailable)}；当前可开厨具{Names(usable, "无")}。")
            : Make("food-cooker-missing", "food-cooker", $"满足料理点单 Tag「{tag}」的配方缺少可用厨具{Names(cookers)}；当前摆放{Names(placed, "无")}。");
        if (Count(rule, "requiredExtraIngredientIds") > 0 && Num(foods["generated"]) == 0) return Make("food-required-extra-unavailable", "food-required-extra", $"特殊经营强制加料 {string.Join("、", Numbers(rule["requiredExtraIngredientIds"]).Select(IngredientLabel))} 无法用于当前订单；请检查材料目录、库存、排除设置、配方禁忌和剩余加料槽。");
        if (Count(rule, "forbiddenExtraIngredientIds") > 0 && Num(foods["generated"]) == 0) return Make("food-special-rule-mismatch", "food-special-rule", $"当前订单不能把 {string.Join("、", Numbers(rule["forbiddenExtraIngredientIds"]).Select(IngredientLabel))} 作为额外加料，移除后没有可满足原订单的安全料理方案。");
        if (Num(foods["baseOrderMatched"]) == 0)
        {
            if (Num(recipes["requiredTagReachable"]) == 0) return Make("food-tag-not-supported", "food-tag-reachability", $"当前配方目录在现有加料上限与 Tag 规则下无法构成料理点单 Tag「{tag}」。");
            if (Num(recipes["requiredTagReachableUnlocked"]) == 0) return Make("food-recipe-locked", "food-recipe-unlocked", $"能满足料理点单 Tag「{tag}」的配方尚未解锁。");
            if (Num(recipes["requiredTagReachableBaseIngredientsReady"]) == 0) return Make("food-base-ingredient-missing", "food-base-ingredients", $"满足料理点单 Tag「{tag}」的已解锁配方缺少基础材料{Names(ingredients)}。");
            if (Num(recipes["requiredTagReachableCookerReady"]) == 0) return CookerReason();
            if (Num(foods["generatedRequiredTagMatched"]) == 0) return Make("food-required-tag-not-generated", "food-candidate-generation", $"满足料理点单 Tag「{tag}」的配方已具备运行资格，但当前可用加料未生成对应料理候选。");
        }
        if (Num(foods["executable"]) == 0 && Num(recipes["requiredTagReachableBaseIngredientsReady"]) > 0 && Num(recipes["requiredTagReachableCookerReady"]) == 0 && cookers.Length > 0) return CookerReason();
        if (Num(foods["negativeSafe"]) == 0 && Num(foods["baseOrderMatched"]) > 0) return Make("food-negative-tag", "food-negative-safe", "满足原订单的料理候选均包含当前稀客厌恶 Tag，已停止自动执行。");
        if (Num(foods["specialRuleMatched"]) == 0) return Make("food-special-rule-mismatch", "food-special-rule", $"{reason}下没有可安全执行的料理候选。");
        if (Num(beverages["specialRuleMatched"]) == 0)
        {
            if (Num(beverages["available"]) == 0) return Make("beverage-unavailable", "beverage-available", "当前库存中没有可用酒水。");
            if (Num(beverages["allowed"]) == 0) return Make("beverage-excluded", "beverage-allowed", "当前库存中的酒水均被推荐排除设置过滤。");
            if (Num(beverages["requiredTagMatched"]) == 0) return Make("beverage-tag-mismatch", "beverage-required-tag", $"当前可用酒水无法满足酒水点单 Tag「{Str(demand["requiredBeverageTag"])}」。");
            return Make("beverage-tag-mismatch", "beverage-required-tag", $"{reason}下没有可安全执行的酒水候选。");
        }
        if (Str(context["budgetPolicy"]) == "block" && (!Bool(Obj(context["budget"])["willPayMoney"], true) || remaining != null && minimum != null && minimum > remaining))
            return Make("budget-unavailable", "budget", !Bool(Obj(context["budget"])["willPayMoney"], true) ? "稀客当前不会付款，预算阻止了自动执行。" : $"最低可执行组合价格 {Key(minimum!.Value)}，超过剩余预算 {Key(remaining!.Value)}。");
        if (Num(plans["specialRuleSafe"]) == 0 && (Bool(rule["preferYuyukoPositiveSpell"]) || Bool(rule["requiresHighEvaluation"]) || Str(rule["yuyukoProgressEvaluationMode"]) != "none"))
            return Make("special-evaluation-unmet", "special-evaluation", Bool(rule["preferYuyukoPositiveSpell"]) ? "当前资源下没有可预测触发正面符卡的完美（ExGood）组合。" : $"{SpecialBusinessRules.TextOr(Str(rule["reason"]), "特殊经营")}下没有满足评价要求的安全组合。");
        return Make("execution-plan-missing", "execution-plan", "候选已生成，但当前没有可直接执行的完整料理/酒水组合。");
    }
    private static string Names(string[] values, string empty = "未识别")
    { var all = Unique(values).ToArray(); return all.Length == 0 ? $"：{empty}" : $"：{string.Join("、", all.Take(4))}{(all.Length > 4 ? $"等 {all.Length} 项" : "")}"; }
}
