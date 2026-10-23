using System.Globalization;
using MystiaStewardCompanion.Save;

// 无 Unity/BepInEx/文件日志依赖：按确定性时间推进真实生产窗口，验证汇总数值和失效边界。
var assertions = 0;
void Check(bool value, string message)
{
    assertions++;
    if (!value) throw new InvalidOperationException(message);
}
var window = new RuntimePerformanceDiagnostics();
window.Reset(10);
window.Record("snapshot.publish", 1);
window.Record("snapshot.publish", 8);
window.Record("snapshot.publish", 12);
window.Record("ui.orderHighlight", 0.125);
window.Record("invalid", double.NaN);
window.Record("invalid", double.PositiveInfinity);
window.Record("invalid", -1);
Check(!window.TryDrain(14.999, out _), "窗口到期前不得格式化逐帧日志。");
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
Check(window.TryDrain(15, out var report), "五秒到期应输出一次汇总。");
Check(report.Contains("stage: snapshot.publish; count=3; meanMs=7; maxMs=12; slow=2"), "均值/最大值/次数和慢调用数量必须来自每次真实观测。");
Check(report.Contains("stage: ui.orderHighlight; count=1; meanMs=0.125; maxMs=0.125; slow=0"), "数值必须使用固定小数点且保留低于一毫秒的计时。");
Check(!report.Contains("invalid"), "非法耗时不得污染窗口。");
Check(!window.TryDrain(15, out _), "同一窗口不得重复输出。");
window.Record("after", 2);
Check(window.TryDrain(20, out report) && report.Contains("stage: after;") && !report.Contains("snapshot.publish"), "新窗口不能重复旧观测。");
window.Record("discard-on-disable", 3);
window.Reset(30);
Check(!window.TryDrain(35, out _), "关闭后重新开启必须丢弃旧窗口。");
window.Record("clock-reset", 1);
Check(!window.TryDrain(1, out _) && !window.TryDrain(6, out _), "单调时钟回退必须清空陈旧数据。");
window.Reset(100);
for (var index = 0; index < 100; index++) window.Record($"key{index}", 1);
Check(window.TryDrain(105, out report) && report.Split("stage: ").Length - 1 == 96, "动态键必须受固定上限保护，并容纳实际全扩展与动作子阶段。");
window.Reset(110);
window.Record("frame.updateGap", 20);
window.Record("frame.updateGap", 50);
window.Record("frame.updateGap", 80);
Check(window.TryDrain(115, out report)
    && report.Contains("frameGapSlowThresholdMs: 50")
    && report.Contains("stage: frame.updateGap; count=3; meanMs=50; maxMs=80; slow=2"),
    "Update 间隔必须使用50ms独立慢阈值，不能把正常60Hz间隔全计作慢调用。");

var gaps = new RuntimeUpdateGapSampler();
Check(!gaps.Observe(1000, 1000, "night", 1, true, out _), "首帧没有前序时间戳，不能虚构间隔。");
Check(gaps.Observe(1017, 1000, "night", 1, true, out var gap) && gap == 17, "间隔应来自确切相邻入口墙钟。");
Check(!gaps.Observe(2000, 1000, "night", 1, false, out _)
    && !gaps.Observe(9000, 1000, "night", 1, true, out _), "暂停及恢复首帧不得计作长间隔。");
Check(gaps.Observe(9016, 1000, "night", 1, true, out gap) && gap == 16, "恢复后下一帧正常采样。");
Check(!gaps.Observe(9500, 1000, "day", 1, true, out _), "场景变化不得沿用上个场景时间戳。");
Check(!gaps.Observe(9600, 1000, "day", 2, true, out _), "相同场景名的新经营代次必须重置。");
Check(!gaps.Observe(100, 1000, "day", 2, true, out _), "时戳回退不可得到负间隔。");
gaps.Reset();
Check(!gaps.Observe(120, 1000, "day", 2, true, out _), "日志开关重新启用后首帧不复用陈旧时戳。");
Check(!gaps.Observe(140, 0, "day", 2, true, out _), "无效时钟频率必须放弃样本。");

