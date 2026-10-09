using System.Globalization;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using static MystiaStewardCompanion.Business.Domain.Support.SupportJson;

namespace MystiaStewardCompanion.Business.Domain.Support;

/// <summary>
/// 自定义料理的纯业务规则。自定义只改变候选来源和排序信号，仍通过解锁、材料、
/// 配方禁忌、厨具和特殊目标的标准检查，不能直接成为游戏执行命令。
/// </summary>
public static class CustomRecipes
{
    /// <summary>规范化加料 ID；保持既有有限非负整数、去重和数值升序语义。</summary>
    public static int[] NormalizeIdList(JsonNode? values) => Items(values).Select(v => Number(v, -1))
        .Where(v => double.IsFinite(v) && v >= 0 && v <= int.MaxValue).Select(v => (int)Math.Truncate(v)).Distinct().OrderBy(v => v).ToArray();

    /// <summary>返回规范配置副本。未知条目身份不参与候选，配置全局关闭时不生成任何自定义候选。</summary>
    public static JsonObject NormalizeCustomRecipeData(JsonNode? dataNode)
    {
        var data = Object(dataNode);
        var entries = Items(data["recipes"]).OfType<JsonObject>().Select(NormalizeEntry).OfType<JsonObject>()
            .OrderBy(v => v, Comparer<JsonObject>.Create(CompareEntries));
        return new JsonObject { ["version"] = Math.Max(1, Number(data["version"], 1)), ["enabled"] = Bool(data["enabled"], true), ["recipes"] = Array(entries) };
    }

    /// <summary>匹配规范客人 ID 与显式点单标签；不使用客人姓名作为身份。</summary>
    public static JsonArray GetEffectiveCustomRecipeEntries(JsonNode? data, int customerId, string foodTag)
    {
        var normalized = NormalizeCustomRecipeData(data);
        if (!Bool(normalized["enabled"])) return new JsonArray();
        return Array(Items(normalized["recipes"]).OfType<JsonObject>().Where(entry => Bool(entry["enabled"])
            && Int(entry["customerId"]) == customerId && (entry["foodTag"] is null || Text(entry["foodTag"]) == foodTag)));
    }

