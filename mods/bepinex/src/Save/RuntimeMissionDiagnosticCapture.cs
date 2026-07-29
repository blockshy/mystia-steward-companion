using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeMissionDiagnosticCapture
{
    private const string SaveManagementTypeName = "GameData.Utils.SaveManagement";
    private const string PlayerSaveFileTypeName = "GameData.RunTime.Common.PlayerSaveFile";
    private const string SchedulerTypeName = "GameData.RunTime.Common.RunTimeScheduler";
    private const string TrackedMissionTypeName =
        "GameData.RunTime.Common.RunTimeScheduler+TrackedMissionData";
    private const string FormattingTypeName = "Newtonsoft.Json.Formatting";
    private const int ExpectedHookCount = 9;
    private const int MaxDefinitionReadsPerLoad = 512;
    private static readonly TimeSpan PendingLoadMaximumAge = TimeSpan.FromSeconds(30);

    private static readonly object AttachRoot = new();
    private static readonly RuntimeMissionDiagnosticState State = new();

    [ThreadStatic]
    private static Stack<StartHookFrame>? _startFrames;

    [ThreadStatic]
    private static PendingLoadSeed? _pendingLoadSeed;

    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static RuntimeShape? _shape;
    private static bool _hooksReady;

    public static RuntimeMissionDiagnosticSnapshot Snapshot()
    {
        return State.Snapshot();
    }

    public static RuntimeMissionDiagnosticReport Report()
    {
        return State.Report();
    }

    public static RuntimeTrackedMissionsSnapshot ReadTrackedMissions()
    {
        return State.ReadTrackedMissions();
    }

    public static string Status
    {
        get
        {
            var snapshot = Snapshot();
            return $"hooks={snapshot.HookStatus}; phase={snapshot.Phase}; "
                + $"available={snapshot.RuntimeAvailable}; generation={snapshot.Generation}; "
                + $"trackingBuckets={snapshot.TrackingBucketCount}; buffer={snapshot.TrackingBufferCount}; "
                + $"active={snapshot.ActiveMissionCount}; unverified={snapshot.UnverifiedMissionCount}; "
                + $"tracking={snapshot.TrackingMissionCount}; fulfilled={snapshot.FulfilledMissionCount}; "
                + $"finishedUnique={snapshot.FinishedUniqueMissionCount}; definitions="
                + $"{snapshot.DefinitionAvailableCount}/{snapshot.ActiveMissionCount}; "
                + $"starts={snapshot.StartCommitCount}/{snapshot.StartAttemptCount}; "
                + $"removes={snapshot.RemoveCount}; finishes={snapshot.FinishCount}; "
                + $"refreshes={snapshot.StateRefreshCount}; last={snapshot.LastEvent}; "
                + $"error={snapshot.LastError}";
        }
    }

    public static bool TryGetServeInWorkDefinitions(
        long generation,
        out IReadOnlyList<RuntimeMissionServeInWorkDefinition> definitions)
    {
        return State.TryGetServeInWorkDefinitions(generation, out definitions);
    }

    public static void RefreshPresentations(
        long daySceneGeneration,
        RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot)
    {
        ArgumentNullException.ThrowIfNull(mappedGuestSnapshot);
        var before = Snapshot();
        if (!mappedGuestSnapshot.IsComplete
            || !State.TryReadPresentationRequests(
                before.Generation,
                daySceneGeneration,
                mappedGuestSnapshot.CapturedAtUtc,
                DateTime.UtcNow,
                out var requests))
        {
            return;
        }
        if (requests.Count == 0)
        {
            return;
        }

        try
        {
            var presentations = RuntimeMissionPresentationReader.ReadMany(
                requests
                    .Select(request => request.ReceiverLabel)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                mappedGuestSnapshot,
                before.Generation,
                daySceneGeneration);
            var results = new RuntimeMissionPresentationApply[requests.Count];
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                if (!presentations.TryGetValue(
                        request.ReceiverLabel,
                        out var presentation))
                {
                    return;
                }
                results[index] = new RuntimeMissionPresentationApply(
                    request.Label,
                    request.ReceiverLabel,
                    presentation);
            }
            if (!State.TryApplyPresentations(
                    before.Generation,
                    daySceneGeneration,
                    mappedGuestSnapshot.CapturedAtUtc,
                    results,
                    DateTime.UtcNow,
                    out var readyCount))
            {
                return;
            }

            var samples = presentations.Values
                .Where(presentation => !string.Equals(
                    presentation.PresentationStatus,
                    RuntimeMissionPresentation.ReadyStatus,
                    StringComparison.Ordinal))
                .Select(presentation => presentation.PresentationStatus)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(status => status, StringComparer.Ordinal)
                .Take(4)
                .ToArray();
            _log?.LogInfo(
                "Runtime mission presentation metadata refreshed: "
                + $"generation={before.Generation}; daySceneGeneration={daySceneGeneration}; "
                + $"ready={readyCount}/{requests.Count}; "
                + $"statuses={(samples.Length == 0 ? "ready" : string.Join(",", samples))}.");
        }
        catch (Exception ex)
        {
            _log?.LogWarning(
                "Runtime mission presentation metadata refresh failed: "
                + $"{ex.GetType().Name}:{ex.GetBaseException().Message}");
        }
    }

    public static void Attach(ManualLogSource log)
    {
        ArgumentNullException.ThrowIfNull(log);
        lock (AttachRoot)
        {
            _log = log;
            if (_hooksReady) return;

            try
            {
                var shape = RuntimeShape.Resolve();
                var harmony = _harmony ??= new Harmony(
                    "com.tyukki.mystia-steward-companion.runtime-mission-diagnostic-capture");
                PatchTryUpgrade(harmony, shape.TryUpgradeSaveVersion);
                PatchInitialize(harmony, shape.Initialize);
                PatchStartMission(harmony, shape.StartMission);
                PatchObservedPostfix(
                    harmony,
                    shape.GenerateTrackingData,
                    nameof(OnGenerateTrackingDataPostfix));
                PatchObservedPostfix(
                    harmony,
                    shape.RemoveMissionFromList,
                    nameof(OnRemoveMissionPostfix));
                PatchObservedPostfix(
                    harmony,
                    shape.FinishMission,
                    nameof(OnFinishMissionPostfix));
                PatchObservedPostfix(
                    harmony,
                    shape.SetFinishedMissions,
                    nameof(OnSetFinishedMissionPostfix));
                PatchFinishNode(harmony, shape.FinishNodeExtern);
                PatchObservedPostfix(
                    harmony,
                    shape.UpdateFinishStates,
                    nameof(OnUpdateFinishStatesPostfix));

                _shape = shape;
                _hooksReady = true;
                State.SetHookStatus(
                    $"patched:{ExpectedHookCount}/{ExpectedHookCount}",
                    attached: true,
                    DateTime.UtcNow);
                log.LogInfo(
                    "Runtime mission diagnostic capture patched with load seed and stable lifecycle methods.");
            }
            catch (Exception ex)
            {
                _shape = null;
                _hooksReady = false;
                State.SetHookStatus("unavailable", attached: false, DateTime.UtcNow);
                log.LogWarning(
                    "Runtime mission diagnostic capture unavailable; no task data will be published: "
                    + ex.GetBaseException().Message);
            }
        }
    }

    private static void PatchTryUpgrade(Harmony harmony, MethodInfo target)
    {
        harmony.Patch(
            target,
            prefix: new HarmonyMethod(RequireHook(nameof(OnTryUpgradePrefix)))
            {
                priority = Priority.First,
            },
            postfix: new HarmonyMethod(RequireHook(nameof(OnTryUpgradePostfix)))
            {
                priority = Priority.Last,
            },
            finalizer: new HarmonyMethod(RequireHook(nameof(OnTryUpgradeFinalizer)))
            {
                priority = Priority.Last,
            });
    }

    private static void PatchInitialize(Harmony harmony, MethodInfo target)
    {
        harmony.Patch(
            target,
            prefix: new HarmonyMethod(RequireHook(nameof(OnInitializePrefix)))
            {
                priority = Priority.First,
            },
            postfix: new HarmonyMethod(RequireHook(nameof(OnInitializePostfix)))
            {
                priority = Priority.Last,
            },
            finalizer: new HarmonyMethod(RequireHook(nameof(OnInitializeFinalizer)))
            {
                priority = Priority.Last,
            });
    }

    private static void PatchStartMission(Harmony harmony, MethodInfo target)
    {
        harmony.Patch(
            target,
            prefix: new HarmonyMethod(RequireHook(nameof(OnStartMissionPrefix)))
            {
                priority = Priority.First,
            },
            postfix: new HarmonyMethod(RequireHook(nameof(OnStartMissionPostfix)))
            {
                priority = Priority.Last,
            },
            finalizer: new HarmonyMethod(RequireHook(nameof(OnStartMissionFinalizer)))
            {
                priority = Priority.Last,
            });
    }

    private static void PatchObservedPostfix(
        Harmony harmony,
        MethodInfo target,
        string hookName)
    {
        harmony.Patch(
            target,
            postfix: new HarmonyMethod(RequireHook(hookName)) { priority = Priority.Last },
            finalizer: new HarmonyMethod(RequireHook(nameof(OnObservedMethodFinalizer)))
            {
                priority = Priority.Last,
            });
    }

    private static void PatchFinishNode(Harmony harmony, MethodInfo target)
    {
        harmony.Patch(
            target,
            prefix: new HarmonyMethod(RequireHook(nameof(OnFinishNodePrefix)))
            {
                priority = Priority.First,
            },
            postfix: new HarmonyMethod(RequireHook(nameof(OnFinishNodePostfix)))
            {
                priority = Priority.Last,
            },
            finalizer: new HarmonyMethod(RequireHook(nameof(OnFinishNodeFinalizer)))
            {
                priority = Priority.Last,
            });
    }

    private static MethodInfo RequireHook(string name)
    {
        return typeof(RuntimeMissionDiagnosticCapture).GetMethod(
                name,
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing runtime mission hook {name}.");
    }

    // Il2CppInterop 783 continues native return marshalling after reporting a hook exception.
    // These outer shells therefore must never let diagnostic failures escape the trampoline.
    private static void OnTryUpgradePrefix(out LoadHookFrame? __state)
    {
        __state = null;
        try
        {
            OnTryUpgradePrefixUnsafe(out __state);
        }
        catch
        {
        }
    }

    private static void OnTryUpgradePostfix(
        object? __result,
        LoadHookFrame? __state)
    {
        try
        {
            OnTryUpgradePostfixUnsafe(__result, __state);
        }
        catch
        {
        }
    }

    private static Exception? OnTryUpgradeFinalizer(
        Exception? __exception,
        LoadHookFrame? __state)
    {
        try
        {
            _ = OnTryUpgradeFinalizerUnsafe(__exception, __state);
        }
        catch
        {
        }

        return __exception;
    }

    private static void OnInitializePrefix(
        object? __1,
        int __2,
        out InitializeHookFrame? __state)
    {
        __state = null;
        try
        {
            OnInitializePrefixUnsafe(__1, __2, out __state);
        }
        catch
        {
        }
    }

    private static void OnInitializePostfix(InitializeHookFrame? __state)
    {
        try
        {
            OnInitializePostfixUnsafe(__state);
        }
        catch
        {
        }
    }

    private static Exception? OnInitializeFinalizer(
        Exception? __exception,
        InitializeHookFrame? __state)
    {
        try
        {
            _ = OnInitializeFinalizerUnsafe(__exception, __state);
        }
        catch
        {
        }

        return __exception;
    }

    private static void OnStartMissionPrefix(
        object? __0,
        out StartHookFrame? __state)
    {
        __state = null;
        try
        {
            OnStartMissionPrefixUnsafe(__0, out __state);
        }
        catch
        {
        }
    }

    private static void OnGenerateTrackingDataPostfix(object? __result)
    {
        try
        {
            OnGenerateTrackingDataPostfixUnsafe(__result);
        }
        catch
        {
        }
    }

    private static void OnStartMissionPostfix(StartHookFrame? __state)
    {
        try
        {
            OnStartMissionPostfixUnsafe(__state);
        }
        catch
        {
        }
    }

    private static Exception? OnStartMissionFinalizer(
        Exception? __exception,
        StartHookFrame? __state)
    {
        try
        {
            _ = OnStartMissionFinalizerUnsafe(__exception, __state);
        }
        catch
        {
        }

        return __exception;
    }

    private static Exception? OnObservedMethodFinalizer(
        Exception? __exception,
        MethodBase __originalMethod)
    {
        try
        {
            _ = OnObservedMethodFinalizerUnsafe(__exception, __originalMethod);
        }
        catch
        {
        }

        return __exception;
    }

    private static void OnRemoveMissionPostfix(object? __0)
    {
        try
        {
            OnRemoveMissionPostfixUnsafe(__0);
        }
        catch
        {
        }
    }

    private static void OnFinishMissionPostfix(object? __0)
    {
        try
        {
            OnFinishMissionPostfixUnsafe(__0);
        }
        catch
        {
        }
    }

    private static void OnSetFinishedMissionPostfix(object? __0)
    {
        try
        {
            OnSetFinishedMissionPostfixUnsafe(__0);
        }
        catch
        {
        }
    }

    private static void OnFinishNodePrefix(out FinishNodeHookFrame? __state)
    {
        __state = null;
        try
        {
            OnFinishNodePrefixUnsafe(out __state);
        }
        catch
        {
        }
    }

    private static void OnFinishNodePostfix(FinishNodeHookFrame? __state)
    {
        try
        {
            OnFinishNodePostfixUnsafe(__state);
        }
        catch
        {
        }
    }

    private static Exception? OnFinishNodeFinalizer(
        Exception? __exception,
        FinishNodeHookFrame? __state)
    {
        try
        {
            _ = OnFinishNodeFinalizerUnsafe(__exception, __state);
        }
        catch
        {
        }

        return __exception;
    }

    private static void OnUpdateFinishStatesPostfix(object? __instance)
    {
        try
        {
            OnUpdateFinishStatesPostfixUnsafe(__instance);
        }
        catch
        {
        }
    }

    private static void OnTryUpgradePrefixUnsafe(out LoadHookFrame? __state)
    {
        __state = null;
        if (!_hooksReady) return;

        try
        {
            _pendingLoadSeed = null;
            _startFrames?.Clear();
            var token = State.BeginLoadCapture(
                Environment.CurrentManagedThreadId,
                DateTime.UtcNow);
            RuntimeScheduledEventDiagnosticCapture.ResetForMissionGeneration(
                token.Generation,
                DateTime.UtcNow);
            RuntimeServeInWorkMissionDiagnosticCapture.ResetForMissionGeneration(
                token.Generation,
                DateTime.UtcNow);
            __state = new LoadHookFrame(token);
        }
        catch (Exception ex)
        {
            LogCallbackFailure("load-prefix", ex);
        }
    }

    private static void OnTryUpgradePostfixUnsafe(
        object? __result,
        LoadHookFrame? __state)
    {
        if (!_hooksReady || __state == null || __state.Completed) return;

        try
        {
            var shape = _shape
                ?? throw new InvalidOperationException("Runtime mission shape is unavailable.");
            if (__result == null || __result.GetType() != shape.PlayerSaveFileType)
            {
                throw new InvalidOperationException(
                    "TryUpgradeSaveVersion returned an unexpected PlayerSaveFile value.");
            }

            var serializationTimer = Stopwatch.StartNew();
            var rawJson = InvokeGenerateSaveString(shape, __result);
            serializationTimer.Stop();
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                throw new InvalidOperationException("GenerateSaveString returned empty JSON.");
            }

            var parseTimer = Stopwatch.StartNew();
            var seed = RuntimeMissionLoadSeedParser.Parse(rawJson);
            parseTimer.Stop();
            var metrics = new RuntimeMissionDiagnosticLoadMetrics(
                rawJson.Length,
                ComputeSha256(rawJson),
                serializationTimer.ElapsedMilliseconds,
                parseTimer.ElapsedMilliseconds,
                seed.FileVersion,
                seed.SavedGameDay,
                seed.TrackingMissionCount,
                seed.FinishedMissionCount,
                seed.DlcPartitions.Count);
            if (!State.TryMarkLoadSeedReady(
                    __state.Token,
                    metrics,
                    DateTime.UtcNow))
            {
                throw new InvalidOperationException(
                    "Mission load seed no longer matches the current load generation.");
            }

            _pendingLoadSeed = new PendingLoadSeed(
                __state.Token,
                seed,
                metrics,
                Stopwatch.GetTimestamp());
            __state.Completed = true;
            AppendSnapshotDiagnostic("load-seed-ready");
        }
        catch (Exception ex)
        {
            _pendingLoadSeed = null;
            State.FailLoadCapture(
                __state.Token,
                $"load-seed-capture-failed:{DescribeException(ex)}",
                DateTime.UtcNow);
            __state.Completed = true;
            AppendSnapshotDiagnostic("load-seed-unavailable");
            LogCallbackFailure("load-postfix", ex);
        }
    }

    private static Exception? OnTryUpgradeFinalizerUnsafe(
        Exception? __exception,
        LoadHookFrame? __state)
    {
        if (!_hooksReady || __state == null || __state.Completed) return __exception;

        try
        {
            _pendingLoadSeed = null;
            State.FailLoadCapture(
                __state.Token,
                __exception == null
                    ? "load-seed-postfix-not-completed"
                    : "load-upgrade-original-exception",
                DateTime.UtcNow);
            __state.Completed = true;
            AppendSnapshotDiagnostic("load-finalizer");
        }
        catch (Exception ex)
        {
            LogCallbackFailure("load-finalizer", ex);
        }

        return __exception;
    }

    private static void OnInitializePrefixUnsafe(
        object? __1,
        int __2,
        out InitializeHookFrame? __state)
    {
        __state = null;
        if (!_hooksReady) return;

        try
        {
            var now = DateTime.UtcNow;
            var pending = _pendingLoadSeed;
            _pendingLoadSeed = null;
            if (pending == null)
            {
                MarkInitializeWithoutSeed(now);
                return;
            }
            if (pending.Token.ThreadId != Environment.CurrentManagedThreadId)
            {
                State.FailLoadCapture(
                    pending.Token,
                    "initialize-thread-mismatch",
                    now);
                AppendSnapshotDiagnostic("initialize-unavailable");
                return;
            }
            if (HasElapsed(pending.CapturedTimestamp, PendingLoadMaximumAge))
            {
                State.FailLoadCapture(
                    pending.Token,
                    "initialize-load-seed-expired",
                    now);
                AppendSnapshotDiagnostic("initialize-unavailable");
                return;
            }

            var shape = _shape
                ?? throw new InvalidOperationException("Runtime mission shape is unavailable.");
            if (__1 == null || __1.GetType() != shape.InitializeDlcDictionaryType)
            {
                State.FailLoadCapture(
                    pending.Token,
                    "initialize-dlc-dictionary-type-mismatch",
                    now);
                AppendSnapshotDiagnostic("initialize-unavailable");
                return;
            }
            if (!RuntimeConcreteCollectionReader.TryReadDictionaryCount(
                    __1,
                    out var runtimeDlcCount,
                    out var countFailure))
            {
                State.FailLoadCapture(
                    pending.Token,
                    $"initialize-dlc-count-{FormatFailure(countFailure)}",
                    now);
                AppendSnapshotDiagnostic("initialize-unavailable");
                return;
            }

            var selectedDlcLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var partition in pending.Seed.DlcPartitions)
            {
                if (!RuntimeConcreteCollectionReader.TryContainsDictionaryKey(
                        __1,
                        partition.Label,
                        out var selected,
                        out var containsFailure))
                {
                    State.FailLoadCapture(
                        pending.Token,
                        $"initialize-dlc-contains-{FormatFailure(containsFailure)}",
                        now);
                    AppendSnapshotDiagnostic("initialize-unavailable");
                    return;
                }
                if (selected)
                {
                    selectedDlcLabels.Add(partition.Label);
                }
            }

            if (selectedDlcLabels.Count != runtimeDlcCount)
            {
                State.FailLoadCapture(
                    pending.Token,
                    "initialize-dlc-partition-count-mismatch",
                    now);
                AppendSnapshotDiagnostic("initialize-unavailable");
                return;
            }

            var selection = RuntimeMissionLoadSeedParser.SelectAndMerge(
                pending.Seed,
                __2,
                selectedDlcLabels);
            if (!State.TryBeginInitialization(
                    pending.Token,
                    Environment.CurrentManagedThreadId,
                    now,
                    out var initializationToken))
            {
                State.FailLoadCapture(
                    pending.Token,
                    "initialize-load-generation-mismatch",
                    now);
                AppendSnapshotDiagnostic("initialize-unavailable");
                return;
            }

            __state = new InitializeHookFrame(
                initializationToken,
                selection,
                pending.Metrics);
        }
        catch (Exception ex)
        {
            var snapshot = State.Snapshot();
            State.FailCurrentGeneration(
                snapshot.Generation,
                Environment.CurrentManagedThreadId,
                $"initialize-prefix-exception:{DescribeException(ex)}",
                DateTime.UtcNow);
            AppendSnapshotDiagnostic("initialize-unavailable");
            LogCallbackFailure("initialize-prefix", ex);
        }
    }

    private static void OnInitializePostfixUnsafe(InitializeHookFrame? __state)
    {
        if (!_hooksReady || __state == null || __state.Completed) return;

        try
        {
            CaptureInitializedState(__state);
        }
        catch (Exception ex)
        {
            State.FailInitialization(
                __state.Token,
                $"initialize-capture-exception:{DescribeException(ex)}",
                trackingBucketCount: 0,
                trackingBufferCount: 0,
                DateTime.UtcNow);
            AppendSnapshotDiagnostic("initialize-unavailable");
            LogCallbackFailure("initialize-postfix", ex);
        }
        finally
        {
            __state.Completed = true;
        }
    }

    private static Exception? OnInitializeFinalizerUnsafe(
        Exception? __exception,
        InitializeHookFrame? __state)
    {
        if (!_hooksReady || __state == null || __state.Completed) return __exception;

        try
        {
            State.FailInitialization(
                __state.Token,
                __exception == null
                    ? "initialize-postfix-not-completed"
                    : "initialize-original-exception",
                trackingBucketCount: 0,
                trackingBufferCount: 0,
                DateTime.UtcNow);
            __state.Completed = true;
            AppendSnapshotDiagnostic("initialize-finalizer");
        }
        catch (Exception ex)
        {
            LogCallbackFailure("initialize-finalizer", ex);
        }

        return __exception;
    }

    private static void CaptureInitializedState(InitializeHookFrame frame)
    {
        var shape = _shape
            ?? throw new InvalidOperationException("Runtime mission shape is unavailable.");
        var trackingMissions = shape.TrackingMissions.Read(null);
        var trackingBuffer = shape.TrackingMissionBuffer.Read(null);
        var finishedMissions = shape.FinishedMissions.Read(null);

        if (!RuntimeConcreteCollectionReader.TryReadDictionaryCount(
                trackingMissions,
                out var initialTrackingCount,
                out var initialTrackingFailure))
        {
            FailInitialization(
                frame,
                $"tracking-count-{FormatFailure(initialTrackingFailure)}",
                0,
                0);
            return;
        }
        if (!RuntimeConcreteCollectionReader.TryReadHashSetCount(
                trackingBuffer,
                out var initialBufferCount,
                out var initialBufferFailure))
        {
            FailInitialization(
                frame,
                $"tracking-buffer-{FormatFailure(initialBufferFailure)}",
                initialTrackingCount,
                0);
            return;
        }
        if (!TryReadStringList(
                finishedMissions,
                out var runtimeFinishedLabels,
                out var finishedFailure))
        {
            FailInitialization(
                frame,
                $"finished-list-{FormatFailure(finishedFailure)}",
                initialTrackingCount,
                initialBufferCount);
            return;
        }
        if (!RuntimeConcreteCollectionReader.TryReadDictionaryCount(
                trackingMissions,
                out var finalTrackingCount,
                out var finalTrackingFailure))
        {
            FailInitialization(
                frame,
                $"tracking-final-count-{FormatFailure(finalTrackingFailure)}",
                initialTrackingCount,
                initialBufferCount);
            return;
        }
        if (!RuntimeConcreteCollectionReader.TryReadHashSetCount(
                trackingBuffer,
                out var finalBufferCount,
                out var finalBufferFailure))
        {
            FailInitialization(
                frame,
                $"tracking-buffer-final-{FormatFailure(finalBufferFailure)}",
                finalTrackingCount,
                initialBufferCount);
            return;
        }
        if (initialTrackingCount != finalTrackingCount)
        {
            FailInitialization(
                frame,
                "tracking-count-changed",
                finalTrackingCount,
                finalBufferCount);
            return;
        }
        if (initialBufferCount != finalBufferCount)
        {
            FailInitialization(
                frame,
                "tracking-buffer-count-changed",
                finalTrackingCount,
                finalBufferCount);
            return;
        }
        if (frame.Selection.Tasks.Count > MaxDefinitionReadsPerLoad)
        {
            FailInitialization(
                frame,
                "definition-read-count-overflow",
                finalTrackingCount,
                finalBufferCount);
            return;
        }
        if (finalTrackingCount != frame.Selection.Buckets.Count)
        {
            FailInitialization(
                frame,
                "tracking-bucket-count-mismatch",
                finalTrackingCount,
                finalBufferCount);
            return;
        }
        if (finalBufferCount != 0)
        {
            FailInitialization(
                frame,
                "tracking-buffer-not-empty",
                finalTrackingCount,
                finalBufferCount);
            return;
        }
        if (!HaveSameLabelMultiset(
                frame.Selection.FinishedMissionLabels,
                runtimeFinishedLabels))
        {
            FailInitialization(
                frame,
                "finished-mission-multiset-mismatch",
                finalTrackingCount,
                finalBufferCount);
            return;
        }

        if (!TryReadInitializedTrackedMissions(
                trackingMissions,
                frame.Selection,
                out var boundMissions,
                out var bindFailure))
        {
            FailInitialization(
                frame,
                $"tracking-bind-{bindFailure}",
                finalTrackingCount,
                finalBufferCount);
            return;
        }

        var definitionTimer = Stopwatch.StartNew();
        var definitionsByLabel =
            new Dictionary<string, RuntimeMissionDefinitionDiagnosticReadResult>(
                boundMissions.Count,
                StringComparer.Ordinal);
        foreach (var boundMission in boundMissions)
        {
            var definition = RuntimeMissionDefinitionDiagnosticReader.Read(
                boundMission.Label);
            if (!definition.Success
                || definition.Definition == null
                || !string.Equals(
                    definition.Definition.Label,
                    boundMission.Label,
                    StringComparison.Ordinal)
                || !definitionsByLabel.TryAdd(boundMission.Label, definition))
            {
                definitionTimer.Stop();
                FailInitialization(
                    frame,
                    definition.Success
                        ? "definition-preflight-identity-mismatch"
                        : $"definition-preflight-{definition.Failure}",
                    finalTrackingCount,
                    finalBufferCount);
                return;
            }
        }
        definitionTimer.Stop();

        foreach (var boundMission in boundMissions)
        {
            InvokeTrackedStateRefresh(boundMission.Instance);
        }

        if (!RuntimeConcreteCollectionReader.TryReadDictionaryCount(
                trackingMissions,
                out var refreshedTrackingCount,
                out var refreshedTrackingFailure))
        {
            FailInitialization(
                frame,
                $"tracking-refresh-count-{FormatFailure(refreshedTrackingFailure)}",
                finalTrackingCount,
                finalBufferCount);
            return;
        }
        if (refreshedTrackingCount != finalTrackingCount)
        {
            FailInitialization(
                frame,
                "tracking-refresh-bucket-count-changed",
                refreshedTrackingCount,
                finalBufferCount);
            return;
        }
        if (!RuntimeConcreteCollectionReader.TryReadHashSetCount(
                trackingBuffer,
                out var refreshedBufferCount,
                out var refreshedBufferFailure))
        {
            FailInitialization(
                frame,
                $"tracking-refresh-buffer-{FormatFailure(refreshedBufferFailure)}",
                finalTrackingCount,
                finalBufferCount);
            return;
        }
        if (refreshedBufferCount != finalBufferCount)
        {
            FailInitialization(
                frame,
                "tracking-refresh-buffer-count-changed",
                finalTrackingCount,
                refreshedBufferCount);
            return;
        }
        if (!TryReadStringList(
                finishedMissions,
                out var refreshedFinishedLabels,
                out var refreshedFinishedFailure))
        {
            FailInitialization(
                frame,
                $"tracking-refresh-finished-{FormatFailure(refreshedFinishedFailure)}",
                finalTrackingCount,
                finalBufferCount);
            return;
        }
        if (!runtimeFinishedLabels.SequenceEqual(
                refreshedFinishedLabels,
                StringComparer.Ordinal))
        {
            FailInitialization(
                frame,
                "tracking-refresh-finished-list-changed",
                finalTrackingCount,
                finalBufferCount);
            return;
        }

        if (!TryReadInitializedTrackedMissions(
                trackingMissions,
                frame.Selection,
                out var refreshedMissions,
                out var refreshReadFailure))
        {
            FailInitialization(
                frame,
                $"tracking-refresh-read-{refreshReadFailure}",
                finalTrackingCount,
                finalBufferCount);
            return;
        }
        if (boundMissions.Count != refreshedMissions.Count)
        {
            FailInitialization(
                frame,
                "tracking-refresh-count-changed",
                finalTrackingCount,
                finalBufferCount);
            return;
        }

        var refreshedByLabel =
            new Dictionary<string, RuntimeMissionDiagnosticTrackedSeed>(
                refreshedMissions.Count,
                StringComparer.Ordinal);
        for (var index = 0; index < boundMissions.Count; index++)
        {
            var before = boundMissions[index];
            var after = refreshedMissions[index];
            if (before.Identity != after.Identity
                || !string.Equals(before.Label, after.Label, StringComparison.Ordinal))
            {
                FailInitialization(
                    frame,
                    "tracking-refresh-identity-changed",
                    finalTrackingCount,
                    finalBufferCount);
                return;
            }
            if (!TryReadTrackedSeed(
                    after.Instance,
                    out var refreshedState,
                    out var refreshedStateFailure))
            {
                FailInitialization(
                    frame,
                    $"tracking-refresh-{refreshedStateFailure}",
                    finalTrackingCount,
                    finalBufferCount);
                return;
            }
            if (refreshedState.Identity != after.Identity
                || !string.Equals(
                    refreshedState.Label,
                    after.Label,
                    StringComparison.Ordinal)
                || !refreshedByLabel.TryAdd(after.Label, refreshedState))
            {
                FailInitialization(
                    frame,
                    "tracking-refresh-identity-changed",
                    finalTrackingCount,
                    finalBufferCount);
                return;
            }
        }

        var loadedMissions = frame.Selection.Tasks
            .Select(task =>
            {
                var source = task.Source;
                return new RuntimeMissionDiagnosticLoadedSeed(
                    source.SourcePartition,
                    source.SourceIsCore,
                    source.SourceBucket,
                    task.MergedBucket,
                    source.SourceOrdinal,
                    source.Label,
                    source.FinishStateCount,
                    source.TrueFinishStateCount,
                    source.ConditionDataCount,
                    refreshedByLabel[source.Label],
                    definitionsByLabel[source.Label]);
            })
            .ToArray();

        var seed = new RuntimeMissionDiagnosticInitializationSeed(
            frame.Token.Generation,
            frame.Token.ThreadId,
            finalTrackingCount,
            frame.Selection.Buckets.Count,
            finalBufferCount,
            frame.Selection.CurrentDate,
            frame.Selection.SelectedDlcPartitions,
            loadedMissions,
            frame.Selection.FinishedMissionLabels,
            runtimeFinishedLabels,
            definitionTimer.ElapsedMilliseconds);
        if (!State.TryCommitInitialization(
                frame.Token,
                seed,
                DateTime.UtcNow,
                out var committedSnapshot))
        {
            AppendSnapshotDiagnostic("initialize-commit-rejected");
            return;
        }
        RuntimeScheduledEventDiagnosticCapture.ArmMissionGeneration(
            committedSnapshot.Generation,
            committedSnapshot.OwnerThreadId,
            DateTime.UtcNow);
        AppendSnapshotDiagnostic("initialize-postfix");
    }

    private static void FailInitialization(
        InitializeHookFrame frame,
        string failure,
        int trackingBucketCount,
        int trackingBufferCount)
    {
        State.FailInitialization(
            frame.Token,
            failure,
            trackingBucketCount,
            trackingBufferCount,
            DateTime.UtcNow);
        AppendSnapshotDiagnostic("initialize-unavailable");
    }

    private static void MarkInitializeWithoutSeed(DateTime changedAtUtc)
    {
        var snapshot = State.Snapshot();
        if (snapshot.Phase == RuntimeMissionDiagnosticPhase.Unavailable
            || snapshot.Phase == RuntimeMissionDiagnosticPhase.CapturingLoadSeed)
        {
            AppendSnapshotDiagnostic("initialize-without-seed");
            return;
        }

        var token = State.BeginLoadCapture(
            Environment.CurrentManagedThreadId,
            changedAtUtc);
        RuntimeServeInWorkMissionDiagnosticCapture.ResetForMissionGeneration(
            token.Generation,
            changedAtUtc);
        State.FailLoadCapture(
            token,
            "initialize-without-load-seed",
            changedAtUtc);
        AppendSnapshotDiagnostic("initialize-without-seed");
    }

    private static void OnStartMissionPrefixUnsafe(object? __0, out StartHookFrame? __state)
    {
        __state = null;
        if (!_hooksReady) return;

        try
        {
            var snapshot = State.Snapshot();
            if (!snapshot.RuntimeAvailable
                || snapshot.Phase != RuntimeMissionDiagnosticPhase.Ready)
            {
                return;
            }

            var threadId = Environment.CurrentManagedThreadId;
            if (!State.ObserveStartAttempt(snapshot.Generation, threadId, DateTime.UtcNow))
            {
                return;
            }
            if (__0 is not string label || string.IsNullOrWhiteSpace(label))
            {
                State.FailCurrentGeneration(
                    snapshot.Generation,
                    threadId,
                    "invalid-start-label",
                    DateTime.UtcNow);
                RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
                    snapshot.Generation,
                    DateTime.UtcNow);
                AppendSnapshotDiagnostic("start-unavailable");
                return;
            }

            var frame = new StartHookFrame(snapshot.Generation, threadId, label);
            (_startFrames ??= new Stack<StartHookFrame>()).Push(frame);
            __state = frame;
        }
        catch (Exception ex)
        {
            FailCurrentCapture("start-prefix-exception", "start-prefix-unavailable");
            LogCallbackFailure("start-prefix", ex);
        }
    }

    private static void OnGenerateTrackingDataPostfixUnsafe(object? __result)
    {
        if (!_hooksReady) return;

        try
        {
            var frame = PeekStartFrame();
            if (frame == null)
            {
                FailCurrentCapture(
                    "generate-without-start-frame",
                    "generate-unavailable");
                return;
            }
            if (frame.GeneratedSeed != null)
            {
                FailStartFrame(frame, "duplicate-generate-result");
                return;
            }
            if (!TryReadTrackedSeed(__result, out var seed, out var failure))
            {
                FailStartFrame(frame, failure);
                return;
            }
            if (!string.Equals(frame.RequestedLabel, seed.Label, StringComparison.Ordinal))
            {
                FailStartFrame(frame, "generated-label-mismatch");
                return;
            }

            frame.GeneratedSeed = seed;
            frame.GeneratedInstance = __result;
        }
        catch (Exception ex)
        {
            var frame = PeekStartFrame();
            if (frame != null)
            {
                FailStartFrame(frame, "generate-postfix-exception");
            }
            else
            {
                FailCurrentCapture(
                    "generate-postfix-exception",
                    "generate-unavailable");
            }
            LogCallbackFailure("generate-postfix", ex);
        }
    }

    private static void OnStartMissionPostfixUnsafe(StartHookFrame? __state)
    {
        if (!_hooksReady || __state == null || __state.Completed) return;

        var framePopped = false;
        RuntimeMissionDefinitionDiagnosticReadResult? definition = null;
        try
        {
            if (!ReferenceEquals(PeekStartFrame(), __state))
            {
                FailStartFrame(__state, "start-frame-order-mismatch");
                return;
            }
            if (!IsCurrentReadyStartFrame(__state))
            {
                return;
            }

            if (!__state.Faulted && __state.GeneratedSeed != null)
            {
                definition = RuntimeMissionDefinitionDiagnosticReader.Read(
                    __state.GeneratedSeed.Label);
                if (!definition.Success
                    || definition.Definition == null
                    || !string.Equals(
                        definition.Definition.Label,
                        __state.GeneratedSeed.Label,
                        StringComparison.Ordinal))
                {
                    FailStartFrame(
                        __state,
                        definition.Success
                            ? "start-definition-preflight-identity-mismatch"
                            : $"start-definition-preflight-{definition.Failure}");
                    return;
                }
            }

            if (!__state.Faulted
                && __state.GeneratedSeed != null
                && __state.RefreshedSeed == null)
            {
                if (__state.GeneratedInstance == null)
                {
                    FailStartFrame(__state, "start-generated-instance-missing");
                    return;
                }

                InvokeTrackedStateRefresh(__state.GeneratedInstance);
                if (__state.RefreshedSeed == null)
                {
                    if (!TryReadTrackedSeed(
                            __state.GeneratedInstance,
                            out var refreshedSeed,
                            out var refreshFailure))
                    {
                        FailStartFrame(__state, $"start-refresh-{refreshFailure}");
                        return;
                    }
                    __state.RefreshedSeed = refreshedSeed;
                }
            }
            if (!IsCurrentReadyStartFrame(__state))
            {
                return;
            }

            if ((__state.GeneratedSeed == null) != (__state.RefreshedSeed == null))
            {
                FailStartFrame(__state, "start-generated-refresh-pair-mismatch");
                return;
            }
            if (__state.GeneratedSeed != null
                && (__state.GeneratedSeed.Identity != __state.RefreshedSeed!.Identity
                    || !string.Equals(
                        __state.GeneratedSeed.Label,
                        __state.RefreshedSeed.Label,
                        StringComparison.Ordinal)))
            {
                FailStartFrame(__state, "start-generated-refresh-identity-mismatch");
                return;
            }

            if (!TryPopStartFrame(__state))
            {
                FailStartFrame(__state, "start-frame-order-mismatch");
                return;
            }
            framePopped = true;

            if (!IsCurrentReadyStartFrame(__state))
            {
                return;
            }
            if (!__state.Faulted && __state.RefreshedSeed != null)
            {
                if (definition == null)
                {
                    FailStartFrame(__state, "start-definition-preflight-missing");
                    return;
                }
                if (!State.TryCommitStartedMission(
                        __state.Generation,
                        __state.ThreadId,
                        __state.RefreshedSeed,
                        definition,
                        stateVerified: true,
                        DateTime.UtcNow))
                {
                    RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
                        __state.Generation,
                        DateTime.UtcNow);
                    AppendSnapshotDiagnostic("start-commit-rejected");
                    return;
                }
                RuntimeServeInWorkMissionDiagnosticCapture.ReconcileForMissionLifecycle(
                    __state.Generation,
                    DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            FailStartFrame(__state, "start-postfix-exception");
            LogCallbackFailure("start-postfix", ex);
        }
        finally
        {
            if (!framePopped)
            {
                if (!TryPopStartFrame(__state))
                {
                    _startFrames?.Clear();
                }
            }
            __state.Completed = true;
        }
    }

    private static Exception? OnStartMissionFinalizerUnsafe(
        Exception? __exception,
        StartHookFrame? __state)
    {
        if (!_hooksReady || __state == null || __state.Completed) return __exception;

        try
        {
            if (!TryPopStartFrame(__state))
            {
                _startFrames?.Clear();
            }

            FailStartFrame(
                __state,
                __exception == null
                    ? "start-postfix-not-completed"
                    : "start-original-exception");
            __state.Completed = true;
        }
        catch (Exception ex)
        {
            FailCurrentCapture(
                "start-finalizer-exception",
                "start-unavailable");
            LogCallbackFailure("start-finalizer", ex);
        }

        return __exception;
    }

    private static Exception? OnObservedMethodFinalizerUnsafe(
        Exception? __exception,
        MethodBase __originalMethod)
    {
        if (!_hooksReady || __exception == null) return __exception;

        try
        {
            var snapshot = State.Snapshot();
            FailCurrentCapture(
                $"native-{__originalMethod.Name}-exception",
                "native-lifecycle-unavailable");
            RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
                snapshot.Generation,
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            LogCallbackFailure("native-lifecycle-finalizer", ex);
        }

        return __exception;
    }

    private static void OnRemoveMissionPostfixUnsafe(object? __0)
    {
        ObserveTrackedSeed(
            __0,
            "remove",
            static (snapshot, threadId, seed) =>
                State.TryObserveRemoval(
                    snapshot.Generation,
                    threadId,
                    seed,
                    DateTime.UtcNow));
    }

    private static void OnFinishMissionPostfixUnsafe(object? __0)
    {
        ObserveTrackedSeed(
            __0,
            "finish",
            static (snapshot, threadId, seed) =>
                State.TryObserveFinish(
                    snapshot.Generation,
                    threadId,
                    seed,
                    DateTime.UtcNow));
    }

    private static void OnSetFinishedMissionPostfixUnsafe(object? __0)
    {
        if (!_hooksReady) return;

        try
        {
            var snapshot = State.Snapshot();
            if (!snapshot.RuntimeAvailable) return;
            if (State.TryObserveFinishedLabel(
                    snapshot.Generation,
                    Environment.CurrentManagedThreadId,
                    __0 as string,
                    DateTime.UtcNow))
            {
                RuntimeServeInWorkMissionDiagnosticCapture.ReconcileForMissionLifecycle(
                    snapshot.Generation,
                    DateTime.UtcNow);
            }
            else
            {
                RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
                    snapshot.Generation,
                    DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            FailCurrentCapture(
                "set-finished-postfix-exception",
                "set-finished-unavailable");
            LogCallbackFailure("set-finished-postfix", ex);
        }
    }

    private static void OnFinishNodePrefixUnsafe(out FinishNodeHookFrame? __state)
    {
        __state = null;
        if (!_hooksReady) return;

        try
        {
            var snapshot = State.Snapshot();
            if (!snapshot.RuntimeAvailable
                || snapshot.Phase != RuntimeMissionDiagnosticPhase.Ready)
            {
                return;
            }

            var shape = _shape
                ?? throw new InvalidOperationException("Runtime mission shape is unavailable.");
            var source = shape.FinishedMissions.Read(null);
            if (!TryReadStringList(source, out var labels, out var failure))
            {
                State.FailCurrentGeneration(
                    snapshot.Generation,
                    Environment.CurrentManagedThreadId,
                    $"finish-node-before-{FormatFailure(failure)}",
                    DateTime.UtcNow);
                RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
                    snapshot.Generation,
                    DateTime.UtcNow);
                AppendSnapshotDiagnostic("finish-node-unavailable");
                return;
            }

            __state = new FinishNodeHookFrame(
                snapshot.Generation,
                Environment.CurrentManagedThreadId,
                labels);
        }
        catch (Exception ex)
        {
            FailCurrentCapture(
                "finish-node-prefix-exception",
                "finish-node-unavailable");
            LogCallbackFailure("finish-node-prefix", ex);
        }
    }

    private static void OnFinishNodePostfixUnsafe(FinishNodeHookFrame? __state)
    {
        if (!_hooksReady || __state == null || __state.Completed) return;

        try
        {
            var snapshot = State.Snapshot();
            if (!snapshot.RuntimeAvailable
                || snapshot.Phase != RuntimeMissionDiagnosticPhase.Ready
                || snapshot.Generation != __state.Generation)
            {
                return;
            }

            var shape = _shape
                ?? throw new InvalidOperationException("Runtime mission shape is unavailable.");
            var source = shape.FinishedMissions.Read(null);
            if (!TryReadStringList(source, out var currentLabels, out var failure))
            {
                FailFinishNodeFrame(
                    __state,
                    $"finish-node-after-{FormatFailure(failure)}");
                return;
            }
            if (!TryGetAppendedFinishedLabels(
                    __state.FinishedLabelsBefore,
                    currentLabels,
                    out var appendedLabels))
            {
                FailFinishNodeFrame(__state, "finish-node-list-not-append-only");
                return;
            }

            if (State.TryObserveFinishNode(
                    __state.Generation,
                    __state.ThreadId,
                    appendedLabels,
                    DateTime.UtcNow))
            {
                RuntimeServeInWorkMissionDiagnosticCapture.ReconcileForMissionLifecycle(
                    __state.Generation,
                    DateTime.UtcNow);
            }
            else
            {
                RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
                    __state.Generation,
                    DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            FailFinishNodeFrame(__state, "finish-node-postfix-exception");
            LogCallbackFailure("finish-node-postfix", ex);
        }
        finally
        {
            __state.Completed = true;
        }
    }

    private static Exception? OnFinishNodeFinalizerUnsafe(
        Exception? __exception,
        FinishNodeHookFrame? __state)
    {
        if (!_hooksReady || __state == null || __state.Completed) return __exception;

        try
        {
            FailFinishNodeFrame(
                __state,
                __exception == null
                    ? "finish-node-postfix-not-completed"
                    : "finish-node-original-exception");
        }
        catch (Exception ex)
        {
            LogCallbackFailure("finish-node-finalizer", ex);
        }
        finally
        {
            __state.Completed = true;
        }

        return __exception;
    }

    private static void OnUpdateFinishStatesPostfixUnsafe(object? __instance)
    {
        if (!_hooksReady) return;

        try
        {
            var snapshot = State.Snapshot();
            if (!snapshot.RuntimeAvailable) return;
            if (!TryReadTrackedSeed(__instance, out var seed, out var failure))
            {
                State.FailCurrentGeneration(
                    snapshot.Generation,
                    Environment.CurrentManagedThreadId,
                    failure,
                    DateTime.UtcNow);
                RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
                    snapshot.Generation,
                    DateTime.UtcNow);
                AppendSnapshotDiagnostic("state-refresh-unavailable");
                return;
            }

            if (!TryResolveStartFrameForRefresh(
                    seed,
                    out var currentStart,
                    out var startFrameFailure))
            {
                State.FailCurrentGeneration(
                    snapshot.Generation,
                    Environment.CurrentManagedThreadId,
                    startFrameFailure,
                    DateTime.UtcNow);
                RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
                    snapshot.Generation,
                    DateTime.UtcNow);
                AppendSnapshotDiagnostic("state-refresh-unavailable");
                return;
            }
            if (currentStart != null)
            {
                currentStart.RefreshedSeed = seed;
                return;
            }

            if (!State.TryObserveStateRefresh(
                snapshot.Generation,
                Environment.CurrentManagedThreadId,
                seed,
                DateTime.UtcNow))
            {
                RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
                    snapshot.Generation,
                    DateTime.UtcNow);
                return;
            }
            RuntimeServeInWorkMissionDiagnosticCapture.ReconcileForMissionLifecycle(
                snapshot.Generation,
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            FailCurrentCapture(
                "state-refresh-postfix-exception",
                "state-refresh-unavailable");
            LogCallbackFailure("state-refresh-postfix", ex);
        }
    }

    private static void ObserveTrackedSeed(
        object? value,
        string source,
        Func<RuntimeMissionDiagnosticSnapshot, int, RuntimeMissionDiagnosticTrackedSeed, bool> observer)
    {
        if (!_hooksReady) return;

        try
        {
            var snapshot = State.Snapshot();
            if (!snapshot.RuntimeAvailable) return;
            var threadId = Environment.CurrentManagedThreadId;
            if (!TryReadTrackedSeed(value, out var seed, out var failure))
            {
                State.FailCurrentGeneration(
                    snapshot.Generation,
                    threadId,
                    $"{source}-{failure}",
                    DateTime.UtcNow);
                RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
                    snapshot.Generation,
                    DateTime.UtcNow);
                AppendSnapshotDiagnostic($"{source}-unavailable");
                return;
            }

            if (observer(snapshot, threadId, seed))
            {
                RuntimeServeInWorkMissionDiagnosticCapture.ReconcileForMissionLifecycle(
                    snapshot.Generation,
                    DateTime.UtcNow);
            }
            else
            {
                RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
                    snapshot.Generation,
                    DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            FailCurrentCapture(
                $"{source}-postfix-exception",
                $"{source}-unavailable");
            LogCallbackFailure($"{source}-postfix", ex);
        }
    }

    private static bool TryReadInitializedTrackedMissions(
        object trackingMissions,
        RuntimeMissionLoadSelection selection,
        out IReadOnlyList<BoundTrackedMission> missions,
        out string failure)
    {
        var result = new List<BoundTrackedMission>(selection.Tasks.Count);
        var identities = new HashSet<nint>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bucket in selection.Buckets)
        {
            if (!RuntimeConcreteCollectionReader.TryGetDictionaryValue(
                    trackingMissions,
                    bucket.Bucket,
                    out var rawList,
                    out var found,
                    out var dictionaryFailure))
            {
                missions = Array.Empty<BoundTrackedMission>();
                failure = $"bucket-{bucket.Bucket}-{FormatFailure(dictionaryFailure)}";
                return false;
            }
            if (!found || rawList == null)
            {
                missions = Array.Empty<BoundTrackedMission>();
                failure = $"bucket-{bucket.Bucket}-missing";
                return false;
            }
            if (!RuntimeConcreteCollectionReader.TryReadList(
                    rawList,
                    out var rawMissions,
                    out var listFailure))
            {
                missions = Array.Empty<BoundTrackedMission>();
                failure = $"bucket-{bucket.Bucket}-list-{FormatFailure(listFailure)}";
                return false;
            }
            if (rawMissions.Count != bucket.Tasks.Count)
            {
                missions = Array.Empty<BoundTrackedMission>();
                failure = $"bucket-{bucket.Bucket}-task-count-mismatch";
                return false;
            }

            for (var index = 0; index < bucket.Tasks.Count; index++)
            {
                var expected = bucket.Tasks[index].Source;
                var instance = rawMissions[index];
                if (!TryReadTrackedSeed(
                        instance,
                        out var state,
                        out var stateFailure))
                {
                    missions = Array.Empty<BoundTrackedMission>();
                    failure = $"bucket-{bucket.Bucket}-task-{index}-{stateFailure}";
                    return false;
                }
                if (!string.Equals(
                        state.Label,
                        expected.Label,
                        StringComparison.Ordinal))
                {
                    missions = Array.Empty<BoundTrackedMission>();
                    failure = $"bucket-{bucket.Bucket}-task-{index}-label-mismatch";
                    return false;
                }
                if (!identities.Add(state.Identity) || !labels.Add(state.Label))
                {
                    missions = Array.Empty<BoundTrackedMission>();
                    failure = "duplicate-task-identity";
                    return false;
                }

                result.Add(new BoundTrackedMission(
                    state.Identity,
                    state.Label,
                    instance!));
            }
        }

        if (result.Count != selection.Tasks.Count)
        {
            missions = Array.Empty<BoundTrackedMission>();
            failure = "task-count-mismatch";
            return false;
        }

        missions = result;
        failure = "";
        return true;
    }

    private static void InvokeTrackedStateRefresh(object instance)
    {
        var shape = _shape
            ?? throw new InvalidOperationException("Runtime mission shape is unavailable.");
        if (instance.GetType() != shape.TrackedMissionType)
        {
            throw new InvalidOperationException(
                "Tracked mission refresh received an unexpected runtime type.");
        }

        try
        {
            shape.UpdateFinishStates.Invoke(instance, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw new InvalidOperationException(
                "Tracked mission state refresh threw.",
                ex.InnerException);
        }
    }

    private static bool TryReadTrackedSeed(
        object? value,
        out RuntimeMissionDiagnosticTrackedSeed seed,
        out string failure)
    {
        seed = new RuntimeMissionDiagnosticTrackedSeed(0, "", Array.Empty<bool>());
        failure = "invalid-tracked-mission";
        var shape = _shape;
        if (shape == null
            || value == null
            || value.GetType() != shape.TrackedMissionType
            || !TryReadTrackedPointer(value, out var pointer))
        {
            return false;
        }

        var rawLabel = shape.MissionLabel.Read(value);
        if (rawLabel is not string label || string.IsNullOrWhiteSpace(label))
        {
            failure = "invalid-tracked-mission-label";
            return false;
        }

        var finishStateSource = shape.ConditionFinishStates.Read(value);
        if (!RuntimeConcreteCollectionReader.TryReadList(
                finishStateSource,
                out var rawFinishStates,
                out var listFailure))
        {
            failure = $"condition-state-{FormatFailure(listFailure)}";
            return false;
        }
        if (rawFinishStates.Count > RuntimeMissionLoadSeedParser.MaxConditionEntriesPerTask)
        {
            failure = "condition-state-count-overflow";
            return false;
        }

        var finishStates = new bool[rawFinishStates.Count];
        for (var index = 0; index < rawFinishStates.Count; index++)
        {
            if (rawFinishStates[index] is not bool state)
            {
                failure = "condition-state-element-type-mismatch";
                return false;
            }
            finishStates[index] = state;
        }

        seed = new RuntimeMissionDiagnosticTrackedSeed(pointer, label, finishStates);
        failure = "";
        return true;
    }

    private static bool TryReadTrackedPointer(object? value, out nint pointer)
    {
        pointer = 0;
        var shape = _shape;
        if (shape == null
            || value == null
            || value.GetType() != shape.TrackedMissionType
            || value is not Il2CppObjectBase il2CppObject
            || il2CppObject.Pointer == IntPtr.Zero)
        {
            return false;
        }

        pointer = il2CppObject.Pointer;
        return true;
    }

    private static bool TryReadStringList(
        object? source,
        out IReadOnlyList<string> values,
        out RuntimeCollectionReadFailure failure)
    {
        values = Array.Empty<string>();
        if (!RuntimeConcreteCollectionReader.TryReadList(source, out var rawValues, out failure))
        {
            return false;
        }
        if (rawValues.Count > RuntimeMissionLoadSeedParser.MaxFinishedMissions)
        {
            failure = RuntimeCollectionReadFailure.CountMismatch;
            return false;
        }

        var result = new string[rawValues.Count];
        for (var index = 0; index < rawValues.Count; index++)
        {
            if (rawValues[index] is not string label || string.IsNullOrWhiteSpace(label))
            {
                failure = RuntimeCollectionReadFailure.ElementTypeMismatch;
                return false;
            }
            result[index] = label;
        }

        values = result;
        failure = RuntimeCollectionReadFailure.None;
        return true;
    }

    private static bool HaveSameLabelMultiset(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        if (left.Count != right.Count) return false;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var label in left)
        {
            if (string.IsNullOrWhiteSpace(label)) return false;
            counts[label] = counts.TryGetValue(label, out var count)
                ? checked(count + 1)
                : 1;
        }
        foreach (var label in right)
        {
            if (string.IsNullOrWhiteSpace(label)
                || !counts.TryGetValue(label, out var count))
            {
                return false;
            }
            if (count == 1)
            {
                counts.Remove(label);
            }
            else
            {
                counts[label] = count - 1;
            }
        }

        return counts.Count == 0;
    }

    private static bool TryGetAppendedFinishedLabels(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after,
        out IReadOnlyList<string> appended)
    {
        appended = Array.Empty<string>();
        if (after.Count < before.Count) return false;

        for (var index = 0; index < before.Count; index++)
        {
            if (!string.Equals(before[index], after[index], StringComparison.Ordinal))
            {
                return false;
            }
        }
        if (after.Count == before.Count) return true;

        var suffix = new string[after.Count - before.Count];
        for (var index = before.Count; index < after.Count; index++)
        {
            suffix[index - before.Count] = after[index];
        }
        appended = suffix;
        return true;
    }

    private static string InvokeGenerateSaveString(
        RuntimeShape shape,
        object playerSaveFile)
    {
        try
        {
            return shape.GenerateSaveString.Invoke(
                    null,
                    new[] { playerSaveFile, shape.FormattingNone })
                as string
                ?? throw new InvalidOperationException(
                    "GenerateSaveString returned a non-String value.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw new InvalidOperationException(
                "GenerateSaveString threw.",
                ex.InnerException);
        }
    }

    private static string ComputeSha256(string value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var encoder = Encoding.UTF8.GetEncoder();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var offset = 0;
            while (offset < value.Length)
            {
                encoder.Convert(
                    value.AsSpan(offset),
                    buffer.AsSpan(),
                    flush: true,
                    out var charsUsed,
                    out var bytesUsed,
                    out _);
                if (charsUsed == 0)
                {
                    throw new InvalidOperationException(
                        "UTF-8 hashing made no progress.");
                }
                hash.AppendData(buffer.AsSpan(0, bytesUsed));
                offset += charsUsed;
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static bool HasElapsed(long startedTimestamp, TimeSpan maximumAge)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
        if (elapsedTicks < 0)
        {
            return true;
        }

        return elapsedTicks > maximumAge.TotalSeconds * Stopwatch.Frequency;
    }

    private static StartHookFrame? PeekStartFrame()
    {
        return _startFrames is { Count: > 0 } ? _startFrames.Peek() : null;
    }

    private static bool IsCurrentReadyStartFrame(StartHookFrame frame)
    {
        var snapshot = State.Snapshot();
        var threadId = Environment.CurrentManagedThreadId;
        return snapshot.RuntimeAvailable
            && snapshot.Phase == RuntimeMissionDiagnosticPhase.Ready
            && snapshot.Generation == frame.Generation
            && snapshot.OwnerThreadId == frame.ThreadId
            && threadId == frame.ThreadId;
    }

    private static bool TryResolveStartFrameForRefresh(
        RuntimeMissionDiagnosticTrackedSeed seed,
        out StartHookFrame? match,
        out string failure)
    {
        match = null;
        failure = "";
        var frames = _startFrames;
        if (frames == null) return true;

        foreach (var frame in frames)
        {
            var generated = frame.GeneratedSeed;
            if (frame.Faulted
                || frame.Completed
                || generated == null
                || generated.Identity != seed.Identity
                || !string.Equals(
                    generated.Label,
                    seed.Label,
                    StringComparison.Ordinal))
            {
                continue;
            }
            if (match != null)
            {
                match = null;
                failure = "ambiguous-start-refresh-frame";
                return false;
            }
            match = frame;
        }

        return true;
    }

    private static bool TryPopStartFrame(StartHookFrame frame)
    {
        var frames = _startFrames;
        if (frames == null
            || frames.Count == 0
            || !ReferenceEquals(frames.Peek(), frame))
        {
            return false;
        }

        frames.Pop();
        return true;
    }

    private static void FailStartFrame(StartHookFrame frame, string failure)
    {
        frame.Faulted = true;
        State.FailCurrentGeneration(
            frame.Generation,
            frame.ThreadId,
            failure,
            DateTime.UtcNow);
        RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
            frame.Generation,
            DateTime.UtcNow);
        AppendSnapshotDiagnostic("start-unavailable");
    }

    private static void FailFinishNodeFrame(
        FinishNodeHookFrame frame,
        string failure)
    {
        State.FailCurrentGeneration(
            frame.Generation,
            frame.ThreadId,
            failure,
            DateTime.UtcNow);
        RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
            frame.Generation,
            DateTime.UtcNow);
        AppendSnapshotDiagnostic("finish-node-unavailable");
    }

    private static void FailCurrentCapture(string failure, string diagnosticSource)
    {
        try
        {
            var snapshot = State.Snapshot();
            State.FailCurrentGeneration(
                snapshot.Generation,
                Environment.CurrentManagedThreadId,
                failure,
                DateTime.UtcNow);
            RuntimeServeInWorkMissionDiagnosticCapture.ClearForMissionLifecycle(
                snapshot.Generation,
                DateTime.UtcNow);
            AppendSnapshotDiagnostic(diagnosticSource);
        }
        catch (Exception ex)
        {
            LogCallbackFailure($"{diagnosticSource}-fail-closed", ex);
        }
    }

    private static void AppendSnapshotDiagnostic(string source)
    {
        try
        {
            var snapshot = State.Snapshot();
            AggregateModLogService.AppendSection(
                "runtime-mission",
                "Mission diagnostic capture",
                $"source={source}; phase={snapshot.Phase}; available={snapshot.RuntimeAvailable}; "
                    + $"generation={snapshot.Generation}; thread={snapshot.OwnerThreadId}; "
                    + $"jsonLength={snapshot.LoadJsonLength}; jsonSha256={snapshot.LoadJsonSha256}; "
                    + $"serializeMs={snapshot.SerializeElapsedMilliseconds}; parseMs={snapshot.ParseElapsedMilliseconds}; "
                    + $"definitionMs={snapshot.DefinitionReadElapsedMilliseconds}; "
                    + $"parsed={snapshot.ParsedTrackingMissionCount}/{snapshot.ParsedFinishedMissionCount}; "
                    + $"selectedDlc={snapshot.SelectedDlcCount}/{snapshot.ParsedDlcPartitionCount}; "
                    + $"trackingBuckets={snapshot.TrackingBucketCount}; buffer={snapshot.TrackingBufferCount}; "
                    + $"active={snapshot.ActiveMissionCount}; unverified={snapshot.UnverifiedMissionCount}; "
                    + $"tracking={snapshot.TrackingMissionCount}; fulfilled={snapshot.FulfilledMissionCount}; "
                    + $"finishedUnique={snapshot.FinishedUniqueMissionCount}; definitions="
                    + $"{snapshot.DefinitionAvailableCount}/{snapshot.ActiveMissionCount}; "
                    + $"titles={snapshot.TitleAvailableCount}; serve={snapshot.ServeInWorkMissionCount}; "
                    + $"starts={snapshot.StartCommitCount}/{snapshot.StartAttemptCount}; "
                    + $"removes={snapshot.RemoveCount}; finishes={snapshot.FinishCount}; "
                    + $"refreshes={snapshot.StateRefreshCount}; last={snapshot.LastEvent}; "
                    + $"error={snapshot.LastError}");
        }
        catch
        {
        }
    }

    private static void LogCallbackFailure(string source, Exception exception)
    {
        try
        {
            _log?.LogWarning(
                $"Runtime mission diagnostic callback {source} failed without affecting the game method: "
                + exception.GetBaseException().Message);
        }
        catch
        {
        }
    }

    private static string DescribeException(Exception exception)
    {
        return $"{exception.GetBaseException().GetType().Name}:"
            + exception.GetBaseException().Message;
    }

    private static string FormatFailure(RuntimeCollectionReadFailure failure)
    {
        return failure.ToString().ToLowerInvariant();
    }

    private sealed class LoadHookFrame
    {
        public LoadHookFrame(RuntimeMissionDiagnosticLoadToken token)
        {
            Token = token;
        }

        public RuntimeMissionDiagnosticLoadToken Token { get; }
        public bool Completed { get; set; }
    }

    private sealed record PendingLoadSeed(
        RuntimeMissionDiagnosticLoadToken Token,
        RuntimeMissionLoadSeed Seed,
        RuntimeMissionDiagnosticLoadMetrics Metrics,
        long CapturedTimestamp);

    private sealed record BoundTrackedMission(
        nint Identity,
        string Label,
        object Instance);

    private sealed class InitializeHookFrame
    {
        public InitializeHookFrame(
            RuntimeMissionDiagnosticInitializationToken token,
            RuntimeMissionLoadSelection selection,
            RuntimeMissionDiagnosticLoadMetrics metrics)
        {
            Token = token;
            Selection = selection;
            Metrics = metrics;
        }

        public RuntimeMissionDiagnosticInitializationToken Token { get; }
        public RuntimeMissionLoadSelection Selection { get; }
        public RuntimeMissionDiagnosticLoadMetrics Metrics { get; }
        public bool Completed { get; set; }
    }

    private sealed class StartHookFrame
    {
        public StartHookFrame(long generation, int threadId, string requestedLabel)
        {
            Generation = generation;
            ThreadId = threadId;
            RequestedLabel = requestedLabel;
        }

        public long Generation { get; }
        public int ThreadId { get; }
        public string RequestedLabel { get; }
        public object? GeneratedInstance { get; set; }
        public RuntimeMissionDiagnosticTrackedSeed? GeneratedSeed { get; set; }
        public RuntimeMissionDiagnosticTrackedSeed? RefreshedSeed { get; set; }
        public bool Faulted { get; set; }
        public bool Completed { get; set; }
    }

    private sealed class FinishNodeHookFrame
    {
        public FinishNodeHookFrame(
            long generation,
            int threadId,
            IReadOnlyList<string> finishedLabelsBefore)
        {
            Generation = generation;
            ThreadId = threadId;
            FinishedLabelsBefore = finishedLabelsBefore.ToArray();
        }

        public long Generation { get; }
        public int ThreadId { get; }
        public IReadOnlyList<string> FinishedLabelsBefore { get; }
        public bool Completed { get; set; }
    }

    private sealed record RuntimeShape(
        Type PlayerSaveFileType,
        Type InitializeDlcDictionaryType,
        Type TrackedMissionType,
        object FormattingNone,
        ExactPropertyReader TrackingMissions,
        ExactPropertyReader TrackingMissionBuffer,
        ExactPropertyReader FinishedMissions,
        ExactPropertyReader MissionLabel,
        ExactPropertyReader ConditionFinishStates,
        MethodInfo TryUpgradeSaveVersion,
        MethodInfo GenerateSaveString,
        MethodInfo Initialize,
        MethodInfo StartMission,
        MethodInfo GenerateTrackingData,
        MethodInfo RemoveMissionFromList,
        MethodInfo FinishMission,
        MethodInfo SetFinishedMissions,
        MethodInfo FinishNodeExtern,
        MethodInfo UpdateFinishStates)
    {
        private const string Il2CppDictionaryTypeName =
            "Il2CppSystem.Collections.Generic.Dictionary`2";
        private const string Il2CppListTypeName =
            "Il2CppSystem.Collections.Generic.List`1";
        private const string Il2CppHashSetTypeName =
            "Il2CppSystem.Collections.Generic.HashSet`1";

        public static RuntimeShape Resolve()
        {
            var saveManagementType = RuntimeReflectionUtility.FindType(SaveManagementTypeName)
                ?? throw new InvalidOperationException($"{SaveManagementTypeName} is not loaded.");
            var playerSaveFileType = RuntimeReflectionUtility.FindType(PlayerSaveFileTypeName)
                ?? throw new InvalidOperationException($"{PlayerSaveFileTypeName} is not loaded.");
            var schedulerType = RuntimeReflectionUtility.FindType(SchedulerTypeName)
                ?? throw new InvalidOperationException($"{SchedulerTypeName} is not loaded.");
            var trackedMissionType = schedulerType.GetNestedType(
                    "TrackedMissionData",
                    BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"{TrackedMissionTypeName} is not loaded.");
            if (!string.Equals(
                    trackedMissionType.FullName,
                    TrackedMissionTypeName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "TrackedMissionData resolved to an unexpected type.");
            }

            var trackingMissions = ExactPropertyReader.Resolve(
                schedulerType,
                "trackingMissions",
                isStatic: true);
            var trackingMissionBuffer = ExactPropertyReader.Resolve(
                schedulerType,
                "trackingMissionBuffer",
                isStatic: true);
            var finishedMissions = ExactPropertyReader.Resolve(
                schedulerType,
                "finishedMissions",
                isStatic: true);
            var missionLabel = ExactPropertyReader.Resolve(
                trackedMissionType,
                "missionLabel",
                isStatic: false);
            var conditionFinishStates = ExactPropertyReader.Resolve(
                trackedMissionType,
                "conditionFinishStates",
                isStatic: false);

            var trackedListType = RequireClosedGeneric(
                trackingMissions.ValueType,
                Il2CppDictionaryTypeName,
                expectedArgumentCount: 2)[1];
            RequireClosedGeneric(
                trackedListType,
                Il2CppListTypeName,
                trackedMissionType);
            RequireClosedGeneric(
                trackingMissions.ValueType,
                Il2CppDictionaryTypeName,
                typeof(int),
                trackedListType);
            RequireClosedGeneric(
                trackingMissionBuffer.ValueType,
                Il2CppHashSetTypeName,
                trackedMissionType);
            RequireClosedGeneric(
                finishedMissions.ValueType,
                Il2CppListTypeName,
                typeof(string));
            if (missionLabel.ValueType != typeof(string))
            {
                throw new InvalidOperationException(
                    "TrackedMissionData.missionLabel is not String.");
            }
            RequireClosedGeneric(
                conditionFinishStates.ValueType,
                Il2CppListTypeName,
                typeof(bool));

            var tryUpgrade = RequireStaticMethod(
                saveManagementType,
                "TryUpgradeSaveVersion",
                playerSaveFileType,
                playerSaveFileType);
            var generateSaveString = RequireGenerateSaveStringMethod(
                saveManagementType,
                playerSaveFileType,
                out var formattingNone);
            var initialize = RequireInitializeMethod(schedulerType);
            var initializeDlcDictionaryType = initialize.GetParameters()[1].ParameterType;
            var startMission = RequireStaticMethod(
                schedulerType,
                "StartMission",
                typeof(void),
                typeof(string));
            var generateTrackingData = RequireStaticMethod(
                schedulerType,
                "GenerateTrackingData",
                trackedMissionType,
                typeof(string));
            var removeMissionFromList = RequireStaticMethod(
                schedulerType,
                "RemoveMissionFromList",
                typeof(void),
                trackedMissionType);
            var finishMission = RequireStaticMethod(
                schedulerType,
                "FinishMission",
                typeof(void),
                trackedMissionType);
            var setFinishedMissions = RequireStaticMethod(
                schedulerType,
                "SetFinishedMissions",
                typeof(void),
                typeof(string));
            var finishNodeExtern = RequireStaticMethod(
                schedulerType,
                "FinishNodeExtern",
                typeof(void),
                typeof(string));
            var updateFinishStates = RequireInstanceMethod(
                trackedMissionType,
                "UpdateFinishStates",
                typeof(void));

            return new RuntimeShape(
                playerSaveFileType,
                initializeDlcDictionaryType,
                trackedMissionType,
                formattingNone,
                trackingMissions,
                trackingMissionBuffer,
                finishedMissions,
                missionLabel,
                conditionFinishStates,
                tryUpgrade,
                generateSaveString,
                initialize,
                startMission,
                generateTrackingData,
                removeMissionFromList,
                finishMission,
                setFinishedMissions,
                finishNodeExtern,
                updateFinishStates);
        }

        private static MethodInfo RequireGenerateSaveStringMethod(
            Type saveManagementType,
            Type playerSaveFileType,
            out object formattingNone)
        {
            var candidates = saveManagementType
                .GetMethods(
                    BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "GenerateSaveString"
                    && method.ReturnType == typeof(string)
                    && !method.IsGenericMethod)
                .Where(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 2
                        && parameters[0].ParameterType == playerSaveFileType
                        && string.Equals(
                            parameters[1].ParameterType.FullName,
                            FormattingTypeName,
                            StringComparison.Ordinal);
                })
                .ToArray();
            if (candidates.Length != 1)
            {
                throw new InvalidOperationException(
                    $"SaveManagement.GenerateSaveString exact overload count was {candidates.Length}.");
            }

            var formattingType = candidates[0].GetParameters()[1].ParameterType;
            if (!formattingType.IsEnum
                || Enum.GetUnderlyingType(formattingType) != typeof(int))
            {
                throw new InvalidOperationException(
                    "Newtonsoft.Json.Formatting is not an Int32 enum.");
            }
            var noneField = formattingType.GetField(
                "None",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (noneField == null
                || !noneField.IsLiteral
                || Convert.ToInt32(noneField.GetRawConstantValue()) != 0)
            {
                throw new InvalidOperationException(
                    "Newtonsoft.Json.Formatting.None is not zero.");
            }

            formattingNone = Enum.ToObject(formattingType, 0);
            return candidates[0];
        }

        private static MethodInfo RequireInitializeMethod(Type schedulerType)
        {
            var candidates = schedulerType
                .GetMethods(
                    BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "Initialize"
                    && method.ReturnType == typeof(void)
                    && !method.IsGenericMethod)
                .Where(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 3
                        && parameters[2].ParameterType == typeof(int)
                        && string.Equals(
                            parameters[0].ParameterType.FullName,
                            "GameData.RunTime.Common.PlayerSaveFile+RunTimeSchedulerSaveDataPartial",
                            StringComparison.Ordinal)
                        && IsSchedulerDlcDictionary(parameters[1].ParameterType);
                })
                .ToArray();
            return candidates.Length == 1
                ? candidates[0]
                : throw new InvalidOperationException(
                    $"RunTimeScheduler.Initialize exact overload count was {candidates.Length}.");
        }

        private static bool IsSchedulerDlcDictionary(Type type)
        {
            if (!TryGetClosedGeneric(type, out var definitionName, out var arguments)
                || arguments.Length != 2
                || definitionName != Il2CppDictionaryTypeName
                || arguments[0] != typeof(string))
            {
                return false;
            }

            return string.Equals(
                arguments[1].FullName,
                "GameData.RunTime.Common.PlayerSaveFile+DLCSchedulerSaveData",
                StringComparison.Ordinal);
        }

        private static MethodInfo RequireStaticMethod(
            Type type,
            string name,
            Type returnType,
            params Type[] parameterTypes)
        {
            return RequireMethod(type, name, isStatic: true, returnType, parameterTypes);
        }

        private static MethodInfo RequireInstanceMethod(
            Type type,
            string name,
            Type returnType,
            params Type[] parameterTypes)
        {
            return RequireMethod(type, name, isStatic: false, returnType, parameterTypes);
        }

        private static MethodInfo RequireMethod(
            Type type,
            string name,
            bool isStatic,
            Type returnType,
            IReadOnlyList<Type> parameterTypes)
        {
            var flags = BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly
                | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            var candidates = type.GetMethods(flags)
                .Where(method => method.Name == name
                    && method.ReturnType == returnType
                    && !method.IsGenericMethod)
                .Where(method =>
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length != parameterTypes.Count) return false;
                    for (var index = 0; index < parameters.Length; index++)
                    {
                        if (parameters[index].ParameterType != parameterTypes[index]) return false;
                    }
                    return true;
                })
                .ToArray();
            return candidates.Length == 1
                ? candidates[0]
                : throw new InvalidOperationException(
                    $"{type.FullName}.{name} exact overload count was {candidates.Length}.");
        }

        private static Type[] RequireClosedGeneric(
            Type type,
            string expectedDefinition,
            int expectedArgumentCount)
        {
            if (!TryGetClosedGeneric(type, out var definitionName, out var arguments)
                || definitionName != expectedDefinition
                || arguments.Length != expectedArgumentCount)
            {
                throw new InvalidOperationException(
                    $"{type.FullName} does not have the required closed generic shape.");
            }
            return arguments;
        }

        private static void RequireClosedGeneric(
            Type type,
            string expectedDefinition,
            params Type[] expectedArguments)
        {
            var arguments = RequireClosedGeneric(
                type,
                expectedDefinition,
                expectedArguments.Length);
            for (var index = 0; index < expectedArguments.Length; index++)
            {
                if (arguments[index] != expectedArguments[index])
                {
                    throw new InvalidOperationException(
                        $"{type.FullName} generic argument {index} was unexpected.");
                }
            }
        }

        private static bool TryGetClosedGeneric(
            Type type,
            out string definitionName,
            out Type[] arguments)
        {
            definitionName = "";
            arguments = Array.Empty<Type>();
            if (!type.IsGenericType || type.ContainsGenericParameters) return false;
            definitionName = type.GetGenericTypeDefinition().FullName ?? "";
            arguments = type.GetGenericArguments();
            return definitionName.Length > 0;
        }
    }

    private sealed record ExactPropertyReader(
        Type ValueType,
        PropertyInfo Property)
    {
        public static ExactPropertyReader Resolve(Type ownerType, string name, bool isStatic)
        {
            var flags = BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly
                | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            var candidates = ownerType
                .GetProperties(flags)
                .Where(property =>
                {
                    var getter = property.GetGetMethod(nonPublic: true);
                    return string.Equals(property.Name, name, StringComparison.Ordinal)
                        && property.GetIndexParameters().Length == 0
                        && getter != null
                        && getter.IsStatic == isStatic;
                })
                .Take(2)
                .ToArray();
            if (candidates.Length != 1)
            {
                throw new InvalidOperationException(
                    $"{ownerType.FullName}.{name} exact property count was {candidates.Length}.");
            }

            return new ExactPropertyReader(
                candidates[0].PropertyType,
                candidates[0]);
        }

        public object Read(object? instance)
        {
            object? value;
            try
            {
                value = Property.GetValue(instance);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw new InvalidOperationException(
                    "Exact runtime member getter threw.",
                    ex.InnerException);
            }

            if (value == null || value.GetType() != ValueType)
            {
                throw new InvalidOperationException(
                    "Exact runtime member returned an unexpected value type.");
            }
            return value;
        }
    }
}
