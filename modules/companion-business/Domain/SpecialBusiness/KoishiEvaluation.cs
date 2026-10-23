using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.SpecialBusiness;

/// <summary>古明地恋破防期的有限预算与剩余提交次数规划；不改变原订单匹配条件。</summary>
public static class KoishiEvaluation
{
    public static double? NonNegativeInt(JsonNode? value) => NullableNumber(value) is double n ? Math.Max(0, Math.Truncate(n)) : null;
    public static double? RemainingScore(JsonNode? special)
    {
        var s = Obj(special); if (!Bool(s["active"])) return null;
        var max = NonNegativeInt(s["maxValue"]); var achieved = NonNegativeInt(s["targetValue"]) ?? NonNegativeInt(s["currentValue"]) ?? 0;
        if (max > 0) return Math.Max(0, max.Value - achieved);
        var target = NonNegativeInt(s["targetValue"]); return target > 0 ? Math.Max(0, target.Value - (NonNegativeInt(s["currentValue"]) ?? 0)) : null;
    }
    public static JsonObject PairFacts(JsonNode? food, JsonNode? beverage, double price)
    {
        var f = Obj(food); var b = Obj(beverage); var recipe = Obj(f["recipe"]); var drink = Obj(b["beverage"]);
        return Object(("meetsRequiredFood", Bool(f["meetsRequiredFood"])), ("meetsRequiredBeverage", Bool(b["meetsRequiredBeverage"])),
            ("preferenceMatches", Arr(f["matchedPositiveTags"]).Count + Arr(b["matchedTags"]).Count), ("negativeMatches", Arr(f["matchedNegativeTags"]).Count),
            ("foodLevel", recipe["level"]), ("beverageLevel", drink["level"]), ("foodPrice", recipe["price"]), ("beveragePrice", drink["price"]), ("estimatedPrice", price));
    }
    public static double Price(JsonObject food, JsonObject beverage) => Math.Max(0, Num(Obj(food["recipe"])["price"])) + Math.Max(0, Num(Obj(beverage["beverage"])["price"]));
    public static double PairScore(JsonObject food, JsonObject beverage, JsonObject context) => ScoreCore(food, beverage, Price(food, beverage), context);
    public static double PlanScore(JsonObject plan, JsonObject context) => plan["food"] == null || plan["beverage"] == null || Str(plan["bucket"]) == "blocked" ? double.NegativeInfinity : ScoreCore(Obj(plan["food"]), Obj(plan["beverage"]), Math.Max(0, Num(plan["estimatedPrice"])), context);
    public static int ComparePlans(JsonObject a, JsonObject b, JsonObject context)
    { var difference = PlanScore(b, context).CompareTo(PlanScore(a, context)); return difference != 0 ? difference : Num(a["estimatedPrice"]).CompareTo(Num(b["estimatedPrice"])); }
    private static double ScoreCore(JsonObject food, JsonObject beverage, double price, JsonObject context)
    {
        var facts = PairFacts(food, beverage, price); var feed = RecommendationEngine.EstimateKoishiBrokenShieldFeedScore(facts);
        var budget = NonNegativeInt(context["remainingBudget"]); var score = NonNegativeInt(context["remainingScore"]); var count = NonNegativeInt(context["remainingOrderCount"]);
        var planningArgs = Object(("remainingScore", score), ("remainingBudget", budget), ("remainingOrderCount", count));
        var planning = RecommendationEngine.BuildKoishiFeedPlanningInfo(planningArgs); var minimum = NullableNumber(planning["requiredScoreThisOrder"]);
        var budgetFit = budget == null ? 0 : price <= budget ? 1e9 : -Math.Max(0, price - budget.Value) * 10000;
        var attempts = minimum == null ? 0 : feed >= minimum ? 2e9 : -Math.Max(0, minimum.Value - feed) * 5e8;
        double budgetScore = 0;
        if (budget != null && score > 0 && feed > 0)
        {
            if (price > budget) budgetScore = -Math.Max(0, price - budget.Value) * 1e6;
            else
            {
                var sustainability = Obj(Clone(planningArgs)); sustainability["estimatedPrice"] = price; sustainability["estimatedFeedScore"] = feed;
                budgetScore = (RecommendationEngine.IsKoishiFeedPlanSustainable(sustainability) ? 3e9 : 0) + (feed >= score ? 1e9 : 0) + Round(feed * 1e6 / Math.Max(1, price));
            }
        }
        return budgetFit + attempts + budgetScore + feed * 1e7 + Num(facts["preferenceMatches"]) * 100 - Num(facts["negativeMatches"]) * 100000 - Math.Ceiling(Num(food["resourcePressure"]) * 10) - price - Arr(food["extraIngredients"]).Count;
    }
    public static string PlanReason(JsonObject plan)
    {
        var f = Obj(plan["food"]); var b = Obj(plan["beverage"]); var facts = PairFacts(plan["food"], plan["beverage"], Num(plan["estimatedPrice"])); var budget = Obj(plan["budget"]);
        var text = plan["budget"] == null ? "预算未读取" : budget["remainingBudget"] == null ? $"预算未知，预计花费 {Key(Num(budget["estimatedPrice"]))}" : Num(budget["overBudget"]) > 0 ? $"预计花费 {Key(Num(budget["estimatedPrice"]))}，超预算 {Key(Num(budget["overBudget"]))} / 剩余 {Key(Num(budget["remainingBudget"]))}" : $"预计花费 {Key(Num(budget["estimatedPrice"]))} / 剩余预算 {Key(Num(budget["remainingBudget"]))}";
        return $"古明地恋破防：先满足原订单料理/酒水要求，再按预算内总分规划；料理 Lv.{Key(Num(Obj(f["recipe"])["level"]))}，酒水 Lv.{Key(Num(Obj(b["beverage"])["level"]))}，等级参考 {Key(RecommendationEngine.EstimateKoishiBrokenShieldDamageLevel(facts))}，投食分估算 {Key(RecommendationEngine.EstimateKoishiBrokenShieldFeedScore(facts))}，喜好命中 {Key(Num(facts["preferenceMatches"]))}，厌恶 {Key(Num(facts["negativeMatches"]))}，评价参考 {Key(RecommendationEngine.EstimateKoishiBrokenShieldEvaluationScore(facts))}，{text}";
    }
    public static string BudgetReason(JsonObject food, JsonObject beverage, JsonObject context)
    {
        var price = Price(food, beverage); var facts = PairFacts(food, beverage, price); var feed = RecommendationEngine.EstimateKoishiBrokenShieldFeedScore(facts);
        var budget = NonNegativeInt(context["remainingBudget"]); var score = NonNegativeInt(context["remainingScore"]);
        var planning = RecommendationEngine.BuildKoishiFeedPlanningInfo(context);
        var scoreText = $"等级参考 {Key(RecommendationEngine.EstimateKoishiBrokenShieldDamageLevel(facts))}，评价参考 {Key(RecommendationEngine.EstimateKoishiBrokenShieldEvaluationScore(facts))}，投食分估算 {Key(feed)}" + (score == null ? "" : $" / 还需 {Key(score.Value)}");
        var budgetText = budget == null ? $"预算未知，预计花费 {Key(price)}" : $"预计花费 {Key(price)} / 当前预算 {Key(budget.Value)}";
        var attempts = planning["attemptsRemaining"] == null || planning["requiredScoreThisOrder"] == null ? "" : $"，剩余提交 {Key(Num(planning["attemptsRemaining"]))} 次，本轮至少 {Key(Num(planning["requiredScoreThisOrder"]))} 分";
        var sustainability = Obj(Clone(context)); sustainability["estimatedPrice"] = price; sustainability["estimatedFeedScore"] = feed;
        var risk = score != null && budget != null && feed > 0 && !RecommendationEngine.IsKoishiFeedPlanSustainable(sustainability) ? "，该组合偏贵，仅在没有更省预算方案时使用" : "";
        return $"，{scoreText}，{budgetText}{attempts}{risk}";
    }
}
