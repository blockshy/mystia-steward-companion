using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Recommendation;

public static partial class RecommendationEngine
{
    /// <summary>
    /// 生成稀客料理候选。严格保留 main 基线的材料资格语义：资格由 available/disabled/excluded 集合决定；
    /// ownedIngredientQty 仅在当前算法中参与压力排序，不在移植时擅自增加与旧版不同的数量门槛。
    /// </summary>
    public static JsonArray BuildRareFoodCandidates(JsonObject data, JsonObject demand, JsonObject context, JsonObject? options = null)
    {
        options ??= new JsonObject();
        var ingredients = Objects(data["ingredients"]).ToArray();
        var byName = ingredients.GroupBy(x => Str(x["name"])).ToDictionary(x => x.Key, x => x.Last());
        var byId = ingredients.GroupBy(x => Num(x["id"])).ToDictionary(x => x.Key, x => x.Last());
        var forbiddenIds = ValidConstraintIds(Arr(options["forbiddenExtraIngredientIds"]));
        var requiredIds = ValidConstraintIds(Arr(options["requiredExtraIngredientIds"]));
        if (forbiddenIds is null || requiredIds is null) return new JsonArray();
        if (requiredIds.Any(id => !byId.ContainsKey(id) || !Has(context, "availableIngredientIds", id) || IngredientExcluded(id, context) || forbiddenIds.Contains(id))) return new JsonArray();
        var required = requiredIds.Select(id => byId[id]).ToArray();
        var usable = Numbers(context["availableIngredientIds"]).Where(id => !IngredientExcluded(id, context) && !forbiddenIds.Contains(id) && byId.ContainsKey(id)).Select(id => byId[id]).ToArray();
        var searchDemand = new SearchDemand(demand, context);
        var quantities = Obj(context["ownedIngredientQty"]);
        var projectedIngredients = byId.ToDictionary(x => x.Key, x => new SearchIngredient(x.Value, quantities));
        var requiredSearch = required.Select(x => projectedIngredients[Num(x["id"])]).ToArray();
        var candidates = new List<JsonObject>();
        foreach (var recipe in Objects(data["recipes"]))
        {
            if (!Has(context, "availableRecipeIds", Num(recipe["id"])) || !BaseIngredientsAvailable(recipe, byName, context)
                || Bool(context["filterMissingCookers"]) && !CookerAvailable(recipe, context)) continue;
            var slots = ExtraSlots(recipe, context);
            var baseIds = Strings(recipe["ingredients"]).Where(byName.ContainsKey).Select(x => Num(byName[x]["id"])).Where(x => x >= 0).ToHashSet();
            if (required.Length > slots || required.Any(x => baseIds.Contains(Num(x["id"])) || HasForbiddenIngredientTag(x, recipe))) continue;
            var searchRecipe = new SearchRecipe(recipe);
            var initial = new IngredientSearchState(searchRecipe, requiredSearch, searchDemand);
            var reserved = baseIds.Union(required.Select(x => Num(x["id"]))).ToHashSet();
            var customer = Obj(demand["customer"]);
            var useful = new[] { Str(demand["requiredFoodTag"]) }.Concat(Strings(customer["positiveTags"]))
                .Concat(Strings(SpecialFoodTarget(demand)["tags"]))
                .Concat(Strings(FindTagsThatCanSuppress(Array(initial.Active), Arr(customer["negativeTags"]), Arr(context["tagPriorityRules"])))).ToHashSet();
            var pool = usable.Where(x => !reserved.Contains(Num(x["id"])) && !HasForbiddenIngredientTag(x, recipe) && Strings(x["tags"]).Any(useful.Contains))
                .OrderBy(x => Num(x["id"])).Select(x => projectedIngredients[Num(x["id"])]).ToArray();
            var preserve = Bool(options["preserveTwoTagSpecialTargetReachability"]) && Arr(SpecialFoodTarget(demand)["tags"]).Count == 2;
            var states = new Dictionary<string, IngredientSearchState> { [initial.Key] = initial };
            var frontier = new[] { initial };
            for (var depth = 1; depth <= slots - required.Length; depth++)
            {
                // 同一组材料的排列只在去重后计算一次。保存最后排列且保留首插位置，与旧版先评估再 Map.set 完全同义。
                var expanded = new Dictionary<string, SearchIngredient[]>();
                foreach (var state in frontier)
                {
                    var used = state.Ingredients.Select(x => x.Id).ToHashSet();
                    foreach (var ingredient in pool.Where(x => !used.Contains(x.Id)))
                    {
                        var extras = state.Ingredients.Append(ingredient).ToArray();
                        expanded[SearchStateKey(extras)] = extras;
                    }
                }
                if (expanded.Count == 0) break;
                frontier = BestSearchStates(expanded.Select(x => new IngredientSearchState(searchRecipe, x.Value, searchDemand, x.Key)), 64, preserve, searchDemand);
                foreach (var state in frontier) states[state.Key] = state;
            }
            candidates.AddRange(BestSearchStates(states.Values, 16, preserve, searchDemand).Select(state => FoodCandidate(recipe, state.ToJson(), demand, context, byName)));
        }
        return Sorted(candidates, CompareFoodCandidates);
    }

