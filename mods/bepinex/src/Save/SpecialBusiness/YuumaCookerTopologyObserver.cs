using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

internal static class YuumaCookerTopologyObserver
{
    private const string EventManagerTypeName = "NightScene.EventUtility.EventManager";
    private const string PartnerManagerTypeName = "NightScene.PartnerUtility.PartnerManager";
    private const string Il2CppEnumerableTypeName = "Il2CppSystem.Collections.Generic.IEnumerable`1";
    private const string Il2CppActionTypeName = "Il2CppSystem.Action";
    private const string BuffTypeName = "NightScene.EventUtility.EventManager+BuffType";
    private const int ExpectedHookCount = 3;
    private const int MaxCallbackFailureLogs = 16;

    private static readonly object SyncRoot = new();
    private static readonly YuumaCookerTopologyTracker Tracker = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly HashSet<string> CallbackFailures = new(StringComparer.Ordinal);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static DateTime _lastAttachAttemptUtc = DateTime.MinValue;
    private static string _attachStatus = "not attached";
    private static string _lastResetReason = "";

    public static string Status
    {
        get
        {
            lock (SyncRoot)
            {
                return $"hooks={_attachStatus}; "
                    + Tracker.Describe(HooksReadyLocked(), _lastResetReason);
            }
        }
    }

    public static void Attach(ManualLogSource log)
    {
        _log = log;
        TryAttach(force: true);
    }

    public static void Reset(string reason)
    {
        lock (SyncRoot)
        {
            Tracker.Reset();
            CallbackFailures.Clear();
            _lastResetReason = reason;
        }
    }

    public static bool TryAcquireFreshLease(
        out YuumaCookerTopologyLease lease,
        out string diagnostic)
    {
        lease = null!;
        TryAttach(force: false);
        if (!TryGetActiveYuumaGeneration(out var generation))
        {
            diagnostic = "blood-pond-hell-context-unavailable";
            return false;
        }

        YuumaCookerTopologySnapshotProbe probe;
        lock (SyncRoot)
        {
            if (!Tracker.TryBeginSnapshot(
                    generation,
                    HooksReadyLocked(),
                    out probe,
                    out diagnostic))
            {
                diagnostic = $"{diagnostic}; {StatusLocked()}";
                return false;
            }
        }

        if (!TryCaptureFreshSnapshot(out var snapshot, out diagnostic)) return false;
        if (!TryGetActiveYuumaGeneration(out var currentGeneration)
            || currentGeneration != generation)
        {
            diagnostic = "blood-pond-hell-generation-changed-during-snapshot";
            return false;
        }

        lock (SyncRoot)
        {
            if (!Tracker.TryCommitSnapshot(
                    probe,
                    HooksReadyLocked(),
                    snapshot.Signature,
                    snapshot.ControllerCount,
                    snapshot.LockedControllerCount,
                    out lease,
                    out diagnostic))
            {
                diagnostic = $"{diagnostic}; {StatusLocked()}";
                return false;
            }
        }

        return true;
    }

    public static bool TryValidateFreshLease(
        YuumaCookerTopologyLease lease,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(lease);
        TryAttach(force: false);
        if (!TryGetActiveYuumaGeneration(out var generation)
            || generation != lease.BusinessGeneration)
        {
            diagnostic = "blood-pond-hell-generation-does-not-match-lease";
            return false;
        }

        YuumaCookerTopologySnapshotProbe probe;
        lock (SyncRoot)
        {
            if (!Tracker.TryBeginSnapshot(
                    generation,
                    HooksReadyLocked(),
                    out probe,
                    out diagnostic))
            {
                diagnostic = $"{diagnostic}; {StatusLocked()}";
                return false;
            }
        }

        if (!TryCaptureFreshSnapshot(out var snapshot, out diagnostic)) return false;
        if (!TryGetActiveYuumaGeneration(out var currentGeneration)
            || currentGeneration != generation)
        {
            diagnostic = "blood-pond-hell-generation-changed-during-validation";
            return false;
        }

        lock (SyncRoot)
        {
            if (!Tracker.TryValidateSnapshot(
                    probe,
                    HooksReadyLocked(),
                    lease,
                    snapshot.Signature,
                    snapshot.ControllerCount,
                    snapshot.LockedControllerCount,
                    out diagnostic))
            {
                diagnostic = $"{diagnostic}; {StatusLocked()}";
                return false;
            }
        }

        return true;
    }

