using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Orders;

/// <summary>
/// 对权威调度快照进行一次完整集合校验。暂停不改变订单采集、只读展示或推荐候选；只有
/// 自动化执行与原生游戏辅助应用此参与过滤。客户端不计算队列位置，且不能仅跳过坏行后继续执行。
/// </summary>
public static class RareOrderParticipationProjection
{
    public static bool IsEnabled(JsonObject preferences) => Bool(preferences["rareGuestParticipationModuleEnabled"])
        && preferences["managedRareGuestIds"] is JsonArray { Count: > 0 };

    /// <summary>模块关闭或有效名单为空时不建立额外条件；启用时任何集合/身份歧义均返回 false。</summary>
    public static bool TryReadPositions(JsonObject snapshot, JsonObject preferences, out Dictionary<string, int>? positions)
    {
        positions = null;
        if (!IsEnabled(preferences)) return true;
        var generation = Num(snapshot["nightBusinessGeneration"]);
        if (generation <= 0 || snapshot["nightBusiness"] is not JsonObject night
            || !Bool(night["ordersReadComplete"]) || !string.IsNullOrEmpty(Str(night["error"]))
            || night["orders"] is not JsonArray orders
            || snapshot["rareGuestParticipation"] is not JsonObject state || !Bool(state["active"])
            || Num(state["businessGeneration"]) != generation || Num(state["participationRevision"]) <= 0
            || state["entries"] is not JsonArray entries || state["managedGuestIds"] is not JsonArray managed
            || preferences["managedRareGuestIds"] is not JsonArray configured
            || !managed.ToJsonString().Equals(configured.ToJsonString(), StringComparison.Ordinal)
            || entries.Count != orders.Count) return false;
        var configuredIds = new HashSet<double>();
        var previous = -1d;
        foreach (var id in configured)
        {
            if (id is not JsonValue value || !value.TryGetValue<int>(out var number) || number < 0 || number <= previous)
                return false;
            previous = number; configuredIds.Add(number);
        }
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in orders)
        {
            if (node is not JsonObject order || !TryIdentityKey(order, out var key) || !current.Add(key)) return false;
        }
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in entries)
        {
            if (node is not JsonObject entry || !TryIdentityKey(entry, out var key) || !current.Contains(key) || !seen.Add(key)
                || entry["managed"] is not JsonValue managedValue || !managedValue.TryGetValue<bool>(out var isManaged)
                || isManaged != configuredIds.Contains(Num(entry["guestId"]))
                || entry["participating"] is not JsonValue participantValue || !participantValue.TryGetValue<bool>(out var participating)) return false;
            if (!participating)
            {
                if (!isManaged || entry["queuePosition"] != null) return false;
                continue;
            }
            if (entry["queuePosition"] is not JsonValue positionValue || !positionValue.TryGetValue<int>(out var position)
                || position <= 0) return false;
            result.Add(key, position);
        }
        if (!result.Values.OrderBy(position => position).SequenceEqual(Enumerable.Range(1, result.Count))) return false;
        positions = result;
        return true;
    }

    /// <summary>返回完整校验后的参与订单；参数数组仍包含暂停订单，不能将返回值写回原始快照。</summary>
    public static IEnumerable<JsonObject> FilterOrders(IEnumerable<JsonObject> orders, JsonObject snapshot, JsonObject preferences)
    {
        if (!TryReadPositions(snapshot, preferences, out var positions)) return System.Array.Empty<JsonObject>();
        if (positions == null) return orders;
        return orders.Where(order => TryIdentityKey(order, out var key) && positions.ContainsKey(key))
            .OrderBy(order => positions[IdentityKey(order)]);
    }

    /// <summary>特殊经营安全优先级仍优先于人工队列；缺材料的队首由计划器跳过，不阻塞下一订单。</summary>
    public static IEnumerable<JsonObject> SortForExecution(IEnumerable<JsonObject> orders, JsonObject snapshot, JsonObject preferences)
    {
        if (!IsEnabled(preferences)) return OrderIdentityAndSorting.SortRareForExecution(orders, snapshot["specialBusiness"]);
        return FilterOrders(orders, snapshot, preferences).OrderBy(order =>
            SpecialBusinessRules.OrderPriority(snapshot["specialBusiness"], Str(order["specialBusinessRole"])));
    }

    /// <summary>只使用公开精确身份。规范 guestId 必须存在；名字、桌位、runtimeGuestId 均不能补缺。</summary>
    public static bool TryIdentityKey(JsonObject order, out string key)
    {
        key = "";
        var trace = Str(order["traceId"]);
        if (trace.Length < 3 || !trace.StartsWith("R-", StringComparison.Ordinal)
            || trace[2..].Any(c => c < '0' || c > '9')
            || !SafeInteger(order["orderLifecycleSequence"]) || Num(order["orderLifecycleSequence"]) <= 0
            || !SafeInteger(order["guestId"]) || Num(order["guestId"]) < 0 || Num(order["guestId"]) > int.MaxValue) return false;
        // 冻结 JSON 可能保留 Int32 / Int64 或整数 Double；判断数值语义而非 CLR 泛型，
        // 防止同一合法身份只因复制路径不同而被误判为无法参与。
        key = $"{trace}|{(long)Num(order["orderLifecycleSequence"])}|{(int)Num(order["guestId"])}";
        return true;
    }

    public static string IdentityKey(JsonObject order) => TryIdentityKey(order, out var key) ? key : "";
}
