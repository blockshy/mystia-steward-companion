using System.Text.Json.Nodes;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Application;

/// <summary>
/// 只读推荐展示的来源作用域。它用于判断上次成功结果能否继续显示，绝不授予执行或游戏辅助许可。
/// 全量快照中的观察时间、进度和诊断文字会持续变化，不能据此清空仍属于同一场景的展示结果。
/// </summary>
public static class BusinessDisplayContext
{
    private static readonly string[] SnapshotFields =
    {
        "automationSessionId", "nightBusinessGeneration", "nightBusinessLifecyclePhase", "activeSceneName",
        "runtimeLoaded", "runtimeDaySceneGeneration", "runtimeDaySceneReady", "runtimeDataSignature",
    };
    private static readonly string[] SpecialFields =
    {
        "active", "challengeTypeAvailable", "challengeType", "phase", "foodTargetTags", "beverageTargetTags",
        "requiredExtraIngredientIds", "yuumaFoodTargetRevision", "wackyKoishiShieldBroken",
        "wackyKoishiFoodPreferenceTags", "wackyKoishiFoodHateTags", "wackyKoishiBeveragePreferenceTags",
    };

    /// <summary>
    /// 复制明确的来源字段，缺失字段统一为 null。特殊经营保留推荐目标、阶段及破防伤害所需数值；
    /// 来源诊断、观察时刻、倒计时、怒气和符卡展示不进入作用域。偏好修订与摘要变化必须清空旧展示。
    /// </summary>
    public static JsonObject Build(JsonObject snapshot, string registryId, long authorityRevision,
        long activeProfileRevision, string activeProfileHash)
    {
        var context = new JsonObject();
        foreach (var field in SnapshotFields) context[field] = J.Clone(snapshot[field]);
        JsonObject? special = null;
        if (snapshot["specialBusiness"] is JsonObject source)
        {
            special = new JsonObject();
            foreach (var field in SpecialFields) special[field] = J.Clone(source[field]);
            foreach (var field in new[] { "currentValue", "maxValue", "targetValue" })
                special[field] = J.Bool(source["wackyKoishiShieldBroken"]) ? J.Clone(source[field]) : null;
            special["error"] = J.Bool(source["challengeTypeAvailable"]) ? null : J.Clone(source["error"]);
        }
        context["specialBusiness"] = special;
        return J.Object(("snapshot", context), ("authority", J.Object(("registryId", registryId),
            ("authorityRevision", authorityRevision), ("activeProfileRevision", activeProfileRevision),
            ("activeProfileHash", activeProfileHash))));
    }

    /// <summary>两个值均由固定字段顺序的 Build 构建；缺失上下文一律不能保留上次结果。</summary>
    public static bool Matches(JsonObject? left, JsonObject? right) => left != null && right != null
        && string.Equals(left.ToJsonString(), right.ToJsonString(), StringComparison.Ordinal);
}
