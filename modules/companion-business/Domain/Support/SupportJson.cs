using System.Globalization;
using System.Text.Json.Nodes;

namespace MystiaStewardCompanion.Business.Domain.Support;

/// <summary>
/// 领域辅助模块使用的 JSON 值访问函数。所有返回到外部的集合均为独立值树，
/// 不共享 JsonNode 的父节点，也不将可变请求数据当作跨线程缓存。
/// </summary>
internal static class SupportJson
{
    // 与推荐核心共用递归值复制，避免支持模块在查询边界重新序列化整棵目录或候选树。
    public static JsonNode? Clone(JsonNode? value) => Recommendation.RecommendationJson.Clone(value);
    public static JsonObject Object(JsonNode? value) => value as JsonObject ?? new JsonObject();
    public static IEnumerable<JsonNode?> Items(JsonNode? value) => value is JsonArray array ? array : Enumerable.Empty<JsonNode?>();
    public static string Text(JsonNode? value, string fallback = "") => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : fallback;
    public static bool Bool(JsonNode? value, bool fallback = false) => value is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag : fallback;
    public static double Number(JsonNode? value, double fallback = 0)
    {
        if (value is not JsonValue || !double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return fallback;
        return double.IsFinite(number) ? number : fallback;
    }
    public static bool Integer(JsonNode? value, int minimum = int.MinValue, int maximum = int.MaxValue)
    {
        var number = Number(value, double.NaN);
        return double.IsFinite(number) && number == Math.Truncate(number) && number >= minimum && number <= maximum;
    }
    public static int Int(JsonNode? value, int fallback = 0) => Integer(value) ? (int)Number(value) : fallback;
    public static HashSet<int> IntSet(JsonNode? value) => Items(value).Where(v => Integer(v)).Select(v => Int(v)).ToHashSet();
    public static HashSet<string> StringSet(JsonNode? value) => Items(value).Select(v => Text(v)).ToHashSet(StringComparer.Ordinal);
    public static JsonArray Array(IEnumerable<JsonNode?> values) => new(values.Select(Clone).ToArray());
    public static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    public static JsonArray Integers(IEnumerable<int> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
