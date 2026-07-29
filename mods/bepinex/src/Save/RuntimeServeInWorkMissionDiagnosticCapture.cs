using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeServeInWorkMissionDiagnosticCapture
{
    private const string SchedulerTypeName = "GameData.RunTime.Common.RunTimeScheduler";
    private const string TargetMethodName = "ContainsSpecialNPCServeInWorkMission";

    private static readonly object AttachRoot = new();
    private static readonly RuntimeServeInWorkMissionDiagnosticState State = new();

    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static bool _hookReady;

    public static RuntimeServeInWorkMissionDiagnosticSnapshot Snapshot()
    {
        return State.Snapshot();
    }

    public static void Attach(ManualLogSource log)
    {
        ArgumentNullException.ThrowIfNull(log);
        lock (AttachRoot)
        {
            _log = log;
            if (_hookReady) return;

            try
            {
                var target = ResolveTarget();
                var harmony = _harmony ??= new Harmony(
                    "com.tyukki.mystia-steward-companion.runtime-serve-in-work-diagnostic-capture");
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(RequireHook(nameof(OnPrefix)))
                    {
                        priority = Priority.First,
                    },
                    postfix: new HarmonyMethod(RequireHook(nameof(OnPostfix)))
                    {
                        priority = Priority.Last,
                    },
                    finalizer: new HarmonyMethod(RequireHook(nameof(OnFinalizer)))
                    {
                        priority = Priority.Last,
                    });
                _hookReady = true;
                State.SetHookStatus("patched:1/1", attached: true, DateTime.UtcNow);
                var business = RuntimeNightBusinessLifecycle.Snapshot;
                State.ApplyBusinessBoundary(
                    business.Generation,
                    business.Phase.ToString(),
                    DateTime.UtcNow);
                log.LogInfo(
                    "Runtime ServeInWork mission diagnostic capture patched as a passive observer.");
            }
            catch (Exception ex)
            {
                _hookReady = false;
                State.SetHookStatus("unavailable", attached: false, DateTime.UtcNow);
                log.LogWarning(
                    "Runtime ServeInWork mission diagnostic capture unavailable: "
                    + ex.GetBaseException().Message);
            }
        }
    }

    public static void ResetForMissionGeneration(
        long generation,
        DateTime changedAtUtc)
    {
        if (!_hookReady || generation <= 0) return;
        RunManagedTransition(
            "reset mission generation",
            () => State.ResetForMissionGeneration(generation, changedAtUtc));
    }

    public static void ApplyBusinessBoundary(
        NightBusinessLifecycleSnapshot snapshot,
        DateTime changedAtUtc)
    {
        if (!_hookReady) return;
        RunManagedTransition(
            "apply business boundary",
            () => State.ApplyBusinessBoundary(
                snapshot.Generation,
                snapshot.Phase.ToString(),
                changedAtUtc));
    }

    public static void ClearForMissionLifecycle(
        long generation,
        DateTime changedAtUtc)
    {
        if (!_hookReady || generation <= 0) return;
        RunManagedTransition(
            "clear mission lifecycle",
            () => State.ClearForMissionLifecycle(generation, changedAtUtc));
    }

    public static void ReconcileForMissionLifecycle(
        long generation,
        DateTime changedAtUtc)
    {
        if (!_hookReady || generation <= 0) return;
        try
        {
            _ = ReconcileForMissionLifecycleUnsafe(generation, changedAtUtc);
        }
        catch (Exception ex)
        {
            TryLogWarning(
                "Runtime ServeInWork mission lifecycle reconciliation failed closed: "
                + ex.GetBaseException().Message);
            try
            {
                _ = State.ClearForMissionLifecycle(generation, changedAtUtc);
            }
            catch (Exception clearException)
            {
                TryLogWarning(
                    "Runtime ServeInWork mission lifecycle reconciliation could not clear "
                    + "the expected generation: "
                    + clearException.GetBaseException().Message);
            }
        }
    }

    // A diagnostic callback must not replace the game's bool return if Il2CppInterop reports it.
    private static void OnPrefix(int __0, out HookFrame? __state)
    {
        __state = null;
        try
        {
            OnPrefixUnsafe(__0, out __state);
        }
        catch
        {
        }
    }

    private static void OnPostfix(
        ref int __1,
        bool __result,
        HookFrame? __state)
    {
        try
        {
            OnPostfixUnsafe(ref __1, __result, __state);
        }
        catch
        {
        }
    }

    private static Exception? OnFinalizer(
        Exception? __exception,
        HookFrame? __state)
    {
        try
        {
            _ = OnFinalizerUnsafe(__exception, __state);
        }
        catch
        {
        }

        return __exception;
    }

    private static void OnPrefixUnsafe(int __0, out HookFrame? __state)
    {
        __state = null;
        if (!_hookReady) return;

        try
        {
            var mission = RuntimeMissionDiagnosticCapture.Snapshot();
            var business = RuntimeNightBusinessLifecycle.Snapshot;
            __state = new HookFrame(
                mission.Generation,
                business.Generation,
                __0,
                ResolveCanonicalGuestId(__0));
        }
        catch (Exception ex)
        {
            LogCallbackFailure("prefix", ex);
        }
    }

    private static void OnPostfixUnsafe(
        ref int __1,
        bool __result,
        HookFrame? __state)
    {
        if (!_hookReady || __state == null || __state.Completed) return;

        try
        {
            ResolveDefinitionStatus(
                __state.MissionGeneration,
                __state.CanonicalGuestId,
                __1,
                __result,
                out var definitionStatus,
                out var expectedFoodId);
            State.ObserveResult(
                __state.MissionGeneration,
                __state.BusinessGeneration,
                __state.RawGuestId,
                __state.CanonicalGuestId,
                __1,
                __result,
                definitionStatus,
                expectedFoodId,
                DateTime.UtcNow);
            __state.Completed = true;
        }
        catch (Exception ex)
        {
            State.ObserveNativeException(
                __state.MissionGeneration,
                __state.BusinessGeneration,
                __state.RawGuestId,
                __state.CanonicalGuestId,
                $"diagnostic-{ex.GetBaseException().GetType().Name}",
                DateTime.UtcNow);
            __state.Completed = true;
            LogCallbackFailure("postfix", ex);
        }
    }

    private static Exception? OnFinalizerUnsafe(
        Exception? __exception,
        HookFrame? __state)
    {
        if (!_hookReady || __state == null || __state.Completed) return __exception;

        try
        {
            State.ObserveNativeException(
                __state.MissionGeneration,
                __state.BusinessGeneration,
                __state.RawGuestId,
                __state.CanonicalGuestId,
                __exception == null
                    ? "postfix-not-completed"
                    : __exception.GetBaseException().GetType().Name,
                DateTime.UtcNow);
            __state.Completed = true;
        }
        catch (Exception ex)
        {
            LogCallbackFailure("finalizer", ex);
        }

        return __exception;
    }

    private static void ResolveDefinitionStatus(
        long missionGeneration,
        int? canonicalGuestId,
        int foodId,
        bool result,
        out RuntimeServeInWorkMissionDefinitionStatus status,
        out int? expectedFoodId)
    {
        status = RuntimeServeInWorkMissionDefinitionStatus.Pending;
        expectedFoodId = null;
        if (!result) return;
        if (canonicalGuestId is not >= 0) return;
        if (!RuntimeMappedGuestCatalog.TryGetLoadedSnapshot(out var catalog)) return;
        if (!RuntimeMissionDiagnosticCapture.TryGetServeInWorkDefinitions(
                missionGeneration,
                out var definitions))
        {
            return;
        }

        var candidateFoods = new HashSet<int>();
        var unresolvedReceiver = false;
        foreach (var definition in definitions)
        {
            if (definition.Freshness == RuntimeMissionDiagnosticFreshness.Fulfilled)
            {
                continue;
            }
            if (!TryResolveReceiverCanonicalId(
                    catalog,
                    definition.Receiver,
                    out var receiverCanonicalId))
            {
                unresolvedReceiver = true;
                continue;
            }
            if (receiverCanonicalId != canonicalGuestId.Value) continue;
            foreach (var candidateFood in definition.FoodIds)
            {
                candidateFoods.Add(candidateFood);
            }
        }

        if (candidateFoods.Contains(foodId))
        {
            status = RuntimeServeInWorkMissionDefinitionStatus.Matched;
            expectedFoodId = foodId;
            return;
        }

        var missionSnapshot = RuntimeMissionDiagnosticCapture.Snapshot();
        if (unresolvedReceiver || missionSnapshot.DefinitionFailureCount > 0)
        {
            return;
        }

        status = RuntimeServeInWorkMissionDefinitionStatus.Mismatch;
        expectedFoodId = candidateFoods.Count == 1
            ? candidateFoods.Single()
            : null;
    }

    private static int? ResolveCanonicalGuestId(int rawGuestId)
    {
        if (!RuntimeMappedGuestCatalog.TryGetLoadedSnapshot(out var snapshot)
            || !snapshot.ByRuntimeId.TryGetValue(rawGuestId, out var entry)
            || entry.SourceGuestId is not >= 0)
        {
            return null;
        }

        return entry.SourceGuestId.Value;
    }

    private static bool TryResolveReceiverCanonicalId(
        RuntimeMappedGuestCatalogSnapshot snapshot,
        string receiver,
        out int canonicalGuestId)
    {
        canonicalGuestId = -1;
        if (string.IsNullOrWhiteSpace(receiver)) return false;

        var canonicalIds = new HashSet<int>();
        if (snapshot.ByRuntimeStringId.TryGetValue(receiver, out var runtimeEntry)
            && runtimeEntry.SourceGuestId is >= 0)
        {
            canonicalIds.Add(runtimeEntry.SourceGuestId.Value);
        }
        foreach (var entry in snapshot.Entries)
        {
            if (string.Equals(
                    entry.SourceStringId,
                    receiver,
                    StringComparison.Ordinal)
                && entry.SourceGuestId is >= 0)
            {
                canonicalIds.Add(entry.SourceGuestId.Value);
            }
        }

        if (canonicalIds.Count != 1) return false;
        canonicalGuestId = canonicalIds.Single();
        return true;
    }

    private static bool ReconcileForMissionLifecycleUnsafe(
        long generation,
        DateTime changedAtUtc)
    {
        if (!RuntimeMappedGuestCatalog.TryGetLoadedSnapshot(out var catalog)
            || !RuntimeMissionDiagnosticCapture.TryGetServeInWorkDefinitions(
                generation,
                out var definitions))
        {
            return State.ClearForMissionLifecycle(generation, changedAtUtc);
        }

        var reconcileDefinitions = definitions
            .Select(definition => new RuntimeServeInWorkMissionReconcileDefinition(
                definition.Receiver,
                definition.FoodIds,
                definition.Freshness == RuntimeMissionDiagnosticFreshness.Fulfilled))
            .ToArray();
        if (!RuntimeServeInWorkMissionSignalReconciler.TryBuildActiveSignalKeys(
                reconcileDefinitions,
                receiver => TryResolveReceiverCanonicalId(
                    catalog,
                    receiver,
                    out var canonicalGuestId)
                        ? canonicalGuestId
                        : null,
                out var activeSignals))
        {
            return State.ClearForMissionLifecycle(generation, changedAtUtc);
        }

        return State.ReconcileForMissionLifecycle(
            generation,
            activeSignals,
            changedAtUtc);
    }

    private static MethodInfo ResolveTarget()
    {
        var schedulerType = RuntimeReflectionUtility.FindType(SchedulerTypeName)
            ?? throw new InvalidOperationException($"{SchedulerTypeName} is not loaded.");
        var candidates = schedulerType
            .GetMethods(
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly)
            .Where(method => string.Equals(
                    method.Name,
                    TargetMethodName,
                    StringComparison.Ordinal)
                && method.ReturnType == typeof(bool)
                && !method.IsGenericMethod)
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType == typeof(int)
                    && parameters[1].IsOut
                    && parameters[1].ParameterType == typeof(int).MakeByRefType();
            })
            .ToArray();
        return candidates.Length == 1
            ? candidates[0]
            : throw new InvalidOperationException(
                $"{SchedulerTypeName}.{TargetMethodName} exact overload count was {candidates.Length}.");
    }

    private static MethodInfo RequireHook(string name)
    {
        return typeof(RuntimeServeInWorkMissionDiagnosticCapture).GetMethod(
                name,
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing ServeInWork hook {name}.");
    }

    private static void RunManagedTransition(string label, Func<bool> transition)
    {
        try
        {
            _ = transition();
        }
        catch (Exception ex)
        {
            TryLogWarning(
                $"Runtime ServeInWork mission diagnostic could not {label}: "
                + ex.GetBaseException().Message);
        }
    }

    private static void LogCallbackFailure(string source, Exception exception)
    {
        TryLogWarning(
            $"Runtime ServeInWork mission diagnostic callback {source} failed "
            + "without affecting the game method: "
            + exception.GetBaseException().Message);
    }

    private static void TryLogWarning(string message)
    {
        try
        {
            _log?.LogWarning(message);
        }
        catch
        {
        }
    }

    private sealed class HookFrame
    {
        public HookFrame(
            long missionGeneration,
            long businessGeneration,
            int rawGuestId,
            int? canonicalGuestId)
        {
            MissionGeneration = missionGeneration;
            BusinessGeneration = businessGeneration;
            RawGuestId = rawGuestId;
            CanonicalGuestId = canonicalGuestId;
        }

        public long MissionGeneration { get; }
        public long BusinessGeneration { get; }
        public int RawGuestId { get; }
        public int? CanonicalGuestId { get; }
        public bool Completed { get; set; }
    }
}
