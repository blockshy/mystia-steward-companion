using BepInEx.Logging;
using HarmonyLib;
using MystiaStewardCompanion.Save;
using NightScene.GuestManagementUtility;
using NightScene.UI.HUDUtility;
using UnityEngine;

const string OwnedFillName = "MystiaStewardCompanion.ThrowDeliveryTargetFill";
var unityThreadId = Environment.CurrentManagedThreadId;
var log = new ManualLogSource();
long generationSequence = 10;
long targetSetGeneration = 0;

Assert(TryResolveSelectionEventShape(
        typeof(DEYU.AdpUISystem.LogicalCollection.UILogicalUnit),
        out var exactSelectionShapeFailure),
    $"The verified UILogicalUnit custom UnityEvent_Bool chain must resolve: {exactSelectionShapeFailure}");
Assert(!TryResolveSelectionEventShape(
           typeof(DEYU.AdpUISystem.LogicalCollection.BareUnityEventLogicalUnitShape),
           out var bareSelectionShapeFailure)
       && bareSelectionShapeFailure.Contains(
           "not the exact custom UnityEvent_Bool subtype",
           StringComparison.Ordinal),
    "A bare UnityEvent<bool> must not replace the exact custom event subtype.");
Assert(!TryResolveSelectionEventShape(
           typeof(DEYU.AdpUISystem.LogicalCollection.UnrelatedUnityEventLogicalUnitShape),
           out var unrelatedSelectionShapeFailure)
       && unrelatedSelectionShapeFailure.Contains(
           "not the exact custom UnityEvent_Bool subtype",
           StringComparison.Ordinal),
    "An unrelated UnityEvent<bool> subclass must not pass exact event resolution.");

RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(
    true,
    1,
    unityThreadId);
RuntimeThrowDeliverOrderHighlightService.Attach(log);

Assert(Harmony.Patches.Count == 2,
    "Throw-delivery highlighting must patch exactly the panel open and close lifecycle methods.");
var openPatch = Harmony.Patches.Single(patch => patch.Target.Name == "OnPanelOpen");
var closePatch = Harmony.Patches.Single(patch => patch.Target.Name == "OnPanelClose");
Assert(openPatch.Prefix?.priority == Priority.First
       && openPatch.Postfix?.priority == Priority.Last,
    "OnPanelOpen must retire old ownership before native rebuild and bind after it finishes.");
Assert(closePatch.Prefix?.priority == Priority.First && closePatch.Postfix == null,
    "OnPanelClose must retire the Mod-owned fill before native close behavior.");
Assert(Harmony.Patches.All(patch => patch.Target.Name is "OnPanelOpen" or "OnPanelClose"),
    "No selection, delivery, coroutine, or generated method may be patched.");

// Hooks installed during an already-active business cover only the next generation.
var uncoveredOrder = new GuestsManager.NormalOrder(3);
var uncoveredController = new GuestGroupController(3);
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    1,
    CaptureTarget(uncoveredOrder, uncoveredController, 3, lifecycle: 11)));
RuntimeThrowDeliverOrderHighlightService.Resume("initial uncovered generation");
InvokeOpen(CreatePanel(Card(3, uncoveredOrder, uncoveredController)));
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status,
    "outside the exact covered business session",
    "An already-active generation at attach time must remain fail-closed.");
Assert(LiveOwnedFills().Length == 0,
    "An uncovered business generation must never create a fill.");

BeginBusiness(2);

// NormalOrder: select by keyed identity even when the pool and native siblings are shuffled.
var normalOrder = new GuestsManager.NormalOrder(3);
var normalController = new GuestGroupController(3);
var otherOrder = new GuestsManager.SpecialOrder(0);
var otherController = new GuestGroupController(0);
ClearRuntimeCaptures();
var normalTarget = CaptureTarget(normalOrder, normalController, 3, lifecycle: 21);
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(2, normalTarget));
var normalPanel = CreatePanel(
    Card(3, normalOrder, normalController),
    Card(0, otherOrder, otherController));
normalPanel.PoolDeskOrder.Add(0);
normalPanel.PoolDeskOrder.Add(3);
NativeVisualSnapshot? normalNative = null;
NativeVisualSnapshot? specialNative = null;
Time.realtimeSinceStartup = 0f;
GameObject.ReverseComponentEnumeration = true;
GameObject.DegradeTypedImageQuery = true;
var proxyQueriesBefore = GameObject.LogicalUnitProxyQueryCount;
var proxyCastsBefore = RuntimeReflectionUtility.LogicalUnitProxyCastCount;
InvokeOpen(normalPanel, afterOriginal: () =>
{
    var visuals = GetVisuals(normalPanel, 3);
    visuals.ValidBackground!.gameObject.name = "not-a-background-name";
    visuals.InvalidBackground!.gameObject.name = "selection-looking-name";
    visuals.SelectionOutline!.gameObject.name = "ordinary-leaf";
    visuals.Content.name = "valid-looking-content";
    visuals.Content.transform.SetSiblingIndex(0);
    GetSelectionEvent(normalPanel, 3).SetPersistentCalls(
        (visuals.SelectionOutline, "set_enabled"));
    normalNative = CaptureNativeVisuals(visuals);
    specialNative = CaptureNativeVisuals(GetVisuals(normalPanel, 0));
});
GameObject.ReverseComponentEnumeration = false;
GameObject.DegradeTypedImageQuery = false;

var normalVisuals = GetVisuals(normalPanel, 3);
var normalFill = SingleLiveOwnedFill(normalVisuals.Button);
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status,
    "active:1/1",
    "A verified NormalOrder target must publish the owned fill.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status,
    $"order:0x{normalOrder.Pointer:x}",
    "The binding must retain the exact native NormalOrder pointer.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status,
    $"controller:0x{normalController.Pointer:x}",
    "The binding must retain the exact controller pointer.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status,
    $"button:0x{normalVisuals.Button.Pointer:x}",
    "A shuffled pool must still resolve through the keyed logical unit.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status, "kind:Normal",
    "The NormalOrder branch must remain explicit in the active identity.");
Assert(GameObject.LogicalUnitProxyQueryCount > proxyQueriesBefore
       && RuntimeReflectionUtility.LogicalUnitProxyCastCount > proxyCastsBefore,
    "BepInEx 783 typed Component proxies must be recovered through the exact runtime cast.");
AssertOwnedFill(normalFill, normalVisuals, normalVisuals.ValidBackground!, normalNative!);
AssertNativeVisualsUnchanged(normalNative!, allowBackgroundEnabledChange: false);
AssertColorEquals(
    GetImage(normalFill).color,
    RuntimeTargetHighlightStyle.BuildOrderHighlightPulseColor(
        RuntimeUiTargetKinds.Normal,
        new RuntimeTargetHighlightPalette(
            RuntimeTargetHighlightColor.DefaultRare,
            RuntimeTargetHighlightColor.DefaultNormal),
        Time.realtimeSinceStartup),
    "The owned Image must use the configured normal-order blue pulse.");
Assert(AllLogMessages().Any(message =>
        message.Contains("target fill bound", StringComparison.Ordinal)),
    "Successful publication must emit a bounded target-fill bound log.");

// Pulse updates only the owned clone. Native focus-outline transitions remain game-owned.
var normalFillPointer = normalFill.Pointer;
var firstPulse = GetImage(normalFill).color;
normalVisuals.SelectionOutline!.enabled = true;
Time.realtimeSinceStartup = 0.8f;
RuntimeThrowDeliverOrderHighlightService.Tick();
Assert(SingleLiveOwnedFill(normalVisuals.Button).Pointer == normalFillPointer,
    "A focus-outline transition must not rebuild the owned background.");
Assert(normalVisuals.SelectionOutline.enabled,
    "The Mod must not overwrite the game's focused selection-outline state.");
Assert(GetImage(normalFill).color != firstPulse,
    "The owned fill must continue pulsing without native Image mutation.");
normalVisuals.SelectionOutline.enabled = false;
Time.realtimeSinceStartup = 1.2f;
RuntimeThrowDeliverOrderHighlightService.Tick();
Assert(!normalVisuals.SelectionOutline.enabled,
    "The Mod must preserve the game's unfocused selection-outline state.");
AssertNativeVisualsUnchanged(normalNative!, allowBackgroundEnabledChange: false);

// Native valid/invalid state switches rebuild from the newly effective background.
var firstOwnedImage = GetImage(normalFill);
normalVisuals.ValidBackground!.enabled = false;
normalVisuals.InvalidBackground!.enabled = true;
Time.realtimeSinceStartup = 2f;
RuntimeThrowDeliverOrderHighlightService.Tick();
var rebuiltFill = SingleLiveOwnedFill(normalVisuals.Button);
Assert(rebuiltFill.Pointer != normalFillPointer
       && normalFill.m_CachedPtr == IntPtr.Zero
       && firstOwnedImage.m_CachedPtr == IntPtr.Zero,
    "A valid/invalid background switch must destroy and replace the prior owned clone.");
AssertOwnedFill(rebuiltFill, normalVisuals, normalVisuals.InvalidBackground, normalNative!);
AssertNativeVisualsUnchanged(normalNative!, allowBackgroundEnabledChange: true);
Assert(log.Information.Any(message =>
        message.Contains("target fill bound", StringComparison.Ordinal)
        && message.Contains("reason=background-switched", StringComparison.Ordinal)),
    "A background-state rebuild must be visible through the bounded bound log.");

// Exact target changes retire the old clone and can bind a SpecialOrder on another keyed button.
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    2,
    CaptureTarget(otherOrder, otherController, 0, lifecycle: 22)));
Time.realtimeSinceStartup = 3f;
RuntimeThrowDeliverOrderHighlightService.Tick();
var specialVisuals = GetVisuals(normalPanel, 0);
var specialFill = SingleLiveOwnedFill(specialVisuals.Button);
Assert(rebuiltFill.m_CachedPtr == IntPtr.Zero,
    "Changing target identity must retire the previous button's fill.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status, "kind:Special",
    "The same strict pipeline must support SpecialOrder targets.");
AssertOwnedFill(
    specialFill,
    specialVisuals,
    specialVisuals.ValidBackground!,
    specialNative!);

