using System.Globalization;
using System.Text.Json.Nodes;

namespace MystiaStewardCompanion.Business.Domain.Recommendation;

/// <summary>
/// 推荐领域的 JSON 值工具。输入对象仅供读取，输出始终复制值树，避免 JsonNode 的单父节点约束
/// 令调用方数据被移动或在并发查询之间互相污染；所有数值均按 JavaScript Number 的双精度语义读取。
/// </summary>
public static class RecommendationJson
{
    /// <summary>复制可空节点；兼容 .NET 6，不能依赖后续框架才提供的 DeepClone。</summary>
    public static JsonNode? Clone(JsonNode? value)
    {
        // .NET 6 没有 DeepClone。直接递归复制节点可保持独立 parent 所有权，避免在搜索热路径反复
        // 分配 JSON 文本并重新解析。解析得到的标量应提取为独立CLR值，而不是逐个JsonElement.Clone：
        // 后者会为每个标量创建独立文档，在高频状态读取中产生大量小对象和GC压力。
        if (value is JsonObject sourceObject)
        {
            var result = new JsonObject();
            foreach (var entry in sourceObject) result[entry.Key] = Clone(entry.Value);
            return result;
        }
        if (value is JsonArray sourceArray)
        {
            var result = new JsonArray();
            foreach (var entry in sourceArray) result.Add(Clone(entry));
            return result;
        }
        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<System.Text.Json.JsonElement>(out var element))
            {
                return element.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => JsonValue.Create(element.GetString()),
                    System.Text.Json.JsonValueKind.True => JsonValue.Create(true),
                    System.Text.Json.JsonValueKind.False => JsonValue.Create(false),
                    // 优先保留常见Int32契约，使复制后的协议版本/目录ID仍可通过TryGetValue<int>读取。
                    System.Text.Json.JsonValueKind.Number => element.TryGetInt32(out var smallInteger)
                        ? JsonValue.Create(smallInteger) : element.TryGetInt64(out var integer)
                            ? JsonValue.Create(integer) : JsonValue.Create(element.GetDouble()),
                    System.Text.Json.JsonValueKind.Null => null,
                    // JsonValue也可能由外部调用方包装整个JsonElement；非标量情况仍完整脱离原文档。
                    _ => JsonValue.Create(element.Clone()),
                };
            }
            return JsonValue.Create(scalar.GetValue<object>());
        }
        return null;
    }
    /// <summary>读取对象字段；缺失字段返回空对象，调用方应另行执行必需字段校验。</summary>
    public static JsonObject Obj(JsonNode? value) => value as JsonObject ?? new JsonObject();
    /// <summary>读取数组字段；缺失字段按空集合处理。</summary>
    public static JsonArray Arr(JsonNode? value) => value as JsonArray ?? new JsonArray();
    /// <summary>读取字符串，不对数字或对象作隐式转换。</summary>
    public static string Str(JsonNode? value, string fallback = "") => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : fallback;
    /// <summary>读取有限数；保留 null 与 0 的区别应使用 NullableNumber。</summary>
    public static double Num(JsonNode? value, double fallback = 0) => NullableNumber(value) ?? fallback;
    /// <summary>读取可空有限数，拒绝非数值 JSON。</summary>
    public static double? NullableNumber(JsonNode? value)
    {
        if (value is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        return null;
    }
    /// <summary>仅接受显式布尔值，不将非空字符串解释为 true。</summary>
    public static bool Bool(JsonNode? value, bool fallback = false) => value is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    /// <summary>返回字符串数组；领域协议中的 Set 均在线路上使用数组表达。</summary>
    public static string[] Strings(JsonNode? value) => Arr(value).Select(x => Str(x)).ToArray();
    /// <summary>返回数值数组。</summary>
    public static double[] Numbers(JsonNode? value) => Arr(value).Select(x => Num(x)).ToArray();
    /// <summary>读取对象数组。</summary>
    public static IEnumerable<JsonObject> Objects(JsonNode? value) => Arr(value).OfType<JsonObject>();
    /// <summary>构建复制后的数组，调用方可安全复用原始候选。</summary>
    public static JsonArray Array(IEnumerable<JsonNode?> values) => new(values.Select(Clone).ToArray());
    /// <summary>构建字符串数组并保留调用方顺序。</summary>
    public static JsonArray Array(IEnumerable<string> values) => new(values.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    /// <summary>构建数值数组并保留调用方顺序。</summary>
    public static JsonArray Array(IEnumerable<double> values) => new(values.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    /// <summary>构建对象并复制所有节点；元组值可为基本类型、节点或 null。</summary>
    public static JsonObject Object(params (string Key, object? Value)[] values)
    {
        var result = new JsonObject();
        foreach (var (key, value) in values) result[key] = value switch
        {
            null => null,
            JsonNode n => Clone(n),
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d when double.IsFinite(d) => JsonValue.Create(d),
            double => null,
            _ => throw new ArgumentException($"不支持的领域 JSON 类型：{value.GetType().Name}", nameof(values))
        };
        return result;
    }
    /// <summary>数值 ID 在线路对象键中使用不受系统区域设置影响的表示。</summary>
    public static string Key(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    /// <summary>返回与 JS 稳定 sort 相同的比较排序结果。</summary>
    public static JsonArray Sorted(IEnumerable<JsonObject> values, Comparison<JsonObject> compare) =>
        Array(values.OrderBy(x => x, Comparer<JsonObject>.Create(compare)));
    /// <summary>有限整数检查，与 Number.isInteger 保持一致。</summary>
    public static bool Integer(JsonNode? value) => NullableNumber(value) is double d && Math.Truncate(d) == d;
    /// <summary>安全整数检查，限制在 JS 能精确表达的整数范围内。</summary>
    public static bool SafeInteger(JsonNode? value) => Integer(value) && Math.Abs(Num(value)) <= 9007199254740991d;
    /// <summary>按 JS Math.round 规则舍入；避免 C# 默认银行家舍入改变边界排序。</summary>
    public static double Round(double value) => Math.Floor(value + 0.5d);
}
