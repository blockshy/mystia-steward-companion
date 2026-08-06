using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// Tracks the exact lifetime of the game's night-business runtime before Unity changes scenes.
/// </summary>
internal static class RuntimeNightBusinessLifecycle
{
    private const string SceneManagerTypeName = "NightScene.SceneManager";
    private const string WorkScenePanelTypeName = "NightScene.UI.WorkSceneSustainedPannel";
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const int ExpectedHookCount = 5;

    private static readonly object PatchRoot = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly NightBusinessLifecycleTracker Tracker = new();
    private static NightBusinessLifecycleSnapshot _publishedSnapshot = Tracker.Snapshot;

    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static string _hookStatus = "not attached";

    public static NightBusinessLifecycleSnapshot Snapshot => Volatile.Read(ref _publishedSnapshot);

    public static bool IsActive => Snapshot.IsActive;

    public static long Generation => Snapshot.Generation;

    public static string Status
    {
        get
        {
            var snapshot = Snapshot;
            lock (PatchRoot)
            {
                return $"hooks={_hookStatus}; phase={snapshot.Phase}; generation={snapshot.Generation}; version={snapshot.Version}; source={snapshot.Source}; thread={snapshot.ThreadId}";
            }
        }
    }

    public static void Attach(ManualLogSource log)
    {
        _log = log;
        var patchedNow = new List<string>();
        var missing = new List<string>();
        var failed = new List<string>();
        try
        {
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.night-business-lifecycle");
            TryPatchMethod(_harmony, WorkScenePanelTypeName, "OnPannelPostOpen", 0, nameof(OnBusinessStarted), postfix: true, patchedNow, missing, failed);
            // TryCloseIzakaya only stops arrivals and drains seated guests; runtime objects remain serviceable.
            TryPatchMethod(_harmony, GuestsManagerTypeName, "CloseIzakayaDelayed", 2, nameof(OnDelayedBusinessClosing), postfix: false, patchedNow, missing, failed);
            TryPatchMethod(_harmony, GuestsManagerTypeName, "CloseIzakayaAndLeaveChallengeMode", 2, nameof(OnChallengeBusinessClosing), postfix: false, patchedNow, missing, failed);
            TryPatchMethod(_harmony, SceneManagerTypeName, "ToResult", 0, nameof(OnResultTransitionStarting), postfix: false, patchedNow, missing, failed);
            TryPatchMethod(_harmony, SceneManagerTypeName, "OnInstanceDestroyed", 0, nameof(OnBusinessDestroyed), postfix: false, patchedNow, missing, failed);

            lock (PatchRoot)
            {
                _hookStatus = PatchedMethods.Count == ExpectedHookCount
                    ? "patched"
                    : $"partial:{PatchedMethods.Count}/{ExpectedHookCount}";
            }

            if (patchedNow.Count > 0)
            {
                TryLogInfo($"Night-business lifecycle patched: {string.Join(", ", patchedNow)}.");
            }
            if (missing.Count > 0)
            {
                TryLogWarning($"Night-business lifecycle unavailable; game members were not found: {string.Join(", ", missing)}.");
            }
            if (failed.Count > 0)
            {
                TryLogWarning($"Night-business lifecycle hook installation failed: {string.Join(" | ", failed)}.");
            }
        }
        catch (Exception ex)
        {
            lock (PatchRoot) _hookStatus = $"error:{ex.GetBaseException().Message}";
            TryLogWarning($"Night-business lifecycle attach failed: {ex.GetBaseException().Message}");
        }
    }

