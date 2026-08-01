namespace MystiaStewardCompanion.Save;

internal static class RuntimeNightBusinessLifecycle
{
    public static bool IsActive { get; set; } = true;
    public static long Generation { get; set; } = 1;
}