    /// <summary>计算稀客酒水候选，库存 -1 按无限库存排序，不更改原始快照值。</summary>
    public static JsonArray BuildRareBeverageCandidates(JsonObject data, JsonObject demand, JsonObject context)
    {
        var rows = new List<JsonObject>();
        foreach (var beverage in Objects(data["beverages"]))
        {
            var id = Num(beverage["id"]);
            if (!Has(context, "availableBeverageIds", id) || Has(context, "excludedBeverageIds", id)) continue;
            var tags = ResolveTagPriority(Arr(beverage["tags"]), Arr(context["tagPriorityRules"]));
            var active = Strings(tags["activeTags"]);
            var matched = active.Where(Strings(Obj(demand["customer"])["beverageTags"]).Contains).ToArray();
            var meets = active.Contains(Str(demand["requiredBeverageTag"]));
            var conditions = new JsonArray(Condition("beverage.required-tag", "beverage", meets ? "pass" : "warn", meets ? "hard" : "soft", "酒水点单", $"{(meets ? "满足" : "未满足")}点单酒水 {Str(demand["requiredBeverageTag"])}"));
            if (matched.Length > 0) conditions.Add(Condition("beverage.preference", "beverage", "boost", "soft", "酒水偏好", $"命中 {string.Join("、", matched)}"));
            rows.Add(Object(("beverage", beverage), ("activeTags", tags["activeTags"]), ("matchedTags", Array(matched)),
                ("meetsRequiredBeverage", meets), ("ownedQuantity", Num(Obj(context["ownedBeverageQty"])[Key(id)])), ("conditionResults", conditions)));
        }
        return Sorted(rows, CompareBeverageCandidates);
    }

    /// <summary>轻量诊断只统计资格，不为诊断重新执行组合搜索。</summary>
    public static JsonObject DiagnoseRareFoodCandidateSearch(JsonObject data, JsonObject demand, JsonObject context, JsonArray generatedCandidates)
    {
        var ingredients = Objects(data["ingredients"]).ToArray();
        var byName = ingredients.GroupBy(x => Str(x["name"])).ToDictionary(x => x.Key, x => x.Last());
        var missingIngredients = new HashSet<string>(); var missingCookers = new HashSet<string>();
        var reachable = 0; var unlocked = 0; var baseReady = 0; var cookerReady = 0;
        foreach (var recipe in Objects(data["recipes"]))
        {
            if (!CanReachRequiredTag(recipe, ingredients, byName, demand, context)) continue;
            reachable++;
            if (!Has(context, "availableRecipeIds", Num(recipe["id"]))) continue;
            unlocked++;
            if (!BaseIngredientsAvailable(recipe, byName, context))
            {
                foreach (var name in Strings(recipe["ingredients"]).Where(x => !byName.TryGetValue(x, out var item) || !Has(context, "availableIngredientIds", Num(item["id"])) || IngredientExcluded(Num(item["id"]), context))) missingIngredients.Add(name);
                continue;
            }
            baseReady++;
            if (!CookerAvailable(recipe, context)) { if (Str(recipe["cooker"]).Trim().Length > 0) missingCookers.Add(Str(recipe["cooker"]).Trim()); continue; }
            cookerReady++;
        }
        return Object(("catalogRecipeCount", Arr(data["recipes"]).Count), ("requiredTagReachableRecipeCount", reachable), ("requiredTagReachableUnlockedRecipeCount", unlocked),
            ("requiredTagReachableBaseIngredientsReadyRecipeCount", baseReady), ("requiredTagReachableCookerReadyRecipeCount", cookerReady), ("generatedCandidateCount", generatedCandidates.Count),
            ("generatedRequiredTagMatchedCandidateCount", Objects(generatedCandidates).Count(x => Bool(x["meetsRequiredFood"]))),
            ("missingIngredientNames", Array(missingIngredients.OrderBy(x => x, StringComparer.Ordinal))), ("missingCookerNames", Array(missingCookers.OrderBy(x => x, StringComparer.Ordinal))));
    }