// Invalid/disabled targets fail closed and explicitly retire ownership.
var retiredBeforeInvalidTarget = CountRetiredLogs();
var invalidTargetRejected = false;
try
{
    _ = new RuntimeUiTargetSnapshot(
        RuntimeUiTargetKind.Rare,
        RuntimeTargetHighlightColor.DefaultRare,
        listPinningEnabled: false,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: true,
        " R-1",
        "",
        22,
        0,
        -1,
        Array.Empty<int>(),
        Array.Empty<int>(),
        -1,
        -1,
        "invalid-trace");
}
catch (ArgumentException)
{
    invalidTargetRejected = true;
}
Assert(invalidTargetRejected,
    "An invalid exact trace must be rejected by immutable target construction.");
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(2));
Time.realtimeSinceStartup = 4f;
RuntimeThrowDeliverOrderHighlightService.Tick();
Assert(specialFill.m_CachedPtr == IntPtr.Zero && LiveOwnedFills().Length == 0,
    "Publishing no valid target after protocol rejection must retire the active fill.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status, "target disabled",
    "Rejected protocol input must not enter the runtime binding state.");
Assert(CountRetiredLogs() == retiredBeforeInvalidTarget + 1,
    "A target-validation change must emit one bounded target-fill retired log.");

ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    2,
    CaptureTarget(otherOrder, otherController, 0, lifecycle: 22)));
Time.realtimeSinceStartup = 5f;
RuntimeThrowDeliverOrderHighlightService.Tick();
var restoredFill = SingleLiveOwnedFill(specialVisuals.Button);
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(2));
Time.realtimeSinceStartup = 6f;
RuntimeThrowDeliverOrderHighlightService.Tick();
Assert(restoredFill.m_CachedPtr == IntPtr.Zero && LiveOwnedFills().Length == 0,
    "Disabling the feature target must destroy the owned fill.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status, "target disabled",
    "Disabled target status must not retain an active binding.");

// A removed pool membership invalidates the entire current identity.
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    2,
    CaptureTarget(otherOrder, otherController, 0, lifecycle: 22)));
Time.realtimeSinceStartup = 7f;
RuntimeThrowDeliverOrderHighlightService.Tick();
var poolFill = SingleLiveOwnedFill(specialVisuals.Button);
normalPanel.m_BtnInstances.Remove(specialVisuals.Button);
Time.realtimeSinceStartup = 8f;
RuntimeThrowDeliverOrderHighlightService.Tick();
Assert(poolFill.m_CachedPtr == IntPtr.Zero && LiveOwnedFills().Length == 0,
    "Loss of the unique live pool membership must retire the owned fill.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status, "membership in m_BtnInstances",
    "Pool-membership drift must be reported as a binding failure.");

// New panel open retires before native hierarchy replacement; normal close retires in prefix.
BeginBusiness(3);
var panelReplacementOrder = new GuestsManager.NormalOrder(2);
var panelReplacementController = new GuestGroupController(2);
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    3,
    CaptureTarget(panelReplacementOrder, panelReplacementController, 2, lifecycle: 31)));
var firstPanel = CreatePanel(Card(2, panelReplacementOrder, panelReplacementController));
InvokeOpen(firstPanel);
var firstPanelFill = SingleLiveOwnedFill(GetVisuals(firstPanel, 2).Button);
var replacementPanel = CreatePanel(Card(2, panelReplacementOrder, panelReplacementController));
InvokeOpen(
    replacementPanel,
    beforeOriginal: () => Assert(firstPanelFill.m_CachedPtr == IntPtr.Zero,
        "OnPanelOpen prefix must retire the prior panel fill before native rebuild."));
var replacementFill = SingleLiveOwnedFill(GetVisuals(replacementPanel, 2).Button);
var retiredBeforeStaleClose = CountRetiredLogs();
InvokeClose(firstPanel);
Assert(replacementFill.m_CachedPtr != IntPtr.Zero
       && SingleLiveOwnedFill(GetVisuals(replacementPanel, 2).Button).Pointer
           == replacementFill.Pointer,
    "A stale close callback from the replaced panel must not clear the current panel binding.");
Assert(CountRetiredLogs() == retiredBeforeStaleClose,
    "Ignoring a stale panel close must not retire the current panel's fill.");
var retiredBeforeClose = CountRetiredLogs();
replacementPanel.BeforeOriginalClose = () => Assert(
    replacementFill.m_CachedPtr == IntPtr.Zero && LiveOwnedFills().Length == 0,
    "OnPanelClose prefix must retire the fill before the game's original close method.");
InvokeClose(replacementPanel);
Assert(replacementPanel.OriginalCloseCalls == 1,
    "The passive lifecycle prefix must not skip the game's original close method.");
Assert(CountRetiredLogs() == retiredBeforeClose + 1,
    "Normal panel close must emit one bounded target-fill retired log.");

// Closing and Dispose destroy owned visuals; Destroyed abandonment only drops wrappers.
var closingPanel = CreatePanel(Card(2, panelReplacementOrder, panelReplacementController));
InvokeOpen(closingPanel);
var closingFill = SingleLiveOwnedFill(GetVisuals(closingPanel, 2).Button);
var retiredBeforeClosing = CountRetiredLogs();
RuntimeThrowDeliverOrderHighlightService.Suspend("night business closing");
Assert(closingFill.m_CachedPtr == IntPtr.Zero && LiveOwnedFills().Length == 0,
    "Closing suspension on the Unity thread must destroy the owned fill.");
Assert(CountRetiredLogs() == retiredBeforeClosing + 1,
    "Closing retirement must emit a bounded target-fill retired log.");

BeginBusiness(4);
var abandonOrder = new GuestsManager.SpecialOrder(4);
var abandonController = new GuestGroupController(4);
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    4,
    CaptureTarget(abandonOrder, abandonController, 4, lifecycle: 41)));
var abandonPanel = CreatePanel(Card(4, abandonOrder, abandonController));
InvokeOpen(abandonPanel);
var abandonedFill = SingleLiveOwnedFill(GetVisuals(abandonPanel, 4).Button);
var destroyedBeforeAbandon = GameObject.Destroyed.Count;
RuntimeThrowDeliverOrderHighlightService.Abandon("scene objects destroyed externally");
Assert(abandonedFill.m_CachedPtr != IntPtr.Zero
       && GameObject.Destroyed.Count == destroyedBeforeAbandon,
    "Destroyed-scene abandonment must never call Unity Destroy through stale wrappers.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status,
    "abandoned: scene objects destroyed externally",
    "Destroyed abandonment must leave an explicit wrapper-only state.");
UnityEngine.Object.Destroy(abandonPanel.gameObject);

BeginBusiness(5);
var disposeOrder = new GuestsManager.NormalOrder(5);
var disposeController = new GuestGroupController(5);
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    5,
    CaptureTarget(disposeOrder, disposeController, 5, lifecycle: 51)));
var disposePanel = CreatePanel(Card(5, disposeOrder, disposeController));
InvokeOpen(disposePanel);
var disposeFill = SingleLiveOwnedFill(GetVisuals(disposePanel, 5).Button);
RuntimeThrowDeliverOrderHighlightService.Dispose("smoke dispose");
Assert(disposeFill.m_CachedPtr == IntPtr.Zero && LiveOwnedFills().Length == 0,
    "Dispose on the Unity thread must destroy the owned fill.");

// A panel first opened while the target is disabled must bind on the first later enable.
var lateEnableGeneration = NextGeneration();
BeginBusiness(lateEnableGeneration);
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(lateEnableGeneration));
var lateEnableOrder = new GuestsManager.NormalOrder(6);
var lateEnableController = new GuestGroupController(6);
var lateEnablePanel = CreatePanel(Card(6, lateEnableOrder, lateEnableController));
InvokeOpen(lateEnablePanel);
Assert(LiveOwnedFills().Length == 0,
    "A panel opened with no target must not create a fill.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status, "target disabled",
    "The registered disabled panel must remain available for a later target.");
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    lateEnableGeneration,
    CaptureTarget(lateEnableOrder, lateEnableController, 6, lifecycle: 61)));
Time.realtimeSinceStartup += 1f;
RuntimeThrowDeliverOrderHighlightService.Tick();
Assert(SingleLiveOwnedFill(GetVisuals(lateEnablePanel, 6).Button).m_CachedPtr
       != IntPtr.Zero,
    "The first enable after panel open must bind without reopening the panel.");

// A new exact trace at the same desk retires and republishes against the same native button.
var sameDeskGeneration = NextGeneration();
BeginBusiness(sameDeskGeneration);
var sameDeskOrder = new GuestsManager.SpecialOrder(2);
var sameDeskController = new GuestGroupController(2);
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    sameDeskGeneration,
    CaptureTarget(sameDeskOrder, sameDeskController, 2, lifecycle: 71)));
var sameDeskPanel = CreatePanel(Card(2, sameDeskOrder, sameDeskController));
InvokeOpen(sameDeskPanel);
var sameDeskButton = GetVisuals(sameDeskPanel, 2).Button;
var firstSameDeskFill = SingleLiveOwnedFill(sameDeskButton);
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    sameDeskGeneration,
    CaptureTarget(sameDeskOrder, sameDeskController, 2, lifecycle: 72)));
Time.realtimeSinceStartup += 1f;
RuntimeThrowDeliverOrderHighlightService.Tick();
var secondSameDeskFill = SingleLiveOwnedFill(sameDeskButton);
Assert(firstSameDeskFill.m_CachedPtr == IntPtr.Zero
       && secondSameDeskFill.Pointer != firstSameDeskFill.Pointer,
    "A new lifecycle/trace at the same desk must replace, not reuse, the prior fill.");

// Reopening the same panel creates a genuinely fresh native pool and recovers automatically.
var poolRebindGeneration = NextGeneration();
BeginBusiness(poolRebindGeneration);
var poolRebindOrder = new GuestsManager.NormalOrder(7);
var poolRebindController = new GuestGroupController(7);
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    poolRebindGeneration,
    CaptureTarget(poolRebindOrder, poolRebindController, 7, lifecycle: 81)));
