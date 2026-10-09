using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Recommendation;

public static partial class RecommendationEngine
{
    private static readonly string[] ObjectiveKeys = { "foodPreference", "beveragePreference", "negativeRisk", "extraCount", "resourcePressure", "totalCost", "profit", "beverageStock", "cookerAvailable" };
    private static readonly string[] ObjectiveDirections = { "desc", "desc", "asc", "asc", "asc", "asc", "desc", "desc", "desc" };
    private static readonly Dictionary<string, int[]> PresetWeights = new()
    {
        ["balanced"] = new[] { 70, 60, 90, 45, 55, 30, 35, 35, 60 },
        ["resources"] = new[] { 55, 45, 90, 65, 100, 70, 20, 80, 60 },
        ["profit"] = new[] { 60, 50, 85, 25, 35, 25, 100, 25, 50 },
        ["simple"] = new[] { 50, 45, 90, 100, 45, 45, 25, 40, 70 }
    };

    /// <summary>归一化 main 基线的九项目标；未知字段丢弃，相同 key 的最后一项覆盖前项。</summary>
    public static JsonObject NormalizeSortProfile(JsonObject? value)
    {
        value ??= new JsonObject(); var preset = Str(value["preset"], "balanced");
        if (!PresetWeights.ContainsKey(preset)) preset = "balanced";
        var overrides = Objects(value["objectives"]).Where(x => x["key"] is JsonValue).GroupBy(x => Str(x["key"])).ToDictionary(x => x.Key, x => x.Last());
        var rules = new List<JsonObject>();
        for (var i = 0; i < ObjectiveKeys.Length; i++)
        {
            overrides.TryGetValue(ObjectiveKeys[i], out var item); item ??= new JsonObject();
            var direction = Str(item["direction"]); if (direction is not ("asc" or "desc")) direction = ObjectiveDirections[i];
            rules.Add(Object(("key", ObjectiveKeys[i]), ("enabled", Bool(item["enabled"], true)),
                ("weight", Math.Clamp(Math.Truncate(Num(item["weight"], PresetWeights[preset][i])), 0, 100)), ("direction", direction)));
        }
        return Object(("preset", preset), ("objectives", Array(rules)));
    }
    /// <summary>使用与 TS 导出名一致的完整名称，方便调用方直接迁移。</summary>
    public static JsonObject NormalizeRecommendationSortProfile(JsonObject? value) => NormalizeSortProfile(value);
    /// <summary>构建指定预设的独立配置对象。</summary>
    public static JsonObject BuildDefaultRecommendationSortProfile(string preset = "balanced") => NormalizeSortProfile(Object(("preset", preset)));
    /// <summary>序列化之前归一化，避免旧配置字段影响最终权重。</summary>
    public static string SerializeRecommendationSortProfile(JsonObject value) => NormalizeSortProfile(value).ToJsonString();

