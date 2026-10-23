using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Orders;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>
/// HTTP 读取端的订单显示投影。只重排当前快照的订单数组，不参与推荐求解、动作许可或原始
/// 快照签名计算；新订单按固定点单时间显示，无需等待可能耗时的后台组合搜索。
/// </summary>
internal sealed class LocalApiSnapshotPresentation
{
    // 该锁只由 HTTP 读取线程访问；Unity 的 SetSnapshotJson 和后台业务循环均不取得此锁。
    // 单槽保留最新一次读取结果，避免多个轮询客户端对相同正文反复解析、排序和序列化。
    private readonly object _lock = new();
    private string? _source;
    private Result? _cached;

    internal sealed record Result(string Json, string Signature);

    /// <summary>展示令牌与业务快照签名分开；它不是权限凭据，只供客户端判断显示顺序是否变化。</summary>
    internal static string Signature(string snapshotSignature) => $"order-v2:time:{snapshotSignature}";

    internal Result Get(string snapshotJson, string snapshotSignature)
    {
        var signature = Signature(snapshotSignature);
        lock (_lock)
        {
            if (_cached != null && _source == snapshotJson && _cached.Signature == signature)
                return _cached;

            using var document = JsonDocument.Parse(snapshotJson);
            var root = document.RootElement;
            // 只把少量订单转为领域入口需要的 JsonNode。目录、库存、事件等大字段由
            // JsonElement 原样写回，不创建整棵可变 JSON 树，也绝不覆盖原 snapshotSignature。
            var rareOrders = ReadOrders(root, "nightBusiness");
            var normalOrders = ReadOrders(root, "normalBusiness");
            var ordered = OrderIdentityAndSorting.SortPresentation(
                (rareOrders ?? Array.Empty<JsonObject>()).Select(order => (Kind: "rare", Order: order))
                .Concat((normalOrders ?? Array.Empty<JsonObject>()).Select(order => (Kind: "normal", Order: order)))).ToArray();
            // 专注页合并普/稀客时只读此顺位，不需要把 C# 时间排序移回客户端。
            // 这是本次 HTTP 投影的新节点，不修改原始快照；顺位不是动作身份或许可。
            for (var index = 0; index < ordered.Length; index++)
                ordered[index].Order["serviceOrderPosition"] = index;
            var rareSorted = ordered.Where(item => item.Kind == "rare").Select(item => item.Order).ToArray();
            var normalSorted = ordered.Where(item => item.Kind == "normal").Select(item => item.Order).ToArray();
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    // 本字段仅由 HTTP 显示层拥有，不能把未来误带入的旧展示令牌继续透传。
                    if (property.NameEquals("orderPresentationSignature")) continue;
                    writer.WritePropertyName(property.Name);
                    if (property.NameEquals("nightBusiness")) WriteBusiness(writer, property.Value, rareOrders == null ? null : rareSorted);
                    else if (property.NameEquals("normalBusiness")) WriteBusiness(writer, property.Value, normalOrders == null ? null : normalSorted);
                    else property.Value.WriteTo(writer);
                }
                writer.WriteString("orderPresentationSignature", signature);
                writer.WriteEndObject();
            }
            var result = new Result(Encoding.UTF8.GetString(buffer.ToArray()), signature);
            _source = snapshotJson;
            _cached = result;
            return result;
        }
    }

    /// <summary>错误或不完整读取不能通过排序转换成有效事实，原始形状必须继续交给客户端。</summary>
    private static JsonObject[]? ReadOrders(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var business) || business.ValueKind != JsonValueKind.Object) return null;
        if (business.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && (error.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(error.GetString()))) return null;
        if (!business.TryGetProperty("orders", out var orders) || orders.ValueKind != JsonValueKind.Array
            || orders.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Object)) return null;
        return orders.EnumerateArray().Select(value => JsonNode.Parse(value.GetRawText())!.AsObject()).ToArray();
    }

    private static void WriteBusiness(Utf8JsonWriter writer, JsonElement business, IReadOnlyList<JsonObject>? sorted)
    {
        // 未加载、错误和缺失状态保持原始形状；不能把 null/非法集合改成可用的空订单列表。
        if (sorted == null) { business.WriteTo(writer); return; }
        writer.WriteStartObject();
        foreach (var property in business.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (!property.NameEquals("orders"))
            {
                property.Value.WriteTo(writer);
                continue;
            }
            writer.WriteStartArray();
            foreach (var order in sorted) order.WriteTo(writer);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}
