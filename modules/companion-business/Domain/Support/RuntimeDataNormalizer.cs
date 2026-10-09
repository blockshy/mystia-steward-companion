using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Support;

/// <summary>
/// 将游戏发布的完整目录转换为推荐输入。目录缺失时返回不可用状态，绝不回退到静态或旧版本目录。
/// 基础材料是数量敏感序列，重复项必须保留；标签与地点才按集合去重。
/// </summary>
public static class RuntimeDataNormalizer
{
    private static readonly HashSet<string> Places = new(StringComparer.Ordinal)
    { "妖怪兽道", "人间之里", "博丽神社", "红魔馆", "迷途竹林", "魔法森林", "妖怪之山", "旧地狱", "地灵殿", "命莲寺", "神灵庙", "太阳花田", "辉针城", "月之都", "魔界" };

    public static JsonObject Build(JsonNode? source)
    {
        var raw = Obj(source);
        if (!Bool(raw["isComplete"])) return Empty("等待游戏运行时数据");
        var data = Empty(NonEmpty(Str(raw["status"]), NonEmpty(Str(raw["source"]), "game runtime")));
        foreach (var kind in new[] { "recipes", "ingredients", "beverages", "normalCustomers", "rareCustomers" })
            data[kind] = Array(Objects(raw[kind]).Select(item => Normalize(item, kind)).Where(item => item != null));
        data["rareCustomerProfiles"] = Array(Objects(raw["rareCustomers"]).Select(item => Normalize(item, "profiles")).Where(item => item != null));
        if (new[] { "recipes", "ingredients", "beverages", "normalCustomers", "rareCustomers" }.Any(kind => Arr(data[kind]).Count == 0))
            return Empty(NonEmpty(Str(raw["status"]), NonEmpty(Str(raw["source"]), "运行时数据不完整")));
        data["source"] = "runtime";
        data["foodTagIdMap"] = StringRecord(raw["foodTagIdMap"]);
        data["beverageTagIdMap"] = StringRecord(raw["beverageTagIdMap"]);
        data["tagPriorityRules"] = Array(Objects(raw["tagPriorityRules"]).Select(item =>
        {
            // JSON 缺字段对应旧客户端的 undefined，而显式 null 对应 Number(null) == 0；二者不可混同。
            var id = item.ContainsKey("id") ? Numeric(item["id"]) : null;
            var ids = Arr(item["tagIds"]).Select(Numeric).Where(n => n.HasValue && n >= 0).Select(n => Math.Truncate(n!.Value)).Distinct().ToArray();
            var tags = Texts(item["tags"]);
            return id is not double valid || valid < 0 || ids.Length == 0 || tags.Count == 0 ? null
                : Object(("id", Math.Truncate(valid)), ("tagIds", Array(ids)), ("tags", tags));
        }).Where(item => item != null));
        return data;
    }

