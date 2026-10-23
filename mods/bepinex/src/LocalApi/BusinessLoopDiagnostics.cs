using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>固定阶段枚举，禁止把订单 ID 或异常文字当成诊断键导致无界增长。</summary>
internal enum BusinessLoopStage { Capture, Payload, Recommendations, Bind, Automation, Targets, Publish, Page, Commands, Iteration }

/// <summary>
/// 唯一后台业务循环使用的有界分配/耗时窗口。不保存 JSON、订单身份或 Unity 对象，不写日志。
/// 阶段作用域必须在同一同步线程内结束，不能跨 await；业务循环等待前先完成本轮记录。
/// 线程分配仅表示这段后台工作的 CLR 分配，不能将其解释为游戏帧率或 GC 停顿因果。
/// </summary>
internal sealed class BusinessLoopDiagnostics
{
    private readonly Metric[] _metrics = new Metric[10];
    private bool _enabled;
    private long _windowStart;
    private long _iterations, _reused, _stale, _failed, _hits, _misses;
    private struct Metric { public long Count, Bytes, MaxBytes; public double Milliseconds, MaxMilliseconds; }

    internal void Configure(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        Reset(Stopwatch.GetTimestamp());
    }

    internal Scope Measure(BusinessLoopStage stage) => _enabled ? new Scope(this, stage) : default;

    internal readonly struct Scope : IDisposable
    {
        private readonly BusinessLoopDiagnostics? _owner;
        private readonly BusinessLoopStage _stage;
        private readonly long _started, _allocated;
        internal Scope(BusinessLoopDiagnostics owner, BusinessLoopStage stage)
        { _owner = owner; _stage = stage; _started = Stopwatch.GetTimestamp(); _allocated = GC.GetAllocatedBytesForCurrentThread(); }
        public void Dispose()
        {
            if (_owner == null) return;
            _owner.Record(_stage, (Stopwatch.GetTimestamp() - _started) * 1000d / Stopwatch.Frequency,
                GC.GetAllocatedBytesForCurrentThread() - _allocated);
        }
    }

    // 数值入口也供纯托管测试注入确定计数，不使用实际睡眠或触发强制 GC。
    internal void Record(BusinessLoopStage stage, double milliseconds, long bytes)
    {
        if (!_enabled || !double.IsFinite(milliseconds) || milliseconds < 0 || bytes < 0) return;
        ref var metric = ref _metrics[(int)stage];
        metric.Count++; metric.Bytes += bytes; metric.MaxBytes = Math.Max(metric.MaxBytes, bytes);
        metric.Milliseconds += milliseconds; metric.MaxMilliseconds = Math.Max(metric.MaxMilliseconds, milliseconds);
    }

    internal void Complete(bool reused, bool stale, bool failed, long hits, long misses)
    {
        if (!_enabled) return;
        _iterations++; if (reused) _reused++; if (stale) _stale++; if (failed) _failed++;
        _hits += Math.Max(0, hits); _misses += Math.Max(0, misses);
    }

    internal bool TryDrain(long timestamp, long frequency, out string report)
    {
        report = "";
        if (!_enabled || frequency <= 0) return false;
        if (timestamp < _windowStart) { Reset(timestamp); return false; }
        var seconds = (timestamp - _windowStart) / (double)frequency;
        if (seconds < 5 || _iterations == 0) return false;
        var text = new StringBuilder("marker: MSC-BUSINESS-WINDOW v1\nwindowSeconds: ");
        text.Append(Number(seconds)).Append("\niterations: ").Append(_iterations)
            .Append("\norderResultReused: ").Append(_reused).Append("\nstale: ").Append(_stale)
            .Append("\nfailed: ").Append(_failed).Append("\ncandidateCacheHits: ").Append(_hits)
            .Append("\ncandidateCacheMisses: ").Append(_misses);
        for (var index = 0; index < _metrics.Length; index++)
        {
            var metric = _metrics[index]; if (metric.Count == 0) continue;
            text.Append("\nstage: ").Append((BusinessLoopStage)index).Append("; count=").Append(metric.Count)
                .Append("; meanMs=").Append(Number(metric.Milliseconds / metric.Count))
                .Append("; maxMs=").Append(Number(metric.MaxMilliseconds)).Append("; allocatedBytes=").Append(metric.Bytes)
                .Append("; maxAllocatedBytes=").Append(metric.MaxBytes);
        }
        text.Append("\nscope: synchronous business-loop CLR allocations; nested stages overlap; commands may wait for Unity; not FPS or GC causation");
        report = text.ToString(); Reset(timestamp); return true;
    }

    private void Reset(long now)
    { Array.Clear(_metrics, 0, _metrics.Length); _windowStart = now; _iterations = _reused = _stale = _failed = _hits = _misses = 0; }
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
