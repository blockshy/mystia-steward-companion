namespace MystiaStewardCompanion.Save;

internal static class RuntimeNightBusinessLifecycle
{
    public static bool IsActive { get; set; } = true;
    public static long Generation { get; set; } = 1;
    public static RuntimeNightBusinessLifecycleSnapshot Snapshot => new(IsActive, Generation);
}

internal readonly record struct RuntimeNightBusinessLifecycleSnapshot(
    bool IsActive,
    long Generation);
