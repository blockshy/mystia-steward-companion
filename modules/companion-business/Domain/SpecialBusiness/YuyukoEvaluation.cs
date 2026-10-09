using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.SpecialBusiness;

/// <summary>
/// 幽幽子剧情、重修及二阶段符卡的评价模型。三种评价分别使用等级、实际修饰标签和稀客点单，
/// 不把一种场景的高分结果套用到另一种场景；返回值仅作选择依据，实际评价仍由游戏适配层确认。
/// </summary>
public static class YuyukoEvaluation
{
    public static double LevelSum(JsonNode? food, JsonNode? beverage) => Math.Max(0, Num(Obj(Obj(food)["recipe"])["level"])) + Math.Max(0, Num(Obj(Obj(beverage)["beverage"])["level"]));
    private static string[] Unique(IEnumerable<string> tags) => tags.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    private static string[] Without(JsonNode? tags, string required) => Unique(Strings(tags).Where(x => x.Trim() != required.Trim()));
    public static string[] NegativeTags(JsonNode? food, string required) => Without(Obj(food)["matchedNegativeTags"], required);
    private static double InventoryRank(JsonNode? value) => Num(value) == -1 ? 99 : Math.Min(Num(value), 99);
    public static JsonObject TagOrderPair(JsonNode? food, JsonNode? beverage, JsonNode? demand)
    {
        var f = Obj(food); var b = Obj(beverage); var d = Obj(demand);
        var basics = (Bool(f["meetsRequiredFood"]) ? 1 : 0) + (Bool(b["meetsRequiredBeverage"]) ? 1 : 0);
        var foods = Without(f["matchedPositiveTags"], Str(d["requiredFoodTag"])); var beverages = Without(b["matchedTags"], Str(d["requiredBeverageTag"]));
        return Object(("baseDemandScore", basics), ("extraPreferenceScore", foods.Length + beverages.Length), ("evaluationScore", basics + foods.Length + beverages.Length),
            ("foodExtraPreferenceTags", Array(foods)), ("beverageExtraPreferenceTags", Array(beverages)), ("negativeTags", Array(NegativeTags(food, Str(d["requiredFoodTag"])))));
    }
    public static JsonObject PositiveSpellPair(JsonNode? food, JsonNode? beverage, JsonNode? demand)
    {
        var e = TagOrderPair(food, beverage, demand);
        e["canTriggerPositiveSpell"] = food != null && beverage != null && Num(e["baseDemandScore"]) == 2 && Arr(e["negativeTags"]).Count == 0 && Num(e["evaluationScore"]) >= 4;
        return e;
    }
    public static bool IsPositiveSpellPlan(JsonObject plan) => Str(plan["bucket"]) != "blocked" && Bool(PositiveSpellPair(plan["food"], plan["beverage"], plan["demand"])["canTriggerPositiveSpell"]);
    public static double PositiveFoodRank(JsonObject food, string required) => !Bool(food["meetsRequiredFood"]) || NegativeTags(food, required).Length > 0 ? 0 : 10000 + Without(food["matchedPositiveTags"], required).Length * 10000 - Math.Min(Num(Obj(food["recipe"])["price"]), 999) - Math.Ceiling(Num(food["resourcePressure"]) * 10) - Arr(food["extraIngredients"]).Count;
    public static double PositiveBeverageRank(JsonObject beverage, string required) => !Bool(beverage["meetsRequiredBeverage"]) ? 0 : 10000 + Without(beverage["matchedTags"], required).Length * 10000 - Math.Min(Num(Obj(beverage["beverage"])["price"]), 999) + InventoryRank(beverage["ownedQuantity"]);
    public static double PositivePlanScore(JsonObject plan)
    {
        if (plan["food"] == null || plan["beverage"] == null || Str(plan["bucket"]) == "blocked") return double.NegativeInfinity;
        var f = Obj(plan["food"]); var b = Obj(plan["beverage"]); var e = PositiveSpellPair(f, b, plan["demand"]);
        return (Bool(e["canTriggerPositiveSpell"]) ? 1e9 : 0) + Num(e["evaluationScore"]) * 1e7 + Num(e["extraPreferenceScore"]) * 1e6 + Num(e["baseDemandScore"]) * 1e5
            + Math.Min(Num(Obj(f["recipe"])["price"]) + Num(Obj(b["beverage"])["price"]), 999) * 10 + InventoryRank(b["ownedQuantity"])
            - Arr(e["negativeTags"]).Count * 1e8 - Math.Ceiling(Num(f["resourcePressure"]) * 100) - Arr(f["extraIngredients"]).Count;
    }
    public static int ComparePositivePlans(JsonObject left, JsonObject right)
    { var score = PositivePlanScore(right).CompareTo(PositivePlanScore(left)); return score != 0 ? score : Num(left["estimatedPrice"]).CompareTo(Num(right["estimatedPrice"])); }

