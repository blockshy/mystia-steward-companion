namespace MystiaStewardCompanion.Save;

internal sealed class MizuchiOrderModule : ISpecialBusinessOrderModule
{
    public bool MatchesChallenge(string challengeType)
    {
        return SpecialBusinessChallengeTypes.IsMizuchiChallenge(challengeType);
    }

    public SpecialBusinessOrderClassification Classify(
        string challengeType,
        object? order,
        object? controller,
        string source)
    {
        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        if (resolution.Resolved && resolution.Kind == RuntimeOrderKind.Normal)
        {
            return SpecialBusinessOrderClassification.Standard;
        }

        var isStory = string.Equals(
            challengeType,
            SpecialBusinessChallengeTypes.StoryMizuchi,
            StringComparison.Ordinal);
        var identity = MizuchiOrderIdentity.Read(challengeType, order, controller);
        if (!identity.Verified)
        {
            var blocked = SpecialBusinessModuleRegistry.Blocked(
                isStory
                    ? SpecialBusinessOrderRoles.MizuchiStoryUnverified
                    : SpecialBusinessOrderRoles.MizuchiTrialUnverified,
                isStory ? "寻找瑞灵踪迹订单待确认" : "月都试炼订单待确认",
                $"{(isStory ? "寻找瑞灵踪迹" : "月都试炼")}订单身份无法精确确认，已阻止自动化接管：{identity.Reason}");
            SpecialBusinessDiagnostics.AppendMizuchiOrderClassification(
                challengeType,
                blocked,
                identity,
                order,
                controller,
                source);
            return blocked;
        }

        var role = (isStory, identity.IsPossessed) switch
        {
            (true, true) => SpecialBusinessOrderRoles.MizuchiStoryPossessed,
            (true, false) => SpecialBusinessOrderRoles.MizuchiStoryOrdinary,
            (false, true) => SpecialBusinessOrderRoles.MizuchiTrialPossessed,
            _ => SpecialBusinessOrderRoles.MizuchiTrialOrdinary,
        };
        var label = (isStory, identity.IsPossessed) switch
        {
            (true, true) => "寻找瑞灵踪迹 · 瑞灵附身订单",
            (true, false) => "寻找瑞灵踪迹 · 普通稀客订单",
            (false, true) => "月都试炼 · 瑞灵附身订单",
            _ => "月都试炼 · 普通稀客订单",
        };
        var classification = SpecialBusinessModuleRegistry.AllowedSpecialOrder(
            role,
            label,
            identity.OrderGuestId);
        SpecialBusinessDiagnostics.AppendMizuchiOrderClassification(
            challengeType,
            classification,
            identity,
            order,
            controller,
            source);
        return classification;
    }
}