    private static JsonObject? Normalize(JsonObject item, string kind)
    {
        if (NullableNumber(item["id"]) == null || Str(item["name"]).Length == 0) return null;
        if (kind == "recipes" && NullableNumber(item["recipeId"]) == null) return null;
        var result = Object(("id", item["id"]), ("name", item["name"]));
        if (kind is "normalCustomers" or "rareCustomers" or "profiles")
        {
            var name = Str(item["name"]).Trim();
            if (name.Length == 0 || name is "missing" or "null" || name.Contains('?') || name.StartsWith('#')) return null;
            var positive = Texts(item["positiveTags"]);
            if (kind != "normalCustomers") positive = Array(Strings(positive).Where(tag => tag is not ("流行喜爱" or "流行厌恶")));
            var beverage = Texts(item["beverageTags"]);
            var places = Array(Strings(Texts(item["places"])).Where(Places.Contains));
            if (kind == "normalCustomers" ? positive.Count == 0 && beverage.Count == 0 : positive.Count == 0 || beverage.Count == 0) return null;
            if (kind != "profiles" && places.Count == 0) return null;
            result["positiveTags"] = positive; result["beverageTags"] = beverage;
            if (kind != "normalCustomers") result["negativeTags"] = Texts(item["negativeTags"]);
            if (kind == "profiles") return result;
            result["places"] = places;
            Set(result, item, "description", JsonValue.Create("")); Set(result, item, "dlc", JsonValue.Create(0));
            if (kind == "rareCustomers")
            {
                Set(result, item, "price", new JsonArray(0, 0)); Set(result, item, "enduranceLimit", JsonValue.Create(1));
                Set(result, item, "collection", JsonValue.Create(false));
                foreach (var key in new[] { "positiveTagMapping", "beverageTagMapping", "evaluation" }) Set(result, item, key, new JsonObject());
                Set(result, item, "spellCards", new JsonObject { ["positive"] = new JsonArray(), ["negative"] = new JsonArray() });
            }
            return result;
        }
        Set(result, item, "description", JsonValue.Create(""));
        foreach (var key in new[] { "dlc", "level", "price" }) Set(result, item, key, JsonValue.Create(0));
        Set(result, item, "from", new JsonObject());
        if (kind == "recipes")
        {
            result["recipeId"] = Clone(item["recipeId"]); result["ingredients"] = Texts(item["ingredients"], false);
            result["positiveTags"] = Texts(item["positiveTags"]); result["negativeTags"] = Texts(item["negativeTags"]);
            Set(result, item, "cooker", JsonValue.Create("")); Set(result, item, "baseCookTime", JsonValue.Create(0));
        }
        else
        {
            result["tags"] = Texts(item["tags"]);
            if (kind == "ingredients") Set(result, item, "type", JsonValue.Create(""));
        }
        return result;
    }

    private static JsonObject Empty(string status) => new()
    {
        ["recipes"] = new JsonArray(), ["ingredients"] = new JsonArray(), ["beverages"] = new JsonArray(),
        ["normalCustomers"] = new JsonArray(), ["rareCustomers"] = new JsonArray(), ["rareCustomerProfiles"] = new JsonArray(),
        ["foodTagIdMap"] = new JsonObject(), ["beverageTagIdMap"] = new JsonObject(), ["tagPriorityRules"] = new JsonArray(),
        ["source"] = "unavailable", ["status"] = status,
    };
    private static void Set(JsonObject target, JsonObject source, string key, JsonNode? fallback) => target[key] = Clone(source[key] ?? fallback);
    private static string NonEmpty(string value, string fallback) => value.Length > 0 ? value : fallback;
    /// <summary>复现目录清洗使用的 JavaScript String；对象与数组不能直接序列化为 JSON 字面量。</summary>
    private static string JsText(JsonNode? value)
    {
        if (value == null) return "null";
        if (value is JsonObject) return "[object Object]";
        if (value is JsonArray array) return string.Join(",", array.Select(item => item == null ? "" : JsText(item)));
        return value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value.ToJsonString();
    }
    private static JsonArray Texts(JsonNode? value, bool distinct = true)
    {
        var values = Arr(value).Select(JsText).Select(text => text.Trim()).Where(text => text.Length > 0);
        return Array(distinct ? values.Distinct(StringComparer.Ordinal) : values);
    }
    private static double? Numeric(JsonNode? value)
    {
        if (value == null) return 0;
        if (NullableNumber(value) is double number) return number;
        if (value is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean)) return boolean ? 1 : 0;
        var text = JsText(value).Trim();
        if (text.Length == 0) return 0;
        // Number 支持无符号的十六/八/二进制字符串；这里只复现 JSON 输入能表达的转换边界。
        if (text.Length > 2 && text[0] == '0' && "xXoObB".Contains(text[1]))
        {
            var radix = char.ToLowerInvariant(text[1]) switch { 'x' => 16, 'o' => 8, _ => 2 };
            try { return Convert.ToInt64(text[2..], radix); } catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException) { return null; }
        }
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) && double.IsFinite(number) ? number : null;
    }
    private static JsonObject StringRecord(JsonNode? value)
    {
        var result = new JsonObject();
        foreach (var field in Obj(value)) { var key = field.Key.Trim(); var text = JsText(field.Value).Trim(); if (key.Length > 0 && text.Length > 0) result[key] = text; }
        return result;
    }
}
