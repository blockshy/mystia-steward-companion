using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeSceneReadinessCapture
{
    private const string DaySceneSustainedPanelTypeName = "DayScene.UI.DaySceneSustainedPannel";
    private const string IzakayaConfigPanelTypeName = "PrepNightScene.UI.IzakayaConfigPannel";

    private static readonly object SyncRoot = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);

    private static Harmony? _harmony;
    private static bool _daySceneReady;
    private static bool _izakayaPrepReady;
    private static long _changeVersion;
    private static string _status = "not attached";
    private static string _lastEvent = "";

    public static bool DaySceneReady
    {
        get
        {
            lock (SyncRoot)
            {
                return _daySceneReady;
            }
        }
    }

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

    public static string Status
    {
        get
        {
            lock (SyncRoot)
            {
                return $"{_status}; day={(_daySceneReady ? "ready" : "waiting")}; prep={(_izakayaPrepReady ? "ready" : "waiting")}; last={_lastEvent}";
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

            PatchMethod(_harmony, DaySceneSustainedPanelTypeName, "OnPannelPostOpen", 0, nameof(OnDaySceneReady), patchedNow, missing);
            PatchMethod(_harmony, DaySceneSustainedPanelTypeName, "OnPanelClosed", 0, nameof(OnDaySceneClosed), patchedNow, missing);
            PatchMethod(_harmony, DaySceneSustainedPanelTypeName, "OnPrePanelDestroyed", 0, nameof(OnDaySceneDestroyed), patchedNow, missing);
            PatchMethod(_harmony, IzakayaConfigPanelTypeName, "OnPanelOpen", 1, nameof(OnIzakayaPrepReady), patchedNow, missing);
            PatchMethod(_harmony, IzakayaConfigPanelTypeName, "GoToSpecific", 1, nameof(OnIzakayaPrepSpecificReady), patchedNow, missing);
            PatchMethod(_harmony, IzakayaConfigPanelTypeName, "Cleanup_Generated", 0, nameof(OnIzakayaPrepClosed), patchedNow, missing);
            PatchMethod(_harmony, IzakayaConfigPanelTypeName, "GotoWork", 0, nameof(OnIzakayaPrepClosed), patchedNow, missing);

            lock (SyncRoot)
            {
                _status = PatchedMethods.Count == 0
                    ? $"waiting: {string.Join(", ", missing.Take(4))}"
                    : missing.Count == 0
                        ? $"patched={PatchedMethods.Count}"
                        : $"patched={PatchedMethods.Count}; missing={string.Join(", ", missing.Take(4))}";
            }

            if (patchedNow.Count > 0)
            {
                log.LogInfo($"Runtime scene readiness patched: {string.Join(", ", patchedNow)}.");
            }
            else if (PatchedMethods.Count == 0)
            {
                log.LogWarning($"Runtime scene readiness waiting for game types: {string.Join(", ", missing.Take(4))}.");
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
            SetReadyLocked(dayReady: false, prepReady: false, reason);
        }
    }

    private static void PatchMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        int parameterCount,
        string postfixName,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/{parameterCount}";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        var type = FindType(typeName);
        var target = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == parameterCount);
        var postfix = typeof(RuntimeSceneReadinessCapture).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || postfix == null)
        {
            missing.Add(key);
            return;
        }

        harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        lock (SyncRoot)
        {
            PatchedMethods.Add(key);
        }

        patchedNow.Add(key);
    }

    private static void OnDaySceneReady()
    {
        lock (SyncRoot)
        {
            SetReadyLocked(dayReady: true, prepReady: false, "DaySceneSustainedPannel.OnPannelPostOpen");
        }
    }

    private static void OnDaySceneClosed()
    {
        lock (SyncRoot)
        {
            SetReadyLocked(dayReady: false, prepReady: _izakayaPrepReady, "DaySceneSustainedPannel.OnPanelClosed");
        }
    }

    private static void OnDaySceneDestroyed()
    {
        lock (SyncRoot)
        {
            SetReadyLocked(dayReady: false, prepReady: _izakayaPrepReady, "DaySceneSustainedPannel.OnPrePanelDestroyed");
        }
    }

    private static void OnIzakayaPrepReady()
    {
        lock (SyncRoot)
        {
            SetReadyLocked(dayReady: false, prepReady: true, "IzakayaConfigPannel.OnPanelOpen");
        }
    }

    private static void OnIzakayaPrepSpecificReady()
    {
        lock (SyncRoot)
        {
            SetReadyLocked(dayReady: false, prepReady: true, "IzakayaConfigPannel.GoToSpecific");
        }
    }

    private static void OnIzakayaPrepClosed()
    {
        lock (SyncRoot)
        {
            SetReadyLocked(dayReady: _daySceneReady, prepReady: false, "IzakayaConfigPannel closed");
        }
    }

    private static void SetReadyLocked(bool dayReady, bool prepReady, string reason)
    {
        if (_daySceneReady == dayReady
            && _izakayaPrepReady == prepReady
            && string.Equals(_lastEvent, reason, StringComparison.Ordinal))
        {
            return;
        }

        _daySceneReady = dayReady;
        _izakayaPrepReady = prepReady;
        _lastEvent = reason;
        _changeVersion++;
    }

    private static Type? FindType(string fullName)
    {
        var direct = Type.GetType(fullName, false);
        if (direct != null) return direct;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            catch
            {
                // Ignore assemblies that cannot resolve unrelated IL2CPP types.
            }
        }

        return null;
    }
}
