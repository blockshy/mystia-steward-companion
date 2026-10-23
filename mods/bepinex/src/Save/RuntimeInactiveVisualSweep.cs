namespace MystiaStewardCompanion.Save;

/// <summary>
/// 非活动视觉注册表的清理时钟。仅保存时刻和托管代次，不保存游戏对象或授权结果。
/// 活动视觉的身份检查及新绑定验证必须由调用方逐帧/当场执行，不受此时钟控制。
/// </summary>
internal sealed class RuntimeInactiveVisualSweep
{
    private readonly double _intervalSeconds;
    private double _lastSweep = double.NaN;
    private long _targetGeneration;
    private long _sessionGeneration;

    public RuntimeInactiveVisualSweep(double intervalSeconds)
    {
        if (!double.IsFinite(intervalSeconds) || intervalSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
        _intervalSeconds = intervalSeconds;
    }

    public bool IsDue(double now, long targetGeneration, long sessionGeneration)
    {
        // 时钟回退、非法时刻或任何代次变化都立即清理，不把旧时钟窗口带到新经营会话。
        var due = !double.IsFinite(now) || !double.IsFinite(_lastSweep) || now < _lastSweep
            || now - _lastSweep >= _intervalSeconds
            || targetGeneration != _targetGeneration || sessionGeneration != _sessionGeneration;
        if (!due) return false;
        _lastSweep = now;
        _targetGeneration = targetGeneration;
        _sessionGeneration = sessionGeneration;
        return true;
    }
}
