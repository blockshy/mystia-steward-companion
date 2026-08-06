using System.Collections.Concurrent;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// Owns one private visual overlay per exact rare/normal target HUD card.
/// </summary>
internal static class RuntimeOrderHighlightService
{
    private const string OrderingElementTypeName = "NightScene.UI.GuestManagementUtility.OrderingElement";
    private const string OrderControllerTypeName = "Night.UI.HUD.Ordering.OrderController";
    private const string OrderBaseTypeName = "NightScene.GuestManagementUtility.GuestsManager+OrderBase";
    private const string ImageTypeName = "UnityEngine.UI.Image";
    private const string RectTransformTypeName = "UnityEngine.RectTransform";
    private const string CanvasRendererTypeName = "UnityEngine.CanvasRenderer";
    private const string LayoutGroupTypeName = "UnityEngine.UI.LayoutGroup";
    private const string Il2CppActionTypeName = "Il2CppSystem.Action";
    private const int MaxTrackedElements = 64;
    private const int MaxWarningLogs = 4;
    private const float RetryIntervalSeconds = 0.5f;
    private const float HealthCheckIntervalSeconds = 0.25f;
    private static readonly TimeSpan AttachRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CaptureMaxAge = TimeSpan.FromHours(6);
    private static readonly object PatchRoot = new();
    private static readonly object DesiredRoot = new();
    private static readonly object VisualRoot = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly Dictionary<nint, RegisteredElement> RegisteredElements = new();
    private static readonly ConcurrentDictionary<Type, ImageAccessors> ImageAccessorCache = new();
    private static readonly string[] RequiredPatchKeys =
    {
        PatchKey(OrderingElementTypeName, "Initialize", 5),
        PatchKey(OrderControllerTypeName, "CreateOrderingElement", 1),
        PatchKey(OrderingElementTypeName, "Out", 0),
        PatchKey(OrderingElementTypeName, "DestroySelf", 0),
    };

    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static DateTime _lastAttachAttemptUtc = DateTime.MinValue;
    private static long _firstCoveredBusinessGeneration = long.MaxValue;
    private static PropertyInfo? _activeOrderProperty;
    private static PropertyInfo? _deskCodeProperty;
    private static PropertyInfo? _currentBorderImageProperty;
    private static Type? _rectTransformType;
    private static Type? _canvasRendererType;
    private static Type? _layoutGroupType;
    private static RuntimeUiTargetSetSnapshot _desiredTargetSet = RuntimeUiTargetSetSnapshot.Disabled;
    private static long _appliedTargetGeneration;
    private static bool _suspended = true;
    private static string _suspendReason = "night business inactive";
    private static readonly Dictionary<RuntimeUiTargetKind, ActiveOrderVisual> ActiveVisuals = new();
    private static readonly Dictionary<RuntimeUiTargetKind, float> NextAttemptAt = new();
    private static string _state = "disabled";
    private static string _lastFailure = "";
    private static long _registrationCallbacks;
    private static long _teardownCallbacks;
    private static long _createdVisuals;
    private static long _destroyErrors;
    private static long _bindingErrors;
    private static long _visualErrors;
    private static int _warningLogs;

    public static string Status
    {
        get
        {
            string hookStatus;
            lock (PatchRoot)
            {
                var missing = RequiredPatchKeys.Count(key => !PatchedMethods.Contains(key));
                hookStatus = $"hooks={RequiredPatchKeys.Length - missing}/{RequiredPatchKeys.Length}; firstCoveredGeneration={(_firstCoveredBusinessGeneration == long.MaxValue ? "pending" : _firstCoveredBusinessGeneration)}";
            }

            var desired = Volatile.Read(ref _desiredTargetSet);
            lock (VisualRoot)
            {
                var active = string.Join(",", ActiveVisuals
                    .OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key}:target:{pair.Value.TargetSetGeneration}/order:{FormatPointer(pair.Value.OrderPointer)}"));
                return $"{_state}; {hookStatus}; desired={desired.Generation}/session:{desired.SessionGeneration}/targets:{desired.Targets.Count}; applied={_appliedTargetGeneration}; tracked={RegisteredElements.Count}; active={active}; suspended={_suspended}; callbacks=registered:{_registrationCallbacks},teardown:{_teardownCallbacks}; visuals=created:{_createdVisuals},destroyErrors:{_destroyErrors}; errors=binding:{_bindingErrors},visual:{_visualErrors}; warnings={_warningLogs}/{MaxWarningLogs}; lastFailure={NormalizeStatus(_lastFailure)}";
            }
        }
    }

    public static void Attach(ManualLogSource log)
    {
        _log = log;
        TryAttach(force: true);
    }

    /// <summary>
    /// Publishes managed target state. Unity objects are only reconciled later on the main thread.
    /// </summary>
    public static void UpdateTargets(RuntimeUiTargetSetSnapshot targetSet)
    {
        ArgumentNullException.ThrowIfNull(targetSet);
        lock (DesiredRoot)
        {
            Volatile.Write(ref _desiredTargetSet, targetSet);
        }
    }