    /// <summary>先执行置顶与分桶，再执行特殊经营和归一化权重；稳定排序保持原候选顺序。</summary>
    public static JsonArray SortRareOrderPlans(JsonArray plans, JsonObject? profile = null, JsonObject? sortContext = null)
    {
        var normalized = NormalizeSortProfile(profile); var context = sortContext ?? new JsonObject(); var values = Objects(plans).ToArray();
        var ranges = ObjectiveKeys.ToDictionary(key => key, key => values.Length == 0 ? (Min: 0d, Max: 0d) : (Min: values.Min(x => ObjectiveValue(x, key)), Max: values.Max(x => ObjectiveValue(x, key))));
        return Sorted(values, (left, right) =>
        {
            var result = PinRank(right, context).CompareTo(PinRank(left, context)); if (result != 0) return result;
            var lf = Obj(left["food"]); var rf = Obj(right["food"]);
            if (Bool(lf["customRecipePinned"]) && Bool(rf["customRecipePinned"]))
            { result = Num(lf["customRecipeSortOrder"], 9007199254740991d).CompareTo(Num(rf["customRecipeSortOrder"], 9007199254740991d)); if (result != 0) return result; }
            result = FirstComparison(BucketRank(Str(right["bucket"])).CompareTo(BucketRank(Str(left["bucket"]))), Arr(left["warnings"]).Count.CompareTo(Arr(right["warnings"]).Count), CompareSpecialPlans(left, right, context));
            if (result != 0) return result;
            result = PlanScore(right, normalized, ranges).CompareTo(PlanScore(left, normalized, ranges)); if (result != 0) return result;
            if (left["food"] is JsonObject leftFood && right["food"] is JsonObject rightFood) { result = CompareFoodCandidates(leftFood, rightFood); if (result != 0) return result; }
            return left["beverage"] is JsonObject leftDrink && right["beverage"] is JsonObject rightDrink ? CompareBeverageCandidates(leftDrink, rightDrink) : 0;
        });
    }
    private static double ObjectiveValue(JsonObject plan, string key)
    {
        var food = Obj(plan["food"]); var drink = Obj(plan["beverage"]);
        return key switch
        {
            "foodPreference" => Arr(food["matchedPositiveTags"]).Count,
            "beveragePreference" => Arr(drink["matchedTags"]).Count,
            "negativeRisk" => Arr(food["matchedNegativeTags"]).Count,
            "extraCount" => Arr(food["extraIngredients"]).Count,
            "resourcePressure" => Num(food["resourcePressure"]),
            "totalCost" => Num(food["baseCost"]) + Num(food["extraCost"]),
            "profit" => Num(Obj(food["recipe"])["price"]) - Num(food["baseCost"]) - Num(food["extraCost"]) + Num(Obj(drink["beverage"])["price"]),
            "beverageStock" => plan["beverage"] is null ? 0 : InventoryRank(Num(drink["ownedQuantity"])),
            "cookerAvailable" => Bool(food["cookerAvailable"]) ? 1 : 0,
            _ => 0
        };
    }
    private static double PlanScore(JsonObject plan, JsonObject profile, Dictionary<string, (double Min, double Max)> ranges)
    {
        double score = 0;
        foreach (var rule in Objects(profile["objectives"]))
        {
            if (!Bool(rule["enabled"]) || Num(rule["weight"]) <= 0) continue;
            var key = Str(rule["key"]); var range = ranges[key]; if (range.Min == range.Max) continue;
            var normalized = (ObjectiveValue(plan, key) - range.Min) / (range.Max - range.Min);
            score += (Str(rule["direction"]) == "desc" ? normalized : 1 - normalized) * Num(rule["weight"]);
        }
        return score;
    }
    private static int PinRank(JsonObject plan, JsonObject context)
    {
        if (Str(plan["bucket"]) == "blocked") return 0;
        var food = Obj(plan["food"]); var drink = Obj(plan["beverage"]); var rank = 0;
        if (Bool(context["specialPreferDamageLevel"]) && Arr(food["matchedNegativeTags"]).Count == 0) rank = 10000;
        if (IsMissionRecipeExecutionPlan(plan, context)) rank = Math.Max(rank, 50);
        if (Bool(food["customRecipePinned"])) rank = Math.Max(rank, 40);
        var recipeKey = $"{Key(Num(Obj(food["recipe"])["id"]))}:{string.Join(",", Objects(food["extraIngredients"]).Select(x => Num(x["id"])).OrderBy(x => x).Select(Key))}";
        if (Bool(context["pinFavoriteRecipe"]) && plan["food"] is not null && Strings(context["favoriteRecipeKeys"]).Contains(recipeKey)) rank = Math.Max(rank, 20);
        if (Bool(context["pinFavoriteBeverage"]) && plan["beverage"] is not null && Numbers(context["favoriteBeverageIds"]).Contains(Num(Obj(drink["beverage"])["id"]))) rank = Math.Max(rank, 20);
        return rank;
    }
    private static int SpecialRank(JsonObject plan, JsonObject context) =>
        Strings(Obj(plan["food"])["activeTags"]).Count(Strings(context["specialTargetFoodTags"]).Contains)
        + Strings(Obj(plan["beverage"])["activeTags"]).Count(Strings(context["specialTargetBeverageTags"]).Contains);
    private static int CompareSpecialPlans(JsonObject left, JsonObject right, JsonObject context)
    {
        var result = SpecialRank(right, context).CompareTo(SpecialRank(left, context)); if (result != 0) return result;
        if (Bool(context["specialPreferDamageLevel"]))
        {
            result = FirstComparison(Negatives(left).CompareTo(Negatives(right)), BudgetFit(right).CompareTo(BudgetFit(left)), CompareKoishiBudget(left, right, context),
                DamageLevel(right).CompareTo(DamageLevel(left)), DamageProxy(right).CompareTo(DamageProxy(left)), PreferenceScore(right).CompareTo(PreferenceScore(left)),
                BudgetEfficiency(right).CompareTo(BudgetEfficiency(left)), Num(left["estimatedPrice"]).CompareTo(Num(right["estimatedPrice"])));
            if (result != 0) return result;
        }
        if (Bool(context["specialPreferHighFoodLevel"]) || Bool(context["specialPreferHighBeverageLevel"]))
        { result = FirstComparison(Negatives(left).CompareTo(Negatives(right)), PreferenceScore(right).CompareTo(PreferenceScore(left))); if (result != 0) return result; }
        if (Bool(context["specialPreferHighFoodLevel"])) { result = FoodLevel(right).CompareTo(FoodLevel(left)); if (result != 0) return result; }
        return Bool(context["specialPreferHighBeverageLevel"]) ? DrinkLevel(right).CompareTo(DrinkLevel(left)) : 0;
    }
    private static int BucketRank(string bucket) => bucket switch { "complete" => 4, "tradeoff" => 3, "preference" => 2, "blocked" => 1, _ => 0 };
    private static int Negatives(JsonObject plan) => Arr(Obj(plan["food"])["matchedNegativeTags"]).Count;
    private static int PreferenceScore(JsonObject plan) => Arr(Obj(plan["food"])["matchedPositiveTags"]).Count + Arr(Obj(plan["beverage"])["matchedTags"]).Count;
    private static double FoodLevel(JsonObject plan) => Num(Obj(Obj(plan["food"])["recipe"])["level"]);
    private static double DrinkLevel(JsonObject plan) => Num(Obj(Obj(plan["beverage"])["beverage"])["level"]);
    private static double DamageLevel(JsonObject plan) => FoodLevel(plan) + DrinkLevel(plan);
    private static int BudgetFit(JsonObject plan) => Obj(plan["budget"])["remainingBudget"] is null ? 0 : Num(Obj(plan["budget"])["overBudget"]) <= 0 ? 1 : -1;
    private static double DamageProxy(JsonObject plan) => KoishiPlanScore(plan) * 10000 + PreferenceScore(plan) * 10 - Math.Max(0, Num(plan["estimatedPrice"])) - Negatives(plan) * 100000;
    private static double BudgetEfficiency(JsonObject plan)
    {
        var score = KoishiPlanScore(plan); var budget = Obj(plan["budget"]);
        if (score <= 0 || budget["remainingBudget"] is null) return 0;
        return Num(budget["overBudget"]) > 0 ? -Num(budget["overBudget"]) * 1000 : Round(score * 100000 / Math.Max(1, Num(plan["estimatedPrice"])));
    }
    private static double KoishiPlanScore(JsonObject plan)
    {
        var food = Obj(plan["food"]); var drink = Obj(plan["beverage"]); var recipe = Obj(food["recipe"]); var beverage = Obj(drink["beverage"]);
        return EstimateKoishiBrokenShieldFeedScore(Object(("meetsRequiredFood", Bool(food["meetsRequiredFood"])), ("meetsRequiredBeverage", Bool(drink["meetsRequiredBeverage"])),
            ("preferenceMatches", PreferenceScore(plan)), ("negativeMatches", Negatives(plan)), ("foodLevel", recipe["level"]), ("beverageLevel", beverage["level"]),
            ("foodPrice", recipe["price"]), ("beveragePrice", beverage["price"]), ("estimatedPrice", plan["estimatedPrice"])));
    }
    private static JsonObject? KoishiBudgetInfo(JsonObject plan, double score, JsonNode? remainingOrders)
    {
        var budget = NullableNumber(Obj(plan["budget"])["remainingBudget"]); if (budget is null or <= 0 || Str(plan["bucket"]) == "blocked") return null;
        var feed = KoishiPlanScore(plan); if (feed <= 0) return null;
        var planning = BuildKoishiFeedPlanningInfo(Object(("remainingScore", score), ("remainingBudget", budget), ("remainingOrderCount", remainingOrders)));
        var floor = planning["requiredScoreThisOrder"] is null || feed >= Num(planning["requiredScoreThisOrder"]);
        var price = Math.Max(0, Num(plan["estimatedPrice"]));
        if (price > budget) return Object(("feedScore", feed), ("efficiency", -Math.Max(0, price - budget.Value)), ("sustainable", false), ("completesTarget", false), ("meetsAttemptFloor", floor));
        var sustainable = IsKoishiFeedPlanSustainable(Object(("estimatedPrice", price), ("estimatedFeedScore", feed), ("remainingBudget", budget), ("remainingScore", score), ("remainingOrderCount", remainingOrders)));
        return Object(("feedScore", feed), ("efficiency", Round(feed * 1000000 / Math.Max(1, price))), ("sustainable", sustainable), ("completesTarget", feed >= score), ("meetsAttemptFloor", floor));
    }
    private static int CompareKoishiBudget(JsonObject left, JsonObject right, JsonObject context)
    {
        var score = PositiveInt(context["specialKoishiRemainingScore"]); if (score is null) return 0;
        var l = KoishiBudgetInfo(left, score.Value, context["specialKoishiRemainingOrderCount"]); var r = KoishiBudgetInfo(right, score.Value, context["specialKoishiRemainingOrderCount"]);
        if (l is null || r is null) return 0;
        var result = FirstComparison(Bool(r["sustainable"]).CompareTo(Bool(l["sustainable"])), Bool(r["completesTarget"]).CompareTo(Bool(l["completesTarget"])), Bool(r["meetsAttemptFloor"]).CompareTo(Bool(l["meetsAttemptFloor"])));
        if (result != 0) return result;
        if (Bool(l["sustainable"]) && Bool(r["sustainable"])) { result = Num(r["feedScore"]).CompareTo(Num(l["feedScore"])); if (result != 0) return result; }
        var efficiency = Num(r["efficiency"]) - Num(l["efficiency"]); if (Math.Abs(efficiency) >= 1) return Math.Sign(efficiency);
        return FirstComparison(Num(r["feedScore"]).CompareTo(Num(l["feedScore"])), Num(left["estimatedPrice"]).CompareTo(Num(right["estimatedPrice"])));
    }
}
