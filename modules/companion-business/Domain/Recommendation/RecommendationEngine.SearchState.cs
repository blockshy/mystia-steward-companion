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
        internal readonly string[] Positive;
        internal readonly string[] Negative;
        internal readonly string[] MatchedSpecial;
        internal readonly bool MeetsRequired;
        internal readonly bool MeetsSpecial;
        internal readonly double Cost;
        internal readonly double Pressure;
        internal IngredientSearchState(SearchRecipe recipe, SearchIngredient[] ingredients, SearchDemand demand, string? key = null)
        {
            Ingredients = ingredients; Key = key ?? SearchStateKey(ingredients);
            var tags = ResolvePreparedFoodTagValues(recipe.PreparedTags[ingredients.Length], ingredients.SelectMany(x => x.Tags),
                demand.Rules, demand.Famous, demand.Popular, demand.Hate);
            Active = tags.Active; Suppressed = tags.Suppressed;
            Positive = Active.Where(demand.Positive.Contains).ToArray(); Negative = Active.Where(demand.Negative.Contains).ToArray();
            MatchedSpecial = demand.SpecialTags.Where(Active.Contains).ToArray(); MeetsRequired = Active.Contains(demand.Required);
            MeetsSpecial = demand.SpecialNone || demand.SpecialTags.Length > 0 && (demand.SpecialAll ? MatchedSpecial.Length == demand.SpecialTags.Length : MatchedSpecial.Length > 0);
            Cost = ingredients.Sum(x => x.Price); Pressure = ingredients.Sum(x => x.Pressure);
        }

        /// <summary>只为最终保留的候选构造协议值树，沿用已有条件/解释生成器。</summary>
        internal JsonObject ToJson() => Object(("ingredients", Array(Ingredients.Select(x => x.Source))),
            ("activeTags", Array(Active)), ("suppressedTags", Array(Suppressed)), ("matchedPositiveTags", Array(Positive)),
            ("matchedNegativeTags", Array(Negative)), ("matchedSpecialFoodTargetTags", Array(MatchedSpecial)),
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
        difference = left.Negative.Length.CompareTo(right.Negative.Length); if (difference != 0) return difference;
        difference = right.Positive.Length.CompareTo(left.Positive.Length); if (difference != 0) return difference;
        difference = left.Ingredients.Length.CompareTo(right.Ingredients.Length); if (difference != 0) return difference;
        difference = left.Pressure.CompareTo(right.Pressure); if (difference != 0) return difference;
        difference = left.Cost.CompareTo(right.Cost); if (difference != 0) return difference;
        return CultureInfo.GetCultureInfo("en-US").CompareInfo.Compare(left.Key, right.Key, CompareOptions.None);
    }

    /// <summary>保留首个插入位置与最后一个排列值，严格对应 Map.set 去重和稳定 Array.sort。</summary>
    private static IngredientSearchState[] BestSearchStates(IEnumerable<IngredientSearchState> states, int limit, bool preserve, SearchDemand demand)
    {
        var unique = new Dictionary<string, IngredientSearchState>();
        foreach (var state in states) unique[state.Key] = state;
        var comparer = Comparer<IngredientSearchState>.Create(CompareSearchStates);
        var sorted = unique.Values.OrderBy(x => x, comparer).ToArray();
        if (!preserve) return sorted.Take(limit).ToArray();
        var representatives = new Dictionary<string, IngredientSearchState>();
        foreach (var state in sorted)
        {
            var signature = string.Join("|", demand.SpecialTags.Select(tag => state.Active.Contains(tag) ? "matched"
                : ResolveTagPriorityValues(state.Active.Append(tag), demand.Rules).Active.Contains(tag) ? "reachable" : "blocked"));
            if (!representatives.ContainsKey(signature)) representatives[signature] = state;
        }
        var selected = representatives.Values.OrderBy(x => x, comparer).Take(limit).ToList();
        var keys = selected.Select(x => x.Key).ToHashSet();
        foreach (var state in sorted) { if (selected.Count >= limit) break; if (keys.Add(state.Key)) selected.Add(state); }
        return selected.ToArray();
    }
}
