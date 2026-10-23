using System.Globalization;
using System.Text;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 相邻 Update 入口与前一帧 Mod 工作的有界关联。调用方只在聚合日志开启时使用，
/// 传入同一 Stopwatch 时基的数值、静态阶段名及托管场景/代次，不传 Unity 对象。
/// 常态观测只更新字段和八个固定结构槽，不格式化、不写盘、不读取 GC 或系统时间。
/// </summary>
internal sealed class RuntimeFrameStallDiagnostics
{
    public const int MaximumSamples = 8;
    public const double WindowSeconds = 5;
    public const double DistributionThresholdMs = 16.667;
    public const double SampleThresholdMs = 33.333;
    public const double LongGapThresholdMs = 50;

    private readonly Sample[] _samples = new Sample[MaximumSamples];
    private int _sampleCount;
    private long _intervalCount;
    private long _count16;
    private long _count33;
    private long _count50;
    private long _incompleteUpdateCount;
    private bool _hasWindow;
    private long _windowStartedAt;
    private long _frequency;
    private string _scene = "";
    private long _generation;
    private bool _hasFrame;
    private long _frameSequence;
    private Frame _frame;

    /// <summary>
    /// 日志停用、暂停、换场或经营代次改变时清除旧帧和未输出窗口。
    /// 不能把暂停时间或两个不同经营会话拼成一个慢帧；恢复第一帧不产生间隔。
    /// </summary>
    public void Reset()
    {
        Array.Clear(_samples, 0, _samples.Length);
        _sampleCount = 0;
        _intervalCount = _count16 = _count33 = _count50 = _incompleteUpdateCount = 0;
        _hasWindow = _hasFrame = false;
        _windowStartedAt = _frequency = _generation = _frameSequence = 0;
        _scene = "";
        _frame = default;
    }

    /// <summary>
    /// 在当前 Update 入口调用，消费已经结束的上一帧。gc0/1/2 为此入口读取的
    /// GC.CollectionCount：差值属于整个 CLR 进程，仅提供相关性，不证明 GC 导致停顿。
    /// scene/generation/running 必须来自已有托管状态，不能为此额外遍历游戏对象。
    /// </summary>
    public void BeginFrame(long timestamp, long frequency, string scene, long generation, bool running,
        int gc0, int gc1, int gc2)
    {
        if (!running || timestamp < 0 || frequency <= 0 || string.IsNullOrWhiteSpace(scene)
            || gc0 < 0 || gc1 < 0 || gc2 < 0)
        {
            Reset();
            return;
        }

        if (_hasWindow && (_frequency != frequency || !string.Equals(_scene, scene, StringComparison.Ordinal)
            || _generation != generation || timestamp < _windowStartedAt
            || (_hasFrame && (timestamp < _frame.Timestamp || gc0 < _frame.Gc0 || gc1 < _frame.Gc1 || gc2 < _frame.Gc2))))
            Reset();

        if (!_hasWindow)
        {
            _hasWindow = true;
            _windowStartedAt = timestamp;
            _frequency = frequency;
            _scene = scene;
            _generation = generation;
        }

        if (_hasFrame)
        {
            var gapMs = (timestamp - _frame.Timestamp) * 1000d / frequency;
            _intervalCount++;
            if (gapMs >= DistributionThresholdMs) _count16++;
            if (gapMs >= LongGapThresholdMs) _count50++;
            if (!_frame.UpdateObserved) _incompleteUpdateCount++;
            if (gapMs >= SampleThresholdMs)
            {
                _count33++;
                // 缺少前一帧完整 Update 时仍保留间隔分布，但不能生成伪完整关联样本。
                // droppedCount 会包含这些未保留间隔，另报告 incompleteUpdateCount 解释缺口。
                if (_frame.UpdateObserved)
                    KeepSlowest(new Sample(timestamp, _frame.Sequence, gapMs, _frame.UpdateMs,
                        _frame.LateUpdateMs, _frame.UpdateAllocatedBytes, _frame.LateUpdateAllocatedBytes,
                        _frame.LateUpdateCalls, _frame.SlowestStage, _frame.SlowestStageMs,
                        gc0 - _frame.Gc0, gc1 - _frame.Gc1, gc2 - _frame.Gc2));
            }
        }

        _hasFrame = true;
        _frame = new Frame
        {
            Timestamp = timestamp,
            Sequence = ++_frameSequence,
            Gc0 = gc0,
            Gc1 = gc1,
            Gc2 = gc2,
        };
    }

