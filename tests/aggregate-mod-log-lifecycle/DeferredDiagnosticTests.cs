using System.Reflection;
using System.Text;
using MystiaStewardCompanion.Save;

/// <summary>
/// 延迟性能窗口的生产实现回归。使用受控流和写入锁制造确定的慢盘场景，
/// 验证主线程入口不等待写盘、时间不漂移、边界排空以及内存上限；不接触游戏。
/// </summary>
internal static class DeferredDiagnosticTests
{
    private static readonly Type Service = typeof(AggregateModLogService);
    private static FieldInfo Field(string name) => Service.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"缺少诊断字段 {name}");
    private static object WriterLock => Field("SyncRoot").GetValue(null)!;

    public static void Run(string root)
    {
        VerifyBlockedFlushDoesNotBlockSubmission(root);
        VerifyConcurrentSectionsPreserveContentAndTimestamp(root);
        VerifyReconfigureDrainsOldFile(root);
        VerifyBoundedPressureIsReported(root);
        VerifyByteLimitAndDisabledEntry(root);
        VerifyFailureDoesNotReplayUnknownSection(root);
        Console.WriteLine("PASS: 6 deferred-diagnostic scenarios; blocked Flush remains off caller, timestamps/content preserved, old-file drain, bounded pressure and explicit loss, failed append not replayed.");
    }

    private static string Open(string root, string name)
    {
        var path = Path.Combine(root, "deferred-" + name, "aggregate-mod.log");
        AggregateModLogService.Configure(true, path, 3);
        return path;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void VerifyBlockedFlushDoesNotBlockSubmission(string root)
    {
        Open(root, "blocked-flush");
        var writerField = Field("_writer");
        ((StreamWriter)writerField.GetValue(null)!).Dispose();
        using var stream = new BlockingFlushStream();
        writerField.SetValue(null, new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true });
        stream.Arm();
        using var submitted = new ManualResetEventSlim();
        var ordinary = new Thread(() => AggregateModLogService.AppendSection("test", "slow ordinary writer", "held")) { IsBackground = true };
        var producer = new Thread(() =>
        {
            AggregateModLogService.AppendDeferredSection("performance", "frozen frame", "frame=123; gapMs=205");
            submitted.Set();
        }) { IsBackground = true };
        var producerStarted = false;
        try
        {
            ordinary.Start();
            Require(stream.Entered.Wait(TimeSpan.FromSeconds(3)), "慢盘夹具未进入真实Flush。");
            producer.Start();
            producerStarted = true;
            Require(submitted.Wait(TimeSpan.FromSeconds(3)), "延迟窗口提交仍等待生产写入锁/磁盘。");
            Require(ordinary.IsAlive && !stream.Release.IsSet, "尚未证明提交结束时磁盘仍阻塞。");
        }
        finally
        {
            stream.Release.Set();
            Require(ordinary.Join(TimeSpan.FromSeconds(3)), "普通写线程未正常退出。");
            if (producerStarted) Require(producer.Join(TimeSpan.FromSeconds(3)), "诊断提交线程未正常退出。");
            AggregateModLogService.Shutdown();
        }
        Require(Encoding.UTF8.GetString(stream.ToArray()).Contains("frame=123; gapMs=205", StringComparison.Ordinal), "延迟窗口在关闭排空时丢失。");
    }

    private static void VerifyConcurrentSectionsPreserveContentAndTimestamp(string root)
    {
        var path = Open(root, "concurrent");
        DateTimeOffset submittedBy;
        lock (WriterLock)
        {
            // 64份小段落均低于容量；消费者被确定阻塞，保证测试不是偶然碰到空闲磁盘。
            Task.WaitAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
            {
                for (var index = 0; index < 16; index++)
                    AggregateModLogService.AppendDeferredSection("performance", $"unique-{worker}-{index}", "第一行\r\n第二行\r末行");
            })).ToArray());
            submittedBy = DateTimeOffset.Now;
            Require((int)Field("_deferredCount").GetValue(null)! == 64, "队列丢失已提交条目。");
            Thread.Sleep(30);
        }
        AggregateModLogService.Shutdown();
        var text = File.ReadAllText(path);
        for (var worker = 0; worker < 4; worker++)
        for (var index = 0; index < 16; index++)
        {
            var expected = $"[performance] unique-{worker}-{index} ====";
            Require(text.Split(expected, StringSplitOptions.None).Length == 2, "并发窗口丢失或重复。");
        }
        Require(text.Contains("第一行" + Environment.NewLine + "第二行" + Environment.NewLine + "末行", StringComparison.Ordinal), "延迟段落换行合同改变。");
        var header = text.Split('\n').First(line => line.Contains("[performance] unique-", StringComparison.Ordinal));
        var timestamp = DateTimeOffset.Parse(header.Substring(5, 30));
        Require(timestamp <= submittedBy, "窗口时间错误变成了后台实际落盘时间。");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static void VerifyReconfigureDrainsOldFile(string root)
    {
        var before = Open(root, "old-file");
        var after = Path.Combine(root, "deferred-new-file", "aggregate-mod.log");
        lock (WriterLock)
        {
            AggregateModLogService.AppendDeferredSection("performance", "belongs-to-old-session", "old");
            // 重入生产锁模拟重配先于消费者到达：旧段落必须先进入旧文件。
            AggregateModLogService.Configure(true, after, 3);
            AggregateModLogService.AppendDeferredSection("performance", "belongs-to-new-session", "new");
        }
        AggregateModLogService.Shutdown();
        var oldText = File.ReadAllText(before);
        var newText = File.ReadAllText(after);
        Require(oldText.Contains("belongs-to-old-session", StringComparison.Ordinal), "重配没有排空旧文件。");
        Require(!newText.Contains("belongs-to-old-session", StringComparison.Ordinal), "旧会话诊断泄漏到新路径。");
        Require(newText.Contains("belongs-to-new-session", StringComparison.Ordinal), "新会话诊断丢失。");
        Require((int)Field("_deferredCount").GetValue(null)! == 0 && (long)Field("_deferredBytes").GetValue(null)! == 0, "关闭后队列预算未释放。");
    }

    private static void VerifyBoundedPressureIsReported(string root)
    {
        var path = Open(root, "count-limit");
        lock (WriterLock)
        {
            for (var index = 0; index < AggregateModLogService.MaximumDeferredSections + 3; index++)
                AggregateModLogService.AppendDeferredSection("performance", $"bounded-{index}", "value");
            Require((int)Field("_deferredCount").GetValue(null)! == AggregateModLogService.MaximumDeferredSections, "慢盘时队列条数越界。");
            Require((long)Field("_deferredDroppedSections").GetValue(null)! == 3, "容量压力没有记录精确丢弃数。");
        }
        AggregateModLogService.Shutdown();
        Require(File.ReadAllText(path).Contains("droppedSections=3", StringComparison.Ordinal), "容量损失没有写入明确日志标记。");
    }

    private static void VerifyByteLimitAndDisabledEntry(string root)
    {
        var path = Open(root, "byte-limit");
        lock (WriterLock)
        {
            AggregateModLogService.AppendDeferredSection("performance", "too-large", new string('x', (int)AggregateModLogService.MaximumDeferredBytes));
            Require((int)Field("_deferredCount").GetValue(null)! == 0 && (long)Field("_deferredBytes").GetValue(null)! == 0, "超大段落没有撤回预算。");
            AggregateModLogService.AppendDeferredSection("performance", "small-after-large", "retained");
        }
        AggregateModLogService.Shutdown();
        var text = File.ReadAllText(path);
        Require(!text.Contains("too-large", StringComparison.Ordinal) && text.Contains("small-after-large", StringComparison.Ordinal), "超大段落影响正常诊断。");
        Require(text.Contains("droppedSections=1", StringComparison.Ordinal), "字节超限没有记录。");
        AggregateModLogService.AppendDeferredSection("performance", "disabled", "must-not-queue");
        Require((int)Field("_deferredCount").GetValue(null)! == 0, "日志关闭后仍接受延迟段落。");
    }

    private static void VerifyFailureDoesNotReplayUnknownSection(string root)
    {
        var path = Open(root, "writer-failure");
        lock (WriterLock)
        {
            ((StreamWriter)Field("_writer").GetValue(null)!).Dispose();
            AggregateModLogService.AppendDeferredSection("performance", "unknown-commit", "must-not-replay");
            AggregateModLogService.AppendDeferredSection("performance", "after-reopen", "normal");
        }
        AggregateModLogService.Shutdown();
        var text = File.ReadAllText(path);
        Require(!text.Contains("unknown-commit", StringComparison.Ordinal), "写入结果未知的延迟段落被重放。");
        Require(text.Contains("after-reopen", StringComparison.Ordinal) && text.Contains("previous writer failed during append", StringComparison.Ordinal), "后续段落没有恢复失败写入器。");
    }
}
