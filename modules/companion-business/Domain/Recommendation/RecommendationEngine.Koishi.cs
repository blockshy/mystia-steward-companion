using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Recommendation;

public static partial class RecommendationEngine
{
    /// <summary>按料理/酒水点单、偏好及厌恶计算破盾后的评价估值。</summary>
    public static double EstimateKoishiBrokenShieldEvaluationScore(JsonObject input) => Math.Max(0,
        (Bool(input["meetsRequiredFood"]) ? 1 : 0) + (Bool(input["meetsRequiredBeverage"]) ? 1 : 0)
        + Num(input["preferenceMatches"]) - Num(input["negativeMatches"]) * 2);
    /// <summary>计算料理和酒水等级合计，厌恶标签每项抵消三级。</summary>
    public static double EstimateKoishiBrokenShieldDamageLevel(JsonObject input) => Math.Max(0,
        NonNegativeInt(input["foodLevel"]) + NonNegativeInt(input["beverageLevel"]) - Num(input["negativeMatches"]) * 3);
    /// <summary>保留原规则的评价、等级、价格三层估值；等级有效时不使用价格替代。</summary>
    public static double EstimateKoishiBrokenShieldFeedScore(JsonObject input)
    {
        var evaluation = EstimateKoishiBrokenShieldEvaluationScore(input);
        var evaluationScore = evaluation >= 6 ? 7 : evaluation >= 5 ? 5 : evaluation >= 4 ? 4 : Math.Clamp(evaluation, 0, 3);
        if (evaluationScore <= 0) return 0;
        var levelScore = Math.Clamp(Math.Truncate(EstimateKoishiBrokenShieldDamageLevel(input)), 0, 7);
        var foodPrice = NonNegativeInt(input["foodPrice"]);
        var drinkPrice = NonNegativeInt(input["beveragePrice"]);
        var foodScore = foodPrice >= 80 ? 4 : foodPrice >= 30 ? 3 : foodPrice >= 10 ? 2 : foodPrice > 0 ? 1 : 0;
        var drinkScore = drinkPrice >= 120 ? 3 : drinkPrice >= 40 ? 2 : drinkPrice > 0 ? 1 : 0;
        var price = NonNegativeInt(input["estimatedPrice"]);
        var priceScore = foodScore > 0 || drinkScore > 0 ? Math.Clamp(foodScore + drinkScore, 0, 7)
            : price >= 180 ? 7 : price >= 120 ? 5 : price >= 70 ? 3 : price > 0 ? 2 : 0;
        var damage = levelScore > 0 ? levelScore : priceScore;
        return damage <= 0 ? evaluationScore : Math.Min(evaluationScore, damage);
    }
    /// <summary>剩余次数包括当前订单；未知次数必须保持未知，不猜测为零。</summary>
    public static JsonObject BuildKoishiFeedPlanningInfo(JsonObject input)
    {
        var score = PositiveInt(input["remainingScore"]);
        var count = NullableNonNegativeInt(input["remainingOrderCount"]);
        double? attempts = score is null || count is null ? null : count + 1;
        return Object(("attemptsRemaining", attempts), ("requiredScoreThisOrder", attempts is null ? null : Math.Max(1, Math.Ceiling(score!.Value / Math.Max(1, attempts.Value)))));
    }
    /// <summary>验证单次投食及后续预算的可持续性；不确定预算/目标分数时拒绝认定可持续。</summary>
    public static bool IsKoishiFeedPlanSustainable(JsonObject input)
    {
        var budget = NullableNonNegativeInt(input["remainingBudget"]);
        var score = PositiveInt(input["remainingScore"]);
        var feed = Num(input["estimatedFeedScore"]);
        var price = Num(input["estimatedPrice"]);
        if (budget is null || score is null || feed <= 0 || price > budget) return false;
        if (feed >= score) return true;
        if (price * score > budget * feed) return false;
        var count = NullableNonNegativeInt(input["remainingOrderCount"]);
        return !(count is > 0 && budget - price < Num(input["minFollowUpBudget"], 120));
    }
    private static double NonNegativeInt(JsonNode? value) => Math.Max(0, Math.Truncate(Num(value)));
    private static double? NullableNonNegativeInt(JsonNode? value) => NullableNumber(value) is double d ? Math.Max(0, Math.Truncate(d)) : null;
    private static double? PositiveInt(JsonNode? value) => NullableNumber(value) is double d && Math.Truncate(d) > 0 ? Math.Truncate(d) : null;
}
