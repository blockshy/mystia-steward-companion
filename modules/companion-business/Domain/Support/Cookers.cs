using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MystiaStewardCompanion.Business.Domain.Support.SupportJson;

namespace MystiaStewardCompanion.Business.Domain.Support;

/// <summary>
/// 纯数据厨具规则。类型名称仅用于已验证目录的规则映射，实际预约始终携带
/// 控制器索引、原生对象标识和三维网格坐标，不通过名称猜测游戏对象。
/// </summary>
public static class Cookers
{
    private static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [1] = "煮锅", [2] = "烧烤架", [3] = "油锅", [4] = "蒸锅", [5] = "料理台",
    };

    /// <summary>保留旧领域规则中的少量厨具显示别名，返回规范类型名称。</summary>
    public static string NormalizeCookerName(string? value) => (value ?? "").Trim() switch
    {
        "烤架" or "烧烤台" => "烧烤架", "锅" => "煮锅", "炸锅" => "油锅", var name => name,
    };

    /// <summary>将规范名称映射为游戏厨具类型；未知名称不推测，返回 -1。</summary>
    public static int ResolveCookerTypeId(string? value)
    {
        var name = NormalizeCookerName(value);
        return Names.Where(pair => pair.Value == name).Select(pair => pair.Key).DefaultIfEmpty(-1).First();
    }

    /// <summary>把运行时已解锁集合和完整厨具证据投影为推荐输入，Set 在线上用数组表达。</summary>
    public static JsonObject? BuildRuntimeSets(JsonNode? runtimeNode, JsonObject data)
    {
        if (runtimeNode is not JsonObject runtime) return null;
        var ingredients = IntSet(runtime["availableIngredientIds"]);
        var placed = IntSet(runtime["placedCookerTypeIds"]);
        var complete = Bool(runtime["placedCookerSnapshotComplete"]);
        var unavailable = complete && Int(runtime["placedCookerLockedControllerCount"]) > 0
            ? Names.Keys.Except(placed) : Enumerable.Empty<int>();
        return new JsonObject
        {
            ["recipeIds"] = Integers(IntSet(runtime["availableRecipeIds"])),
            ["beverageIds"] = Integers(IntSet(runtime["availableBeverageIds"])),
            ["ingredientIds"] = Integers(ingredients),
            ["unavailableIngredientIds"] = Integers(Items(data["ingredients"]).Select(v => Int(v?["id"])).Where(id => !ingredients.Contains(id)).Distinct()),
            ["ownedIngredientQty"] = NormalizeOwnedQuantity(runtime["ownedIngredientQty"]),
            ["ownedBeverageQty"] = NormalizeOwnedQuantity(runtime["ownedBeverageQty"]),
            ["placedCookerTypeIds"] = Integers(placed),
            ["placedCookerNames"] = Strings(placed.Where(Names.ContainsKey).Select(id => Names[id])),
            ["usableCookerNames"] = Strings(placed.Where(Names.ContainsKey).Select(id => Names[id])),
            ["runtimeUnavailableCookerNames"] = Strings(unavailable.Select(id => Names[id])),
            ["hasCookerSnapshot"] = complete,
        };
    }

    /// <summary>
    /// 运行时数量字典的键按既有 Number(id) 语义规范化；例如 "01" 与 "1" 表示同一材料。
    /// 数量本身原样复制，-1 的无限语义由统一库存规则解释，不在此处改写或截断。
    /// </summary>
    private static JsonObject NormalizeOwnedQuantity(JsonNode? node)
    {
        var normalized = new JsonObject();
        foreach (var (key, value) in Object(node))
        {
            var trimmed = key.Trim();
            var numeric = trimmed.Length == 0 ? 0d : double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
            var normalizedKey = double.IsNaN(numeric) ? "NaN" : numeric == 0 ? "0" : numeric.ToString("G17", CultureInfo.InvariantCulture);
            normalized[normalizedKey] = Clone(value);
        }
        return normalized;
    }

    /// <summary>缺失厨具过滤关闭时仍遵守完整快照中不可安全使用的锁定类型。</summary>
    public static JsonArray BuildRecommendationCookerNameSet(JsonObject runtimeSets, bool filterMissingCookers)
    {
        if (!Bool(runtimeSets["hasCookerSnapshot"])) return Array(Items(runtimeSets["placedCookerNames"]));
        if (filterMissingCookers) return Array(Items(runtimeSets["usableCookerNames"]));
        var unavailable = StringSet(runtimeSets["runtimeUnavailableCookerNames"]);
        return Strings(Names.Values.Where(name => !unavailable.Contains(name)));
    }

    /// <summary>逐条验证主线程发布的完整厨具快照；任何结构或身份冲突使整份快照不可用。</summary>
    public static string ValidateRecommendationCookerSnapshot(JsonObject runtime)
    {
        if (runtime["placedCookerTypeIds"] is not JsonArray types) return "placedCookerTypeIds 不是数组";
        if (runtime["placedCookers"] is not JsonArray cookers) return "placedCookers 不是数组";
        if (runtime["placedCookerSnapshotComplete"] is not JsonValue completeValue || !completeValue.TryGetValue<bool>(out var complete)) return "placedCookerSnapshotComplete 不是布尔值";
        foreach (var key in new[] { "placedCookerControllerCount", "placedCookerEmptyControllerCount", "placedCookerLockedControllerCount", "placedCookerReadFailureCount" })
            if (!Integer(runtime[key], 0)) return $"{key} 不是非负整数";
        if (runtime["placedCookerStatus"] is not JsonValue status || !status.TryGetValue<string>(out _)) return "placedCookerStatus 不是字符串";
        var total = Int(runtime["placedCookerControllerCount"]);
        var empty = Int(runtime["placedCookerEmptyControllerCount"]);
        var locked = Int(runtime["placedCookerLockedControllerCount"]);
        var failed = Int(runtime["placedCookerReadFailureCount"]);
        if ((long)empty + locked + failed > total) return "空位、锁定与读取失败数量大于 controllerCount";
        if ((long)cookers.Count + empty + locked + failed != total) return "placedCookers 数量与 controllerCount/emptyControllerCount/lockedControllerCount/readFailureCount 不一致";
        if (complete && (failed != 0 || (long)cookers.Count + empty + locked != total)) return "完整厨具快照包含读取失败或缺失控制器";
        if (!complete && (cookers.Count != 0 || types.Count != 0 || empty != 0)) return "不可用厨具快照包含部分控制器、空位或类型";
        var placedTypes = new HashSet<int>();
        foreach (var type in types)
        {
            if (!Integer(type, 1, 5)) return "placedCookerTypeIds 包含非法厨具类型";
            if (!placedTypes.Add(Int(type))) return "placedCookerTypeIds 包含重复厨具类型";
        }
        var indexes = new HashSet<int>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var positions = new HashSet<string>(StringComparer.Ordinal);
        var projected = new HashSet<int>();
        foreach (var node in cookers)
        {
            if (node is not JsonObject cooker || !Integer(cooker["controllerIndex"], 0) || Int(cooker["controllerIndex"]) >= total) return "placedCookers 包含非法 controllerIndex";
            var index = Int(cooker["controllerIndex"]);
            if (!indexes.Add(index)) return "placedCookers 包含重复 controllerIndex";
            if (!IsGridPosition(cooker["gridPosition"])) return $"controller {index} 的 gridPosition 非法";
            var position = Object(cooker["gridPosition"]);
            if (!positions.Add($"{Int(position["x"])},{Int(position["y"])},{Int(position["z"])}")) return "placedCookers 包含重复 gridPosition";
            if (!IsControllerIdentity(cooker["controllerIdentity"])) return $"controller {index} 的 controllerIdentity 非法";
            if (!identities.Add(Text(cooker["controllerIdentity"]))) return "placedCookers 包含重复 controllerIdentity";
            if (cooker["typeIds"] is not JsonArray ids || ids.Count == 0 || ids.Any(v => !Integer(v, 1, 5)) || IntSet(ids).Count != ids.Count) return $"controller {index} 的 typeIds 非法";
            if (cooker["typeNames"] is not JsonArray typeNames || typeNames.Any(v => v is not JsonValue s || !s.TryGetValue<string>(out _))) return $"controller {index} 的 typeNames 非法";
            var expected = ids.Select(v => Names[Int(v)]).ToArray();
            if (!typeNames.Select(v => Text(v)).SequenceEqual(expected) || Text(cooker["name"]) != string.Join("/", expected)) return $"controller {index} 的厨具名称与 typeIds 不一致";
            foreach (var key in new[] { "challengeLocked", "couldOpen", "automationAvailable" })
                if (cooker[key] is not JsonValue b || !b.TryGetValue<bool>(out _)) return $"controller {index} 的基础字段非法";
            foreach (var key in new[] { "name", "automationAvailabilityDiagnostic", "source" })
                if (cooker[key] is not JsonValue s || !s.TryGetValue<string>(out _)) return $"controller {index} 的基础字段非法";
            var availability = Text(cooker["automationAvailability"]);
            if (availability is not ("StrictIdle" or "ExtractedResidual" or "Unavailable")) return $"controller {index} 的 automationAvailability 非法";
            if (Bool(cooker["challengeLocked"]) || !Bool(cooker["couldOpen"])) return $"controller {index} 已锁定或不可开，不应进入 placedCookers";
            if (Bool(cooker["automationAvailable"]) != (availability != "Unavailable")) return $"controller {index} 的自动化可用状态不一致";
            projected.UnionWith(IntSet(ids));
        }
        return placedTypes.SetEquals(projected) ? "" : "placedCookerTypeIds 与控制器类型投影不一致";
    }

    /// <summary>构建独立控制器槽位池；多类型锅占用一个槽位，不能按类型重复预约。</summary>
    public static JsonObject BuildAutomationCookerPool(JsonNode? runtimeNode)
    {
        var runtime = Object(runtimeNode);
        var count = Int(runtime["placedCookerControllerCount"]);
        var failures = Int(runtime["placedCookerReadFailureCount"]);
        var cookers = Items(runtime["placedCookers"]).OfType<JsonObject>().ToArray();
        var complete = runtimeNode != null && Bool(runtime["placedCookerSnapshotComplete"]) && failures == 0
            && cookers.Length + Int(runtime["placedCookerEmptyControllerCount"]) + Int(runtime["placedCookerLockedControllerCount"]) == count;
        var slots = new Dictionary<int, JsonObject>();
        var duplicated = new HashSet<int>();
        if (complete)
        foreach (var cooker in cookers)
        {
            if (!Bool(cooker["automationAvailable"]) || !Bool(cooker["couldOpen"]) || Bool(cooker["challengeLocked"], true)
                || !Integer(cooker["controllerIndex"], 0) || !IsGridPosition(cooker["gridPosition"]) || !IsControllerIdentity(cooker["controllerIdentity"])) continue;
            var index = Int(cooker["controllerIndex"]);
            if (slots.Remove(index)) { duplicated.Add(index); continue; }
            if (duplicated.Contains(index)) continue;
            var keys = IntSet(cooker["typeIds"]).Where(Names.ContainsKey).Select(id => Names[id]).OrderBy(v => v, StringComparer.Ordinal).ToArray();
            if (keys.Length == 0) continue;
            slots[index] = new JsonObject
            {
                ["controllerIndex"] = index, ["controllerIdentity"] = Clone(cooker["controllerIdentity"]),
                ["gridPosition"] = Clone(cooker["gridPosition"]), ["supportedKeys"] = Strings(keys),
            };
        }
        return new JsonObject
        {
            ["slots"] = Array(slots.OrderBy(p => p.Key).Select(p => p.Value)), ["snapshotComplete"] = complete,
            ["controllerCount"] = count, ["readFailureCount"] = failures,
        };
    }

    /// <summary>优先预约能力最少的可用锅，避免单类型任务过早占用多类型资源。</summary>
    public static JsonObject? FindAvailableAutomationCookerSlot(JsonObject pool, string cookerKey, ISet<int> unavailableControllerIndexes)
        => Clone(Items(pool["slots"]).OfType<JsonObject>()
            .Where(slot => StringSet(slot["supportedKeys"]).Contains(cookerKey) && !unavailableControllerIndexes.Contains(Int(slot["controllerIndex"])))
            .OrderBy(slot => Items(slot["supportedKeys"]).Count()).ThenBy(slot => Int(slot["controllerIndex"])).FirstOrDefault()) as JsonObject;

    /// <summary>只有完整的整数三维坐标可作为物理槽位身份的一部分。</summary>
    private static bool IsGridPosition(JsonNode? node) => node is JsonObject obj && new[] { "x", "y", "z" }.All(key => Integer(obj[key]));
    private static bool IsControllerIdentity(JsonNode? node) => Regex.IsMatch(Text(node), "^0x(?=[0-9A-F]*[1-9A-F])[0-9A-F]+$", RegexOptions.CultureInvariant);
}