var poolRebindPanel = CreatePanel(Card(7, poolRebindOrder, poolRebindController));
InvokeOpen(poolRebindPanel);
var oldPoolButton = GetVisuals(poolRebindPanel, 7).Button;
var oldPoolFill = SingleLiveOwnedFill(oldPoolButton);
InvokeOpen(poolRebindPanel);
var freshPoolButton = GetVisuals(poolRebindPanel, 7).Button;
var freshPoolFill = SingleLiveOwnedFill(freshPoolButton);
Assert(oldPoolFill.m_CachedPtr == IntPtr.Zero
       && freshPoolButton.Pointer != oldPoolButton.Pointer
       && freshPoolFill.m_CachedPtr != IntPtr.Zero,
    "A real same-panel pool rebuild must retire old ownership and bind the fresh keyed button.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status,
    $"button:0x{freshPoolButton.Pointer:x}",
    "Recovered status must publish only the fresh pool button identity.");

// A full-stretch child may have a different local Rect origin from a non-centred parent.
// Anchors/offsets define exact coverage; comparing parent and child Rect values would
// incorrectly reject this normal Unity RectTransform shape.
var nonCentredParentGeneration = NextGeneration();
BeginBusiness(nonCentredParentGeneration);
var nonCentredParentOrder = new GuestsManager.NormalOrder(1);
var nonCentredParentController = new GuestGroupController(1);
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    nonCentredParentGeneration,
    CaptureTarget(nonCentredParentOrder, nonCentredParentController, 1, lifecycle: 91)));
var nonCentredParentPanel = CreatePanel(
    Card(1, nonCentredParentOrder, nonCentredParentController));
NativeVisualSnapshot? nonCentredParentNative = null;
InvokeOpen(nonCentredParentPanel, afterOriginal: () =>
{
    var visuals = GetVisuals(nonCentredParentPanel, 1);
    visuals.Button.transform.pivot = new Vector2(0f, 1f);
    visuals.Button.transform.rect = new Rect(0f, -84f, 116f, 84f);
    nonCentredParentNative = CaptureNativeVisuals(visuals);
});
var nonCentredParentVisuals = GetVisuals(nonCentredParentPanel, 1);
Assert(nonCentredParentVisuals.SelectionOutline!.gameObject.transform.pivot
           == new Vector2(0.5f, 0.5f)
       && nonCentredParentVisuals.SelectionOutline.gameObject.transform.rect
           == new Rect(-58f, -42f, 116f, 84f),
    "The regression fixture must retain the child-local centred selection Rect.");
var nonCentredParentFill = SingleLiveOwnedFill(nonCentredParentVisuals.Button);
AssertOwnedFill(
    nonCentredParentFill,
    nonCentredParentVisuals,
    nonCentredParentVisuals.ValidBackground!,
    nonCentredParentNative!);
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status,
    "active:1/1",
    "Exact same-geometry children must bind when a non-centred parent has a different local Rect origin.");

// The serialized selection listener and exact sibling geometry identify the visual region.
// The prefab is not required to use one hard-coded anchor/pivot/offset convention.
var nonCanonicalGeometryGeneration = NextGeneration();
BeginBusiness(nonCanonicalGeometryGeneration);
var nonCanonicalGeometryOrder = new GuestsManager.NormalOrder(2);
var nonCanonicalGeometryController = new GuestGroupController(2);
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    nonCanonicalGeometryGeneration,
    CaptureTarget(nonCanonicalGeometryOrder, nonCanonicalGeometryController, 2, lifecycle: 92)));
var nonCanonicalGeometryPanel = CreatePanel(
    Card(2, nonCanonicalGeometryOrder, nonCanonicalGeometryController));
NativeVisualSnapshot? nonCanonicalGeometryNative = null;
InvokeOpen(nonCanonicalGeometryPanel, afterOriginal: () =>
{
    var visuals = GetVisuals(nonCanonicalGeometryPanel, 2);
    ApplyNonCanonicalSiblingGeometry(visuals);
    nonCanonicalGeometryNative = CaptureNativeVisuals(visuals);
});
var nonCanonicalGeometryVisuals = GetVisuals(nonCanonicalGeometryPanel, 2);
var nonCanonicalGeometryFill = SingleLiveOwnedFill(nonCanonicalGeometryVisuals.Button);
AssertOwnedFill(
    nonCanonicalGeometryFill,
    nonCanonicalGeometryVisuals,
    nonCanonicalGeometryVisuals.ValidBackground!,
    nonCanonicalGeometryNative!);
Assert(log.Information.Any(message =>
        message.Contains("target fill bound", StringComparison.Ordinal)
        && message.Contains(
            "geometry=anchors=(0.12,0.18)->(0.88,0.83),pivot=(0.23,0.71)",
            StringComparison.Ordinal)),
    "Successful binding must log the exact bounded non-canonical geometry fingerprint.");

// A diagnostic must never re-read an IL2CPP wrapper after the owned visual is published.
// The geometry fingerprint is captured while source evidence is still inside its guarded
// read transaction, so a later getter failure cannot retire an otherwise valid fill.
var diagnosticIsolationGeneration = NextGeneration();
BeginBusiness(diagnosticIsolationGeneration);
var diagnosticIsolationOrder = new GuestsManager.NormalOrder(4);
var diagnosticIsolationController = new GuestGroupController(4);
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    diagnosticIsolationGeneration,
    CaptureTarget(diagnosticIsolationOrder, diagnosticIsolationController, 4, lifecycle: 93)));
var diagnosticIsolationPanel = CreatePanel(
    Card(4, diagnosticIsolationOrder, diagnosticIsolationController));
NativeVisualSnapshot? diagnosticIsolationNative = null;
InvokeOpen(diagnosticIsolationPanel, afterOriginal: () =>
{
    var visuals = GetVisuals(diagnosticIsolationPanel, 4);
    diagnosticIsolationNative = CaptureNativeVisuals(visuals);
    UnityEngine.Object.AfterInstantiate = () =>
    {
        visuals.SelectionOutline!.gameObject.transform.ThrowOnRectRead = true;
    };
});
var diagnosticIsolationVisuals = GetVisuals(diagnosticIsolationPanel, 4);
var diagnosticIsolationFill = SingleLiveOwnedFill(diagnosticIsolationVisuals.Button);
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status,
    "active:1/1",
    "A post-publication diagnostic must not re-read or invalidate the selection wrapper.");
Assert(log.Information.Any(message =>
        message.Contains("target fill bound", StringComparison.Ordinal)
        && message.Contains($"target={diagnosticIsolationGeneration}", StringComparison.Ordinal)),
    "The guarded evidence fingerprint must remain available to the success diagnostic.");
diagnosticIsolationVisuals.SelectionOutline!.gameObject.transform.ThrowOnRectRead = false;
AssertOwnedFill(
    diagnosticIsolationFill,
    diagnosticIsolationVisuals,
    diagnosticIsolationVisuals.ValidBackground!,
    diagnosticIsolationNative!);

// Strict source-shape rejection matrix. Every case uses a fresh business identity.
RunMalformedCase("zero selection listener", panel =>
{
    GetSelectionEvent(panel, 1).SetPersistentCalls();
}, expectedFailure: "listener");
RunMalformedCase("single selection listener uses the wrong method", panel =>
{
    var visuals = GetVisuals(panel, 1);
    GetSelectionEvent(panel, 1).SetPersistentCalls(
        (visuals.SelectionOutline, "not_the_enabled_setter"));
}, expectedFailure: "exact Image enabled setter");
RunMalformedCase("multiple selection listeners", panel =>
{
    var visuals = GetVisuals(panel, 1);
    GetSelectionEvent(panel, 1).SetPersistentCalls(
        (visuals.SelectionOutline, "a"),
        (visuals.ValidBackground, "b"));
}, expectedFailure: "listener");
RunMalformedCase("listener points to owner rather than exact Image", panel =>
{
    var visuals = GetVisuals(panel, 1);
    GetSelectionEvent(panel, 1).SetPersistentCalls(
        (visuals.SelectionOutline!.gameObject, "set_enabled"));
}, expectedFailure: "Image");
RunMalformedCase("target button parent owns a LayoutGroup", panel =>
{
    GetVisuals(panel, 1).Button.Attach(new UnityEngine.UI.VerticalLayoutGroup());
}, expectedFailure: "LayoutGroup");
RunMalformedCase("selection Image geometry differs from both backgrounds", panel =>
{
    GetVisuals(panel, 1).SelectionOutline!.gameObject.transform.sizeDelta =
        new Vector2(2f, 0f);
}, expectedFailure: "differentImageGeometry=");
RunMalformedCase("selection Image geometry contains a non-finite value", panel =>
{
    GetVisuals(panel, 1).SelectionOutline!.gameObject.transform.anchorMin =
        new Vector2(float.NaN, 0f);
}, expectedFailure: "finite positive render geometry");
RunMalformedCase("selection Image rect has zero width", panel =>
{
    GetVisuals(panel, 1).SelectionOutline!.gameObject.transform.rect =
        new Rect(0f, 0f, 0f, 84f);
}, expectedFailure: "finite positive render geometry");
RunMalformedCase("a background is ordered after selection", panel =>
{
    var visuals = GetVisuals(panel, 1);
    visuals.ValidBackground!.transform.SetSiblingIndex(
        visuals.Button.transform.childCount - 1);
}, expectedFailure: "ordered before");
RunMalformedCase("only one exact background", panel =>
{
    var visuals = GetVisuals(panel, 1);
    visuals.InvalidBackground!.gameObject.Detach(
        visuals.InvalidBackground.gameObject.GetComponent(typeof(CanvasRenderer))!);
}, expectedFailure: "background");
RunMalformedCase("three exact backgrounds", panel =>
{
    _ = AddExactBackgroundLeaf(GetVisuals(panel, 1).Button, "third");
}, expectedFailure: "background");
RunMalformedCase("both backgrounds enabled", panel =>
{
    var visuals = GetVisuals(panel, 1);
    visuals.ValidBackground!.enabled = true;
    visuals.InvalidBackground!.enabled = true;
}, expectedFailure: "enabled XOR");
RunMalformedCase("both backgrounds disabled", panel =>
{
    var visuals = GetVisuals(panel, 1);
    visuals.ValidBackground!.enabled = false;
    visuals.InvalidBackground!.enabled = false;
}, expectedFailure: "enabled XOR");
RunMalformedCase("active hierarchy disagrees with enabled state", panel =>
{
    var visuals = GetVisuals(panel, 1);
    visuals.ValidBackground!.gameObject.SetActive(false);
}, expectedFailure: "both active");
RunMalformedCase("enabled background has null sprite", panel =>
{
    GetVisuals(panel, 1).ValidBackground!.sprite = null;
}, expectedFailure: "sprite");
RunMalformedCase("background geometry differs", panel =>
{
    GetVisuals(panel, 1).InvalidBackground!.gameObject.transform.offsetMax =
        new Vector2(0.000001f, 0f);
}, expectedFailure: "1E-06");
RunMalformedCase("background anchor minimum differs", panel =>
{
    GetVisuals(panel, 1).InvalidBackground!.gameObject.transform.anchorMin =
        new Vector2(0.00001f, 0f);
}, expectedFailure: "background");
RunMalformedCase("background anchor maximum differs", panel =>
{
    GetVisuals(panel, 1).InvalidBackground!.gameObject.transform.anchorMax =
        new Vector2(1f, 0.99999f);
}, expectedFailure: "background");
RunMalformedCase("background pivot differs", panel =>
{
    GetVisuals(panel, 1).InvalidBackground!.gameObject.transform.pivot =
        new Vector2(0.50001f, 0.5f);
}, expectedFailure: "background");
RunMalformedCase("background anchored position differs", panel =>
{
    GetVisuals(panel, 1).InvalidBackground!.gameObject.transform.anchoredPosition =
        new Vector2(0.00001f, 0f);
}, expectedFailure: "background");
RunMalformedCase("background size delta differs", panel =>
{
    GetVisuals(panel, 1).InvalidBackground!.gameObject.transform.sizeDelta =
        new Vector2(0.00001f, 0f);
}, expectedFailure: "background");
RunMalformedCase("background offset minimum differs", panel =>
{
    GetVisuals(panel, 1).InvalidBackground!.gameObject.transform.offsetMin =
        new Vector2(0.00001f, 0f);
}, expectedFailure: "background");
RunMalformedCase("background local scale differs", panel =>
{
    GetVisuals(panel, 1).InvalidBackground!.gameObject.transform.localScale =
        new Vector3(1.00001f, 1f, 1f);
}, expectedFailure: "background");
RunMalformedCase("background local rect differs", panel =>
{
    GetVisuals(panel, 1).InvalidBackground!.gameObject.transform.rect =
        new Rect(-58.00001f, -42f, 116f, 84f);
}, expectedFailure: "background");
RunMalformedCase("background is not a leaf", panel =>
{
    var child = new GameObject { name = "nested" };
    child.transform.parent = GetVisuals(panel, 1).InvalidBackground!.transform;
}, expectedFailure: "background");
RunMalformedCase("background has an extra component", panel =>
{
    GetVisuals(panel, 1).InvalidBackground!.gameObject.Attach(new CanvasGroup());
}, expectedFailure: "component");
RunMalformedCase("background native Image class drifts", panel =>
{
    GetVisuals(panel, 1).InvalidBackground!.OverrideNativeType(
        typeof(UnityEngine.UI.DerivedImage));
}, expectedFailure: "Image");
RunMalformedCase("background Image render types disagree", panel =>
{
    GetVisuals(panel, 1).InvalidBackground!.type = UnityEngine.UI.Image.Type.Tiled;
}, expectedFailure: "same exact Image render type");

