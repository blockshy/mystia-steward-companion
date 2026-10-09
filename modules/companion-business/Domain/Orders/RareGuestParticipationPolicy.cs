using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Orders;

/// <summary>
/// 历史稀客人工参与配置的兼容安全边界。managedRareGuestIds是“新订单默认暂停、需逐单加入队列”的名单，
/// 并非允许自动执行的白名单；仅有名单不能还原经营代次内的逐单许可和队列位置。
/// 本分支未移植旧队列，因此配置启用且名单非空时暂停整个稀客执行/辅助通道，保留手动推荐和普通订单。
/// </summary>
public static class RareGuestParticipationPolicy
{
    public const string Code = "legacy-participation-queue-unavailable";
    public const string Message = "已保留旧稀客手动参与设置；本分支未实现按订单参与队列，稀客自动化/辅助已暂停，可在设置关闭该模块恢复默认自动参与。";

    /// <summary>缺失字段、关闭模块或空名单保持main原行为；非空名单不得被当成已获逐单执行许可。</summary>
    public static bool IsBlocked(JsonObject preferences) => Bool(preferences["rareGuestParticipationModuleEnabled"])
        && Arr(preferences["managedRareGuestIds"]).Count > 0;

    /// <summary>推荐、自动化和界面共用同一诊断码与解释，不在客户端重新推断历史配置的执行含义。</summary>
    public static JsonObject Diagnostic() => Object(("code", Code), ("automationBlocked", true),
        ("gameUiBlocked", true), ("message", Message));
}
