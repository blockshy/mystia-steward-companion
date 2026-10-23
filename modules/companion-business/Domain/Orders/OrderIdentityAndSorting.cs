using System.Globalization;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Orders;

/// <summary>订单纯值身份和稳定显示顺序；本地复合键不能作为游戏原生对象标识回传。</summary>
public static class OrderIdentityAndSorting
{
    private static readonly StringComparer Chinese = StringComparer.Create(CultureInfo.GetCultureInfo("zh-Hans-CN"), false);
    public static string NormalKey(JsonObject order)
    {
        var id = SpecialBusinessRules.TextOr(Str(order["orderKey"]), Str(order["traceId"])); var sequence = Num(order["orderLifecycleSequence"]);
        return sequence > 0 && id.Length > 0 ? $"{id}|lifecycle:{Key(sequence)}" : string.Join("|", "unbound", Str(order["firstSeenAtUtc"]), Key(Num(order["deskCode"])), order["runtimeGuestId"] == null ? "unknown-runtime-guest" : Key(Num(order["runtimeGuestId"])), Str(order["guestName"]), Key(Num(order["foodId"])), Key(Num(order["beverageId"])));
    }
    /// <summary>
    /// 经营展示固定按首次发现的点单时间排列，同刻使用稳定身份打破平局。
    /// 缺少合法首次时间的订单排在已知时间之后；不得使用不断变化的 lastSeenAtUtc，
    /// 否则普通刷新就会移动未知时间订单。排序只读，不补造时间或执行身份。
    /// </summary>
    public static IEnumerable<JsonObject> SortNormal(IEnumerable<JsonObject> orders) =>
        SortPresentation(orders.Select(order => (Kind: "normal", Order: order))).Select(item => item.Order);

    public static IEnumerable<JsonObject> SortRare(IEnumerable<JsonObject> orders) =>
        SortPresentation(orders.Select(order => (Kind: "rare", Order: order))).Select(item => item.Order);

    /// <summary>普通订单执行保留原先的同刻桌号/菜名次序，显示调整不改写调度竞争顺序。</summary>
    public static IEnumerable<JsonObject> SortNormalForExecution(IEnumerable<JsonObject> orders) => orders
        .OrderBy(order => Seen(order, true)).ThenBy(order => Num(order["deskCode"]))
        .ThenBy(order => Str(order["foodName"]), Chinese).ThenBy(order => Str(order["beverageName"]), Chinese);

    /// <summary>
    /// 两个业务通道共用的只读显示顺序。HTTP 据此写入 serviceOrderPosition，专注页
    /// 只合并该序号，不在客户端重新实现时间或特殊经营规则。精确重复身份保持输入稳定序。
    /// </summary>
    public static IEnumerable<(string Kind, JsonObject Order)> SortPresentation(
        IEnumerable<(string Kind, JsonObject Order)> orders) => orders
        .OrderBy(item => Seen(item.Order, true))
        .ThenBy(item => Str(item.Order["traceId"]), StringComparer.Ordinal)
        .ThenBy(item => Num(item.Order["orderLifecycleSequence"]))
        .ThenBy(item => Str(item.Order["orderKey"]), StringComparer.Ordinal)
        .ThenBy(item => item.Kind, StringComparer.Ordinal)
        .ThenBy(item => Num(item.Order["deskCode"]));

    /// <summary>
    /// 自动化与游戏辅助保留必要的特殊经营执行优先级，独立于经营显示顺序。
    /// 此入口没有用户配置分支；删掉旧客人分组后，也不能用显示序号改变写入调度。
    /// </summary>
    public static IEnumerable<JsonObject> SortRareForExecution(IEnumerable<JsonObject> orders, JsonNode? special)
    {
        return orders.OrderBy(o => o, Comparer<JsonObject>.Create((a, b) =>
        {
            var value = SpecialBusinessRules.OrderPriority(special, Str(a["specialBusinessRole"])).CompareTo(SpecialBusinessRules.OrderPriority(special, Str(b["specialBusinessRole"]))); if (value != 0) return value;
            value = Seen(a, false).CompareTo(Seen(b, false)); if (value != 0) return value;
            value = Num(a["deskCode"]).CompareTo(Num(b["deskCode"])); return value != 0 ? value : Chinese.Compare(Str(a["guestName"]), Str(b["guestName"]));
        }));
    }
    private static double Seen(JsonObject order, bool normal)
    { var text = Str(order["firstSeenAtUtc"] ?? (normal ? null : order["lastSeenAtUtc"])); return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.ToUnixTimeMilliseconds() : 9007199254740991d; }
}
