using System.Globalization;
using System.Diagnostics;
using System.Text;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 主线程阶段耗时的有界窗口。每次观测只更新托管数值，不写日志、不查询 Unity，
/// 五秒到期才排序并格式化；同一阶段的 mean/max/count/slow 来自真实调用，而非重复快照值。
/// </summary>
internal sealed class RuntimePerformanceDiagnostics
{
    // 只允许拥有窗口的主线程绑定。后台业务/HTTP 即使调用同一静态服务，也不会把耗时
    // 写入 Unity 帧窗口；所有权标记确保旧 Controller 销毁时不能清掉新实例的记录器。
    [ThreadStatic] private static object? _stageOwner;
    [ThreadStatic] private static Action<string, double>? _stageRecorder;

    internal static void BindStageRecorder(object owner, Action<string, double> recorder)
    {
        _stageOwner = owner;
        _stageRecorder = recorder;
    }

    internal static void ClearStageRecorder(object owner)
    {
        if (!ReferenceEquals(_stageOwner, owner)) return;
        _stageOwner = null;
        _stageRecorder = null;
    }

    /// <summary>
    /// 同步服务内部使用的零闭包作用域。调用方必须传固定字面量键，不跨 await/线程；
    /// 未绑定（包括日志关闭）直接返回空结构，不读取时钟、不分配对象或格式化字符串。
    /// </summary>
    internal static StageScope MeasureStage(string key) => _stageRecorder is { } recorder
        ? new StageScope(_stageOwner, recorder, key) : default;

    internal readonly struct StageScope : IDisposable
    {
        private readonly object? _owner;
        private readonly Action<string, double>? _recorder;
        private readonly string? _key;
        private readonly long _started;
        internal StageScope(object? owner, Action<string, double> recorder, string key)
        { _owner = owner; _recorder = recorder; _key = key; _started = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            // 关闭、替换或错误地跨线程释放时丢弃旧作用域；诊断 sink 抛错不能替换游戏异常，
            // 也不能让原已成功的游戏动作被误报失败或再次执行。
            if (_recorder == null || !ReferenceEquals(_stageOwner, _owner)
                || !ReferenceEquals(_stageRecorder, _recorder)) return;
            try { _recorder(_key!, (Stopwatch.GetTimestamp() - _started) * 1000d / Stopwatch.Frequency); }
            catch { /* 性能记录永远不进入游戏控制流。 */ }
        }
    }

    public const double WindowSeconds = 5;
    public const double SlowThresholdMs = 8;
    public const double FrameGapSlowThresholdMs = 50;
    // 真实全扩展窗口已有62个阶段；加入动作和周期刷新子阶段后超过旧64上限。
    // 保留固定有界容量，避免诊断后半段被截断，同时不允许动态键无限增长。
    private const int MaximumMetrics = 96;
    private readonly Dictionary<string, Metric> _metrics = new(StringComparer.Ordinal);
    private double _startedAt = double.NaN;

    public void Reset(double now)
    {
        _metrics.Clear();
        _startedAt = now;
    }

    public void Record(string key, double milliseconds)
    {
        if (string.IsNullOrEmpty(key) || !double.IsFinite(milliseconds) || milliseconds < 0) return;
        if (!_metrics.TryGetValue(key, out var metric))
        {
            if (_metrics.Count >= MaximumMetrics) return;
            metric = new Metric();
            _metrics.Add(key, metric);
        }
        metric.Count++;
        metric.TotalMs += milliseconds;
        metric.MaximumMs = Math.Max(metric.MaximumMs, milliseconds);
        // 正常 60Hz Update 间隔约 16.7ms，不能用阶段 8ms 阈值把所有帧都误报为慢帧。
        var threshold = key == "frame.updateGap" ? FrameGapSlowThresholdMs : SlowThresholdMs;
        if (milliseconds >= threshold) metric.SlowCount++;
    }