    /// <summary>
    /// 当前 Update 的最外层 finally 最后调用，elapsedMs/allocatedBytes 应包含诊断
    /// 汇总及同步日志 flush；分配量是当前线程在这段 Mod 范围的计数差，不是整帧总分配。
    /// 调用方应在任何观察器开始工作前取起点，避免把诊断自身成本漏出完整 Update。
    /// </summary>
    public void RecordUpdate(double elapsedMs, long allocatedBytes)
    {
        if (!_hasFrame || !double.IsFinite(elapsedMs) || elapsedMs < 0 || allocatedBytes < 0) return;
        _frame.UpdateObserved = true;
        _frame.UpdateMs = elapsedMs;
        _frame.UpdateAllocatedBytes = allocatedBytes;
    }

    /// <summary>
    /// 当前帧 LateUpdate 的最外层 finally 调用。未调用时样本明确 lateUpdateCalls=0，
    /// 不把“未观察到”伪装成已经测得零耗时；意外多次调用按实际范围累计。
    /// </summary>
    public void RecordLateUpdate(double elapsedMs, long allocatedBytes)
    {
        if (!_hasFrame || !double.IsFinite(elapsedMs) || elapsedMs < 0 || allocatedBytes < 0) return;
        _frame.LateUpdateCalls++;
        _frame.LateUpdateMs += elapsedMs;
        _frame.LateUpdateAllocatedBytes += allocatedBytes;
    }

    /// <summary>
    /// 记录当前帧最慢业务阶段，仅保留最大值，不相加嵌套阶段。调用方传静态键，
    /// 精确排除整帧总量及已完整拆分的两个父容器，避免父耗时必然覆盖其中动作。
    /// 其它边界（例如 snapshot.publish）仍可含尚未细分的工作，不按前缀一概丢弃。
    /// 阶段可能包含等待、反射、同步日志或原生调用，其计时不代表纯 CPU 执行时间。
    /// </summary>
    public void RecordStage(string staticStage, double elapsedMs)
    {
        if (!_hasFrame || string.IsNullOrEmpty(staticStage)
            || staticStage is "main.update" or "main.lateUpdate" or "frame.updateGap"
                or "main.orderPreparations" or "main.orderCaptureRefresh"
            || !double.IsFinite(elapsedMs)
            || elapsedMs < 0 || elapsedMs <= _frame.SlowestStageMs) return;
        _frame.SlowestStage = staticStage;
        _frame.SlowestStageMs = elapsedMs;
    }