    /// <summary>为自定义料理构建与标准搜索相同形状的候选及可解释条件。</summary>
    public static JsonArray BuildCustomFoodCandidates(JsonObject options)
    {
        var data = Object(options["data"]);
        var context = Object(options["context"]);
        var customer = Object(options["customer"]);
        var foodTag = Text(options["requiredFoodTag"]);
        var requiredNodes = Items(options["requiredExtraIngredientIds"]).ToArray();
        var forbiddenNodes = Items(options["forbiddenExtraIngredientIds"]).ToArray();
        if (requiredNodes.Concat(forbiddenNodes).Any(v => !Integer(v, 0))
            || requiredNodes.Select(v => Int(v)).Distinct().Count() != requiredNodes.Length
            || forbiddenNodes.Select(v => Int(v)).Distinct().Count() != forbiddenNodes.Length) return new JsonArray();
        var required = requiredNodes.Select(v => Int(v)).ToHashSet();
        var forbidden = forbiddenNodes.Select(v => Int(v)).ToHashSet();
        // 与原 Map 行为一致：规范目录由入口验证，映射构造仍采用同键最后一项。
        var recipes = Index(data["recipes"], item => Int(item["id"]).ToString(CultureInfo.InvariantCulture));
        var ingredientsById = Index(data["ingredients"], item => Int(item["id"]).ToString(CultureInfo.InvariantCulture));
        var ingredientsByName = Index(data["ingredients"], item => Text(item["name"]));
        var recipeIds = IntSet(context["availableRecipeIds"]);
        var ingredientIds = IntSet(context["availableIngredientIds"]);
        var excluded = IntSet(context["excludedIngredientIds"]);
        excluded.UnionWith(IntSet(context["disabledIngredientIds"]));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<JsonObject>();
        foreach (var entry in GetEffectiveCustomRecipeEntries(options["customRecipes"], Int(customer["id"]), foodTag).OfType<JsonObject>())
        {
            if (!recipes.TryGetValue(Int(entry["foodId"]).ToString(CultureInfo.InvariantCulture), out var recipe)) continue;
            var extraIds = NormalizeIdList(entry["extraIngredientIds"]);
            var extras = extraIds.Where(id => ingredientsById.ContainsKey(id.ToString(CultureInfo.InvariantCulture))).Select(id => ingredientsById[id.ToString(CultureInfo.InvariantCulture)]).ToArray();
            if (extras.Length != extraIds.Length || !recipeIds.Contains(Int(recipe["id"]))) continue;
            var baseNames = Items(recipe["ingredients"]).Select(v => Text(v)).ToArray();
            if (baseNames.Length + extras.Length > 5) continue;
            if (baseNames.Any(name => !ingredientsByName.TryGetValue(name, out var ingredient) || !ingredientIds.Contains(Int(ingredient["id"])) || excluded.Contains(Int(ingredient["id"])))) continue;
            var cookerAvailable = !Bool(context["hasCookerSnapshot"]) || StringSet(context["placedCookerNames"]).Contains(Text(recipe["cooker"]));
            if (Bool(context["filterMissingCookers"]) && !cookerAvailable) continue;
            var baseIds = baseNames.Select(name => Int(ingredientsByName[name]["id"])).ToHashSet();
            if (extras.Any(ingredient => !ingredientIds.Contains(Int(ingredient["id"])) || excluded.Contains(Int(ingredient["id"]))
                || baseIds.Contains(Int(ingredient["id"])) || RecommendationEngine.HasForbiddenIngredientTag(ingredient, recipe))) continue;
            if (!required.IsSubsetOf(extraIds) || forbidden.Overlaps(extraIds)) continue;
            var candidate = BuildCandidate(recipe, extras, entry, options, context, ingredientsByName, cookerAvailable);
            if (seen.Add(CandidateKey(candidate))) candidates.Add(candidate);
        }
        return Array(candidates.OrderBy(v => v, Comparer<JsonObject>.Create((a, b) =>
        {
            var order = Number(a["customRecipeSortOrder"], 9007199254740991d).CompareTo(Number(b["customRecipeSortOrder"], 9007199254740991d));
            return order != 0 ? order : RecommendationEngine.CompareFoodCandidates(a, b);
        })));
    }

    /// <summary>自定义候选先进入同一去重集合，避免相同料理加料组合重复影响排序。</summary>
    public static JsonArray MergeCustomFoodCandidates(JsonArray foodCandidates, JsonArray customFoodCandidates)
    {
        // 原规则在没有自定义来源时直接保留标准候选序列；去重只服务于两类来源的合并。
        if (customFoodCandidates.Count == 0) return Array(foodCandidates);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return Array(customFoodCandidates.Concat(foodCandidates).OfType<JsonObject>().Where(candidate => seen.Add(CandidateKey(candidate))));
    }

