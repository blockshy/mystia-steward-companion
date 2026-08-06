using System.Globalization;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// Resolves the exact keyed desk-order button created by
/// <c>WorkSceneThrowDeliverPanel</c> and owns one non-raycast target-fill clone per exact target.
/// Native Images and logical selection remain unchanged.
/// </summary>
internal static class RuntimeThrowDeliverOrderHighlightService
{
    private const string ThrowDeliverPanelTypeName =
        "NightScene.UI.HUDUtility.WorkSceneThrowDeliverPanel";
    private const string GuestControllerTypeName =
        "NightScene.GuestManagementUtility.GuestGroupController";
    private const string Il2CppDictionaryTypeName =
        "Il2CppSystem.Collections.Generic.Dictionary`2";
    private const string Il2CppListTypeName =
        "Il2CppSystem.Collections.Generic.List`1";
    private const string Il2CppValueTupleTypeName = "Il2CppSystem.ValueTuple`3";
    private const string UiLogicalGroupTypeName =
        "DEYU.AdpUISystem.Utils.UILogicalGroupT`1";
    private const string AlignedListTypeName = "DEYU.Collections.AlignedList`2";
    private const string UiLogicalUnitTypeName =
        "DEYU.AdpUISystem.LogicalCollection.UILogicalUnit";
    private const string SelectionEventTypeName =
        "DEYU.AdpUISystem.Utils.AdpUISystemUtils+UnityEvent_Bool";
    private const string UnityEventTypeName = "UnityEngine.Events.UnityEvent`1";
    private const string UnityEventBaseTypeName = "UnityEngine.Events.UnityEventBase";
    private const string PersistentCallGroupTypeName =
        "UnityEngine.Events.PersistentCallGroup";
    private const string PersistentCallTypeName = "UnityEngine.Events.PersistentCall";
    private const string RectTransformTypeName = "UnityEngine.RectTransform";
    private const string CanvasRendererTypeName = "UnityEngine.CanvasRenderer";
    private const string ImageTypeName = "UnityEngine.UI.Image";
    private const string ImageRenderTypeName = "UnityEngine.UI.Image+Type";
    private const string LayoutGroupTypeName = "UnityEngine.UI.LayoutGroup";
    private const string GameObjectTypeName = "UnityEngine.GameObject";
    private const string Vector3TypeName = "UnityEngine.Vector3";
    private const string OpenPatchKey = ThrowDeliverPanelTypeName + ".OnPanelOpen/0";
    private const string ClosePatchKey = ThrowDeliverPanelTypeName + ".OnPanelClose/0";
    private const string OwnedFillName = "MystiaStewardCompanion.ThrowDeliveryTargetFill";
    private const string SelectionEnabledMethodName = "set_enabled";
    private const int MaxWarningLogsPerBusiness = 8;
    private const int MaxVisualLogsPerBusiness = 16;
    private const int MaxPanelButtons = 64;
    private const int MaxDirectButtonChildren = 32;
    private const int MaxGeometryDiagnosticEntries = 4;
    private const float HealthCheckIntervalSeconds = 0.25f;
    private static readonly TimeSpan AttachRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CaptureMaxAge = TimeSpan.FromHours(6);
    private static readonly object PatchRoot = new();
    private static readonly object TargetRoot = new();
    private static readonly object BindingRoot = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);

    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static DateTime _lastAttachAttemptUtc = DateTime.MinValue;
    private static PanelMembers? _members;
    private static long _firstCoveredBusinessGeneration = long.MaxValue;
    private static RuntimeUiTargetSetSnapshot _desiredTargetSet = RuntimeUiTargetSetSnapshot.Disabled;
    private static ActivePanelRegistration? _activePanel;
    private static readonly Dictionary<RuntimeUiTargetKind, ActiveTargetBinding> ActiveBindings = new();
    private static readonly Dictionary<RuntimeUiTargetKind, float> NextBindAttemptAt = new();
    private static bool _suspended = true;
    private static string _suspendReason = "night business inactive";
    private static string _state = "disabled";
    private static string _lastFailure = "";
    private static long _openPrefixes;
    private static long _openPostfixes;
    private static long _closePrefixes;
    private static long _registeredPanels;
    private static long _bindingErrors;
    private static long _visualErrors;
    private static long _createdVisuals;
    private static long _rebuiltVisuals;
    private static long _destroyedVisuals;
    private static long _abandonedVisuals;
    private static int _visualLogs;
    private static int _warningLogs;
    private static long _warningBusinessGeneration;
    private static readonly HashSet<FailureLogIdentity> LoggedFailures = new();

    public static string Status
    {
        get
        {
            string hookStatus;
            lock (PatchRoot)
            {
                hookStatus = $"hooks={(PatchedMethods.Contains(OpenPatchKey) ? 1 : 0) + (PatchedMethods.Contains(ClosePatchKey) ? 1 : 0)}/2; firstCoveredGeneration={(_firstCoveredBusinessGeneration == long.MaxValue ? "pending" : _firstCoveredBusinessGeneration)}";
            }

            var desired = Volatile.Read(ref _desiredTargetSet);
            lock (BindingRoot)
            {
                var panel = _activePanel;
                var bindings = string.Join(",", ActiveBindings.OrderBy(pair => pair.Key).Select(pair =>
                {
                    var binding = pair.Value;
                    return $"{pair.Key}:target:{binding.TargetGeneration}/business:{binding.BusinessGeneration}/kind:{binding.OrderKind}/order:{FormatPointer(binding.OrderPointer)}/controller:{FormatPointer(binding.ControllerPointer)}/button:{FormatPointer(binding.ButtonPointer)}/fill:{FormatPointer(binding.OwnedVisual?.ImagePointer ?? 0)}";
                }));
                return $"{_state}; {hookStatus}; desired={desired.Generation}/session:{desired.SessionGeneration}/targets:{desired.Targets.Count}; panel={(panel == null ? "none" : $"business:{panel.BusinessGeneration}/thread:{panel.ThreadId}/ptr:{FormatPointer(panel.PanelPointer)}")}; bindings={bindings}; suspended={_suspended}:{NormalizeStatus(_suspendReason)}; callbacks=openPrefix:{_openPrefixes},openPostfix:{_openPostfixes},closePrefix:{_closePrefixes}; registrations={_registeredPanels}; visuals=created:{_createdVisuals},rebuilt:{_rebuiltVisuals},destroyed:{_destroyedVisuals},abandoned:{_abandonedVisuals}; errors=binding:{_bindingErrors},visual:{_visualErrors}; warnings={_warningLogs}/{MaxWarningLogsPerBusiness}; lastFailure={NormalizeStatus(_lastFailure)}";
            }
        }
    }

    public static void Attach(ManualLogSource log)
    {
        _log = log;
        TryAttach(force: true);
    }

    public static void UpdateTargets(RuntimeUiTargetSetSnapshot targetSet)
    {
        ArgumentNullException.ThrowIfNull(targetSet);
        lock (TargetRoot)
        {
            Volatile.Write(ref _desiredTargetSet, targetSet);
        }
    }

    public static void Tick()
    {
        TryAttach(force: false);
        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            NotePanelError($"health check failed: {ex.GetBaseException().Message}");
        }
    }

    public static void Suspend(string reason)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        lock (BindingRoot)
        {
            if (Environment.CurrentManagedThreadId == lifecycle.ThreadId)
            {
                ClearPanelLocked(destroyOwnedVisual: true, "service suspended");
            }
            else
            {
                ClearPanelLocked(destroyOwnedVisual: false, "service suspended off main thread");
            }
            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            _state = HasOrderHighlightTargets(Volatile.Read(ref _desiredTargetSet))
                ? $"suspended: {_suspendReason}"
                : "disabled";
        }
    }

    public static void Resume(string reason)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        lock (BindingRoot)
        {
            if (Environment.CurrentManagedThreadId == lifecycle.ThreadId)
            {
                ClearPanelLocked(destroyOwnedVisual: true, "service resumed");
            }
            else
            {
                ClearPanelLocked(destroyOwnedVisual: false, "service resumed off main thread");
            }
            _suspended = false;
            _suspendReason = NormalizeReason(reason);
            ResetBusinessLogBudgetLocked(lifecycle.Generation);
            _state = HasOrderHighlightTargets(Volatile.Read(ref _desiredTargetSet))
                ? "waiting for exact throw-delivery panel"
                : "disabled";
        }
    }

    public static void Abandon(string reason)
    {
        lock (BindingRoot)
        {
            ClearPanelLocked(destroyOwnedVisual: false, "service abandoned");
            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            _state = $"abandoned: {_suspendReason}";
        }
    }

    public static void Dispose(string reason)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        lock (BindingRoot)
        {
            if (Environment.CurrentManagedThreadId == lifecycle.ThreadId)
            {
                ClearPanelLocked(destroyOwnedVisual: true, "service disposed");
            }
            else
            {
                ClearPanelLocked(destroyOwnedVisual: false, "service disposed off main thread");
            }
            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            _state = $"disposed: {_suspendReason}";
        }
    }

    private static void TickCore()
    {
        ActivePanelRegistration? panel;
        lock (BindingRoot)
        {
            if (_suspended || _activePanel == null) return;
            panel = _activePanel;
        }

        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        var isMainThread = Environment.CurrentManagedThreadId == lifecycle.ThreadId;
        var panelIsLive = TryReadLivePanelPointer(
            panel.Panel,
            out var currentPanelPointer,
            out _)
            && currentPanelPointer == panel.PanelPointer;
        if (!lifecycle.IsActive
            || !isMainThread
            || lifecycle.Generation != panel.BusinessGeneration
            || lifecycle.ThreadId != panel.ThreadId
            || !panelIsLive)
        {
            ClearRegisteredPanel(
                "registered throw-delivery panel lost business or native ownership",
                destroyOwnedVisual: lifecycle.IsActive && isMainThread && panelIsLive);
            return;
        }

        var targetSet = Volatile.Read(ref _desiredTargetSet);
        var orderTargets = targetSet.Targets.Where(target => target.OrderHighlightEnabled).ToArray();
        lock (BindingRoot)
        {
            if (!ReferenceEquals(_activePanel, panel)) return;
            if (orderTargets.Length == 0)
            {
                ClearAllActiveBindingsLocked(destroyOwnedVisual: true, "target disabled");
                _state = "registered: target disabled";
                return;
            }
            if (targetSet.SessionGeneration != lifecycle.Generation)
            {
                ClearAllActiveBindingsLocked(destroyOwnedVisual: true, "target business generation changed");
                _state = "waiting: target belongs to a different business session";
                return;
            }

            foreach (var (kind, active) in ActiveBindings.ToList())
            {
                if (!targetSet.TryGetTarget(kind, out var target)
                    || !target.OrderHighlightEnabled
                    || !active.HasSameTarget(targetSet, target)
                    || active.BusinessGeneration != lifecycle.Generation
                    || active.PanelPointer != panel.PanelPointer)
                {
                    ClearActiveBindingLocked(kind, destroyOwnedVisual: true, "target identity changed");
                    NextBindAttemptAt[kind] = 0f;
                }
            }
        }

        var now = Time.realtimeSinceStartup;
        var pending = new List<(RuntimeUiTargetSnapshot Target, ActiveTargetBinding? Active)>();
        foreach (var target in orderTargets)
        {
            lock (BindingRoot)
            {
                if (!ActiveBindings.TryGetValue(target.Kind, out var active))
                {
                    if (NextBindAttemptAt.TryGetValue(target.Kind, out var retryAt) && now < retryAt) continue;
                    pending.Add((target, null));
                    continue;
                }
                if (!TryApplyPulseLocked(
                        active,
                        targetSet.Palette,
                        out var pulseFailure,
                        out var abandonOwnedVisual))
                {
                    HandleActiveVisualFailureLocked(
                        target.Kind,
                        $"active throw-delivery pulse failed: {pulseFailure}",
                        abandonOwnedVisual);
                    continue;
                }
                if (now < active.NextHealthCheckAt) continue;
                active.NextHealthCheckAt = now + HealthCheckIntervalSeconds;
                pending.Add((target, active));
            }
        }

        if (pending.Count > 0)
        {
            if (!TryReadPanelDirectoryEvidence(panel.Panel, out var directory, out var directoryFailure))
            {
                NotePanelError($"throw-delivery directory changed: {directoryFailure}");
                return;
            }

            foreach (var work in pending)
            {
                if (work.Active == null)
                {
                    TryObserveTarget(panel, lifecycle, targetSet, work.Target, directory);
                    continue;
                }

                TryReconcileActiveTarget(
                    panel,
                    lifecycle,
                    targetSet,
                    work.Target,
                    directory,
                    work.Active);
            }
        }

        lock (BindingRoot)
        {
            if (!ReferenceEquals(_activePanel, panel)) return;
            _state = ActiveBindings.Count == 0
                ? "waiting: exact throw-delivery target buttons"
                : $"active:{ActiveBindings.Count}/{orderTargets.Length}";
            if (ActiveBindings.Count == orderTargets.Length) _lastFailure = "";
        }
    }

    private static bool TryReconcileActiveTarget(
        ActivePanelRegistration panel,
        NightBusinessLifecycleSnapshot lifecycle,
        RuntimeUiTargetSetSnapshot targetSet,
        RuntimeUiTargetSnapshot target,
        PanelDirectoryEvidence directory,
        ActiveTargetBinding active)
    {
        lock (BindingRoot)
        {
            if (!ReferenceEquals(_activePanel, panel)
                || !ActiveBindings.TryGetValue(target.Kind, out var currentBinding)
                || !ReferenceEquals(currentBinding, active)) return false;
        }

        if (!RuntimeUiTargetOrderResolver.TryResolveCurrentCapture(
                target,
                CaptureMaxAge,
                out var capture,
                out var captureFailure)
            || capture == null)
        {
            NoteBindingFailure(target.Kind, $"active throw-delivery capture changed: {captureFailure}");
            return false;
        }
        if (capture.Value.OrderPointer != active.OrderPointer
            || capture.Value.ControllerPointer != active.ControllerPointer
            || capture.Value.OrderLifecycleSequence != active.OrderLifecycleSequence)
        {
            NoteBindingFailure(
                target.Kind,
                "active throw-delivery capture changed exact order, controller, or lifecycle identity");
            return false;
        }
        if (!TryReadTargetPanelEvidence(directory, target, out var current, out var evidenceFailure))
        {
            NoteBindingFailure(target.Kind, $"active throw-delivery identity changed: {evidenceFailure}");
            return false;
        }
        if (!active.HasSameIdentity(current))
        {
            NoteBindingFailure(
                target.Kind,
                "active throw-delivery panel identity no longer matches the bound target");
            return false;
        }
        if (!TryReadVisualSourceEvidence(current, active.OwnedVisual?.OwnerPointer ?? 0, out var visualSource, out var sourceFailure))
        {
            var ownedDead = active.OwnedVisual == null || !IsLiveUnityObject(active.OwnedVisual.Owner);
            HandleActiveVisualFailure(target.Kind, $"active throw-delivery visual source changed: {sourceFailure}", ownedDead);
            return false;
        }

        lock (BindingRoot)
        {
            if (!ReferenceEquals(_activePanel, panel)
                || !ActiveBindings.TryGetValue(target.Kind, out var currentBinding)
                || !ReferenceEquals(currentBinding, active)
                || Volatile.Read(ref _desiredTargetSet).Generation != targetSet.Generation)
            {
                return false;
            }
            if (!active.HasSameVisualSourceIdentity(visualSource))
            {
                HandleActiveVisualFailureLocked(target.Kind, "active throw-delivery selection or background identity changed", false);
                return false;
            }
            if (active.ActiveBackgroundImagePointer != visualSource.ActiveBackgroundImagePointer
                && !TryRebuildOwnedVisualLocked(active, visualSource, targetSet.Palette, out var rebuildFailure))
            {
                HandleActiveVisualFailureLocked(target.Kind, $"active throw-delivery background switch rebuild failed: {rebuildFailure}", active.OwnedVisual == null || !IsLiveUnityObject(active.OwnedVisual.Owner));
                return false;
            }
            if (!TryValidateOwnedVisual(active, visualSource, out var ownedFailure, out var abandonOwnedVisual))
            {
                HandleActiveVisualFailureLocked(target.Kind, $"active throw-delivery owned fill failed validation: {ownedFailure}", abandonOwnedVisual);
                return false;
            }
        }
        return true;
    }

    private static void TryAttach(bool force)
    {
        lock (PatchRoot)
        {
            if (PatchedMethods.Contains(OpenPatchKey) && PatchedMethods.Contains(ClosePatchKey)) return;
            if (!force && DateTime.UtcNow - _lastAttachAttemptUtc < AttachRetryInterval) return;
            _lastAttachAttemptUtc = DateTime.UtcNow;
        }

        try
        {
            var panelType = RuntimeReflectionUtility.FindType(ThrowDeliverPanelTypeName);
            if (panelType == null)
            {
                if (force)
                {
                    TryLogWarning("Runtime throw-delivery order highlight is waiting for the exact panel type.");
                }
                return;
            }

            if (!TryResolveExactMembers(panelType, out var members, out var memberFailure))
            {
                SetAttachFailure(memberFailure, force);
                return;
            }

            lock (PatchRoot) _members = members;
            _harmony ??= new Harmony(
                "com.tyukki.mystia-steward-companion.runtime-throw-delivery-order-highlight");
            var patchedNow = new List<string>();
            var failures = new List<string>();
            TryPatchOpen(_harmony, panelType, patchedNow, failures);
            TryPatchClose(_harmony, panelType, patchedNow, failures);

            lock (PatchRoot)
            {
                if (_firstCoveredBusinessGeneration == long.MaxValue
                    && PatchedMethods.Contains(OpenPatchKey)
                    && PatchedMethods.Contains(ClosePatchKey))
                {
                    _firstCoveredBusinessGeneration = checked(RuntimeNightBusinessLifecycle.Generation + 1);
                }
            }

            if (patchedNow.Count > 0)
            {
                TryLogInfo($"Runtime throw-delivery order highlight patched: {string.Join(", ", patchedNow)}.");
            }
            if (failures.Count > 0 && force)
            {
                TryLogWarning(
                    $"Runtime throw-delivery order highlight exact hooks are incomplete: {string.Join(" | ", failures)}.");
            }
        }
        catch (Exception ex)
        {
            SetAttachFailure(ex.GetBaseException().Message, force);
        }
    }

    private static bool TryResolveExactMembers(
        Type panelType,
        out PanelMembers members,
        out string failure)
    {
        members = null!;
        if (panelType.FullName != ThrowDeliverPanelTypeName
            || !typeof(Component).IsAssignableFrom(panelType))
        {
            failure = "throw-delivery panel type is not the verified Unity Component";
            return false;
        }

        var data = FindUniqueProperty(panelType, "m_Data", declaredOnly: true);
        var buttonInstances = FindUniqueProperty(panelType, "m_BtnInstances", declaredOnly: true);
        var group = FindUniqueProperty(panelType, "m_Group", declaredOnly: true);
        var buttonField = FindUniqueProperty(panelType, "m_BtnField", declaredOnly: true);
        if (!IsExactGeneratedFieldProperty(data)
            || !IsExactGeneratedFieldProperty(buttonInstances)
            || !IsExactGeneratedFieldProperty(buttonField))
        {
            failure = "throw-delivery panel generated field properties are incomplete";
            return false;
        }
        if (!IsExactGeneratedFieldProperty(group))
        {
            failure = $"m_Group generated field property is missing or invalid: property={DescribeProperty(group)}";
            return false;
        }

        if (!TryGetClosedGenericArguments(
                data!.PropertyType,
                Il2CppDictionaryTypeName,
                2,
                out var dictionaryArguments)
            || dictionaryArguments[0] != typeof(int)
            || !TryGetClosedGenericArguments(
                dictionaryArguments[1],
                Il2CppValueTupleTypeName,
                3,
                out var tupleArguments)
            || tupleArguments[0].FullName != Vector3TypeName
            || tupleArguments[1].FullName != RuntimeOrderTypeResolver.OrderBaseTypeName
            || tupleArguments[2].FullName != GuestControllerTypeName)
        {
            failure = "m_Data does not match Dictionary<int, ValueTuple<Vector3, OrderBase, GuestGroupController>>";
            return false;
        }

        if (!TryGetClosedGenericArguments(
                buttonInstances!.PropertyType,
                Il2CppListTypeName,
                1,
                out var buttonArguments)
            || buttonArguments[0].FullName != GameObjectTypeName)
        {
            failure = "m_BtnInstances does not match the verified concrete GameObject list";
            return false;
        }

        if (!TryGetClosedGenericArguments(
                group!.PropertyType,
                UiLogicalGroupTypeName,
                1,
                out var groupArguments)
            || groupArguments[0] != typeof(int))
        {
            failure = $"m_Group does not match the verified UILogicalGroupT<int>: actual={DescribeType(group.PropertyType)}";
            return false;
        }
        if (buttonField!.PropertyType != typeof(RectTransform))
        {
            failure = "m_BtnField does not match the verified RectTransform";
            return false;
        }

        var tupleType = dictionaryArguments[1];
        var tupleItem2 = FindUniqueProperty(tupleType, "Item2", declaredOnly: true);
        var tupleItem3 = FindUniqueProperty(tupleType, "Item3", declaredOnly: true);
        if (!IsExactReadableProperty(tupleItem2, tupleArguments[1])
            || !IsExactReadableProperty(tupleItem3, tupleArguments[2]))
        {
            failure = "m_Data tuple Item2/Item3 getters do not match the verified types";
            return false;
        }

        var orderDeskCode = FindUniqueProperty(tupleArguments[1], "DeskCode", declaredOnly: true);
        var controllerDeskCode = FindUniqueProperty(tupleArguments[2], "DeskCode", declaredOnly: true);
        if (!IsExactIntGetter(orderDeskCode, setterRequired: false)
            || !IsExactIntGetter(controllerDeskCode, setterRequired: true))
        {
            failure = "OrderBase/GuestGroupController DeskCode properties changed shape";
            return false;
        }

        var groupChildren = FindUniqueProperty(group.PropertyType, "m_Children", declaredOnly: true);
        if (!IsExactGeneratedFieldProperty(groupChildren)
            || !TryGetClosedGenericArguments(
                groupChildren!.PropertyType,
                AlignedListTypeName,
                2,
                out var alignedArguments)
            || alignedArguments[0] != typeof(int)
            || alignedArguments[1].FullName != UiLogicalUnitTypeName
            || !typeof(Component).IsAssignableFrom(alignedArguments[1]))
        {
            failure = $"UILogicalGroupT<int>.m_Children does not match AlignedList<int, UILogicalUnit>: group={DescribeType(group.PropertyType)}; property={DescribeProperty(groupChildren)}";
            return false;
        }

        var tryGetValueCandidates = groupChildren.PropertyType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == "TryGetValue")
            .ToArray();
        var tryGetValue = tryGetValueCandidates
            .Where(method => method.ReturnType == typeof(bool)
                && !method.IsGenericMethod)
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType == typeof(int)
                    && parameters[1].IsOut
                    && parameters[1].ParameterType == alignedArguments[1].MakeByRefType();
            })
            .ToArray();
        if (tryGetValue.Length != 1)
        {
            failure = $"AlignedList<int, UILogicalUnit>.TryGetValue(int, out UILogicalUnit) is not unique: actual={DescribeType(groupChildren.PropertyType)}; candidates={tryGetValueCandidates.Length}; exact={tryGetValue.Length}; signatures={DescribeMethodCandidates(tryGetValueCandidates)}";
            return false;
        }

        var unitRectTransform = FindUniqueProperty(
            alignedArguments[1],
            "RectTransform",
            declaredOnly: true);
        if (!IsExactReadableProperty(unitRectTransform, typeof(RectTransform)))
        {
            failure = "UILogicalUnit.RectTransform does not match the verified getter";
            return false;
        }

        if (!TryResolveSelectionEventSerializedMembers(
                alignedArguments[1],
                out var selectionUpdateEvent,
                out var persistentEventSerializedMembers,
                out var selectionEventFailure))
        {
            failure = selectionEventFailure;
            return false;
        }

        var canvasRendererType = RuntimeReflectionUtility.FindType(CanvasRendererTypeName);
        var imageType = RuntimeReflectionUtility.FindType(ImageTypeName);
        var layoutGroupType = RuntimeReflectionUtility.FindType(LayoutGroupTypeName);
        if (canvasRendererType == null
            || canvasRendererType.FullName != CanvasRendererTypeName
            || !typeof(Component).IsAssignableFrom(canvasRendererType)
            || imageType == null
            || imageType.FullName != ImageTypeName
            || !typeof(Component).IsAssignableFrom(imageType)
            || layoutGroupType == null
            || layoutGroupType.FullName != LayoutGroupTypeName
            || !typeof(Component).IsAssignableFrom(layoutGroupType)
            || !TryResolveImageAccessors(imageType, out var imageAccessors))
        {
            failure = "Unity UI visual types do not match the verified clone accessors";
            return false;
        }

        members = new PanelMembers(
            data,
            buttonInstances,
            group,
            buttonField,
            tupleType,
            tupleItem2!,
            tupleItem3!,
            orderDeskCode!,
            controllerDeskCode!,
            groupChildren,
            tryGetValue[0],
            alignedArguments[1],
            unitRectTransform!,
            selectionUpdateEvent,
            persistentEventSerializedMembers,
            canvasRendererType,
            imageType,
            layoutGroupType,
            imageAccessors);
        failure = "";
        return true;
    }

    private static PropertyInfo? FindUniqueProperty(
        Type type,
        string name,
        bool declaredOnly)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        if (declaredOnly) flags |= BindingFlags.DeclaredOnly;
        var matches = type
            .GetProperties(flags)
            .Where(property => property.Name == name && property.GetIndexParameters().Length == 0)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string DescribeProperty(PropertyInfo? property)
    {
        if (property == null) return "missing";
        return LimitAttachDiagnostic(
            $"{property.Name}:{DescribeType(property.PropertyType)}; getter={property.GetMethod != null}; setter={property.SetMethod != null}");
    }

    private static string DescribeMethodCandidates(IReadOnlyList<MethodInfo> candidates)
    {
        if (candidates.Count == 0) return "none";
        const int maxCandidates = 6;
        var descriptions = candidates
            .Take(maxCandidates)
            .Select(method =>
            {
                var parameters = method.GetParameters();
                var parameterDescription = string.Join(",", parameters.Select(parameter =>
                    (parameter.IsOut ? "out " : "") + DescribeType(parameter.ParameterType)));
                return $"{DescribeType(method.ReturnType)} {method.Name}({parameterDescription})";
            });
        var joined = string.Join(" | ", descriptions);
        if (candidates.Count > maxCandidates) joined += " | ...";
        return LimitAttachDiagnostic(joined);
    }

    private static string DescribeType(Type? type)
    {
        if (type == null) return "missing";
        var name = type.FullName ?? type.Name;
        name = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return name.Length <= 180 ? name : name[..180] + "...";
    }

    private static string LimitAttachDiagnostic(string value)
    {
        const int maxLength = 720;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static bool IsExactGeneratedFieldProperty(PropertyInfo? property)
    {
        return property?.GetMethod?.IsStatic == false
            && property.SetMethod?.IsStatic == false
            && property.GetIndexParameters().Length == 0;
    }

    private static bool IsExactReadableProperty(PropertyInfo? property, Type expectedType)
    {
        return property?.PropertyType == expectedType
            && property.GetMethod?.IsStatic == false
            && property.GetIndexParameters().Length == 0;
    }

    private static bool IsExactIntGetter(PropertyInfo? property, bool setterRequired)
    {
        return property?.PropertyType == typeof(int)
            && property.GetMethod?.IsStatic == false
            && (!setterRequired || property.SetMethod?.IsStatic == false)
            && (setterRequired || property.SetMethod == null)
            && property.GetIndexParameters().Length == 0;
    }

    private static bool TryGetClosedGenericArguments(
        Type type,
        string expectedDefinitionName,
        int expectedCount,
        out Type[] arguments)
    {
        arguments = Type.EmptyTypes;
        try
        {
            if (!type.IsGenericType
                || type.ContainsGenericParameters
                || type.GetGenericTypeDefinition().FullName != expectedDefinitionName)
            {
                return false;
            }
            arguments = type.GetGenericArguments();
            return arguments.Length == expectedCount;
        }
        catch
        {
            arguments = Type.EmptyTypes;
            return false;
        }
    }

    private static bool TryResolveSelectionEventSerializedMembers(
        Type uiLogicalUnitType,
        out PropertyInfo selectionUpdateEvent,
        out PersistentEventSerializedMembers serializedMembers,
        out string failure)
    {
        selectionUpdateEvent = null!;
        serializedMembers = null!;
        var candidateEvent = FindUniqueProperty(
            uiLogicalUnitType,
            "m_OnSelectionUpdateCallback",
            declaredOnly: true);
        if (!IsExactGeneratedFieldProperty(candidateEvent))
        {
            failure = $"UILogicalUnit.m_OnSelectionUpdateCallback generated field property is missing or invalid: property={DescribeProperty(candidateEvent)}";
            return false;
        }

        var eventType = candidateEvent!.PropertyType;
        if (eventType.FullName != SelectionEventTypeName
            || eventType.IsGenericType
            || eventType.ContainsGenericParameters)
        {
            failure = $"UILogicalUnit.m_OnSelectionUpdateCallback is not the exact custom UnityEvent_Bool subtype: actual={DescribeType(eventType)}";
            return false;
        }

        var closedUnityEventType = eventType.BaseType;
        if (closedUnityEventType == null
            || !TryGetClosedGenericArguments(
                closedUnityEventType,
                UnityEventTypeName,
                1,
                out var selectionEventArguments)
            || selectionEventArguments[0] != typeof(bool))
        {
            failure = $"UnityEvent_Bool direct base is not the exact closed UnityEvent<bool>: actual={DescribeType(closedUnityEventType)}";
            return false;
        }

        var unityEventBaseType = closedUnityEventType.BaseType;
        if (unityEventBaseType?.FullName != UnityEventBaseTypeName)
        {
            failure = $"closed UnityEvent<bool> direct base is not UnityEventBase: actual={DescribeType(unityEventBaseType)}";
            return false;
        }

        var persistentCalls = FindUniqueProperty(
            unityEventBaseType,
            "m_PersistentCalls",
            declaredOnly: true);
        if (!IsExactGeneratedFieldProperty(persistentCalls)
            || persistentCalls!.PropertyType.FullName != PersistentCallGroupTypeName)
        {
            failure = $"UnityEventBase.m_PersistentCalls does not match the exact PersistentCallGroup generated field: property={DescribeProperty(persistentCalls)}";
            return false;
        }

        var persistentCallGroupType = persistentCalls.PropertyType;
        var calls = FindUniqueProperty(
            persistentCallGroupType,
            "m_Calls",
            declaredOnly: true);
        if (!IsExactGeneratedFieldProperty(calls)
            || !TryGetClosedGenericArguments(
                calls!.PropertyType,
                Il2CppListTypeName,
                1,
                out var callListArguments)
            || callListArguments[0].FullName != PersistentCallTypeName)
        {
            failure = $"PersistentCallGroup.m_Calls does not match the exact List<PersistentCall> generated field: property={DescribeProperty(calls)}";
            return false;
        }

        var callsCount = FindUniqueProperty(calls.PropertyType, "Count", declaredOnly: false);
        if (!IsExactIntGetter(callsCount, setterRequired: false))
        {
            failure = $"List<PersistentCall>.Count does not match the exact read-only int shape: property={DescribeProperty(callsCount)}";
            return false;
        }

        var persistentCallType = callListArguments[0];
        var target = FindUniqueProperty(persistentCallType, "m_Target", declaredOnly: true);
        if (!IsExactGeneratedFieldProperty(target)
            || target!.PropertyType != typeof(UnityEngine.Object))
        {
            failure = $"PersistentCall.m_Target does not match the exact UnityEngine.Object generated field: property={DescribeProperty(target)}";
            return false;
        }

        var methodName = FindUniqueProperty(
            persistentCallType,
            "m_MethodName",
            declaredOnly: true);
        if (!IsExactGeneratedFieldProperty(methodName)
            || methodName!.PropertyType != typeof(string))
        {
            failure = $"PersistentCall.m_MethodName does not match the exact string generated field: property={DescribeProperty(methodName)}";
            return false;
        }

        selectionUpdateEvent = candidateEvent;
        serializedMembers = new PersistentEventSerializedMembers(
            persistentCalls,
            calls,
            callsCount!,
            persistentCallType,
            target,
            methodName);
        failure = "";
        return true;
    }

    private static bool TryResolveImageAccessors(
        Type imageType,
        out ImageAccessors accessors)
    {
        accessors = null!;
        var methods = imageType.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var imageRenderType = imageType.GetNestedType(
            "Type",
            BindingFlags.Public | BindingFlags.NonPublic);
        var getRaycastTarget = SingleMethod(
            methods,
            "get_raycastTarget",
            Type.EmptyTypes,
            typeof(bool));
        var getEnabled = SingleMethod(methods, "get_enabled", Type.EmptyTypes, typeof(bool));
        var getSprite = SingleMethod(
            methods,
            "get_sprite",
            Type.EmptyTypes,
            typeof(Sprite));
        var getMaterial = SingleMethod(
            methods,
            "get_material",
            Type.EmptyTypes,
            typeof(Material));
        var getImageType = imageRenderType?.FullName == ImageRenderTypeName
            && imageRenderType.IsEnum
                ? SingleMethod(
                    methods,
                    "get_type",
                    Type.EmptyTypes,
                    imageRenderType)
                : null;
        var setColor = SingleMethod(methods, "set_color", new[] { typeof(Color) }, typeof(void));
        var setRaycastTarget = SingleMethod(
            methods,
            "set_raycastTarget",
            new[] { typeof(bool) },
            typeof(void));
        var setEnabled = SingleMethod(methods, "set_enabled", new[] { typeof(bool) }, typeof(void));
        if (getRaycastTarget == null
            || getEnabled == null
            || getSprite == null
            || getMaterial == null
            || getImageType == null
            || setColor == null
            || setRaycastTarget == null
            || setEnabled == null)
        {
            return false;
        }
        accessors = new ImageAccessors(
            getRaycastTarget,
            getEnabled,
            getSprite,
            getMaterial,
            getImageType,
            setColor,
            setRaycastTarget,
            setEnabled);
        return true;
    }

    private static MethodInfo? SingleMethod(
        IEnumerable<MethodInfo> methods,
        string name,
        IReadOnlyList<Type> parameterTypes,
        Type returnType)
    {
        var matches = methods.Where(method =>
        {
            if (method.Name != name
                || method.IsGenericMethod
                || method.IsStatic
                || method.ReturnType != returnType)
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

    private static void TryPatchOpen(
        Harmony harmony,
        Type panelType,
        ICollection<string> patchedNow,
        ICollection<string> failures)
    {
        lock (PatchRoot)
        {
            if (PatchedMethods.Contains(OpenPatchKey)) return;
        }

        var target = FindExactPanelLifecycleMethod(panelType, "OnPanelOpen");
        var prefix = typeof(RuntimeThrowDeliverOrderHighlightService).GetMethod(
            nameof(BeforePanelOpen),
            BindingFlags.NonPublic | BindingFlags.Static);
        var postfix = typeof(RuntimeThrowDeliverOrderHighlightService).GetMethod(
            nameof(AfterPanelOpen),
            BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || prefix == null || postfix == null)
        {
            failures.Add(OpenPatchKey);
            return;
        }

        harmony.Patch(
            target,
            prefix: new HarmonyMethod(prefix) { priority = Priority.First },
            postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
        lock (PatchRoot) PatchedMethods.Add(OpenPatchKey);
        patchedNow.Add(OpenPatchKey);
    }

    private static void TryPatchClose(
        Harmony harmony,
        Type panelType,
        ICollection<string> patchedNow,
        ICollection<string> failures)
    {
        lock (PatchRoot)
        {
            if (PatchedMethods.Contains(ClosePatchKey)) return;
        }

        var target = FindExactPanelLifecycleMethod(panelType, "OnPanelClose");
        var prefix = typeof(RuntimeThrowDeliverOrderHighlightService).GetMethod(
            nameof(BeforePanelClose),
            BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || prefix == null)
        {
            failures.Add(ClosePatchKey);
            return;
        }

        harmony.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
        lock (PatchRoot) PatchedMethods.Add(ClosePatchKey);
        patchedNow.Add(ClosePatchKey);
    }

    private static MethodInfo? FindExactPanelLifecycleMethod(Type panelType, string methodName)
    {
        var matches = panelType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == methodName
                && method.ReturnType == typeof(void)
                && method.IsVirtual
                && !method.IsGenericMethod
                && method.GetParameters().Length == 0)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static void BeforePanelOpen(object __instance, out PanelOpenEvidence? __state)
    {
        __state = null;
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        try
        {
            var onActiveUnityThread = lifecycle.IsActive
                && Environment.CurrentManagedThreadId == lifecycle.ThreadId;
            lock (BindingRoot)
            {
                _openPrefixes++;
                ClearPanelLocked(
                    destroyOwnedVisual: onActiveUnityThread,
                    "panel rebuilding");
                _state = "observing throw-delivery panel open";
            }

            if (!onActiveUnityThread)
            {
                return;
            }

            if (!TryReadLivePanelPointer(__instance, out var panelPointer, out var failure))
            {
                __state = new PanelOpenEvidence(
                    lifecycle.Generation,
                    lifecycle.ThreadId,
                    0,
                    failure);
                return;
            }

            __state = new PanelOpenEvidence(
                lifecycle.Generation,
                lifecycle.ThreadId,
                panelPointer,
                "");
        }
        catch (Exception ex)
        {
            __state = new PanelOpenEvidence(
                lifecycle.Generation,
                Environment.CurrentManagedThreadId,
                0,
                ex.GetBaseException().Message);
        }
    }

    private static void AfterPanelOpen(object __instance, PanelOpenEvidence? __state)
    {
        lock (BindingRoot) _openPostfixes++;

        try
        {
            var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
            lock (BindingRoot)
            {
                if (_suspended)
                {
                    _state = $"suspended: {_suspendReason}";
                    return;
                }
            }

            if (!lifecycle.IsActive
                || Environment.CurrentManagedThreadId != lifecycle.ThreadId
                || !IsBusinessReady(lifecycle.Generation))
            {
                SetPanelWaitingState("throw-delivery panel is outside the exact covered business session");
                return;
            }
            if (__state == null || !string.IsNullOrEmpty(__state.Failure))
            {
                NotePanelError(
                    $"throw-delivery prefix identity was unavailable: {__state?.Failure ?? "missing prefix state"}");
                return;
            }
            if (__state.BusinessGeneration != lifecycle.Generation
                || __state.ThreadId != lifecycle.ThreadId)
            {
                NotePanelError("night-business identity changed during throw-delivery panel open");
                return;
            }
            if (!TryReadLivePanelPointer(__instance, out var panelPointer, out var pointerFailure)
                || panelPointer != __state.PanelPointer)
            {
                NotePanelError($"throw-delivery panel identity changed during open: {pointerFailure}");
                return;
            }

            var registration = new ActivePanelRegistration(
                __instance,
                lifecycle.Generation,
                lifecycle.ThreadId,
                panelPointer);
            lock (BindingRoot)
            {
                if (_suspended) return;
                ResetBusinessLogBudgetLocked(lifecycle.Generation);
                _activePanel = registration;
                ActiveBindings.Clear();
                NextBindAttemptAt.Clear();
                _state = "registered: reconciling target";
                _lastFailure = "";
                _registeredPanels++;
            }

            ReconcileRegisteredPanel(registration);
        }
        catch (Exception ex)
        {
            NotePanelError($"throw-delivery open handling failed: {ex.GetBaseException().Message}");
        }
    }

    private static void BeforePanelClose(object __instance)
    {
        lock (BindingRoot) _closePrefixes++;
        try
        {
            if (!TryReadLivePanelPointer(
                    __instance,
                    out var closingPanelPointer,
                    out var pointerFailure))
            {
                TryLogBoundedFailure(
                    "panel-close",
                    $"closing panel identity was unavailable: {pointerFailure}");
                return;
            }

            var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
            lock (BindingRoot)
            {
                if (_activePanel?.PanelPointer != closingPanelPointer) return;

                ClearPanelLocked(
                    destroyOwnedVisual: lifecycle.IsActive
                        && Environment.CurrentManagedThreadId == lifecycle.ThreadId,
                    "panel closing");
                _state = HasOrderHighlightTargets(Volatile.Read(ref _desiredTargetSet))
                    ? "waiting: throw-delivery panel closed"
                    : "disabled";
            }
        }
        catch (Exception ex)
        {
            TryLogBoundedFailure(
                "panel-close",
                $"throw-delivery close handling failed: {ex.GetBaseException().Message}");
        }
    }

    private static bool HasOrderHighlightTargets(RuntimeUiTargetSetSnapshot targetSet)
    {
        return targetSet.Targets.Any(target => target.OrderHighlightEnabled);
    }

    private static bool TryReadPanelDirectoryEvidence(
        object panel,
        out PanelDirectoryEvidence directory,
        out string failure)
    {
        directory = null!;
        PanelMembers? members;
        lock (PatchRoot) members = _members;
        if (members == null)
        {
            failure = "verified throw-delivery members are unavailable";
            return false;
        }
        if (!TryReadLivePanelPointer(panel, out var panelPointer, out failure)) return false;

        try
        {
            var data = members.Data.GetValue(panel);
            if (data == null || data.GetType() != members.Data.PropertyType)
            {
                failure = "m_Data is null or has a different concrete dictionary type";
                return false;
            }
            if (!RuntimeConcreteCollectionReader.TryReadDictionaryCount(
                    data,
                    out var dataCount,
                    out var countFailure)
                || dataCount <= 0
                || dataCount > MaxPanelButtons)
            {
                failure = $"m_Data count is unavailable or out of range: {countFailure}/{dataCount}";
                return false;
            }
            var group = members.Group.GetValue(panel);
            if (group == null
                || group.GetType() != members.Group.PropertyType
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(group, out var groupPointer))
            {
                failure = "m_Group has no exact closed type or native identity";
                return false;
            }
            var buttonList = members.ButtonInstances.GetValue(panel);
            if (buttonList == null || buttonList.GetType() != members.ButtonInstances.PropertyType)
            {
                failure = "m_BtnInstances is null or has a different concrete list type";
                return false;
            }
            if (!TryReadPoolDirectory(
                    buttonList,
                    out var buttonPointers,
                    out var poolCount,
                    out failure))
            {
                return false;
            }

            if (members.ButtonField.GetValue(panel) is not RectTransform buttonField
                || !IsLiveUnityObject(buttonField)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                    buttonField,
                    out var buttonFieldPointer))
            {
                failure = "m_BtnField has no live exact RectTransform identity";
                return false;
            }
            directory = new PanelDirectoryEvidence(
                data,
                group,
                members,
                panelPointer,
                groupPointer,
                buttonFieldPointer,
                poolCount,
                buttonPointers);
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
    }

    private static bool TryReadTargetPanelEvidence(
        PanelDirectoryEvidence directory,
        RuntimeUiTargetSnapshot target,
        out PanelEvidence evidence,
        out string failure)
    {
        evidence = default;
        try
        {
            if (!RuntimeConcreteCollectionReader.TryGetDictionaryValue(
                    directory.Data,
                    target.DeskCode,
                    out var rawTuple,
                    out var found,
                    out var lookupFailure)
                || !found
                || rawTuple == null)
            {
                failure = $"m_Data has no exact target desk entry: {lookupFailure}";
                return false;
            }
            if (!TryReadDataTuple(
                    rawTuple,
                    target.DeskCode,
                    directory.Members,
                    out var orderPointer,
                    out var controllerPointer,
                    out var orderKind,
                    out failure)
                || !TryReadKeyedLogicalUnit(
                    directory.Group,
                    target.DeskCode,
                    directory.Members,
                    out var unitPointer,
                    out _,
                    out var buttonPointer,
                    out var rectTransform,
                    out var rectTransformPointer,
                    out failure))
            {
                return false;
            }
            if (!directory.ButtonPointers.Contains(buttonPointer))
            {
                failure = "keyed target button has no exact membership in m_BtnInstances";
                return false;
            }
            if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(rectTransform.parent, out var buttonParentPointer)
                || buttonParentPointer != directory.ButtonFieldPointer)
            {
                failure = "keyed target button is not a direct child of m_BtnField";
                return false;
            }

            evidence = new PanelEvidence(
                rectTransform,
                directory.PanelPointer,
                orderPointer,
                controllerPointer,
                directory.GroupPointer,
                unitPointer,
                buttonPointer,
                rectTransformPointer,
                directory.ButtonFieldPointer,
                target.DeskCode,
                orderKind,
                directory.PoolCount);
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
    }

    private static bool TryReadDataTuple(
        object rawTuple,
        int targetDeskCode,
        PanelMembers members,
        out nint orderPointer,
        out nint controllerPointer,
        out RuntimeOrderKind orderKind,
        out string failure)
    {
        orderPointer = 0;
        controllerPointer = 0;
        orderKind = default;
        if (rawTuple.GetType() != members.DataTupleType)
        {
            failure = "m_Data target value has a different concrete tuple type";
            return false;
        }

        var order = members.TupleItem2.GetValue(rawTuple);
        var controller = members.TupleItem3.GetValue(rawTuple);
        if (order == null
            || controller == null)
        {
            failure = "m_Data target tuple has null or mismatched Item2/Item3";
            return false;
        }

        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        if (!resolution.Resolved || resolution.ReadableOrder == null)
        {
            failure = $"m_Data target order type is unresolved: {resolution.Reason}";
            return false;
        }
        if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(order, out orderPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                resolution.ReadableOrder,
                out var readableOrderPointer)
            || readableOrderPointer != orderPointer)
        {
            failure = "m_Data OrderBase and concrete order do not share one native identity";
            orderPointer = 0;
            return false;
        }
        if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(controller, out controllerPointer))
        {
            failure = "m_Data controller has no native identity";
            controllerPointer = 0;
            return false;
        }
        if (members.OrderDeskCode.GetValue(order) is not int orderDeskCode
            || members.ControllerDeskCode.GetValue(controller) is not int controllerDeskCode
            || orderDeskCode < 0
            || controllerDeskCode < 0
            || orderDeskCode != targetDeskCode
            || controllerDeskCode != targetDeskCode)
        {
            failure = "m_Data key and order/controller DeskCode evidence are conflicting";
            return false;
        }

        orderKind = resolution.Kind;
        failure = "";
        return true;
    }

    private static bool TryReadKeyedLogicalUnit(
        object group,
        int deskCode,
        PanelMembers members,
        out nint unitPointer,
        out GameObject button,
        out nint buttonPointer,
        out RectTransform rectTransform,
        out nint rectTransformPointer,
        out string failure)
    {
        unitPointer = 0;
        button = null!;
        buttonPointer = 0;
        rectTransform = null!;
        rectTransformPointer = 0;
        var children = members.GroupChildren.GetValue(group);
        if (children == null || children.GetType() != members.GroupChildren.PropertyType)
        {
            failure = "m_Group.m_Children is null or has a different aligned-list type";
            return false;
        }

        var arguments = new object?[] { deskCode, null };
        var rawFound = members.AlignedTryGetValue.Invoke(children, arguments);
        if (rawFound is not bool found || !found || arguments[1] == null)
        {
            failure = "m_Group.m_Children has no exact target desk UILogicalUnit";
            return false;
        }
        var unit = arguments[1]!;
        if (!members.UiLogicalUnitType.IsInstanceOfType(unit)
            || unit is not Component component
            || !IsLiveUnityObject(component)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(component, out unitPointer))
        {
            failure = "keyed UILogicalUnit has no live exact Component identity";
            return false;
        }

        button = component.gameObject;
        if (!IsLiveUnityObject(button)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(button, out buttonPointer))
        {
            failure = "keyed UILogicalUnit GameObject is not a live button";
            return false;
        }
        if (members.UiLogicalUnitRectTransform.GetValue(unit) is not RectTransform exactRectTransform
            || !IsLiveUnityObject(exactRectTransform)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                exactRectTransform,
                out rectTransformPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                button.transform,
                out var buttonTransformPointer)
            || buttonTransformPointer != rectTransformPointer)
        {
            failure = "keyed UILogicalUnit RectTransform is missing or differs from its GameObject root";
            return false;
        }

        rectTransform = exactRectTransform;
        failure = "";
        return true;
    }

    private static bool TryReadPoolDirectory(
        object buttonList,
        out HashSet<nint> pointers,
        out int poolCount,
        out string failure)
    {
        pointers = new HashSet<nint>();
        poolCount = 0;
        if (!RuntimeConcreteCollectionReader.TryReadList(
                buttonList,
                out var values,
                out var readFailure)
            || values.Count <= 0
            || values.Count > MaxPanelButtons)
        {
            failure = $"m_BtnInstances is unreadable or out of range: {readFailure}/{values.Count}";
            return false;
        }

        foreach (var value in values)
        {
            if (value is not GameObject candidate
                || !IsLiveUnityObject(candidate)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(candidate, out var pointer))
            {
                failure = "m_BtnInstances contains a non-GameObject or dead entry";
                return false;
            }
            if (!pointers.Add(pointer))
            {
                failure = "m_BtnInstances contains duplicate native GameObject identities";
                return false;
            }
        }

        poolCount = values.Count;
        failure = "";
        return true;
    }

    private static bool TryReadLivePanelPointer(
        object panel,
        out nint panelPointer,
        out string failure)
    {
        if (!IsLiveUnityObject(panel)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(panel, out panelPointer))
        {
            panelPointer = 0;
            failure = "throw-delivery panel wrapper has no live native identity";
            return false;
        }
        failure = "";
        return true;
    }

    private static void ReconcileRegisteredPanel(ActivePanelRegistration panel)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycle.IsActive
            || lifecycle.Generation != panel.BusinessGeneration
            || lifecycle.ThreadId != panel.ThreadId
            || Environment.CurrentManagedThreadId != lifecycle.ThreadId)
        {
            ClearRegisteredPanel(
                "registered throw-delivery panel is outside its business session",
                destroyOwnedVisual: Environment.CurrentManagedThreadId == lifecycle.ThreadId);
            return;
        }
        TickCore();
    }

    private static void TryObserveTarget(
        ActivePanelRegistration panel,
        NightBusinessLifecycleSnapshot lifecycle,
        RuntimeUiTargetSetSnapshot targetSet,
        RuntimeUiTargetSnapshot target,
        PanelDirectoryEvidence directory)
    {
        if (!RuntimeUiTargetOrderResolver.TryResolveCurrentCapture(
                target,
                CaptureMaxAge,
                out var capture,
                out var captureFailure)
            || capture == null)
        {
            NoteBindingFailure(target.Kind, $"target capture was unavailable: {captureFailure}");
            return;
        }
        if (!TryReadTargetPanelEvidence(directory, target, out var evidence, out var evidenceFailure))
        {
            NoteBindingFailure(target.Kind, $"target throw-delivery button was unavailable: {evidenceFailure}");
            return;
        }
        if (capture.Value.OrderPointer != evidence.OrderPointer)
        {
            SetBindingWaiting(target.Kind, "target desk contains a different native order");
            return;
        }
        if (capture.Value.ControllerPointer != evidence.ControllerPointer)
        {
            NoteBindingFailure(target.Kind, "target order is associated with a different native controller");
            return;
        }
        var expectedKind = target.Kind == RuntimeUiTargetKind.Rare
            ? RuntimeOrderKind.Special
            : RuntimeOrderKind.Normal;
        if (capture.Value.DeskCode != evidence.DeskCode
            || target.DeskCode != evidence.DeskCode
            || evidence.OrderKind != expectedKind)
        {
            NoteBindingFailure(target.Kind, "target order has a conflicting exact desk code or order kind");
            return;
        }

        if (!TryReadVisualSourceEvidence(
                evidence,
                ownedOwnerPointerToExclude: 0,
                out var visualSource,
                out var visualSourceFailure))
        {
            NoteBindingFailure(target.Kind,
                $"exact throw-delivery visual source was unavailable: {visualSourceFailure}");
            return;
        }

        var latestTargetSet = Volatile.Read(ref _desiredTargetSet);
        var latestLifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (latestTargetSet.Generation != targetSet.Generation
            || latestLifecycle.Generation != lifecycle.Generation
            || !latestLifecycle.IsActive
            || Environment.CurrentManagedThreadId != latestLifecycle.ThreadId)
        {
            SetBindingWaiting(target.Kind, "target or business changed while resolving throw-delivery visual source");
            return;
        }

        if (!TryCreateOwnedVisual(
                visualSource,
                target.Claim,
                targetSet.Palette,
                out var ownedVisual,
                out var creationFailure))
        {
            Interlocked.Increment(ref _visualErrors);
            NoteBindingFailure(target.Kind, $"exact throw-delivery target fill creation failed: {creationFailure}");
            return;
        }

        latestTargetSet = Volatile.Read(ref _desiredTargetSet);
        latestLifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (latestTargetSet.Generation != targetSet.Generation
            || latestLifecycle.Generation != lifecycle.Generation
            || !latestLifecycle.IsActive
            || Environment.CurrentManagedThreadId != latestLifecycle.ThreadId)
        {
            RetireOwnedVisual(
                ownedVisual,
                "target changed during fill creation",
                logRetirement: false);
            SetBindingWaiting(target.Kind, "target or business changed while creating throw-delivery target fill");
            return;
        }

        var binding = new ActiveTargetBinding(
            targetSet.Generation,
            target,
            lifecycle.Generation,
            evidence.PanelPointer,
            evidence.OrderPointer,
            evidence.ControllerPointer,
            evidence.GroupPointer,
            evidence.UnitPointer,
            evidence.ButtonPointer,
            evidence.RectTransformPointer,
            evidence.ButtonFieldPointer,
            evidence.DeskCode,
            evidence.OrderKind,
            evidence.PoolCount,
            capture.Value.OrderLifecycleSequence,
            visualSource.SelectionImagePointer,
            visualSource.BackgroundImagePointer1,
            visualSource.BackgroundImagePointer2,
            visualSource.ActiveBackgroundImagePointer,
            ownedVisual);

        lock (BindingRoot)
        {
            var buttonClaimedByOtherKind = ActiveBindings.Values.Any(active =>
                active.TargetKind != target.Kind
                && active.ButtonPointer == evidence.ButtonPointer);
            if (_suspended
                || !ReferenceEquals(_activePanel, panel)
                || Volatile.Read(ref _desiredTargetSet).Generation != targetSet.Generation
                || buttonClaimedByOtherKind)
            {
                RetireOwnedVisual(
                    ownedVisual,
                    "target changed before fill publication",
                    logRetirement: false);
                if (buttonClaimedByOtherKind)
                {
                    const string collision =
                        "one throw-delivery button is claimed by both target kinds";
                    NextBindAttemptAt[target.Kind] =
                        Time.realtimeSinceStartup + HealthCheckIntervalSeconds;
                    _bindingErrors++;
                    _lastFailure = collision;
                    _state = $"unavailable: {collision}";
                    TryLogBoundedFailure($"binding-{target.Kind}", collision);
                }
                return;
            }

            ClearActiveBindingLocked(target.Kind, destroyOwnedVisual: true, "publishing fresh target fill");
            ActiveBindings[target.Kind] = binding;
            NextBindAttemptAt[target.Kind] = 0f;
            _createdVisuals++;
            TryLogVisualInfo(
                $"Runtime throw-delivery target fill bound: reason=initial; business={lifecycle.Generation}; target={targetSet.Generation}; kind={target.Kind}; panel={FormatPointer(evidence.PanelPointer)}; button={FormatPointer(evidence.ButtonPointer)}; selection={FormatPointer(visualSource.SelectionImagePointer)}; backgrounds={FormatPointer(visualSource.BackgroundImagePointer1)},{FormatPointer(visualSource.BackgroundImagePointer2)}; activeBackground={FormatPointer(visualSource.ActiveBackgroundImagePointer)}; fill={FormatPointer(ownedVisual.OwnerPointer)}; image={FormatPointer(ownedVisual.ImagePointer)}; geometry={visualSource.SelectionGeometryFingerprint}.");
        }
    }

    private static bool TryReadVisualSourceEvidence(
        PanelEvidence evidence,
        nint ownedOwnerPointerToExclude,
        out VisualSourceEvidence source,
        out string failure)
    {
        source = null!;
        PanelMembers? members;
        lock (PatchRoot) members = _members;
        if (members == null)
        {
            failure = "verified throw-delivery visual members are unavailable";
            return false;
        }

        try
        {
            if (!TryReadSelectionEventTarget(
                    evidence,
                    members,
                    out var selection,
                    out failure)
                || !TryValidateNoLayoutGroup(
                    evidence.RectTransform,
                    members.LayoutGroupType,
                    out failure))
            {
                return false;
            }

            var childCount = evidence.RectTransform.childCount;
            if (childCount <= 0 || childCount > MaxDirectButtonChildren)
            {
                failure = $"exact target button direct-child count is out of range: {childCount}";
                return false;
            }

            var backgrounds = new List<LeafImageEvidence>(2);
            var differentImageGeometries = new List<string>(MaxGeometryDiagnosticEntries);
            var selectionMatches = 0;
            var ownedMatches = 0;
            for (var index = 0; index < childCount; index++)
            {
                var rawChild = evidence.RectTransform.GetChild(index);
                if (!IsLiveUnityObject(rawChild)
                    || RuntimeReflectionUtility.TryCastRuntimeObject(
                        rawChild,
                        RectTransformTypeName) is not RectTransform childRect
                    || !IsLiveUnityObject(childRect))
                {
                    failure = $"target button direct child {index} is not an exact live RectTransform";
                    return false;
                }

                var childOwner = childRect.gameObject;
                if (!IsLiveUnityObject(childOwner)
                    || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                        childOwner,
                        out var childOwnerPointer))
                {
                    failure = $"target button direct child {index} has no live GameObject identity";
                    return false;
                }

                if (ownedOwnerPointerToExclude != 0
                    && childOwnerPointer == ownedOwnerPointerToExclude)
                {
                    ownedMatches++;
                    continue;
                }
                if (childOwnerPointer == selection.OwnerPointer)
                {
                    selectionMatches++;
                    if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(
                            childRect,
                            out var selectionRectPointer)
                        || selectionRectPointer != selection.RectPointer)
                    {
                        failure = "selection-event target direct-child RectTransform identity changed";
                        return false;
                    }
                    continue;
                }
                if (!HasSameRectTransformGeometry(childRect, selection.RectTransform))
                {
                    if (differentImageGeometries.Count < MaxGeometryDiagnosticEntries
                        && childOwner.GetComponent(Il2CppType.From(members.ImageType)) != null)
                    {
                        differentImageGeometries.Add(
                            $"{index}:{FormatRectTransformGeometry(childRect)}");
                    }
                    continue;
                }

                var rawImageComponent = childOwner.GetComponent(
                    Il2CppType.From(members.ImageType));
                if (rawImageComponent == null) continue;
                var rawImage = RuntimeReflectionUtility.TryCastRuntimeObject(
                    rawImageComponent,
                    ImageTypeName);
                if (rawImage == null)
                {
                    failure = "same-geometry direct child exposes an Image component that cannot be cast exactly";
                    return false;
                }
                if (!TryReadExactLeafImage(
                        childOwner,
                        childRect,
                        rawImage,
                        members,
                        out var background,
                        out failure))
                {
                    failure = $"same-geometry Image direct child failed exact background shape: {failure}";
                    return false;
                }
                backgrounds.Add(background);
            }

            if (selectionMatches != 1)
            {
                failure = $"selection-event Image matched {selectionMatches} exact direct children";
                return false;
            }
            if (ownedOwnerPointerToExclude != 0 && ownedMatches != 1)
            {
                failure = $"owned target fill matched {ownedMatches} exact direct children";
                return false;
            }
            if (backgrounds.Count != 2)
            {
                var differentGeometry = differentImageGeometries.Count == 0
                    ? "none"
                    : string.Join('|', differentImageGeometries);
                failure = $"selection-event parent has {backgrounds.Count} exact non-selection same-geometry background Images instead of 2; selectionGeometry={FormatRectTransformGeometry(selection.RectTransform)}; differentImageGeometry={differentGeometry}";
                return false;
            }

            var first = backgrounds[0];
            var second = backgrounds[1];
            if (first.OwnerPointer == second.OwnerPointer
                || first.RectPointer == second.RectPointer
                || first.ImagePointer == second.ImagePointer
                || first.SpritePointer == second.SpritePointer
                || first.MaterialPointer != second.MaterialPointer
                || first.ImageTypeValue != second.ImageTypeValue)
            {
                failure = "the two native background Images do not have distinct owner/rect/image/sprite identities, one shared material, and the same exact Image render type";
                return false;
            }
            if (!first.ActiveSelf
                || !first.ActiveInHierarchy
                || !second.ActiveSelf
                || !second.ActiveInHierarchy)
            {
                failure = "the two native background owners are not both active";
                return false;
            }
            if (first.Enabled == second.Enabled)
            {
                failure = $"the two native background Images do not have an enabled XOR state: {first.Enabled}/{second.Enabled}";
                return false;
            }

            var selectionSibling = selection.RectTransform.GetSiblingIndex();
            var firstSibling = first.RectTransform.GetSiblingIndex();
            var secondSibling = second.RectTransform.GetSiblingIndex();
            if (firstSibling < 0
                || secondSibling < 0
                || selectionSibling < 0
                || firstSibling == secondSibling
                || firstSibling >= selectionSibling
                || secondSibling >= selectionSibling)
            {
                failure = $"native backgrounds are not both ordered before the exact selection target: backgrounds={firstSibling},{secondSibling}; selection={selectionSibling}";
                return false;
            }

            var ordered = backgrounds
                .OrderBy(item => item.ImagePointer)
                .ToArray();
            var activeBackground = first.Enabled ? first : second;
            var selectionGeometryFingerprint =
                FormatRectTransformGeometry(selection.RectTransform);
            source = new VisualSourceEvidence(
                evidence.RectTransform,
                selection,
                ordered[0],
                ordered[1],
                activeBackground,
                selectionGeometryFingerprint);
            failure = "";
            return true;
        }
        catch (TargetInvocationException ex)
        {
            failure = ex.InnerException?.GetBaseException().Message
                ?? ex.GetBaseException().Message;
            return false;
        }
        catch (Exception ex)
        {
            failure = ex.GetBaseException().Message;
            return false;
        }
    }

    private static bool TryReadSelectionEventTarget(
        PanelEvidence evidence,
        PanelMembers members,
        out LeafImageEvidence selection,
        out string failure)
    {
        selection = null!;
        failure = "";
        var button = evidence.RectTransform.gameObject;
        var rawUnitComponent = button.GetComponent(
            Il2CppType.From(members.UiLogicalUnitType));
        var rawUnit = RuntimeReflectionUtility.TryCastRuntimeObject(
            rawUnitComponent,
            UiLogicalUnitTypeName);
        if (rawUnitComponent is not Component queriedUnit
            || rawUnit is not Component exactUnit
            || !IsLiveUnityObject(queriedUnit)
            || !IsLiveUnityObject(exactUnit)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                queriedUnit,
                out var queriedUnitPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                exactUnit,
                out var exactUnitPointer)
            || queriedUnitPointer != exactUnitPointer
            || exactUnitPointer != evidence.UnitPointer)
        {
            failure = "keyed UILogicalUnit selection event has no stable exact native identity";
            return false;
        }

        var exactUnitOwner = exactUnit.gameObject;
        if (!IsLiveUnityObject(exactUnitOwner)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                exactUnitOwner,
                out var exactUnitOwnerPointer)
            || exactUnitOwnerPointer != evidence.ButtonPointer)
        {
            failure = "keyed UILogicalUnit selection event owner differs from the exact target button";
            return false;
        }

        var rawEvent = members.UiLogicalUnitSelectionUpdateEvent.GetValue(rawUnit);
        if (rawEvent == null
            || rawEvent.GetType()
                != members.UiLogicalUnitSelectionUpdateEvent.PropertyType)
        {
            failure = "keyed UILogicalUnit m_OnSelectionUpdateCallback is null or has a different exact custom UnityEvent_Bool type";
            return false;
        }

        var serialized = members.PersistentEventSerializedMembers;
        var rawPersistentCalls = serialized.PersistentCalls.GetValue(rawEvent);
        if (rawPersistentCalls == null
            || rawPersistentCalls.GetType()
                != serialized.PersistentCalls.PropertyType)
        {
            failure = "keyed selection event serialized m_PersistentCalls is null or has a different exact PersistentCallGroup type";
            return false;
        }
        var rawCalls = serialized.Calls.GetValue(rawPersistentCalls);
        if (rawCalls == null
            || rawCalls.GetType() != serialized.Calls.PropertyType)
        {
            failure = "keyed selection event serialized m_Calls is null or has a different exact List<PersistentCall> type";
            return false;
        }
        if (serialized.CallsCount.GetValue(rawCalls) is not int count
            || count < 0)
        {
            failure = "keyed selection event serialized m_Calls has an unreadable or negative count";
            return false;
        }
        if (count != 1)
        {
            failure = $"keyed UILogicalUnit selection event must contain exactly one persistent listener: {count}";
            return false;
        }
        if (!RuntimeConcreteCollectionReader.TryReadList(
                rawCalls,
                out var rawPersistentCallValues,
                out var callsReadFailure))
        {
            failure = $"keyed selection event serialized m_Calls could not be read without count drift: {callsReadFailure}";
            return false;
        }
        if (rawPersistentCallValues.Count != count)
        {
            failure = $"keyed selection event serialized m_Calls count drifted during read: {count}->{rawPersistentCallValues.Count}";
            return false;
        }

        var rawPersistentCall = rawPersistentCallValues[0];
        if (rawPersistentCall == null
            || rawPersistentCall.GetType() != serialized.PersistentCallType)
        {
            failure = "selection event serialized persistent call 0 is null or has a different exact PersistentCall type";
            return false;
        }
        var rawTarget = serialized.Target.GetValue(rawPersistentCall);
        var rawMethodName = serialized.MethodName.GetValue(rawPersistentCall);
        if (rawMethodName is not string methodName
            || !string.Equals(
                methodName,
                SelectionEnabledMethodName,
                StringComparison.Ordinal))
        {
            failure = "selection event persistent listener 0 does not call the exact Image enabled setter";
            return false;
        }
        if (rawTarget is not UnityEngine.Object target
            || !IsLiveUnityObject(target)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                target,
                out var targetPointer)
            || !TryReadNativeClassName(target, out var targetClassName)
            || targetClassName != ImageTypeName)
        {
            failure = "selection event persistent listener 0 has no exact live UnityEngine.UI.Image target";
            return false;
        }

        var rawImage = RuntimeReflectionUtility.TryCastRuntimeObject(
            target,
            ImageTypeName);
        if (rawImage is not Component imageComponent
            || !IsLiveUnityObject(imageComponent)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                imageComponent,
                out var castImagePointer)
            || castImagePointer != targetPointer)
        {
            failure = "selection event persistent listener 0 target cannot be cast to the same exact Image identity";
            return false;
        }

        var owner = imageComponent.gameObject;
        if (!IsLiveUnityObject(owner)
            || RuntimeReflectionUtility.TryCastRuntimeObject(
                imageComponent.transform,
                RectTransformTypeName) is not RectTransform selectionRect
            || !IsLiveUnityObject(selectionRect)
            || !TryReadExactLeafImage(
                owner,
                selectionRect,
                rawImage,
                members,
                out selection,
                out failure))
        {
            if (string.IsNullOrEmpty(failure))
            {
                failure = "selection-event Image target has no exact leaf visual shape";
            }
            return false;
        }

        if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(
                selectionRect.parent,
                out var selectionParentPointer)
            || selectionParentPointer != evidence.RectTransformPointer)
        {
            failure = "selection-event Image target is not a direct child of the exact target button";
            return false;
        }
        if (!HasValidRectTransformGeometry(selectionRect))
        {
            failure = $"selection-event Image target does not have finite positive render geometry: {FormatRectTransformGeometry(selectionRect)}";
            return false;
        }

        failure = "";
        return true;
    }

    private static bool TryReadExactLeafImage(
        GameObject owner,
        RectTransform rectTransform,
        object rawImage,
        PanelMembers members,
        out LeafImageEvidence evidence,
        out string failure)
    {
        evidence = null!;
        if (rawImage is not Component imageComponent
            || !IsLiveUnityObject(owner)
            || !IsLiveUnityObject(rectTransform)
            || !IsLiveUnityObject(imageComponent)
            || rectTransform.childCount != 0
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                owner,
                out var ownerPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                rectTransform,
                out var rectPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                imageComponent,
                out var imagePointer))
        {
            failure = "leaf Image owner/rect/image identity is unavailable or the owner is not a leaf";
            return false;
        }

        var components = owner.GetComponents<Component>();
        if (components.Length != 3)
        {
            failure = $"leaf Image owner has {components.Length} components instead of exactly 3";
            return false;
        }
        var enumeratedPointers = new HashSet<nint>();
        foreach (var component in components)
        {
            if (component == null
                || !IsLiveUnityObject(component)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                    component,
                    out var componentPointer)
                || !enumeratedPointers.Add(componentPointer))
            {
                failure = "leaf Image component enumeration contains a null, dead, or duplicate identity";
                return false;
            }
        }

        if (!TryGetExactComponent(
                owner,
                typeof(RectTransform),
                RectTransformTypeName,
                out var queriedRect,
                out var queriedRectPointer,
                out failure)
            || queriedRectPointer != rectPointer
            || !TryGetExactComponent(
                owner,
                members.CanvasRendererType,
                CanvasRendererTypeName,
                out _,
                out var canvasRendererPointer,
                out failure)
            || !TryGetExactComponent(
                owner,
                members.ImageType,
                ImageTypeName,
                out var queriedImage,
                out var queriedImagePointer,
                out failure)
            || queriedImagePointer != imagePointer)
        {
            if (string.IsNullOrEmpty(failure))
            {
                failure = "leaf Image typed components do not match their supplied exact identities";
            }
            return false;
        }

        var exactPointers = new HashSet<nint>
        {
            queriedRectPointer,
            canvasRendererPointer,
            queriedImagePointer,
        };
        if (exactPointers.Count != 3
            || !enumeratedPointers.SetEquals(exactPointers)
            || queriedRect is not RectTransform
            || queriedImage.GetType() != rawImage.GetType())
        {
            failure = "leaf Image components are not exactly RectTransform/CanvasRenderer/Image";
            return false;
        }

        var accessors = members.ImageAccessors;
        if (accessors.GetEnabled.Invoke(rawImage, null) is not bool enabled
            || accessors.GetRaycastTarget.Invoke(rawImage, null)
                is not bool raycastTarget
            || accessors.GetSprite.Invoke(rawImage, null) is not Sprite sprite
            || !IsLiveUnityObject(sprite)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                sprite,
                out var spritePointer)
            || accessors.GetMaterial.Invoke(rawImage, null)
                is not Material material
            || !IsLiveUnityObject(material)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                material,
                out var materialPointer)
            || accessors.GetImageType.Invoke(rawImage, null)
                is not object rawImageType
            || rawImageType.GetType()
                != accessors.GetImageType.ReturnType)
        {
            failure = "leaf Image visual state or type/sprite/material identity is unavailable";
            return false;
        }
        var imageTypeValue = Convert.ToInt32(rawImageType);

        evidence = new LeafImageEvidence(
            owner,
            rectTransform,
            ownerPointer,
            rectPointer,
            imagePointer,
            spritePointer,
            materialPointer,
            imageTypeValue,
            enabled,
            owner.activeSelf,
            owner.activeInHierarchy,
            raycastTarget);
        failure = "";
        return true;
    }

    private static bool TryGetExactComponent(
        GameObject owner,
        Type componentType,
        string componentTypeName,
        out Component component,
        out nint pointer,
        out string failure)
    {
        component = null!;
        pointer = 0;
        var rawComponent = owner.GetComponent(Il2CppType.From(componentType));
        var exactComponent = RuntimeReflectionUtility.TryCastRuntimeObject(
            rawComponent,
            componentTypeName);
        if (rawComponent is not Component queried
            || exactComponent is not Component exact
            || !IsLiveUnityObject(queried)
            || !IsLiveUnityObject(exact)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                queried,
                out var queriedPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                exact,
                out var exactPointer)
            || queriedPointer != exactPointer
            || !TryReadNativeClassName(exact, out var nativeClassName)
            || nativeClassName != componentTypeName)
        {
            failure = $"GameObject does not expose one exact live {componentTypeName} component";
            return false;
        }

        component = exact;
        pointer = exactPointer;
        failure = "";
        return true;
    }

    private static bool TryValidateNoLayoutGroup(
        RectTransform button,
        Type layoutGroupType,
        out string failure)
    {
        if (!IsLiveUnityObject(button)
            || !IsLiveUnityObject(button.gameObject))
        {
            failure = "exact target button is unavailable while checking LayoutGroup";
            return false;
        }

        var rawLayout = button.gameObject.GetComponent(
            Il2CppType.From(layoutGroupType));
        if (rawLayout != null)
        {
            failure = "exact target button contains a LayoutGroup and cannot own a stable fill sibling";
            return false;
        }

        failure = "";
        return true;
    }

    private static bool HasValidRectTransformGeometry(RectTransform value)
    {
        var rect = value.rect;
        return IsFinite(value.anchorMin)
            && IsFinite(value.anchorMax)
            && IsFinite(value.pivot)
            && IsFinite(value.anchoredPosition)
            && IsFinite(value.sizeDelta)
            && IsFinite(value.offsetMin)
            && IsFinite(value.offsetMax)
            && IsFinite(value.localScale)
            && IsFinite(rect)
            && value.localScale.x != 0f
            && value.localScale.y != 0f
            && value.localScale.z != 0f
            && rect.width > 0f
            && rect.height > 0f;
    }

    private static bool HasSameRectTransformGeometry(
        RectTransform left,
        RectTransform right)
    {
        return HasValidRectTransformGeometry(left)
            && HasValidRectTransformGeometry(right)
            && HasSameVector2(left.anchorMin, right.anchorMin)
            && HasSameVector2(left.anchorMax, right.anchorMax)
            && HasSameVector2(left.pivot, right.pivot)
            && HasSameVector2(
                left.anchoredPosition,
                right.anchoredPosition)
            && HasSameVector2(left.sizeDelta, right.sizeDelta)
            && HasSameVector2(left.offsetMin, right.offsetMin)
            && HasSameVector2(left.offsetMax, right.offsetMax)
            && HasSameVector3(left.localScale, right.localScale)
            && HasSameRectValue(left.rect, right.rect);
    }

    private static bool IsFinite(Vector2 value) =>
        IsFinite(value.x) && IsFinite(value.y);

    private static bool IsFinite(Vector3 value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

    private static bool IsFinite(Rect value) =>
        IsFinite(value.x)
        && IsFinite(value.y)
        && IsFinite(value.width)
        && IsFinite(value.height);

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);

    private static bool HasSameVector2(Vector2 left, Vector2 right) =>
        left.x == right.x && left.y == right.y;

    private static bool HasSameVector3(Vector3 left, Vector3 right) =>
        left.x == right.x && left.y == right.y && left.z == right.z;

    private static bool HasSameRectValue(Rect left, Rect right) =>
        left.x == right.x
        && left.y == right.y
        && left.width == right.width
        && left.height == right.height;

    private static string FormatRectTransformGeometry(RectTransform value)
    {
        var rect = value.rect;
        return $"anchors={FormatVector(value.anchorMin)}->{FormatVector(value.anchorMax)},pivot={FormatVector(value.pivot)},position={FormatVector(value.anchoredPosition)},sizeDelta={FormatVector(value.sizeDelta)},offsets={FormatVector(value.offsetMin)}->{FormatVector(value.offsetMax)},scale={FormatVector(value.localScale)},rect={FormatRect(rect)}";
    }

    private static string FormatVector(Vector2 value) =>
        $"({FormatFloat(value.x)},{FormatFloat(value.y)})";

    private static string FormatVector(Vector3 value) =>
        $"({FormatFloat(value.x)},{FormatFloat(value.y)},{FormatFloat(value.z)})";

    private static string FormatRect(Rect value) =>
        $"({FormatFloat(value.x)},{FormatFloat(value.y)},{FormatFloat(value.width)},{FormatFloat(value.height)})";

    private static string FormatFloat(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static bool TryCreateOwnedVisual(
        VisualSourceEvidence source,
        RuntimeUiTargetKinds claims,
        RuntimeTargetHighlightPalette palette,
        out OwnedFillVisual visual,
        out string failure)
    {
        visual = null!;
        GameObject? clone = null;
        try
        {
            PanelMembers? members;
            lock (PatchRoot) members = _members;
            if (members == null)
            {
                failure = "verified throw-delivery visual members became unavailable";
                return false;
            }
            if (!TryValidateNoLayoutGroup(
                    source.ButtonRectTransform,
                    members.LayoutGroupType,
                    out failure))
            {
                return false;
            }

            var parent = source.Selection.RectTransform.parent;
            if (parent == null
                || !IsLiveUnityObject(parent)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                    parent,
                    out var parentPointer)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                    source.ButtonRectTransform,
                    out var buttonPointer)
                || parentPointer != buttonPointer)
            {
                failure = "selection-event target parent changed before fill creation";
                return false;
            }

            clone = UnityEngine.Object.Instantiate(
                source.ActiveBackground.Owner,
                parent);
            if (clone == null || !IsLiveUnityObject(clone))
            {
                failure = "failed to instantiate the exact active native background";
                return false;
            }
            clone.name = OwnedFillName;

            if (RuntimeReflectionUtility.TryCastRuntimeObject(
                    clone.transform,
                    RectTransformTypeName) is not RectTransform cloneRect
                || !IsLiveUnityObject(cloneRect))
            {
                failure = "cloned background has no exact live RectTransform";
                return false;
            }
            var rawCloneImageComponent = clone.GetComponent(
                Il2CppType.From(members.ImageType));
            var rawCloneImage = RuntimeReflectionUtility.TryCastRuntimeObject(
                rawCloneImageComponent,
                ImageTypeName);
            if (rawCloneImage == null
                || !TryReadExactLeafImage(
                    clone,
                    cloneRect,
                    rawCloneImage,
                    members,
                    out var initialClone,
                    out failure))
            {
                failure = $"cloned background failed exact leaf validation: {failure}";
                return false;
            }
            if (!HasSameRectTransformGeometry(
                    cloneRect,
                    source.ActiveBackground.RectTransform)
                || initialClone.SpritePointer
                    != source.ActiveBackground.SpritePointer
                || initialClone.MaterialPointer
                    != source.ActiveBackground.MaterialPointer
                || initialClone.ImageTypeValue
                    != source.ActiveBackground.ImageTypeValue)
            {
                failure = "cloned background did not preserve source geometry, sprite, and material";
                return false;
            }

            var selectionSibling = source.Selection.RectTransform.GetSiblingIndex();
            var maximumBackgroundSibling = Math.Max(
                source.Background1.RectTransform.GetSiblingIndex(),
                source.Background2.RectTransform.GetSiblingIndex());
            if (selectionSibling <= maximumBackgroundSibling)
            {
                failure = "native background/selection ordering changed before fill insertion";
                return false;
            }
            cloneRect.SetSiblingIndex(selectionSibling);

            members.ImageAccessors.SetRaycastTarget.Invoke(
                rawCloneImage,
                new object?[] { false });
            members.ImageAccessors.SetEnabled.Invoke(
                rawCloneImage,
                new object?[] { true });
            clone.SetActive(true);
            var color = RuntimeTargetHighlightStyle.BuildOrderHighlightPulseColor(
                claims,
                palette,
                Time.realtimeSinceStartup);
            members.ImageAccessors.SetColor.Invoke(
                rawCloneImage,
                new object?[] { color });

            if (!TryReadExactLeafImage(
                    clone,
                    cloneRect,
                    rawCloneImage,
                    members,
                    out var finalClone,
                    out failure)
                || !finalClone.ActiveSelf
                || !finalClone.ActiveInHierarchy
                || !finalClone.Enabled
                || finalClone.RaycastTarget
                || finalClone.SpritePointer
                    != source.ActiveBackground.SpritePointer
                || finalClone.MaterialPointer
                    != source.ActiveBackground.MaterialPointer
                || finalClone.ImageTypeValue
                    != source.ActiveBackground.ImageTypeValue
                || !HasSameRectTransformGeometry(
                    finalClone.RectTransform,
                    source.ActiveBackground.RectTransform))
            {
                failure = $"owned target fill failed post-configuration validation: {failure}";
                return false;
            }

            var currentCloneSibling = cloneRect.GetSiblingIndex();
            var currentSelectionSibling =
                source.Selection.RectTransform.GetSiblingIndex();
            var currentMaximumBackgroundSibling = Math.Max(
                source.Background1.RectTransform.GetSiblingIndex(),
                source.Background2.RectTransform.GetSiblingIndex());
            if (currentCloneSibling <= currentMaximumBackgroundSibling
                || currentCloneSibling + 1 != currentSelectionSibling)
            {
                failure = $"owned target fill ordering is invalid: backgrounds<={currentMaximumBackgroundSibling}; fill={currentCloneSibling}; selection={currentSelectionSibling}";
                return false;
            }

            visual = new OwnedFillVisual(
                clone,
                rawCloneImage,
                finalClone.OwnerPointer,
                finalClone.RectPointer,
                finalClone.ImagePointer,
                source.ActiveBackground.ImagePointer,
                finalClone.ImageTypeValue);
            clone = null;
            failure = "";
            return true;
        }
        catch (TargetInvocationException ex)
        {
            failure = ex.InnerException?.GetBaseException().Message
                ?? ex.GetBaseException().Message;
            return false;
        }
        catch (Exception ex)
        {
            failure = ex.GetBaseException().Message;
            return false;
        }
        finally
        {
            if (clone != null) SafeDestroyTransientClone(clone);
        }
    }

    private static bool TryValidateOwnedVisualIdentity(
        OwnedFillVisual visual,
        out string failure)
    {
        try
        {
            if (!IsLiveUnityObject(visual.Owner)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                    visual.Owner,
                    out var ownerPointer))
            {
                failure = "owned target fill owner is missing or destroyed";
                return false;
            }
            if (ownerPointer != visual.OwnerPointer)
            {
                failure = $"owned target fill owner native identity drifted: expected={FormatPointer(visual.OwnerPointer)}; actual={FormatPointer(ownerPointer)}";
                return false;
            }
            if (visual.Image is not Component image
                || !IsLiveUnityObject(image)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                    image,
                    out var imagePointer))
            {
                failure = "owned target fill Image is missing or destroyed";
                return false;
            }
            if (imagePointer != visual.ImagePointer)
            {
                failure = $"owned target fill Image native identity drifted: expected={FormatPointer(visual.ImagePointer)}; actual={FormatPointer(imagePointer)}";
                return false;
            }

            var imageOwner = image.gameObject;
            if (!IsLiveUnityObject(imageOwner)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                    imageOwner,
                    out var imageOwnerPointer)
                || imageOwnerPointer != visual.OwnerPointer)
            {
                failure = "owned target fill Image owner no longer matches the exact owned GameObject identity";
                return false;
            }

            failure = "";
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.GetBaseException().Message;
            return false;
        }
    }

    private static bool TryValidateOwnedVisual(
        ActiveTargetBinding binding,
        VisualSourceEvidence source,
        out string failure,
        out bool abandonOwnedVisual)
    {
        abandonOwnedVisual = false;
        failure = "";
        var visual = binding.OwnedVisual;
        if (visual == null)
        {
            abandonOwnedVisual = true;
            failure = "owned target fill is missing";
            return false;
        }
        if (!TryValidateOwnedVisualIdentity(visual, out var identityFailure))
        {
            abandonOwnedVisual = true;
            failure = $"owned target fill identity is unsafe: {identityFailure}";
            return false;
        }

        try
        {
            PanelMembers? members;
            lock (PatchRoot) members = _members;
            if (members == null)
            {
                failure = "verified throw-delivery visual members are unavailable";
                return false;
            }
            if (RuntimeReflectionUtility.TryCastRuntimeObject(
                    visual.Owner.transform,
                    RectTransformTypeName) is not RectTransform rectTransform
                || !IsLiveUnityObject(rectTransform)
                || !TryReadExactLeafImage(
                    visual.Owner,
                    rectTransform,
                    visual.Image,
                    members,
                    out var current,
                    out failure))
            {
                abandonOwnedVisual = !TryValidateOwnedVisualIdentity(
                    visual,
                    out _);
                if (string.IsNullOrEmpty(failure))
                {
                    failure = "owned target fill no longer has its exact leaf shape";
                }
                return false;
            }

            if (current.OwnerPointer != visual.OwnerPointer
                || current.RectPointer != visual.RectPointer
                || current.ImagePointer != visual.ImagePointer
                || current.ImageTypeValue != visual.ImageTypeValue
                || current.SpritePointer
                    != source.ActiveBackground.SpritePointer
                || current.MaterialPointer
                    != source.ActiveBackground.MaterialPointer
                || current.ImageTypeValue
                    != source.ActiveBackground.ImageTypeValue
                || visual.SourceBackgroundImagePointer
                    != source.ActiveBackground.ImagePointer
                || !current.ActiveSelf
                || !current.ActiveInHierarchy
                || !current.Enabled
                || current.RaycastTarget
                || !HasSameRectTransformGeometry(
                    current.RectTransform,
                    source.ActiveBackground.RectTransform)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                    current.RectTransform.parent,
                    out var parentPointer)
                || parentPointer != binding.RectTransformPointer)
            {
                failure = "owned target fill identity, source resources, geometry, state, or parent changed";
                return false;
            }

            var fillSibling = current.RectTransform.GetSiblingIndex();
            var selectionSibling = source.Selection.RectTransform.GetSiblingIndex();
            var maximumBackgroundSibling = Math.Max(
                source.Background1.RectTransform.GetSiblingIndex(),
                source.Background2.RectTransform.GetSiblingIndex());
            if (fillSibling <= maximumBackgroundSibling
                || fillSibling + 1 != selectionSibling)
            {
                failure = $"owned target fill sibling ordering changed: backgrounds<={maximumBackgroundSibling}; fill={fillSibling}; selection={selectionSibling}";
                return false;
            }

            failure = "";
            return true;
        }
        catch (TargetInvocationException ex)
        {
            failure = ex.InnerException?.GetBaseException().Message
                ?? ex.GetBaseException().Message;
            abandonOwnedVisual = !TryValidateOwnedVisualIdentity(
                visual,
                out _);
            return false;
        }
        catch (Exception ex)
        {
            failure = ex.GetBaseException().Message;
            abandonOwnedVisual = !TryValidateOwnedVisualIdentity(
                visual,
                out _);
            return false;
        }
    }

    private static bool TryApplyPulseLocked(
        ActiveTargetBinding binding,
        RuntimeTargetHighlightPalette palette,
        out string failure,
        out bool abandonOwnedVisual)
    {
        abandonOwnedVisual = false;
        var visual = binding.OwnedVisual;
        if (visual == null)
        {
            abandonOwnedVisual = true;
            failure = "owned target fill is missing";
            return false;
        }
        if (!TryValidateOwnedVisualIdentity(visual, out var identityFailure))
        {
            abandonOwnedVisual = true;
            failure = $"owned target fill identity is unsafe: {identityFailure}";
            return false;
        }

        try
        {
            PanelMembers? members;
            lock (PatchRoot) members = _members;
            if (members == null)
            {
                failure = "verified target fill color setter is unavailable";
                return false;
            }
            var color = RuntimeTargetHighlightStyle.BuildOrderHighlightPulseColor(
                binding.Target.Claim,
                palette,
                Time.realtimeSinceStartup);
            members.ImageAccessors.SetColor.Invoke(
                visual.Image,
                new object?[] { color });
            failure = "";
            return true;
        }
        catch (TargetInvocationException ex)
        {
            failure = ex.InnerException?.GetBaseException().Message
                ?? ex.GetBaseException().Message;
            abandonOwnedVisual = !TryValidateOwnedVisualIdentity(
                visual,
                out _);
            return false;
        }
        catch (Exception ex)
        {
            failure = ex.GetBaseException().Message;
            abandonOwnedVisual = !TryValidateOwnedVisualIdentity(
                visual,
                out _);
            return false;
        }
    }

    private static bool TryRebuildOwnedVisualLocked(
        ActiveTargetBinding binding,
        VisualSourceEvidence source,
        RuntimeTargetHighlightPalette palette,
        out string failure)
    {
        var oldVisual = binding.OwnedVisual;
        if (oldVisual == null)
        {
            failure = "previous owned target fill is missing";
            return false;
        }

        if (!RetireOwnedVisual(
                oldVisual,
                "native background enabled state switched"))
        {
            binding.OwnedVisual = null;
            failure = "previous owned target fill could not be retired with its exact native identity";
            return false;
        }
        binding.OwnedVisual = null;
        if (!TryCreateOwnedVisual(
                source,
                binding.Target.Claim,
                palette,
                out var rebuilt,
                out failure))
        {
            return false;
        }

        binding.OwnedVisual = rebuilt;
        binding.ActiveBackgroundImagePointer =
            source.ActiveBackgroundImagePointer;
        _createdVisuals++;
        _rebuiltVisuals++;
        TryLogVisualInfo(
            $"Runtime throw-delivery target fill bound: reason=background-switched; business={binding.BusinessGeneration}; target={binding.TargetGeneration}; panel={FormatPointer(binding.PanelPointer)}; button={FormatPointer(binding.ButtonPointer)}; selection={FormatPointer(source.SelectionImagePointer)}; backgrounds={FormatPointer(source.BackgroundImagePointer1)},{FormatPointer(source.BackgroundImagePointer2)}; activeBackground={FormatPointer(source.ActiveBackgroundImagePointer)}; fill={FormatPointer(rebuilt.OwnerPointer)}; image={FormatPointer(rebuilt.ImagePointer)}.");
        failure = "";
        return true;
    }

    private static void SafeDestroyTransientClone(GameObject clone)
    {
        try
        {
            if (!IsLiveUnityObject(clone)) return;
            clone.SetActive(false);
            UnityEngine.Object.Destroy(clone);
        }
        catch
        {
            Interlocked.Increment(ref _visualErrors);
        }
    }

    private static bool TryReadNativeClassName(UnityEngine.Object value, out string fullName)
    {
        fullName = "";
        if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(value, out var pointer))
        {
            return false;
        }
        var classPointer = IL2CPP.il2cpp_object_get_class(pointer);
        if (classPointer == IntPtr.Zero) return false;
        var className = IL2CPP.il2cpp_class_get_name_(classPointer);
        var classNamespace = IL2CPP.il2cpp_class_get_namespace_(classPointer);
        if (string.IsNullOrEmpty(className)) return false;
        fullName = string.IsNullOrEmpty(classNamespace)
            ? className
            : classNamespace + "." + className;
        return true;
    }

    private static bool IsBusinessReady(long generation)
    {
        lock (PatchRoot)
        {
            return PatchedMethods.Contains(OpenPatchKey)
                && PatchedMethods.Contains(ClosePatchKey)
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

    private static void ClearRegisteredPanel(
        string state,
        bool destroyOwnedVisual)
    {
        lock (BindingRoot)
        {
            ClearPanelLocked(destroyOwnedVisual, state);
            _state = $"waiting: {NormalizeStatus(state)}";
        }
    }

    private static void ClearPanelLocked(
        bool destroyOwnedVisual,
        string reason)
    {
        ClearAllActiveBindingsLocked(destroyOwnedVisual, reason);
        _activePanel = null;
        NextBindAttemptAt.Clear();
    }

    private static void ClearActiveBindingLocked(
        RuntimeUiTargetKind kind,
        bool destroyOwnedVisual,
        string reason)
    {
        if (!ActiveBindings.Remove(kind, out var binding)) return;
        if (binding?.OwnedVisual == null) return;

        if (destroyOwnedVisual)
        {
            RetireOwnedVisual(binding.OwnedVisual, reason);
        }
        else
        {
            AbandonOwnedVisual(binding.OwnedVisual, reason);
        }
        binding.OwnedVisual = null;
    }

    private static void ClearAllActiveBindingsLocked(bool destroyOwnedVisual, string reason)
    {
        foreach (var kind in ActiveBindings.Keys.ToList())
        {
            ClearActiveBindingLocked(kind, destroyOwnedVisual, reason);
        }
    }

    private static void SetPanelWaitingState(string state)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        lock (BindingRoot)
        {
            ClearPanelLocked(
                destroyOwnedVisual: lifecycle.IsActive
                    && Environment.CurrentManagedThreadId == lifecycle.ThreadId,
                reason: state);
            _state = $"waiting: {NormalizeStatus(state)}";
        }
    }

    private static void SetBindingWaiting(RuntimeUiTargetKind kind, string state)
    {
        lock (BindingRoot)
        {
            ClearActiveBindingLocked(
                kind,
                destroyOwnedVisual: true,
                reason: state);
            NextBindAttemptAt[kind] =
                Time.realtimeSinceStartup + HealthCheckIntervalSeconds;
            _state = $"waiting: {NormalizeStatus(state)}";
        }
    }

    private static void HandleActiveVisualFailure(
        RuntimeUiTargetKind kind,
        string failure,
        bool abandonOwnedVisual)
    {
        lock (BindingRoot)
        {
            HandleActiveVisualFailureLocked(kind, failure, abandonOwnedVisual);
        }
    }

    private static void HandleActiveVisualFailureLocked(
        RuntimeUiTargetKind kind,
        string failure,
        bool abandonOwnedVisual)
    {
        var normalized = NormalizeStatus(failure);
        ClearActiveBindingLocked(
            kind,
            destroyOwnedVisual: !abandonOwnedVisual,
            reason: normalized);
        NextBindAttemptAt[kind] =
            Time.realtimeSinceStartup + HealthCheckIntervalSeconds;
        ResetBusinessLogBudgetLocked(
            RuntimeNightBusinessLifecycle.Generation);
        _bindingErrors++;
        _visualErrors++;
        _lastFailure = normalized;
        _state = $"unavailable: {normalized}";
        TryLogBoundedFailure(
            "active",
            normalized);
    }

    private static bool RetireOwnedVisual(
        OwnedFillVisual visual,
        string reason,
        bool logRetirement = true)
    {
        var normalizedReason = NormalizeStatus(reason);
        if (!TryValidateOwnedVisualIdentity(
                visual,
                out var identityFailure))
        {
            _visualErrors++;
            AbandonOwnedVisual(
                visual,
                $"{normalizedReason}; identity-check={identityFailure}");
            return false;
        }

        try
        {
            visual.Owner.SetActive(false);
            if (!TryValidateOwnedVisualIdentity(
                    visual,
                    out identityFailure))
            {
                _visualErrors++;
                AbandonOwnedVisual(
                    visual,
                    $"{normalizedReason}; pre-destroy-identity-check={identityFailure}");
                return false;
            }
            UnityEngine.Object.Destroy(visual.Owner);
            _destroyedVisuals++;
            if (logRetirement)
            {
                TryLogVisualInfo(
                    $"Runtime throw-delivery target fill retired: mode=destroyed; reason={normalizedReason}; fill={FormatPointer(visual.OwnerPointer)}; image={FormatPointer(visual.ImagePointer)}.");
            }
            return true;
        }
        catch
        {
            _visualErrors++;
            AbandonOwnedVisual(visual, normalizedReason);
            return false;
        }
    }

    private static void AbandonOwnedVisual(
        OwnedFillVisual visual,
        string reason)
    {
        _abandonedVisuals++;
        TryLogVisualInfo(
            $"Runtime throw-delivery target fill retired: mode=abandoned; reason={NormalizeStatus(reason)}; fill={FormatPointer(visual.OwnerPointer)}; image={FormatPointer(visual.ImagePointer)}.");
    }

    private static void TryLogBoundedFailure(
        string phase,
        string reason)
    {
        var normalizedPhase = NormalizeStatus(phase);
        var normalizedReason = NormalizeStatus(reason);
        var shouldLog = false;
        lock (BindingRoot)
        {
            var identity = new FailureLogIdentity(
                normalizedPhase,
                normalizedReason);
            if (_warningLogs < MaxWarningLogsPerBusiness
                && LoggedFailures.Add(identity))
            {
                _warningLogs++;
                shouldLog = true;
            }
        }
        if (shouldLog)
        {
            TryLogWarning(
                $"Runtime throw-delivery target fill failed: phase={normalizedPhase}; reason={normalizedReason}.");
        }
    }

    private static void TryLogVisualInfo(string message)
    {
        lock (BindingRoot)
        {
            if (_visualLogs >= MaxVisualLogsPerBusiness) return;
            _visualLogs++;
        }
        TryLogInfo(message);
    }

    private static void SetAttachFailure(string failure, bool log)
    {
        lock (BindingRoot)
        {
            _lastFailure = NormalizeStatus(failure);
            _state = $"unavailable: attach failed: {_lastFailure}";
        }
        if (log)
        {
            TryLogWarning($"Runtime throw-delivery order highlight attach failed: {NormalizeStatus(failure)}");
        }
    }

    private static void NotePanelError(string failure)
    {
        var normalized = NormalizeStatus(failure);
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        lock (BindingRoot)
        {
            ClearPanelLocked(
                destroyOwnedVisual:
                    lifecycle.IsActive
                    && Environment.CurrentManagedThreadId == lifecycle.ThreadId,
                reason: normalized);
            ResetBusinessLogBudgetLocked(lifecycle.Generation);
            _bindingErrors++;
            _lastFailure = normalized;
            _state = $"unavailable: {normalized}";
        }
        TryLogBoundedFailure("panel", normalized);
    }

    private static void NoteBindingFailure(RuntimeUiTargetKind kind, string failure)
    {
        var normalized = NormalizeStatus(failure);
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        lock (BindingRoot)
        {
            ClearActiveBindingLocked(
                kind,
                destroyOwnedVisual:
                    lifecycle.IsActive
                    && Environment.CurrentManagedThreadId == lifecycle.ThreadId,
                reason: normalized);
            NextBindAttemptAt[kind] = Time.realtimeSinceStartup + HealthCheckIntervalSeconds;
            ResetBusinessLogBudgetLocked(lifecycle.Generation);
            _bindingErrors++;
            _lastFailure = normalized;
            _state = $"unavailable: {normalized}";
        }
        TryLogBoundedFailure($"binding-{kind}", normalized);
    }

    private static void ResetBusinessLogBudgetLocked(long businessGeneration)
    {
        if (_warningBusinessGeneration == businessGeneration) return;
        _warningBusinessGeneration = businessGeneration;
        _warningLogs = 0;
        _visualLogs = 0;
        LoggedFailures.Clear();
    }

    private static void TryLogInfo(string message)
    {
        try
        {
            _log?.LogInfo(message);
        }
        catch
        {
            // Logging must never affect the observed game method.
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
            // Logging must never affect the observed game method.
        }
    }

    private static string NormalizeReason(string reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? "unspecified" : NormalizeStatus(reason);
    }

    private static string NormalizeStatus(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 1_024 ? normalized : normalized[..1_024] + "...";
    }

    private static string FormatPointer(nint pointer)
    {
        return pointer == 0 ? "none" : $"0x{pointer:x}";
    }

    private sealed record PanelMembers(
        PropertyInfo Data,
        PropertyInfo ButtonInstances,
        PropertyInfo Group,
        PropertyInfo ButtonField,
        Type DataTupleType,
        PropertyInfo TupleItem2,
        PropertyInfo TupleItem3,
        PropertyInfo OrderDeskCode,
        PropertyInfo ControllerDeskCode,
        PropertyInfo GroupChildren,
        MethodInfo AlignedTryGetValue,
        Type UiLogicalUnitType,
        PropertyInfo UiLogicalUnitRectTransform,
        PropertyInfo UiLogicalUnitSelectionUpdateEvent,
        PersistentEventSerializedMembers PersistentEventSerializedMembers,
        Type CanvasRendererType,
        Type ImageType,
        Type LayoutGroupType,
        ImageAccessors ImageAccessors);

    private sealed record PersistentEventSerializedMembers(
        PropertyInfo PersistentCalls,
        PropertyInfo Calls,
        PropertyInfo CallsCount,
        Type PersistentCallType,
        PropertyInfo Target,
        PropertyInfo MethodName);

    private sealed record ImageAccessors(
        MethodInfo GetRaycastTarget,
        MethodInfo GetEnabled,
        MethodInfo GetSprite,
        MethodInfo GetMaterial,
        MethodInfo GetImageType,
        MethodInfo SetColor,
        MethodInfo SetRaycastTarget,
        MethodInfo SetEnabled);

    private sealed record PanelOpenEvidence(
        long BusinessGeneration,
        int ThreadId,
        nint PanelPointer,
        string Failure);

    private sealed record PanelDirectoryEvidence(
        object Data,
        object Group,
        PanelMembers Members,
        nint PanelPointer,
        nint GroupPointer,
        nint ButtonFieldPointer,
        int PoolCount,
        HashSet<nint> ButtonPointers);

    private readonly record struct PanelEvidence(
        RectTransform RectTransform,
        nint PanelPointer,
        nint OrderPointer,
        nint ControllerPointer,
        nint GroupPointer,
        nint UnitPointer,
        nint ButtonPointer,
        nint RectTransformPointer,
        nint ButtonFieldPointer,
        int DeskCode,
        RuntimeOrderKind OrderKind,
        int PoolCount);

    private sealed record ActivePanelRegistration(
        object Panel,
        long BusinessGeneration,
        int ThreadId,
        nint PanelPointer);

    private sealed class ActiveTargetBinding
    {
        public ActiveTargetBinding(
            long targetGeneration,
            RuntimeUiTargetSnapshot target,
            long businessGeneration,
            nint panelPointer,
            nint orderPointer,
            nint controllerPointer,
            nint groupPointer,
            nint unitPointer,
            nint buttonPointer,
            nint rectTransformPointer,
            nint buttonFieldPointer,
            int deskCode,
            RuntimeOrderKind orderKind,
            int poolCount,
            long orderLifecycleSequence,
            nint selectionImagePointer,
            nint backgroundImagePointer1,
            nint backgroundImagePointer2,
            nint activeBackgroundImagePointer,
            OwnedFillVisual ownedVisual)
        {
            TargetGeneration = targetGeneration;
            Target = target;
            BusinessGeneration = businessGeneration;
            PanelPointer = panelPointer;
            OrderPointer = orderPointer;
            ControllerPointer = controllerPointer;
            GroupPointer = groupPointer;
            UnitPointer = unitPointer;
            ButtonPointer = buttonPointer;
            RectTransformPointer = rectTransformPointer;
            ButtonFieldPointer = buttonFieldPointer;
            DeskCode = deskCode;
            OrderKind = orderKind;
            PoolCount = poolCount;
            OrderLifecycleSequence = orderLifecycleSequence;
            SelectionImagePointer = selectionImagePointer;
            BackgroundImagePointer1 = backgroundImagePointer1;
            BackgroundImagePointer2 = backgroundImagePointer2;
            ActiveBackgroundImagePointer = activeBackgroundImagePointer;
            OwnedVisual = ownedVisual;
            NextHealthCheckAt = Time.realtimeSinceStartup + HealthCheckIntervalSeconds;
        }

        public long TargetGeneration { get; private set; }
        public RuntimeUiTargetSnapshot Target { get; private set; }
        public RuntimeUiTargetKind TargetKind => Target.Kind;
        public long BusinessGeneration { get; }
        public nint PanelPointer { get; }
        public nint OrderPointer { get; }
        public nint ControllerPointer { get; }
        public nint GroupPointer { get; }
        public nint UnitPointer { get; }
        public nint ButtonPointer { get; }
        public nint RectTransformPointer { get; }
        public nint ButtonFieldPointer { get; }
        public int DeskCode { get; }
        public RuntimeOrderKind OrderKind { get; }
        public int PoolCount { get; }
        public long OrderLifecycleSequence { get; }
        public nint SelectionImagePointer { get; }
        public nint BackgroundImagePointer1 { get; }
        public nint BackgroundImagePointer2 { get; }
        public nint ActiveBackgroundImagePointer { get; set; }
        public OwnedFillVisual? OwnedVisual { get; set; }
        public float NextHealthCheckAt { get; set; }

        public bool HasSameTarget(
            RuntimeUiTargetSetSnapshot targetSet,
            RuntimeUiTargetSnapshot target)
        {
            if (targetSet.SessionGeneration != BusinessGeneration
                || !Target.HasSameValues(target))
            {
                return false;
            }

            TargetGeneration = targetSet.Generation;
            Target = target;
            return true;
        }

        public bool HasSameIdentity(PanelEvidence other)
        {
            return PanelPointer != 0
                && PanelPointer == other.PanelPointer
                && OrderPointer != 0
                && OrderPointer == other.OrderPointer
                && ControllerPointer != 0
                && ControllerPointer == other.ControllerPointer
                && GroupPointer != 0
                && GroupPointer == other.GroupPointer
                && UnitPointer != 0
                && UnitPointer == other.UnitPointer
                && ButtonPointer != 0
                && ButtonPointer == other.ButtonPointer
                && RectTransformPointer != 0
                && RectTransformPointer == other.RectTransformPointer
                && ButtonFieldPointer != 0
                && ButtonFieldPointer == other.ButtonFieldPointer
                && DeskCode == other.DeskCode
                && OrderKind == other.OrderKind
                && PoolCount == other.PoolCount;
        }

        public bool HasSameVisualSourceIdentity(
            VisualSourceEvidence other)
        {
            return SelectionImagePointer != 0
                && SelectionImagePointer == other.SelectionImagePointer
                && BackgroundImagePointer1 != 0
                && BackgroundImagePointer1
                    == other.BackgroundImagePointer1
                && BackgroundImagePointer2 != 0
                && BackgroundImagePointer2
                    == other.BackgroundImagePointer2;
        }
    }

    private sealed record LeafImageEvidence(
        GameObject Owner,
        RectTransform RectTransform,
        nint OwnerPointer,
        nint RectPointer,
        nint ImagePointer,
        nint SpritePointer,
        nint MaterialPointer,
        int ImageTypeValue,
        bool Enabled,
        bool ActiveSelf,
        bool ActiveInHierarchy,
        bool RaycastTarget);

    private sealed record VisualSourceEvidence(
        RectTransform ButtonRectTransform,
        LeafImageEvidence Selection,
        LeafImageEvidence Background1,
        LeafImageEvidence Background2,
        LeafImageEvidence ActiveBackground,
        string SelectionGeometryFingerprint)
    {
        public nint SelectionImagePointer => Selection.ImagePointer;
        public nint BackgroundImagePointer1 => Background1.ImagePointer;
        public nint BackgroundImagePointer2 => Background2.ImagePointer;
        public nint ActiveBackgroundImagePointer =>
            ActiveBackground.ImagePointer;
    }

    private sealed record OwnedFillVisual(
        GameObject Owner,
        object Image,
        nint OwnerPointer,
        nint RectPointer,
        nint ImagePointer,
        nint SourceBackgroundImagePointer,
        int ImageTypeValue);

    private readonly record struct FailureLogIdentity(
        string Phase,
        string Reason);

}