var gate = new RuntimeSnapshotDiagnosticGate();
Check(gate.ShouldWrite(0, "scene-A|ready", "publish", out _), "首条诊断即时输出。");
Check(!gate.ShouldWrite(.1, "scene-A|ready", "skip-unchanged", out _), "未变化快照必须聚合。");
Check(gate.ShouldWrite(.2, "scene-A|error", "publish", out _), "新错误不得等到五秒窗口。");
Check(!gate.ShouldWrite(.3, "scene-A|error", "publish", out _), "重复同一错误可归并，但不影响其它错误日志通道。");
Check(gate.ShouldWrite(.4, "scene-A|ready", "publish", out _), "错误恢复必须即时记录。");
Check(gate.ShouldWrite(.5, "scene-B|ready", "publish-forced", out _), "场景变化必须即时记录。");
Check(!gate.ShouldWrite(1, "scene-B|ready", "publish-forced", out _), "强制发布不能绕过健康不变的日志限频。");
Check(gate.ShouldWrite(5.5, "scene-B|ready", "skip-unchanged", out report)
    && report == "windowOutcomes: publish=0; forced=1; unchanged=1", "窗口必须保留被压缩发布的结果计数。");
Check(gate.ShouldWrite(0, "scene-B|ready", "publish", out _), "时钟回退不能无限抑制诊断。");
gate.Reset();
Check(gate.ShouldWrite(0, "scene-B|ready", "publish", out _), "重新启用日志必须重新记录首条。");

// 旧实现内容签名/dirty reason 变化即写；这里模拟连续 20 分钟、350ms 一次快照，
// 只比较新限频器确切的日志次数，不能把减少写入直接换算为游戏帧率。
gate.Reset();
var written = 0;
const int observations = 3429;
for (var index = 0; index < observations; index++)
    if (gate.ShouldWrite(index * .35, "scene-stable|ready", index % 2 == 0 ? "publish" : "skip-unchanged", out _)) written++;
Check(written <= 230 && written > 220, "稳定场景的完整快照日志应按窗口合并而非逐次写入。");
Console.WriteLine($"SNAPSHOT-LOG-COUNTS observations={observations}; writes={written}; scenario=stable-health-alternating-outcomes; not-FPS");

var sweep = new RuntimeInactiveVisualSweep(.25);
Check(sweep.IsDue(10, 1, 1) && !sweep.IsDue(10.1, 1, 1), "非活动注册表首轮立即扫描，窗口内不重复。");
Check(sweep.IsDue(10.25, 1, 1), "非活动扫描须在250ms到期。");
Check(sweep.IsDue(10.251, 2, 1), "目标代次变化必须立即扫描。");
Check(sweep.IsDue(10.252, 2, 2), "经营代次变化必须立即扫描。");
Check(sweep.IsDue(0, 2, 2), "时钟回退不能阻止清理。");
Check(sweep.IsDue(double.NaN, 2, 2) && sweep.IsDue(1, 2, 2), "未知时钟必须保守扫描，不跳过验证。");

// 链接生产周期门禁，以实际调用次数验证可读性探针被延后，而不是在测试里复制新算法。
// 对照循环只保留旧顺序（先查询再查到期），两者必须在完全相同的帧标记快照为脏。
var probeCalls = 0;
var ready = true;
bool Probe() { probeCalls++; return ready; }
float nextAt = 0;
float oldNextAt = 0;
var oldProbeCalls = 0;
var oldScheduled = new List<int>();
var scheduled = new List<int>();
for (var frame = 0; frame < 600; frame++)
{
    var now = frame / 60f;
    oldProbeCalls++;
    if (ready && now >= oldNextAt)
    {
        oldNextAt = now + .75f;
        oldScheduled.Add(frame);
    }
    if (RuntimePeriodicSnapshotRefresh.TrySchedule(now, ref nextAt, .75f, Probe)) scheduled.Add(frame);
}
Check(scheduled.SequenceEqual(oldScheduled), "前移到期门禁不能改变原750ms刷新帧，也不能改变事件刷新入口。");
Check(probeCalls == 14 && oldProbeCalls == 600, "600帧稳定场景只应在14个原截止时间查询可读性，不能每帧查找对象。");
Console.WriteLine($"PERIODIC-READINESS-COUNTS frames=600; before={oldProbeCalls}; after={probeCalls}; scheduled={scheduled.Count}; not-FPS");

