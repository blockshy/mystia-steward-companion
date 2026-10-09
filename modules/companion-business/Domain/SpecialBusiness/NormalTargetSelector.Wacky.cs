using MystiaStewardCompanion.Business.Domain.Orders;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.SpecialBusiness;

public static partial class NormalTargetSelector
{
    /// <summary>怪诞经营按普通替代、精确原订单、本体未破防、本体破防四条独立路径选择。</summary>
    private static JsonObject SelectWacky(JsonObject args, OrderCandidateCache cache)
    {
        var s = Obj(args["specialBusiness"]); var o = Obj(args["order"]); var data = Obj(args["data"]);
        if (args["runtime"] == null || Str(data["source"]) != "runtime") return Empty("特殊经营自动化等待运行时推荐数据后再选择普客执行目标。");
        var context = Context(args); if (context == null) return Empty();
        if (SpecialBusinessRules.PhaseThree(Str(s["phase"])) && Str(o["specialBusinessRole"]).Trim() == SpecialBusinessRules.KoishiRole) return Bool(s["wackyKoishiShieldBroken"]) ? WackyBroken(o, s, context, data, cache) : WackyBody(o, s, context, data, cache);
        var tags = Array(SpecialBusinessRules.NormalizeTags(s["foodTargetTags"]));
        var countdown = tags.Count > 0 ? SpecialBusinessRules.CountdownDeferral(s) : ""; if (countdown.Length > 0) return Empty(countdown);
        var high = SpecialBusinessRules.PhaseTwo(Str(s["phase"])) || SpecialBusinessRules.PhaseThree(Str(s["phase"]));
        if (high) return WackyExact(o, s, context, data, Strings(args["rejectedRecipeKeys"]), cache);
        var customer = SyntheticCustomer(o, data, false, null);
        var demand = Demand(customer, SpecialBusinessRules.TextOr(First(Strings(customer["positiveTags"])), First(Strings(tags))), First(Strings(customer["beverageTags"])), Object(("enforcement", tags.Count > 0 ? "require" : "none"), ("match", "any"), ("tags", tags)));
        var foods = Objects(cache.Food(data, demand, context)).Where(x => NoHardFailures(x));
        var beverages = Objects(cache.Beverage(data, demand, context)).Where(x => NoHardFailures(x));
        if (tags.Count > 0) foods = foods.Where(x => WackyCandidateAllowed(x, tags, Strings(args["rejectedRecipeKeys"])));
        var best = BestPair(foods, beverages, f => WackyFoodScore(f, tags), PreferenceBeverageScore, (f, b) => (SpecialBusinessRules.MatchesTags(f["activeTags"], tags, "any") ? 20000 : 0) + (Count(f, "matchedPositiveTags") + Count(b, "matchedTags")) * 1200 - Count(f, "matchedNegativeTags") * 8000 + WackyFoodScore(f, tags) + PreferenceBeverageScore(b));
        var text = string.Join("、", Strings(tags));
        if (best == null) return Empty(tags.Count > 0 ? $"当前怪诞料理目标 Tag 为 {text}，没有可制作且未被实机判定失败的普客替代料理。" : "当前没有可用于怪诞料理大赛高评价的普客替代料理/酒水组合。");
        var matches = Count(best.Value.Food, "matchedPositiveTags") + Count(best.Value.Beverage, "matchedTags");
        if (tags.Count == 0 && matches < 3) return Empty("怪诞料理大赛第一阶段需要至少 3 个喜好 Tag 命中，当前普客没有稳定高评价组合。");
        return Target(o, best.Value.Food, best.Value.Beverage, tags.Count > 0 ? $"怪诞目标 Tag：{text}，高评价命中 {matches} 个喜好 Tag" : $"怪诞高评价命中 {matches} 个喜好 Tag", Object(("specialTargetFoodTags", tags)));
    }
    private static JsonObject WackyBody(JsonObject o, JsonObject s, JsonObject context, JsonObject data, OrderCandidateCache cache)
    {
        var foodsTags = SpecialBusinessRules.NormalizeTags(s["wackyKoishiFoodPreferenceTags"]); var hates = SpecialBusinessRules.NormalizeTags(s["wackyKoishiFoodHateTags"]); var beverageTags = SpecialBusinessRules.NormalizeTags(s["wackyKoishiBeveragePreferenceTags"]);
        if (foodsTags.Length == 0 || beverageTags.Length == 0) return Empty($"怪诞料理三阶段古明地恋本体需要先读取场上揭示的正面料理 Tag 和酒水 Tag，暂不自动提交，避免继续触发差评。\n已读取：正面料理 {(foodsTags.Length > 0 ? string.Join("、", foodsTags) : "无")}；厌恶料理 {(hates.Length > 0 ? string.Join("、", hates) : "无")}；酒水 {(beverageTags.Length > 0 ? string.Join("、", beverageTags) : "无")}。");
        var customer = Customer(Num(o["guestId"], -1), SpecialBusinessRules.TextOr(Str(o["guestName"]), "怪诞料理大赛 · 古明地恋本体"), foodsTags, hates, beverageTags); var demand = Demand(customer, First(foodsTags), First(beverageTags));
        var foods = Objects(cache.Food(data, demand, context)).Where(x => NoHardFailures(x, "food.required-tag") && Count(x, "matchedNegativeTags") == 0 && Count(x, "matchedPositiveTags") >= Math.Min(3, foodsTags.Length));
        var beverages = Objects(cache.Beverage(data, demand, context)).Where(x => NoHardFailures(x, "beverage.required-tag") && Count(x, "matchedTags") >= 1);
        double FoodScore(JsonObject f) => Count(f, "matchedPositiveTags") * 4000 - Count(f, "matchedNegativeTags") * 20000 + Number(f, "recipe", "level") * 120 + Number(f, "recipe", "price") + Count(f, "extraIngredients") * 40 - Num(f["resourcePressure"]);
        double BeverageScore(JsonObject b) => Count(b, "matchedTags") * 3500 + Number(b, "beverage", "level") * 100 + Number(b, "beverage", "price") + Stock(b, 20);
        var best = BestPair(foods, beverages, FoodScore, BeverageScore, (f, b) => Count(f, "matchedPositiveTags") * 6000 + Count(b, "matchedTags") * 5000 + FoodScore(f) + BeverageScore(b));
        if (best == null) return Empty($"怪诞料理三阶段古明地恋本体当前揭示：正面料理 {string.Join("、", foodsTags)}，厌恶料理 {(hates.Length > 0 ? string.Join("、", hates) : "无")}，酒水 {string.Join("、", beverageTags)}；没有可制作且可稳定高评价的料理/酒水组合。");
        return Target(o, best.Value.Food, best.Value.Beverage, $"古明地恋本体：命中正面料理 {string.Join("、", Strings(best.Value.Food["matchedPositiveTags"]))}，酒水 {string.Join("、", Strings(best.Value.Beverage["matchedTags"]))}，避开厌恶 Tag{(hates.Length > 0 ? " " + string.Join("、", hates) : "")}");
    }
    private static JsonObject WackyBroken(JsonObject o, JsonObject s, JsonObject context, JsonObject data, OrderCandidateCache cache)
    {
        var recipe = Recipe(o, data); if (recipe == null) return Empty($"怪诞料理三阶段古明地恋本体已破防，但无法找到原订单料理 {FoodLabel(o)} 的配方数据。");
        var drink = Beverage(o, data); if (drink == null) return Empty($"怪诞料理三阶段古明地恋本体已破防，但无法找到原订单酒水 {BeverageLabel(o)} 的数据。");
        var customer = SyntheticCustomer(o, data, true, recipe["positiveTags"]);
        var demand = Demand(customer, SpecialBusinessRules.TextOr(First(Strings(customer["positiveTags"])), First(Strings(recipe["positiveTags"]))), SpecialBusinessRules.TextOr(First(Strings(customer["beverageTags"])), First(Strings(drink["tags"]))));
        var foods = Objects(cache.Food(data, demand, context)).Where(x => NoHardFailures(x, "food.required-tag") && Number(x, "recipe", "id") == Num(recipe["id"]) && Count(x, "matchedNegativeTags") == 0);
        var beverages = Objects(cache.Beverage(data, demand, context)).Where(x => NoHardFailures(x, "beverage.required-tag") && Number(x, "beverage", "id") == Num(drink["id"]));
        var planning = Object(("remainingBudget", KoishiEvaluation.NonNegativeInt(o["fund"])), ("remainingScore", KoishiEvaluation.RemainingScore(s)), ("remainingOrderCount", KoishiEvaluation.NonNegativeInt(o["remainingOrderCount"])));
        var best = BestPair(foods, beverages, f => Count(f, "matchedPositiveTags") * 10000 + Number(f, "recipe", "level") * 500 - Num(f["resourcePressure"]), b => Count(b, "matchedTags") * 10000 + Number(b, "beverage", "level") * 500 - Number(b, "beverage", "price") + Stock(b, 20), (f, b) => KoishiEvaluation.PairScore(f, b, planning));
        if (best == null) return Empty($"怪诞料理三阶段古明地恋本体已破防，但 {Str(recipe["name"])} / {Str(drink["name"])} 当前没有可制作且满足原订单的方案。");
        return Target(o, best.Value.Food, best.Value.Beverage, $"古明地恋破防：保持原订单 {Str(recipe["name"])} / {Str(drink["name"])}，按预算内总分规划，料理 Lv.{Key(Number(best.Value.Food, "recipe", "level"))} / 酒水 Lv.{Key(Number(best.Value.Beverage, "beverage", "level"))}{KoishiEvaluation.BudgetReason(best.Value.Food, best.Value.Beverage, planning)}");
    }
    private static JsonObject WackyExact(JsonObject o, JsonObject s, JsonObject context, JsonObject data, string[] rejected, OrderCandidateCache cache)
    {
        var tags = Array(SpecialBusinessRules.NormalizeTags(s["foodTargetTags"])); var recipe = Recipe(o, data); if (recipe == null) return Empty($"怪诞料理大赛无法找到原订单料理 {FoodLabel(o)} 的配方数据。");
        var drink = Beverage(o, data); if (drink == null) return Empty($"怪诞料理大赛无法找到原订单酒水 {BeverageLabel(o)} 的数据。");
        var customer = SyntheticCustomer(o, data, true, recipe["positiveTags"]);
        var demand = Demand(customer, SpecialBusinessRules.TextOr(First(Strings(customer["positiveTags"])), SpecialBusinessRules.TextOr(First(Strings(recipe["positiveTags"])), First(Strings(tags)))), SpecialBusinessRules.TextOr(First(Strings(customer["beverageTags"])), First(Strings(drink["tags"]))), Object(("enforcement", tags.Count > 0 ? "require" : "none"), ("match", "any"), ("tags", tags)));
        var foods = Objects(cache.Food(data, demand, context)).Where(x => NoHardFailures(x) && Number(x, "recipe", "id") == Num(recipe["id"]) && Count(x, "matchedNegativeTags") == 0 && WackyCandidateAllowed(x, tags, rejected));
        var beverages = Objects(cache.Beverage(data, demand, context)).Where(x => NoHardFailures(x) && Number(x, "beverage", "id") == Num(drink["id"]));
        double Evaluation(JsonObject f, JsonObject? b) => 2 + Count(f, "matchedPositiveTags") + (b == null ? 0 : Count(b, "matchedTags")) - Count(f, "matchedNegativeTags") * 2;
        double FoodScore(JsonObject f, bool target) => (target && SpecialBusinessRules.MatchesTags(f["activeTags"], tags, "any") ? 10000 : 0) + Evaluation(f, null) * 1000 + Number(f, "recipe", "level") * 40 + Number(f, "recipe", "price") - Num(f["resourcePressure"]);
        var best = BestPair(foods, beverages, f => FoodScore(f, true), PreferenceBeverageScore, (f, b) => Evaluation(f, b) * 2000 + FoodScore(f, false) + PreferenceBeverageScore(b));
        var text = string.Join("、", Strings(tags));
        if (best == null) return Empty($"怪诞料理大赛需要按原订单制作 {Str(recipe["name"])} / {Str(drink["name"])}{(tags.Count > 0 ? $"并满足当前怪诞 Tag {text}" : "")}，当前没有安全的高评价加料方案。");
        var matches = Count(best.Value.Food, "matchedPositiveTags") + Count(best.Value.Beverage, "matchedTags");
        if (Count(best.Value.Food, "matchedNegativeTags") != 0 || matches < 2 || Evaluation(best.Value.Food, best.Value.Beverage) < 4) return Empty($"怪诞料理大赛需要原订单最高评价，{Str(recipe["name"])} / {Str(drink["name"])} 当前仅命中 {matches} 个喜好 Tag，等待更安全的订单或目标刷新。");
        return Target(o, best.Value.Food, best.Value.Beverage, tags.Count > 0 ? $"怪诞高评价：保持原订单，目标 Tag {text}，命中 {matches} 个喜好 Tag" : $"怪诞高评价：保持原订单，命中 {matches} 个喜好 Tag", Object(("specialTargetFoodTags", tags)));
    }
    private static bool WackyCandidateAllowed(JsonObject f, JsonArray tags, string[] rejected)
    {
        if (tags.Count > 0 && !SpecialBusinessRules.MatchesTags(f["activeTags"], tags, "any")) return false;
        var key = SpecialBusinessRules.RejectedRecipeKey(tags, Number(f, "recipe", "id"), Number(f, "recipe", "recipeId"), Objects(f["extraIngredients"]).Select(x => Num(x["id"]))); return key.Length == 0 || !rejected.Contains(key);
    }
    private static double WackyFoodScore(JsonObject f, JsonArray tags) => (SpecialBusinessRules.MatchesTags(f["activeTags"], tags, "any") ? 10000 : 0) + Count(f, "matchedPositiveTags") * 600 - Count(f, "matchedNegativeTags") * 5000 + Number(f, "recipe", "level") * 40 + Number(f, "recipe", "price") - Num(f["resourcePressure"]);
    private static double PreferenceBeverageScore(JsonObject b) => Count(b, "matchedTags") * 300 + Number(b, "beverage", "level") * 20 + Number(b, "beverage", "price") + Stock(b, 20);
}
