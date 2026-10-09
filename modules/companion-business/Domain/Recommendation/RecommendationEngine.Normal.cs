using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Recommendation;

public static partial class RecommendationEngine
{
    /// <summary>读取地区普客，保留目录顺序用于覆盖说明。</summary>
    public static JsonArray GetNormalCustomersByPlace(JsonObject data, string place) =>
        Array(Objects(data["normalCustomers"]).Where(x => Strings(x["places"]).Contains(place)));

    /// <summary>计算地区普客料理覆盖。此接口只处理地区基础配方，不引入订单预算或加料搜索。</summary>
    public static JsonArray BuildNormalFoodRecommendations(JsonObject options)
    {
        var data = Obj(options["data"]); var context = Obj(options["context"]);
        var customers = Objects(GetNormalCustomersByPlace(data, Str(options["place"]))).ToArray();
        if (customers.Length == 0) return new JsonArray();
        var byName = Objects(data["ingredients"]).GroupBy(x => Str(x["name"])).ToDictionary(x => x.Key, x => x.Last());
        var available = Numbers(context["availableRecipeIds"]).ToHashSet();
        var disabled = Numbers(context["disabledIngredientIds"]).ToHashSet();
        var rows = new List<JsonObject>();
        foreach (var recipe in Objects(data["recipes"]))
        {
            var names = Strings(recipe["ingredients"]);
            if (!available.Contains(Num(recipe["id"])) || names.Any(x => !byName.TryGetValue(x, out var ingredient) || disabled.Contains(Num(ingredient["id"])))) continue;
            var tagOptions = FoodTagOptions(recipe, System.Array.Empty<JsonObject>(), context);
            var tags = ResolveFoodTags(tagOptions);
            var coverage = Coverage(customers, Strings(tags["activeTags"]), "positiveTags");
            var cost = names.Sum(x => byName.TryGetValue(x, out var ingredient) ? Num(ingredient["price"]) : 0);
            var conditions = new JsonArray(Condition("normal.food.coverage", "food", Objects(coverage).Any(x => Num(x["matchedTagCount"]) > 0) ? "boost" : "info", "soft", "普客覆盖", CoverageDetail(coverage)));
            if (Str(recipe["cooker"]).Length > 0) conditions.Add(Condition("normal.food.cooker", "food", "info", "info", "厨具", $"需要 {Str(recipe["cooker"])}"));
            if (Arr(tags["suppressedTags"]).Count > 0) conditions.Add(Condition("normal.food.suppressed-tags", "food", "info", "info", "标签优先级", $"压制 {string.Join("、", Strings(tags["suppressedTags"]))}"));
            rows.Add(Object(("recipe", recipe), ("activeTags", tags["activeTags"]), ("suppressedTags", tags["suppressedTags"]),
                ("customerCoverage", coverage), ("totalCoverage", Objects(coverage).Sum(x => Num(x["matchedTagCount"]))),
                ("coveredCustomerCount", Objects(coverage).Count(x => Num(x["matchedTagCount"]) > 0)), ("profit", Num(recipe["price"]) - cost),
                ("matchedTags", Array(Objects(coverage).SelectMany(x => Strings(x["matchedTags"])).Distinct())), ("ingredientCost", cost), ("conditionResults", conditions)));
        }
        return Sorted(rows, CompareNormalFoodRecommendations);
    }

    /// <summary>计算地区普客酒水覆盖，不使用稀客标签压制流程，保持既有覆盖口径。</summary>
    public static JsonArray BuildNormalBeverageRecommendations(JsonObject options)
    {
        var data = Obj(options["data"]); var context = Obj(options["context"]);
        var customers = Objects(GetNormalCustomersByPlace(data, Str(options["place"]))).ToArray();
        if (customers.Length == 0) return new JsonArray();
        var available = Numbers(context["availableBeverageIds"]).ToHashSet();
        var rows = new List<JsonObject>();
        foreach (var beverage in Objects(data["beverages"]).Where(x => available.Contains(Num(x["id"]))))
        {
            var coverage = Coverage(customers, Strings(beverage["tags"]), "beverageTags");
            rows.Add(Object(("beverage", beverage), ("activeTags", Array(Strings(beverage["tags"]).Distinct())), ("customerCoverage", coverage),
                ("totalCoverage", Objects(coverage).Sum(x => Num(x["matchedTagCount"]))), ("coveredCustomerCount", Objects(coverage).Count(x => Num(x["matchedTagCount"]) > 0)),
                ("matchedTags", Array(Objects(coverage).SelectMany(x => Strings(x["matchedTags"])).Distinct())),
                ("conditionResults", new JsonArray(Condition("normal.beverage.coverage", "beverage", Objects(coverage).Any(x => Num(x["matchedTagCount"]) > 0) ? "boost" : "info", "soft", "普客覆盖", $"{Str(beverage["name"])}: {CoverageDetail(coverage)}")))));
        }
        return Sorted(rows, CompareNormalBeverageRecommendations);
    }

    /// <summary>保留原排序方向：覆盖、人数、材料成本、利润降序，最后按 ID 升序。</summary>
    public static int CompareNormalFoodRecommendations(JsonObject left, JsonObject right) => FirstComparison(
        Num(right["totalCoverage"]).CompareTo(Num(left["totalCoverage"])), Num(right["coveredCustomerCount"]).CompareTo(Num(left["coveredCustomerCount"])),
        Num(right["ingredientCost"]).CompareTo(Num(left["ingredientCost"])), Num(right["profit"]).CompareTo(Num(left["profit"])),
        Num(Obj(left["recipe"])["id"]).CompareTo(Num(Obj(right["recipe"])["id"])));
    /// <summary>覆盖相同时按酒水价格降序及 ID 升序排序。</summary>
    public static int CompareNormalBeverageRecommendations(JsonObject left, JsonObject right) => FirstComparison(
        Num(right["totalCoverage"]).CompareTo(Num(left["totalCoverage"])), Num(right["coveredCustomerCount"]).CompareTo(Num(left["coveredCustomerCount"])),
        Num(Obj(right["beverage"])["price"]).CompareTo(Num(Obj(left["beverage"])["price"])), Num(Obj(left["beverage"])["id"]).CompareTo(Num(Obj(right["beverage"])["id"])));

    private static JsonArray Coverage(IEnumerable<JsonObject> customers, string[] tags, string field) => Array(customers.Select(customer =>
    {
        var matches = Strings(customer[field]).Where(tags.Contains).ToArray();
        return Object(("customerId", customer["id"]), ("customerName", customer["name"]), ("matchedTagCount", matches.Length), ("matchedTags", Array(matches)));
    }));
    private static string CoverageDetail(JsonArray coverage)
    {
        var covered = Objects(coverage).Where(x => Num(x["matchedTagCount"]) > 0).ToArray();
        return covered.Length == 0 ? "未命中当前地区普客偏好。" : string.Join("；", covered.Select(x => $"{Str(x["customerName"])} {string.Join("、", Strings(x["matchedTags"]))}"));
    }
    private static int FirstComparison(params int[] comparisons) => comparisons.FirstOrDefault(x => x != 0);
    private static JsonObject FoodTagOptions(JsonObject recipe, IEnumerable<JsonObject> extras, JsonObject context) => Object(
        ("recipe", recipe), ("extraIngredients", Array(extras)), ("popularFoodTag", context["popularFoodTag"]),
        ("popularHateFoodTag", context["popularHateFoodTag"]), ("famousShopEnabled", Bool(context["famousShopEnabled"])), ("tagPriorityRules", context["tagPriorityRules"]));
}
