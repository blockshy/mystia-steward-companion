using MystiaStewardCompanion.Save;

/// <summary>
/// 链接真实纯托管观察器，以可控 Stopwatch 时基验证关联、边界和容量。
/// 不启动 Unity、不调用 GC.Collect、不用真实睡眠模拟慢帧，也不把本测试解释为游戏 FPS。
/// </summary>
internal static class RuntimeFrameStallDiagnosticsTests
{
    internal static int Run()
    {
        var assertions = 0;
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
        const long frequency = 1_000_000;
        var diagnostics = new RuntimeFrameStallDiagnostics();
        diagnostics.BeginFrame(0, frequency, "Work", 7, true, 100, 50, 20);
        Check(!diagnostics.TryDrain(4_999_999, frequency, out _), "五秒之前不得输出慢帧日志。");
        long now = 0;
        var gaps = new[] { 10_000, 16_667, 33_333, 50_000, 99_999 };
        for (var index = 0; index < gaps.Length; index++)
        {
            diagnostics.RecordStage("smaller", 1);
            diagnostics.RecordStage("action.normal.complete", 2 + index);
            diagnostics.RecordStage("main.orderPreparations", 100);
            diagnostics.RecordStage("main.orderCaptureRefresh", 200);
            diagnostics.RecordStage("main.update", 500);
            diagnostics.RecordStage("ignored-nan", double.NaN);
            diagnostics.RecordUpdate(3 + index, 100 + index);
            diagnostics.RecordLateUpdate(1, 20);
            now += gaps[index];
            diagnostics.BeginFrame(now, frequency, "Work", 7, true, 101 + index, index == 4 ? 52 : 50, 20);
        }
        Check(diagnostics.TryDrain(5_000_000, frequency, out var report), "完整窗口必须输出已观测分布。");
        Check(report.Contains("intervalCount: 5\ngapAtLeast16_667Ms: 4\ngapAtLeast33_333Ms: 3\ngapAtLeast50Ms: 2"),
            "三个含等号阈值必须分别统计，不能只保留50ms以上事件。");
        Check(report.Contains("sampleCount: 3\ndroppedCount: 0\nincompleteUpdateCount: 0"), "首帧不虚构间隔且完整样本不应丢弃。");
        Check(report.Contains("sample: previousFrame=5; windowOffsetMs=209.999; ageMs=4790.001; gapMs=99.999; updateMs=7; lateUpdateMs=1; lateUpdateCalls=1; updateAllocatedBytes=104; lateUpdateAllocatedBytes=20; slowestStage=action.normal.complete; slowestStageMs=6; gc0Delta=1; gc1Delta=2; gc2Delta=0"),
            "入口间隔必须关联前一帧的完整Mod范围、最慢业务阶段与入口之间的进程GC差值。");
        Check(report.IndexOf("gapMs=99.999;", StringComparison.Ordinal) < report.IndexOf("gapMs=50;", StringComparison.Ordinal)
            && report.IndexOf("gapMs=50;", StringComparison.Ordinal) < report.IndexOf("gapMs=33.333;", StringComparison.Ordinal),
            "保留样本必须按最慢优先输出，而非最新优先覆盖。");
        Check(report.Contains("GC deltas are process-wide correlation, not causation") && !report.Contains("ignored-nan"),
            "报告必须声明GC只能说明相关，非法计时不得污染热点。");
        Check(!report.Contains("slowestStage=main."), "已拆分父容器不得因耗时更大覆盖真实动作子阶段。");
        Check(!diagnostics.TryDrain(5_000_000, frequency, out _), "同一窗口不能重复输出。");

        // 五秒到期的输出发生在当前 Update 内部；它不能清掉尚未完成的当前帧。
        // 当前帧在 flush 之后登记完整耗时，下一入口才把它与实际50ms间隔关联。
        diagnostics.Reset();
        diagnostics.BeginFrame(0, frequency, "Work", 7, true, 0, 0, 0);
        diagnostics.RecordUpdate(1, 10);
        diagnostics.BeginFrame(4_990_000, frequency, "Work", 7, true, 0, 0, 0);
        Check(diagnostics.TryDrain(5_000_000, frequency, out _), "到期应能输出前一帧关联。");
        diagnostics.RecordStage("diagnostic.flush", 15);
        diagnostics.RecordUpdate(20, 200);
        diagnostics.RecordLateUpdate(2, 30);
        diagnostics.BeginFrame(5_040_000, frequency, "Work", 7, true, 1, 0, 0);
        Check(diagnostics.TryDrain(10_000_000, frequency, out report)
            && report.Contains("previousFrame=2; windowOffsetMs=40; ageMs=4960; gapMs=50; updateMs=20; lateUpdateMs=2;")
            && report.Contains("slowestStage=diagnostic.flush; slowestStageMs=15; gc0Delta=1;"),
            "drain后必须保留当前帧，不能漏掉诊断输出自身耗时和分配。");

        diagnostics.Reset();
        diagnostics.BeginFrame(0, frequency, "Work", 1, true, 0, 0, 0);
        now = 0;
        for (var index = 0; index < 12; index++)
        {
            diagnostics.RecordUpdate(1, 0);
            now += (34 + index) * 1000;
            diagnostics.BeginFrame(now, frequency, "Work", 1, true, 0, 0, 0);
        }
        Check(diagnostics.TryDrain(5_000_000, frequency, out report)
            && report.Contains("sampleCount: 8\ndroppedCount: 4"), "任意数量慢间隔只能保留八条，并报告未保留数量。");
        Check(report.Contains("gapMs=45;") && report.Contains("gapMs=38;") && !report.Contains("gapMs=37;"),
            "有界容量满后仍应保留全窗口真正最慢八条。");
        Check(report.Contains("lateUpdateMs=0; lateUpdateCalls=0;"), "没有LateUpdate观测时必须明确标识缺失，不能假称测得零耗时。");

        diagnostics.Reset();
        diagnostics.BeginFrame(0, frequency, "Work", 1, true, 0, 0, 0);
        diagnostics.RecordUpdate(double.NaN, 100);
        diagnostics.BeginFrame(50_000, frequency, "Work", 1, true, 0, 0, 0);
        Check(diagnostics.TryDrain(5_000_000, frequency, out report)
            && report.Contains("gapAtLeast50Ms: 1\nsampleCount: 0\ndroppedCount: 1\nincompleteUpdateCount: 1"),
            "未登记完整Update仍统计间隔，但不得输出伪完整样本。");

        // 每个失效边界都先制造一个未输出慢样本，再验证旧窗口和旧帧均被清除。
        void Seed()
        {
            diagnostics.Reset();
            diagnostics.BeginFrame(1_000_000, frequency, "Work", 1, true, 5, 2, 1);
            diagnostics.RecordUpdate(4, 40);
            diagnostics.BeginFrame(1_100_000, frequency, "Work", 1, true, 5, 2, 1);
        }
        Seed();
        diagnostics.BeginFrame(1_200_000, frequency, "Work", 1, false, 5, 2, 1);
        diagnostics.BeginFrame(10_000_000, frequency, "Work", 1, true, 6, 2, 1);
        Check(!diagnostics.TryDrain(15_000_000, frequency, out _), "暂停、恢复首帧不能沿用旧样本或把暂停计为长间隔。");
        Seed();
        diagnostics.BeginFrame(1_200_000, frequency, "Day", 1, true, 5, 2, 1);
        Check(!diagnostics.TryDrain(6_200_000, frequency, out _), "换场必须清除前一场景的未输出关联。");
        Seed();
        diagnostics.BeginFrame(1_200_000, frequency, "Work", 2, true, 5, 2, 1);
        Check(!diagnostics.TryDrain(6_200_000, frequency, out _), "相同场景名的新经营代次也必须清除旧帧。");
        Seed();
        diagnostics.BeginFrame(100_000, frequency, "Work", 1, true, 5, 2, 1);
        Check(!diagnostics.TryDrain(5_100_000, frequency, out _), "单调时钟回退时不得产生负时间或复用旧窗口。");
        Seed();
        diagnostics.BeginFrame(1_200_000, frequency, "Work", 1, true, 4, 2, 1);
        Check(!diagnostics.TryDrain(6_200_000, frequency, out _), "GC累计计数回退时不构造负delta。");
        Seed();
        diagnostics.Reset();
        Check(!diagnostics.TryDrain(6_200_000, frequency, out _), "关闭日志的显式Reset不得保留待发事件。");
        Seed();
        diagnostics.BeginFrame(1_200_000, 0, "Work", 1, true, 5, 2, 1);
        Check(!diagnostics.TryDrain(6_200_000, frequency, out _), "无效时基必须停止关联。");
        Seed();
        Check(!diagnostics.TryDrain(1_050_000, frequency, out _)
            && !diagnostics.TryDrain(6_200_000, frequency, out _), "输出时戳不能早于当前帧入口并留下负age，必须清空异常时基。");

        // 分配基准仅包围真实热路径，不含断言、字符串格式化、数组创建和日志输出。
        // 先走过排序及满容量路径，再测重复输入；没有强制GC，结果只是本进程托管分配证据。
        diagnostics.Reset();
        now = 0;
        for (var index = 0; index < 100; index++) ObserveHotFrame();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++) ObserveHotFrame();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Check(allocated == 0, "热路径逐帧不得分配托管对象或格式化字符串。");
        Console.WriteLine($"FRAME-STALL-HOT-PATH frames=10000; allocatedBytes={allocated}; not-FPS");
        Console.WriteLine($"Runtime frame stall diagnostics: PASS ({assertions} assertions)");
        return assertions;

        void ObserveHotFrame()
        {
            now += 50_000;
            diagnostics.BeginFrame(now, frequency, "Work", 1, true, 0, 0, 0);
            diagnostics.RecordStage("action.normal.complete", 1);
            diagnostics.RecordUpdate(2, 10);
            diagnostics.RecordLateUpdate(1, 20);
        }
    }
}
