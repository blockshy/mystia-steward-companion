using System.Globalization;
using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Recommendation;

public static partial class RecommendationEngine
{
    /// <summary>
    /// 加料搜索的只读材料投影。Source 只在最终候选序列化时复制；扩展数十万中间状态时只传递
    /// 托管值与引用，不创建 JsonNode、不解析文本，也不改变输入节点的 parent 或内容。
    /// </summary>
    private sealed class SearchIngredient
    {
        internal readonly JsonObject Source;
        internal readonly double Id;
        internal readonly string IdText;
        internal readonly string[] Tags;
        internal readonly double Price;
        internal readonly double Pressure;
        internal SearchIngredient(JsonObject source, JsonObject quantities)
        {
            Source = source; Id = Num(source["id"]); IdText = Key(Id); Tags = Strings(source["tags"]); Price = Num(source["price"]);
            Pressure = InventoryShortage(Num(quantities[Key(Id)]), 5);
        }
    }

    /// <summary>一次搜索共享的顾客/目标/标签上下文，避免每个状态重复解析同一 JSON 数组。</summary>
    private sealed class SearchDemand
    {
        internal readonly HashSet<string> Positive;
        internal readonly HashSet<string> Negative;
        internal readonly string Required;
        internal readonly string[] SpecialTags;
        internal readonly bool SpecialNone;
        internal readonly bool SpecialAll;
        internal readonly string[][] Rules;
        internal readonly bool Famous;
        internal readonly string Popular;
        internal readonly string Hate;
        // 该投影仅在一次同步搜索调用内顺序使用。候选状态只接收 CopyResult 的独立数组；
        // 后续状态或双目标可达性查询清空工作区，均不会修改已有候选。
        internal readonly FoodTagWorkspace TagWorkspace = new();
        internal SearchDemand(JsonObject demand, JsonObject context)
        {
            var customer = Obj(demand["customer"]); var special = SpecialFoodTarget(demand);
            Positive = Strings(customer["positiveTags"]).ToHashSet(); Negative = Strings(customer["negativeTags"]).ToHashSet();
            Required = Str(demand["requiredFoodTag"]); SpecialTags = Strings(special["tags"]);
            SpecialNone = Str(special["enforcement"]) == "none"; SpecialAll = Str(special["match"]) == "all";
            Rules = EffectiveRules(Arr(context["tagPriorityRules"])).ToArray(); Famous = Bool(context["famousShopEnabled"]);
            Popular = Str(context["popularFoodTag"]); Hate = Str(context["popularHateFoodTag"]);
        }
    }

    /// <summary>同一配方的固定属性只读取一次；基础材料数量保留重复项的槽位语义。</summary>
    private sealed class SearchRecipe
    {
        internal readonly string[] Tags;
        internal readonly int BaseCount;
        internal readonly double Price;
        internal readonly string[][] PreparedTags;
        internal SearchRecipe(JsonObject recipe)
        {
            Tags = Strings(recipe["positiveTags"]); BaseCount = Arr(recipe["ingredients"]).Count; Price = Num(recipe["price"]);
            PreparedTags = Enumerable.Range(0, 6).Select(count => Tags.Concat(DynamicFoodTagValues(Tags, Price, BaseCount, count)).ToArray()).ToArray();
        }
    }

    /// <summary>
    /// Beam Search 的不可变中间值。Key 缓存已排序 ID，排序比较不再反复遍历 JSON/排序 ID。
    /// Ingredients 的实际顺序仍原样保留，因为它决定最终 Tag 的首次出现顺序。
    /// </summary>
    private sealed class IngredientSearchState
    {
        internal readonly SearchIngredient[] Ingredients;
        internal readonly string Key;
        internal readonly string[] Active;
        internal readonly string[] Suppressed;
        // 搜索比较只读取命中数量，绝大多数中间状态最终会被beam淘汰，不必为它们
        // 预分配三份匹配数组。共享需求投影在本次搜索中只读；状态不修改其集合，
        // 最终序列化才根据相同Active/需求顺序生成完整字段，不缓存或复用JSON子节点。
        private readonly SearchDemand _demand;
        internal readonly int PositiveCount;
        internal readonly int NegativeCount;
        internal readonly int MatchedSpecialCount;
        internal readonly bool MeetsRequired;
        internal readonly bool MeetsSpecial;
        internal readonly double Cost;
        internal readonly double Pressure;
        internal IngredientSearchState(SearchRecipe recipe, SearchIngredient[] ingredients, SearchDemand demand, string? key = null)
        {
            Ingredients = ingredients; Key = key ?? SearchStateKey(ingredients); _demand = demand;
            var workspace = demand.TagWorkspace;
            workspace.Reset();
            foreach (var tag in recipe.PreparedTags[ingredients.Length]) workspace.AddRaw(tag);
            foreach (var ingredient in ingredients)
                foreach (var tag in ingredient.Tags) workspace.AddRaw(tag);
            workspace.ApplyRules(demand.Rules);
            var tags = workspace.CopyResult(demand.Famous, demand.Popular, demand.Hate);
            Active = tags.Active; Suppressed = tags.Suppressed;
            foreach (var tag in Active)
            {
                if (demand.Positive.Contains(tag)) PositiveCount++;
                if (demand.Negative.Contains(tag)) NegativeCount++;
            }
            foreach (var tag in demand.SpecialTags) if (System.Array.IndexOf(Active, tag) >= 0) MatchedSpecialCount++;
            MeetsRequired = System.Array.IndexOf(Active, demand.Required) >= 0;
            MeetsSpecial = demand.SpecialNone || demand.SpecialTags.Length > 0 && (demand.SpecialAll ? MatchedSpecialCount == demand.SpecialTags.Length : MatchedSpecialCount > 0);
            // 保留材料原顺序的逐项求和；不得排序后累加而改变浮点舍入或最终标签顺序。
            foreach (var ingredient in ingredients) { Cost += ingredient.Price; Pressure += ingredient.Pressure; }
        }

