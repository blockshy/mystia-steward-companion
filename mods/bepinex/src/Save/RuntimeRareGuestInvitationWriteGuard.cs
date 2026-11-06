namespace MystiaStewardCompanion.Save;

internal readonly record struct RareGuestInvitationWriteExpectation(
    long DaySceneGeneration,
    string MapLabel);

/// <summary>在邀请写入前比较候选读取时的日间代次和地图，拒绝跨场景的迟到请求。</summary>
/// <remarks>此检查只证明场景上下文一致；候选身份、邀请次数及执行权限仍由调用方验证。</remarks>
internal static class RuntimeRareGuestInvitationWriteGuard
{
    public static bool Matches(
        RareGuestInvitationWriteExpectation expectation,
        long actualDaySceneGeneration,
        string? actualMapLabel,
        out string reason)
    {
        reason = "";
        var expectedMapLabel = expectation.MapLabel?.Trim() ?? "";
        if (expectation.DaySceneGeneration < 1 || expectedMapLabel.Length == 0)
        {
            reason = "邀请请求缺少有效的日间场景身份，请刷新候选后重试。";
            return false;
        }

        if (actualDaySceneGeneration != expectation.DaySceneGeneration)
        {
            reason = "日间场景已变化，本次邀请未执行。请刷新候选后重试。";
            return false;
        }

        var currentMapLabel = actualMapLabel?.Trim() ?? "";
        if (!string.Equals(currentMapLabel, expectedMapLabel, StringComparison.Ordinal))
        {
            reason = "当前地图已变化，本次邀请未执行。请刷新候选后重试。";
            return false;
        }

        return true;
    }
}
