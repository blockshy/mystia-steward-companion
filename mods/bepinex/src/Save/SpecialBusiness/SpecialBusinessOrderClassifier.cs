namespace MystiaStewardCompanion.Save;

internal static class SpecialBusinessOrderClassifier
{
    public static SpecialBusinessOrderClassification Classify(object? order, object? controller, string source = "")
    {
        var challengeType = RuntimeSpecialBusinessContextService.CurrentChallengeType;
        if (!SpecialBusinessModuleRegistry.IsActiveChallenge(challengeType))
        {
            return SpecialBusinessOrderClassification.Standard;
        }

        var guest = SpecialBusinessOrderProbe.Read(order, controller);
        return SpecialBusinessModuleRegistry.Classify(challengeType, guest, order, controller, source);
    }
}

internal sealed record SpecialBusinessOrderClassification(
    bool AutomationAllowed,
    string Role,
    string RoleLabel,
    string AutomationBlockReason)
{
    public static SpecialBusinessOrderClassification Standard { get; } = new(
        AutomationAllowed: true,
        Role: "",
        RoleLabel: "",
        AutomationBlockReason: "");
}