    private static bool TryCaptureFreshSnapshot(
        out YuumaCookerTopologySnapshotIdentity snapshot,
        out string diagnostic)
    {
        snapshot = null!;
        object? cookSystem;
        try
        {
            cookSystem = RuntimeCookerReflection.GetCookSystemManager();
        }
        catch (Exception ex)
        {
            diagnostic = $"cook-system-manager-read-failed; {FormatException(ex)}";
            return false;
        }

        if (cookSystem == null)
        {
            diagnostic = "cook-system-manager-unavailable";
            return false;
        }

        if (!RuntimeCookerReflection.TryReadLockedCookerPositions(
                out var lockedPositions,
                out var lockedCookersStatus))
        {
            diagnostic = $"fresh-locked-cookers-unavailable; {lockedCookersStatus}";
            return false;
        }

        if (!RuntimeCookerReflection.TryReadCookerControllerEntriesFromCookSystem(
                cookSystem,
                lockedPositions,
                out var entries,
                out var allCookersStatus))
        {
            diagnostic = $"fresh-all-cookers-unavailable; {allCookersStatus}; {lockedCookersStatus}";
            return false;
        }

        var controllers = entries
            .Select((entry, index) => new YuumaCookerTopologyControllerIdentity(
                index,
                entry.ControllerIdentity,
                new YuumaCookerTopologyPosition(
                    entry.GridPosition.X,
                    entry.GridPosition.Y,
                    entry.GridPosition.Z)))
            .ToArray();
        var locked = lockedPositions
            .Select(position => new YuumaCookerTopologyPosition(position.X, position.Y, position.Z))
            .ToArray();
        if (!YuumaCookerTopologySnapshotIdentityBuilder.TryCreate(
                controllers,
                locked,
                out snapshot,
                out var identityStatus))
        {
            diagnostic = $"fresh-topology-identity-invalid; {identityStatus}; "
                + $"{allCookersStatus}; {lockedCookersStatus}";
            return false;
        }

        diagnostic = $"{identityStatus}; {allCookersStatus}; {lockedCookersStatus}";
        return true;
    }