    private static JsonObject BuildCandidate(JsonObject recipe, JsonObject[] extras, JsonObject entry, JsonObject demand, JsonObject context, Dictionary<string, JsonObject> ingredientsByName, bool cookerAvailable)
    {
        var resolved = RecommendationEngine.ResolveFoodTags(new JsonObject
        {
            ["recipe"] = Clone(recipe), ["extraIngredients"] = Array(extras),
            ["popularFoodTag"] = Clone(context["popularFoodTag"]), ["popularHateFoodTag"] = Clone(context["popularHateFoodTag"]),
            ["famousShopEnabled"] = Bool(context["famousShopEnabled"]), ["tagPriorityRules"] = Clone(context["tagPriorityRules"]),
        });
        var tags = Items(resolved["activeTags"]).Select(v => Text(v)).ToArray();
        var positive = StringSet(demand["customer"]?["positiveTags"]);
        var negative = StringSet(demand["customer"]?["negativeTags"]);
        var matchedPositive = tags.Where(positive.Contains).ToArray();
        var matchedNegative = tags.Where(negative.Contains).ToArray();
        var target = Object(demand["specialFoodTarget"]);
        var targetTags = Items(target["tags"]).Select(v => Text(v).Trim()).Where(v => v.Length != 0).Distinct(StringComparer.Ordinal).ToArray();
        var matchedTarget = targetTags.Where(tag => tags.Contains(tag, StringComparer.Ordinal)).ToArray();
        var enforcement = Text(target["enforcement"], "none");
        var all = Text(target["match"], "any") == "all";
        var meetsTarget = enforcement == "none" || targetTags.Length > 0 && (all ? matchedTarget.Length == targetTags.Length : matchedTarget.Length > 0);
        var foodTag = Text(demand["requiredFoodTag"]);
        var meetsFood = tags.Contains(foodTag, StringComparer.Ordinal);
        var suppressed = Items(resolved["suppressedTags"]).Select(v => Text(v)).ToArray();
        var conditions = new JsonArray
        {
            Condition("food.custom-recipe", "info", "info", "自定义配方", entry["foodTag"] is null ? "该自定义配方适用于该稀客的所有点单料理 Tag。" : $"该自定义配方绑定点单料理 {Text(entry["foodTag"])}。"),
            Condition("food.required-tag", meetsFood ? "pass" : "warn", meetsFood ? "hard" : "soft", "料理点单", $"{(meetsFood ? "满足" : "未满足")}点单料理 {foodTag}"),
        };
        if (enforcement != "none")
        {
            var detail = meetsTarget ? $"{(all ? "同时满足" : "满足")}特殊目标 Tag {string.Join("、", matchedTarget)}"
                : targetTags.Length == 0 ? "特殊目标 Tag 尚未完整读取" : $"{(all ? "未同时满足" : "未满足")}特殊目标 Tag {string.Join("、", targetTags.Except(matchedTarget))}";
            conditions.Add(Condition("food.special-target-tag", meetsTarget ? "pass" : "fail", "hard", "特殊目标 Tag", detail));
        }
        if (!cookerAvailable) conditions.Add(Condition("food.cooker", "fail", "hard", "厨具", $"缺少厨具 {Text(recipe["cooker"], "未知")}"));
        if (matchedPositive.Length > 0) conditions.Add(Condition("food.preference", "boost", "soft", "料理偏好", $"命中 {string.Join("、", matchedPositive)}"));
        if (matchedNegative.Length > 0) conditions.Add(Condition("food.negative-tags", "warn", "soft", "厌恶标签", $"包含 {string.Join("、", matchedNegative)}"));
        if (suppressed.Length > 0) conditions.Add(Condition("food.suppressed-tags", "info", "info", "标签优先级", $"压制 {string.Join("、", suppressed)}"));
        var reasons = new JsonObject();
        var relevant = positive.Union(targetTags).Append(foodTag).ToHashSet(StringComparer.Ordinal);
        foreach (var ingredient in extras)
        {
            var matched = Items(ingredient["tags"]).Select(v => Text(v)).Where(relevant.Contains).ToArray();
            if (matched.Length > 0) reasons[Int(ingredient["id"]).ToString(CultureInfo.InvariantCulture)] = Strings(matched);
        }
        return new JsonObject
        {
            ["recipe"] = Clone(recipe), ["extraIngredients"] = Array(extras), ["customRecipe"] = true,
            ["customRecipePinned"] = Bool(entry["pinToTop"]), ["customRecipeSortOrder"] = Clone(entry["sortOrder"]),
            ["customRecipeScope"] = entry["foodTag"] is null ? "all" : "tag", ["customRecipeId"] = Clone(entry["id"]),
            ["extraIngredientReasonTags"] = reasons, ["activeTags"] = Strings(tags), ["suppressedTags"] = Strings(suppressed),
            ["matchedPositiveTags"] = Strings(matchedPositive), ["matchedNegativeTags"] = Strings(matchedNegative),
            ["matchedSpecialFoodTargetTags"] = Strings(matchedTarget), ["meetsRequiredFood"] = meetsFood,
            ["baseCost"] = Items(recipe["ingredients"]).Sum(name => ingredientsByName.TryGetValue(Text(name), out var ingredient) ? Number(ingredient["price"]) : 0),
            ["extraCost"] = extras.Sum(ingredient => Number(ingredient["price"])),
            ["resourcePressure"] = extras.Sum(ingredient => { var qty = Number(context["ownedIngredientQty"]?[Int(ingredient["id"]).ToString(CultureInfo.InvariantCulture)]); return qty == -1 ? 0 : Math.Max(0, 5 - qty); }),
            ["cookerAvailable"] = cookerAvailable, ["conditionResults"] = conditions,
        };
    }