    /// <summary>酒水诊断与正式候选使用相同可用、排除及点单标签规则。</summary>
    public static JsonObject DiagnoseRareBeverageCandidateSearch(JsonObject data, JsonObject demand, JsonObject context)
    {
        var available = 0; var allowed = 0; var matched = 0;
        foreach (var beverage in Objects(data["beverages"]))
        {
            if (!Has(context, "availableBeverageIds", Num(beverage["id"]))) continue;
            available++;
            if (Has(context, "excludedBeverageIds", Num(beverage["id"]))) continue;
            allowed++;
            if (Strings(ResolveTagPriority(Arr(beverage["tags"]), Arr(context["tagPriorityRules"]))["activeTags"]).Contains(Str(demand["requiredBeverageTag"]))) matched++;
        }
        return Object(("catalogBeverageCount", Arr(data["beverages"]).Count), ("availableBeverageCount", available), ("allowedBeverageCount", allowed), ("requiredTagBeverageCount", matched));
    }

    private static bool CanReachRequiredTag(JsonObject recipe, JsonObject[] ingredients, Dictionary<string, JsonObject> byName, JsonObject demand, JsonObject context)
    {
        if (Bool(EvaluateIngredientState(recipe, System.Array.Empty<JsonObject>(), demand, context)["meetsRequiredFood"])) return true;
        var slots = ExtraSlots(recipe, context); if (slots <= 0) return false;
        var baseIds = Strings(recipe["ingredients"]).Where(byName.ContainsKey).Select(x => Num(byName[x]["id"])).Where(x => x >= 0).ToHashSet();
        var allowed = ingredients.Where(x => !baseIds.Contains(Num(x["id"])) && !HasForbiddenIngredientTag(x, recipe)).ToArray();
        if (allowed.Any(x => Bool(EvaluateIngredientState(recipe, new[] { x }, demand, context)["meetsRequiredFood"]))) return true;
        var count = 5 - Arr(recipe["ingredients"]).Count;
        return Str(demand["requiredFoodTag"]) == "大份" && count > 1 && count <= slots && allowed.Length >= count
            && Bool(EvaluateIngredientState(recipe, allowed.Take(count), demand, context)["meetsRequiredFood"]);
    }
    private static HashSet<double>? ValidConstraintIds(JsonArray ids)
    {
        if (ids.Any(x => !Integer(x) || Num(x) < 0)) return null;
        var set = Numbers(ids).ToHashSet(); return set.Count == ids.Count ? set : null;
    }
    private static bool Has(JsonObject context, string key, double id) => Numbers(context[key]).Contains(id);
    private static bool IngredientExcluded(double id, JsonObject context) => Has(context, "disabledIngredientIds", id) || Has(context, "excludedIngredientIds", id);
    private static bool BaseIngredientsAvailable(JsonObject recipe, Dictionary<string, JsonObject> byName, JsonObject context) =>
        Strings(recipe["ingredients"]).All(x => byName.TryGetValue(x, out var item) && Has(context, "availableIngredientIds", Num(item["id"])) && !IngredientExcluded(Num(item["id"]), context));
    private static bool CookerAvailable(JsonObject recipe, JsonObject context) => !Bool(context["hasCookerSnapshot"]) || Strings(context["placedCookerNames"]).Contains(Str(recipe["cooker"]));
    private static double ExtraSlots(JsonObject recipe, JsonObject context) => Math.Max(0, Math.Min(5 - Arr(recipe["ingredients"]).Count, Num(context["maxExtraIngredients"])));
    private static JsonObject SpecialFoodTarget(JsonObject demand)
    {
        var target = Obj(demand["specialFoodTarget"]);
        return Object(("enforcement", Str(target["enforcement"], "none")), ("match", Str(target["match"], "any")),
            ("tags", Array(Strings(target["tags"]).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct())));
    }
    /// <summary>轻量诊断复用搜索的纯值评估器，不另存一套标签/偏好规则。</summary>
    private static JsonObject EvaluateIngredientState(JsonObject recipe, IEnumerable<JsonObject> extras, JsonObject demand, JsonObject context)
    {
        var quantities = Obj(context["ownedIngredientQty"]);
        return new IngredientSearchState(new SearchRecipe(recipe), extras.Select(x => new SearchIngredient(x, quantities)).ToArray(),
            new SearchDemand(demand, context)).ToJson();
    }