        /// <summary>
        /// 只为最终保留的候选构造协议值树，沿用已有条件/解释生成器。喜好和厌恶
        /// 按Active首次出现顺序输出；特殊目标按需求原顺序输出，不能枚举HashSet
        /// 或排序/去重来省分配，否则会改变完整JSON中的标签顺序和重复目标语义。
        /// </summary>
        internal JsonObject ToJson() => Object(("ingredients", Array(Ingredients.Select(x => x.Source))),
            ("activeTags", Array(Active)), ("suppressedTags", Array(Suppressed)), ("matchedPositiveTags", Array(Active.Where(_demand.Positive.Contains))),
            ("matchedNegativeTags", Array(Active.Where(_demand.Negative.Contains))), ("matchedSpecialFoodTargetTags", Array(_demand.SpecialTags.Where(Active.Contains))),
            ("meetsSpecialFoodTarget", MeetsSpecial), ("meetsRequiredFood", MeetsRequired), ("extraCost", Cost), ("resourcePressure", Pressure));
    }

    private static string SearchStateKey(IEnumerable<SearchIngredient> ingredients)
    {
        var sorted = ingredients.ToArray();
        System.Array.Sort(sorted, (left, right) => left.Id.CompareTo(right.Id));
        return string.Join(",", sorted.Select(x => x.IdText));
    }

    /// <summary>排序优先级与 TS 保持一致；最后按数字 ID 文本的 en-US 排序稳定区分相同评分。</summary>
    private static int CompareSearchStates(IngredientSearchState left, IngredientSearchState right)
    {
        // 比较器必须短路：大多数状态在偏好/数量/库存字段已分胜负，无需进行区域文本比较和 params 分配。
        var difference = right.MeetsSpecial.CompareTo(left.MeetsSpecial); if (difference != 0) return difference;
        difference = right.MeetsRequired.CompareTo(left.MeetsRequired); if (difference != 0) return difference;
        difference = left.NegativeCount.CompareTo(right.NegativeCount); if (difference != 0) return difference;
        difference = right.PositiveCount.CompareTo(left.PositiveCount); if (difference != 0) return difference;
        difference = left.Ingredients.Length.CompareTo(right.Ingredients.Length); if (difference != 0) return difference;
        difference = left.Pressure.CompareTo(right.Pressure); if (difference != 0) return difference;
        difference = left.Cost.CompareTo(right.Cost); if (difference != 0) return difference;
        return CultureInfo.GetCultureInfo("en-US").CompareInfo.Compare(left.Key, right.Key, CompareOptions.None);
    }

    /// <summary>
    /// 仅接收调用方字典已按Key去重的状态：expanded和states已保留首个插入位置、
    /// 最后一个排列值，严格对应Map.set；此处不再重复分配同内容字典。OrderBy仍
    /// 保持并列项的原枚举顺序，不能改成不稳定排序或提前截断；双目标代表选择不变。
    /// </summary>
    private static IngredientSearchState[] BestUniqueSearchStates(IEnumerable<IngredientSearchState> states, int limit, bool preserve, SearchDemand demand)
    {
        var comparer = SearchStateComparer;
        var sorted = states.OrderBy(x => x, comparer).ToArray();
        if (!preserve) return sorted.Length <= limit ? sorted : sorted.Take(limit).ToArray();
        IEnumerable<IngredientSearchState> representativeValues;
        if (demand.SpecialTags.Length == 2)
        {
            // 两目标的 matched/reachable/blocked 各是三个互斥状态，三进制键完整表达原
            // 字符串签名的九种可能。仅在恰好两目标时使用，不缩减候选或改变代表选取次序。
            var representatives = new Dictionary<int, IngredientSearchState>();
            foreach (var state in sorted)
            {
                var signature = SearchTargetState(state, demand.SpecialTags[0], demand) * 3
                    + SearchTargetState(state, demand.SpecialTags[1], demand);
                representatives.TryAdd(signature, state);
            }
            representativeValues = representatives.Values;
        }
        else
        {
            // 保留通用路径，避免未来调用方增加目标数量时发生签名碰撞。
            var representatives = new Dictionary<string, IngredientSearchState>();
            foreach (var state in sorted)
            {
                var signature = string.Join("|", demand.SpecialTags.Select(tag => SearchTargetState(state, tag, demand) switch
                { 0 => "matched", 1 => "reachable", _ => "blocked" }));
                representatives.TryAdd(signature, state);
            }
            representativeValues = representatives.Values;
        }
        var selected = representativeValues.OrderBy(x => x, comparer).Take(limit).ToList();
        var keys = selected.Select(x => x.Key).ToHashSet();
        foreach (var state in sorted) { if (selected.Count >= limit) break; if (keys.Add(state.Key)) selected.Add(state); }
        return selected.ToArray();
    }

    private static readonly IComparer<IngredientSearchState> SearchStateComparer = Comparer<IngredientSearchState>.Create(CompareSearchStates);

    private static int SearchTargetState(IngredientSearchState state, string tag, SearchDemand demand) =>
        System.Array.IndexOf(state.Active, tag) >= 0 ? 0 : demand.TagWorkspace.CanAppend(state.Active, tag, demand.Rules) ? 1 : 2;
}
