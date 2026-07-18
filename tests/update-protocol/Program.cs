using MystiaStewardCompanion.Updates;
using BepInEx.Logging;
using System.Text.Json;

try
{
    VerifySemanticVersionOrdering();
    VerifyManifestValidation();
    VerifyAtomParsing();
    VerifyCachedStateValidation();
    VerifyBoundedDownloadCopy();
    VerifyUpdateSchedule();
    VerifySchedulerLifecycle();
    VerifyCancelledCheckRecovery();
    VerifyManualCheckCancellation();
    VerifyDownloadCancellation();
    VerifyRecurringSchedulerAndMutualExclusion();
    VerifyDisposeRetryAfterTimeout();
    VerifyAbruptOperationRecovery();
    Console.WriteLine("PASS: update protocol validation, retry scheduling, recurring execution, mutual exclusion, lifecycle cancellation, abrupt recovery cleanup, and bounded downloads are correct.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifySemanticVersionOrdering()
{
    AssertEqual(true, SemanticVersion.TryParse("v1.2.0-preview.2", out var preview2), "preview.2 did not parse.");
    AssertEqual(true, SemanticVersion.TryParse("1.2.0-preview.10", out var preview10), "preview.10 did not parse.");
    AssertEqual(true, SemanticVersion.TryParse("1.2.0", out var stable), "stable version did not parse.");
    AssertEqual(true, preview10.CompareTo(preview2) > 0, "Numeric prerelease identifiers were compared lexically.");
    AssertEqual(true, stable.CompareTo(preview10) > 0, "Stable release did not sort after prerelease.");
}

static void VerifyManifestValidation()
{
    UpdateService.ValidateManifest(Manifest());
    ExpectInvalidManifest(Manifest(schemaVersion: 2), "schemaVersion");
    ExpectInvalidManifest(Manifest(channel: "preview"), "channel");
    ExpectInvalidManifest(Manifest(packageSha256: "abc"), "packageSha256");
    ExpectInvalidManifest(new UpdateManifest
    {
        SchemaVersion = 1,
        Version = "1.2.3",
        Tag = "v1.2.3",
        Channel = "stable",
        PackageAsset = "mystia-steward-companion-bepinex.zip",
        PackageSha256 = null!,
        PackageSize = 42,
    }, "packageSha256");
    ExpectInvalidManifest(Manifest(packageSize: 0), "packageSize");
    ExpectInvalidManifest(Manifest(tag: "v1.2.4"), "version 与 tag");
}

static void VerifyCachedStateValidation()
{
    var state = new UpdateState
    {
        LatestVersion = "1.2.4",
        LatestTag = "v1.2.4",
        PackageAsset = "mystia-steward-companion-bepinex.zip",
        PackageSha256 = new string('a', 64),
        PackageSize = 42,
        PackageDownloadUrl = "https://example.test/package.zip",
    };
    AssertEqual(true, UpdateService.HasAvailableUpdate(state), "A valid cached candidate was rejected.");
    state.PackageSize = 0;
    AssertEqual(false, UpdateService.HasAvailableUpdate(state), "A cached candidate without package size was accepted.");
}

static void VerifyBoundedDownloadCopy()
{
    var content = new byte[] { 1, 2, 3, 4 };
    using var source = new MemoryStream(content);
    using var destination = new MemoryStream();
    UpdateService.CopyDownloadContentAsync(source, destination, content.Length, CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(true, content.SequenceEqual(destination.ToArray()), "Exact-size download content did not round-trip.");

    ExpectInvalidDownloadSize(content, content.Length - 1);
    ExpectInvalidDownloadSize(content, content.Length + 1);
}

static void VerifyUpdateSchedule()
{
    var completedAtUtc = new DateTime(2026, 7, 14, 1, 2, 3, DateTimeKind.Utc);
    var expectedFailureDelays = new[]
    {
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(4),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(6),
    };
    for (var index = 0; index < expectedFailureDelays.Length; index += 1)
    {
        var failureCount = index + 1;
        AssertEqual(
            expectedFailureDelays[index],
            UpdateService.CalculateFailureRetryDelay(failureCount),
            $"Unexpected retry delay for failure {failureCount}.");
        AssertEqual(
            completedAtUtc + expectedFailureDelays[index],
            UpdateService.CalculateNextCheckAtUtc(completedAtUtc, false, failureCount, 24),
            $"Unexpected retry timestamp for failure {failureCount}.");
    }

    AssertEqual(
        completedAtUtc + TimeSpan.FromHours(1),
        UpdateService.CalculateNextCheckAtUtc(completedAtUtc, true, 0, 0),
        "Successful checks did not clamp the minimum interval.");
    AssertEqual(
        completedAtUtc + TimeSpan.FromHours(168),
        UpdateService.CalculateNextCheckAtUtc(completedAtUtc, true, 0, 999),
        "Successful checks did not clamp the maximum interval.");
}

static void VerifySchedulerLifecycle()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-scheduler-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-scheduler-smoke");
    try
    {
        var nowUtc = new DateTime(2026, 7, 14, 2, 0, 0, DateTimeKind.Utc);
        var fetchCount = 0;
        using var service = new UpdateService(
            Settings(),
            log,
            Path.Combine(root, "single"),
            () => nowUtc,
            _ =>
            {
                Interlocked.Increment(ref fetchCount);
                return Candidate();
            });

        service.StartAutoCheckScheduler();
        service.StartAutoCheckScheduler();
        if (!SpinWait.SpinUntil(
                () => !string.IsNullOrWhiteSpace(service.GetStatus().LastSuccessAtUtc),
                TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("The automatic update scheduler did not complete its due check.");
        }

        Thread.Sleep(100);
        AssertEqual(1, fetchCount, "Starting the scheduler twice created duplicate checks.");
        var status = service.GetStatus();
        AssertEqual(nowUtc.ToString("O"), status.LastAttemptAtUtc, "Last attempt time was not exposed.");
        AssertEqual(nowUtc.ToString("O"), status.LastSuccessAtUtc, "Last success time was not exposed.");
        AssertEqual((nowUtc + TimeSpan.FromHours(24)).ToString("O"), status.NextCheckAtUtc, "Next successful check time was incorrect.");
        AssertEqual(0, status.ConsecutiveFailures, "A successful check retained a failure count.");
        service.Dispose();
        service.Dispose();
        ExpectDisposed(service.StartAutoCheckScheduler);

        var failureNowUtc = nowUtc;
        using (var failureService = new UpdateService(
                   Settings(),
                   log,
                   Path.Combine(root, "failure"),
                   () => failureNowUtc,
                   _ => throw new HttpRequestException("expected network failure")))
        {
            var firstFailure = failureService.CheckForUpdates();
            AssertEqual(1, firstFailure.ConsecutiveFailures, "The first failed check did not increment the failure count.");
            AssertEqual(
                (failureNowUtc + TimeSpan.FromMinutes(15)).ToString("O"),
                firstFailure.NextCheckAtUtc,
                "The first failed check did not schedule a fifteen-minute retry.");

            failureNowUtc += TimeSpan.FromMinutes(1);
            var secondFailure = failureService.CheckForUpdates();
            AssertEqual(2, secondFailure.ConsecutiveFailures, "The second failed check did not retain the failure sequence.");
            AssertEqual(
                (failureNowUtc + TimeSpan.FromMinutes(30)).ToString("O"),
                secondFailure.NextCheckAtUtc,
                "The second failed check did not schedule a thirty-minute retry.");
            AssertEqual("", secondFailure.LastSuccessAtUtc, "A failed check was recorded as successful.");
        }

        using var fetchStarted = new ManualResetEventSlim();
        using var fetchCancelled = new ManualResetEventSlim();
        using var cancellableService = new UpdateService(
            Settings(),
            log,
            Path.Combine(root, "cancellable"),
            () => nowUtc,
            cancellationToken =>
            {
                fetchStarted.Set();
                cancellationToken.WaitHandle.WaitOne();
                fetchCancelled.Set();
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation was not observed.");
            });
        cancellableService.StartAutoCheckScheduler();
        if (!fetchStarted.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("The cancellable scheduler did not start its check.");
        }

        cancellableService.Dispose();
        AssertEqual(true, fetchCancelled.IsSet, "Disposing the update service did not cancel an active automatic check.");
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static void VerifyCancelledCheckRecovery()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-recovery-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-recovery-smoke");
    try
    {
        var nowUtc = new DateTime(2026, 7, 14, 3, 0, 0, DateTimeKind.Utc);
        using (var failedService = new UpdateService(
                   Settings(),
                   log,
                   root,
                   () => nowUtc,
                   _ => throw new HttpRequestException("expected prior failure")))
        {
            AssertEqual(1, failedService.CheckForUpdates().ConsecutiveFailures, "The recovery fixture did not persist a prior failure.");
        }

        nowUtc += TimeSpan.FromMinutes(15);
        using var cancelledFetchStarted = new ManualResetEventSlim();
        using var cancelledFetchObserved = new ManualResetEventSlim();
        using (var interruptedService = new UpdateService(
                   Settings(),
                   log,
                   root,
                   () => nowUtc,
                   cancellationToken =>
                   {
                       cancelledFetchStarted.Set();
                       cancellationToken.WaitHandle.WaitOne();
                       cancelledFetchObserved.Set();
                       cancellationToken.ThrowIfCancellationRequested();
                       throw new InvalidOperationException("Cancellation was not observed.");
                   }))
        {
            interruptedService.StartAutoCheckScheduler();
            AssertEqual(true, cancelledFetchStarted.Wait(TimeSpan.FromSeconds(2)), "The due retry did not start.");
            interruptedService.BeginShutdown();
            interruptedService.Dispose();
        }
        AssertEqual(true, cancelledFetchObserved.IsSet, "The interrupted retry did not observe service shutdown.");

        var resumedFetchCount = 0;
        using var resumedService = new UpdateService(
            Settings(),
            log,
            root,
            () => nowUtc,
            _ =>
            {
                Interlocked.Increment(ref resumedFetchCount);
                return Candidate();
            });
        var recoveredStatus = resumedService.GetStatus();
        AssertEqual(false, recoveredStatus.State == "checking", "A cancelled check remained persisted as active.");
        AssertEqual(nowUtc.ToString("O"), recoveredStatus.NextCheckAtUtc, "A cancelled check retained its previous failure backoff.");

        resumedService.StartAutoCheckScheduler();
        if (!SpinWait.SpinUntil(
                () => !string.IsNullOrWhiteSpace(resumedService.GetStatus().LastSuccessAtUtc),
                TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("The service did not immediately retry after a cancelled check restart.");
        }
        AssertEqual(1, resumedFetchCount, "Restart recovery started an unexpected number of checks.");
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static void VerifyManualCheckCancellation()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-manual-cancel-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-manual-cancel-smoke");
    try
    {
        var nowUtc = new DateTime(2026, 7, 14, 4, 0, 0, DateTimeKind.Utc);
        using var fetchStarted = new ManualResetEventSlim();
        using var fetchCancelled = new ManualResetEventSlim();
        var fetchCount = 0;
        using var service = new UpdateService(
            Settings(),
            log,
            root,
            () => nowUtc,
            cancellationToken =>
            {
                Interlocked.Increment(ref fetchCount);
                fetchStarted.Set();
                cancellationToken.WaitHandle.WaitOne();
                fetchCancelled.Set();
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation was not observed.");
            });

        var checkTask = Task.Run(service.CheckForUpdates);
        AssertEqual(true, fetchStarted.Wait(TimeSpan.FromSeconds(2)), "The manual check did not start.");
        service.BeginShutdown();
        var rejectedStatus = service.CheckForUpdates();
        var status = WaitForTask(checkTask, "The manual check did not stop after service shutdown.");
        service.Dispose();

        AssertEqual(false, rejectedStatus.Ok, "A new update operation entered after shutdown began.");
        AssertEqual(1, fetchCount, "A new update fetch started after shutdown began.");
        AssertEqual(true, fetchCancelled.IsSet, "The manual check did not receive the service cancellation token.");
        AssertEqual(false, status.Ok, "A shutdown-cancelled manual check reported success.");
        AssertEqual("idle", status.State, "A shutdown-cancelled manual check did not restore a stable state.");
        AssertEqual(nowUtc.ToString("O"), status.NextCheckAtUtc, "A shutdown-cancelled manual check was not immediately rescheduled.");
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static void VerifyDownloadCancellation()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-download-cancel-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-download-cancel-smoke");
    try
    {
        var nowUtc = new DateTime(2026, 7, 14, 5, 0, 0, DateTimeKind.Utc);
        using var downloadStarted = new ManualResetEventSlim();
        using var downloadCancelled = new ManualResetEventSlim();
        using var service = new UpdateService(
            Settings(),
            log,
            root,
            () => nowUtc,
            _ => Candidate(),
            downloadFile: (_, _, _, cancellationToken) =>
            {
                downloadStarted.Set();
                cancellationToken.WaitHandle.WaitOne();
                downloadCancelled.Set();
                cancellationToken.ThrowIfCancellationRequested();
            });
        AssertEqual(true, service.CheckForUpdates().HasUpdate, "The download fixture did not discover an update.");

        var downloadTask = Task.Run(service.DownloadUpdate);
        AssertEqual(true, downloadStarted.Wait(TimeSpan.FromSeconds(2)), "The update download did not start.");
        service.BeginShutdown();
        var status = WaitForTask(downloadTask, "The update download did not stop after service shutdown.");
        service.Dispose();

        AssertEqual(true, downloadCancelled.IsSet, "The download did not receive the service cancellation token.");
        AssertEqual(false, status.Ok, "A shutdown-cancelled download reported success.");
        AssertEqual("available", status.State, "A shutdown-cancelled download did not restore the available update state.");
        var downloadsRoot = Path.Combine(root, "downloads");
        var pendingDirectories = Directory.Exists(downloadsRoot)
            ? Directory.EnumerateDirectories(downloadsRoot, ".*.tmp", SearchOption.TopDirectoryOnly).ToArray()
            : Array.Empty<string>();
        AssertEqual(0, pendingDirectories.Length, "A shutdown-cancelled download left a temporary directory behind.");
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static void VerifyRecurringSchedulerAndMutualExclusion()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-recurring-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-recurring-smoke");
    try
    {
        var clock = new AdvancingSchedulerClock(new DateTime(2026, 7, 14, 6, 0, 0, DateTimeKind.Utc));
        using var secondCheckStarted = new ManualResetEventSlim();
        var fetchCount = 0;
        using var service = new UpdateService(
            Settings(),
            log,
            root,
            clock.UtcNow,
            cancellationToken =>
            {
                var currentFetch = Interlocked.Increment(ref fetchCount);
                if (currentFetch == 2)
                {
                    secondCheckStarted.Set();
                    cancellationToken.WaitHandle.WaitOne();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return Candidate();
            },
            waitForScheduler: clock.Wait);

        service.StartAutoCheckScheduler();
        AssertEqual(true, secondCheckStarted.Wait(TimeSpan.FromSeconds(2)), "The scheduler did not run again after the successful interval.");
        var busyStatus = service.CheckForUpdates();
        AssertEqual(false, busyStatus.Ok, "A manual check entered while the automatic check owned the operation lock.");
        AssertEqual(true, busyStatus.Error?.Contains("另一项更新操作", StringComparison.Ordinal) == true, "The operation lock did not return the expected busy status.");
        AssertEqual(2, fetchCount, "The busy manual request executed another update fetch.");
        service.Dispose();
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static void VerifyDisposeRetryAfterTimeout()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-dispose-retry-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-dispose-retry-smoke");
    try
    {
        using var fetchStarted = new ManualResetEventSlim();
        using var releaseFetch = new ManualResetEventSlim();
        using var service = new UpdateService(
            Settings(),
            log,
            root,
            static () => new DateTime(2026, 7, 14, 7, 0, 0, DateTimeKind.Utc),
            _ =>
            {
                fetchStarted.Set();
                releaseFetch.Wait();
                return Candidate();
            },
            shutdownTimeout: TimeSpan.FromMilliseconds(25));

        service.StartAutoCheckScheduler();
        AssertEqual(true, fetchStarted.Wait(TimeSpan.FromSeconds(2)), "The timeout fixture did not start its scheduler check.");
        service.Dispose();
        AssertEqual(false, service.IsDisposeComplete, "A timed-out scheduler disposal reported complete cleanup.");

        releaseFetch.Set();
        if (!SpinWait.SpinUntil(
                () => service.GetStatus().State != "checking",
                TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("The scheduler did not exit after the blocked fetch was released.");
        }
        service.Dispose();
        AssertEqual(true, service.IsDisposeComplete, "A second Dispose did not finish cleanup after the scheduler exited.");
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static void VerifyAbruptOperationRecovery()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-abrupt-recovery-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-abrupt-recovery-smoke");
    try
    {
        var nowUtc = new DateTime(2026, 7, 14, 8, 0, 0, DateTimeKind.Utc);
        var idleRoot = Path.Combine(root, "idle");
        var downloadsRoot = Path.Combine(idleRoot, "downloads");
        var pendingDirectory = Path.Combine(downloadsRoot, $".1.2.4.{Guid.NewGuid():N}.tmp");
        var unrelatedDirectory = Path.Combine(downloadsRoot, "user-content");
        var invalidVersionDirectory = Path.Combine(downloadsRoot, $".not-a-version.{Guid.NewGuid():N}.tmp");
        var invalidGuidDirectory = Path.Combine(downloadsRoot, ".1.2.4.not-a-guid.tmp");
        var nestedPendingDirectory = Path.Combine(unrelatedDirectory, $".1.2.4.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(pendingDirectory);
        Directory.CreateDirectory(invalidVersionDirectory);
        Directory.CreateDirectory(invalidGuidDirectory);
        Directory.CreateDirectory(nestedPendingDirectory);
        WriteUpdateState(idleRoot, new UpdateState
        {
            State = "checking",
            LastAttemptAtUtc = nowUtc - TimeSpan.FromMinutes(1),
            NextCheckAtUtc = nowUtc + TimeSpan.FromHours(6),
            ConsecutiveFailures = 6,
            Error = "stale checking state",
        });

        var resumedFetchCount = 0;
        using (var idleService = new UpdateService(
                   Settings(),
                   log,
                   idleRoot,
                   () => nowUtc,
                   _ =>
                   {
                       Interlocked.Increment(ref resumedFetchCount);
                       return Candidate();
                   }))
        {
            var status = idleService.GetStatus();
            AssertEqual("idle", status.State, "Abrupt checking state without metadata did not recover to idle.");
            AssertEqual(nowUtc.ToString("O"), status.NextCheckAtUtc, "Abrupt checking recovery retained the old six-hour backoff.");
            AssertEqual(false, Directory.Exists(pendingDirectory), "A service-format pending download directory was not removed.");
            AssertEqual(true, Directory.Exists(unrelatedDirectory), "An unrelated downloads directory was removed.");
            AssertEqual(true, Directory.Exists(invalidVersionDirectory), "A non-version temporary directory was removed.");
            AssertEqual(true, Directory.Exists(invalidGuidDirectory), "A temporary directory without a service GUID was removed.");
            AssertEqual(true, Directory.Exists(nestedPendingDirectory), "Recovery scanned below the downloads root first level.");

            idleService.StartAutoCheckScheduler();
            if (!SpinWait.SpinUntil(
                    () => !string.IsNullOrWhiteSpace(idleService.GetStatus().LastSuccessAtUtc),
                    TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("Abrupt checking recovery did not immediately run the due check.");
            }
            AssertEqual(1, resumedFetchCount, "Abrupt checking recovery ran an unexpected number of immediate checks.");
        }

        var currentRoot = Path.Combine(root, "current");
        WriteUpdateState(currentRoot, new UpdateState
        {
            State = "checking",
            LastSuccessAtUtc = nowUtc - TimeSpan.FromHours(1),
            NextCheckAtUtc = nowUtc + TimeSpan.FromHours(24),
        });
        using (var currentService = NewRecoveryService(currentRoot, nowUtc, log))
        {
            var status = currentService.GetStatus();
            AssertEqual("current", status.State, "Abrupt checking state with a successful check did not recover to current.");
            AssertEqual(nowUtc.ToString("O"), status.NextCheckAtUtc, "Abrupt current recovery was not immediately due.");
        }

        var stableRoot = Path.Combine(root, "stable-cleanup");
        var stablePendingDirectory = Path.Combine(
            stableRoot,
            "downloads",
            $".1.2.4.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stablePendingDirectory);
        WriteUpdateState(stableRoot, new UpdateState
        {
            State = "current",
            LastSuccessAtUtc = nowUtc - TimeSpan.FromHours(1),
            NextCheckAtUtc = nowUtc + TimeSpan.FromHours(24),
        });
        using (var stableService = NewRecoveryService(stableRoot, nowUtc, log))
        {
            var status = stableService.GetStatus();
            AssertEqual("current", status.State, "Startup cleanup changed an already stable update state.");
            AssertEqual(
                (nowUtc + TimeSpan.FromHours(24)).ToString("O"),
                status.NextCheckAtUtc,
                "Startup cleanup rescheduled an already stable update state.");
            AssertEqual(
                false,
                Directory.Exists(stablePendingDirectory),
                "A pending directory left by a normally cancelled download was not retried on startup.");
        }

        var availableRoot = Path.Combine(root, "available");
        WriteUpdateState(availableRoot, AvailableState("downloading", nowUtc));
        using (var availableService = NewRecoveryService(availableRoot, nowUtc, log))
        {
            var status = availableService.GetStatus();
            AssertEqual("available", status.State, "Abrupt downloading state with a valid candidate did not recover to available.");
            AssertEqual(nowUtc.ToString("O"), status.NextCheckAtUtc, "Abrupt available recovery was not immediately due.");
        }

        var downloadedRoot = Path.Combine(root, "downloaded");
        var stagedDirectory = Path.Combine(downloadedRoot, "staged", "plugin");
        Directory.CreateDirectory(stagedDirectory);
        var downloadedState = AvailableState("downloading", nowUtc);
        downloadedState.DownloadedVersion = "1.2.4";
        downloadedState.StagedDirectory = stagedDirectory;
        WriteUpdateState(downloadedRoot, downloadedState);
        using (var downloadedService = NewRecoveryService(downloadedRoot, nowUtc, log))
        {
            var status = downloadedService.GetStatus();
            AssertEqual("downloaded", status.State, "Abrupt downloading state with a staged package did not recover to downloaded.");
            AssertEqual(nowUtc.ToString("O"), status.NextCheckAtUtc, "Abrupt downloaded recovery was not immediately due.");
        }
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static UpdateService NewRecoveryService(string root, DateTime nowUtc, ManualLogSource log)
{
    return new UpdateService(Settings(), log, root, () => nowUtc, _ => Candidate());
}

static UpdateState AvailableState(string state, DateTime nowUtc)
{
    return new UpdateState
    {
        State = state,
        LastAttemptAtUtc = nowUtc - TimeSpan.FromMinutes(1),
        LastSuccessAtUtc = nowUtc - TimeSpan.FromHours(1),
        NextCheckAtUtc = nowUtc + TimeSpan.FromHours(24),
        LatestVersion = "1.2.4",
        LatestTag = "v1.2.4",
        PackageAsset = "mystia-steward-companion-bepinex.zip",
        PackageSha256 = new string('a', 64),
        PackageSize = 42,
        PackageDownloadUrl = "https://example.test/package.zip",
    };
}

static void WriteUpdateState(string root, UpdateState state)
{
    Directory.CreateDirectory(root);
    File.WriteAllText(
        Path.Combine(root, "update-state.json"),
        JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}

static UpdateStatus WaitForTask(Task<UpdateStatus> task, string timeoutMessage)
{
    if (!task.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException(timeoutMessage);
    return task.GetAwaiter().GetResult();
}

static UpdateServiceSettings Settings()
{
    return new UpdateServiceSettings
    {
        Enabled = true,
        AutoCheck = true,
        CheckIntervalHours = 24,
        IncludePrerelease = false,
    };
}

static UpdateCandidate Candidate()
{
    return new UpdateCandidate
    {
        Manifest = Manifest(version: "1.2.4", tag: "v1.2.4"),
        ReleaseUrl = "https://example.test/releases/v1.2.4",
        PublishedAtUtc = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc),
        PackageDownloadUrl = "https://example.test/releases/v1.2.4/package.zip",
    };
}

static void ExpectDisposed(Action action)
{
    try
    {
        action();
        throw new InvalidOperationException("A disposed update service restarted its scheduler.");
    }
    catch (ObjectDisposedException)
    {
    }
}

static void ExpectInvalidDownloadSize(byte[] content, long expectedSize)
{
    using var source = new MemoryStream(content);
    using var destination = new MemoryStream();
    try
    {
        UpdateService.CopyDownloadContentAsync(source, destination, expectedSize, CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Expected an invalid download size error.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("大小", StringComparison.Ordinal))
    {
    }
}

static void VerifyAtomParsing()
{
    const string xml = "<?xml version=\"1.0\"?><feed xmlns=\"http://www.w3.org/2005/Atom\">"
        + "<entry><title>v1.2.0-preview.10</title><link href=\"https://example.test/preview?a=1&amp;b=2\" rel=\"alternate\"/><updated>2026-07-01T00:00:00Z</updated></entry>"
        + "<entry><title>v1.2.0</title><link rel=\"alternate\" href=\"https://example.test/stable\"/><updated>2026-07-02T00:00:00Z</updated></entry>"
        + "<entry><title>not-a-version</title></entry></feed>";
    var releases = UpdateService.ParseReleaseFeed(xml);
    AssertEqual(2, releases.Count, "Unexpected number of parsed releases.");
    AssertEqual("v1.2.0", releases[0].TagName, "Stable release was not sorted first.");
    AssertEqual("https://example.test/preview?a=1&b=2", releases[1].HtmlUrl, "Atom link attributes/entities were not parsed structurally.");
}

static UpdateManifest Manifest(
    int schemaVersion = 1,
    string version = "1.2.3",
    string tag = "v1.2.3",
    string channel = "stable",
    string? packageSha256 = null,
    long packageSize = 42)
{
    return new UpdateManifest
    {
        SchemaVersion = schemaVersion,
        Version = version,
        Tag = tag,
        Channel = channel,
        PackageAsset = "mystia-steward-companion-bepinex.zip",
        PackageSha256 = packageSha256 ?? new string('a', 64),
        PackageSize = packageSize,
    };
}

static void ExpectInvalidManifest(UpdateManifest manifest, string expectedMessage)
{
    try
    {
        UpdateService.ValidateManifest(manifest);
        throw new InvalidOperationException($"Manifest containing invalid {expectedMessage} was accepted.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains(expectedMessage, StringComparison.Ordinal))
    {
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}

sealed class AdvancingSchedulerClock
{
    private readonly object _lock = new();
    private DateTime _utcNow;

    public AdvancingSchedulerClock(DateTime utcNow)
    {
        _utcNow = utcNow;
    }

    public DateTime UtcNow()
    {
        lock (_lock)
        {
            return _utcNow;
        }
    }

    public bool Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return true;
        lock (_lock)
        {
            _utcNow += delay;
        }
        Thread.Yield();
        return cancellationToken.IsCancellationRequested;
    }
}
