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
    public static IEnumerable<JsonObject> SortNormal(IEnumerable<JsonObject> orders) => orders.OrderBy(o => Seen(o, true)).ThenBy(o => Num(o["deskCode"])).ThenBy(o => Str(o["foodName"]), Chinese).ThenBy(o => Str(o["beverageName"]), Chinese);
    public static IEnumerable<JsonObject> SortRare(IEnumerable<JsonObject> orders, string mode, JsonNode? special)
    {
        var list = orders.ToList(); var first = list.GroupBy(GroupKey).ToDictionary(x => x.Key, x => x.Min(y => Seen(y, false)), StringComparer.Ordinal);
        return list.OrderBy(o => o, Comparer<JsonObject>.Create((a, b) =>
        {
            var value = SpecialBusinessRules.OrderPriority(special, Str(a["specialBusinessRole"])).CompareTo(SpecialBusinessRules.OrderPriority(special, Str(b["specialBusinessRole"]))); if (value != 0) return value;
            if (mode == "guest" && GroupKey(a) != GroupKey(b))
            {
                value = first[GroupKey(a)].CompareTo(first[GroupKey(b)]); if (value != 0) return value;
                value = Chinese.Compare(Str(a["guestName"]), Str(b["guestName"])); if (value != 0) return value;
                value = Num(a["guestId"], 9007199254740991d).CompareTo(Num(b["guestId"], 9007199254740991d)); if (value != 0) return value;
                value = Num(a["deskCode"]).CompareTo(Num(b["deskCode"])); if (value != 0) return value;
            }
            value = Seen(a, false).CompareTo(Seen(b, false)); if (value != 0) return value;
            value = Num(a["deskCode"]).CompareTo(Num(b["deskCode"])); return value != 0 ? value : Chinese.Compare(Str(a["guestName"]), Str(b["guestName"]));
        }));
    }
    private static string GroupKey(JsonObject o) => NullableNumber(o["guestId"]) is double id && id >= 0 ? $"id:{Key(id)}" : $"name:{Str(o["guestName"]).Trim()}|desk:{Key(Num(o["deskCode"]))}";
    private static double Seen(JsonObject order, bool normal)
    { var text = Str(order["firstSeenAtUtc"] ?? (normal ? null : order["lastSeenAtUtc"])); return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.ToUnixTimeMilliseconds() : 9007199254740991d; }
}
