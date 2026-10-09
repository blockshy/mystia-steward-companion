using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Orders;

public static partial class OrderRecommendationService
{
    private static bool BaseMatch(JsonObject f, JsonObject rule) => !Bool(rule["requiresBaseOrderMatch"]) || Bool(f["meetsRequiredFood"]);
    private static bool NegativeSafe(JsonObject f, JsonObject rule, string required)
    {
        var mode = Str(rule["yuyukoProgressEvaluationMode"]);
        if (Bool(rule["preferYuyukoPositiveSpell"]) || mode == "retake-tag-order") return YuyukoEvaluation.NegativeTags(f, required).Length == 0;
        if (mode == "story-level-sum") return true;
        return (!Bool(rule["requiresHighEvaluation"]) && !Bool(rule["preferKoishiDamage"])) || Count(f, "matchedNegativeTags") == 0;
    }
    private static bool ExtrasAllowed(JsonObject f, JsonObject rule)
    {
        var required = Arr(rule["requiredExtraIngredientIds"]); var forbidden = Arr(rule["forbiddenExtraIngredientIds"]); var ids = Objects(f["extraIngredients"]).Select(x => Num(x["id"])).ToArray();
        if (required.Concat(forbidden).Any(x => !Integer(x) || Num(x) < 0) || Numbers(required).Distinct().Count() != required.Count || Numbers(forbidden).Distinct().Count() != forbidden.Count) return false;
        return Numbers(required).All(x => ids.Count(y => y == x) == 1) && Numbers(forbidden).All(x => !ids.Contains(x));
    }
    private static bool FoodAllowed(JsonObject f, JsonObject rule, HashSet<string> rejected, string required)
    {
        if (!BaseMatch(f, rule) || !NegativeSafe(f, rule, required) || !ExtrasAllowed(f, rule) || Str(rule["blockingReason"]).Length > 0) return false;
        var target = Obj(rule["foodTarget"]); if (Str(target["enforcement"]) != "require") return true;
        if (!SpecialBusinessRules.MatchesFoodTarget(f["activeTags"], target)) return false;
        var key = SpecialBusinessRules.RejectedRecipeKey(target["tags"], Field(f, "recipe", "id"), Field(f, "recipe", "recipeId"), Objects(f["extraIngredients"]).Select(x => Num(x["id"]))); return key.Length == 0 || !rejected.Contains(key);
    }
    private static IEnumerable<JsonObject> FilterPlans(JsonObject[] plans, JsonObject rule)
    {
        if (Str(rule["blockingReason"]).Length > 0) return Enumerable.Empty<JsonObject>();
        if (Str(Obj(rule["foodTarget"])["enforcement"]) != "require" && Count(rule, "requiredExtraIngredientIds") == 0 && Count(rule, "forbiddenExtraIngredientIds") == 0 && !Bool(rule["requiresBaseOrderMatch"]) && !Bool(rule["requiresHighEvaluation"])) return plans;
        return plans.Where(p => SafePlan(p, rule));
    }
    public static bool SafePlan(JsonObject p, JsonObject rule)
    {
        if (p["food"] is not JsonObject f || p["beverage"] is not JsonObject b || Str(p["bucket"]) == "blocked" || Str(rule["blockingReason"]).Length > 0) return false;
        var target = Obj(rule["foodTarget"]);
        if (Str(target["enforcement"]) == "require" && !SpecialBusinessRules.MatchesFoodTarget(f["activeTags"], target) || !ExtrasAllowed(f, rule)) return false;
        if (Bool(rule["requiresBaseOrderMatch"]) && (!Bool(f["meetsRequiredFood"]) || !Bool(b["meetsRequiredBeverage"]))) return false;
        var mode = Str(rule["yuyukoProgressEvaluationMode"]);
        if (mode != "none") return YuyukoEvaluation.IsProgressPlan(p, mode);
        if (Bool(rule["preferYuyukoPositiveSpell"])) return YuyukoEvaluation.IsPositiveSpellPlan(p);
        if (!Bool(rule["requiresHighEvaluation"])) return true;
        var preferences = Count(f, "matchedPositiveTags") + Count(b, "matchedTags");
        return Count(f, "matchedNegativeTags") == 0 && preferences >= Num(rule["highEvaluationMinPreferenceMatches"]) && preferences + (Bool(f["meetsRequiredFood"]) ? 1 : 0) + (Bool(b["meetsRequiredBeverage"]) ? 1 : 0) >= 4;
    }
    private static string IngredientLabel(double id) => id == 5002 ? "噗噗呦果" : id == 5005 ? "辣椒水" : $"#{Key(id)}";
    private static JsonObject AddReasons(JsonObject plan, JsonObject rule, JsonObject sort)
    {
        var p = Obj(Clone(plan)); var reason = ""; var target = Obj(rule["foodTarget"]); var mode = Str(rule["yuyukoProgressEvaluationMode"]);
        if (p["food"] != null && p["beverage"] != null && Str(p["bucket"]) != "blocked")
        {
            if (Count(rule, "requiredExtraIngredientIds") > 0) reason = $"特殊经营强制加料：{string.Join("、", Numbers(rule["requiredExtraIngredientIds"]).Select(IngredientLabel))}";
            else if (Count(rule, "forbiddenExtraIngredientIds") > 0) reason = $"特殊经营禁止额外加料：{string.Join("、", Numbers(rule["forbiddenExtraIngredientIds"]).Select(IngredientLabel))}";
            else if (Str(target["enforcement"]) != "none") reason = Str(target["match"]) == "all" ? $"特殊经营目标：同时满足 {string.Join("、", Strings(target["tags"]))}" : $"特殊经营目标：满足 {string.Join("、", Strings(target["tags"]))} 中至少一个";
            else if (mode != "none") reason = YuyukoEvaluation.ProgressReason(p, mode);
            else if (Bool(rule["preferYuyukoPositiveSpell"])) reason = YuyukoEvaluation.PositiveReason(p);
            else if (Bool(rule["preferKoishiDamage"])) reason = KoishiEvaluation.PlanReason(p);
            if (reason.Length > 0) p["reasons"] = Array(new[] { reason }.Concat(Strings(p["reasons"]).Where(x => x != reason)));
        }
        if (RecommendationEngine.IsMissionRecipeExecutionPlan(p, sort)) p["reasons"] = Array(new[] { "任务料理置顶" }.Concat(Strings(p["reasons"]).Where(x => x != "任务料理置顶")));
        return p;
    }
    private static IEnumerable<string> SpecialBlockedMessages(JsonObject[] raw, JsonObject[] safe, JsonObject rule)
    {
        if (Str(rule["blockingReason"]).Length > 0) return new[] { Str(rule["blockingReason"]) };
        var target = Obj(rule["foodTarget"]); var mode = Str(rule["yuyukoProgressEvaluationMode"]);
        if (safe.Any(p => Str(p["bucket"]) != "blocked") || raw.Length == 0 || Str(target["enforcement"]) != "require" && Count(rule, "requiredExtraIngredientIds") == 0 && Count(rule, "forbiddenExtraIngredientIds") == 0 && !Bool(rule["requiresBaseOrderMatch"]) && !Bool(rule["requiresHighEvaluation"]) && !Bool(rule["preferYuyukoPositiveSpell"]) && mode == "none") return System.Array.Empty<string>();
        if (mode != "none") return YuyukoEvaluation.BlockedMessages(raw, mode);
        if (Bool(rule["preferYuyukoPositiveSpell"])) return YuyukoEvaluation.BlockedMessages(raw, "none", true);
        var messages = new List<string>();
        if (Str(target["enforcement"]) == "require") messages.Add(Str(target["match"]) == "all" ? $"特殊经营要求料理同时满足目标 Tag：{string.Join("、", Strings(target["tags"]))}。" : $"特殊经营要求料理满足以下任一目标 Tag：{string.Join("、", Strings(target["tags"]))}。");
        if (Count(rule, "requiredExtraIngredientIds") > 0) messages.Add($"特殊经营要求料理额外加入材料：{string.Join("、", Numbers(rule["requiredExtraIngredientIds"]).Select(IngredientLabel))}。");
        if (Count(rule, "forbiddenExtraIngredientIds") > 0) messages.Add($"特殊经营禁止把以下材料作为额外加料：{string.Join("、", Numbers(rule["forbiddenExtraIngredientIds"]).Select(IngredientLabel))}。");
        if (Bool(rule["requiresBaseOrderMatch"])) messages.Add("特殊经营要求先满足原订单料理和酒水。");
        if (Bool(rule["requiresHighEvaluation"])) messages.Add($"特殊经营要求最高评价，当前组合至少需要 {Key(Num(rule["highEvaluationMinPreferenceMatches"]))} 个喜好命中且不能包含厌恶 Tag。");
        return messages;
    }
    private static bool BudgetBlocks(JsonNode? budget, string policy) => policy == "block" && budget != null && Bool(Obj(budget)["willPayMoney"], true) && NullableNumber(Obj(budget)["remainingBudget"]) != null;
    private static bool CanPair(JsonObject candidate, IEnumerable<JsonObject> other, bool food, JsonNode? budget, string policy)
    {
        if (policy == "block" && !Bool(Obj(budget)["willPayMoney"], true)) return false;
        if (!BudgetBlocks(budget, policy)) return true;
        var available = Math.Max(0, Math.Truncate(Num(Obj(budget)["remainingBudget"])));
        return other.Any(x => NormalTargetSelector.NoHardFailures(x) && Math.Max(0, Field(candidate, food ? "recipe" : "beverage", "price") + Field(x, food ? "beverage" : "recipe", "price")) <= available);
    }
    private static IEnumerable<string> BlockedPlanMessages(JsonObject[] plans, JsonNode? budget, string policy)
    {
        if (plans.Any(p => Str(p["bucket"]) != "blocked")) return System.Array.Empty<string>();
        if (plans.Length > 0) return plans.SelectMany(p => Objects(p["conditionResults"])).Where(r => Str(r["status"]) == "fail" && Str(r["severity"]) == "hard").Select(r => Str(r["detail"])).Distinct(StringComparer.Ordinal).Take(3);
        if (policy == "block" && !Bool(Obj(budget)["willPayMoney"], true)) return new[] { "稀客当前不会付款。" };
        return BudgetBlocks(budget, policy) ? new[] { "没有可搭配且不超预算的料理/酒水组合。" } : System.Array.Empty<string>();
    }
    private static bool Favorite(JsonObject candidate, JsonObject sort, bool food) => food ? Strings(sort["favoriteRecipeKeys"]).Contains(RecipeKey(candidate)) : Numbers(sort["favoriteBeverageIds"]).Contains(Field(candidate, "beverage", "id"));
    private static (JsonObject Food, JsonObject Beverage)? MissionPair(JsonObject[] foods, JsonObject[] beverages, JsonNode? budget, string policy, JsonObject sort, JsonObject primary)
    {
        foreach (var f in foods)
        {
            if (!RecommendationEngine.IsMissionRecipeFoodCandidate(f, sort) || !NormalTargetSelector.NoHardFailures(f) || Bool(primary["requireRecipeFavorite"]) && !Favorite(f, sort, true)) continue;
            foreach (var b in beverages)
                if (NormalTargetSelector.NoHardFailures(b) && Bool(b["meetsRequiredBeverage"]) && (!Bool(primary["requireBeverageFavorite"]) || Favorite(b, sort, false)) && CanPair(f, new[] { b }, true, budget, policy)) return (f, b);
        }
        return null;
    }
    private static JsonObject[] ExecutionCandidates(JsonObject[] candidates, JsonObject[] other, bool food, JsonNode? budget, string policy, JsonObject sort, JsonObject? mission)
    {
        var eligible = candidates.Where(x => NormalTargetSelector.NoHardFailures(x) && CanPair(x, other, food, budget, policy)).ToArray();
        var expanded = Bool(sort["specialPreferDamageLevel"]) || Bool(sort["specialPreferYuyukoPositiveSpell"]) || sort["specialYuyukoProgressEvaluationMode"] != null;
        var limit = expanded ? food ? 96 : 48 : food ? 24 : 16;
        var ranked = eligible.Select((x, i) => (Candidate: x, Index: i, Rank: Math.Max(PinRank(x, sort, food), Favorite(x, sort, food) ? 1 : 0))).Where(x => x.Rank > 0).OrderByDescending(x => x.Rank).ThenBy(x => x.Index).Select(x => x.Candidate);
        var selected = ranked.Concat(eligible).Distinct().Take(limit).ToArray();
        return mission != null && eligible.Contains(mission) && !selected.Contains(mission) ? new[] { mission }.Concat(selected).Take(limit).ToArray() : selected;
    }
    private static double PinRank(JsonObject c, JsonObject sort, bool food)
    {
        double rank = 0; var kind = food ? "recipe" : "beverage"; var matches = Count(c, food ? "matchedPositiveTags" : "matchedTags"); var stock = Num(c["ownedQuantity"]) == -1 ? 99 : Math.Min(Num(c["ownedQuantity"]), 99);
        if (Bool(sort["specialPreferDamageLevel"])) rank = Math.Max(rank, food && Count(c, "matchedNegativeTags") > 0 ? 0 : 10000 + matches * 1000 + Field(c, kind, "level") * 100 - Math.Min(Field(c, kind, "price"), 999) + (food ? -Math.Ceiling(Num(c["resourcePressure"]) * 10) - Count(c, "extraIngredients") : stock));
        if (Bool(sort["specialPreferYuyukoPositiveSpell"])) rank = Math.Max(rank, YuyukoRank(c, sort, food, "retake-tag-order"));
        if (sort["specialYuyukoProgressEvaluationMode"] != null) rank = Math.Max(rank, YuyukoRank(c, sort, food, Str(sort["specialYuyukoProgressEvaluationMode"])));
        if (food && Bool(c["customRecipePinned"])) rank = Math.Max(rank, 40);
        var special = SpecialRank(c, sort, food); if (special > 0) rank = Math.Max(rank, (food ? 30 : 20) + special);
        if (Bool(sort[food ? "pinFavoriteRecipe" : "pinFavoriteBeverage"]) && Favorite(c, sort, food)) rank = Math.Max(rank, food ? 20 : 10);
        return rank;
    }
    private static double YuyukoRank(JsonObject c, JsonObject sort, bool food, string mode)
    {
        if (mode == "retake-tag-order") return food ? YuyukoEvaluation.PositiveFoodRank(c, Str(sort["specialYuyukoRequiredFoodTag"])) : YuyukoEvaluation.PositiveBeverageRank(c, Str(sort["specialYuyukoRequiredBeverageTag"]));
        if (!Bool(c[food ? "meetsRequiredFood" : "meetsRequiredBeverage"])) return 0;
        var kind = food ? "recipe" : "beverage";
        return 10000 + Field(c, kind, "level") * 1000 + Math.Min(Field(c, kind, "price"), 999) + (food ? -Math.Ceiling(Num(c["resourcePressure"]) * 10) - Count(c, "extraIngredients") : Num(c["ownedQuantity"]) == -1 ? 99 : Math.Min(Num(c["ownedQuantity"]), 99));
    }
    private static int SpecialRank(JsonObject c, JsonObject sort, bool food) => Strings(c["activeTags"]).Count(Strings(sort[food ? "specialTargetFoodTags" : "specialTargetBeverageTags"]).Contains);
}
