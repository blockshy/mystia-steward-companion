namespace MystiaStewardCompanion.Save;

/// <summary>在幽幽子剧情及重修挑战中标注对应客人订单；该分类使用编号/别名探测，原生评价资格由执行侧复核。</summary>
internal sealed class YuyukoChallengeOrderModule : ISpecialBusinessOrderModule
{
    private static readonly HashSet<string> ChallengeTypes = new(StringComparer.Ordinal)
    {
        SpecialBusinessChallengeTypes.StoryYuyuko,
        SpecialBusinessChallengeTypes.RetakeYuyuko,
    };

    private static readonly HashSet<int> YuyukoGuestIds = new()
    {
        23,
        40,
    };

    public bool MatchesChallenge(string challengeType)
    {
        return ChallengeTypes.Contains(challengeType);
    }

    public SpecialBusinessOrderClassification Classify(
        string challengeType,
        object? order,
        object? controller,
        string source)
    {
        var guest = SpecialBusinessOrderProbe.Read(order, controller);
        return guest.IsGuest(YuyukoGuestIds, "Yuyuko", "幽幽子", "西行寺")
            ? SpecialBusinessModuleRegistry.AllowedSpecialOrder(
                SpecialBusinessOrderRoles.YuyukoBoss,
                GetRoleLabel(challengeType))
            : SpecialBusinessOrderClassification.Standard;
    }

    private static string GetRoleLabel(string challengeType)
    {
        return string.Equals(challengeType, SpecialBusinessChallengeTypes.StoryYuyuko, StringComparison.Ordinal)
            ? "幽幽子挑战（剧情版）订单"
            : "幽幽子挑战（重修版）订单";
    }
}
