using MystiaStewardCompanion.Business.Domain.Orders;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using MystiaStewardCompanion.Business.Domain.Support;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.SpecialBusiness;

/// <summary>
/// 特殊经营普通形态订单的纯目标选择。保留不同挑战的原配方要求、有限候选上限和失败说明。
/// 无缓存的调用结果仅依赖当前完整输入，宿主可按完整输入签名进行有界缓存。
/// </summary>
public static partial class NormalTargetSelector
{
    private static readonly string[] AllPlaces = { "妖怪兽道", "人间之里", "博丽神社", "红魔馆", "迷途竹林", "魔法森林", "妖怪之山", "旧地狱", "地灵殿", "命莲寺", "神灵庙", "太阳花田", "辉针城", "月之都", "魔界" };
    public static JsonObject Select(JsonObject args, OrderCandidateCache? cache = null)
    {
        cache ??= new OrderCandidateCache(); var special = args["specialBusiness"]; var s = Obj(special);
        if (special != null && !Bool(s["challengeTypeAvailable"])) return Empty(SpecialBusinessRules.TextOr(Str(s["error"]).Trim(), "特殊经营类型暂时无法读取，自动化目标已暂停。"));
        if (!Bool(s["active"])) return Empty();
        var challenge = Str(s["challengeType"]);
        if (!SpecialBusinessRules.IsRegistered(challenge)) return Empty($"{SpecialBusinessRules.TextOr(Str(s["displayName"]).Trim(), SpecialBusinessRules.TextOr(challenge, "当前特殊经营"))}尚未适配普客自动化执行目标，当前订单已暂停。");
        if (challenge == SpecialBusinessRules.WackyChallenge) return SelectWacky(args, cache);
        if (SpecialBusinessRules.IsYuyuko(challenge)) return SelectYuyuko(args, cache);
        if (challenge == SpecialBusinessRules.YuumaChallenge) return SelectYuuma(args, cache);
        return Empty();
    }
    public static JsonObject Empty(string message = "") => Object(("target", null), ("message", message));
    private static JsonObject? Context(JsonObject args)
    { if (args["runtime"] is not JsonObject runtime) return null; var data = Obj(args["data"]); var sets = RuntimeRecommendationSupport.BuildRuntimeSets(runtime, data); return sets == null ? null : RuntimeRecommendationSupport.BuildRecommendationRuntimeContext(runtime, sets, Obj(args["preferences"]), data); }
    private static JsonObject? Recipe(JsonObject o, JsonObject data) => Objects(data["recipes"]).LastOrDefault(x => Num(x["id"]) == Num(o["foodId"]));
    private static JsonObject? Beverage(JsonObject o, JsonObject data) => Objects(data["beverages"]).FirstOrDefault(x => Num(x["id"]) == Num(o["beverageId"]));
    public static bool NoHardFailures(JsonObject candidate, string? ignored = null) => !Objects(candidate["conditionResults"]).Any(x => Str(x["id"]) != ignored && Str(x["status"]) == "fail" && Str(x["severity"]) == "hard");
    private static int Count(JsonObject value, string field) => Arr(value[field]).Count;
    private static double Number(JsonObject value, string owner, string field) => Num(Obj(value[owner])[field]);
    private static double Stock(JsonObject value, double maximum) => Num(value["ownedQuantity"]) == -1 ? maximum : Math.Min(Num(value["ownedQuantity"]), maximum);
    private static string First(IEnumerable<string> tags) => tags.FirstOrDefault(x => x.Trim().Length > 0) ?? "";
    private static string FoodLabel(JsonObject o, string prefix = "#") => SpecialBusinessRules.TextOr(Str(o["foodName"]), prefix + Key(Num(o["foodId"])));
    private static string BeverageLabel(JsonObject o, string prefix = "#") => SpecialBusinessRules.TextOr(Str(o["beverageName"]), prefix + Key(Num(o["beverageId"])));
    public static JsonObject Demand(JsonObject customer, string food = "", string beverage = "", JsonNode? target = null)
    { var result = Object(("type", "rare-tag-order"), ("customer", customer), ("requiredFoodTag", food), ("requiredBeverageTag", beverage)); if (target != null) result["specialFoodTarget"] = Clone(target); return result; }
    public static JsonObject Customer(double id, string name, IEnumerable<string> food, IEnumerable<string> negative, IEnumerable<string> beverage, bool allPlaces = true) => Object(
        ("id", id), ("name", name), ("description", ""), ("dlc", 0), ("places", Array(allPlaces ? AllPlaces : System.Array.Empty<string>())), ("price", new JsonArray(0, 0)), ("enduranceLimit", 1),
        ("positiveTags", Array(food)), ("negativeTags", Array(negative)), ("beverageTags", Array(beverage)), ("collection", false), ("evaluation", new JsonObject()), ("spellCards", Object(("positive", new JsonArray()), ("negative", new JsonArray()))));
    public static JsonObject? ExactCustomer(JsonObject data, double id)
    { var p = Objects(data["rareCustomerProfiles"]).FirstOrDefault(x => Num(x["id"]) == id); return p == null ? null : Customer(Num(p["id"]), Str(p["name"]), Strings(p["positiveTags"]), Strings(p["negativeTags"]), Strings(p["beverageTags"]), false); }
    private static JsonObject SyntheticCustomer(JsonObject o, JsonObject data, bool preferRare, JsonNode? fallback)
    {
        var role = Str(o["specialBusinessRole"]).Trim(); var allowRare = role.Length > 0 && role != "wacky-target-order";
        var rare = allowRare && o["guestId"] != null ? Objects(data["rareCustomers"]).FirstOrDefault(x => Num(x["id"]) == Num(o["guestId"])) : null;
        if (preferRare && rare != null) return rare;
        var normal = o["guestId"] == null ? null : Objects(data["normalCustomers"]).FirstOrDefault(x => Num(x["id"]) == Num(o["guestId"]));
        return Customer(Num(o["guestId"], -1), SpecialBusinessRules.TextOr(Str(o["guestName"]), "普客"),
            SpecialBusinessRules.NormalizeTags(Strings(o["foodPreferenceTags"]).Concat(Strings(normal?["positiveTags"])).Concat(Strings(rare?["positiveTags"])).Concat(Strings(fallback))),
            Strings(rare?["negativeTags"]), SpecialBusinessRules.NormalizeTags(Strings(o["beveragePreferenceTags"]).Concat(Strings(normal?["beverageTags"])).Concat(Strings(rare?["beverageTags"]))));
    }
    private static (JsonObject Food, JsonObject Beverage)? BestPair(IEnumerable<JsonObject> foods, IEnumerable<JsonObject> beverages, Func<JsonObject, double> foodScore, Func<JsonObject, double> beverageScore, Func<JsonObject, JsonObject, double> pairScore)
    {
        var fs = foods.OrderByDescending(foodScore).Take(48).ToArray(); var bs = beverages.OrderByDescending(beverageScore).Take(32).ToArray();
        (JsonObject Food, JsonObject Beverage)? best = null; double score = 0;
        foreach (var f in fs) foreach (var b in bs) { var next = pairScore(f, b); if (best == null || next > score) { best = (f, b); score = next; } }
        return best;
    }
    private static JsonObject Target(JsonObject order, JsonObject food, JsonObject beverage, string reason, JsonObject? options = null)
    {
        options ??= new JsonObject(); var result = SpecialBusinessRules.EmptyWirePolicy(); var recipe = Obj(food["recipe"]); var drink = Obj(beverage["beverage"]);
        foreach (var p in Object(("matchFoodId", order["foodId"]), ("matchBeverageId", order["beverageId"]), ("foodId", recipe["id"]), ("recipeId", recipe["recipeId"]), ("allowYuumaControlledProgression", Bool(options["allowYuumaControlledProgression"])),
            ("recipeName", recipe["name"]), ("extraIngredientIds", Array(Objects(food["extraIngredients"]).Select(x => x["id"]))), ("beverageId", drink["id"]), ("beverageName", drink["name"]), ("cookerName", recipe["cooker"]), ("foodTags", food["activeTags"]),
            ("expectedFoodModifierTags", Array(SpecialBusinessRules.NormalizeTags(options["expectedFoodModifierTags"]))), ("beverageTags", beverage["activeTags"]), ("specialTargetFoodTags", Array(SpecialBusinessRules.NormalizeTags(options["specialTargetFoodTags"]))), ("reason", reason))) result[p.Key] = Clone(p.Value);
        if (Str(options["executionMode"]).Length > 0) result["executionMode"] = Clone(options["executionMode"]);
        return Object(("target", result), ("message", ""));
    }
    private static JsonObject ExactData(JsonObject data, JsonObject recipe, JsonObject beverage)
    { var exact = Obj(Clone(data)); exact["recipes"] = Array(new[] { recipe }); exact["beverages"] = Array(new[] { beverage }); return exact; }
    private static JsonObject WithoutExtras(JsonObject context) { var value = Obj(Clone(context)); value["maxExtraIngredients"] = 0; return value; }
}
