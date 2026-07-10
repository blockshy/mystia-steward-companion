namespace MystiaStewardCompanion.Save;

internal interface ISpecialBusinessOrderModule
{
    bool MatchesChallenge(string challengeType);

    SpecialBusinessOrderClassification Classify(
        string challengeType,
        SpecialBusinessOrderProbe guest,
        object? order,
        object? controller,
        string source);
}

internal static class SpecialBusinessModuleRegistry
{
    private static readonly IReadOnlyList<ISpecialBusinessOrderModule> Modules = new ISpecialBusinessOrderModule[]
    {
        new WackyCookingCompetitionOrderModule(),
        new YuyukoChallengeOrderModule(),
        new YuumaChallengeOrderModule(),
    };

    public static bool IsActiveChallenge(string challengeType)
    {
        return !string.IsNullOrWhiteSpace(challengeType)
            && !string.Equals(challengeType, SpecialBusinessChallengeTypes.NotChallenge, StringComparison.Ordinal);
    }

    public static SpecialBusinessOrderClassification Classify(
        string challengeType,
        SpecialBusinessOrderProbe guest,
        object? order,
        object? controller,
        string source)
    {
        foreach (var module in Modules)
        {
            if (!module.MatchesChallenge(challengeType)) continue;
            var classification = module.Classify(challengeType, guest, order, controller, source);
            if (classification != SpecialBusinessOrderClassification.Standard)
            {
                return classification;
            }
        }

        return SpecialBusinessOrderClassification.Standard;
    }

    public static SpecialBusinessOrderClassification AllowedSpecialOrder(string role, string label)
    {
        return new SpecialBusinessOrderClassification(
            AutomationAllowed: true,
            Role: role,
            RoleLabel: label,
            AutomationBlockReason: "");
    }

    public static SpecialBusinessOrderClassification Blocked(string role, string label, string reason)
    {
        return new SpecialBusinessOrderClassification(
            AutomationAllowed: false,
            Role: role,
            RoleLabel: label,
            AutomationBlockReason: reason);
    }
}