// Partial clone construction is transactional and never publishes a fallback visual.
RunCloneFaultCase(CloneFaultMode.RemoveImage, "exact leaf");
RunCloneFaultCase(CloneFaultMode.AddUnknownComponent, "exact leaf");
RunCloneFaultCase(CloneFaultMode.InvalidateImage, "exact leaf");
RunCloneFaultCase(CloneFaultMode.None, "Injected SetSiblingIndex", injectSiblingFailure: true);

// Health-check shape and identity drift also fail closed after successful publication.
RunBoundDriftCase("selection listener missing", (panel, _, _) =>
{
    GetSelectionEvent(panel, 1).SetPersistentCalls();
});
RunBoundDriftCase("selection target identity drift", (panel, visuals, _) =>
{
    GetSelectionEvent(panel, 1).SetPersistentCalls(
        (visuals.ValidBackground, "set_enabled"));
});
RunBoundDriftCase("background exact shape disappears", (_, visuals, _) =>
{
    var backgroundOwner = visuals.InvalidBackground!.gameObject;
    backgroundOwner.Detach(backgroundOwner.GetComponent(typeof(CanvasRenderer))!);
});
RunBoundDriftCase("background geometry drift", (_, visuals, _) =>
{
    visuals.InvalidBackground!.gameObject.transform.sizeDelta = new Vector2(2f, 0f);
});
RunBoundDriftCase("background native identity drift", (_, visuals, _) =>
{
    visuals.ValidBackground!.InvalidateNativePointer();
});
RunBoundDriftCase("owned Image becomes dead", (_, _, fill) =>
{
    GetImage(fill).InvalidateNativePointer();
}, expectAbandon: true);
RunBoundDriftCase("a sibling is inserted between fill and selection", (_, visuals, fill) =>
{
    var inserted = new GameObject { name = "unrelated-native-sibling" };
    inserted.transform.parent = visuals.Button.transform;
    inserted.transform.SetSiblingIndex(
        visuals.SelectionOutline!.transform.GetSiblingIndex());
    Assert(fill.transform.GetSiblingIndex() + 2
           == visuals.SelectionOutline.transform.GetSiblingIndex(),
        "The fixture must put exactly one unrelated sibling between fill and selection.");
});
RunBoundDriftCase("owned parent drift", (_, _, fill) =>
{
    var unrelatedParent = new GameObject { name = "unrelated-parent" };
    fill.transform.parent = unrelatedParent.transform;
});
RunBoundDriftCase("owned sibling drift", (_, _, fill) =>
{
    fill.transform.SetSiblingIndex(0);
});
RunBoundDriftCase("owned raycast drift", (_, _, fill) =>
{
    GetImage(fill).raycastTarget = true;
});
RunBoundDriftCase("owned enabled drift", (_, _, fill) =>
{
    GetImage(fill).enabled = false;
});
RunBoundDriftCase("owned sprite drift", (_, _, fill) =>
{
    GetImage(fill).sprite = new Sprite { name = "foreign-sprite" };
});
RunBoundDriftCase("owned material drift", (_, _, fill) =>
{
    GetImage(fill).material = new Material { name = "foreign-material" };
});
RunBoundDriftCase("owned Image render type drift", (_, _, fill) =>
{
    GetImage(fill).type = UnityEngine.UI.Image.Type.Tiled;
});

// Managed wrappers rebound to a different native object are unsafe: no pulse and no Destroy.
foreach (var triggerRetirement in new[] { false, true })
{
    RunUnsafeOwnedIdentityCase(
        "owned GameObject native identity rebind",
        (fill, _) => fill.RebindNativeIdentity(),
        triggerRetirement);
    RunUnsafeOwnedIdentityCase(
        "owned Image native identity rebind",
        (_, image) => image.RebindNativeIdentity(),
        triggerRetirement);
    RunUnsafeOwnedIdentityCase(
        "owned Image owner drift",
        (_, image) =>
        {
            var unrelatedOwner = new GameObject { name = "unrelated-image-owner" };
            image.Rebind(unrelatedOwner);
        },
        triggerRetirement);
}

// Repeated identical failures are deduplicated, and distinct failures remain business-bounded.
var budgetGeneration = NextGeneration();
BeginBusiness(budgetGeneration);
var budgetOrder = new GuestsManager.NormalOrder(1);
var budgetController = new GuestGroupController(1);
ClearRuntimeCaptures();
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    budgetGeneration,
    CaptureTarget(budgetOrder, budgetController, 1, lifecycle: 9001)));
var repeatedFailurePanel = CreatePanel(Card(1, budgetOrder, budgetController));
var failureLogsBefore = CountFailureLogs();
InvokeOpen(repeatedFailurePanel, afterOriginal: () =>
    GetSelectionEvent(repeatedFailurePanel, 1).SetPersistentCalls());
var failureLogsAfterOpen = CountFailureLogs();
for (var retry = 0; retry < 20; retry++)
{
    Time.realtimeSinceStartup += 1f;
    RuntimeThrowDeliverOrderHighlightService.Tick();
}
Assert(CountFailureLogs() == failureLogsAfterOpen
       && failureLogsAfterOpen == failureLogsBefore + 1,
    "One unchanged visual-source failure must log once across repeated bind retries.");
for (var index = 0; index < 12; index++)
{
    var order = new GuestsManager.NormalOrder(1);
    var controller = new GuestGroupController(1);
    ClearRuntimeCaptures();
    RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
        budgetGeneration,
        CaptureTarget(order, controller, 1, lifecycle: 9100 + index)));
    var badPanel = CreatePanel(Card(1, order, controller));
    InvokeOpen(badPanel, afterOriginal: () =>
    {
        for (var extra = 0; extra <= index; extra++)
        {
            _ = AddExactBackgroundLeaf(
                GetVisuals(badPanel, 1).Button,
                $"budget-{index}-{extra}");
        }
    });
}
Assert(CountFailureLogs() - failureLogsBefore == 8,
    "Distinct target-fill failures must stop exactly at the per-business warning budget.");

// Rare and normal targets bind independently from their real capture stores and colors.
var dualGeneration = NextGeneration();
BeginBusiness(dualGeneration);
ClearRuntimeCaptures();
var dualRareOrder = new GuestsManager.SpecialOrder(0);
var dualRareController = new GuestGroupController(0);
var dualNormalOrder = new GuestsManager.NormalOrder(3);
var dualNormalController = new GuestGroupController(3);
var dualRareColor = new RuntimeTargetHighlightColor(0xDE, 0x91, 0x25);
var dualNormalColor = new RuntimeTargetHighlightColor(0x2A, 0x82, 0xC5);
var dualRareTarget = CaptureTarget(
    dualRareOrder,
    dualRareController,
    0,
    lifecycle: 12_001,
    dualRareColor);
var dualNormalTarget = CaptureTarget(
    dualNormalOrder,
    dualNormalController,
    3,
    lifecycle: 12_002,
    dualNormalColor);
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    dualGeneration,
    dualRareTarget,
    dualNormalTarget));
var dualPanel = CreatePanel(
    Card(0, dualRareOrder, dualRareController),
    Card(3, dualNormalOrder, dualNormalController));
