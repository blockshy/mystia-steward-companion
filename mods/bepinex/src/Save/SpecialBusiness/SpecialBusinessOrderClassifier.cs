namespace MystiaStewardCompanion.Save;

internal static class SpecialBusinessOrderClassifier
{
    public static SpecialBusinessOrderClassification Classify(object? order, object? controller, string source = "")
    {
        if (!RuntimeSpecialBusinessContextService.TryGetCurrentChallengeType(out var challengeType, out var error))
        {
            return SpecialBusinessModuleRegistry.Blocked(
                SpecialBusinessOrderRoles.ContextUnavailable,
                "特殊经营状态待确认",
                $"暂时无法读取游戏的特殊经营类型，已暂停自动化。详细原因：{error}");
        }

        if (!SpecialBusinessModuleRegistry.IsActiveChallenge(challengeType))
        {
            return SpecialBusinessOrderClassification.Standard;
        }

        return SpecialBusinessModuleRegistry.Classify(challengeType, order, controller, source);
    }
}

internal sealed record SpecialBusinessOrderClassification(
    bool AutomationAllowed,
    string Role,
    string RoleLabel,
    string AutomationBlockReason,
    int? RuntimeGuestId)
{
    public static SpecialBusinessOrderClassification Standard { get; } = new(
        AutomationAllowed: true,
        Role: "",
        RoleLabel: "",
        AutomationBlockReason: "",
        RuntimeGuestId: null);
}
