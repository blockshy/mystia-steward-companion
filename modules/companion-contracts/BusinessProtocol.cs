using System.Text.Json;
using System.Text.Json.Nodes;

namespace MystiaStewardCompanion.Contracts;

/// <summary>
/// 业务计算协议的唯一版本与边界。协议只交换托管 JSON 值，不能携带 Unity 对象或运行时句柄。
/// 客户端提交查询意图；目录、库存、订单和生效配置始终由游戏宿主补齐。
/// </summary>
public static class BusinessProtocol
{
    public const int Version = 1;
    public const int MaxPageQueries = 16;
    public const int MaxQueryTextLength = 100;

    /// <summary>HTTP 信封必须是深度不超过16的 JSON 对象；null、数组和过深输入在登记前明确拒绝。</summary>
    public static JsonObject ParsePageQuery(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 16 });
        if (node is not JsonObject request) throw new ArgumentException("推荐查询必须为 JSON 对象。");
        return ValidatePageQuery(request);
    }

    /// <summary>严格验证小型查询信封，防止客户端把自行计算的目标或伪造的游戏快照送入业务核心。</summary>
    public static JsonObject ValidatePageQuery(JsonObject request)
    {
        if (request["protocolVersion"] is not JsonValue versionValue
            || !versionValue.TryGetValue<int>(out var version) || version != Version)
            throw new ArgumentException("业务协议版本不兼容，请更新伴随客户端。");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
            { "protocolVersion", "kind", "selectedPlace", "customerId", "foodTag", "beverageTag" };
        if (request.Any(field => !allowed.Contains(field.Key)))
            throw new ArgumentException("推荐查询包含不允许的字段。");
        if (request["kind"] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var kind)
            || kind is not ("normal" or "rare")) throw new ArgumentException("推荐查询类型无效。");
        foreach (var key in new[] { "selectedPlace", "foodTag", "beverageTag" })
            if (request.ContainsKey(key) && (request[key] is not JsonValue value
                || !value.TryGetValue<string>(out var text) || text.Length > MaxQueryTextLength))
                throw new ArgumentException("推荐查询文本必须为不超过100字符的字符串。");
        if (kind == "rare" && (request["customerId"] is not JsonValue id || !id.TryGetValue<int>(out var number) || number < 0))
            throw new ArgumentException("稀客查询缺少有效客人编号。");
        return JsonNode.Parse(request.ToJsonString())!.AsObject();
    }
}