    private static void TryAttach(bool force)
    {
        lock (SyncRoot)
        {
            if (HooksReadyLocked()) return;
            if (!force && DateTime.UtcNow - _lastAttachAttemptUtc < RetryInterval) return;
            _lastAttachAttemptUtc = DateTime.UtcNow;
        }

        var patchedNow = new List<string>();
        var failures = new List<string>();
        try
        {
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.yuuma-cooker-topology-observer");
            PatchExactInstanceMethod(
                _harmony,
                EventManagerTypeName,
                "LockCookers",
                method => MatchesMethod(
                    method,
                    IsVoid,
                    IsClosedGeneric(Il2CppEnumerableTypeName, IsInt32),
                    IsInt32,
                    IsByRef(IsExact(Il2CppActionTypeName)),
                    IsExact(Il2CppActionTypeName),
                    IsExact(BuffTypeName)),
                nameof(OnLockCookersPrefix),
                nameof(OnLockCookersPostfix),
                patchedNow,
                failures);
            PatchExactInstanceMethod(
                _harmony,
                EventManagerTypeName,
                "LockCookers_Forever",
                method => MatchesMethod(
                    method,
                    IsVoid,
                    IsClosedGeneric(Il2CppEnumerableTypeName, IsInt32),
                    IsByRef(IsExact(Il2CppActionTypeName)),
                    IsExact(Il2CppActionTypeName)),
                nameof(OnLockCookersForeverPrefix),
                nameof(OnLockCookersForeverPostfix),
                patchedNow,
                failures);
            PatchExactInstanceMethod(
                _harmony,
                PartnerManagerTypeName,
                "OnCookerAvailabilityUpdate",
                method => MatchesMethod(method, IsVoid, IsInt32),
                nameof(OnCookerAvailabilityUpdatePrefix),
                nameof(OnCookerAvailabilityUpdatePostfix),
                patchedNow,
                failures);

            lock (SyncRoot)
            {
                _attachStatus = HooksReadyLocked()
                    ? $"patched={PatchedMethods.Count}/{ExpectedHookCount}"
                    : $"partial={PatchedMethods.Count}/{ExpectedHookCount}; "
                        + $"unavailable={string.Join(",", failures.Take(3))}";
            }

            if (patchedNow.Count > 0)
            {
                _log?.LogInfo($"Blood Pond Hell cooker topology observer patched: {string.Join(", ", patchedNow)}.");
            }
            if (force && failures.Count > 0)
            {
                _log?.LogWarning(
                    "Blood Pond Hell cooker topology observer is incomplete; "
                    + $"Yuuma automation will fail closed: {string.Join(", ", failures.Take(3))}.");
            }
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                _attachStatus = $"error:{FormatException(ex)}";
            }

            _log?.LogWarning(
                "Blood Pond Hell cooker topology observer attach failed; Yuuma automation will fail closed: "
                + FormatException(ex));
        }
    }

    private static void PatchExactInstanceMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        Func<MethodInfo, bool> signature,
        string prefixName,
        string postfixName,
        ICollection<string> patchedNow,
        ICollection<string> failures)
    {
        var key = $"{typeName}.{methodName}";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        var type = RuntimeReflectionUtility.FindType(typeName);
        if (type == null)
        {
            failures.Add($"{key}:type-missing");
            return;
        }

        var candidates = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .Where(signature)
            .ToArray();
        var prefix = typeof(YuumaCookerTopologyObserver).GetMethod(
            prefixName,
            BindingFlags.NonPublic | BindingFlags.Static);
        var postfix = typeof(YuumaCookerTopologyObserver).GetMethod(
            postfixName,
            BindingFlags.NonPublic | BindingFlags.Static);
        if (candidates.Length != 1 || prefix == null || postfix == null)
        {
            failures.Add($"{key}:exact-count={candidates.Length}");
            return;
        }

        try
        {
            harmony.Patch(
                candidates[0],
                prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
        }
        catch (Exception ex)
        {
            failures.Add($"{key}:patch={FormatException(ex)}");
            return;
        }

        lock (SyncRoot)
        {
            PatchedMethods.Add(key);
        }
        patchedNow.Add(key);
    }

    private static void OnLockCookersPrefix(out YuumaCookerTopologyMutationFrame? __state)
    {
        const string source = "EventManager.LockCookers";
        __state = BeginMutation(source);
        if (__state != null) RuntimeCookerHighlightService.BeginTopologyMutation(source);
    }

    private static void OnLockCookersPostfix(
        YuumaCookerTopologyMutationFrame? __state,
        bool __runOriginal)
    {
        CompleteMutation(__state, __runOriginal);
        if (__state != null)
        {
            RuntimeCookerHighlightService.CompleteTopologyMutation("EventManager.LockCookers");
        }
    }

    private static void OnLockCookersForeverPrefix(out YuumaCookerTopologyMutationFrame? __state)
    {
        const string source = "EventManager.LockCookers_Forever";
        __state = BeginMutation(source);
        if (__state != null) RuntimeCookerHighlightService.BeginTopologyMutation(source);
    }

    private static void OnLockCookersForeverPostfix(
        YuumaCookerTopologyMutationFrame? __state,
        bool __runOriginal)
    {
        CompleteMutation(__state, __runOriginal);
        if (__state != null)
        {
            RuntimeCookerHighlightService.CompleteTopologyMutation("EventManager.LockCookers_Forever");
        }
    }

    private static void OnCookerAvailabilityUpdatePrefix(
        int __0,
        out YuumaCookerTopologyMutationFrame? __state)
    {
        var source = $"PartnerManager.OnCookerAvailabilityUpdate({__0})";
        __state = BeginMutation(source);
        if (__state != null) RuntimeCookerHighlightService.BeginTopologyMutation(source);
    }

    private static void OnCookerAvailabilityUpdatePostfix(
        YuumaCookerTopologyMutationFrame? __state,
        bool __runOriginal)
    {
        CompleteMutation(__state, __runOriginal);
        if (__state != null)
        {
            RuntimeCookerHighlightService.CompleteTopologyMutation(
                "PartnerManager.OnCookerAvailabilityUpdate");
        }
    }

    private static YuumaCookerTopologyMutationFrame? BeginMutation(string source)
    {
        try
        {
            if (!RuntimeSpecialBusinessContextService.TryGetActiveYuumaGeneration(
                    out var generation))
            {
                return null;
            }

            lock (SyncRoot)
            {
                return Tracker.BeginMutation(generation, source);
            }
        }
        catch (Exception ex)
        {
            LogCallbackFailure($"{source}.Prefix", ex);
            return null;
        }
    }

    private static void CompleteMutation(
        YuumaCookerTopologyMutationFrame? frame,
        bool originalRan)
    {
        if (frame == null) return;
        try
        {
            lock (SyncRoot)
            {
                Tracker.CompleteMutation(frame, originalRan);
            }
        }
        catch (Exception ex)
        {
            LogCallbackFailure($"{frame.Source}.Postfix(runOriginal={originalRan})", ex);
        }
    }

    private static void LogCallbackFailure(string source, Exception exception)
    {
        var error = exception.GetBaseException();
        var key = $"{source}|{error.GetType().FullName}|{error.Message}";
        lock (SyncRoot)
        {
            if (CallbackFailures.Contains(key)
                || CallbackFailures.Count >= MaxCallbackFailureLogs)
            {
                return;
            }
            CallbackFailures.Add(key);
        }

        _log?.LogWarning(
            $"Blood Pond Hell cooker topology callback {source} failed without affecting the game method: "
            + error.Message);
    }

    private static bool TryGetActiveYuumaGeneration(out long generation)
    {
        return RuntimeSpecialBusinessContextService.TryGetActiveYuumaGeneration(out generation);
    }

    private static bool HooksReadyLocked()
    {
        return PatchedMethods.Count == ExpectedHookCount;
    }

    private static string StatusLocked()
    {
        return $"hooks={_attachStatus}; "
            + Tracker.Describe(HooksReadyLocked(), _lastResetReason);
    }

    private static bool MatchesMethod(
        MethodInfo method,
        Func<Type, bool> returnType,
        params Func<Type, bool>[] parameters)
    {
        if (method.IsStatic || !returnType(method.ReturnType)) return false;
        var actual = method.GetParameters();
        if (actual.Length != parameters.Length) return false;
        for (var index = 0; index < actual.Length; index++)
        {
            if (!parameters[index](actual[index].ParameterType)) return false;
        }
        return true;
    }

    private static Func<Type, bool> IsExact(string typeName)
    {
        return type => string.Equals(type.FullName, typeName, StringComparison.Ordinal);
    }

    private static Func<Type, bool> IsClosedGeneric(
        string genericTypeName,
        Func<Type, bool> argument)
    {
        return type =>
        {
            if (!type.IsGenericType || type.ContainsGenericParameters) return false;
            try
            {
                var arguments = type.GetGenericArguments();
                return string.Equals(
                        type.GetGenericTypeDefinition().FullName,
                        genericTypeName,
                        StringComparison.Ordinal)
                    && arguments.Length == 1
                    && argument(arguments[0]);
            }
            catch
            {
                return false;
            }
        };
    }

    private static Func<Type, bool> IsByRef(Func<Type, bool> element)
    {
        return type => type.IsByRef
            && type.GetElementType() is { } elementType
            && element(elementType);
    }

    private static bool IsVoid(Type type) => type == typeof(void);

    private static bool IsInt32(Type type) => type == typeof(int);

    private static string FormatException(Exception exception)
    {
        var error = exception.GetBaseException();
        return $"{error.GetType().Name}:{error.Message}";
    }
}