Time.realtimeSinceStartup = 20f;
InvokeOpen(dualPanel);
var dualRareVisuals = GetVisuals(dualPanel, 0);
var dualNormalVisuals = GetVisuals(dualPanel, 3);
var dualRareFill = SingleLiveOwnedFill(dualRareVisuals.Button);
var dualNormalFill = SingleLiveOwnedFill(dualNormalVisuals.Button);
var dualPalette = new RuntimeTargetHighlightPalette(dualRareColor, dualNormalColor);
AssertColorEquals(
    GetImage(dualRareFill).color,
    RuntimeTargetHighlightStyle.BuildOrderHighlightPulseColor(
        RuntimeUiTargetKinds.Rare,
        dualPalette,
        Time.realtimeSinceStartup),
    "The rare throw-delivery card must use its injected color.");
AssertColorEquals(
    GetImage(dualNormalFill).color,
    RuntimeTargetHighlightStyle.BuildOrderHighlightPulseColor(
        RuntimeUiTargetKinds.Normal,
        dualPalette,
        Time.realtimeSinceStartup),
    "The normal throw-delivery card must use its injected color from a real normal capture.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status, "active:2/2",
    "Both exact throw-delivery targets must bind simultaneously.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status, "Rare:target:",
    "The active binding directory must expose the rare slot.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status, "Normal:target:",
    "The active binding directory must expose the normal slot.");

var retainedNormalFillPointer = dualNormalFill.Pointer;
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    dualGeneration,
    dualNormalTarget));
Time.realtimeSinceStartup = 20.1f;
RuntimeThrowDeliverOrderHighlightService.Tick();
Assert(dualRareFill.m_CachedPtr == IntPtr.Zero,
    "Removing the rare target slot must retire only its owned delivery fill.");
Assert(dualNormalFill.m_CachedPtr != IntPtr.Zero
       && SingleLiveOwnedFill(dualNormalVisuals.Button).Pointer == retainedNormalFillPointer,
    "Removing one target slot must preserve the other slot's exact delivery fill identity.");

RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    dualGeneration,
    dualRareTarget,
    dualNormalTarget));
Time.realtimeSinceStartup = 20.2f;
RuntimeThrowDeliverOrderHighlightService.Tick();
var restoredDualRareFill = SingleLiveOwnedFill(dualRareVisuals.Button);
dualRareVisuals.InvalidBackground!.gameObject.transform.sizeDelta = new Vector2(1f, 0f);
Time.realtimeSinceStartup = 21f;
RuntimeThrowDeliverOrderHighlightService.Tick();
Assert(restoredDualRareFill.m_CachedPtr == IntPtr.Zero,
    "A rare-card health drift must retire the rare fill.");
Assert(dualNormalFill.m_CachedPtr != IntPtr.Zero
       && SingleLiveOwnedFill(dualNormalVisuals.Button).Pointer == retainedNormalFillPointer,
    "A failure in one delivery binding must not clear the other target kind.");

// Two typed targets at one desk never stack: the panel's exact current order selects one kind.
var sharedDeskGeneration = NextGeneration();
BeginBusiness(sharedDeskGeneration);
ClearRuntimeCaptures();
var sharedDeskRareOrder = new GuestsManager.SpecialOrder(2);
var sharedDeskRareController = new GuestGroupController(2);
var sharedDeskNormalOrder = new GuestsManager.NormalOrder(2);
var sharedDeskNormalController = new GuestGroupController(2);
var sharedDeskRareTarget = CaptureTarget(
    sharedDeskRareOrder,
    sharedDeskRareController,
    2,
    lifecycle: 13_001);
var sharedDeskNormalTarget = CaptureTarget(
    sharedDeskNormalOrder,
    sharedDeskNormalController,
    2,
    lifecycle: 13_002);
RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
    sharedDeskGeneration,
    sharedDeskRareTarget,
    sharedDeskNormalTarget));
var sharedDeskPanel = CreatePanel(Card(2, sharedDeskRareOrder, sharedDeskRareController));
InvokeOpen(sharedDeskPanel);
Assert(LiveOwnedFills().Length == 1,
    "A shared desk with one exact current panel order must publish only one owned fill.");
AssertContains(RuntimeThrowDeliverOrderHighlightService.Status, "Rare:target:",
    "The matching rare panel order must own the shared-desk fill.");
Assert(!RuntimeThrowDeliverOrderHighlightService.Status.Contains("Normal:target:", StringComparison.Ordinal),
    "A normal target must not bind to a same-desk SpecialOrder by desk fallback.");
RuntimeThrowDeliverOrderHighlightService.Dispose("dual target throw-delivery coverage complete");

AuditProductionSource();
Console.WriteLine("Runtime throw-delivery order highlight smoke checks passed.");

long NextGeneration() => ++generationSequence;

void BeginBusiness(long generation)
{
    RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(
        true,
        generation,
        unityThreadId);
    RuntimeThrowDeliverOrderHighlightService.Resume($"business {generation}");
    Time.realtimeSinceStartup = 0f;
}

void RunMalformedCase(
    string scenario,
    Action<WorkSceneThrowDeliverPanel> mutate,
    string expectedFailure)
{
    var generation = NextGeneration();
    BeginBusiness(generation);
    var order = new GuestsManager.NormalOrder(1);
    var controller = new GuestGroupController(1);
    ClearRuntimeCaptures();
    RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
        generation,
        CaptureTarget(order, controller, 1, lifecycle: generation * 100 + 1)));
    var panel = CreatePanel(Card(1, order, controller));
    var instantiatedBefore = GameObject.Instantiated.Count;
    InvokeOpen(panel, afterOriginal: () => mutate(panel));
    Assert(LiveOwnedFills().Length == 0,
        $"Malformed case '{scenario}' must not publish an owned fill.");
    AssertContains(RuntimeThrowDeliverOrderHighlightService.Status, expectedFailure,
        $"Malformed case '{scenario}' must retain a precise failure.");
    Assert(GameObject.Instantiated.Count == instantiatedBefore,
        $"Malformed source case '{scenario}' must fail before clone construction.");
}

void RunCloneFaultCase(
    CloneFaultMode fault,
    string expectedFailure,
    bool injectSiblingFailure = false)
{
    var generation = NextGeneration();
    BeginBusiness(generation);
    var order = new GuestsManager.SpecialOrder(1);
    var controller = new GuestGroupController(1);
    ClearRuntimeCaptures();
    RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
        generation,
        CaptureTarget(order, controller, 1, lifecycle: generation * 100 + 2)));
    var panel = CreatePanel(Card(1, order, controller));
    var instantiatedBefore = GameObject.Instantiated.Count;
    var destroyedBefore = GameObject.Destroyed.Count;
    GameObject.NextCloneFault = fault;
    Transform.FailNextSetSiblingIndex = injectSiblingFailure;
    InvokeOpen(panel);
    Assert(LiveOwnedFills().Length == 0,
        $"Clone fault {fault}/{injectSiblingFailure} must never publish a partial fill.");
    Assert(GameObject.Instantiated.Count == instantiatedBefore + 1
           && GameObject.Destroyed.Count == destroyedBefore + 1,
        $"Clone fault {fault}/{injectSiblingFailure} must destroy the one partial clone transactionally.");
    AssertContains(RuntimeThrowDeliverOrderHighlightService.Status, expectedFailure,
        $"Clone fault {fault}/{injectSiblingFailure} must retain its exact failure.");
}

void RunBoundDriftCase(
    string scenario,
    Action<WorkSceneThrowDeliverPanel, ThrowDeliverButtonVisuals, GameObject> mutate,
    bool expectAbandon = false)
{
    var generation = NextGeneration();
    BeginBusiness(generation);
    var order = new GuestsManager.NormalOrder(1);
    var controller = new GuestGroupController(1);
    ClearRuntimeCaptures();
    RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
        generation,
        CaptureTarget(order, controller, 1, lifecycle: generation * 100 + 3)));
    var panel = CreatePanel(Card(1, order, controller));
    InvokeOpen(panel);
    var visuals = GetVisuals(panel, 1);
    var fill = SingleLiveOwnedFill(visuals.Button);
    var setActiveCallsBefore = fill.SetActiveCalls;
    var destroyedBefore = GameObject.Destroyed.Count;
    mutate(panel, visuals, fill);
    Time.realtimeSinceStartup += 1f;
    RuntimeThrowDeliverOrderHighlightService.Tick();
    var remainingOwned = LiveOwnedFills();
    Assert(!RuntimeThrowDeliverOrderHighlightService.Status.Contains(
            "active:1/1",
            StringComparison.Ordinal),
        $"Bound drift '{scenario}' must clear the active publication.");
    if (expectAbandon)
    {
        Assert(remainingOwned.Length == 1
               && ReferenceEquals(remainingOwned[0], fill)
               && fill.m_CachedPtr != IntPtr.Zero
               && fill.SetActiveCalls == setActiveCallsBefore
               && GameObject.Destroyed.Count == destroyedBefore,
            $"Unsafe bound drift '{scenario}' must abandon without SetActive or Destroy. "
            + $"Remaining={remainingOwned.Length}; Status={RuntimeThrowDeliverOrderHighlightService.Status}");
        UnityEngine.Object.Destroy(fill);
        return;
    }

    Assert(remainingOwned.Length == 0 && fill.m_CachedPtr == IntPtr.Zero,
        $"Bound drift '{scenario}' must destroy the safely-owned fill and never rebuild. "
        + $"Remaining={string.Join(',', remainingOwned.Select(owner => $"0x{owner.Pointer:x}/image:0x{GetImage(owner).m_CachedPtr:x}"))}; "
        + $"Status={RuntimeThrowDeliverOrderHighlightService.Status}");
}

