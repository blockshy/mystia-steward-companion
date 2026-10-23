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

    /// <summary>
    /// 标签解析的独占工作区。一次搜索会评估大量中间状态，但每次评估只需要临时去重集合；
    /// 清空集合并复用容量可避免每个候选都创建五组集合及其扩容数组。工作区由一次调用持有，
    /// 不是静态缓存，不跨线程共享，也不保留任何游戏对象或输入 JSON 节点。
    /// </summary>
    private sealed class FoodTagWorkspace
    {
        private readonly List<string> _unique = new(16);
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);
        private readonly HashSet<string> _suppressed = new(StringComparer.Ordinal);
        private readonly List<string> _activeValues = new(16);
        private readonly List<string> _suppressedValues = new(8);

        internal void Reset()
        {
            _unique.Clear(); _active.Clear(); _suppressed.Clear();
            _activeValues.Clear(); _suppressedValues.Clear();
        }

        internal void AddRaw(string raw)
        {
            var tag = raw.Trim();
            if (tag.Length > 0 && _active.Add(tag)) _unique.Add(tag);
        }

        internal void ApplyRules(string[][] rules)
        {
            foreach (var rule in rules)
            {
                string? strongest = null;
                foreach (var tag in rule)
                {
                    if (!_active.Contains(tag)) continue;
                    if (strongest == null) strongest = tag;
                    // 只对原始输入标签去空白；优先级规则仍按原文精确匹配。重复的最高项
                    // 不压制自己，后续规则读取前一条规则的结果，不能合并为无序互斥集合。
                    else if (tag != strongest) { _active.Remove(tag); _suppressed.Add(tag); }
                }
            }
        }

        internal bool Contains(string tag) => _active.Contains(tag);

        /// <summary>
        /// 状态持有独立数组，绝不暴露可复用集合的内部缓冲区。流行标签先在压制后的
        /// 顺序列表末尾追加：若该标签此前被压制，仍需保留 suppressed 并在 active 末尾
        /// 重新出现，不能把它放回原始位置或以最终 active 集合推导 suppressed。
        /// </summary>
        internal (string[] Active, string[] Suppressed) CopyResult(bool famous = false, string popular = "", string hate = "")
        {
            foreach (var tag in _unique)
            {
                if (_active.Contains(tag)) _activeValues.Add(tag);
                if (_suppressed.Contains(tag)) _suppressedValues.Add(tag);
            }
            void Append(string tag) { if (_active.Add(tag)) _activeValues.Add(tag); }
            if (famous && _active.Contains("招牌")) Append("流行喜爱");
            if (popular.Length > 0 && _active.Contains(popular)) Append("流行喜爱");
            if (hate.Length > 0 && _active.Contains(hate)) Append("流行厌恶");
            return (_activeValues.ToArray(), _suppressedValues.ToArray());
        }

        /// <summary>双目标代表选择仅查询追加标签的可达性，不创建无人使用的结果数组。</summary>
        internal bool CanAppend(string[] active, string tag, string[][] rules)
        {
            Reset();
            foreach (var value in active) AddRaw(value);
            AddRaw(tag); ApplyRules(rules);
            return Contains(tag);
        }
    }

    /// <summary>公共纯值入口也使用同一规则核心；独立调用自行持有工作区。</summary>
    private static (string[] Active, string[] Suppressed) ResolveTagPriorityValues(IEnumerable<string> rawTags, IEnumerable<string[]> rules)
    {
        var workspace = new FoodTagWorkspace();
        foreach (var raw in rawTags) workspace.AddRaw(raw);
        workspace.ApplyRules(rules as string[][] ?? rules.ToArray());
        return workspace.CopyResult();
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
        var workspace = new FoodTagWorkspace();
        foreach (var tag in baseTags) workspace.AddRaw(tag);
        foreach (var tag in extraTags) workspace.AddRaw(tag);
        workspace.ApplyRules(rules as string[][] ?? rules.ToArray());
        return workspace.CopyResult(famous, popular, hate);
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