    private static void TryPatchMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        int parameterCount,
        string hookName,
        bool postfix,
        ICollection<string> patchedNow,
        ICollection<string> missing,
        ICollection<string> failed)
    {
        try
        {
            PatchMethod(harmony, typeName, methodName, parameterCount, hookName, postfix, patchedNow, missing);
        }
        catch (Exception ex)
        {
            failed.Add($"{typeName}.{methodName}/{parameterCount}: {ex.GetBaseException().Message}");
        }
    }

    private static void PatchMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        int parameterCount,
        string hookName,
        bool postfix,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/{parameterCount}";
        lock (PatchRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        var type = RuntimeReflectionUtility.FindType(typeName);
        var target = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SingleOrDefault(method => method.Name == methodName && method.GetParameters().Length == parameterCount);
        var hook = typeof(RuntimeNightBusinessLifecycle).GetMethod(hookName, BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || hook == null)
        {
            missing.Add(key);
            return;
        }

        var harmonyHook = new HarmonyMethod(hook)
        {
            priority = postfix ? Priority.Last : Priority.First,
        };
        harmony.Patch(
            target,
            prefix: postfix ? null : harmonyHook,
            postfix: postfix ? harmonyHook : null);
        lock (PatchRoot) PatchedMethods.Add(key);
        patchedNow.Add(key);
    }

    private static void OnBusinessStarted()
    {
        lock (PatchRoot)
        {
            if (!string.Equals(_hookStatus, "patched", StringComparison.Ordinal)) return;
        }

        TryPublishTransition(
            static (NightBusinessLifecycleTracker tracker, DateTime now, int threadId, out NightBusinessLifecycleSnapshot snapshot) =>
                tracker.TryActivate("NightScene.UI.WorkSceneSustainedPannel.OnPannelPostOpen", now, threadId, out snapshot));
    }

    private static void OnDelayedBusinessClosing()
    {
        BeginClosing("GuestsManager.CloseIzakayaDelayed");
    }

    private static void OnChallengeBusinessClosing()
    {
        BeginClosing("GuestsManager.CloseIzakayaAndLeaveChallengeMode");
    }

    private static void OnResultTransitionStarting()
    {
        BeginClosing("NightScene.SceneManager.ToResult");
    }

    private static void OnBusinessDestroyed()
    {
        TryPublishTransition(
            static (NightBusinessLifecycleTracker tracker, DateTime now, int threadId, out NightBusinessLifecycleSnapshot snapshot) =>
                tracker.TryMarkDestroyed("NightScene.SceneManager.OnInstanceDestroyed", now, threadId, out snapshot));
    }

    private static void BeginClosing(string source)
    {
        TryPublishTransition(
            (NightBusinessLifecycleTracker tracker, DateTime now, int threadId, out NightBusinessLifecycleSnapshot snapshot) =>
                tracker.TryBeginClosing(source, now, threadId, out snapshot));
    }

    private static void TryPublishTransition(TransitionFactory transitionFactory)
    {
        try
        {
            if (!transitionFactory(Tracker, DateTime.UtcNow, Environment.CurrentManagedThreadId, out var snapshot)) return;

            Volatile.Write(ref _publishedSnapshot, snapshot);
            TryLogInfo($"Night-business lifecycle entering: phase={snapshot.Phase}; generation={snapshot.Generation}; source={snapshot.Source}; thread={snapshot.ThreadId}.");
            ApplyRuntimeBoundary(snapshot);
            TryLogInfo($"Night-business lifecycle boundary completed: phase={snapshot.Phase}; generation={snapshot.Generation}; source={snapshot.Source}.");
        }
        catch (Exception ex)
        {
            TryLogWarning($"Night-business lifecycle callback failed without affecting the game method: {ex.GetBaseException().Message}");
        }
    }

    private static void ApplyRuntimeBoundary(NightBusinessLifecycleSnapshot snapshot)
    {
        var reason = $"night-business-{snapshot.Phase.ToString().ToLowerInvariant()}:{snapshot.Source}";
        RunBoundaryAction(
            "update ServeInWork mission diagnostics",
            () => RuntimeServeInWorkMissionDiagnosticCapture.ApplyBusinessBoundary(
                snapshot,
                DateTime.UtcNow));
        if (snapshot.Phase == NightBusinessLifecyclePhase.Active)
        {
            RunBoundaryAction("resume cooker highlight", () => RuntimeCookerHighlightService.Resume(reason));
            RunBoundaryAction("resume seat highlight", () => RuntimeSeatHighlightService.Resume(reason));
            RunBoundaryAction("resume order highlight", () => RuntimeOrderHighlightService.Resume(reason));
            RunBoundaryAction("resume throw-delivery order highlight", () => RuntimeThrowDeliverOrderHighlightService.Resume(reason));
            RunBoundaryAction("resume pinned-list highlight", () => RuntimePinnedListHighlightService.Resume(reason));
            return;
        }

        if (snapshot.Phase == NightBusinessLifecyclePhase.Closing)
        {
            RunBoundaryAction("suspend cooker highlight", () => RuntimeCookerHighlightService.Suspend(reason));
            RunBoundaryAction("suspend seat highlight", () => RuntimeSeatHighlightService.Suspend(reason));
            RunBoundaryAction("suspend order highlight", () => RuntimeOrderHighlightService.Suspend(reason));
            RunBoundaryAction("suspend throw-delivery order highlight", () => RuntimeThrowDeliverOrderHighlightService.Suspend(reason));
            RunBoundaryAction("suspend pinned-list highlight", () => RuntimePinnedListHighlightService.Suspend(reason));
        }
        else if (snapshot.Phase == NightBusinessLifecyclePhase.Destroyed)
        {
            RunBoundaryAction("abandon cooker highlight", () => RuntimeCookerHighlightService.Abandon(reason));
            RunBoundaryAction("abandon seat highlight", () => RuntimeSeatHighlightService.Abandon(reason));
            RunBoundaryAction("abandon order highlight", () => RuntimeOrderHighlightService.Abandon(reason));
            RunBoundaryAction("abandon throw-delivery order highlight", () => RuntimeThrowDeliverOrderHighlightService.Abandon(reason));
            RunBoundaryAction("abandon pinned-list highlight", () => RuntimePinnedListHighlightService.Abandon(reason));
        }

        RunBoundaryAction("invalidate UI target", () => RuntimeUiPinningService.InvalidateTarget(snapshot.Generation, reason));
        RunBoundaryAction(
            "retire automation safety barriers",
            () => RuntimeOrderPreparationService.ClearAutomationSafetyBarriersForBusinessGeneration(snapshot.Generation));
        RunBoundaryAction("clear runtime order terminal receipts", RuntimeOrderTerminalReceiptStore.Clear);
        RunBoundaryAction("clear special orders", () => SpecialOrderRuntimeCapture.ClearOrders(reason));
        RunBoundaryAction("clear normal orders", () => NormalOrderRuntimeCapture.ClearOrders(reason));
        RunBoundaryAction("clear special-business context", () => RuntimeSpecialBusinessContextService.ClearForBusinessEnd(reason));
        RunBoundaryAction("clear cooking generations", RuntimeCookingGenerationTracker.ClearForSceneChange);
        RunBoundaryAction(
            "clear automation cooking jobs",
            () => RuntimeOrderPreparationService.ClearAutomationCookingJobs(
                "business-lifecycle-ended",
                AutomationCancellationTarget.All,
                preserveIrreversibleTransactions: false));
    }

    private static void RunBoundaryAction(string label, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            TryLogWarning($"Night-business lifecycle could not {label}: {ex.GetBaseException().Message}");
        }
    }

    private static void TryLogInfo(string message)
    {
        try
        {
            _log?.LogInfo(message);
        }
        catch
        {
            // Logging must not affect Harmony callbacks or runtime boundaries.
        }
    }

    private static void TryLogWarning(string message)
    {
        try
        {
            _log?.LogWarning(message);
        }
        catch
        {
            // Logging must not affect Harmony callbacks or runtime boundaries.
        }
    }

    private delegate bool TransitionFactory(
        NightBusinessLifecycleTracker tracker,
        DateTime changedAtUtc,
        int threadId,
        out NightBusinessLifecycleSnapshot snapshot);
}