void RunUnsafeOwnedIdentityCase(
    string scenario,
    Action<GameObject, UnityEngine.UI.Image> mutate,
    bool triggerRetirement)
{
    var generation = NextGeneration();
    BeginBusiness(generation);
    var order = new GuestsManager.SpecialOrder(1);
    var controller = new GuestGroupController(1);
    ClearRuntimeCaptures();
    RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(
        generation,
        CaptureTarget(order, controller, 1, lifecycle: generation * 100 + 4)));
    var panel = CreatePanel(Card(1, order, controller));
    InvokeOpen(panel);
    var fill = SingleLiveOwnedFill(GetVisuals(panel, 1).Button);
    var image = GetImage(fill);
    var originalOwnerPointer = fill.Pointer;
    var originalImagePointer = image.Pointer;
    var originalImageOwner = image.gameObject;
    var originalColor = image.color;
    var colorWritesBefore = image.ColorWriteCount;
    var setActiveCallsBefore = fill.SetActiveCalls;
    var destroyedBefore = GameObject.Destroyed.Count;

    mutate(fill, image);
    Assert(fill.Pointer != originalOwnerPointer
           || image.Pointer != originalImagePointer
           || !ReferenceEquals(image.gameObject, originalImageOwner),
        $"Unsafe identity fixture '{scenario}' did not actually change native identity or ownership.");

    if (triggerRetirement)
    {
        RuntimeThrowDeliverOrderHighlightService.UpdateTargets(CreateTargetSet(generation));
    }
    Time.realtimeSinceStartup += 1f;
    RuntimeThrowDeliverOrderHighlightService.Tick();

    Assert(image.ColorWriteCount == colorWritesBefore && image.color == originalColor,
        $"Unsafe identity '{scenario}' must be rejected before any pulse color write.");
    Assert(fill.m_CachedPtr != IntPtr.Zero
           && fill.activeSelf
           && fill.SetActiveCalls == setActiveCallsBefore
           && GameObject.Destroyed.Count == destroyedBefore
           && !GameObject.Destroyed.Contains(fill),
        $"Unsafe identity '{scenario}' must never SetActive or Destroy the rebound live wrapper.");
    AssertContains(RuntimeThrowDeliverOrderHighlightService.Status, "bindings=",
        $"Unsafe identity '{scenario}' must clear publication after "
        + (triggerRetirement ? "explicit retirement" : "pulse validation"));

    // The simulated scene now owns the abandoned wrapper; explicit fixture cleanup follows assertions.
    UnityEngine.Object.Destroy(fill);
}

ThrowDeliverCardSpec Card(
    int deskCode,
    GuestsManager.OrderBase order,
    GuestGroupController controller) =>
    new(deskCode, order, controller, new Vector3(deskCode * 10f, 2f, 3f));

WorkSceneThrowDeliverPanel CreatePanel(params ThrowDeliverCardSpec[] entries)
{
    var panel = new WorkSceneThrowDeliverPanel();
    panel.Entries.AddRange(entries);
    return panel;
}

ThrowDeliverButtonVisuals GetVisuals(
    WorkSceneThrowDeliverPanel panel,
    int deskCode) =>
    panel.BuiltVisuals[deskCode];

DEYU.AdpUISystem.LogicalCollection.UILogicalUnit GetLogicalUnit(
    WorkSceneThrowDeliverPanel panel,
    int deskCode) =>
    panel.BuiltButtons[deskCode]
        .GetComponent(typeof(DEYU.AdpUISystem.LogicalCollection.UILogicalUnit))
        as DEYU.AdpUISystem.LogicalCollection.UILogicalUnit
    ?? throw new InvalidOperationException("Fixture keyed UILogicalUnit is missing.");

DEYU.AdpUISystem.Utils.AdpUISystemUtils.UnityEvent_Bool GetSelectionEvent(
    WorkSceneThrowDeliverPanel panel,
    int deskCode) =>
    GetLogicalUnit(panel, deskCode).m_OnSelectionUpdateCallback;

