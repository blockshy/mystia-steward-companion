namespace MystiaStewardCompanion.Save;

internal readonly record struct RareGuestInvitationWriteExpectation(
    long DaySceneGeneration,
    string MapLabel);

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