    /// <summary>普通形态：重修版只计入排除基础料理标签后的实际修饰标签。</summary>
    public static JsonObject NormalPair(string challenge, JsonNode? food, JsonNode? beverage, JsonNode? preferences)
    {
        var level = LevelSum(food, beverage); var mode = "unsupported"; double score = 0;
        string[] effective = System.Array.Empty<string>(), positive = System.Array.Empty<string>(), negative = System.Array.Empty<string>();
        if (challenge == "Story_Yuyuko") { mode = "story-level-sum"; score = food != null && beverage != null ? StoryScore(level) : 0; }
        else if (challenge == "Challenge_Yuyuko")
        {
            mode = preferences != null ? "retake-food-modifiers" : "unsupported";
            if (food != null && beverage != null && preferences != null)
            {
                var f = Obj(food); var recipe = Obj(f["recipe"]); var basis = Strings(recipe["positiveTags"]).ToHashSet(StringComparer.Ordinal);
                effective = new[] { Str(recipe["cooker"]) }.Concat(Strings(f["activeTags"])).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Where(x => !basis.Contains(x)).ToArray();
                positive = effective.Where(Strings(Obj(preferences)["positiveTags"]).Contains).ToArray(); negative = effective.Where(Strings(Obj(preferences)["negativeTags"]).Contains).ToArray();
                score = Math.Clamp(2 + positive.Length - negative.Length, 0, 4);
            }
        }
        return Object(("mode", mode), ("evaluationScore", score), ("levelSum", level), ("effectiveModifierTags", Array(effective)), ("positiveModifierTags", Array(positive)), ("negativeModifierTags", Array(negative)));
    }
    public static JsonObject RarePair(string mode, JsonNode? food, JsonNode? beverage, JsonNode? demand)
    {
        var e = TagOrderPair(food, beverage, demand); var level = LevelSum(food, beverage);
        if (mode == "story-level-sum")
        {
            e["evaluationScore"] = food != null && beverage != null ? StoryScore(level) : 0;
            e["extraPreferenceScore"] = 0; e["foodExtraPreferenceTags"] = new JsonArray(); e["beverageExtraPreferenceTags"] = new JsonArray(); e["negativeTags"] = new JsonArray();
            e["canProgress"] = Num(e["baseDemandScore"]) == 2 && Num(e["evaluationScore"]) >= 3;
        }
        else if (mode == "retake-tag-order")
        {
            e["evaluationScore"] = Math.Clamp(Num(e["evaluationScore"]) - Arr(e["negativeTags"]).Count, 0, 4);
            e["canProgress"] = food != null && beverage != null && Num(e["baseDemandScore"]) == 2 && Arr(e["negativeTags"]).Count == 0 && Num(e["evaluationScore"]) >= 3;
        }
        else { e["evaluationScore"] = 0; e["extraPreferenceScore"] = 0; e["foodExtraPreferenceTags"] = new JsonArray(); e["beverageExtraPreferenceTags"] = new JsonArray(); e["negativeTags"] = new JsonArray(); e["canProgress"] = false; }
        e["mode"] = mode; e["levelSum"] = level; return e;
    }
    public static bool IsProgressPlan(JsonObject plan, string mode) => Str(plan["bucket"]) != "blocked" && Bool(RarePair(mode, plan["food"], plan["beverage"], plan["demand"])["canProgress"]);
    public static double PairScore(string mode, JsonObject food, JsonObject beverage, JsonNode? demand)
    {
        var e = RarePair(mode, food, beverage, demand);
        return (Bool(e["canProgress"]) ? 1e9 : 0) + (Num(e["evaluationScore"]) >= 4 ? 1e8 : 0) + Num(e["evaluationScore"]) * 1e7
            + Num(e[mode == "story-level-sum" ? "levelSum" : "extraPreferenceScore"]) * 1e6 + Num(e["baseDemandScore"]) * 1e5
            + Math.Min(Num(Obj(food["recipe"])["price"]) + Num(Obj(beverage["beverage"])["price"]), 999) * 10 + InventoryRank(beverage["ownedQuantity"])
            - Arr(e["negativeTags"]).Count * 1e8 - Math.Ceiling(Num(food["resourcePressure"]) * 100) - Arr(food["extraIngredients"]).Count;
    }
    public static double PlanScore(JsonObject plan, string mode) => plan["food"] == null || plan["beverage"] == null || Str(plan["bucket"]) == "blocked" ? double.NegativeInfinity : PairScore(mode, Obj(plan["food"]), Obj(plan["beverage"]), plan["demand"]);
    public static int ComparePlans(JsonObject left, JsonObject right, string mode)
    { var score = PlanScore(right, mode).CompareTo(PlanScore(left, mode)); return score != 0 ? score : Num(left["estimatedPrice"]).CompareTo(Num(right["estimatedPrice"])); }
    public static string PositiveReason(JsonObject plan)
    {
        var e = PositiveSpellPair(plan["food"], plan["beverage"], plan["demand"]);
        return $"幽幽子二阶段正面符卡：预计完美（ExGood），点单基础 {Key(Num(e["baseDemandScore"]))}，{PreferenceText(e)}，{NegativeText(e)}";
    }
    public static string ProgressReason(JsonObject plan, string mode)
    {
        var e = RarePair(mode, plan["food"], plan["beverage"], plan["demand"]); var score = Num(e["evaluationScore"]); var label = score >= 3 ? EvaluationLabel(score) : "未达稳定推进评价";
        return mode == "story-level-sum" ? $"幽幽子三阶段剧情版：预计 {label}，推进阈值等级合计 >= 5，料理 Lv.{Key(Num(Obj(Obj(plan["food"])["recipe"])["level"]))}，酒水 Lv.{Key(Num(Obj(Obj(plan["beverage"])["beverage"])["level"]))}，等级合计 {Key(Num(e["levelSum"]))}"
            : $"幽幽子三阶段重修版：预计 {label}，点单基础 {Key(Num(e["baseDemandScore"]))}，{PreferenceText(e)}，{NegativeText(e)}";
    }
    public static string[] BlockedMessages(IEnumerable<JsonObject> plans, string mode, bool positiveSpell = false, int limit = 3)
    {
        var values = plans.ToList(); var result = values.Where(p => positiveSpell ? !IsPositiveSpellPlan(p) : !IsProgressPlan(p, mode))
            .OrderBy(p => p, Comparer<JsonObject>.Create((a, b) => positiveSpell ? ComparePositivePlans(a, b) : ComparePlans(a, b, mode)))
            .Select(p => BlockReason(p, mode, positiveSpell)).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Take(Math.Max(1, limit)).ToArray();
        return result.Length > 0 || values.Count == 0 ? result : new[] { positiveSpell ? "幽幽子第二阶段没有可预测触发正面符卡的完美（ExGood）执行方案。" : "幽幽子第三阶段没有可预测推进进度的执行方案。" };
    }
    private static string BlockReason(JsonObject plan, string mode, bool positive)
    {
        var f = plan["food"]; var b = plan["beverage"]; var d = Obj(plan["demand"]); var e = positive ? PositiveSpellPair(f, b, d) : RarePair(mode, f, b, d); var parts = new List<string>();
        if (f == null) parts.Add("缺少料理候选"); else
        {
            if (!Bool(Obj(f)["meetsRequiredFood"])) parts.Add($"料理未满足点单 {SpecialBusinessRules.TextOr(Str(d["requiredFoodTag"]), "未知")}");
            if (Arr(e["negativeTags"]).Count > 0) parts.Add($"包含当前稀客厌恶 Tag {string.Join("、", Strings(e["negativeTags"]))}");
        }
        if (b == null) parts.Add("缺少酒水候选"); else if (!Bool(Obj(b)["meetsRequiredBeverage"])) parts.Add($"酒水未满足点单 {SpecialBusinessRules.TextOr(Str(d["requiredBeverageTag"]), "未知")}");
        if (positive && f != null && b != null && Num(e["baseDemandScore"]) == 2 && Num(e["extraPreferenceScore"]) < 2)
            parts.Add($"除点单 Tag 外仅命中 {Key(Num(e["extraPreferenceScore"]))} 个当前稀客喜好，触发正面符卡需要至少 2 个");
        if (!positive && f != null && b != null && Num(e["evaluationScore"]) < 3)
        {
            var evidence = mode == "story-level-sum" ? $"等级合计 {Key(Num(e["levelSum"]))}" : $"点单基础 {Key(Num(e["baseDemandScore"]))}，额外喜好 {Key(Num(e["extraPreferenceScore"]))}";
            parts.Add($"预计{EvaluationLabel(Num(e["evaluationScore"]))}，未达满意（Good）/完美（ExGood）（{evidence}）");
        }
        parts.AddRange(Objects(plan["conditionResults"]).Where(x => Str(x["status"]) == "fail" && Str(x["severity"]) == "hard").Select(x => Str(x["detail"])));
        if (parts.Count == 0 && Str(plan["bucket"]) == "blocked") parts.Add("组合被推荐引擎标记为不可执行");
        if (parts.Count == 0 && positive) parts.Add("预计无法获得完美（ExGood）评价并触发正面符卡");
        return parts.Count == 0 ? "" : $"{PlanLabel(plan)}：{string.Join("；", Unique(parts))}";
    }
    private static string PlanLabel(JsonObject p)
    {
        var f = Obj(p["food"]); var b = Obj(p["beverage"]); var recipe = Obj(f["recipe"]); var drink = Obj(b["beverage"]);
        var extras = Objects(f["extraIngredients"]).Select(x => Key(Num(x["id"]))).ToArray();
        var food = p["food"] == null ? "无料理" : $"{Str(recipe["name"])}#{Key(Num(recipe["id"]))}{(extras.Length > 0 ? "+" + string.Join(",", extras) : "")}";
        return $"{food} / {(p["beverage"] == null ? "无酒水" : $"{Str(drink["name"])}#{Key(Num(drink["id"]))}")}";
    }
    private static string PreferenceText(JsonObject e)
    { var tags = Strings(e["foodExtraPreferenceTags"]).Concat(Strings(e["beverageExtraPreferenceTags"])).ToArray(); return $"额外喜好 {Key(Num(e["extraPreferenceScore"]))}{(tags.Length > 0 ? $"（{string.Join("、", tags)}）" : "")}"; }
    private static string NegativeText(JsonObject e) => Arr(e["negativeTags"]).Count > 0 ? $"当前稀客厌恶 {string.Join("、", Strings(e["negativeTags"]))}" : "无当前稀客厌恶 Tag";
    public static double StoryScore(double level) => level >= 8 ? 4 : level >= 5 ? 3 : level >= 2 ? 2 : level > 0 ? 1 : 0;
    public static string EvaluationLabel(double score) => score >= 4 ? "完美（ExGood）" : score >= 3 ? "满意（Good）" : score >= 2 ? "普通（Normal）" : "未形成可推进评价";
}
