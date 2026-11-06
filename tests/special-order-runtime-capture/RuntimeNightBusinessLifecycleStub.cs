namespace MystiaStewardCompanion.Save;

/// <summary>
/// 由测试显式设置经营状态和代次，使订单采集可以独立验证跨经营边界的失效规则。
/// 本替身不安装经营生命周期 Hook，也不模拟真实场景切换。
/// </summary>
internal static class RuntimeNightBusinessLifecycle
{
    public static bool IsActive { get; set; } = true;
    public static long Generation { get; set; } = 1;
    public static RuntimeNightBusinessLifecycleSnapshot Snapshot => new(IsActive, Generation);
}

internal readonly record struct RuntimeNightBusinessLifecycleSnapshot(
    bool IsActive,
    long Generation);
