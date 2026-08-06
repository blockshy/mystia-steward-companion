using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeUiPinningService
{
    private const string CookingSelectionPanelTypeName = "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel";
    private const string StoragePanelTypeName = "NightScene.UI.CookingUtility.WorkSceneStoragePannel";
    private const string RunTimePlayerDataTypeName = "GameData.RunTime.Common.RunTimePlayerData";
    private const int MaxPanelRefreshWarningLogs = 4;

    private static readonly object SyncRoot = new();
    private static readonly object TargetPublicationRoot = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);

    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static bool _hudOrderHighlightTargetSynchronized = true;
    private static bool _throwDeliveryOrderHighlightTargetSynchronized = true;
    private static string _checkPinnedPatchStatus = "not attached";
    private static string _cookingScopePatchStatus = "not attached";
    private static string _beverageScopePatchStatus = "not attached";
    private static string _cookingPanelPatchStatus = "not attached";
    private static string _storagePanelPatchStatus = "not attached";
    private static RuntimeUiTargetSetSnapshot _targetSet = RuntimeUiTargetSetSnapshot.Disabled;
    private static MethodInfo? _cookingRefreshMethod;
    private static MethodInfo? _storageRefreshMethod;
    private static PanelRefreshRegistration? _cookingPanel;
    private static PanelRefreshRegistration? _storagePanel;
    private static bool _panelRefreshTickActive;
    private static long _panelRefreshAttempts;
    private static long _panelRefreshSuccesses;
    private static long _panelRefreshFailures;
    private static long _panelRefreshStalePanels;
    private static int _panelRefreshWarningLogs;
    private static string _lastPanelRefreshError = "";
    private static long _recipeForces;
    private static long _ingredientForces;
    private static long _beverageForces;
    private static long _scopeCleanupImbalances;

    [ThreadStatic]
    private static int _cookingRefreshDepth;

    [ThreadStatic]
    private static RuntimeUiTargetSetSnapshot? _cookingScopeTarget;

    [ThreadStatic]
    private static object? _cookingScopeInstance;

    [ThreadStatic]
    private static bool _cookingScopeFailed;

    [ThreadStatic]
    private static object? _recentCookingRefreshInstance;

    [ThreadStatic]
    private static RuntimeUiTargetSetSnapshot? _recentCookingRefreshTarget;

    [ThreadStatic]
    private static int _beverageRefreshDepth;

    [ThreadStatic]
    private static RuntimeUiTargetSetSnapshot? _beverageScopeTarget;

    [ThreadStatic]
    private static object? _beverageScopeInstance;

    [ThreadStatic]
    private static bool _beverageScopeFailed;

    [ThreadStatic]
    private static object? _recentBeverageRefreshInstance;

    [ThreadStatic]
    private static RuntimeUiTargetSetSnapshot? _recentBeverageRefreshTarget;

    public static string Status
    {
        get
        {
            var targetSet = Volatile.Read(ref _targetSet);
            string coreStatus;
            lock (SyncRoot)
            {
                coreStatus = $"patches=checkPinnedPrefix:{_checkPinnedPatchStatus}, cookingScope:{_cookingScopePatchStatus}, beverageScope:{_beverageScopePatchStatus}, cookingPanel:{_cookingPanelPatchStatus}, storagePanel:{_storagePanelPatchStatus}; targetSet={DescribeTargetSet(targetSet)}; orderSurfaces=hud:{(_hudOrderHighlightTargetSynchronized ? "synchronized" : "retry-pending")},throwDelivery:{(_throwDeliveryOrderHighlightTargetSynchronized ? "synchronized" : "retry-pending")}; panelRefresh=cooking:{DescribePanelLocked(_cookingPanel)}, storage:{DescribePanelLocked(_storagePanel)}, attempts:{_panelRefreshAttempts}, successes:{_panelRefreshSuccesses}, failures:{_panelRefreshFailures}, stale:{_panelRefreshStalePanels}, warningLogs:{_panelRefreshWarningLogs}/{MaxPanelRefreshWarningLogs}, lastError:{_lastPanelRefreshError}";
            }

            return $"{coreStatus}; highlight={RuntimeCookerHighlightService.Status}; seat={RuntimeSeatHighlightService.Status}; order={RuntimeOrderHighlightService.Status}; throwDeliveryOrder={RuntimeThrowDeliverOrderHighlightService.Status}; extras={RuntimePinnedRecipeExtrasService.Status}; listHighlight={RuntimePinnedListHighlightService.Status}; forcedTotal=recipe:{Interlocked.Read(ref _recipeForces)}, ingredients:{Interlocked.Read(ref _ingredientForces)}, beverage:{Interlocked.Read(ref _beverageForces)}; scopeImbalance={Interlocked.Read(ref _scopeCleanupImbalances)}";
        }
    }

    public static void Attach(ManualLogSource log)
    {
        _log = log;
        RuntimePinnedRecipeExtrasService.Attach(log);
        try
        {
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.runtime-ui-pinning");
            var patchedNow = new List<string>();
            var missing = new List<string>();

            PatchScopeMethod(
                _harmony,
                CookingSelectionPanelTypeName,
                "UpdateAllVisual",
                0,
                nameof(OnCookingRefreshStarted),
                nameof(OnCookingRefreshFinalized),
                PatchSlot.CookingScope,
                patchedNow,
                missing);
            PatchPrefixMethod(
                _harmony,
                RunTimePlayerDataTypeName,
                "CheckPinned",
                2,
                nameof(OnCheckPinned),
                PatchSlot.CheckPinned,
                patchedNow,
                missing);
            PatchScopeMethod(
                _harmony,
                StoragePanelTypeName,
                "UpdateBevField",
                0,
                nameof(OnBeverageRefreshStarted),
                nameof(OnBeverageRefreshFinalized),
                PatchSlot.BeverageScope,
                patchedNow,
                missing);
            PatchPanelLifecycle(
                _harmony,
                CookingSelectionPanelTypeName,
                nameof(BeforeCookingPanelOpen),
                nameof(AfterCookingPanelOpen),
                nameof(BeforeCookingPanelTeardown),
                PatchSlot.CookingPanel,
                patchedNow,
                missing);
            PatchPanelLifecycle(
                _harmony,
                StoragePanelTypeName,
                nameof(BeforeStoragePanelOpen),
                nameof(AfterStoragePanelOpen),
                nameof(BeforeStoragePanelTeardown),
                PatchSlot.StoragePanel,
                patchedNow,
                missing);

            if (patchedNow.Count > 0)
            {
                log.LogInfo($"Runtime UI pinning patched: {string.Join(", ", patchedNow)}.");
            }
            if (missing.Count > 0)
            {
                log.LogWarning($"Runtime UI pinning unavailable; game members were not found: {string.Join(", ", missing.Take(3))}.");
            }
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                var status = $"error:{ex.GetBaseException().Message}";
                _checkPinnedPatchStatus = status;
                _cookingScopePatchStatus = status;
                _beverageScopePatchStatus = status;
                _cookingPanelPatchStatus = status;
                _storagePanelPatchStatus = status;
            }

            log.LogWarning($"Runtime UI pinning attach failed: {ex.Message}");
        }
    }

    public static string UpdateTargets(
        long sessionGeneration,
        IReadOnlyList<RuntimeUiTargetSnapshot> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ValidateSession(sessionGeneration);

        lock (TargetPublicationRoot)
        {
            ValidateSession(sessionGeneration);
            return PublishTargets(sessionGeneration, targets);
        }
    }

    internal static RuntimeUiTargetSetSnapshot ReadTargetSet()
    {
        var target = Volatile.Read(ref _targetSet);
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        return lifecycle.IsActive && target.SessionGeneration == lifecycle.Generation
            ? target
            : RuntimeUiTargetSetSnapshot.Disabled;
    }

    internal static bool TryExecutePinnedRecipeExtrasTransaction(
        RuntimeUiTargetSetSnapshot capturedTargetSet,
        int recipeId,
        IReadOnlyList<int> extraIngredientIds,
        Action transaction)
    {
        ArgumentNullException.ThrowIfNull(capturedTargetSet);
        ArgumentNullException.ThrowIfNull(extraIngredientIds);
        ArgumentNullException.ThrowIfNull(transaction);

        lock (TargetPublicationRoot)
        {
            var currentTargetSet = Volatile.Read(ref _targetSet);
            var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
            if (!lifecycle.IsActive
                || lifecycle.Generation != capturedTargetSet.SessionGeneration
                || !ReferenceEquals(currentTargetSet, capturedTargetSet)
                || currentTargetSet.Generation != capturedTargetSet.Generation
                || currentTargetSet.SessionGeneration != capturedTargetSet.SessionGeneration
                || currentTargetSet.ResolveRecipeExtras(
                    recipeId,
                    out var currentExtraIngredientIds) != RuntimeUiRecipeExtrasResolution.Resolved
                || !currentExtraIngredientIds.SequenceEqual(extraIngredientIds))
            {
                return false;
            }

            transaction();
            return true;
        }
    }

    /// <summary>
    /// Applies a newly published target to panels that were already open when that target arrived.
    /// This method must only run from the Unity main-thread LateUpdate entry.
    /// </summary>
    public static void Tick()
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycle.IsActive) return;

        var target = Volatile.Read(ref _targetSet);
        if (target.SessionGeneration != lifecycle.Generation) return;

        lock (SyncRoot)
        {
            if (_panelRefreshTickActive) return;
            _panelRefreshTickActive = true;
        }

        try
        {
            TryRefreshOpenPanel(RefreshPanelKind.Cooking, lifecycle, target);
            TryRefreshOpenPanel(RefreshPanelKind.Storage, lifecycle, target);
        }
        finally
        {
            lock (SyncRoot) _panelRefreshTickActive = false;
        }
    }

    public static void Abandon(string reason)
    {
        lock (SyncRoot)
        {
            ClearPanelRegistrationsLocked();
            _lastPanelRefreshError = string.IsNullOrWhiteSpace(reason) ? "runtime unavailable" : reason.Trim();
        }
    }

    internal static void InvalidateTarget(long sessionGeneration, string reason)
    {
        lock (TargetPublicationRoot)
        {
            var current = Volatile.Read(ref _targetSet);
            var disabled = new RuntimeUiTargetSetSnapshot(
                checked(current.Generation + 1),
                sessionGeneration,
                Array.Empty<RuntimeUiTargetSnapshot>());
            Volatile.Write(ref _targetSet, disabled);

            RunCleanupNoThrow(() => Abandon(reason));
            RunCleanupNoThrow(() => RuntimePinnedRecipeExtrasService.Abandon(reason));
            RunCleanupNoThrow(() => RuntimeCookerHighlightService.UpdateTargets(disabled));
            RunCleanupNoThrow(() => RuntimeSeatHighlightService.UpdateTargets(disabled));
            var hudOrderSynchronized = TryUpdateOrderHighlightSurface(
                "HUD",
                () => RuntimeOrderHighlightService.UpdateTargets(disabled));
            var throwDeliveryOrderSynchronized = TryUpdateOrderHighlightSurface(
                "throw-delivery panel",
                () => RuntimeThrowDeliverOrderHighlightService.UpdateTargets(disabled));
            lock (SyncRoot)
            {
                _hudOrderHighlightTargetSynchronized = hudOrderSynchronized;
                _throwDeliveryOrderHighlightTargetSynchronized = throwDeliveryOrderSynchronized;
            }
            TryLogInfo($"Runtime UI target invalidated: generation={sessionGeneration}; reason={reason}.");
        }
    }

    private static string PublishTargets(
        long sessionGeneration,
        IReadOnlyList<RuntimeUiTargetSnapshot> targets)
    {
        var orderedTargets = targets.OrderBy(target => target.Kind).ToArray();
        var current = Volatile.Read(ref _targetSet);
        var targetMatches = current.HasSameValues(sessionGeneration, orderedTargets);
        bool updateHudOrderSurface;
        bool updateThrowDeliveryOrderSurface;
        lock (SyncRoot)
        {
            if (targetMatches
                && _hudOrderHighlightTargetSynchronized
                && _throwDeliveryOrderHighlightTargetSynchronized)
            {
                return Status;
            }
            updateHudOrderSurface = !targetMatches || !_hudOrderHighlightTargetSynchronized;
            updateThrowDeliveryOrderSurface = !targetMatches || !_throwDeliveryOrderHighlightTargetSynchronized;
        }

        var published = current;
        if (!targetMatches)
        {
            published = new RuntimeUiTargetSetSnapshot(
                checked(current.Generation + 1),
                sessionGeneration,
                orderedTargets);
            Volatile.Write(ref _targetSet, published);
            RuntimeCookerHighlightService.UpdateTargets(published);
            RuntimeSeatHighlightService.UpdateTargets(published);
        }
        var hudOrderSynchronized = !updateHudOrderSurface || TryUpdateOrderHighlightSurface(
            "HUD",
            () => RuntimeOrderHighlightService.UpdateTargets(published));
        var throwDeliveryOrderSynchronized = !updateThrowDeliveryOrderSurface || TryUpdateOrderHighlightSurface(
            "throw-delivery panel",
            () => RuntimeThrowDeliverOrderHighlightService.UpdateTargets(published));

        lock (SyncRoot)
        {
            _hudOrderHighlightTargetSynchronized = hudOrderSynchronized;
            _throwDeliveryOrderHighlightTargetSynchronized = throwDeliveryOrderSynchronized;
        }

        TryLogInfo(
            $"Runtime UI target set updated: {DescribeTargetSet(published)}; orderSurfaces=hud:{(hudOrderSynchronized ? "synchronized" : "retry-pending")},throwDelivery:{(throwDeliveryOrderSynchronized ? "synchronized" : "retry-pending")}.");
        return Status;
    }

    private static void RunCleanupNoThrow(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            TryLogWarning($"Runtime UI target cleanup failed without escaping the lifecycle boundary: {ex.GetBaseException().Message}");
        }
    }

    private static bool TryUpdateOrderHighlightSurface(
        string surface,
        Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            TryLogWarning($"Runtime UI target could not update the {surface} order-highlight surface without affecting the other surface: {ex.GetBaseException().Message}");
            return false;
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
            // Diagnostics must not affect target publication or cleanup.
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
            // Diagnostics must not affect target publication or cleanup.
        }
    }

    private static void ValidateSession(long sessionGeneration)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (lifecycle.IsActive && sessionGeneration > 0 && sessionGeneration == lifecycle.Generation) return;

        throw new InvalidOperationException(
            $"Night-business UI target rejected: requested generation={sessionGeneration}, current generation={lifecycle.Generation}, phase={lifecycle.Phase}.");
    }

    private static string DescribeTargetSet(RuntimeUiTargetSetSnapshot targetSet)
    {
        var slots = targetSet.Targets.Count == 0
            ? "none"
            : string.Join(
                ";",
                targetSet.Targets.Select(target =>
                    $"{target.Kind.ToString().ToLowerInvariant()}:features=list:{FormatFlag(target.ListPinningEnabled)},variant:{FormatFlag(target.RecipeVariantEnabled)},cooker:{FormatFlag(target.CookerHighlightEnabled)},seat:{FormatFlag(target.SeatHighlightEnabled)},order:{FormatFlag(target.OrderHighlightEnabled)},trace={target.OrderTraceId},lifecycle={target.OrderLifecycleSequence},desk={target.DeskCode},recipe={target.RecipeId},beverage={target.BeverageId},cooker={target.CookerTypeId},color={target.Color.ToExactHex()},ingredients={string.Join(",", target.IngredientIds)},extras={string.Join(",", target.ExtraIngredientIds)},revisionLength={target.TargetRevision.Length}"));
        return $"generation={targetSet.Generation}/session:{targetSet.SessionGeneration}; targets={targetSet.Targets.Count}[{slots}]";
    }

    private static string FormatFlag(bool enabled) => enabled ? "on" : "off";

    private static void PatchPrefixMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        int parameterCount,
        string prefixName,
        PatchSlot patchSlot,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/{parameterCount}";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key))
            {
                SetPatchStatusLocked(patchSlot, "patched");
                return;
            }
        }

        try
        {
            var type = RuntimeReflectionUtility.FindType(typeName);
            var target = FindMethod(type, methodName, parameterCount);
            var prefix = typeof(RuntimeUiPinningService).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
            if (target == null || prefix == null)
            {
                lock (SyncRoot)
                {
                    SetPatchStatusLocked(patchSlot, target == null ? "method missing" : "prefix missing");
                }

                missing.Add(key);
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            lock (SyncRoot)
            {
                PatchedMethods.Add(key);
                SetPatchStatusLocked(patchSlot, "patched");
            }

            patchedNow.Add(key);
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                SetPatchStatusLocked(patchSlot, $"error:{ex.GetBaseException().Message}");
            }

            missing.Add($"{key} (patch error)");
        }
    }

    private static void PatchScopeMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        int parameterCount,
        string prefixName,
        string finalizerName,
        PatchSlot patchSlot,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/{parameterCount}:{patchSlot}";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key))
            {
                SetPatchStatusLocked(patchSlot, "patched");
                return;
            }
        }

        try
        {
            var type = RuntimeReflectionUtility.FindType(typeName);
            var target = FindMethod(type, methodName, parameterCount);
            var prefix = typeof(RuntimeUiPinningService).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
            var finalizer = typeof(RuntimeUiPinningService).GetMethod(finalizerName, BindingFlags.NonPublic | BindingFlags.Static);
            if (target == null || prefix == null || finalizer == null)
            {
                lock (SyncRoot)
                {
                    SetPatchStatusLocked(patchSlot, target == null ? "method missing" : "hook missing");
                }

                missing.Add(key);
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(prefix), finalizer: new HarmonyMethod(finalizer));
            lock (SyncRoot)
            {
                PatchedMethods.Add(key);
                SetPatchStatusLocked(patchSlot, "patched");
                SetRefreshMethodLocked(patchSlot, target);
            }

            patchedNow.Add(key);
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                SetPatchStatusLocked(patchSlot, $"error:{ex.GetBaseException().Message}");
            }

            missing.Add($"{key} (patch error)");
        }
    }

    private static void PatchPanelLifecycle(
        Harmony harmony,
        string typeName,
        string openPrefixName,
        string openPostfixName,
        string teardownPrefixName,
        PatchSlot patchSlot,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var installed = 0;
        installed += PatchPanelMethod(
            harmony,
            typeName,
            "OnPanelOpen",
            openPrefixName,
            openPostfixName,
            patchedNow,
            missing)
            ? 1
            : 0;
        installed += PatchPanelMethod(
            harmony,
            typeName,
            "OnPanelClose",
            teardownPrefixName,
            null,
            patchedNow,
            missing)
            ? 1
            : 0;
        installed += PatchPanelMethod(
            harmony,
            typeName,
            "OnPanelDestroyed",
            teardownPrefixName,
            null,
            patchedNow,
            missing)
            ? 1
            : 0;

        lock (SyncRoot)
        {
            SetPatchStatusLocked(patchSlot, installed == 3 ? "patched" : $"partial:{installed}/3");
        }
    }

    private static bool PatchPanelMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        string prefixName,
        string? postfixName,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/0:PanelRefresh";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key)) return true;
        }

        try
        {
            var type = RuntimeReflectionUtility.FindType(typeName);
            var target = FindMethod(type, methodName, 0);
            var prefix = typeof(RuntimeUiPinningService).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
            var postfix = postfixName == null
                ? null
                : typeof(RuntimeUiPinningService).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
            if (target == null || prefix == null || postfixName != null && postfix == null)
            {
                missing.Add(key);
                return false;
            }

            harmony.Patch(
                target,
                prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                postfix: postfix == null ? null : new HarmonyMethod(postfix) { priority = Priority.Last });
            lock (SyncRoot) PatchedMethods.Add(key);
            patchedNow.Add(key);
            return true;
        }
        catch (Exception ex)
        {
            missing.Add($"{key} ({ex.GetBaseException().Message})");
            return false;
        }
    }

    private static MethodInfo? FindMethod(Type? type, string methodName, int parameterCount)
    {
        return type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == parameterCount);
    }

    private static void OnCookingRefreshStarted(object __instance)
    {
        var isOutermostRefresh = _cookingRefreshDepth == 0;
        if (isOutermostRefresh)
        {
            _cookingScopeTarget = Volatile.Read(ref _targetSet);
            _cookingScopeInstance = __instance;
            _cookingScopeFailed = false;
        }

        _cookingRefreshDepth++;
        if (isOutermostRefresh && _cookingScopeTarget != null)
        {
            RuntimePinnedRecipeExtrasService.TryApply(__instance, _cookingScopeTarget);
        }
    }

    private static Exception? OnCookingRefreshFinalized(Exception? __exception)
    {
        if (__exception != null) _cookingScopeFailed = true;
        if (_cookingRefreshDepth > 0)
        {
            _cookingRefreshDepth--;
            if (_cookingRefreshDepth == 0)
            {
                var instance = _cookingScopeInstance;
                var target = _cookingScopeTarget;
                var succeeded = !_cookingScopeFailed && __exception == null;
                if (instance != null && target != null)
                {
                    RuntimePinnedRecipeExtrasService.OnRefreshFinalized(
                        instance,
                        target,
                        succeeded ? null : __exception ?? new InvalidOperationException("Nested cooking refresh failed."));
                }
                _cookingScopeTarget = null;
                _cookingScopeInstance = null;
                _cookingScopeFailed = false;
                if (succeeded && instance != null && target != null)
                {
                    RecordPanelRefreshCompleted(RefreshPanelKind.Cooking, instance, target);
                }
            }
        }
        else
        {
            Interlocked.Increment(ref _scopeCleanupImbalances);
        }

        return __exception;
    }

    private static void OnBeverageRefreshStarted(object __instance)
    {
        if (_beverageRefreshDepth == 0)
        {
            _beverageScopeTarget = Volatile.Read(ref _targetSet);
            _beverageScopeInstance = __instance;
            _beverageScopeFailed = false;
        }

        _beverageRefreshDepth++;
    }

    private static Exception? OnBeverageRefreshFinalized(Exception? __exception)
    {
        if (__exception != null) _beverageScopeFailed = true;
        if (_beverageRefreshDepth > 0)
        {
            _beverageRefreshDepth--;
            if (_beverageRefreshDepth == 0)
            {
                var instance = _beverageScopeInstance;
                var target = _beverageScopeTarget;
                var succeeded = !_beverageScopeFailed && __exception == null;
                _beverageScopeTarget = null;
                _beverageScopeInstance = null;
                _beverageScopeFailed = false;
                if (succeeded && instance != null && target != null)
                {
                    RecordPanelRefreshCompleted(RefreshPanelKind.Storage, instance, target);
                }
            }
        }
        else
        {
            Interlocked.Increment(ref _scopeCleanupImbalances);
        }

        return __exception;
    }

    private static void BeforeCookingPanelOpen()
    {
        _recentCookingRefreshInstance = null;
        _recentCookingRefreshTarget = null;
    }

    private static void AfterCookingPanelOpen(object __instance)
    {
        RegisterOpenPanel(
            RefreshPanelKind.Cooking,
            __instance,
            ConsumeRecentPanelRefresh(RefreshPanelKind.Cooking, __instance));
    }

    private static void BeforeCookingPanelTeardown(object __instance)
    {
        ForgetPanel(RefreshPanelKind.Cooking, __instance);
    }

    private static void BeforeStoragePanelOpen()
    {
        _recentBeverageRefreshInstance = null;
        _recentBeverageRefreshTarget = null;
    }

    private static void AfterStoragePanelOpen(object __instance)
    {
        RegisterOpenPanel(
            RefreshPanelKind.Storage,
            __instance,
            ConsumeRecentPanelRefresh(RefreshPanelKind.Storage, __instance));
    }

    private static void BeforeStoragePanelTeardown(object __instance)
    {
        ForgetPanel(RefreshPanelKind.Storage, __instance);
    }

    private static void RecordPanelRefreshCompleted(
        RefreshPanelKind panelKind,
        object instance,
        RuntimeUiTargetSetSnapshot target)
    {
        if (panelKind == RefreshPanelKind.Cooking)
        {
            _recentCookingRefreshInstance = instance;
            _recentCookingRefreshTarget = target;
        }
        else
        {
            _recentBeverageRefreshInstance = instance;
            _recentBeverageRefreshTarget = target;
        }

        lock (SyncRoot)
        {
            var panel = GetPanelLocked(panelKind);
            if (panel == null || !ReferenceEquals(panel.Instance, instance)) return;

            panel.LastAttemptedTargetGeneration = target.Generation;
            panel.LastAppliedTargetGeneration = target.Generation;
        }
    }

    private static RuntimeUiTargetSetSnapshot? ConsumeRecentPanelRefresh(
        RefreshPanelKind panelKind,
        object instance)
    {
        if (panelKind == RefreshPanelKind.Cooking)
        {
            var target = ReferenceEquals(_recentCookingRefreshInstance, instance)
                ? _recentCookingRefreshTarget
                : null;
            _recentCookingRefreshInstance = null;
            _recentCookingRefreshTarget = null;
            return target;
        }

        var beverageTarget = ReferenceEquals(_recentBeverageRefreshInstance, instance)
            ? _recentBeverageRefreshTarget
            : null;
        _recentBeverageRefreshInstance = null;
        _recentBeverageRefreshTarget = null;
        return beverageTarget;
    }

    private static void RegisterOpenPanel(
        RefreshPanelKind panelKind,
        object instance,
        RuntimeUiTargetSetSnapshot? naturallyAppliedTarget)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycle.IsActive || !TryReadLivePanelPointer(instance, out var pointer)) return;

        var appliedGeneration = naturallyAppliedTarget?.Generation ?? long.MinValue;
        lock (SyncRoot)
        {
            SetPanelLocked(
                panelKind,
                new PanelRefreshRegistration(
                    instance,
                    pointer,
                    lifecycle.Generation,
                    appliedGeneration,
                    appliedGeneration));
        }
    }

    private static void ForgetPanel(RefreshPanelKind panelKind, object instance)
    {
        nint pointer = 0;
        _ = TryReadLivePanelPointer(instance, out pointer);
        lock (SyncRoot)
        {
            var panel = GetPanelLocked(panelKind);
            if (panel == null) return;
            if (!ReferenceEquals(panel.Instance, instance)
                && (pointer == 0 || panel.Pointer != pointer))
            {
                return;
            }

            SetPanelLocked(panelKind, null);
        }
    }

    private static void TryRefreshOpenPanel(
        RefreshPanelKind panelKind,
        NightBusinessLifecycleSnapshot lifecycle,
        RuntimeUiTargetSetSnapshot target)
    {
        PanelRefreshAttempt? attempt = null;
        lock (SyncRoot)
        {
            var panel = GetPanelLocked(panelKind);
            var refreshMethod = GetRefreshMethodLocked(panelKind);
            if (panel == null || refreshMethod == null) return;
            if (panel.BusinessGeneration != lifecycle.Generation)
            {
                SetPanelLocked(panelKind, null);
                _panelRefreshStalePanels++;
                return;
            }
            if (panel.LastAttemptedTargetGeneration == target.Generation) return;

            panel.LastAttemptedTargetGeneration = target.Generation;
            _panelRefreshAttempts++;
            attempt = new PanelRefreshAttempt(
                panelKind,
                panel.Instance,
                panel.Pointer,
                panel.BusinessGeneration,
                target.Generation,
                refreshMethod);
        }

        if (attempt == null) return;
        if (!TryReadLivePanelPointer(attempt.Instance, out var pointer)
            || pointer != attempt.Pointer
            || !attempt.RefreshMethod.DeclaringType!.IsInstanceOfType(attempt.Instance))
        {
            ForgetStaleRefreshAttempt(attempt);
            return;
        }

        var latestLifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        var latestTarget = Volatile.Read(ref _targetSet);
        if (!latestLifecycle.IsActive
            || latestLifecycle.Generation != attempt.BusinessGeneration
            || latestTarget.SessionGeneration != attempt.BusinessGeneration
            || latestTarget.Generation != attempt.TargetGeneration
            || !IsCurrentOpenPanel(attempt))
        {
            return;
        }

        try
        {
            attempt.RefreshMethod.Invoke(attempt.Instance, Array.Empty<object?>());
            lock (SyncRoot)
            {
                var panel = GetPanelLocked(panelKind);
                if (panel != null
                    && ReferenceEquals(panel.Instance, attempt.Instance)
                    && panel.Pointer == attempt.Pointer
                    && panel.BusinessGeneration == attempt.BusinessGeneration)
                {
                    panel.LastAppliedTargetGeneration = attempt.TargetGeneration;
                }
                _panelRefreshSuccesses++;
            }
        }
        catch (Exception ex)
        {
            NotePanelRefreshFailure(panelKind, attempt.TargetGeneration, ex);
        }
    }

    private static bool IsCurrentOpenPanel(PanelRefreshAttempt attempt)
    {
        lock (SyncRoot)
        {
            var panel = GetPanelLocked(attempt.PanelKind);
            return panel != null
                && ReferenceEquals(panel.Instance, attempt.Instance)
                && panel.Pointer == attempt.Pointer
                && panel.BusinessGeneration == attempt.BusinessGeneration
                && panel.LastAttemptedTargetGeneration == attempt.TargetGeneration;
        }
    }

    private static void ForgetStaleRefreshAttempt(PanelRefreshAttempt attempt)
    {
        lock (SyncRoot)
        {
            var panel = GetPanelLocked(attempt.PanelKind);
            if (panel == null
                || !ReferenceEquals(panel.Instance, attempt.Instance)
                || panel.Pointer != attempt.Pointer)
            {
                return;
            }

            SetPanelLocked(attempt.PanelKind, null);
            _panelRefreshStalePanels++;
        }
    }

    private static void NotePanelRefreshFailure(
        RefreshPanelKind panelKind,
        long targetGeneration,
        Exception exception)
    {
        var root = exception is TargetInvocationException { InnerException: not null }
            ? exception.InnerException
            : exception.GetBaseException();
        var message = $"{panelKind} target {targetGeneration}: {root.GetType().Name}: {root.Message}";
        if (message.Length > 220) message = message[..220] + "...";

        var shouldLog = false;
        lock (SyncRoot)
        {
            _panelRefreshFailures++;
            _lastPanelRefreshError = message;
            if (_panelRefreshWarningLogs < MaxPanelRefreshWarningLogs)
            {
                _panelRefreshWarningLogs++;
                shouldLog = true;
            }
        }

        if (shouldLog)
        {
            _log?.LogWarning(
                $"Runtime UI {panelKind.ToString().ToLowerInvariant()} panel refresh failed once for target generation {targetGeneration}; waiting for the game's next natural refresh: {root.GetBaseException().Message}");
        }
    }

    private static bool TryReadLivePanelPointer(object instance, out nint pointer)
    {
        pointer = 0;
        try
        {
            if (instance is Il2CppObjectBase nativeObject)
            {
                pointer = nativeObject.Pointer;
                return pointer != 0;
            }

            // Managed probes use the same explicit cached-pointer shape as a Unity wrapper.
            pointer = RuntimeReflectionUtility.ReadObjectPointer(instance);
            return pointer != 0;
        }
        catch
        {
            return false;
        }
    }

    private static PanelRefreshRegistration? GetPanelLocked(RefreshPanelKind panelKind)
    {
        return panelKind == RefreshPanelKind.Cooking ? _cookingPanel : _storagePanel;
    }

    private static void SetPanelLocked(
        RefreshPanelKind panelKind,
        PanelRefreshRegistration? registration)
    {
        if (panelKind == RefreshPanelKind.Cooking)
        {
            _cookingPanel = registration;
        }
        else
        {
            _storagePanel = registration;
        }
    }

    private static MethodInfo? GetRefreshMethodLocked(RefreshPanelKind panelKind)
    {
        return panelKind == RefreshPanelKind.Cooking ? _cookingRefreshMethod : _storageRefreshMethod;
    }

    private static void ClearPanelRegistrationsLocked()
    {
        _cookingPanel = null;
        _storagePanel = null;
        _panelRefreshTickActive = false;
    }

    private static string DescribePanelLocked(PanelRefreshRegistration? panel)
    {
        return panel == null
            ? "closed"
            : $"open@0x{panel.Pointer:X}/business:{panel.BusinessGeneration}/attempted:{panel.LastAttemptedTargetGeneration}/applied:{panel.LastAppliedTargetGeneration}";
    }

    private static bool OnCheckPinned(int pinnedType, int pinnedID, ref bool __result)
    {
        if (pinnedID < 0) return true;

        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycle.IsActive) return true;

        var cookingTarget = _cookingRefreshDepth > 0 ? _cookingScopeTarget : null;
        var beverageTarget = _beverageRefreshDepth > 0 ? _beverageScopeTarget : null;
        if (cookingTarget?.SessionGeneration != lifecycle.Generation) cookingTarget = null;
        if (beverageTarget?.SessionGeneration != lifecycle.Generation) beverageTarget = null;
        if (cookingTarget == null && beverageTarget == null) return true;

        if (pinnedType == (int)PinnedType.Recipes
            && cookingTarget != null
            && cookingTarget.GetRecipeClaims(pinnedID) != RuntimeUiTargetKinds.None)
        {
            __result = true;
            Interlocked.Increment(ref _recipeForces);
            return false;
        }

        if (cookingTarget != null
            && IsIngredientType(pinnedType)
            && cookingTarget.GetIngredientClaims(pinnedID) != RuntimeUiTargetKinds.None)
        {
            __result = true;
            Interlocked.Increment(ref _ingredientForces);
            return false;
        }

        if (beverageTarget != null
            && pinnedType == (int)PinnedType.Beverages
            && beverageTarget.GetBeverageClaims(pinnedID) != RuntimeUiTargetKinds.None)
        {
            __result = true;
            Interlocked.Increment(ref _beverageForces);
            return false;
        }

        return true;
    }

    private static bool IsIngredientType(int pinnedType)
    {
        return pinnedType == (int)PinnedType.IngredientsSeafood
            || pinnedType == (int)PinnedType.IngredientsMeat
            || pinnedType == (int)PinnedType.IngredientsVegetable
            || pinnedType == (int)PinnedType.IngredientsOther;
    }

    private static void SetPatchStatusLocked(PatchSlot patchSlot, string status)
    {
        switch (patchSlot)
        {
            case PatchSlot.CheckPinned:
                _checkPinnedPatchStatus = status;
                break;
            case PatchSlot.CookingScope:
                _cookingScopePatchStatus = status;
                break;
            case PatchSlot.BeverageScope:
                _beverageScopePatchStatus = status;
                break;
            case PatchSlot.CookingPanel:
                _cookingPanelPatchStatus = status;
                break;
            case PatchSlot.StoragePanel:
                _storagePanelPatchStatus = status;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(patchSlot), patchSlot, null);
        }
    }

    private static void SetRefreshMethodLocked(PatchSlot patchSlot, MethodInfo method)
    {
        switch (patchSlot)
        {
            case PatchSlot.CookingScope:
                _cookingRefreshMethod = method;
                break;
            case PatchSlot.BeverageScope:
                _storageRefreshMethod = method;
                break;
        }
    }

    private enum PatchSlot
    {
        CheckPinned,
        CookingScope,
        BeverageScope,
        CookingPanel,
        StoragePanel,
    }

    private enum RefreshPanelKind
    {
        Cooking,
        Storage,
    }

    private sealed class PanelRefreshRegistration
    {
        public PanelRefreshRegistration(
            object instance,
            nint pointer,
            long businessGeneration,
            long lastAttemptedTargetGeneration,
            long lastAppliedTargetGeneration)
        {
            Instance = instance;
            Pointer = pointer;
            BusinessGeneration = businessGeneration;
            LastAttemptedTargetGeneration = lastAttemptedTargetGeneration;
            LastAppliedTargetGeneration = lastAppliedTargetGeneration;
        }

        public object Instance { get; }

        public nint Pointer { get; }

        public long BusinessGeneration { get; }

        public long LastAttemptedTargetGeneration { get; set; }

        public long LastAppliedTargetGeneration { get; set; }
    }

    private sealed record PanelRefreshAttempt(
        RefreshPanelKind PanelKind,
        object Instance,
        nint Pointer,
        long BusinessGeneration,
        long TargetGeneration,
        MethodInfo RefreshMethod);

    private enum PinnedType
    {
        IngredientsSeafood = 0,
        Recipes = 1,
        Beverages = 2,
        Cookers = 3,
        IngredientsMeat = 4,
        IngredientsVegetable = 5,
        IngredientsOther = 6,
    }
}
