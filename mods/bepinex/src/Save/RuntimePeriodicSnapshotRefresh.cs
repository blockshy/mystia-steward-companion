namespace MystiaStewardCompanion.Save;

/// <summary>
/// 活跃经营的周期快照失效调度。只把既有时间门禁放在可读性查询前，不改变周期，
/// 不保存可读性结果或 Unity/IL2CPP 对象；订单事件的即时失效与动作权限仍由原入口处理。
/// </summary>
internal static class RuntimePeriodicSnapshotRefresh
{
    public static bool TrySchedule(float now, ref float nextAt, float interval, Func<bool> canRead)
    {
        // 大多数帧尚未到期。此时原代码最终也会返回，却先执行主菜单 GameObject.Find；
        // 必须先返回，避免把本应每 750ms 一次的检查变成每帧全场景查找。
        if (now < nextAt) return false;
        if (!canRead()) return false;
        // 未就绪不推进截止时间：恢复的下一次调用仍即时检查，不能新增等待窗口。
        nextAt = now + interval;
        return true;
    }
}
