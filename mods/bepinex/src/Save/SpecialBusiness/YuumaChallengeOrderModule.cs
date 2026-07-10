namespace MystiaStewardCompanion.Save;

internal sealed class YuumaChallengeOrderModule : ISpecialBusinessOrderModule
{
    private const string ChallengeType = "Story_BloodPondHell";
    private const int YuumaBossGuestId = 1003;

    public bool MatchesChallenge(string challengeType)
    {
        return string.Equals(challengeType, ChallengeType, StringComparison.Ordinal);
    }

    public SpecialBusinessOrderClassification Classify(
        string challengeType,
        SpecialBusinessOrderProbe guest,
        object? order,
        object? controller,
        string source)
    {
        return guest.IsGuest(YuumaBossGuestId, "Yuuma", "Toutetsu", "饕餮", "尤魔")
            ? SpecialBusinessModuleRegistry.Blocked(
                SpecialBusinessOrderRoles.YuumaBoss,
                "饕餮尤魔挑战订单",
                "饕餮尤魔挑战订单需要走原生怒气和伤害评价流程，已阻止标准自动化接管。")
            : SpecialBusinessOrderClassification.Standard;
    }
}
