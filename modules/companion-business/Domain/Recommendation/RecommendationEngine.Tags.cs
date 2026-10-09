using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Recommendation;

/// <summary>纯数据推荐引擎；只读取传入快照，不访问游戏、线程、文件或网络。</summary>
public static partial class RecommendationEngine
{
    private static readonly string[][] VerifiedPriorityRules =
    {
        new[] { "肉", "素" }, new[] { "重油", "清淡" }, new[] { "饱腹", "下酒" },
        new[] { "大份", "小巧" }, new[] { "灼热", "凉爽" }
    };

    /// <summary>按价格与材料槽位派生动态标签；重复的基础材料仍各占一个槽位。</summary>
    public static JsonArray BuildDynamicFoodTags(JsonObject options)
    {
        var recipe = Obj(options["recipe"]);
        return Array(DynamicFoodTagValues(Strings(recipe["positiveTags"]), Num(recipe["price"]), Arr(recipe["ingredients"]).Count, Arr(options["extraIngredients"]).Count));
    }

    /// <summary>纯值版动态标签；公共 JSON 接口与搜索内部共享规则，避免热路径构造临时 JSON。</summary>
    private static string[] DynamicFoodTagValues(string[] recipeTags, double price, int baseCount, int extraCount)
    {
        var tags = new List<string>();
        if (!recipeTags.Contains("不可加价"))
        {
            if (price < 20) tags.Add("实惠");
            if (price > 60) tags.Add("昂贵");
        }
        if (baseCount + extraCount >= 5) tags.Add("大份");
        return tags.ToArray();
    }

    /// <summary>使用运行时规则；仅在规则为空时使用项目已验证的基础压制关系。</summary>
    private static IEnumerable<string[]> EffectiveRules(JsonArray? rules) => rules is { Count: > 0 }
        ? Objects(rules).Select(x => Strings(x["tags"])) : VerifiedPriorityRules;

    /// <summary>按规则顺序处理压制，返回顺序与原始标签首次出现顺序一致。</summary>
    public static JsonObject ResolveTagPriority(JsonArray rawTags, JsonArray? runtimeRules = null)
    {
        var resolved = ResolveTagPriorityValues(Strings(rawTags), EffectiveRules(runtimeRules));
        return Object(("activeTags", Array(resolved.Active)), ("suppressedTags", Array(resolved.Suppressed)));
    }

    /// <summary>以普通字符串值执行压制；搜索状态不持有或复制 JSON 子树。</summary>
    private static (string[] Active, string[] Suppressed) ResolveTagPriorityValues(IEnumerable<string> rawTags, IEnumerable<string[]> rules)
    {
        var unique = new List<string>();
        var active = new HashSet<string>();
        foreach (var raw in rawTags)
        {
            var tag = raw.Trim();
            if (tag.Length > 0 && active.Add(tag)) unique.Add(tag);
        }
        var suppressed = new HashSet<string>();
        foreach (var rule in rules)
        {
            string? strongest = null;
            foreach (var tag in rule)
            {
                if (!active.Contains(tag)) continue;
                if (strongest == null) strongest = tag;
                // 同一规则若重复列出最高优先级标签，仍应保留该标签；不能按数组位置跳过第一项。
                else if (tag != strongest) { active.Remove(tag); suppressed.Add(tag); }
            }
        }
        var activeValues = new List<string>(); var suppressedValues = new List<string>();
        foreach (var tag in unique)
        {
            if (active.Contains(tag)) activeValues.Add(tag);
            if (suppressed.Contains(tag)) suppressedValues.Add(tag);
        }
        return (activeValues.ToArray(), suppressedValues.ToArray());
    }

    /// <summary>解析基础、动态、加料和流行标签；流行标签在压制之后追加，保持原算法的操作顺序。</summary>
    public static JsonObject ResolveFoodTags(JsonObject options)
    {
        var recipe = Obj(options["recipe"]);
        var extras = Objects(options["extraIngredients"]).ToArray();
        var resolved = ResolveFoodTagValues(Strings(recipe["positiveTags"]), Num(recipe["price"]), Arr(recipe["ingredients"]).Count,
            extras.SelectMany(x => Strings(x["tags"])), extras.Length, EffectiveRules(Arr(options["tagPriorityRules"])),
            Bool(options["famousShopEnabled"]), Str(options["popularFoodTag"]), Str(options["popularHateFoodTag"]));
        return Object(("activeTags", Array(resolved.Active)), ("suppressedTags", Array(resolved.Suppressed)));
    }

    /// <summary>基础、动态、加料、压制、流行标签的顺序是协议语义，公共调用与 Beam Search 必须共用。</summary>
    private static (string[] Active, string[] Suppressed) ResolveFoodTagValues(string[] recipeTags, double price, int baseCount,
        IEnumerable<string> extraTags, int extraCount, IEnumerable<string[]> rules, bool famous, string popular, string hate)
    {
        return ResolvePreparedFoodTagValues(recipeTags.Concat(DynamicFoodTagValues(recipeTags, price, baseCount, extraCount)), extraTags, rules, famous, popular, hate);
    }