    public static void Tick()
    {
        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            NoteVisualError($"main-thread reconcile failed: {ex.GetBaseException().Message}");
        }
    }

    private static void TickCore()
    {
        TryAttach(force: false);
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        var desired = Volatile.Read(ref _desiredTargetSet);
        lock (VisualRoot)
        {
            if (_suspended) return;
            if (Environment.CurrentManagedThreadId != lifecycle.ThreadId)
            {
                _state = "waiting for Unity main thread";
                return;
            }

            if (_appliedTargetGeneration != desired.Generation)
            {
                ReconcileTargetSetChangeLocked(desired);
                _appliedTargetGeneration = desired.Generation;
            }

            var orderTargets = desired.Targets.Where(target => target.OrderHighlightEnabled).ToArray();
            var desiredEnabled = lifecycle.IsActive
                && desired.SessionGeneration == lifecycle.Generation
                && orderTargets.Length > 0
                && IsBusinessReady(lifecycle.Generation);
            if (!desiredEnabled)
            {
                DestroyAllActiveVisualsLocked();
                _state = orderTargets.Length == 0
                    ? "disabled"
                    : !lifecycle.IsActive
                        ? "waiting: night business inactive"
                        : desired.SessionGeneration != lifecycle.Generation
                            ? "waiting: target belongs to a different night-business session"
                            : "waiting: exact OrderingElement hooks did not cover this business session";
                return;
            }

            var now = Time.realtimeSinceStartup;
            foreach (var (kind, active) in ActiveVisuals.ToList())
            {
                if (!desired.TryGetTarget(kind, out var target)
                    || !target.OrderHighlightEnabled
                    || !active.Target.HasSameValues(target)
                    || active.SessionGeneration != desired.SessionGeneration)
                {
                    DestroyActiveVisualLocked(kind);
                    NextAttemptAt[kind] = 0f;
                    continue;
                }

                if (now >= active.NextHealthCheckAt)
                {
                    if (!TryValidateActiveVisualLocked(active, target, out var healthFailure))
                    {
                        DestroyActiveVisualLocked(kind);
                        NextAttemptAt[kind] = now + RetryIntervalSeconds;
                        SetFailureLocked($"{kind} active visual invalid: {healthFailure}");
                        continue;
                    }

                    active.NextHealthCheckAt = now + HealthCheckIntervalSeconds;
                }

                if (TryApplyPulseLocked(active, desired.Palette, out var pulseFailure)) continue;

                _visualErrors++;
                DestroyActiveVisualLocked(kind);
                NextAttemptAt[kind] = now + RetryIntervalSeconds;
                SetFailureLocked($"{kind} pulse failed: {pulseFailure}");
            }

            PruneStaleElementsLocked();
            var resolved = new List<ResolvedTargetElement>();
            foreach (var target in orderTargets)
            {
                if (ActiveVisuals.ContainsKey(target.Kind)
                    || NextAttemptAt.TryGetValue(target.Kind, out var retryAt) && now < retryAt)
                {
                    continue;
                }

                if (!TryResolveTargetElementLocked(
                        target,
                        desired.SessionGeneration,
                        out var registration,
                        out var binding,
                        out var resolutionFailure))
                {
                    NextAttemptAt[target.Kind] = now + RetryIntervalSeconds;
                    SetFailureLocked($"{target.Kind}: {resolutionFailure}");
                    continue;
                }

                resolved.Add(new ResolvedTargetElement(target, registration, binding));
            }

            var conflictingKinds = new HashSet<RuntimeUiTargetKind>();
            foreach (var candidate in resolved)
            {
                foreach (var active in ActiveVisuals.Values.Where(active =>
                             active.Target.Kind != candidate.Target.Kind
                             && active.OrderPointer == candidate.Binding.OrderPointer))
                {
                    conflictingKinds.Add(candidate.Target.Kind);
                    conflictingKinds.Add(active.Target.Kind);
                }
                foreach (var other in resolved.Where(other =>
                             other.Target.Kind != candidate.Target.Kind
                             && other.Binding.OrderPointer == candidate.Binding.OrderPointer))
                {
                    conflictingKinds.Add(candidate.Target.Kind);
                    conflictingKinds.Add(other.Target.Kind);
                }
            }
            foreach (var kind in conflictingKinds)
            {
                DestroyActiveVisualLocked(kind);
                NextAttemptAt[kind] = now + RetryIntervalSeconds;
                SetFailureLocked($"{kind}: exact native order is claimed by both target kinds");
            }

            foreach (var candidate in resolved.Where(candidate => !conflictingKinds.Contains(candidate.Target.Kind)))
            {
                if (!TryCreateActiveVisualLocked(
                        desired,
                        candidate.Target,
                        candidate.Registration,
                        candidate.Binding,
                        out var creationFailure))
                {
                    _visualErrors++;
                    NextAttemptAt[candidate.Target.Kind] = now + RetryIntervalSeconds;
                    SetFailureLocked($"{candidate.Target.Kind}: {creationFailure}");
                }
            }

            var latest = Volatile.Read(ref _desiredTargetSet);
            if (latest.Generation != desired.Generation) return;
            if (ActiveVisuals.Count > 0)
            {
                _state = $"active:{ActiveVisuals.Count}/{orderTargets.Length}";
                if (ActiveVisuals.Count == orderTargets.Length) _lastFailure = "";
            }
        }
    }

    public static void Suspend(string reason)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        lock (VisualRoot)
        {
            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            if (Environment.CurrentManagedThreadId == lifecycle.ThreadId)
            {
                DestroyAllActiveVisualsLocked();
            }
            else
            {
                AbandonAllActiveVisualsLocked();
            }
            RegisteredElements.Clear();
            NextAttemptAt.Clear();
            _state = HasOrderHighlightTargets(Volatile.Read(ref _desiredTargetSet))
                ? $"suspended: {_suspendReason}"
                : "disabled";
        }
    }

    public static void Resume(string reason)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        lock (VisualRoot)
        {
            if (Environment.CurrentManagedThreadId == lifecycle.ThreadId)
            {
                DestroyAllActiveVisualsLocked();
            }
            else
            {
                AbandonAllActiveVisualsLocked();
            }
            RegisteredElements.Clear();
            _suspended = false;
            _suspendReason = NormalizeReason(reason);
            NextAttemptAt.Clear();
            _state = HasOrderHighlightTargets(Volatile.Read(ref _desiredTargetSet))
                ? "waiting for exact HUD order element"
                : "disabled";
        }
    }

    /// <summary>
    /// Drops wrappers after native scene destruction without dereferencing them.
    /// </summary>
    public static void Abandon(string reason)
    {
        lock (VisualRoot)
        {
            AbandonAllActiveVisualsLocked();
            RegisteredElements.Clear();
            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            NextAttemptAt.Clear();
            _state = $"abandoned: {_suspendReason}";
        }
    }

    public static void Dispose(string reason)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        lock (VisualRoot)
        {
            if (Environment.CurrentManagedThreadId == lifecycle.ThreadId)
            {
                DestroyAllActiveVisualsLocked();
            }
            else
            {
                AbandonAllActiveVisualsLocked();
            }
            RegisteredElements.Clear();
            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            NextAttemptAt.Clear();
            _state = $"disposed: {_suspendReason}";
        }
    }

    private static void TryAttach(bool force)
    {
        lock (PatchRoot)
        {
            if (!force && DateTime.UtcNow - _lastAttachAttemptUtc < AttachRetryInterval) return;
            if (RequiredPatchKeys.All(PatchedMethods.Contains)) return;
            _lastAttachAttemptUtc = DateTime.UtcNow;
        }

        try
        {
            var elementType = RuntimeReflectionUtility.FindType(OrderingElementTypeName);
            var controllerType = RuntimeReflectionUtility.FindType(OrderControllerTypeName);
            if (elementType == null || controllerType == null)
            {
                if (force) TryLogWarning("Runtime order highlight is waiting for exact OrderingElement and OrderController types.");
                return;
            }

            if (!TryResolveExactElementMembers(elementType, out var activeOrder, out var currentBorder, out var memberFailure))
            {
                SetAttachFailure(memberFailure, force);
                return;
            }
            if (!TryResolveExactOrderMembers(activeOrder.PropertyType, out var deskCode, out memberFailure))
            {
                SetAttachFailure(memberFailure, force);
                return;
            }
            var rectTransformType = typeof(RectTransform);
            var canvasRendererType = RuntimeReflectionUtility.FindType(CanvasRendererTypeName);
            var layoutGroupType = currentBorder.PropertyType.Assembly.GetType(LayoutGroupTypeName, throwOnError: false);
            if (!IsExactComponentType(rectTransformType, RectTransformTypeName)
                || !IsExactComponentType(canvasRendererType, CanvasRendererTypeName)
                || !IsExactComponentType(layoutGroupType, LayoutGroupTypeName))
            {
                SetAttachFailure("Unity UI visual component types do not match the verified Component declarations", force);
                return;
            }

            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.runtime-order-highlight");
            var patchedNow = new List<string>();
            var failures = new List<string>();
            TryPatchInitialize(_harmony, elementType, patchedNow, failures);
            TryPatchCreateOrderingElement(
                _harmony,
                controllerType,
                elementType,
                activeOrder.PropertyType,
                patchedNow,
                failures);
            TryPatchTeardown(_harmony, elementType, "Out", patchedNow, failures);
            TryPatchTeardown(_harmony, elementType, "DestroySelf", patchedNow, failures);

            lock (PatchRoot)
            {
                _activeOrderProperty = activeOrder;
                _deskCodeProperty = deskCode;
                _currentBorderImageProperty = currentBorder;
                _rectTransformType = rectTransformType;
                _canvasRendererType = canvasRendererType;
                _layoutGroupType = layoutGroupType;
                if (_firstCoveredBusinessGeneration == long.MaxValue
                    && RequiredPatchKeys.All(PatchedMethods.Contains))
                {
                    _firstCoveredBusinessGeneration = checked(RuntimeNightBusinessLifecycle.Generation + 1);
                }
            }

            if (patchedNow.Count > 0)
            {
                _log?.LogInfo($"Runtime order highlight patched: {string.Join(", ", patchedNow)}.");
            }
            if (failures.Count > 0 && force)
            {
                TryLogWarning($"Runtime order highlight exact hooks are incomplete: {string.Join(" | ", failures.Take(4))}.");
            }
        }
        catch (Exception ex)
        {
            SetAttachFailure(ex.GetBaseException().Message, force);
        }
    }

    private static bool TryResolveExactOrderMembers(
        Type orderBaseType,
        out PropertyInfo deskCode,
        out string failure)
    {
        deskCode = null!;
        if (orderBaseType.FullName != OrderBaseTypeName)
        {
            failure = "OrderingElement.ActiveOrder is not the verified OrderBase type";
            return false;
        }

        var exactDeskCode = orderBaseType.GetProperty(
            "DeskCode",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (exactDeskCode?.PropertyType != typeof(int)
            || exactDeskCode.GetMethod?.IsStatic != false
            || exactDeskCode.SetMethod != null
            || exactDeskCode.GetIndexParameters().Length != 0)
        {
            failure = "OrderBase.DeskCode does not match the verified Int32 declaration";
            return false;
        }

        deskCode = exactDeskCode;
        failure = "";
        return true;
    }

    private static bool IsExactComponentType(Type? type, string expectedFullName)
    {
        return type?.FullName == expectedFullName && typeof(Component).IsAssignableFrom(type);
    }

    private static bool TryResolveExactElementMembers(
        Type elementType,
        out PropertyInfo activeOrder,
        out PropertyInfo currentBorder,
        out string failure)
    {
        activeOrder = null!;
        currentBorder = null!;
        var exactActiveOrder = elementType.GetProperty(
            "ActiveOrder",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (exactActiveOrder?.PropertyType.FullName != OrderBaseTypeName
            || exactActiveOrder.GetMethod?.IsStatic != false
            || exactActiveOrder.GetIndexParameters().Length != 0)
        {
            failure = "OrderingElement.ActiveOrder does not match the verified OrderBase declaration";
            return false;
        }

        var exactCurrentBorder = elementType.GetProperty(
            "borderStyleImageForCurrent",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (exactCurrentBorder?.PropertyType.FullName != ImageTypeName
            || exactCurrentBorder.GetMethod?.IsStatic != false
            || exactCurrentBorder.GetIndexParameters().Length != 0)
        {
            failure = "OrderingElement.borderStyleImageForCurrent does not match UnityEngine.UI.Image";
            return false;
        }

        activeOrder = exactActiveOrder;
        currentBorder = exactCurrentBorder;
        failure = "";
        return true;
    }

    private static void TryPatchInitialize(
        Harmony harmony,
        Type elementType,
        ICollection<string> patchedNow,
        ICollection<string> failures)
    {
        var key = PatchKey(OrderingElementTypeName, "Initialize", 5);
        lock (PatchRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        var target = elementType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SingleOrDefault(IsExactInitialize);
        var prefix = typeof(RuntimeOrderHighlightService).GetMethod(
            nameof(BeforeElementInitialize),
            BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || prefix == null)
        {
            failures.Add(key);
            return;
        }

        harmony.Patch(
            target,
            prefix: new HarmonyMethod(prefix) { priority = Priority.First });
        lock (PatchRoot) PatchedMethods.Add(key);
        patchedNow.Add(key);
    }

    private static void TryPatchCreateOrderingElement(
        Harmony harmony,
        Type controllerType,
        Type elementType,
        Type orderBaseType,
        ICollection<string> patchedNow,
        ICollection<string> failures)
    {
        var key = PatchKey(OrderControllerTypeName, "CreateOrderingElement", 1);
        lock (PatchRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        var matches = controllerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == "CreateOrderingElement"
                && method.ReturnType == elementType
                && !method.IsGenericMethod
                && method.GetParameters() is var parameters
                && parameters.Length == 1
                && parameters[0].ParameterType == orderBaseType)
            .ToArray();
        var postfix = typeof(RuntimeOrderHighlightService).GetMethod(
            nameof(AfterElementCreated),
            BindingFlags.NonPublic | BindingFlags.Static);
        if (matches.Length != 1 || postfix == null)
        {
            failures.Add(key);
            return;
        }

        harmony.Patch(
            matches[0],
            postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
        lock (PatchRoot) PatchedMethods.Add(key);
        patchedNow.Add(key);
    }

    private static void TryPatchTeardown(
        Harmony harmony,
        Type elementType,
        string methodName,
        ICollection<string> patchedNow,
        ICollection<string> failures)
    {
        var key = PatchKey(OrderingElementTypeName, methodName, 0);
        lock (PatchRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        var matches = elementType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == methodName
                && method.ReturnType == typeof(void)
                && !method.IsGenericMethod
                && method.GetParameters().Length == 0)
            .ToArray();
        var prefix = typeof(RuntimeOrderHighlightService).GetMethod(
            nameof(BeforeElementTeardown),
            BindingFlags.NonPublic | BindingFlags.Static);
        if (matches.Length != 1 || prefix == null)
        {
            failures.Add(key);
            return;
        }

        harmony.Patch(
            matches[0],
            prefix: new HarmonyMethod(prefix) { priority = Priority.First });
        lock (PatchRoot) PatchedMethods.Add(key);
        patchedNow.Add(key);
    }

    private static bool IsExactInitialize(MethodInfo method)
    {
        if (method.Name != "Initialize"
            || method.ReturnType != typeof(void)
            || method.IsGenericMethod)
        {
            return false;
        }

        var parameters = method.GetParameters();
        return parameters.Length == 5
            && parameters[0].ParameterType.FullName == OrderBaseTypeName
            && parameters[1].ParameterType == typeof(Transform)
            && parameters[2].ParameterType == typeof(int)
            && parameters[3].ParameterType == typeof(Sprite)
            && parameters[4].ParameterType.FullName == Il2CppActionTypeName;
    }

    private static void BeforeElementInitialize(object __instance)
    {
        TryRemoveElement(__instance, "Initialize/rebind");
    }

    private static void AfterElementCreated(object? __result, object __0)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        try
        {
            var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
            if (Environment.CurrentManagedThreadId != lifecycle.ThreadId
                || !IsBusinessReady(lifecycle.Generation)
                || __result == null
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(__result, out var elementPointer)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(__0, out var argumentOrderPointer))
            {
                return;
            }

            PropertyInfo? activeOrderProperty;
            PropertyInfo? deskCodeProperty;
            lock (PatchRoot)
            {
                activeOrderProperty = _activeOrderProperty;
                deskCodeProperty = _deskCodeProperty;
            }
            var activeOrder = activeOrderProperty?.GetValue(__result);
            if (activeOrder == null
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(activeOrder, out var activeOrderPointer)
                || activeOrderPointer != argumentOrderPointer)
            {
                NoteBindingError("CreateOrderingElement did not return the exact ActiveOrder pointer");
                return;
            }

            if (deskCodeProperty?.GetValue(__0) is not int deskCode || deskCode < 0)
            {
                NoteBindingError("CreateOrderingElement order has no valid exact DeskCode");
                return;
            }

            lock (VisualRoot)
            {
                if (_suspended) return;
                RemoveRegisteredElementLocked(elementPointer, destroyActiveVisual: true);
                RegisteredElements[elementPointer] = new RegisteredElement(
                    __result,
                    elementPointer,
                    activeOrderPointer,
                    deskCode,
                    lifecycle.Generation,
                    Time.realtimeSinceStartup);
                _registrationCallbacks++;
                while (RegisteredElements.Count > MaxTrackedElements)
                {
                    var oldest = RegisteredElements.Values
                        .OrderBy(item => item.RegisteredAt)
                        .First();
                    RemoveRegisteredElementLocked(oldest.ElementPointer, destroyActiveVisual: true);
                }
                NextAttemptAt.Clear();
            }
        }
        catch (Exception ex)
        {
            NoteBindingError($"CreateOrderingElement callback failed: {ex.GetBaseException().Message}");
        }
    }

    private static void BeforeElementTeardown(object __instance)
    {
        TryRemoveElement(__instance, "teardown");
    }

    private static void TryRemoveElement(object instance, string source)
    {
        try
        {
            if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(instance, out var elementPointer)) return;
            var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
            lock (VisualRoot)
            {
                _teardownCallbacks++;
                if (Environment.CurrentManagedThreadId == lifecycle.ThreadId)
                {
                    RemoveRegisteredElementLocked(elementPointer, destroyActiveVisual: true);
                }
                else
                {
                    RemoveRegisteredElementLocked(elementPointer, destroyActiveVisual: false);
                }
                NextAttemptAt.Clear();
            }
        }
        catch (Exception ex)
        {
            NoteBindingError($"{source} callback failed: {ex.GetBaseException().Message}");
        }
    }

    private static bool TryResolveTargetElementLocked(
        RuntimeUiTargetSnapshot target,
        long sessionGeneration,
        out RegisteredElement registration,
        out RuntimeUiTargetOrderBinding binding,
        out string failure)
    {
        registration = null!;
        binding = default;
        if (!RuntimeUiTargetOrderResolver.TryResolveCurrentCapture(
                target,
                CaptureMaxAge,
                out var resolved,
                out failure)
            || resolved == null)
        {
            if (string.IsNullOrEmpty(failure)) failure = "target capture has no exact runtime binding";
            return false;
        }
        binding = resolved.Value;

        var matches = new List<RegisteredElement>();
        foreach (var candidate in RegisteredElements.Values)
        {
            if (candidate.BusinessGeneration != sessionGeneration
                || candidate.DeskCode != target.DeskCode
                || candidate.OrderPointer != binding.OrderPointer)
            {
                continue;
            }

            if (TryValidateRegisteredElementLocked(candidate, out _)) matches.Add(candidate);
        }

        if (matches.Count != 1)
        {
            failure = $"exact native order matched {matches.Count} registered HUD elements";
            return false;
        }

        registration = matches[0];
        failure = "";
        return true;
    }

    private static bool TryCreateActiveVisualLocked(
        RuntimeUiTargetSetSnapshot targetSet,
        RuntimeUiTargetSnapshot target,
        RegisteredElement registration,
        RuntimeUiTargetOrderBinding binding,
        out string failure)
    {
        GameObject? clone = null;
        try
        {
            if (!TryValidateRegisteredElementLocked(registration, out failure)) return false;
            PropertyInfo? borderProperty;
            Type? rectTransformType;
            Type? canvasRendererType;
            Type? layoutGroupType;
            lock (PatchRoot)
            {
                borderProperty = _currentBorderImageProperty;
                rectTransformType = _rectTransformType;
                canvasRendererType = _canvasRendererType;
                layoutGroupType = _layoutGroupType;
            }
            if (borderProperty == null
                || rectTransformType == null
                || canvasRendererType == null
                || layoutGroupType == null)
            {
                failure = "verified current-order visual declarations are unavailable";
                return false;
            }

            var sourceImage = borderProperty.GetValue(registration.Element);
            if (sourceImage is not Component sourceImageComponent
                || !IsLiveUnityObject(sourceImageComponent))
            {
                failure = "current-order border image is unavailable";
                return false;
            }

            if (registration.Element is not Component elementComponent
                || !IsLiveUnityObject(elementComponent))
            {
                failure = "registered OrderingElement component is unavailable";
                return false;
            }

            var sourceObject = sourceImageComponent.gameObject;
            var sourceTransform = sourceImageComponent.transform;
            var elementTransform = elementComponent.transform;
            if (sourceObject == null
                || sourceTransform == null
                || elementTransform == null
                || !IsLiveUnityObject(sourceObject)
                || !IsLiveUnityObject(sourceTransform)
                || !IsLiveUnityObject(elementTransform)
                || !sourceTransform.IsChildOf(elementTransform))
            {
                failure = "current-order border image is not owned by the registered OrderingElement";
                return false;
            }

            if (!HasExactSafeImageComponents(
                    sourceObject,
                    sourceTransform,
                    sourceImageComponent,
                    rectTransformType,
                    canvasRendererType,
                    borderProperty.PropertyType,
                    out failure))
            {
                return false;
            }

            var parent = sourceTransform.parent;
            if (parent == null || !IsLiveUnityObject(parent))
            {
                failure = "current-order border parent is unavailable";
                return false;
            }
            if (!HasNoNativeLayoutGroup(parent, layoutGroupType, out failure)) return false;

            clone = UnityEngine.Object.Instantiate(sourceObject, parent);
            if (clone == null || !IsLiveUnityObject(clone))
            {
                failure = "failed to instantiate the private order border image";
                return false;
            }

            clone.name = "MystiaStewardCompanion.TargetOrderHighlight";
            var cloneTransform = clone.transform;
            cloneTransform.localPosition = sourceTransform.localPosition;
            cloneTransform.localRotation = sourceTransform.localRotation;
            cloneTransform.localScale = Vector3.one;
            cloneTransform.SetSiblingIndex(Math.Min(sourceTransform.GetSiblingIndex() + 1, parent.childCount - 1));

            var clonedImageComponent = clone.GetComponent(Il2CppType.From(borderProperty.PropertyType));
            var clonedImage = RuntimeReflectionUtility.TryCastRuntimeObject(
                clonedImageComponent,
                ImageTypeName);
            if (clonedImage is not Component clonedImageWrapper
                || !IsLiveUnityObject(clonedImageWrapper))
            {
                failure = "cloned order border has no exact UnityEngine.UI.Image";
                return false;
            }
            if (!HasExactSafeImageComponents(
                    clone,
                    cloneTransform,
                    clonedImageWrapper,
                    rectTransformType,
                    canvasRendererType,
                    borderProperty.PropertyType,
                    out var cloneShapeFailure))
            {
                failure = $"cloned order border failed exact native shape verification: {cloneShapeFailure}";
                return false;
            }

            var imageAccessors = GetImageAccessors(clonedImage.GetType());
            if (!imageAccessors.IsExact)
            {
                failure = "cloned order border Image accessors do not match the verified declarations";
                return false;
            }

            imageAccessors.SetRaycastTarget!.Invoke(clonedImage, new object?[] { false });
            imageAccessors.SetEnabled!.Invoke(clonedImage, new object?[] { true });
            clone.SetActive(true);
            if (!clone.activeInHierarchy
                || imageAccessors.GetEnabled!.Invoke(clonedImage, null) is not true
                || imageAccessors.GetSprite!.Invoke(clonedImage, null) is not Sprite sprite
                || !IsLiveUnityObject(sprite))
            {
                failure = "cloned order border is not renderable";
                return false;
            }

            var color = RuntimeTargetHighlightStyle.BuildOrderHighlightPulseColor(
                target.Claim,
                targetSet.Palette,
                Time.realtimeSinceStartup);
            imageAccessors.SetColor!.Invoke(clonedImage, new object?[] { color });
            lock (DesiredRoot)
            {
                var latest = Volatile.Read(ref _desiredTargetSet);
                if (latest.Generation != targetSet.Generation
                    || !latest.TryGetTarget(target.Kind, out var latestTarget)
                    || !latestTarget.HasSameValues(target))
                {
                    failure = "target changed while creating order visual";
                    return false;
                }

                ActiveVisuals[target.Kind] = new ActiveOrderVisual(
                    targetSet.Generation,
                    targetSet.SessionGeneration,
                    target,
                    clone,
                    clonedImage,
                    imageAccessors,
                    registration,
                    binding,
                    Time.realtimeSinceStartup + HealthCheckIntervalSeconds);
                _createdVisuals++;
                clone = null;
            }
            failure = "";
            return true;
        }
        catch (TargetInvocationException ex)
        {
            failure = ex.InnerException?.GetBaseException().Message ?? ex.GetBaseException().Message;
            return false;
        }
        catch (Exception ex)
        {
            failure = ex.GetBaseException().Message;
            return false;
        }
        finally
        {
            if (clone != null) SafeDestroyOverlay(clone);
        }
    }

    private static bool TryValidateActiveVisualLocked(
        ActiveOrderVisual active,
        RuntimeUiTargetSnapshot target,
        out string failure)
    {
        if (!IsLiveUnityObject(active.Overlay)
            || !IsLiveUnityObject(active.Image)
            || !active.Overlay.activeInHierarchy
            || active.ImageAccessors.GetEnabled?.Invoke(active.Image, null) is not true)
        {
            failure = "private order border image is unavailable or inactive";
            return false;
        }

        if (!TryValidateRegisteredElementLocked(active.Element, out failure)) return false;
        if (!RuntimeUiTargetOrderResolver.TryResolveCurrentCapture(
                target,
                CaptureMaxAge,
                out var binding,
                out failure)
            || binding == null
            || binding.Value.OrderPointer != active.OrderPointer
            || binding.Value.ControllerPointer != active.ControllerPointer
            || binding.Value.OrderLifecycleSequence != active.OrderLifecycleSequence)
        {
            if (string.IsNullOrEmpty(failure)) failure = "active capture no longer owns the highlighted order";
            return false;
        }

        failure = "";
        return true;
    }

    private static bool TryValidateRegisteredElementLocked(
        RegisteredElement registration,
        out string failure)
    {
        if (!RegisteredElements.TryGetValue(registration.ElementPointer, out var current)
            || !ReferenceEquals(current, registration)
            || !IsLiveUnityObject(registration.Element))
        {
            failure = "registered OrderingElement is stale";
            return false;
        }

        PropertyInfo? activeOrderProperty;
        lock (PatchRoot) activeOrderProperty = _activeOrderProperty;
        try
        {
            var activeOrder = activeOrderProperty?.GetValue(registration.Element);
            if (activeOrder == null
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(activeOrder, out var activeOrderPointer)
                || activeOrderPointer != registration.OrderPointer)
            {
                failure = "OrderingElement.ActiveOrder was rebound";
                return false;
            }
        }
        catch (Exception ex)
        {
            failure = $"OrderingElement.ActiveOrder read failed: {ex.GetBaseException().Message}";
            return false;
        }

        failure = "";
        return true;
    }

    private static bool TryApplyPulseLocked(
        ActiveOrderVisual active,
        RuntimeTargetHighlightPalette palette,
        out string failure)
    {
        try
        {
            if (active.ImageAccessors.SetColor == null)
            {
                failure = "active Image color setter is unavailable";
                return false;
            }

            var color = RuntimeTargetHighlightStyle.BuildOrderHighlightPulseColor(
                active.Target.Claim,
                palette,
                Time.realtimeSinceStartup);
            active.ImageAccessors.SetColor.Invoke(
                active.Image,
                new object?[] { color });
            failure = "";
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.GetBaseException().Message;
            return false;
        }
    }

    private static bool HasExactSafeImageComponents(
        GameObject sourceObject,
        Transform sourceTransform,
        Component sourceImage,
        Type rectTransformType,
        Type canvasRendererType,
        Type imageType,
        out string failure)
    {
        try
        {
            if (sourceObject.transform.childCount != 0)
            {
                failure = $"current-order border has {sourceObject.transform.childCount} child objects instead of a leaf-only visual shape";
                return false;
            }

            var components = sourceObject.GetComponents<Component>();
            if (components.Length != 3)
            {
                failure = $"current-order border has {components.Length} components instead of the exact visual-only shape";
                return false;
            }

            var enumeratedPointers = new HashSet<nint>();
            for (var index = 0; index < components.Length; index += 1)
            {
                var component = components[index];
                if (component == null
                    || !IsLiveUnityObject(component)
                    || !RuntimeReflectionUtility.TryReadNativeObjectPointer(component, out var componentPointer)
                    || !enumeratedPointers.Add(componentPointer))
                {
                    failure = $"current-order border component {index} has no unique live native identity";
                    return false;
                }
            }

            if (!TryGetExactNativeComponent(
                    sourceObject,
                    rectTransformType,
                    RectTransformTypeName,
                    out var rectTransformPointer,
                    out failure)
                || !TryGetExactNativeComponent(
                    sourceObject,
                    canvasRendererType,
                    CanvasRendererTypeName,
                    out var canvasRendererPointer,
                    out failure)
                || !TryGetExactNativeComponent(
                    sourceObject,
                    imageType,
                    ImageTypeName,
                    out var imagePointer,
                    out failure))
            {
                return false;
            }

            if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(sourceTransform, out var sourceTransformPointer)
                || sourceTransformPointer != rectTransformPointer)
            {
                failure = "current-order border Transform does not match its exact native RectTransform";
                return false;
            }
            if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(sourceImage, out var sourceImagePointer)
                || sourceImagePointer != imagePointer)
            {
                failure = "current-order border property does not match its exact native Image";
                return false;
            }

            var exactPointers = new HashSet<nint>
            {
                rectTransformPointer,
                canvasRendererPointer,
                imagePointer,
            };
            if (exactPointers.Count != 3 || !enumeratedPointers.SetEquals(exactPointers))
            {
                failure = "current-order border components do not match the exact native RectTransform/CanvasRenderer/Image shape";
                return false;
            }

            failure = "";
            return true;
        }
        catch (Exception ex)
        {
            failure = $"current-order border component inspection failed: {ex.GetBaseException().Message}";
            return false;
        }
    }

    private static bool TryGetExactNativeComponent(
        GameObject owner,
        Type componentType,
        string componentTypeName,
        out nint pointer,
        out string failure)
    {
        pointer = 0;
        try
        {
            var component = owner.GetComponent(Il2CppType.From(componentType));
            if (component == null || !IsLiveUnityObject(component))
            {
                pointer = 0;
                failure = $"current-order border has no exact live native {componentTypeName}";
                return false;
            }
            pointer = component.Pointer;
            if (pointer == 0)
            {
                failure = $"current-order border {componentTypeName} has no Il2CppObject pointer";
                return false;
            }
            var expectedClassPointer = Il2CppClassPointerStore.GetNativeClassPointer(componentType);
            var actualClassPointer = IL2CPP.il2cpp_object_get_class(pointer);
            if (expectedClassPointer == IntPtr.Zero
                || actualClassPointer == IntPtr.Zero
                || actualClassPointer != expectedClassPointer)
            {
                pointer = 0;
                failure = $"current-order border native component is not the exact {componentTypeName} class";
                return false;
            }

            failure = "";
            return true;
        }
        catch (Exception ex)
        {
            pointer = 0;
            failure = $"current-order border exact {componentTypeName} query failed: {ex.GetBaseException().Message}";
            return false;
        }
    }

    private static bool HasNoNativeLayoutGroup(
        Transform parent,
        Type layoutGroupType,
        out string failure)
    {
        try
        {
            var layoutGroup = parent.gameObject.GetComponent(Il2CppType.From(layoutGroupType));
            if (layoutGroup != null)
            {
                if (!IsLiveUnityObject(layoutGroup)
                    || !RuntimeReflectionUtility.TryReadNativeObjectPointer(layoutGroup, out _))
                {
                    failure = "current-order border parent returned an unavailable UnityEngine.UI.LayoutGroup";
                    return false;
                }

                failure = "current-order border parent is managed by UnityEngine.UI.LayoutGroup";
                return false;
            }

            failure = "";
            return true;
        }
        catch (Exception ex)
        {
            failure = $"current-order border parent layout inspection failed: {ex.GetBaseException().Message}";
            return false;
        }
    }

    private static ImageAccessors GetImageAccessors(Type imageType)
    {
        return ImageAccessorCache.GetOrAdd(imageType, static type =>
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var getSprite = SingleMethod(methods, "get_sprite", Type.EmptyTypes, expectedReturnTypeName: "UnityEngine.Sprite");
            var setColor = SingleMethod(methods, "set_color", new[] { typeof(Color) }, expectedReturnTypeName: "System.Void");
            var setRaycastTarget = SingleMethod(methods, "set_raycastTarget", new[] { typeof(bool) }, expectedReturnTypeName: "System.Void");
            var getEnabled = SingleMethod(methods, "get_enabled", Type.EmptyTypes, expectedReturnTypeName: "System.Boolean");
            var setEnabled = SingleMethod(methods, "set_enabled", new[] { typeof(bool) }, expectedReturnTypeName: "System.Void");
            return new ImageAccessors(getSprite, setColor, setRaycastTarget, getEnabled, setEnabled);
        });
    }

    private static MethodInfo? SingleMethod(
        IEnumerable<MethodInfo> methods,
        string name,
        IReadOnlyList<Type> parameterTypes,
        string expectedReturnTypeName)
    {
        var matches = methods.Where(method =>
        {
            if (method.Name != name
                || method.IsGenericMethod
                || method.ReturnType.FullName != expectedReturnTypeName)
            {
                return false;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != parameterTypes.Count) return false;
            for (var index = 0; index < parameters.Length; index += 1)
            {
                if (parameters[index].ParameterType != parameterTypes[index]) return false;
            }
            return true;
        }).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static void PruneStaleElementsLocked()
    {
        foreach (var registration in RegisteredElements.Values.ToList())
        {
            if (TryValidateRegisteredElementLocked(registration, out _)) continue;
            RemoveRegisteredElementLocked(registration.ElementPointer, destroyActiveVisual: true);
        }
    }

    private static void RemoveRegisteredElementLocked(nint pointer, bool destroyActiveVisual)
    {
        if (!RegisteredElements.Remove(pointer, out var removed)) return;
        foreach (var (kind, active) in ActiveVisuals
                     .Where(pair => ReferenceEquals(pair.Value.Element, removed))
                     .ToList())
        {
            if (destroyActiveVisual) DestroyActiveVisualLocked(kind);
            else AbandonActiveVisualLocked(kind);
        }
    }

    private static void ReconcileTargetSetChangeLocked(RuntimeUiTargetSetSnapshot targetSet)
    {
        foreach (var (kind, active) in ActiveVisuals.ToList())
        {
            if (targetSet.SessionGeneration != active.SessionGeneration
                || !targetSet.TryGetTarget(kind, out var target)
                || !target.OrderHighlightEnabled
                || !active.Target.HasSameValues(target))
            {
                DestroyActiveVisualLocked(kind);
                NextAttemptAt[kind] = 0f;
                continue;
            }

            active.TargetSetGeneration = targetSet.Generation;
            active.Target = target;
        }

        foreach (var kind in NextAttemptAt.Keys.ToList())
        {
            if (!targetSet.TryGetTarget(kind, out var target) || !target.OrderHighlightEnabled)
            {
                NextAttemptAt.Remove(kind);
            }
        }
        foreach (var target in targetSet.Targets.Where(target => target.OrderHighlightEnabled))
        {
            if (!ActiveVisuals.ContainsKey(target.Kind)) NextAttemptAt[target.Kind] = 0f;
        }
    }

    private static bool HasOrderHighlightTargets(RuntimeUiTargetSetSnapshot targetSet)
    {
        return targetSet.Targets.Any(target => target.OrderHighlightEnabled);
    }

    private static void DestroyActiveVisualLocked(RuntimeUiTargetKind kind)
    {
        if (!ActiveVisuals.Remove(kind, out var active)) return;
        SafeDestroyOverlay(active.Overlay);
    }

    private static void AbandonActiveVisualLocked(RuntimeUiTargetKind kind)
    {
        ActiveVisuals.Remove(kind);
    }

    private static void DestroyAllActiveVisualsLocked()
    {
        foreach (var kind in ActiveVisuals.Keys.ToList()) DestroyActiveVisualLocked(kind);
    }

    private static void AbandonAllActiveVisualsLocked()
    {
        ActiveVisuals.Clear();
    }

    private static void SafeDestroyOverlay(GameObject overlay)
    {
        try
        {
            if (!IsLiveUnityObject(overlay)) return;
            overlay.SetActive(false);
            UnityEngine.Object.Destroy(overlay);
        }
        catch
        {
            Interlocked.Increment(ref _destroyErrors);
        }
    }

    private static bool IsBusinessReady(long generation)
    {
        lock (PatchRoot)
        {
            return RequiredPatchKeys.All(PatchedMethods.Contains)
                && generation >= _firstCoveredBusinessGeneration;
        }
    }

    private static bool IsLiveUnityObject(object target)
    {
        try
        {
            if (!typeof(UnityEngine.Object).IsInstanceOfType(target)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(target, out _))
            {
                return false;
            }

            var cachedPointerProperty = typeof(UnityEngine.Object).GetProperty(
                "m_CachedPtr",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            return cachedPointerProperty?.PropertyType == typeof(IntPtr)
                && cachedPointerProperty.GetMethod?.IsStatic == false
                && cachedPointerProperty.GetIndexParameters().Length == 0
                && cachedPointerProperty.GetValue(target) is IntPtr cachedPointer
                && cachedPointer != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static void SetAttachFailure(string failure, bool log)
    {
        lock (VisualRoot)
        {
            SetFailureLocked($"attach failed: {failure}");
        }
        if (log) TryLogWarning($"Runtime order highlight attach failed: {NormalizeStatus(failure)}");
    }

    private static void SetFailureLocked(string failure)
    {
        _lastFailure = NormalizeStatus(failure);
        _state = $"unavailable: {_lastFailure}";
    }

    private static void NoteBindingError(string failure)
    {
        ManualLogSource? log = null;
        lock (VisualRoot)
        {
            _bindingErrors++;
            _lastFailure = NormalizeStatus(failure);
            if (_warningLogs < MaxWarningLogs)
            {
                _warningLogs++;
                log = _log;
            }
        }
        log?.LogWarning($"Runtime order highlight binding failed: {NormalizeStatus(failure)}");
    }

    private static void NoteVisualError(string failure)
    {
        ManualLogSource? log = null;
        lock (VisualRoot)
        {
            _visualErrors++;
            DestroyAllActiveVisualsLocked();
            NextAttemptAt.Clear();
            SetFailureLocked(failure);
            if (_warningLogs < MaxWarningLogs)
            {
                _warningLogs++;
                log = _log;
            }
        }
        log?.LogWarning($"Runtime order highlight visual failed: {NormalizeStatus(failure)}");
    }

    private static void TryLogWarning(string message)
    {
        try
        {
            _log?.LogWarning(message);
        }
        catch
        {
            // Diagnostics must not affect native callbacks.
        }
    }

    private static string PatchKey(string typeName, string methodName, int parameterCount)
    {
        return $"{typeName}.{methodName}/{parameterCount}";
    }

    private static string NormalizeReason(string reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? "scene unavailable" : reason.Trim();
    }

    private static string NormalizeStatus(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
        return normalized.Length <= 220 ? normalized : normalized[..220] + "...";
    }

    private static string FormatPointer(nint pointer)
    {
        return pointer == 0 ? "none" : $"0x{pointer:x}";
    }

    private sealed class ActiveOrderVisual
    {
        public ActiveOrderVisual(
            long targetSetGeneration,
            long sessionGeneration,
            RuntimeUiTargetSnapshot target,
            GameObject overlay,
            object image,
            ImageAccessors imageAccessors,
            RegisteredElement element,
            RuntimeUiTargetOrderBinding binding,
            float nextHealthCheckAt)
        {
            TargetSetGeneration = targetSetGeneration;
            SessionGeneration = sessionGeneration;
            Target = target;
            Overlay = overlay;
            Image = image;
            ImageAccessors = imageAccessors;
            Element = element;
            OrderPointer = binding.OrderPointer;
            ControllerPointer = binding.ControllerPointer;
            OrderLifecycleSequence = binding.OrderLifecycleSequence;
            NextHealthCheckAt = nextHealthCheckAt;
        }

        public long TargetSetGeneration { get; set; }
        public long SessionGeneration { get; }
        public RuntimeUiTargetSnapshot Target { get; set; }
        public GameObject Overlay { get; }
        public object Image { get; }
        public ImageAccessors ImageAccessors { get; }
        public RegisteredElement Element { get; }
        public nint OrderPointer { get; }
        public nint ControllerPointer { get; }
        public long OrderLifecycleSequence { get; }
        public float NextHealthCheckAt { get; set; }
    }

    private sealed record ResolvedTargetElement(
        RuntimeUiTargetSnapshot Target,
        RegisteredElement Registration,
        RuntimeUiTargetOrderBinding Binding);

    private sealed record RegisteredElement(
        object Element,
        nint ElementPointer,
        nint OrderPointer,
        int DeskCode,
        long BusinessGeneration,
        float RegisteredAt);

    private sealed record ImageAccessors(
        MethodInfo? GetSprite,
        MethodInfo? SetColor,
        MethodInfo? SetRaycastTarget,
        MethodInfo? GetEnabled,
        MethodInfo? SetEnabled)
    {
        public bool IsExact => GetSprite != null
            && SetColor != null
            && SetRaycastTarget != null
            && GetEnabled != null
            && SetEnabled != null;
    }
}