    private static JsonObject FoodCandidate(JsonObject recipe, JsonObject state, JsonObject demand, JsonObject context, Dictionary<string, JsonObject> byName)
    {
        var available = CookerAvailable(recipe, context); var meets = Bool(state["meetsRequiredFood"]); var special = SpecialFoodTarget(demand);
        var conditions = new JsonArray(Condition("food.required-tag", "food", meets ? "pass" : "warn", meets ? "hard" : "soft", "料理点单", $"{(meets ? "满足" : "未满足")}点单料理 {Str(demand["requiredFoodTag"])}"));
        if (Str(special["enforcement"]) != "none")
        {
            var all = Str(special["match"]) == "all"; var matched = Strings(state["matchedSpecialFoodTargetTags"]); var target = Strings(special["tags"]); var ok = Bool(state["meetsSpecialFoodTarget"]);
            var detail = ok ? $"{(all ? "同时满足" : "满足")}特殊目标 Tag {string.Join("、", matched)}" : target.Length == 0 ? "特殊目标 Tag 尚未完整读取" : $"{(all ? "未同时满足" : "未满足")}特殊目标 Tag {string.Join("、", target.Where(x => !matched.Contains(x)))}";
            conditions.Add(Condition("food.special-target-tag", "food", ok ? "pass" : "fail", "hard", "特殊目标 Tag", detail));
        }
        if (!available) conditions.Add(Condition("food.cooker", "food", "fail", "hard", "厨具", $"缺少厨具 {(Str(recipe["cooker"]).Length > 0 ? Str(recipe["cooker"]) : "未知")}"));
        foreach (var (field, id, status, severity, label, prefix) in new[] { ("matchedPositiveTags", "food.preference", "boost", "soft", "料理偏好", "命中"), ("matchedNegativeTags", "food.negative-tags", "warn", "soft", "厌恶标签", "包含"), ("suppressedTags", "food.suppressed-tags", "info", "info", "标签优先级", "压制") })
            if (Arr(state[field]).Count > 0) conditions.Add(Condition(id, "food", status, severity, label, $"{prefix} {string.Join("、", Strings(state[field]))}"));
        var reasons = new JsonObject(); var relevant = new[] { Str(demand["requiredFoodTag"]) }.Concat(Strings(Obj(demand["customer"])["positiveTags"])).Concat(Strings(special["tags"])).ToHashSet();
        foreach (var ingredient in Objects(state["ingredients"]))
        {
            var tags = Strings(ingredient["tags"]).Where(relevant.Contains).ToArray(); if (tags.Length > 0) reasons[Key(Num(ingredient["id"]))] = Array(tags);
        }
        var result = Object(("recipe", recipe), ("extraIngredients", state["ingredients"]), ("extraIngredientReasonTags", reasons), ("baseCost", Strings(recipe["ingredients"]).Sum(x => byName.TryGetValue(x, out var item) ? Num(item["price"]) : 0)), ("cookerAvailable", available), ("conditionResults", conditions));
        foreach (var field in new[] { "activeTags", "suppressedTags", "matchedPositiveTags", "matchedNegativeTags", "matchedSpecialFoodTargetTags", "meetsRequiredFood", "extraCost", "resourcePressure" }) result[field] = Clone(state[field]);
        return result;
    }
    /// <summary>候选排序只使用领域信号；最终组合权重排序在方案层执行。</summary>
    public static int CompareFoodCandidates(JsonObject left, JsonObject right) => FirstComparison(
        Arr(right["matchedSpecialFoodTargetTags"]).Count.CompareTo(Arr(left["matchedSpecialFoodTargetTags"]).Count), Bool(right["meetsRequiredFood"]).CompareTo(Bool(left["meetsRequiredFood"])),
        Arr(left["matchedNegativeTags"]).Count.CompareTo(Arr(right["matchedNegativeTags"]).Count), Arr(right["matchedPositiveTags"]).Count.CompareTo(Arr(left["matchedPositiveTags"]).Count),
        Arr(left["extraIngredients"]).Count.CompareTo(Arr(right["extraIngredients"]).Count), Num(left["resourcePressure"]).CompareTo(Num(right["resourcePressure"])),
        (Num(left["baseCost"]) + Num(left["extraCost"])).CompareTo(Num(right["baseCost"]) + Num(right["extraCost"])), Num(Obj(left["recipe"])["id"]).CompareTo(Num(Obj(right["recipe"])["id"])));
    /// <summary>酒水排序：满足点单、偏好、库存、价格，然后 ID。</summary>
    public static int CompareBeverageCandidates(JsonObject left, JsonObject right) => FirstComparison(
        Bool(right["meetsRequiredBeverage"]).CompareTo(Bool(left["meetsRequiredBeverage"])), Arr(right["matchedTags"]).Count.CompareTo(Arr(left["matchedTags"]).Count),
        InventoryRank(Num(right["ownedQuantity"])).CompareTo(InventoryRank(Num(left["ownedQuantity"]))), Num(Obj(right["beverage"])["price"]).CompareTo(Num(Obj(left["beverage"])["price"])),
        Num(Obj(left["beverage"])["id"]).CompareTo(Num(Obj(right["beverage"])["id"])));
}