    /// <summary>搜索可预先缓存相同槽位数量的基础/动态标签；之后仍使用与公共接口一致的压制和流行规则。</summary>
    private static (string[] Active, string[] Suppressed) ResolvePreparedFoodTagValues(IEnumerable<string> baseTags,
        IEnumerable<string> extraTags, IEnumerable<string[]> rules, bool famous, string popular, string hate)
    {
        var resolved = ResolveTagPriorityValues(baseTags.Concat(extraTags), rules);
        if (!famous && popular.Length == 0 && hate.Length == 0) return resolved;
        var active = resolved.Active.ToList();
        void Add(string tag) { if (!active.Contains(tag)) active.Add(tag); }
        if (famous && active.Contains("招牌")) Add("流行喜爱");
        if (popular.Length > 0 && active.Contains(popular)) Add("流行喜爱");
        if (hate.Length > 0 && active.Contains(hate)) Add("流行厌恶");
        return (active.ToArray(), resolved.Suppressed);
    }

    /// <summary>查找可以压制当前厌恶标签的更高优先级标签，供有限宽度搜索构造材料池。</summary>
    public static JsonArray FindTagsThatCanSuppress(JsonArray activeTags, JsonArray tagsToSuppress, JsonArray? runtimeRules = null)
    {
        var active = Strings(activeTags).ToHashSet();
        var target = Strings(tagsToSuppress).ToHashSet();
        var result = new List<string>();
        foreach (var rule in EffectiveRules(runtimeRules))
            for (var index = 1; index < rule.Length; index++)
                if (active.Contains(rule[index]) && target.Contains(rule[index])) result.AddRange(rule.Take(index));
        return Array(result.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct());
    }

    /// <summary>配方禁忌判断在标签压制之前进行，不能靠加另一种材料掩盖禁忌材料。</summary>
    public static bool HasForbiddenIngredientTag(JsonObject ingredient, JsonObject recipe) =>
        Strings(ingredient["tags"]).Any(Strings(recipe["negativeTags"]).Contains);

    /// <summary>任务信号只在精确订单身份及代际均有效时参与排序。</summary>
    public static JsonObject? GetVerifiedMissionRecipeSortContext(JsonObject order)
    {
        if (order["missionRecipePriority"] is not JsonObject priority
            || !Integer(priority["foodId"]) || Num(priority["foodId"]) < 0
            || !Integer(priority["recipeId"]) || Num(priority["recipeId"]) < 0
            || !SafeInteger(priority["missionGeneration"]) || Num(priority["missionGeneration"]) < 1
            || !SafeInteger(priority["businessGeneration"]) || Num(priority["businessGeneration"]) < 1
            || Str(order["traceId"]).Length == 0 || Str(priority["traceId"]) != Str(order["traceId"])) return null;
        foreach (var key in new[] { "deskCode", "guestId", "runtimeGuestId" })
            if (NullableNumber(priority[key]) != NullableNumber(order[key])) return null;
        return Object(("missionRecipeFoodId", priority["foodId"]), ("missionRecipeId", priority["recipeId"]));
    }

    /// <summary>使用食物 ID 与配方 ID 双重标识匹配任务料理。</summary>
    public static bool IsMissionRecipeFoodCandidate(JsonObject food, JsonObject sortContext) =>
        sortContext["missionRecipeFoodId"] is not null && sortContext["missionRecipeId"] is not null
        && Num(Obj(food["recipe"])["id"]) == Num(sortContext["missionRecipeFoodId"])
        && Num(Obj(food["recipe"])["recipeId"]) == Num(sortContext["missionRecipeId"]);

    /// <summary>任务优先不绕过任意硬失败，并继续要求酒水满足点单。</summary>
    public static bool IsMissionRecipeExecutionPlan(JsonObject plan, JsonObject sortContext) =>
        Str(plan["bucket"]) != "blocked" && plan["food"] is JsonObject food && plan["beverage"] is JsonObject beverage
        && IsMissionRecipeFoodCandidate(food, sortContext) && !HasHardFailures(Arr(food["conditionResults"]))
        && !HasHardFailures(Arr(beverage["conditionResults"])) && !HasHardFailures(Arr(plan["conditionResults"]))
        && Bool(beverage["meetsRequiredBeverage"]);

    private static bool HasHardFailures(JsonArray conditions) => Objects(conditions).Any(x => Str(x["status"]) == "fail" && Str(x["severity"]) == "hard");
    private static JsonObject Condition(string id, string target, string status, string severity, string label, string detail) =>
        Object(("id", id), ("target", target), ("status", status), ("severity", severity), ("label", label), ("detail", detail));
    private static double InventoryRank(double quantity) => quantity == -1 ? 9007199254740991d : quantity;
    private static double InventoryShortage(double quantity, double threshold) => quantity == -1 ? 0 : Math.Max(0, threshold - quantity);
}
