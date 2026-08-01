using System.Reflection;
using System.Collections.Concurrent;
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
    VerifyProductionSourceContract();
    Console.WriteLine(
        "PASS: aggregate log recreates externally removed paths under sequential and concurrent writes, "
        + "retries failed writers, keeps one listener, releases handles, and preserves rotation semantics.");
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

    var content = File.ReadAllText(path);
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
    AssertContains(File.ReadAllText(path), "after removal", "Logging did not resume after recreating the parent directory.");
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
    AssertContains(File.ReadAllText(path), "aggregate log writer recovered", "Same-parameter Configure did not recreate the missing active path.");
    AggregateModLogService.Shutdown();
}

static void VerifyBepInExEventRecreatesDeletedActivePath(string root)
{
    var path = NewPath(root, "bepinex-event");
    AggregateModLogService.Configure(true, path, 3);
    File.Delete(path);

    Logger.Emit("event after external removal");

    var content = File.ReadAllText(path);
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

    var content = File.ReadAllText(path);
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
    AssertContains(File.ReadAllText(path), "final sentinel", "Final append did not recover after concurrent deletion stopped.");
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
    AssertContains(File.ReadAllText(path), "aggregate log enabled", "New active file did not receive the service boundary.");
    AssertEqual(2, AggregateModLogService.EnumerateFiles(path).Count, "Rotation exceeded the configured file count.");
    AggregateModLogService.Shutdown();
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
