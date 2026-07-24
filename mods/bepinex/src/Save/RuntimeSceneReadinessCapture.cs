using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeSceneReadinessCapture
{
    private const string DaySceneSustainedPanelTypeName = "DayScene.UI.DaySceneSustainedPannel";
    private const string DaySceneManagerTypeName = "DayScene.SceneManager";
    private const string RunTimeSchedulerTypeName = "GameData.RunTime.Common.RunTimeScheduler";
    private const string SceneDirectorTypeName = "Common.SceneDirector";
    private const string UniversalGameManagerTypeName = "Common.UI.UniversalGameManager";
    private const string NightSceneDirectorTypeName = "NightScene.NightSceneDirector";
    private const string IzakayaConfigPanelTypeName = "PrepNightScene.UI.IzakayaConfigPannel";

    private static readonly object SyncRoot = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);

    private static Harmony? _harmony;
    private static readonly RuntimeDaySceneReadinessState DaySceneState = new();
    private static Type? _daySceneManagerType;
    private static Type? _runTimeSchedulerType;
    private static Type? _sceneDirectorType;
    private static Type? _universalGameManagerType;
    private static Type? _nightSceneDirectorType;
    private static bool _dayHooksComplete;
    private static bool _izakayaPrepReady;
    private static long _changeVersion;
    private static string _status = "not attached";
    private static string _lastEvent = "";

    public static bool IzakayaPrepReady
    {
        get
        {
            lock (SyncRoot)
            {
                return _izakayaPrepReady;
            }
        }
    }

    public static long ChangeVersion
    {
        get
        {
            lock (SyncRoot)
            {
                return _changeVersion;
            }
        }
    }

    public static long DaySceneGeneration
    {
        get
        {
            lock (SyncRoot)
            {
                return DaySceneState.Generation;
            }
        }
    }

    public static string Status
    {
        get
        {
            lock (SyncRoot)
            {
                return $"{_status}; day=({DaySceneState.Status}); prep={(_izakayaPrepReady ? "ready" : "waiting")}; last={_lastEvent}";
            }
        }
    }

    public static void Attach(ManualLogSource log)
    {
        try
        {
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.runtime-scene-readiness");
            var patchedNow = new List<string>();
            var missing = new List<string>();

            _daySceneManagerType = RuntimeReflectionUtility.FindType(DaySceneManagerTypeName);
            _runTimeSchedulerType = RuntimeReflectionUtility.FindType(RunTimeSchedulerTypeName);
            _sceneDirectorType = RuntimeReflectionUtility.FindType(SceneDirectorTypeName);
            _universalGameManagerType = RuntimeReflectionUtility.FindType(UniversalGameManagerTypeName);
            _nightSceneDirectorType = RuntimeReflectionUtility.FindType(NightSceneDirectorTypeName);
            PatchMethod(_harmony, DaySceneSustainedPanelTypeName, "OnPannelPostOpen", 0, null, nameof(OnDayScenePanelOpened), patchedNow, missing);
            PatchMethod(_harmony, DaySceneSustainedPanelTypeName, "OnPrePanelDestroyed", 0, null, nameof(OnDaySceneDestroyed), patchedNow, missing);
            PatchMethod(_harmony, DaySceneManagerTypeName, "OnFirstEnterDaySceneFinish", 0, nameof(OnFirstEnterDayScenePrefix), nameof(OnFirstEnterDayScenePostfix), patchedNow, missing);
            PatchMethod(_harmony, RunTimeSchedulerTypeName, "OnEnterDayScene", 2, nameof(OnEnterDayScenePrefix), null, patchedNow, missing);
            PatchMethod(_harmony, RunTimeSchedulerTypeName, "OnEnterDaySceneMap", 2, nameof(OnEnterDaySceneMapPrefix), null, patchedNow, missing);
            PatchMethod(_harmony, RunTimeSchedulerTypeName, "DefaultOnFinish", 1, nameof(OnSchedulerFinishPrefix), nameof(OnSchedulerFinishPostfix), patchedNow, missing);
            PatchMethod(_harmony, IzakayaConfigPanelTypeName, "OnPanelOpen", 1, null, nameof(OnIzakayaPrepReady), patchedNow, missing);
            PatchMethod(_harmony, IzakayaConfigPanelTypeName, "GoToSpecific", 1, null, nameof(OnIzakayaPrepSpecificReady), patchedNow, missing);
            PatchMethod(_harmony, IzakayaConfigPanelTypeName, "Cleanup_Generated", 0, null, nameof(OnIzakayaPrepClosed), patchedNow, missing);
            PatchMethod(_harmony, IzakayaConfigPanelTypeName, "GotoWork", 0, null, nameof(OnIzakayaPrepClosed), patchedNow, missing);

            lock (SyncRoot)
            {
                _dayHooksComplete = new[]
                    {
                        $"{DaySceneSustainedPanelTypeName}.OnPannelPostOpen/0",
                        $"{DaySceneSustainedPanelTypeName}.OnPrePanelDestroyed/0",
                        $"{DaySceneManagerTypeName}.OnFirstEnterDaySceneFinish/0",
                        $"{RunTimeSchedulerTypeName}.OnEnterDayScene/2",
                        $"{RunTimeSchedulerTypeName}.OnEnterDaySceneMap/2",
                        $"{RunTimeSchedulerTypeName}.DefaultOnFinish/1",
                    }
                    .All(PatchedMethods.Contains);
                _status = PatchedMethods.Count == 0
                    ? $"unavailable: {string.Join(", ", missing.Take(4))}"
                    : missing.Count == 0
                        ? $"patched={PatchedMethods.Count}; dayHooks={(_dayHooksComplete ? "complete" : "incomplete")}"
                        : $"patched={PatchedMethods.Count}; dayHooks={(_dayHooksComplete ? "complete" : "incomplete")}; missing={string.Join(", ", missing.Take(4))}";
            }

            if (patchedNow.Count > 0)
            {
                log.LogInfo($"Runtime scene readiness patched: {string.Join(", ", patchedNow)}.");
            }
            else if (PatchedMethods.Count == 0)
            {
                log.LogWarning($"Runtime scene readiness unavailable; game members were not found: {string.Join(", ", missing.Take(4))}.");
            }
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                _status = $"error: {ex.Message}";
            }

            log.LogWarning($"Runtime scene readiness attach failed: {ex.Message}");
        }
    }

    public static void ClearForSceneChange(string sceneName)
    {
        var reason = string.IsNullOrWhiteSpace(sceneName) ? "scene changed" : $"scene changed: {sceneName}";
        lock (SyncRoot)
        {
            UpdateDayStateLocked(() => DaySceneState.Reset(reason), reason);
            SetPrepReadyLocked(false, reason);
        }
    }

    public static bool CanReadDaySceneRuntime()
    {
        Type? managerType;
        Type? schedulerType;
        Type? sceneDirectorType;
        Type? universalGameManagerType;
        lock (SyncRoot)
        {
            if (!_dayHooksComplete || !DaySceneState.Ready) return false;
            managerType = _daySceneManagerType;
            schedulerType = _runTimeSchedulerType;
            sceneDirectorType = _sceneDirectorType;
            universalGameManagerType = _universalGameManagerType;
        }

        if (managerType == null
            || schedulerType == null
            || sceneDirectorType == null
            || universalGameManagerType == null)
        {
            return false;
        }

        if (!TryResolveCurrentDaySceneManager(managerType, out var manager, out var managerPointer)) return false;

        var swappingValue = RuntimeReflectionUtility.GetMemberValue(manager, "IsMapSwapping");
        if (swappingValue is not bool isMapSwapping) return false;
        var pendingEventValue = RuntimeReflectionUtility.GetMemberValue(manager, "m_HasTriggerOnEnterDaySceneEvent");
        if (pendingEventValue is not bool hasPendingEnterDayEvent) return false;
        var schedulerExecutingValue = RuntimeReflectionUtility.GetStaticMemberValue(schedulerType, "isExecuting");
        if (schedulerExecutingValue is not bool runTimeSchedulerIsExecuting) return false;
        var sceneDirectorInEventValue = RuntimeReflectionUtility.GetStaticMemberValue(sceneDirectorType, "IsInEvent");
        if (sceneDirectorInEventValue is not bool sceneDirectorIsInEvent) return false;
        var scheduledActionsExecutingValue = RuntimeReflectionUtility.GetMemberValue(manager, "isExecutingScheduledActions");
        if (scheduledActionsExecutingValue is not bool daySceneManagerIsExecutingScheduledActions) return false;
        var switchingSceneValue = RuntimeReflectionUtility.GetStaticMemberValue(universalGameManagerType, "IsSwitchScene");
        if (switchingSceneValue is not bool universalGameManagerIsSwitchScene) return false;
        var mapLabel = RuntimeReflectionUtility.GetMemberValue(manager, "CurrentActiveMapLabel") as string;
        lock (SyncRoot)
        {
            return DaySceneState.CanRead(
                managerPointer,
                isMapSwapping,
                hasPendingEnterDayEvent,
                !string.IsNullOrWhiteSpace(mapLabel),
                runTimeSchedulerIsExecuting,
                sceneDirectorIsInEvent,
                daySceneManagerIsExecutingScheduledActions,
                universalGameManagerIsSwitchScene);
        }
    }

    private static void PatchMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        int parameterCount,
        string? prefixName,
        string? postfixName,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/{parameterCount}";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        var type = RuntimeReflectionUtility.FindType(typeName);
        var target = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == parameterCount);
        var prefix = string.IsNullOrWhiteSpace(prefixName)
            ? null
            : typeof(RuntimeSceneReadinessCapture).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
        var postfix = string.IsNullOrWhiteSpace(postfixName)
            ? null
            : typeof(RuntimeSceneReadinessCapture).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null
            || (!string.IsNullOrWhiteSpace(prefixName) && prefix == null)
            || (!string.IsNullOrWhiteSpace(postfixName) && postfix == null))
        {
            missing.Add(key);
            return;
        }

        harmony.Patch(
            target,
            prefix: prefix == null ? null : new HarmonyMethod(prefix),
            postfix: postfix == null ? null : new HarmonyMethod(postfix));
        lock (SyncRoot)
        {
            PatchedMethods.Add(key);
        }

        patchedNow.Add(key);
    }

    private static void OnDayScenePanelOpened()
    {
        lock (SyncRoot)
        {
            UpdateDayStateLocked(DaySceneState.OpenPanel, "DaySceneSustainedPannel.OnPannelPostOpen");
            SetPrepReadyLocked(false, "DaySceneSustainedPannel.OnPannelPostOpen");
        }
    }

    private static void OnFirstEnterDayScenePrefix(object __instance)
    {
        if (!TryReadNativePointer(__instance, out var managerPointer)) managerPointer = 0;
        var manualWorkValue = _nightSceneDirectorType == null
            ? null
            : RuntimeReflectionUtility.GetStaticMemberValue(_nightSceneDirectorType, "IsManualWorkSceneSession");
        var manualWorkReturn = manualWorkValue is bool isManualWorkSession && isManualWorkSession;
        lock (SyncRoot)
        {
            UpdateDayStateLocked(
                () => DaySceneState.BeginFirstEnter(managerPointer, manualWorkReturn),
                "DayScene.SceneManager.OnFirstEnterDaySceneFinish begin");
        }
    }

    private static void OnFirstEnterDayScenePostfix()
    {
        lock (SyncRoot)
        {
            UpdateDayStateLocked(
                DaySceneState.EndFirstEnter,
                "DayScene.SceneManager.OnFirstEnterDaySceneFinish returned");
        }
    }

    private static void OnEnterDayScenePrefix(object[] __args)
    {
        if (!TryReadArgumentPointer(__args, 0, out var actionPointer)) return;
        lock (SyncRoot)
        {
            UpdateDayStateLocked(
                () => DaySceneState.CaptureEnterDayAction(actionPointer),
                "RunTimeScheduler.OnEnterDayScene");
        }
    }

    private static void OnEnterDaySceneMapPrefix(object[] __args)
    {
        if (!TryReadArgumentPointer(__args, 1, out var actionPointer)) return;
        Type? managerType;
        lock (SyncRoot)
        {
            managerType = _daySceneManagerType;
        }

        if (managerType == null
            || !TryResolveCurrentDaySceneManager(managerType, out var manager, out var managerPointer))
        {
            return;
        }

        var pendingEventValue = RuntimeReflectionUtility.GetMemberValue(manager, "m_HasTriggerOnEnterDaySceneEvent");
        if (pendingEventValue is not bool hasPendingEnterDayEvent) return;
        var mapLabel = __args.Length > 0 ? __args[0] as string : null;
        lock (SyncRoot)
        {
            UpdateDayStateLocked(
                () => DaySceneState.CaptureEnterDayMapAction(
                    managerPointer,
                    actionPointer,
                    hasPendingEnterDayEvent,
                    mapLabel),
                "RunTimeScheduler.OnEnterDaySceneMap");
        }
    }

    private static void OnSchedulerFinishPrefix(object[] __args, out RuntimeDaySceneFinishToken __state)
    {
        __state = default;
        if (!TryReadArgumentPointer(__args, 0, out var actionPointer)) return;
        lock (SyncRoot)
        {
            var version = DaySceneState.Version;
            __state = DaySceneState.BeginSchedulerFinish(actionPointer);
            if (DaySceneState.Version != version)
            {
                _changeVersion++;
                _lastEvent = "RunTimeScheduler.DefaultOnFinish begin";
            }
        }
    }

    private static void OnSchedulerFinishPostfix(RuntimeDaySceneFinishToken __state)
    {
        if (__state.Kind == RuntimeDaySceneFinishKind.None) return;
        lock (SyncRoot)
        {
            UpdateDayStateLocked(
                () => DaySceneState.CompleteSchedulerFinish(__state),
                "RunTimeScheduler.DefaultOnFinish completed");
        }
    }

    private static void OnDaySceneDestroyed()
    {
        lock (SyncRoot)
        {
            UpdateDayStateLocked(
                () => DaySceneState.Reset("DaySceneSustainedPannel.OnPrePanelDestroyed"),
                "DaySceneSustainedPannel.OnPrePanelDestroyed");
        }
    }

    private static void OnIzakayaPrepReady()
    {
        lock (SyncRoot)
        {
            UpdateDayStateLocked(
                () => DaySceneState.Reset("IzakayaConfigPannel.OnPanelOpen"),
                "IzakayaConfigPannel.OnPanelOpen");
            SetPrepReadyLocked(true, "IzakayaConfigPannel.OnPanelOpen");
        }
    }

    private static void OnIzakayaPrepSpecificReady()
    {
        lock (SyncRoot)
        {
            UpdateDayStateLocked(
                () => DaySceneState.Reset("IzakayaConfigPannel.GoToSpecific"),
                "IzakayaConfigPannel.GoToSpecific");
            SetPrepReadyLocked(true, "IzakayaConfigPannel.GoToSpecific");
        }
    }

    private static void OnIzakayaPrepClosed()
    {
        lock (SyncRoot)
        {
            SetPrepReadyLocked(false, "IzakayaConfigPannel closed");
        }
    }

    private static void UpdateDayStateLocked(Action update, string reason)
    {
        var version = DaySceneState.Version;
        update();
        if (DaySceneState.Version == version) return;

        _lastEvent = reason;
        _changeVersion++;
    }

    private static void SetPrepReadyLocked(bool ready, string reason)
    {
        if (_izakayaPrepReady == ready) return;

        _izakayaPrepReady = ready;
        _lastEvent = reason;
        _changeVersion++;
    }

    private static bool TryReadArgumentPointer(object[] args, int index, out nint pointer)
    {
        pointer = 0;
        return index >= 0
            && index < args.Length
            && TryReadNativePointer(args[index], out pointer);
    }

    private static bool TryReadNativePointer(object? value, out nint pointer)
    {
        pointer = 0;
        if (value is not Il2CppObjectBase il2CppObject || il2CppObject.Pointer == IntPtr.Zero)
        {
            return false;
        }

        pointer = il2CppObject.Pointer;
        return true;
    }

    private static bool TryResolveCurrentDaySceneManager(
        Type managerType,
        out object manager,
        out nint managerPointer)
    {
        manager = null!;
        managerPointer = 0;
        var current = RuntimeReflectionUtility.GetStaticMemberValue(managerType, "Instance");
        if (!TryReadNativePointer(current, out managerPointer) || current == null) return false;

        manager = current;
        return true;
    }

}
