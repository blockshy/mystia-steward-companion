using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeScheduledEventDiagnosticCapture
{
    private static readonly RuntimeScheduledEventDiagnosticState State =
        new(RuntimeScheduledMissionSourceReader.Limits);
    private static readonly object AttachRoot = new();

    private static ManualLogSource? _log;
    private static bool _readerReady;

    public static RuntimeScheduledEventDiagnosticSnapshot Snapshot()
    {
        return State.Snapshot();
    }

    public static RuntimeScheduledEventDiagnosticReport Report()
    {
        return State.Report();
    }

    public static void Attach(ManualLogSource log)
    {
        ArgumentNullException.ThrowIfNull(log);
        lock (AttachRoot)
        {
            _log = log;
            if (_readerReady) return;

            _readerReady = RuntimeScheduledMissionSourceReader.TryResolve(
                out var failure);
            State.SetReaderStatus(
                _readerReady
                    ? "resolved:bepinex-783-exact-shape"
                    : failure,
                attached: _readerReady,
                DateTime.UtcNow);
            if (_readerReady)
            {
                log.LogInfo(
                    "Runtime scheduled event diagnostic reader resolved; capture remains day-readiness gated.");
            }
            else
            {
                log.LogWarning(
                    "Runtime scheduled event diagnostic reader unavailable: "
                    + failure);
            }
        }
    }

    public static void ResetForMissionGeneration(
        long missionGeneration,
        DateTime changedAtUtc)
    {
        try
        {
            State.ResetForMissionGeneration(
                missionGeneration,
                Environment.CurrentManagedThreadId,
                changedAtUtc);
        }
        catch (Exception ex)
        {
            LogFailure("generation-reset", ex);
        }
    }

    public static void ArmMissionGeneration(
        long missionGeneration,
        int ownerThreadId,
        DateTime changedAtUtc)
    {
        try
        {
            if (!State.ArmMissionGeneration(
                    missionGeneration,
                    ownerThreadId,
                    changedAtUtc))
            {
                _log?.LogDebug(
                    $"Runtime scheduled event diagnostic generation {missionGeneration} was not armed because its independent state no longer matched.");
            }
        }
        catch (Exception ex)
        {
            LogFailure("generation-arm", ex);
        }
    }

    // Called by StewardOverlayController.Update on the Unity main thread.
    public static void Tick(int unityMainThreadId)
    {
        if (!_readerReady || unityMainThreadId < 1) return;

        try
        {
            var currentThreadId = Environment.CurrentManagedThreadId;
            var stateBefore = State.Snapshot();
            var dayGeneration = RuntimeSceneReadinessCapture.DaySceneGeneration;
            if (stateBefore.DaySceneGeneration == dayGeneration
                && stateBefore.Phase is RuntimeScheduledEventDiagnosticPhase.Ready
                    or RuntimeScheduledEventDiagnosticPhase.Unavailable)
            {
                return;
            }

            var missionBefore = RuntimeMissionDiagnosticCapture.Snapshot();
            if (currentThreadId != unityMainThreadId
                || missionBefore.OwnerThreadId != unityMainThreadId
                || missionBefore.Generation != stateBefore.MissionGeneration
                || missionBefore.Phase != RuntimeMissionDiagnosticPhase.Ready
                || !missionBefore.RuntimeAvailable)
            {
                return;
            }

            if (dayGeneration < 1
                || !RuntimeSceneReadinessCapture.CanReadDaySceneRuntime())
            {
                State.WaitForDayScene(
                    missionBefore.Generation,
                    dayGeneration,
                    unityMainThreadId,
                    "day scene runtime not ready",
                    DateTime.UtcNow);
                return;
            }

            if (!RuntimeMappedGuestCatalog.TryGetLoadedSnapshot(
                    out var mappedGuestSnapshot)
                || !mappedGuestSnapshot.IsComplete)
            {
                State.WaitForDayScene(
                    missionBefore.Generation,
                    dayGeneration,
                    unityMainThreadId,
                    "mapped guest identity runtime not ready",
                    DateTime.UtcNow);
                return;
            }

            if (!State.TryBeginCapture(
                    missionBefore.Generation,
                    dayGeneration,
                    unityMainThreadId,
                    DateTime.UtcNow,
                    out var token))
            {
                return;
            }

            var timer = Stopwatch.StartNew();
            try
            {
                var result = RuntimeScheduledMissionSourceReader.ReadFresh(
                    missionBefore,
                    dayGeneration,
                    mappedGuestSnapshot);
                var missionAfter = RuntimeMissionDiagnosticCapture.Snapshot();
                if (Environment.CurrentManagedThreadId != unityMainThreadId
                    || missionAfter.Generation != missionBefore.Generation
                    || missionAfter.ChangeVersion != missionBefore.ChangeVersion
                    || missionAfter.OwnerThreadId != unityMainThreadId
                    || missionAfter.Phase != RuntimeMissionDiagnosticPhase.Ready
                    || !missionAfter.RuntimeAvailable
                    || RuntimeSceneReadinessCapture.DaySceneGeneration != dayGeneration
                    || !RuntimeSceneReadinessCapture.CanReadDaySceneRuntime()
                    || !RuntimeMappedGuestCatalog.TryGetLoadedSnapshot(
                        out var mappedGuestSnapshotAfter)
                    || !ReferenceEquals(
                        mappedGuestSnapshotAfter,
                        mappedGuestSnapshot))
                {
                    timer.Stop();
                    State.FailCapture(
                        token,
                        "runtime identity changed during capture",
                        timer.ElapsedMilliseconds,
                        DateTime.UtcNow);
                    return;
                }

                State.TryCommitCapture(token, result, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                timer.Stop();
                State.FailCapture(
                    token,
                    $"capture-failed:{DescribeException(ex)}",
                    timer.ElapsedMilliseconds,
                    DateTime.UtcNow);
                LogFailure("capture", ex);
            }
        }
        catch (Exception ex)
        {
            LogFailure("tick", ex);
        }
    }

    private static string DescribeException(Exception exception)
    {
        var current = exception;
        while (current is TargetInvocationException { InnerException: not null })
        {
            current = current.InnerException;
        }
        return $"{current.GetType().Name}:{current.Message}";
    }

    private static void LogFailure(string stage, Exception exception)
    {
        _log?.LogWarning(
            $"Runtime scheduled event diagnostic {stage} failed: "
            + exception.GetBaseException().Message);
    }
}
