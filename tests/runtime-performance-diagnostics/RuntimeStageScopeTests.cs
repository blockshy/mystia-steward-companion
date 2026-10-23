using MystiaStewardCompanion.Save;

/// <summary>真实同步计时转发的所有权、异常隔离与关闭开销回归；不触碰 Unity 或文件日志。</summary>
internal static class RuntimeStageScopeTests
{
    internal static void Run(Action<bool, string> check)
    {
        var firstOwner = new object();
        var nextOwner = new object();
        var first = new List<string>();
        var next = new List<string>();
        RuntimePerformanceDiagnostics.BindStageRecorder(firstOwner, (key, milliseconds) =>
        {
            if (!double.IsFinite(milliseconds) || milliseconds < 0) throw new InvalidOperationException("计时必须有限非负。");
            first.Add(key);
        });
        try
        {
            using (RuntimePerformanceDiagnostics.MeasureStage("outer"))
            using (RuntimePerformanceDiagnostics.MeasureStage("inner")) { }
            check(first.SequenceEqual(new[] { "inner", "outer" }), "嵌套作用域必须在各自原同步边界结束。");

            var actionError = new InvalidOperationException("游戏动作原异常");
            Exception? observed = null;
            RuntimePerformanceDiagnostics.BindStageRecorder(firstOwner, (_, _) => throw new InvalidOperationException("诊断异常"));
            try
            {
                using var scope = RuntimePerformanceDiagnostics.MeasureStage("failing-action");
                throw actionError;
            }
            catch (Exception exception) { observed = exception; }
            check(ReferenceEquals(observed, actionError), "诊断 sink 不得覆盖游戏原异常。");
            var actionCompleted = false;
            using (RuntimePerformanceDiagnostics.MeasureStage("successful-action")) actionCompleted = true;
            check(actionCompleted, "诊断 sink 不得把成功动作变成失败。");

            var oldScope = RuntimePerformanceDiagnostics.MeasureStage("old-owner");
            RuntimePerformanceDiagnostics.BindStageRecorder(nextOwner, (key, _) => next.Add(key));
            RuntimePerformanceDiagnostics.ClearStageRecorder(firstOwner);
            oldScope.Dispose();
            using (RuntimePerformanceDiagnostics.MeasureStage("new-owner")) { }
            check(next.SequenceEqual(new[] { "new-owner" }), "旧实例清理/旧 scope 不能清空或污染新实例窗口。");

            // 使用真实独立线程，而非 Task 的不确定调度；线程局部转发不能进入主线程窗口。
            var background = new Thread(() => { using var scope = RuntimePerformanceDiagnostics.MeasureStage("background"); });
            background.Start(); background.Join();
            check(next.Count == 1, "后台线程不得使用主线程记录器。");
            var closingScope = RuntimePerformanceDiagnostics.MeasureStage("closed-owner");
            RuntimePerformanceDiagnostics.ClearStageRecorder(nextOwner);
            closingScope.Dispose();
            check(next.Count == 1, "关闭前开始、关闭后结束的 scope 不得写回旧窗口。");

            // 预热后测真实生产关闭分支。固定字面量与结构体 using 不创建闭包或箱装对象。
            for (var index = 0; index < 32; index++)
                using (RuntimePerformanceDiagnostics.MeasureStage("disabled")) { }
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 10000; index++)
                using (RuntimePerformanceDiagnostics.MeasureStage("disabled")) { }
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            check(bytes == 0 && next.Count == 1, "关闭的10,000次子阶段不得分配或调用记录器。");
            Console.WriteLine($"STAGE-SCOPE-ALLOC disabledIterations=10000; bytes={bytes}; not-FPS");
        }
        finally
        {
            RuntimePerformanceDiagnostics.ClearStageRecorder(firstOwner);
            RuntimePerformanceDiagnostics.ClearStageRecorder(nextOwner);
        }
    }
}
