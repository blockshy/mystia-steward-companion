using System.Reflection;
using BepInEx.Logging;

namespace MystiaStewardCompanion.Save;

internal readonly record struct RuntimeNightBusinessAutomationGateSnapshot(
    bool Allowed,
    string BlockReason,
    string Status,
    long Generation,
    long Version,
    bool TutorialLatched,
    bool RuntimeAvailable);

/// <summary>
/// Reads the game's exact night-scene tutorial state and blocks every automation side effect while it is active.
/// </summary>
internal static class RuntimeNightBusinessAutomationGate
{
    public const string LifecycleUnavailableReason = "night-business-lifecycle-unavailable";
    public const string TutorialActiveReason = "night-business-tutorial-active";
    public const string TutorialStateUnavailableReason = "night-business-tutorial-state-unavailable";

    private const string NightSceneDirectorTypeName = "NightScene.NightSceneDirector";
    private const string MonoSingletonTypeDefinitionName = "DEYU.Singletons.MonoSingleton`1";
    private const string TutorialPropertyName = "IsInTutorial";

    private static readonly object SyncRoot = new();
    private static RuntimeNightBusinessAutomationGateSnapshot _snapshot = new(
        Allowed: false,
        BlockReason: LifecycleUnavailableReason,
        Status: "blocked; reason=night-business-lifecycle-unavailable; detail=not initialized",
        Generation: 0,
        Version: 0,
        TutorialLatched: false,
        RuntimeAvailable: false);
    private static ManualLogSource? _log;
    private static int _mainThreadId;
    private static long _tutorialGeneration;
    private static Type? _cachedDirectorType;
    private static MethodInfo? _cachedSingletonGetter;
    private static MethodInfo? _cachedTutorialGetter;

    public static RuntimeNightBusinessAutomationGateSnapshot Snapshot
    {
        get
        {
            lock (SyncRoot) return _snapshot;
        }
    }

    public static void Initialize(int mainThreadId, ManualLogSource log)
    {
        if (mainThreadId <= 0) throw new ArgumentOutOfRangeException(nameof(mainThreadId));

        lock (SyncRoot)
        {
            _mainThreadId = mainThreadId;
            _log = log;
            _tutorialGeneration = 0;
            _cachedDirectorType = null;
            _cachedSingletonGetter = null;
            _cachedTutorialGetter = null;
            PublishLocked(
                allowed: false,
                blockReason: LifecycleUnavailableReason,
                status: "blocked; reason=night-business-lifecycle-unavailable; detail=waiting for active business",
                generation: RuntimeNightBusinessLifecycle.Generation,
                tutorialLatched: false,
                runtimeAvailable: false);
        }
    }

