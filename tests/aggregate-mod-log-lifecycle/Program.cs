using System.Reflection;
using System.Collections.Concurrent;
using System.Text;
using BepInEx.Logging;
using MystiaStewardCompanion.Save;

var root = Path.Combine(Path.GetTempPath(), $"mystia-aggregate-log-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);

try
{
    VerifyRepeatedAutomationRecreatesDeletedActivePath(root);
    VerifyParentDirectoryDeletionIsRecovered(root);
    VerifyBepInExEventRecreatesDeletedActivePath(root);
    VerifySameConfigurationPerformsHealthCheck(root);
    VerifyWriterFailureRetriesWithoutReplayingUnknownWrite(root);
    VerifyConcurrentWritesAndExternalDeletionRemainRecoverable(root);
    VerifyShutdownReleasesWriter(root);
    VerifyFullActiveFileRotatesOnOpen(root);
    VerifySectionUsesOneFlush(root);
    VerifyEnabledReadDoesNotWaitForWriter(root);
    VerifyProductionSourceContract();
    // 配置文件中的历史越界值也按同一 1–30 上限收敛，不能只约束表单。
    AssertEqual(1, AggregateModLogService.NormalizeMaxFileCount(0), "日志数量下界失效。");
    AssertEqual(30, AggregateModLogService.NormalizeMaxFileCount(31), "日志数量上界失效。");
    AssertEqual(30, AggregateModLogService.NormalizeMaxFileCount(9999), "历史过大保留数量没有收敛。");
    Console.WriteLine(
        "PASS: aggregate log recreates externally removed paths under sequential and concurrent writes, "
        + "retries failed writers, keeps one listener, releases handles, preserves rotation semantics, flushes each diagnostic section once, and reads Enabled without waiting for a blocked writer.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}
finally
{
    AggregateModLogService.Shutdown();
    TryDeleteDirectory(root);
}

static void VerifyRepeatedAutomationRecreatesDeletedActivePath(string root)
{
    var path = NewPath(root, "repeated-automation");
    AggregateModLogService.Configure(true, path, 3);
    AggregateModLogService.AppendAutomation("wait", null, "same state");
    var staticResetCount = RuntimeStaticDataDiagnosticFormatter.ResetCount;
    var specialResetCount = SpecialBusinessDiagnostics.ResetCount;

    File.Delete(path);
    AssertFalse(File.Exists(path), "The active aggregate log could not be removed during the smoke test.");

    AggregateModLogService.AppendAutomation("wait", null, "same state");

    var content = ReadActiveLog(path);
    AssertContains(content, "aggregate log writer recovered", "Deleted active log did not record its recovery boundary.");
    AssertContains(content, "reason=active log path was removed externally", "Recovery reason did not identify external removal.");
    AssertContains(content, "action=wait", "Repeat suppression ran before the active-path health check.");
    AssertEqual(staticResetCount + 1, RuntimeStaticDataDiagnosticFormatter.ResetCount, "Static diagnostic deduplication was not reset.");
    AssertEqual(specialResetCount + 1, SpecialBusinessDiagnostics.ResetCount, "Special-business diagnostic deduplication was not reset.");
    AssertEqual(1, Logger.Listeners.Count, "Active-path recovery duplicated the aggregate listener.");
    AggregateModLogService.Shutdown();
}

static void VerifyParentDirectoryDeletionIsRecovered(string root)
{
    var path = NewPath(root, "removed-parent");
    var directory = Path.GetDirectoryName(path)!;
    AggregateModLogService.Configure(true, path, 3);
    AggregateModLogService.AppendSection("smoke", "before removal", "before");

    Directory.Delete(directory, recursive: true);
    AggregateModLogService.AppendSection("smoke", "after removal", "after");

    AssertTrue(Directory.Exists(directory), "Deleted aggregate-log parent directory was not recreated.");
    AssertContains(ReadActiveLog(path), "after removal", "Logging did not resume after recreating the parent directory.");
    AggregateModLogService.Shutdown();
}

static void VerifySameConfigurationPerformsHealthCheck(string root)
{
    var path = NewPath(root, "same-configure");
    AggregateModLogService.Configure(true, path, 4);
    File.Delete(path);

    AggregateModLogService.Configure(true, path, 4);

    AssertTrue(AggregateModLogService.Enabled, "Same-parameter health check disabled aggregate logging.");
    AssertEqual(1, Logger.Listeners.Count, "Same-parameter health check registered a second listener.");
    AssertContains(ReadActiveLog(path), "aggregate log writer recovered", "Same-parameter Configure did not recreate the missing active path.");
    AggregateModLogService.Shutdown();
}

static void VerifyBepInExEventRecreatesDeletedActivePath(string root)
{
    var path = NewPath(root, "bepinex-event");
    AggregateModLogService.Configure(true, path, 3);
    File.Delete(path);

    Logger.Emit("event after external removal");

    var content = ReadActiveLog(path);
    AssertContains(content, "aggregate log writer recovered", "BepInEx listener entry did not recover its active path.");
    AssertContains(content, "event after external removal", "BepInEx event was not written after recovery.");
    AggregateModLogService.Shutdown();
}

static void VerifyWriterFailureRetriesWithoutReplayingUnknownWrite(string root)
{
    var path = NewPath(root, "writer-failure");
    AggregateModLogService.Configure(true, path, 3);
    AggregateModLogService.AppendSection("smoke", "before writer failure", "before");

    var writerField = typeof(AggregateModLogService).GetField("_writer", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Aggregate writer field was not found.");
    var writer = writerField.GetValue(null) as StreamWriter
        ?? throw new InvalidOperationException("Aggregate writer was not active.");
    writer.Dispose();

    AggregateModLogService.AppendSection("smoke", "unknown commit state", "must not replay");
    AggregateModLogService.AppendSection("smoke", "after retry", "resumed");

    var content = ReadActiveLog(path);
    AssertFalse(content.Contains("unknown commit state", StringComparison.Ordinal), "A failed write with unknown commit state was replayed.");
    AssertContains(content, "reason=previous writer failed during append", "Writer failure did not leave a recovery boundary.");
    AssertContains(content, "after retry", "The next append did not reopen a failed writer.");
    AggregateModLogService.Shutdown();
}

static void VerifyShutdownReleasesWriter(string root)
{
    var path = NewPath(root, "shutdown");
    AggregateModLogService.Configure(true, path, 3);
    AggregateModLogService.AppendSection("smoke", "shutdown", "content");

    AggregateModLogService.Shutdown();

    AssertFalse(AggregateModLogService.Enabled, "Shutdown left aggregate logging enabled.");
    AssertEqual(0, Logger.Listeners.Count, "Shutdown left the aggregate listener registered.");
    using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
    }

    File.Delete(path);
    AssertFalse(File.Exists(path), "Shutdown retained the aggregate file handle.");
}

static void VerifyConcurrentWritesAndExternalDeletionRemainRecoverable(string root)
{
    const int writeIterations = 160;
    const int deleteIterations = 80;
    var path = NewPath(root, "concurrent-deletion");
    using var start = new ManualResetEventSlim(initialState: false);
    using var firstWritesCompleted = new CountdownEvent(initialCount: 3);
    using var deletionStarted = new ManualResetEventSlim(initialState: false);
    var failures = new ConcurrentQueue<Exception>();
    AggregateModLogService.Configure(true, path, 3);

    var sectionWriter = Task.Run(() =>
    {
        start.Wait();
        AggregateModLogService.AppendSection("concurrent", "section-0", "payload-0");
        firstWritesCompleted.Signal();
        deletionStarted.Wait();
        for (var index = 1; index < writeIterations; index++)
        {
            AggregateModLogService.AppendSection("concurrent", $"section-{index}", $"payload-{index}");
        }
    });
    var automationWriter = Task.Run(() =>
    {
        start.Wait();
        AggregateModLogService.AppendAutomation("concurrent", null, "state-0");
        firstWritesCompleted.Signal();
        deletionStarted.Wait();
        for (var index = 1; index < writeIterations; index++)
        {
            AggregateModLogService.AppendAutomation("concurrent", null, $"state-{index % 4}");
        }
    });
    var secondSectionWriter = Task.Run(() =>
    {
        start.Wait();
        AggregateModLogService.AppendSection("concurrent", "secondary-0", "payload");
        firstWritesCompleted.Signal();
        deletionStarted.Wait();
        for (var index = 1; index < writeIterations; index++)
        {
            AggregateModLogService.AppendSection("concurrent", $"secondary-{index}", "payload");
        }
    });
    var deleter = Task.Run(() =>
    {
        start.Wait();
        firstWritesCompleted.Wait();
        for (var index = 0; index < deleteIterations; index++)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }

            if (index == 0) deletionStarted.Set();
            Thread.Sleep(1);
        }
    });

    start.Set();
    var concurrentTasks = new[] { sectionWriter, automationWriter, secondSectionWriter, deleter };
    if (!Task.WaitAll(concurrentTasks, TimeSpan.FromSeconds(15)))
    {
        throw new TimeoutException("Concurrent aggregate-log recovery did not finish within 15 seconds.");
    }
    if (failures.TryDequeue(out var deletionFailure))
    {
        throw new InvalidOperationException("Concurrent external deletion failed.", deletionFailure);
    }

    AggregateModLogService.AppendSection("concurrent", "final sentinel", "sentinel-content");

    AssertTrue(AggregateModLogService.Enabled, "Concurrent deletion disabled aggregate logging.");
    AssertEqual(1, Logger.Listeners.Count, "Concurrent recovery duplicated the aggregate listener.");
    AssertContains(ReadActiveLog(path), "final sentinel", "Final append did not recover after concurrent deletion stopped.");
    AggregateModLogService.Shutdown();
    using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
    }

    File.Delete(path);
    AssertFalse(File.Exists(path), "Concurrent recovery leaked its final writer handle after shutdown.");
}

