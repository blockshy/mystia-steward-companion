using System.Text.Json.Nodes;

namespace MystiaStewardCompanion.Contracts;

/// <summary>
/// Flutter 产品客户端协议。版本 2 的人工意图必须携带进程会话和经营代次，
/// 防止持久化权限修订未变、但进程内事件序号重用时确认了另一场经营的安全屏障。
/// 本协议不携带执行步骤；游戏行为仍由宿主和业务编排器决定。
/// </summary>
public static class ClientProtocol
{
    public const int Version = 2;

    /// <summary>
    /// 校验人工意图的不可变来源。空会话、非 Active、旧代次或旧协议全部拒绝，
    /// 不把缺失字段转成当前快照，也不保留旧版仅 sequence 的兼容入口。
    /// </summary>
    public static void ValidateInteractionScope(int protocolVersion, string automationSessionId,
        long businessGeneration, JsonObject snapshot)
    {
        if (protocolVersion != Version)
            throw new ArgumentException("客户端操作协议不兼容，请更新完整客户端与 Mod。");
        if (string.IsNullOrWhiteSpace(automationSessionId) || automationSessionId.Length > 128
            || businessGeneration <= 0)
            throw new ArgumentException("人工操作缺少有效游戏会话或经营代次。");
        if (snapshot["automationSessionId"] is not JsonValue session
            || !session.TryGetValue<string>(out var currentSession)
            || !string.Equals(currentSession, automationSessionId, StringComparison.Ordinal)
            || snapshot["nightBusinessGeneration"] is not JsonValue generation
            || !generation.TryGetValue<long>(out var currentGeneration) || currentGeneration != businessGeneration
            || snapshot["nightBusinessLifecyclePhase"] is not JsonValue phase
            || !phase.TryGetValue<string>(out var currentPhase) || currentPhase != "Active")
            throw new ArgumentException("人工操作所属游戏会话或经营代次已失效，请读取最新状态后重新确认。");
    }
}
