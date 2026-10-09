using MystiaStewardCompanion.Business.Domain.Orders;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.SpecialBusiness;

public static partial class NormalTargetSelector
{
    /// <summary>幽幽子普通形态保留原料理和酒水，优先可推进评价，其次明确的普通评价清理。</summary>
    private static JsonObject SelectYuyuko(JsonObject args, OrderCandidateCache cache)
    {
        var s = Obj(args["specialBusiness"]); var o = Obj(args["order"]); var data = Obj(args["data"]); var challenge = Str(s["challengeType"]);
        if (!SpecialBusinessRules.PhaseThree(Str(s["phase"]))) return Empty();
        if (args["runtime"] == null || Str(data["source"]) != "runtime") return Empty("幽幽子第三阶段等待运行时推荐数据后再选择高评价执行目标。");
        var context = Context(args); if (context == null) return Empty("幽幽子第三阶段缺少完整库存、厨具或菜单运行时数据，暂不选择自动化执行目标。");
        var preferences = challenge == "Challenge_Yuyuko" ? Objects(data["rareCustomerProfiles"]).FirstOrDefault(x => Num(x["id"]) == 23) : null;
        if (challenge == "Challenge_Yuyuko" && preferences == null) return Empty("幽幽子重修第三阶段缺少运行时 characterId=23 的料理喜好/厌恶档案，暂不处理分身精确订单。");
        var recipe = Recipe(o, data); if (recipe == null) return Empty($"幽幽子第三阶段无法找到 {FoodLabel(o, "料理 #")} 的配方数据。");
        var drink = Beverage(o, data); if (drink == null) return Empty($"幽幽子第三阶段无法找到 {BeverageLabel(o, "酒水 #")} 的酒水数据。");
        var basis = Strings(recipe["positiveTags"]);
        var customer = Customer(23, "幽幽子第三阶段精确订单", Strings(preferences?["positiveTags"]).Where(x => !basis.Contains(x)), Strings(preferences?["negativeTags"]).Where(x => !basis.Contains(x)), System.Array.Empty<string>(), false);
        var demand = Demand(customer); var exact = ExactData(data, recipe, drink); var story = challenge == "Story_Yuyuko";
        var searched = Objects(cache.Food(exact, demand, story ? WithoutExtras(context) : context)).ToArray();
        var baseFoods = story ? searched : Objects(cache.Food(exact, demand, WithoutExtras(context))).ToArray();
        var foodMap = new Dictionary<string, JsonObject>();
        foreach (var f in searched.Concat(baseFoods)) foodMap[$"{Key(Number(f, "recipe", "id"))}:{string.Join(",", Objects(f["extraIngredients"]).Select(x => Num(x["id"])).OrderBy(x => x).Select(Key))}"] = f;
        var foods = foodMap.Values.Where(x => NoHardFailures(x)).ToArray();
        var searchedBeverages = Objects(cache.Beverage(exact, demand, context)).ToArray(); var beverages = searchedBeverages.Where(x => NoHardFailures(x)).ToArray();
        double PairScore(JsonObject f, JsonObject b) => YuyukoNormalPairScore(challenge, preferences, f, b);
        double FoodScore(JsonObject f) => beverages.Length > 0 ? PairScore(f, beverages[0]) : (story ? Number(f, "recipe", "level") * 1000 : 0) + Number(f, "recipe", "price") - Num(f["resourcePressure"]) - Count(f, "extraIngredients");
        double BeverageScore(JsonObject b) => (story ? Number(b, "beverage", "level") * 1000 : 0) + Number(b, "beverage", "price") + Stock(b, 20);
        bool Qualified(JsonObject f, JsonObject b, bool progress)
        { var e = YuyukoEvaluation.NormalPair(challenge, f, b, preferences); return Str(e["mode"]) != "unsupported" && (progress ? Num(e["evaluationScore"]) >= 3 : Num(e["evaluationScore"]) >= 2 && Num(e["evaluationScore"]) < 3); }
        foreach (var progress in new[] { true, false })
        {
            var best = BestPair(foods, beverages, FoodScore, BeverageScore, (f, b) => Qualified(f, b, progress) ? PairScore(f, b) : double.NegativeInfinity);
            if (best == null || !Qualified(best.Value.Food, best.Value.Beverage, progress)) continue;
            var e = YuyukoEvaluation.NormalPair(challenge, best.Value.Food, best.Value.Beverage, preferences); var mode = progress ? "progress" : "refresh";
            return Target(o, best.Value.Food, best.Value.Beverage, YuyukoNormalReason(best.Value.Food, best.Value.Beverage, e, progress), Object(("executionMode", mode), ("expectedFoodModifierTags", e["effectiveModifierTags"])));
        }
        var details = new List<string>();
        if (foods.Length == 0) details.Add("原料理候选 0（可能未解锁、缺基础材料或厨具不可用）");
        if (beverages.Length == 0) details.Add($"原酒水 {BeverageLabel(o)} 候选 0（可能未持有、未解锁或被排除）");
        var diagnosticFood = foods.OrderByDescending(FoodScore).FirstOrDefault(); var diagnosticBeverage = beverages.OrderByDescending(BeverageScore).FirstOrDefault();
        if (diagnosticFood != null && diagnosticBeverage != null)
        {
            var e = YuyukoEvaluation.NormalPair(challenge, diagnosticFood, diagnosticBeverage, preferences);
            if (Count(e, "negativeModifierTags") > 0) details.Add($"生效料理修饰包含幽幽子厌恶 Tag {string.Join("、", Strings(e["negativeModifierTags"]))}");
            if (Num(e["evaluationScore"]) < 3)
            {
                var evidence = Str(e["mode"]) == "story-level-sum" ? $"料理 Lv.{Key(Number(diagnosticFood, "recipe", "level"))}，酒水 Lv.{Key(Number(diagnosticBeverage, "beverage", "level"))}，等级合计 {Key(Num(e["levelSum"]))}" : $"原生普通评价基准 Normal，{ModifierEvidence(e)}";
                details.Add($"预计{YuyukoEvaluation.EvaluationLabel(Num(e["evaluationScore"]))}，未达满意（Good）/完美（ExGood）（{evidence}）");
            }
        }
        if (details.Count == 0) details.Add("未找到可预测推进进度或安全清理的原订单料理/酒水组合");
        return Empty($"幽幽子第三阶段原订单 {Str(recipe["name"])} / {BeverageLabel(o)} 暂不能推进或安全清理；{string.Join("；", details)}；候选统计：原料理搜索 {searched.Length}、无加料 {baseFoods.Length}、可执行 {foods.Length}，原酒水搜索 {searchedBeverages.Length}、可执行 {beverages.Length}");
    }
    private static double YuyukoNormalPairScore(string challenge, JsonObject? preferences, JsonObject f, JsonObject b)
    {
        var e = YuyukoEvaluation.NormalPair(challenge, f, b, preferences);
        var tie = Str(e["mode"]) == "story-level-sum" ? Num(e["levelSum"]) * 1e6 : Count(e, "positiveModifierTags") * 100000 - Count(e, "negativeModifierTags") * 1e6;
        return Num(e["evaluationScore"]) * 1e7 + tie + Math.Min(Number(f, "recipe", "price") + Number(b, "beverage", "price"), 999) * 10 + Stock(b, 99) - Math.Ceiling(Num(f["resourcePressure"]) * 100) - Count(f, "extraIngredients");
    }
    private static string YuyukoNormalReason(JsonObject f, JsonObject b, JsonObject e, bool progress)
    {
        var prefix = Str(e["mode"]) == "story-level-sum" ? "幽幽子剧情版三阶段" : "幽幽子重修三阶段";
        var action = progress ? "用于推进挑战进度" : "不推进进度，仅清理当前普客订单";
        var evidence = Str(e["mode"]) == "story-level-sum" ? $"料理 Lv.{Key(Number(f, "recipe", "level"))}，酒水 Lv.{Key(Number(b, "beverage", "level"))}，等级合计 {Key(Num(e["levelSum"]))}" : $"原生普通评价基准 Normal，{ModifierEvidence(e)}";
        return $"{prefix}{(progress ? "执行" : "清理")}方案：预计 {YuyukoEvaluation.EvaluationLabel(Num(e["evaluationScore"]))}，{action}，{evidence}";
    }
    private static string ModifierEvidence(JsonObject e)
    {
        string Format(string label, string key) => $"{label} {(Count(e, key) > 0 ? string.Join("、", Strings(e[key])) : "无")}";
        return $"{Format("生效修饰 Tag", "effectiveModifierTags")}，{Format("喜好修饰", "positiveModifierTags")}，{Format("厌恶修饰", "negativeModifierTags")}";
    }
}