    private static JsonObject Condition(string id, string status, string severity, string label, string detail)
        => new() { ["id"] = id, ["target"] = "food", ["status"] = status, ["severity"] = severity, ["label"] = label, ["detail"] = detail };
    private static Dictionary<string, JsonObject> Index(JsonNode? entries, Func<JsonObject, string> key)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var entry in Items(entries).OfType<JsonObject>()) result[key(entry)] = entry;
        return result;
    }
    private static string CandidateKey(JsonObject candidate) => $"{Int(candidate["recipe"]?["id"])}:{string.Join(",", NormalizeIdList(Array(Items(candidate["extraIngredients"]).Select(v => v?["id"]))))}";
    private static JsonObject? NormalizeEntry(JsonObject entry)
    {
        var id = Text(entry["id"]).Trim();
        var customer = NonNegative(entry["customerId"], -1);
        var food = NonNegative(entry["foodId"], -1);
        if (id.Length == 0 || customer < 0 || food < 0) return null;
        var tag = Text(entry["foodTag"]).Trim();
        return new JsonObject
        {
            ["id"] = id, ["customerId"] = customer, ["foodId"] = food, ["customerName"] = Text(entry["customerName"]).Trim(),
            ["foodTag"] = tag.Length == 0 ? null : JsonValue.Create(tag), ["recipeId"] = NonNegative(entry["recipeId"], -1),
            ["recipeName"] = Text(entry["recipeName"]).Trim(), ["extraIngredientIds"] = Integers(NormalizeIdList(entry["extraIngredientIds"])),
            ["enabled"] = Bool(entry["enabled"], true), ["pinToTop"] = Bool(entry["pinToTop"], true),
            ["sortOrder"] = NonNegative(entry["sortOrder"], 0), ["createdAtUtc"] = Text(entry["createdAtUtc"]), ["updatedAtUtc"] = Text(entry["updatedAtUtc"]),
        };
    }
    private static int NonNegative(JsonNode? node, int fallback) { var number = Number(node, -1); return number >= 0 && number <= int.MaxValue ? (int)Math.Truncate(number) : fallback; }
    private static int CompareEntries(JsonObject a, JsonObject b)
    {
        var order = Int(a["sortOrder"]).CompareTo(Int(b["sortOrder"]));
        if (order != 0) return order;
        order = Int(a["customerId"]).CompareTo(Int(b["customerId"]));
        if (order != 0) return order;
        // 使用明确文化避免客户端与后台线程继承不同系统文化而改变并列项顺序。
        var comparer = CultureInfo.GetCultureInfo("en-US").CompareInfo;
        order = comparer.Compare(Text(a["foodTag"]), Text(b["foodTag"]), CompareOptions.None);
        return order != 0 ? order : comparer.Compare(Text(a["id"]), Text(b["id"]), CompareOptions.None);
    }
}
