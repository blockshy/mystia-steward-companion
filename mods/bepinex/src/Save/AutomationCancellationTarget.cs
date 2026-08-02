namespace MystiaStewardCompanion.Save;

internal enum AutomationCancellationTarget
{
    Commands,
    Rare,
    Normal,
    All,
}

internal static class AutomationCancellationTargetPolicy
{
    public static bool TryParse(string value, out AutomationCancellationTarget target)
    {
        target = value switch
        {
            "commands" => AutomationCancellationTarget.Commands,
            "rare" => AutomationCancellationTarget.Rare,
            "normal" => AutomationCancellationTarget.Normal,
            "all" => AutomationCancellationTarget.All,
            _ => default,
        };
        return value is "commands" or "rare" or "normal" or "all";
    }

    public static string ToWireValue(AutomationCancellationTarget target)
    {
        return target switch
        {
            AutomationCancellationTarget.Commands => "commands",
            AutomationCancellationTarget.Rare => "rare",
            AutomationCancellationTarget.Normal => "normal",
            AutomationCancellationTarget.All => "all",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };
    }

    public static bool IncludesCookingJob(
        AutomationCancellationTarget target,
        bool rareTarget)
    {
        return target == AutomationCancellationTarget.All
            || (rareTarget
                ? target == AutomationCancellationTarget.Rare
                : target == AutomationCancellationTarget.Normal);
    }
}