bool TryResolveSelectionEventShape(Type unitType, out string failure)
{
    var resolver = typeof(RuntimeThrowDeliverOrderHighlightService).GetMethod(
        "TryResolveSelectionEventSerializedMembers",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("Selection-event exact resolver is missing.");
    object?[] arguments = { unitType, null, null, null };
    var resolved = resolver.Invoke(null, arguments) as bool? == true;
    failure = arguments[3] as string ?? "resolver returned no staged failure";
    return resolved;
}

RuntimeUiTargetSetSnapshot CreateTargetSet(
    long sessionGeneration,
    params RuntimeUiTargetSnapshot[] targets) => new(
    ++targetSetGeneration,
    sessionGeneration,
    targets);

void ClearRuntimeCaptures()
{
    SpecialOrderRuntimeCapture.Captures.Clear();
    NormalOrderRuntimeCapture.Captures.Clear();
}

RuntimeUiTargetSnapshot CaptureTarget(
    GuestsManager.OrderBase order,
    GuestGroupController controller,
    int deskCode,
    long lifecycle,
    RuntimeTargetHighlightColor? color = null)
{
    var at = DateTime.UtcNow.AddMilliseconds(order.Pointer.ToInt64() % 997);
    if (order is GuestsManager.NormalOrder)
    {
        var capture = new CapturedRuntimeNormalOrder(
            $"ptr:{order.Pointer:x}",
            deskCode,
            at,
            at)
        {
            OrderObject = order,
            ControllerObject = controller,
            OrderLifecycleSequence = lifecycle,
        };
        NormalOrderRuntimeCapture.Captures.Add(capture);
        return new RuntimeUiTargetSnapshot(
            RuntimeUiTargetKind.Normal,
            color ?? RuntimeTargetHighlightColor.DefaultNormal,
            listPinningEnabled: false,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: true,
            RuntimeOrderTraceIdService.GetNormalTraceId(capture),
            capture.RuntimeKey,
            lifecycle,
            deskCode,
            -1,
            Array.Empty<int>(),
            Array.Empty<int>(),
            -1,
            -1,
            $"throw-normal:{lifecycle}:{order.Pointer:x}");
    }

    if (order is not GuestsManager.SpecialOrder)
    {
        throw new InvalidOperationException("The throw-delivery fixture requires an exact NormalOrder or SpecialOrder.");
    }

    var specialCapture = new CapturedRuntimeSpecialOrder(
        deskCode,
        100 + deskCode,
        "Target",
        20 + deskCode,
        40 + deskCode,
        false,
        false,
        at,
        at,
        $"ptr:{order.Pointer:x}",
        "ThrowDeliverySmoke")
    {
        OrderObject = order,
        ControllerObject = controller,
        OrderLifecycleSequence = lifecycle,
    };
    SpecialOrderRuntimeCapture.Captures.Add(specialCapture);
    return new RuntimeUiTargetSnapshot(
        RuntimeUiTargetKind.Rare,
        color ?? RuntimeTargetHighlightColor.DefaultRare,
        listPinningEnabled: false,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: true,
        RuntimeOrderTraceIdService.GetRareTraceId(specialCapture),
        "",
        lifecycle,
        deskCode,
        -1,
        Array.Empty<int>(),
        Array.Empty<int>(),
        -1,
        -1,
        $"throw-rare:{lifecycle}:{order.Pointer:x}");
}

void InvokeOpen(
    WorkSceneThrowDeliverPanel panel,
    Action? beforeOriginal = null,
    Action? afterOriginal = null)
{
    var prefixArguments = new object?[] { panel, null };
    openPatch.Prefix!.methodInfo.Invoke(null, prefixArguments);
    beforeOriginal?.Invoke();
    panel.OnPanelOpen();
    afterOriginal?.Invoke();
    openPatch.Postfix!.methodInfo.Invoke(null, new[] { panel, prefixArguments[1] });
}

void InvokeClose(WorkSceneThrowDeliverPanel panel)
{
    closePatch.Prefix!.methodInfo.Invoke(null, new object?[] { panel });
    panel.OnPanelClose();
}

GameObject[] LiveOwnedFills() => GameObject.Instantiated
    .Where(candidate => candidate.m_CachedPtr != IntPtr.Zero
        && string.Equals(candidate.name, OwnedFillName, StringComparison.Ordinal))
    .ToArray();

GameObject SingleLiveOwnedFill(GameObject parent)
{
    var matches = LiveOwnedFills()
        .Where(candidate => ReferenceEquals(candidate.transform.parent?.gameObject, parent))
        .ToArray();
    Assert(matches.Length == 1,
        $"Expected one live owned fill below exact button 0x{parent.Pointer:x}, got {matches.Length}.");
    return matches[0];
}

UnityEngine.UI.Image GetImage(GameObject owner) =>
    owner.GetComponent(typeof(UnityEngine.UI.Image)) as UnityEngine.UI.Image
    ?? throw new InvalidOperationException("Exact Image is missing.");

NativeVisualSnapshot CaptureNativeVisuals(ThrowDeliverButtonVisuals visuals)
{
    var nativeOrder = visuals.Button.transform.Children
        .Select(child => child.gameObject.Pointer)
        .ToArray();
    return new NativeVisualSnapshot(
        visuals,
        nativeOrder,
        CaptureImage(visuals.ValidBackground!),
        CaptureImage(visuals.InvalidBackground!),
        CaptureImage(visuals.SelectionOutline!));
}

NativeImageSnapshot CaptureImage(UnityEngine.UI.Image image) => new(
    image,
    image.color,
    image.raycastTarget,
    image.enabled,
    image.sprite,
    image.material,
    image.type,
    image.gameObject.activeSelf,
    image.gameObject.transform.rect,
    image.gameObject.transform.anchorMin,
    image.gameObject.transform.anchorMax,
    image.gameObject.transform.pivot,
    image.gameObject.transform.anchoredPosition,
    image.gameObject.transform.sizeDelta,
    image.gameObject.transform.offsetMin,
    image.gameObject.transform.offsetMax,
    image.gameObject.transform.localScale);

void AssertOwnedFill(
    GameObject fill,
    ThrowDeliverButtonVisuals visuals,
    UnityEngine.UI.Image source,
    NativeVisualSnapshot native)
{
    Assert(string.Equals(fill.name, OwnedFillName, StringComparison.Ordinal)
           && fill.activeSelf
           && ReferenceEquals(fill.transform.parent?.gameObject, visuals.Button),
        "The Mod-owned fill must be active, named only for ownership, and parented to the exact target button.");
    var components = fill.GetComponents<Component>();
    Assert(components.Length == 3
           && components.Count(component => component.GetType() == typeof(RectTransform)) == 1
           && components.Count(component => component.GetType() == typeof(CanvasRenderer)) == 1
           && components.Count(component => component.GetType() == typeof(UnityEngine.UI.Image)) == 1
           && fill.transform.childCount == 0,
        "The owned clone must retain the exact leaf RectTransform/CanvasRenderer/Image shape only.");
    var image = GetImage(fill);
    Assert(image.enabled && !image.raycastTarget
           && ReferenceEquals(image.sprite, source.sprite)
           && ReferenceEquals(image.material, source.material)
           && image.type == source.type,
        "The owned Image must be enabled/non-raycast and preserve the active native sprite/material/render type.");
    AssertSameGeometry(fill.transform, source.gameObject.transform,
        "The owned fill must preserve the selected native background geometry.");
    var fillIndex = fill.transform.GetSiblingIndex();
    var validIndex = visuals.ValidBackground!.transform.GetSiblingIndex();
    var invalidIndex = visuals.InvalidBackground!.transform.GetSiblingIndex();
    var selectionIndex = visuals.SelectionOutline!.transform.GetSiblingIndex();
    Assert(fillIndex > Math.Max(validIndex, invalidIndex)
           && fillIndex + 1 == selectionIndex,
        "The owned fill must sit after both native backgrounds and immediately before the selection outline.");
    var remainingNativeOrder = visuals.Button.transform.Children
        .Select(child => child.gameObject)
        .Where(child => child.Pointer != fill.Pointer)
        .Select(child => child.Pointer)
        .ToArray();
    Assert(remainingNativeOrder.SequenceEqual(native.NativeSiblingOrder),
        "Creating the owned fill must not reorder any native sibling.");
}

void AssertNativeVisualsUnchanged(
    NativeVisualSnapshot snapshot,
    bool allowBackgroundEnabledChange)
{
    AssertNativeImageUnchanged(snapshot.Valid, allowBackgroundEnabledChange);
    AssertNativeImageUnchanged(snapshot.Invalid, allowBackgroundEnabledChange);
    AssertNativeImageUnchanged(snapshot.Selection, allowEnabledChange: true);
}

void AssertNativeImageUnchanged(NativeImageSnapshot snapshot, bool allowEnabledChange)
{
    var image = snapshot.Image;
    Assert(image.color == snapshot.Color
           && image.raycastTarget == snapshot.RaycastTarget
           && (allowEnabledChange || image.enabled == snapshot.Enabled)
           && ReferenceEquals(image.sprite, snapshot.Sprite)
           && ReferenceEquals(image.material, snapshot.Material)
           && image.type == snapshot.ImageType
           && image.gameObject.activeSelf == snapshot.ActiveSelf,
        "The Mod must not recolor, rebind, activate, raycast-enable, or focus-toggle native Images.");
    AssertSnapshotGeometry(image.gameObject.transform, snapshot,
        "The Mod must not alter native RectTransform geometry.");
}

void AssertSameGeometry(RectTransform actual, RectTransform expected, string message)
{
    Assert(actual.rect == expected.rect
           && actual.anchorMin == expected.anchorMin
           && actual.anchorMax == expected.anchorMax
           && actual.pivot == expected.pivot
           && actual.anchoredPosition == expected.anchoredPosition
           && actual.sizeDelta == expected.sizeDelta
           && actual.offsetMin == expected.offsetMin
           && actual.offsetMax == expected.offsetMax
           && actual.localScale == expected.localScale,
        message);
}

void AssertSnapshotGeometry(RectTransform actual, NativeImageSnapshot expected, string message)
{
    Assert(actual.rect == expected.Rect
           && actual.anchorMin == expected.AnchorMin
           && actual.anchorMax == expected.AnchorMax
           && actual.pivot == expected.Pivot
           && actual.anchoredPosition == expected.AnchoredPosition
           && actual.sizeDelta == expected.SizeDelta
           && actual.offsetMin == expected.OffsetMin
           && actual.offsetMax == expected.OffsetMax
           && actual.localScale == expected.LocalScale,
        message);
}

UnityEngine.UI.Image AddExactBackgroundLeaf(GameObject button, string suffix)
{
    var selection = button.transform.Children
        .Select(child => child.gameObject.GetComponent(typeof(UnityEngine.UI.Image)))
        .OfType<UnityEngine.UI.Image>()
        .Single(image => ReferenceEquals(
            image,
            button.GetComponents<DEYU.AdpUISystem.LogicalCollection.UILogicalUnit>()
                .Single().m_OnSelectionUpdateCallback.m_PersistentCalls.m_Calls
                .get_Item(0).m_Target));
    var owner = new GameObject { name = suffix };
    owner.transform.parent = button.transform;
    owner.transform.rect = selection.gameObject.transform.rect;
    owner.transform.anchorMin = selection.gameObject.transform.anchorMin;
    owner.transform.anchorMax = selection.gameObject.transform.anchorMax;
    owner.transform.pivot = selection.gameObject.transform.pivot;
    owner.transform.anchoredPosition = selection.gameObject.transform.anchoredPosition;
    owner.transform.sizeDelta = selection.gameObject.transform.sizeDelta;
    owner.transform.offsetMin = selection.gameObject.transform.offsetMin;
    owner.transform.offsetMax = selection.gameObject.transform.offsetMax;
    owner.transform.localScale = selection.gameObject.transform.localScale;
    owner.Attach(new CanvasRenderer());
    var image = new UnityEngine.UI.Image
    {
        enabled = false,
        raycastTarget = false,
        sprite = new Sprite { name = suffix + "-sprite" },
        material = new Material { name = suffix + "-material" },
        type = selection.type,
    };
    owner.Attach(image);
    return image;
}

void ApplyNonCanonicalSiblingGeometry(ThrowDeliverButtonVisuals visuals)
{
    var siblings = new[]
    {
        visuals.ValidBackground!.gameObject.transform,
        visuals.InvalidBackground!.gameObject.transform,
        visuals.SelectionOutline!.gameObject.transform,
    };
    foreach (var sibling in siblings)
    {
        sibling.anchorMin = new Vector2(0.12f, 0.18f);
        sibling.anchorMax = new Vector2(0.88f, 0.83f);
        sibling.pivot = new Vector2(0.23f, 0.71f);
        sibling.anchoredPosition = new Vector2(3.25f, -2.5f);
        sibling.sizeDelta = new Vector2(-4f, 6f);
        sibling.offsetMin = new Vector2(4.17f, -6.76f);
        sibling.offsetMax = new Vector2(0.17f, -0.76f);
        sibling.localScale = new Vector3(0.95f, 1.05f, 1f);
        sibling.rect = new Rect(-19.3568f, -43.026f, 84.16f, 60.6f);
    }
}

void AssertColorEquals(Color actual, Color expected, string message) =>
    Assert(MathF.Abs(actual.r - expected.r) < 0.0001f
           && MathF.Abs(actual.g - expected.g) < 0.0001f
           && MathF.Abs(actual.b - expected.b) < 0.0001f
           && MathF.Abs(actual.a - expected.a) < 0.0001f,
        message);

string[] AllLogMessages() => log.Information.Concat(log.Warnings).ToArray();

int CountFailureLogs() => AllLogMessages().Count(message =>
    message.Contains("target fill failed", StringComparison.Ordinal));

int CountRetiredLogs() => AllLogMessages().Count(message =>
    message.Contains("target fill retired", StringComparison.Ordinal));

void AuditProductionSource()
{
    var sourcePath = FindRepositoryPath(
        "mods", "bepinex", "src", "Save", "RuntimeThrowDeliverOrderHighlightService.cs");
    var source = File.ReadAllText(sourcePath);

    foreach (var required in new[]
             {
                 "OwnedFillName",
                 "TryReadVisualSourceEvidence",
                 "TryReadPanelDirectoryEvidence",
                 "TryReadTargetPanelEvidence",
                 "RuntimeUiTargetOrderResolver.TryResolveCurrentCapture",
                 "Dictionary<RuntimeUiTargetKind, ActiveTargetBinding>",
                 "TryReadSelectionEventTarget",
                 "HasValidRectTransformGeometry",
                 "HasSameRectTransformGeometry",
                 "FormatRectTransformGeometry",
                 "ToString(\"R\", CultureInfo.InvariantCulture)",
                 "SelectionGeometryFingerprint",
                 "TryCreateOwnedVisual",
                 "TryValidateOwnedVisual",
                 "TryApplyPulseLocked",
                 "TryRebuildOwnedVisualLocked",
                 "RuntimeTargetHighlightStyle.BuildOrderHighlightPulseColor",
                 "ImageRenderTypeName = \"UnityEngine.UI.Image+Type\"",
                 "GetImageType",
                 "ImageTypeValue",
                 "UnityEngine.Object.Instantiate",
                 "SetSiblingIndex",
                 "target fill bound",
                 "target fill failed",
                 "target fill retired",
             })
    {
        Assert(source.Contains(required, StringComparison.Ordinal),
            $"Production source lost required owned-fill contract token {required}.");
    }

    foreach (var forbidden in new[]
             {
                 "WorkSceneServePannel",
                 "RuntimeServePanelOrderHighlightService",
                 "GetObjectByName",
                 "FindObjectOfType",
                 "FindObjectsOfType",
                 "SelectionOutline",
                 "Shader.Find",
                 "new Material",
                 "SetAsLastSibling",
                 "LayoutElement",
                 "ThrowDeliveryTargetBorder",
                 "OwnedBorderEdge",
                 "TryFocusToOrder",
                 "OpenThrowDeliverPanel",
                 "AddListener",
                 "RemoveListener",
                 "HasExactFullStretchGeometry",
                 "HasSameFullStretchGeometry",
                 "GetColor",
             })
    {
        Assert(!source.Contains(forbidden, StringComparison.Ordinal),
            $"Production source must not retain diagnostic/fallback/selection path {forbidden}.");
    }

    var keyedUnit = Slice(
        source,
        "private static bool TryReadKeyedLogicalUnit(",
        "private static bool TryReadPoolDirectory(");
    Assert(keyedUnit.Contains("AlignedTryGetValue.Invoke(children, arguments)", StringComparison.Ordinal)
           && !keyedUnit.Contains("m_BtnInstances", StringComparison.Ordinal)
           && !keyedUnit.Contains(".name", StringComparison.Ordinal)
           && !keyedUnit.Contains("text", StringComparison.OrdinalIgnoreCase),
        "Desk selection must use only the exact keyed UILogicalUnit identity.");

    var pool = Slice(
        source,
        "private static bool TryReadPoolDirectory(",
        "private static bool TryReadLivePanelPointer(");
    Assert(pool.Contains("RuntimeConcreteCollectionReader.TryReadList", StringComparison.Ordinal)
           && pool.Contains("pointers.Add(pointer)", StringComparison.Ordinal)
           && !pool.Contains("deskCode", StringComparison.Ordinal),
        "The pool may validate unique membership but must never select by desk/index.");

    var tick = Slice(
        source,
        "private static void TickCore()",
        "private static bool TryReconcileActiveTarget(");
    Assert(CountOccurrences(tick, "TryReadPanelDirectoryEvidence(") == 1
           && tick.Contains("var orderTargets = targetSet.Targets.Where(target => target.OrderHighlightEnabled).ToArray();", StringComparison.Ordinal)
           && tick.Contains("foreach (var target in orderTargets)", StringComparison.Ordinal),
        "One tick must read the shared panel directory exactly once before reconciling every target.");

    var publishedBinding = Slice(
        source,
        "ActiveBindings[target.Kind] = binding;",
        "private static bool TryReadVisualSourceEvidence(");
    Assert(publishedBinding.Contains(
               "geometry={visualSource.SelectionGeometryFingerprint}",
               StringComparison.Ordinal)
           && !publishedBinding.Contains(
               "FormatRectTransformGeometry(",
               StringComparison.Ordinal),
        "Success diagnostics must use the managed fingerprint captured before publication and never re-read an IL2CPP RectTransform.");

    var sourceEvidence = Slice(
        source,
        "private static bool TryReadVisualSourceEvidence(",
        "private static bool TryReadSelectionEventTarget(");
    foreach (var required in new[]
             {
                 "MaxDirectButtonChildren",
                 "childCount",
                 "GetChild",
                 "TryReadExactLeafImage",
                 "selection",
                 "background",
                 "activeInHierarchy",
                 "sprite",
             })
    {
        Assert(sourceEvidence.Contains(required, StringComparison.OrdinalIgnoreCase),
            $"Visual-source evidence lost strict direct-child evidence {required}.");
    }
    Assert(!sourceEvidence.Contains(".name", StringComparison.Ordinal)
           && !sourceEvidence.Contains("GetSiblingIndex() ==", StringComparison.Ordinal)
           && !sourceEvidence.Contains("sprite.name", StringComparison.Ordinal)
           && !sourceEvidence.Contains("color ==", StringComparison.Ordinal),
        "Names, fixed sibling indices, sprite names, and native colors must not select backgrounds.");

    var listenerRead = Slice(
        source,
        "private static bool TryReadSelectionEventTarget(",
        "private static bool TryReadExactLeafImage(");
    foreach (var required in new[]
             {
                 "Il2CppType.From(members.UiLogicalUnitType)",
                 "queriedUnitPointer != exactUnitPointer",
                 "RuntimeConcreteCollectionReader.TryReadList",
                 "count != 1",
                 "Target.GetValue",
             })
    {
        Assert(listenerRead.Contains(required, StringComparison.Ordinal),
            $"Selection-listener resolution lost exact serialized evidence {required}.");
    }
    Assert(listenerRead.Contains("MethodName.GetValue", StringComparison.Ordinal)
           && listenerRead.Contains("SelectionEnabledMethodName", StringComparison.Ordinal),
        "The unique listener must call the exact Image enabled setter before its target is accepted.");

    var create = Slice(
        source,
        "private static bool TryCreateOwnedVisual(",
        "private static bool TryValidateOwnedVisual(");
    Assert(create.Contains("UnityEngine.Object.Instantiate", StringComparison.Ordinal)
           && create.Contains("OwnedFillName", StringComparison.Ordinal)
           && create.Contains("SetSiblingIndex", StringComparison.Ordinal)
           && create.Contains("currentCloneSibling + 1 != currentSelectionSibling", StringComparison.Ordinal)
           && create.Contains("SetRaycastTarget", StringComparison.Ordinal)
           && create.Contains("SetEnabled", StringComparison.Ordinal)
           && create.Contains("SafeDestroyTransientClone", StringComparison.Ordinal),
        "Owned-fill construction must be one transactional clone with exact UI state and cleanup.");
    Assert(!create.Contains("new Material", StringComparison.Ordinal)
           && !create.Contains("Shader.Find", StringComparison.Ordinal)
           && !create.Contains("SetAsLastSibling", StringComparison.Ordinal),
        "Owned-fill construction must not synthesize materials or use a fallback placement.");

    var pulse = Slice(
        source,
        "private static bool TryApplyPulseLocked(",
        "private static bool TryRebuildOwnedVisualLocked(");
    Assert(pulse.Contains("RuntimeTargetHighlightStyle.BuildOrderHighlightPulseColor", StringComparison.Ordinal)
           && pulse.Contains("SetColor", StringComparison.Ordinal)
           && pulse.IndexOf("TryValidateOwnedVisualIdentity(", StringComparison.Ordinal)
              < pulse.IndexOf("SetColor.Invoke", StringComparison.Ordinal)
           && !pulse.Contains("Background", StringComparison.Ordinal),
        "Pulse mutation must follow fresh owned identity validation and stay confined to the owned Image.");

    var ownedIdentity = Slice(
        source,
        "private static bool TryValidateOwnedVisualIdentity(",
        "private static bool TryValidateOwnedVisual(");
    foreach (var required in new[]
             {
                 "ownerPointer != visual.OwnerPointer",
                 "imagePointer != visual.ImagePointer",
                 "var imageOwner = image.gameObject",
                 "imageOwnerPointer != visual.OwnerPointer",
             })
    {
        Assert(ownedIdentity.Contains(required, StringComparison.Ordinal),
            $"Owned wrapper identity validation lost safety check {required}.");
    }

    var retire = Slice(
        source,
        "private static bool RetireOwnedVisual(",
        "private static void AbandonOwnedVisual(");
    var firstIdentityCheck = retire.IndexOf(
        "TryValidateOwnedVisualIdentity(",
        StringComparison.Ordinal);
    var setInactive = retire.IndexOf("visual.Owner.SetActive(false)", StringComparison.Ordinal);
    var secondIdentityCheck = retire.IndexOf(
        "TryValidateOwnedVisualIdentity(",
        firstIdentityCheck + 1,
        StringComparison.Ordinal);
    var destroyOwned = retire.IndexOf("UnityEngine.Object.Destroy(visual.Owner)", StringComparison.Ordinal);
    Assert(firstIdentityCheck >= 0
           && firstIdentityCheck < setInactive
           && setInactive < secondIdentityCheck
           && secondIdentityCheck < destroyOwned,
        "Retirement must validate exact ownership before SetActive and again before Destroy.");

    var ownedValidation = Slice(
        source,
        "private static bool TryValidateOwnedVisual(",
        "private static bool TryApplyPulseLocked(");
    Assert(ownedValidation.Contains("fillSibling + 1 != selectionSibling", StringComparison.Ordinal),
        "Health validation must require the fill to remain immediately adjacent to selection.");

    var boundedFailure = Slice(
        source,
        "private static void TryLogBoundedFailure(",
        "private static void TryLogVisualInfo(");
    Assert(boundedFailure.Contains("new FailureLogIdentity(", StringComparison.Ordinal)
           && boundedFailure.Contains("LoggedFailures.Add(identity)", StringComparison.Ordinal)
           && boundedFailure.Contains("_warningLogs < MaxWarningLogsPerBusiness", StringComparison.Ordinal),
        "Target-fill failures must be exact phase/reason deduplicated before consuming the bounded warning budget.");
    var budgetReset = Slice(
        source,
        "private static void ResetBusinessLogBudgetLocked(",
        "private static void TryLogInfo(");
    Assert(budgetReset.Contains("LoggedFailures.Clear()", StringComparison.Ordinal),
        "Failure-log identities must reset only with the business-generation budget.");

    var panelEvidence = Slice(
        source,
        "private readonly record struct PanelEvidence(",
        "private sealed record ActivePanelRegistration(");
    Assert(!panelEvidence.Contains("object Panel,", StringComparison.Ordinal)
           && !panelEvidence.Contains("object Group,", StringComparison.Ordinal)
           && !panelEvidence.Contains("object Unit,", StringComparison.Ordinal),
        "Per-read panel evidence must not retain redundant IL2CPP wrappers.");

    var obsoleteSource = Path.Combine(
        Path.GetDirectoryName(sourcePath)!,
        "RuntimeServePanelOrderHighlightService.cs");
    Assert(!File.Exists(obsoleteSource),
        "The obsolete serve-panel path must remain deleted rather than layered as compatibility.");
}

string Slice(string source, string startMarker, string endMarker)
{
    var start = source.IndexOf(startMarker, StringComparison.Ordinal);
    var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
    Assert(start >= 0 && end > start,
        $"Source audit markers missing: {startMarker} -> {endMarker}");
    return source[start..end];
}

int CountOccurrences(string source, string value)
{
    var count = 0;
    var index = 0;
    while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
    {
        count += 1;
        index += value.Length;
    }
    return count;
}

string FindRepositoryPath(params string[] segments)
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current != null)
    {
        var candidate = Path.Combine(new[] { current.FullName }.Concat(segments).ToArray());
        if (File.Exists(candidate)) return candidate;
        current = current.Parent;
    }
    throw new FileNotFoundException(string.Join('/', segments));
}

void AssertContains(string actual, string expected, string message) =>
    Assert(actual.Contains(expected, StringComparison.Ordinal), $"{message} Actual: {actual}");

void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed record NativeVisualSnapshot(
    ThrowDeliverButtonVisuals Visuals,
    IntPtr[] NativeSiblingOrder,
    NativeImageSnapshot Valid,
    NativeImageSnapshot Invalid,
    NativeImageSnapshot Selection);

internal sealed record NativeImageSnapshot(
    UnityEngine.UI.Image Image,
    Color Color,
    bool RaycastTarget,
    bool Enabled,
    Sprite? Sprite,
    Material? Material,
    UnityEngine.UI.Image.Type ImageType,
    bool ActiveSelf,
    Rect Rect,
    Vector2 AnchorMin,
    Vector2 AnchorMax,
    Vector2 Pivot,
    Vector2 AnchoredPosition,
    Vector2 SizeDelta,
    Vector2 OffsetMin,
    Vector2 OffsetMax,
    Vector3 LocalScale);