nextAt = 20;
Check(!RuntimePeriodicSnapshotRefresh.TrySchedule(19.99f, ref nextAt, .75f,
    () => throw new InvalidOperationException("未到期不得执行昂贵可读性探针")), "未到期应直接返回。");
ready = false;
var previousCalls = probeCalls;
Check(!RuntimePeriodicSnapshotRefresh.TrySchedule(20, ref nextAt, .75f, Probe)
    && nextAt == 20 && probeCalls == previousCalls + 1, "到期但未就绪必须探测，且不得推进截止时间。");
ready = true;
Check(RuntimePeriodicSnapshotRefresh.TrySchedule(20.01f, ref nextAt, .75f, Probe)
    && nextAt == 20.01f + .75f, "恢复后的第一次调用必须即时刷新，不添加额外750ms等待。");
ready = false;
var due = nextAt;
Check(!RuntimePeriodicSnapshotRefresh.TrySchedule(due, ref nextAt, .75f, Probe)
    && nextAt == due, "到期需重新读取当前可读性，不能复用上次true。");
nextAt = 0; // 控制器沿用原场景切换的截止时间复位。
ready = true;
Check(RuntimePeriodicSnapshotRefresh.TrySchedule(.01f, ref nextAt, .75f, Probe), "场景复位后必须立即允许原周期探测。");

// 此宿主不引用 Unity。用真实控制器源码确认运行时仍调用被行为测试覆盖的门禁，
// 并保留每帧 Hook 版本检查在它之前，避免仅测试脱离产品的帮助类而漏掉接线回归。
using var controllerSource = typeof(RuntimePerformanceDiagnostics).Assembly.GetManifestResourceStream("StewardOverlayController.cs")!;
using var controllerReader = new StreamReader(controllerSource);
var controllerText = controllerReader.ReadToEnd();
Check(controllerText.Contains("RuntimePeriodicSnapshotRefresh.TrySchedule(now, ref _nextActiveBusinessSnapshotRefreshAt,")
    && controllerText.Contains("_activeBusinessSnapshotReadProbe ??= CanReadActiveBusinessSnapshot"), "控制器必须使用被测的延迟探针门禁。");
var eventRefreshIndex = controllerText.IndexOf("        RefreshBusinessContextOnOrderCaptureChange();", StringComparison.Ordinal);
var periodicRefreshIndex = controllerText.IndexOf("        MarkActiveBusinessSnapshotDirtyIfDue();", StringComparison.Ordinal);
Check(eventRefreshIndex >= 0 && periodicRefreshIndex > eventRefreshIndex, "每帧Hook版本检查不能被移进周期门禁。");
foreach (var key in new[] { "capture.versionRefresh", "snapshot.activePollDue", "action.gate", "action.rare.prepare",
    "action.rare.complete", "action.normal.complete", "action.refresh.rare", "action.refresh.normal" })
    Check(controllerText.Contains($"EndDiagnosticStage(\"{key}\","), $"必须保留独立阶段计时：{key}");
RuntimeFrameStallDiagnosticsTests.Run();
RuntimeStageScopeTests.Run(Check);
Console.WriteLine($"Runtime performance diagnostics smoke: PASS ({assertions} assertions plus frame correlation suite)");