static void VerifyFullActiveFileRotatesOnOpen(string root)
{
    var path = NewPath(root, "rotation");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        stream.SetLength(AggregateModLogService.MaxFileBytes);
    }

    AggregateModLogService.Configure(true, path, 2);

    var archive = Path.Combine(
        Path.GetDirectoryName(path)!,
        $"{Path.GetFileNameWithoutExtension(path)}.1{Path.GetExtension(path)}");
    AssertTrue(File.Exists(archive), "A full active log was not rotated before append.");
    AssertEqual(AggregateModLogService.MaxFileBytes, new FileInfo(archive).Length, "Rotated archive length changed.");
    AssertContains(ReadActiveLog(path), "aggregate log enabled", "New active file did not receive the service boundary.");
    AssertEqual(2, AggregateModLogService.EnumerateFiles(path).Count, "Rotation exceeded the configured file count.");
    AggregateModLogService.Shutdown();
}

/// <summary>
/// 在既有临时目录和有效活动路径上替换为可计数流，验证生产 AppendSection 的实际
/// AutoFlush 次数，而非只检查源码包含某个调用。测试不触碰正式日志或游戏配置。
/// </summary>
static void VerifySectionUsesOneFlush(string root)
{
    var path = NewPath(root, "section-single-flush");
    AggregateModLogService.Configure(true, path, 2);
    var writerField = typeof(AggregateModLogService).GetField("_writer", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Aggregate writer field was not found.");
    ((StreamWriter)writerField.GetValue(null)!).Dispose();
    using var countingStream = new FlushCountingStream();
    var writer = new StreamWriter(countingStream, new UTF8Encoding(false), 1024, leaveOpen: true)
    {
        AutoFlush = true,
    };
    writerField.SetValue(null, writer);
    countingStream.ResetFlushCount();

    // 混合换行与空行必须继续规范化为本平台换行；长于写入器缓冲区也只能主动刷新一次。
    var lines = Enumerable.Range(0, 120).Select(index => $"字段{index}: 合成诊断值").ToArray();
    var content = string.Join("\r\n", lines) + "\r\n\r末行";
    AggregateModLogService.AppendSection("smoke", "single-flush", content);
    AssertEqual(1, countingStream.FlushCount, "A single diagnostic section still triggers multiple synchronous flushes.");
    var written = Encoding.UTF8.GetString(countingStream.ToArray());
    AssertContains(written, "[smoke] single-flush", "The section header was lost.");
    AssertContains(written, string.Join(Environment.NewLine, lines), "Section content or line normalization changed.");
    AssertTrue(written.EndsWith(Environment.NewLine + "末行" + Environment.NewLine + Environment.NewLine, StringComparison.Ordinal),
        "The final line or blank section terminator was lost.");
    AggregateModLogService.Shutdown();
}

/// <summary>
/// 用真实 AppendSection 持有生产写入锁，并在底层 Flush 处暂停来模拟慢磁盘。
/// 另一个专用线程读取 Enabled 必须在释放 Flush 前完成；这验证实际锁竞争行为，
/// 不依赖检索源码里的 Volatile 字样，也不把测量阈值当作游戏帧率证据。
/// </summary>
static void VerifyEnabledReadDoesNotWaitForWriter(string root)
{
    var path = NewPath(root, "enabled-reader-during-flush");
    AggregateModLogService.Configure(true, path, 2);
    var writerField = typeof(AggregateModLogService).GetField("_writer", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Aggregate writer field was not found.");
    ((StreamWriter)writerField.GetValue(null)!).Dispose();
    using var blockedStream = new BlockingFlushStream();
    var writer = new StreamWriter(blockedStream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
    writerField.SetValue(null, writer);
    blockedStream.Arm();

    using var readCompleted = new ManualResetEventSlim(false);
    var readValue = false;
    Exception? writeFailure = null;
    Exception? readFailure = null;
    // 使用独立线程避免把线程池饥饿误判成开关锁等待；只操作本测试的内存流和临时日志。
    var writing = new Thread(() =>
    {
        try { AggregateModLogService.AppendSection("smoke", "blocked-flush", "synthetic payload"); }
        catch (Exception error) { writeFailure = error; }
    }) { IsBackground = true };
    var reading = new Thread(() =>
    {
        try { readValue = AggregateModLogService.Enabled; }
        catch (Exception error) { readFailure = error; }
        finally { readCompleted.Set(); }
    }) { IsBackground = true };
    var readerStarted = false;
    try
    {
        writing.Start();
        AssertTrue(blockedStream.Entered.Wait(TimeSpan.FromSeconds(3)), "The synthetic writer did not reach its held Flush.");
        reading.Start();
        readerStarted = true;
        AssertTrue(readCompleted.Wait(TimeSpan.FromSeconds(3)), "Enabled waited for the log writer lock and would block Unity Update.");
        AssertTrue(writeFailure == null && readFailure == null, "The synthetic writer or Enabled reader failed.");
        AssertTrue(readValue, "Enabled lost the published active state while another thread was writing.");
        AssertFalse(blockedStream.Release.IsSet, "The test released the writer before observing Enabled.");
        AssertTrue(writing.IsAlive, "The writer was not still blocked during the nonblocking read assertion.");
    }
    finally
    {
        // 即使旧实现触发超时断言，也先放开写入器并回收测试线程，避免清理代码死锁。
        blockedStream.Release.Set();
        AssertTrue(writing.Join(TimeSpan.FromSeconds(3)), "The synthetic writer did not exit after releasing Flush.");
        if (readerStarted) AssertTrue(reading.Join(TimeSpan.FromSeconds(3)), "The Enabled reader did not finish after test cleanup.");
        AggregateModLogService.Shutdown();
    }
    AssertFalse(AggregateModLogService.Enabled, "Shutdown must publish disabled after the blocked write has completed.");
    AssertEqual(0, Logger.Listeners.Count, "Nonblocking Enabled must not change listener shutdown semantics.");
    Console.WriteLine("PASS: Enabled read completed while real AppendSection Flush remained blocked; shutdown state published.");
}

static void VerifyProductionSourceContract()
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AggregateModLogService.cs")
        ?? throw new InvalidOperationException("Aggregate log source resource was not embedded.");
    using var reader = new StreamReader(stream);
    var source = reader.ReadToEnd();

    AssertContains(source, "FileShare.ReadWrite | FileShare.Delete", "Active aggregate logs no longer permit intentional external deletion.");
    AssertContains(source, "EnsureWriterLocked();\n\n                var now", "Automation repeat suppression can bypass writer health checks.");
    AssertContains(source, "_pendingWriterRecoveryReason = \"previous writer failed during append\"", "Failed writer retry state is not explicit.");
}

static string NewPath(string root, string name)
{
    return Path.Combine(root, name, "aggregate-mod.log");
}

/// <summary>
/// 读取仍由生产写入器持有的活动日志。Windows 的共享检查是双向的：测试读取句柄也
/// 必须允许既有写入/删除访问；File.ReadAllText 默认只共享读取，会在这里制造共享冲突。
/// 此处保持原来的即时内容断言，不关闭生产写入器、不重试，也不跳过日志恢复验证。
/// 关闭后的独占打开测试仍使用 FileShare.None，继续验证生产句柄确实释放。
/// </summary>
static string ReadActiveLog(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
    catch
    {
        // The primary assertion reports any leaked handle; final cleanup is best effort.
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertFalse(bool condition, string message)
{
    AssertTrue(!condition, message);
}

static void AssertContains(string value, string expected, string message)
{
    if (!value.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Missing: {expected}");
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected={expected}; Actual={actual}");
    }
}

/// <summary>只统计真正到达流的 Flush；缓冲写入次数与刷新次数分开，避免伪计数。</summary>
sealed class FlushCountingStream : MemoryStream
{
    public int FlushCount { get; private set; }

    public void ResetFlushCount() => FlushCount = 0;

    public override void Flush()
    {
        FlushCount++;
        base.Flush();
    }
}

/// <summary>仅暂停被明确武装后的第一次刷新；构造与退出的正常刷新不会额外等待。</summary>
sealed class BlockingFlushStream : MemoryStream
{
    private int _armed;
    public ManualResetEventSlim Entered { get; } = new(false);
    public ManualResetEventSlim Release { get; } = new(false);

    public void Arm() => Interlocked.Exchange(ref _armed, 1);

    public override void Flush()
    {
        if (Interlocked.Exchange(ref _armed, 0) != 0)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(12))) throw new TimeoutException("Synthetic Flush was not released.");
        }
        base.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Entered.Dispose();
            Release.Dispose();
        }
        base.Dispose(disposing);
    }
}