    public bool TryDrain(double now, out string report)
    {
        report = "";
        if (!double.IsFinite(now)) return false;
        if (!double.IsFinite(_startedAt) || now < _startedAt)
        {
            Reset(now);
            return false;
        }
        if (now - _startedAt < WindowSeconds) return false;
        var seconds = now - _startedAt;
        _startedAt = now;
        if (_metrics.Count == 0) return false;
        var builder = new StringBuilder();
        builder.Append("marker: MSC-PERF-WINDOW v1\nwindowSeconds: ").Append(Number(seconds))
            .Append("\nslowThresholdMs: ").Append(Number(SlowThresholdMs))
            .Append("\nframeGapSlowThresholdMs: ").Append(Number(FrameGapSlowThresholdMs));
        foreach (var item in _metrics.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var metric = item.Value;
            builder.Append("\nstage: ").Append(item.Key)
                .Append("; count=").Append(metric.Count)
                .Append("; meanMs=").Append(Number(metric.TotalMs / metric.Count))
                .Append("; maxMs=").Append(Number(metric.MaximumMs))
                .Append("; slow=").Append(metric.SlowCount);
        }
        _metrics.Clear();
        report = builder.ToString();
        return true;
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed class Metric
    {
        public long Count;
        public double TotalMs;
        public double MaximumMs;
        public long SlowCount;
    }
}

/// <summary>
/// 相邻主线程 Update 入口的墙钟间隔，只接受时戳、场景及托管代次。它能标记 Mod 外部的
/// 调度/游戏工作造成的间隔，但不是 GPU 计时或帧率归因；暂停、换场及经营代次变化后首帧不计。
/// </summary>
internal sealed class RuntimeUpdateGapSampler
{
    private long? _previousTimestamp;
    private string _scene = "";
    private long _generation;

    public void Reset() => _previousTimestamp = null;

    public bool Observe(long timestamp, long frequency, string scene, long generation, bool running, out double milliseconds)
    {
        milliseconds = 0;
        if (!running || frequency <= 0 || timestamp < 0)
        {
            Reset();
            return false;
        }
        var previous = _previousTimestamp;
        var sameContext = string.Equals(_scene, scene, StringComparison.Ordinal) && _generation == generation;
        _previousTimestamp = timestamp;
        _scene = scene;
        _generation = generation;
        if (!previous.HasValue || !sameContext || timestamp < previous.Value) return false;
        milliseconds = (timestamp - previous.Value) * 1000d / frequency;
        return true;
    }
}

/// <summary>
/// 快照诊断限频器：首条、场景/可读性/错误变化立即记录，其余发布和未变化跳过按五秒聚合。
/// 输入状态仅含安全诊断事实，不能放订单签名、脉冲值或累计计数，否则会重新退化为逐帧写盘。
/// </summary>
internal sealed class RuntimeSnapshotDiagnosticGate
{
    private string? _lastHealthState;
    private double _lastWrite = double.NaN;
    private long _publish;
    private long _forced;
    private long _unchanged;

    public void Reset()
    {
        _lastHealthState = null;
        _lastWrite = double.NaN;
        _publish = _forced = _unchanged = 0;
    }

    public bool ShouldWrite(double now, string healthState, string outcome, out string summary)
    {
        if (outcome == "skip-unchanged") _unchanged++;
        else if (outcome == "publish-forced") _forced++;
        else _publish++;
        summary = "";
        if (string.Equals(healthState, _lastHealthState, StringComparison.Ordinal)
            && double.IsFinite(now) && double.IsFinite(_lastWrite) && now >= _lastWrite
            && now - _lastWrite < RuntimePerformanceDiagnostics.WindowSeconds) return false;
        summary = $"windowOutcomes: publish={_publish}; forced={_forced}; unchanged={_unchanged}";
        _lastHealthState = healthState;
        _lastWrite = now;
        _publish = _forced = _unchanged = 0;
        return true;
    }
}
