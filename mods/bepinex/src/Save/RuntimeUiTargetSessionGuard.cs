namespace MystiaStewardCompanion.Save;

/// <summary>
/// 夜间游戏目标发布的纯托管会话边界。空目标集合也属于发布，必须处于同一代次的 Active；
/// 白天推荐只能跳过发布，不能借空集合绕过此校验。生产发布入口与离线宿主共用本实现。
/// </summary>
internal static class RuntimeUiTargetSessionGuard
{
    public static void Validate(long sessionGeneration, NightBusinessLifecycleSnapshot lifecycle)
    {
        if (lifecycle.IsActive && sessionGeneration > 0 && sessionGeneration == lifecycle.Generation) return;

        throw new InvalidOperationException(
            $"Night-business UI target rejected: requested generation={sessionGeneration}, current generation={lifecycle.Generation}, phase={lifecycle.Phase}.");
    }
}