    /// <summary>
    /// 每五秒最多格式化八条最慢入口间隔。timestamp/frequency 必须继续使用 BeginFrame
    /// 的 Stopwatch 时基，不能混入 Unity realtime 或系统 UTC；系统时刻由外层日志头提供。
    /// drain 不清除正在测量的当前帧，以便下一入口能包含本次格式化/flush 的完整成本。
    /// </summary>
    public bool TryDrain(long timestamp, long frequency, out string report)
    {
        report = "";
        if (!_hasWindow) return false;
        if (frequency != _frequency || frequency <= 0 || timestamp < _windowStartedAt
            || (_hasFrame && timestamp < _frame.Timestamp))
        {
            Reset();
            return false;
        }
        var windowSeconds = (timestamp - _windowStartedAt) / (double)frequency;
        if (windowSeconds < WindowSeconds) return false;
        if (_intervalCount == 0)
        {
            _windowStartedAt = timestamp;
            return false;
        }
        // 排名已在固定数组插入时完成；只有真正到期输出时创建字符串，常态帧不分配。
        var builder = new StringBuilder();
        builder.Append("marker: MSC-FRAME-STALL v1\nwindowSeconds: ").Append(Number(windowSeconds))
            .Append("\nscene: ").Append(_scene).Append("\ngeneration: ").Append(_generation)
            .Append("\nintervalCount: ").Append(_intervalCount)
            .Append("\ngapAtLeast16_667Ms: ").Append(_count16)
            .Append("\ngapAtLeast33_333Ms: ").Append(_count33)
            .Append("\ngapAtLeast50Ms: ").Append(_count50)
            .Append("\nsampleCount: ").Append(_sampleCount)
            .Append("\ndroppedCount: ").Append(_count33 - _sampleCount)
            .Append("\nincompleteUpdateCount: ").Append(_incompleteUpdateCount);
        for (var index = 0; index < _sampleCount; index++)
        {
            var sample = _samples[index];
            builder.Append("\nsample: previousFrame=").Append(sample.PreviousFrame)
                .Append("; windowOffsetMs=").Append(Number((sample.Timestamp - _windowStartedAt) * 1000d / frequency))
                .Append("; ageMs=").Append(Number((timestamp - sample.Timestamp) * 1000d / frequency))
                .Append("; gapMs=").Append(Number(sample.GapMs))
                .Append("; updateMs=").Append(Number(sample.UpdateMs))
                .Append("; lateUpdateMs=").Append(Number(sample.LateUpdateMs))
                .Append("; lateUpdateCalls=").Append(sample.LateUpdateCalls)
                .Append("; updateAllocatedBytes=").Append(sample.UpdateAllocatedBytes)
                .Append("; lateUpdateAllocatedBytes=").Append(sample.LateUpdateAllocatedBytes)
                .Append("; slowestStage=").Append(sample.SlowestStage ?? "none")
                .Append("; slowestStageMs=").Append(Number(sample.SlowestStageMs))
                .Append("; gc0Delta=").Append(sample.Gc0Delta)
                .Append("; gc1Delta=").Append(sample.Gc1Delta)
                .Append("; gc2Delta=").Append(sample.Gc2Delta);
        }
        builder.Append("\nscope: previous-frame Mod Update includes diagnostic flush; allocations are main-thread Mod scopes; ")
            .Append("GC deltas are process-wide correlation, not causation; stages may overlap; not GPU/FPS measurement");
        report = builder.ToString();
        Array.Clear(_samples, 0, _samples.Length);
        _sampleCount = 0;
        _intervalCount = _count16 = _count33 = _count50 = _incompleteUpdateCount = 0;
        _windowStartedAt = timestamp;
        return true;
    }

    private void KeepSlowest(Sample sample)
    {
        var insertion = 0;
        while (insertion < _sampleCount && _samples[insertion].GapMs >= sample.GapMs) insertion++;
        if (insertion >= MaximumSamples) return;
        var last = Math.Min(_sampleCount, MaximumSamples - 1);
        for (var index = last; index > insertion; index--) _samples[index] = _samples[index - 1];
        _samples[insertion] = sample;
        if (_sampleCount < MaximumSamples) _sampleCount++;
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private struct Frame
    {
        public long Timestamp;
        public long Sequence;
        public int Gc0;
        public int Gc1;
        public int Gc2;
        public bool UpdateObserved;
        public double UpdateMs;
        public long UpdateAllocatedBytes;
        public double LateUpdateMs;
        public long LateUpdateAllocatedBytes;
        public int LateUpdateCalls;
        public string? SlowestStage;
        public double SlowestStageMs;
    }

    private readonly record struct Sample(long Timestamp, long PreviousFrame, double GapMs,
        double UpdateMs, double LateUpdateMs, long UpdateAllocatedBytes, long LateUpdateAllocatedBytes,
        int LateUpdateCalls, string? SlowestStage, double SlowestStageMs, int Gc0Delta, int Gc1Delta, int Gc2Delta);
}