    /// <summary>
    /// Fresh-reads <c>NightSceneDirector.IsInTutorial</c> on the initialized Unity main thread.
    /// </summary>
    public static RuntimeNightBusinessAutomationGateSnapshot Refresh()
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        lock (SyncRoot)
        {
            if (_mainThreadId <= 0 || currentThreadId != _mainThreadId)
            {
                var generation = RuntimeNightBusinessLifecycle.Generation;
                return new RuntimeNightBusinessAutomationGateSnapshot(
                    Allowed: false,
                    BlockReason: TutorialStateUnavailableReason,
                    Status: $"blocked; reason={TutorialStateUnavailableReason}; detail=Unity main thread required; thread={currentThreadId}; expected={_mainThreadId}",
                    Generation: generation,
                    Version: _snapshot.Version,
                    TutorialLatched: _tutorialGeneration == generation && generation > 0,
                    RuntimeAvailable: false);
            }

            var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
            if (!lifecycle.IsActive)
            {
                _tutorialGeneration = 0;
                return PublishLocked(
                    allowed: false,
                    blockReason: LifecycleUnavailableReason,
                    status: $"blocked; reason={LifecycleUnavailableReason}; phase={lifecycle.Phase}; generation={lifecycle.Generation}",
                    generation: lifecycle.Generation,
                    tutorialLatched: false,
                    runtimeAvailable: false);
            }

            if (_tutorialGeneration == lifecycle.Generation)
            {
                return PublishLocked(
                    allowed: false,
                    blockReason: TutorialActiveReason,
                    status: $"blocked; reason={TutorialActiveReason}; generation={lifecycle.Generation}; tutorial=latched",
                    generation: lifecycle.Generation,
                    tutorialLatched: true,
                    runtimeAvailable: true);
            }

            if (!TryReadTutorialState(out var inTutorial, out var diagnostic))
            {
                diagnostic = NormalizeDiagnostic(diagnostic);
                return PublishLocked(
                    allowed: false,
                    blockReason: TutorialStateUnavailableReason,
                    status: $"blocked; reason={TutorialStateUnavailableReason}; generation={lifecycle.Generation}; detail={diagnostic}",
                    generation: lifecycle.Generation,
                    tutorialLatched: false,
                    runtimeAvailable: false);
            }

            if (inTutorial)
            {
                _tutorialGeneration = lifecycle.Generation;
                return PublishLocked(
                    allowed: false,
                    blockReason: TutorialActiveReason,
                    status: $"blocked; reason={TutorialActiveReason}; generation={lifecycle.Generation}; tutorial=latched",
                    generation: lifecycle.Generation,
                    tutorialLatched: true,
                    runtimeAvailable: true);
            }

            return PublishLocked(
                allowed: true,
                blockReason: "",
                status: $"allowed; generation={lifecycle.Generation}; tutorial=false; source={NightSceneDirectorTypeName}.{TutorialPropertyName}",
                generation: lifecycle.Generation,
                tutorialLatched: false,
                runtimeAvailable: true);
        }
    }

    private static RuntimeNightBusinessAutomationGateSnapshot PublishLocked(
        bool allowed,
        string blockReason,
        string status,
        long generation,
        bool tutorialLatched,
        bool runtimeAvailable)
    {
        if (_snapshot.Allowed == allowed
            && string.Equals(_snapshot.BlockReason, blockReason, StringComparison.Ordinal)
            && string.Equals(_snapshot.Status, status, StringComparison.Ordinal)
            && _snapshot.Generation == generation
            && _snapshot.TutorialLatched == tutorialLatched
            && _snapshot.RuntimeAvailable == runtimeAvailable)
        {
            return _snapshot;
        }

        var previous = _snapshot;
        _snapshot = new RuntimeNightBusinessAutomationGateSnapshot(
            allowed,
            blockReason,
            status,
            generation,
            previous.Version + 1,
            tutorialLatched,
            runtimeAvailable);

        if (!string.Equals(previous.BlockReason, blockReason, StringComparison.Ordinal)
            || previous.Generation != generation)
        {
            if (allowed)
            {
                _log?.LogInfo($"Night-business automation gate opened: {status}.");
            }
            else if (string.Equals(blockReason, TutorialStateUnavailableReason, StringComparison.Ordinal))
            {
                _log?.LogWarning($"Night-business automation gate unavailable and fail-closed: {status}.");
            }
            else
            {
                _log?.LogInfo($"Night-business automation gate closed: {status}.");
            }
        }

        return _snapshot;
    }

    private static bool TryReadTutorialState(out bool inTutorial, out string diagnostic)
    {
        inTutorial = false;
        diagnostic = "";
        try
        {
            if (!TryResolveExactMembers(out var directorType, out var singletonGetter, out var tutorialGetter, out diagnostic))
            {
                return false;
            }

            var director = singletonGetter.Invoke(null, Array.Empty<object?>());
            if (director == null || !directorType.IsInstanceOfType(director))
            {
                diagnostic = "exact NightSceneDirector singleton is null or has the wrong runtime type";
                return false;
            }

            var value = tutorialGetter.Invoke(director, Array.Empty<object?>());
            if (value is not bool tutorial)
            {
                diagnostic = "IsInTutorial did not return System.Boolean";
                return false;
            }

            inTutorial = tutorial;
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = ex.GetBaseException().Message;
            return false;
        }
    }

    private static bool TryResolveExactMembers(
        out Type directorType,
        out MethodInfo singletonGetter,
        out MethodInfo tutorialGetter,
        out string diagnostic)
    {
        if (_cachedDirectorType != null && _cachedSingletonGetter != null && _cachedTutorialGetter != null)
        {
            directorType = _cachedDirectorType;
            singletonGetter = _cachedSingletonGetter;
            tutorialGetter = _cachedTutorialGetter;
            diagnostic = "";
            return true;
        }

        directorType = RuntimeReflectionUtility.FindType(NightSceneDirectorTypeName)!;
        singletonGetter = null!;
        tutorialGetter = null!;
        diagnostic = "";
        if (directorType == null)
        {
            diagnostic = $"type {NightSceneDirectorTypeName} is not loaded";
            return false;
        }

        var baseType = directorType.BaseType;
        if (baseType == null || !baseType.IsGenericType || baseType.ContainsGenericParameters)
        {
            diagnostic = "NightSceneDirector does not have a closed direct generic base";
            return false;
        }

        Type definition;
        Type[] arguments;
        try
        {
            definition = baseType.GetGenericTypeDefinition();
            arguments = baseType.GetGenericArguments();
        }
        catch (Exception ex)
        {
            diagnostic = ex.GetBaseException().Message;
            return false;
        }

        if (!string.Equals(definition.FullName, MonoSingletonTypeDefinitionName, StringComparison.Ordinal)
            || arguments.Length != 1
            || arguments[0] != directorType)
        {
            diagnostic = $"NightSceneDirector direct base is not {MonoSingletonTypeDefinitionName}<NightSceneDirector>";
            return false;
        }

        var resolvedDirectorType = directorType;
        var singletonGetters = baseType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == "get_Instance"
                && method.ReturnType == resolvedDirectorType
                && method.GetParameters().Length == 0)
            .ToArray();
        if (singletonGetters.Length != 1)
        {
            diagnostic = $"exact MonoSingleton.get_Instance count was {singletonGetters.Length}";
            return false;
        }

        var tutorialGetters = resolvedDirectorType
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.Name == TutorialPropertyName
                && property.PropertyType == typeof(bool)
                && property.GetIndexParameters().Length == 0)
            .Select(property => property.GetGetMethod(nonPublic: true))
            .Where(method => method != null
                && !method.IsStatic
                && method.ReturnType == typeof(bool)
                && method.GetParameters().Length == 0)
            .Cast<MethodInfo>()
            .ToArray();
        if (tutorialGetters.Length != 1)
        {
            diagnostic = $"exact NightSceneDirector.IsInTutorial getter count was {tutorialGetters.Length}";
            return false;
        }

        singletonGetter = singletonGetters[0];
        tutorialGetter = tutorialGetters[0];
        _cachedDirectorType = directorType;
        _cachedSingletonGetter = singletonGetter;
        _cachedTutorialGetter = tutorialGetter;
        return true;
    }

    private static string NormalizeDiagnostic(string diagnostic)
    {
        var normalized = string.IsNullOrWhiteSpace(diagnostic)
            ? "unknown exact runtime read failure"
            : diagnostic.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }
}
