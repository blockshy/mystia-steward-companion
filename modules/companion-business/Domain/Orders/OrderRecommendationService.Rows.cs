using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Orders;

public static partial class OrderRecommendationService
{
    /// <summary>供独立推荐页面复用订单料理行选择，所有预算和排序规则与订单服务一致。</summary>
    public static JsonArray DeriveRecipeRowsFromCandidates(JsonArray foods, JsonArray beverages, JsonObject options) => Array(DeriveRows(Objects(foods).ToArray(), Objects(beverages).ToArray(), true, options["budget"], Str(options["budgetPolicy"]), Obj(options["sortContext"]), options["sortProfile"], Num(options["limit"], double.PositiveInfinity), Num(options["variantLimitPerBase"], double.PositiveInfinity)));
    /// <summary>供独立推荐页面复用订单酒水行选择，不引入另一套客户端排序。</summary>
    public static JsonArray DeriveBeverageRowsFromCandidates(JsonArray beverages, JsonArray foods, JsonObject options) => Array(DeriveRows(Objects(beverages).ToArray(), Objects(foods).ToArray(), false, options["budget"], Str(options["budgetPolicy"]), Obj(options["sortContext"]), options["sortProfile"], Num(options["limit"], double.PositiveInfinity), double.PositiveInfinity));
    /// <summary>保留原展示排序的钉选、特殊经营、归一化权重和最终稳定比较顺序。</summary>
    private static JsonObject[] DeriveRows(JsonObject[] candidates, JsonObject[] others, bool food, JsonNode? budget, string policy, JsonObject context, JsonNode? profile, double limit, double variants)
    {
        limit = RowLimit(limit); variants = RowLimit(variants); if (limit <= 0 || variants <= 0) return System.Array.Empty<JsonObject>();
        var sort = Obj(Clone(context)); sort[food ? "pinFavoriteBeverage" : "pinFavoriteRecipe"] = false;
        var display = candidates.Where(c => NormalTargetSelector.NoHardFailures(c) && CanPair(c, others, food, budget, policy)
            && (Bool(c[food ? "meetsRequiredFood" : "meetsRequiredBeverage"]) || Count(c, food ? "matchedPositiveTags" : "matchedTags") > 0 || food && Bool(c["customRecipe"]) || PinRank(c, sort, food) > 0)).ToArray();
        var normalized = RecommendationEngine.NormalizeSortProfile(profile as JsonObject); var objectives = Objects(normalized["objectives"]).ToArray();
        var ranges = objectives.ToDictionary(x => Str(x["key"]), x => display.Length == 0 ? (Min: 0d, Max: 0d) : (Min: display.Min(c => Objective(c, Str(x["key"]), food)), Max: display.Max(c => Objective(c, Str(x["key"]), food))));
        double Score(JsonObject c)
        {
            double total = 0;
            foreach (var rule in objectives)
            {
                if (!Bool(rule["enabled"]) || Num(rule["weight"]) <= 0) continue;
                var key = Str(rule["key"]); var range = ranges[key]; if (range.Min == range.Max) continue;
                var value = (Objective(c, key, food) - range.Min) / (range.Max - range.Min);
                total += (Str(rule["direction"]) == "desc" ? value : 1 - value) * Num(rule["weight"]);
            }
            return total;
        }
        var ordered = display.OrderBy(c => c, Comparer<JsonObject>.Create((a, b) =>
        {
            var diff = PinRank(b, sort, food).CompareTo(PinRank(a, sort, food)); if (diff != 0) return diff;
            diff = Bool(b[food ? "meetsRequiredFood" : "meetsRequiredBeverage"]).CompareTo(Bool(a[food ? "meetsRequiredFood" : "meetsRequiredBeverage"])); if (diff != 0) return diff;
            if (food && Bool(a["customRecipe"]) && Bool(b["customRecipe"])) { diff = Num(a["customRecipeSortOrder"], 9007199254740991d).CompareTo(Num(b["customRecipeSortOrder"], 9007199254740991d)); if (diff != 0) return diff; }
            diff = CompareSpecialRows(a, b, sort, food); if (diff != 0) return diff;
            diff = Score(b).CompareTo(Score(a)); return diff != 0 ? diff : food ? RecommendationEngine.CompareFoodCandidates(a, b) : RecommendationEngine.CompareBeverageCandidates(a, b);
        }));
        var seen = new HashSet<string>(StringComparer.Ordinal); var counts = new Dictionary<double, int>(); var rows = new List<JsonObject>();
        foreach (var candidate in ordered)
        {
            var key = food ? RecipeKey(candidate) : Key(Field(candidate, "beverage", "id")); if (!seen.Add(key)) continue;
            var id = Field(candidate, food ? "recipe" : "beverage", "id"); counts.TryGetValue(id, out var count); if (food && count >= variants) continue; counts[id] = count + 1;
            rows.Add(food ? FoodRow(candidate) : BeverageRow(candidate)); if (rows.Count >= limit) break;
        }
        return rows.ToArray();
    }
    private static int CompareSpecialRows(JsonObject a, JsonObject b, JsonObject sort, bool food)
    {
        var diff = SpecialRank(b, sort, food).CompareTo(SpecialRank(a, sort, food)); if (diff != 0) return diff;
        var kind = food ? "recipe" : "beverage"; var preferences = food ? "matchedPositiveTags" : "matchedTags";
        if (Bool(sort["specialPreferDamageLevel"]))
        {
            if (food) { diff = Count(a, "matchedNegativeTags").CompareTo(Count(b, "matchedNegativeTags")); if (diff != 0) return diff; }
            diff = Field(b, kind, "level").CompareTo(Field(a, kind, "level")); if (diff != 0) return diff;
            diff = Count(b, preferences).CompareTo(Count(a, preferences)); if (diff != 0) return diff;
            diff = Field(b, kind, "price").CompareTo(Field(a, kind, "price")); if (diff != 0) return diff;
            diff = food ? Num(a["resourcePressure"]).CompareTo(Num(b["resourcePressure"])) : StockRank(b).CompareTo(StockRank(a)); if (diff != 0) return diff;
        }
        if (Bool(sort[food ? "specialPreferHighFoodLevel" : "specialPreferHighBeverageLevel"]))
        {
            if (food) { diff = Count(a, "matchedNegativeTags").CompareTo(Count(b, "matchedNegativeTags")); if (diff != 0) return diff; }
            diff = Count(b, preferences).CompareTo(Count(a, preferences)); if (diff != 0) return diff;
            diff = Field(b, kind, "level").CompareTo(Field(a, kind, "level")); if (diff != 0) return diff;
        }
        if (Bool(sort["specialPreferYuyukoPositiveSpell"])) { diff = YuyukoRank(b, sort, food, "retake-tag-order").CompareTo(YuyukoRank(a, sort, food, "retake-tag-order")); if (diff != 0) return diff; }
        return sort["specialYuyukoProgressEvaluationMode"] == null ? 0 : YuyukoRank(b, sort, food, Str(sort["specialYuyukoProgressEvaluationMode"])).CompareTo(YuyukoRank(a, sort, food, Str(sort["specialYuyukoProgressEvaluationMode"])));
    }
    private static double StockRank(JsonObject c) => Num(c["ownedQuantity"]) == -1 ? 9007199254740991d : Num(c["ownedQuantity"]);
    private static double Objective(JsonObject c, string key, bool food) => food ? key switch
    {
        "foodPreference" => Count(c, "matchedPositiveTags"), "negativeRisk" => Count(c, "matchedNegativeTags"), "extraCount" => Count(c, "extraIngredients"),
        "resourcePressure" => Num(c["resourcePressure"]), "totalCost" => Num(c["baseCost"]) + Num(c["extraCost"]), "profit" => Field(c, "recipe", "price") - Num(c["baseCost"]) - Num(c["extraCost"]), "cookerAvailable" => Bool(c["cookerAvailable"]) ? 1 : 0, _ => 0
    } : key switch { "beveragePreference" => Count(c, "matchedTags"), "profit" => Field(c, "beverage", "price"), "beverageStock" => StockRank(c), _ => 0 };
    private static double RowLimit(double value) => double.IsFinite(value) ? Math.Max(0, Math.Truncate(value)) : double.PositiveInfinity;
    public static JsonObject FoodRow(JsonObject food)
    {
        var result = Object(("recipe", food["recipe"]), ("extraIngredients", food["extraIngredients"]));
        foreach (var key in new[] { "customRecipe", "customRecipePinned", "customRecipeSortOrder", "customRecipeScope", "customRecipeId", "extraIngredientReasonTags" }) if (food.ContainsKey(key)) result[key] = Clone(food[key]);
        result["allTags"] = Clone(food["activeTags"]); result["cancelledTags"] = Clone(food["suppressedTags"]); result["meetsRequiredFood"] = Clone(food["meetsRequiredFood"]); result["baseCost"] = Clone(food["baseCost"]); result["extraCost"] = Clone(food["extraCost"]); return result;
    }
    private static JsonObject BeverageRow(JsonObject beverage) => Object(("beverage", beverage["beverage"]), ("meetsRequiredBev", beverage["meetsRequiredBeverage"]), ("matchedTags", beverage["matchedTags"]));
    private static void ProjectPrimary(ref JsonObject[] foods, ref JsonObject[] beverages, JsonObject? plan, double limit, double variants, JsonObject sort)
    {
        if (plan == null || Str(plan["bucket"]) == "blocked") return;
        if (plan["food"] is JsonObject food)
        {
            var primary = FoodRow(food); primary["missionTarget"] = RecommendationEngine.IsMissionRecipeExecutionPlan(plan, sort); var key = RecipeKey(primary);
            var rows = new List<JsonObject>(); var counts = new Dictionary<double, int>();
            if (RowLimit(limit) > 0 && RowLimit(variants) > 0)
            foreach (var row in new[] { primary }.Concat(foods.Where(x => RecipeKey(x) != key)))
            {
                var id = Field(row, "recipe", "id"); counts.TryGetValue(id, out var count); if (count >= RowLimit(variants)) continue; counts[id] = count + 1; rows.Add(row); if (rows.Count >= RowLimit(limit)) break;
            }
            foods = rows.ToArray();
        }
        if (plan["beverage"] is JsonObject beverage) beverages = new[] { BeverageRow(beverage) }.Concat(beverages.Where(x => Field(x, "beverage", "id") != Field(beverage, "beverage", "id"))).Take((int)RowLimit(limit)).ToArray();
    }
}
