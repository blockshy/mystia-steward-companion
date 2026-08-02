using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using MystiaStewardCompanion.Save;
using Night.UI.HUD.Ordering;
using NightScene.GuestManagementUtility;
using NightScene.UI.GuestManagementUtility;
using UnityEngine;
using UnityEngine.UI;

var unityThreadId = Environment.CurrentManagedThreadId;
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(
    IsActive: true,
    Generation: 1,
    ThreadId: unityThreadId);

var log = new ManualLogSource();
RuntimeOrderHighlightService.Attach(log);

Assert(Harmony.Patches.Count == 4,
    $"Order highlighting must install exactly four safe hooks. Warnings: {string.Join(" | ", log.Warnings)}");
AssertPatch(typeof(OrderingElement), "Initialize", parameterCount: 5, hasPrefix: true, hasPostfix: false);
AssertPatch(typeof(OrderController), "CreateOrderingElement", parameterCount: 1, hasPrefix: false, hasPostfix: true);
AssertPatch(typeof(OrderingElement), "Out", parameterCount: 0, hasPrefix: true, hasPostfix: false);
AssertPatch(typeof(OrderingElement), "DestroySelf", parameterCount: 0, hasPrefix: true, hasPostfix: false);
Assert(Harmony.Patches.All(patch => patch.Target.Name != "OnDestroy"), "The shared empty OnDestroy native stub must never be hooked.");

// Trace ids are exact opaque identities: no trimming, Unicode digits, case folding, or aliases.
AssertTrace("R-0", expected: true);
AssertTrace("R-0000000000000000", expected: true);
foreach (var invalid in new[]
         {
             "", "R-", "r-0001", " R-0001", "R-0001 ", "R-１２", "R-00000000000000000", "N-0001",
         })
{
    AssertTrace(invalid, expected: false);
}
Assert(RuntimeOrderTraceIdService.NormalizeRareTraceId("ignored", enabled: false) == "", "A disabled target must erase its trace without parsing it.");

// IL2CPP can erase concrete managed wrapper types in GetComponents<Component>() even though
// typed GetComponent probes still resolve the same native RectTransform/CanvasRenderer/Image.
var wrapperLeaf = new GameObject { name = "Il2CppBaseWrapperLeaf" };
var wrapperCanvasRenderer = new CanvasRenderer();
var wrapperImage = new Image { sprite = new Sprite() };
wrapperLeaf.Attach(wrapperCanvasRenderer);
wrapperLeaf.Attach(wrapperImage);
wrapperLeaf.ReturnBaseComponentWrappers = true;
var baseWrappedComponents = wrapperLeaf.GetComponents<Component>();
Assert(baseWrappedComponents.Length == 3, "The IL2CPP wrapper probe must retain the native component count.");
Assert(baseWrappedComponents.All(component => component.GetType() == typeof(Component)),
    "The IL2CPP wrapper probe must expose every non-generic component as the base managed wrapper type.");
Assert(baseWrappedComponents.Select(component => component.Pointer).ToHashSet().SetEquals(new[]
       {
           wrapperLeaf.transform.Pointer,
           wrapperCanvasRenderer.Pointer,
           wrapperImage.Pointer,
       }), "Base wrappers must preserve the underlying native component pointers.");
Assert(ReferenceEquals(wrapperLeaf.GetComponent(typeof(RectTransform)), wrapperLeaf.transform)
       && ReferenceEquals(wrapperLeaf.GetComponent(typeof(CanvasRenderer)), wrapperCanvasRenderer)
       && ReferenceEquals(wrapperLeaf.GetComponent(typeof(Image)), wrapperImage),
    "Typed GetComponent probes must still resolve the concrete native components.");
var wrapperLayoutParent = new GameObject { name = "Il2CppBaseWrapperLayoutParent" };
var wrapperLayout = new VerticalLayoutGroup();
wrapperLayoutParent.Attach(wrapperLayout);
wrapperLayoutParent.ReturnBaseComponentWrappers = true;
Assert(wrapperLayoutParent.GetComponents<Component>().All(component => component.GetType() == typeof(Component)),
    "The layout probe must also reproduce base managed wrappers on the parent.");
Assert(ReferenceEquals(wrapperLayoutParent.GetComponent(typeof(LayoutGroup)), wrapperLayout),
    "Typed LayoutGroup lookup must still recognize a derived native layout component.");

RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, 2, unityThreadId);
RuntimeOrderHighlightService.Resume("safe hook coverage starts with this business");

var capturedAtA = new DateTime(2026, 8, 2, 8, 0, 0, DateTimeKind.Utc);
var orderA = new GuestsManager.OrderBase();
var captureA = AddCapture(orderA, capturedAtA, deskCode: 0, guestId: 105, foodTagId: 12, beverageTagId: 34);
var traceA = RuntimeOrderTraceIdService.GetRareTraceId(captureA);
Assert(traceA == "R-0001", "The first captured rare order must receive a stable rare trace.");
Assert(RuntimeOrderTraceIdService.GetRareTraceId(captureA) == traceA, "The same capture must retain its trace.");
Assert(RuntimeOrderTraceIdService.TryResolveCurrentRareCapture(traceA, 0, TimeSpan.FromHours(6), out var resolvedA, out _)
       && ReferenceEquals(resolvedA, captureA), "A unique current capture must resolve by exact trace and desk.");
Assert(!RuntimeOrderTraceIdService.TryResolveCurrentRareCapture(traceA, 1, TimeSpan.FromHours(6), out _, out var wrongDeskFailure)
       && wrongDeskFailure.Contains("desk mismatch", StringComparison.Ordinal), "Desk identity must be an exact cross-check, not a fallback.");

var duplicateCaptureOrder = new GuestsManager.OrderBase();
var duplicateCapture = AddCapture(duplicateCaptureOrder, capturedAtA, deskCode: 0, guestId: 105, foodTagId: 12, beverageTagId: 34);
Assert(RuntimeOrderTraceIdService.GetRareTraceId(duplicateCapture) == traceA, "Equivalent capture metadata must expose the ambiguity under the same trace.");
Assert(!RuntimeOrderTraceIdService.TryResolveCurrentRareCapture(traceA, 0, TimeSpan.FromHours(6), out _, out var duplicateCaptureFailure)
       && duplicateCaptureFailure.Contains("matched 2 active captures", StringComparison.Ordinal), "Duplicate active captures must fail closed.");
SpecialOrderRuntimeCapture.Captures.Remove(duplicateCapture);

var orderOther = new GuestsManager.OrderBase();
var captureOther = AddCapture(
    orderOther,
    capturedAtA.AddSeconds(1),
    deskCode: 0,
    guestId: 106,
    foodTagId: 13,
    beverageTagId: 35);
var traceOther = RuntimeOrderTraceIdService.GetRareTraceId(captureOther);

var controller = new OrderController();
var elementA = CreateElement(controller, orderA, deskCode: 0);
var elementOther = CreateElement(controller, orderOther, deskCode: 0);

var nativeImage = elementA.borderStyleImageForCurrent;
var nativeColor = nativeImage.color;
var nativeRaycast = nativeImage.raycastTarget;
var nativeEnabled = nativeImage.enabled;
var nativeScale = nativeImage.transform.localScale;
var nativeCurrent = elementA.current;
nativeImage.gameObject.ReturnBaseComponentWrappers = true;
nativeImage.gameObject.ReturnBaseTypedComponentWrappers = true;
Assert(nativeImage.gameObject.GetComponents<Component>().All(component => component.GetType() == typeof(Component)),
    "The end-to-end visual probe must use the real IL2CPP base-wrapper shape.");
Assert(nativeImage.gameObject.GetComponent(typeof(Image))?.GetType() == typeof(Component),
    "The end-to-end visual probe must also reproduce a typed GetComponent cache miss.");

Time.realtimeSinceStartup = 0f;
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, traceA, deskCode: 0);
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == 1, "The exact target order must create one private overlay despite another order sharing its desk.");
var firstOverlay = FindLatestOverlay();
var firstOverlayImage = InspectPrivateOverlay(firstOverlay);
Assert(RuntimeOrderHighlightService.Status.Contains("active", StringComparison.Ordinal), "A unique registered target must become active.");
Assert(RuntimeOrderHighlightService.Status.Contains($"order:0x{orderA.Pointer:x}", StringComparison.Ordinal), "Diagnostics must identify the exact captured order pointer.");
Assert(firstOverlayImage.color.r == 1f && firstOverlayImage.color.g == 0.86f && firstOverlayImage.color.b == 0.18f,
    "The private border must use the verified yellow highlight color.");
Assert(firstOverlayImage.color.a is >= 0.62f and <= 1f, "The yellow overlay alpha must remain in the bounded pulse range.");

Time.realtimeSinceStartup = 0.2f;
var firstAlpha = firstOverlayImage.color.a;
RuntimeOrderHighlightService.Tick();
Assert(firstOverlayImage.color.a != firstAlpha, "An active private overlay must pulse without recreating the card.");
Assert(GameObject.Instantiated.Count == 1, "Pulse updates must not recreate a healthy overlay.");
Assert(elementA.current == nativeCurrent && elementA.ChangeBorderStyleCalls == 0, "Highlighting must not change the game's current order focus.");
Assert(nativeImage.color == nativeColor
       && nativeImage.raycastTarget == nativeRaycast
       && nativeImage.enabled == nativeEnabled
       && nativeImage.transform.localScale == nativeScale,
    "Highlighting must not mutate the native current-order border.");

// A duplicate HUD card for the same native order must be rejected rather than picking either card.
var duplicateElement = CreateElement(controller, orderA, deskCode: 0);
RuntimeOrderHighlightService.UpdateTarget(2, enabled: false, "", deskCode: -1);
RuntimeOrderHighlightService.Tick();
var destroyedBeforeDuplicate = GameObject.Destroyed.Count;
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, traceA, deskCode: 0);
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == 1, "Duplicate HUD elements must not create a replacement overlay.");
Assert(GameObject.Destroyed.Count == destroyedBeforeDuplicate, "Duplicate HUD resolution must fail before creating a visual.");
Assert(RuntimeOrderHighlightService.Status.Contains("matched 2 registered HUD elements", StringComparison.Ordinal), "Duplicate HUD identity must be diagnosed precisely.");
TeardownElement(duplicateElement, "Out");
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == 2, "Removing the duplicate card must let the unchanged exact target recover.");

// Cloning is only safe for a leaf image outside a parent-controlled layout hierarchy.
RuntimeOrderHighlightService.UpdateTarget(2, enabled: false, "", deskCode: -1);
RuntimeOrderHighlightService.Tick();
var templateChild = new GameObject { name = "UnsafeTemplateChild" };
templateChild.transform.parent = nativeImage.transform;
var instantiatedBeforeChildShape = GameObject.Instantiated.Count;
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, traceA, deskCode: 0);
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeChildShape, "A border template with child objects must fail before cloning.");
Assert(RuntimeOrderHighlightService.Status.Contains("child objects instead of a leaf-only visual shape", StringComparison.Ordinal),
    "A non-leaf template must expose the exact visual safety gate.");

RuntimeOrderHighlightService.UpdateTarget(2, enabled: false, "", deskCode: -1);
RuntimeOrderHighlightService.Tick();
templateChild.transform.parent = null;
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, traceA, deskCode: 0);
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeChildShape + 1, "The unchanged leaf-only border must recover after the unsafe child is removed.");

RuntimeOrderHighlightService.UpdateTarget(2, enabled: false, "", deskCode: -1);
RuntimeOrderHighlightService.Tick();
var unknownVisualComponent = new Component();
nativeImage.gameObject.Attach(unknownVisualComponent);
var instantiatedBeforeUnknownComponent = GameObject.Instantiated.Count;
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, traceA, deskCode: 0);
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeUnknownComponent,
    "A border template with an additional unknown native component must fail before cloning.");
Assert(RuntimeOrderHighlightService.Status.Contains("has 4 components instead of the exact visual-only shape", StringComparison.Ordinal),
    "An additional native component must remain visible as an exact component-count failure.");

RuntimeOrderHighlightService.UpdateTarget(2, enabled: false, "", deskCode: -1);
RuntimeOrderHighlightService.Tick();
nativeImage.gameObject.Detach(unknownVisualComponent);
nativeImage.gameObject.ReturnBaseTypedComponentWrappers = false;
var nativeCanvasRenderer = nativeImage.gameObject.GetComponent(typeof(CanvasRenderer)) as CanvasRenderer;
if (nativeCanvasRenderer == null) throw new InvalidOperationException("The native border CanvasRenderer probe is unavailable.");
nativeImage.gameObject.Detach(nativeCanvasRenderer);
var wrongThirdComponent = new Component();
nativeImage.gameObject.Attach(wrongThirdComponent);
nativeImage.gameObject.ReturnBaseTypedComponentWrappers = true;
Assert(nativeImage.gameObject.GetComponents<Component>().Length == 3,
    "The wrong-component probe must preserve the exact component count.");
var instantiatedBeforeWrongComponent = GameObject.Instantiated.Count;
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, traceA, deskCode: 0);
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeWrongComponent,
    "A three-component template missing the exact CanvasRenderer must fail before cloning.");
Assert(RuntimeOrderHighlightService.Status.Contains("has no exact live native UnityEngine.CanvasRenderer", StringComparison.Ordinal),
    "A wrong native component must be rejected by the typed CanvasRenderer query, not accepted by count alone.");

RuntimeOrderHighlightService.UpdateTarget(2, enabled: false, "", deskCode: -1);
RuntimeOrderHighlightService.Tick();
nativeImage.gameObject.ReturnBaseTypedComponentWrappers = false;
nativeImage.gameObject.Detach(wrongThirdComponent);
nativeImage.gameObject.Attach(nativeCanvasRenderer);
nativeImage.gameObject.ReturnBaseTypedComponentWrappers = true;
var managedLayout = new VerticalLayoutGroup();
elementA.gameObject.Attach(managedLayout);
elementA.gameObject.ReturnBaseComponentWrappers = true;
elementA.gameObject.ReturnBaseTypedComponentWrappers = true;
var instantiatedBeforeManagedLayout = GameObject.Instantiated.Count;
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, traceA, deskCode: 0);
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeManagedLayout, "A LayoutGroup-managed parent must fail before sibling insertion.");
Assert(RuntimeOrderHighlightService.Status.Contains("managed by UnityEngine.UI.LayoutGroup", StringComparison.Ordinal),
    "The parent safety gate must use a typed native LayoutGroup query even when enumeration exposes base wrappers.");

RuntimeOrderHighlightService.UpdateTarget(2, enabled: false, "", deskCode: -1);
RuntimeOrderHighlightService.Tick();
elementA.gameObject.Detach(managedLayout);
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, traceA, deskCode: 0);
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeManagedLayout + 1, "A normal parent with a leaf border must remain supported.");
Assert(RuntimeOrderHighlightService.Status.Contains("active", StringComparison.Ordinal), "The normal leaf visual must stay active after both safety-gate probes.");

// A typed Image lookup may return a derived native UI class; exact class identity must reject it.
RuntimeOrderHighlightService.UpdateTarget(2, enabled: false, "", deskCode: -1);
RuntimeOrderHighlightService.Tick();
var derivedImageOrder = new GuestsManager.OrderBase(deskCode: 3);
var derivedImageCapture = AddCapture(
    derivedImageOrder,
    capturedAtA.AddSeconds(4),
    deskCode: 3,
    guestId: 109,
    foodTagId: 16,
    beverageTagId: 38);
var derivedImageTrace = RuntimeOrderTraceIdService.GetRareTraceId(derivedImageCapture);
var derivedImageElement = new OrderingElement(new DerivedImage { sprite = new Sprite() });
derivedImageElement.borderStyleImageForCurrent.gameObject.ReturnBaseComponentWrappers = true;
derivedImageElement.borderStyleImageForCurrent.gameObject.ReturnBaseTypedComponentWrappers = true;
CreateElement(controller, derivedImageOrder, deskCode: 3, pooledElement: derivedImageElement);
var instantiatedBeforeDerivedImage = GameObject.Instantiated.Count;
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, derivedImageTrace, deskCode: 3);
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeDerivedImage,
    "A derived native Image class must fail before cloning even when typed Image lookup succeeds.");
Assert(RuntimeOrderHighlightService.Status.Contains("native component is not the exact UnityEngine.UI.Image class", StringComparison.Ordinal),
    "The exact native Image class mismatch must remain visible in diagnostics.");
TeardownElement(derivedImageElement, "Out");
SpecialOrderRuntimeCapture.Captures.Remove(derivedImageCapture);
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, traceA, deskCode: 0);
RuntimeOrderHighlightService.Tick();

// Cross-generation and main-thread gates must stop before touching Unity visuals.
RuntimeOrderHighlightService.UpdateTarget(3, enabled: true, traceA, deskCode: 0);
RuntimeOrderHighlightService.Tick();
Assert(RuntimeOrderHighlightService.Status.Contains("different night-business session", StringComparison.Ordinal), "A target from another business generation must fail closed.");
var instantiatedBeforeWrongThread = GameObject.Instantiated.Count;
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, 2, unityThreadId + 1);
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, traceA, deskCode: 0);
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeWrongThread, "A non-Unity-thread tick must not create a visual.");
Assert(RuntimeOrderHighlightService.Status.Contains("Unity main thread", StringComparison.Ordinal), "The thread gate must be visible in diagnostics.");
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, 2, unityThreadId);
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeWrongThread + 1, "The same valid target must recover on the Unity thread.");

// Invalid protocol input must clear the current visual and must not be silently normalized.
var destroyedBeforeInvalidTrace = GameObject.Destroyed.Count;
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, " R-0001", deskCode: 0);
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Destroyed.Count == destroyedBeforeInvalidTrace + 1, "An invalid exact trace must retire the live overlay.");
Assert(RuntimeOrderHighlightService.Status.Contains("invalid target", StringComparison.Ordinal), "An invalid exact trace must remain visible as a protocol failure.");
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, traceA, deskCode: 0);
RuntimeOrderHighlightService.Tick();

// Registration reads the exact get-only OrderBase.DeskCode, including non-zero desks.
RuntimeOrderHighlightService.UpdateTarget(2, enabled: false, "", deskCode: -1);
RuntimeOrderHighlightService.Tick();
var nonZeroDeskOrder = new GuestsManager.OrderBase(deskCode: 2);
var nonZeroDeskCapture = AddCapture(
    nonZeroDeskOrder,
    capturedAtA.AddSeconds(2),
    deskCode: 2,
    guestId: 107,
    foodTagId: 14,
    beverageTagId: 36);
var nonZeroDeskTrace = RuntimeOrderTraceIdService.GetRareTraceId(nonZeroDeskCapture);
var nonZeroDeskElement = CreateElement(controller, nonZeroDeskOrder, deskCode: 2);
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, nonZeroDeskTrace, deskCode: 2);
RuntimeOrderHighlightService.Tick();
Assert(RuntimeOrderHighlightService.Status.Contains("active", StringComparison.Ordinal),
    "A non-zero exact OrderBase.DeskCode must register and resolve normally.");
TeardownElement(nonZeroDeskElement, "Out");
SpecialOrderRuntimeCapture.Captures.Remove(nonZeroDeskCapture);

var mismatchedDeskOrder = new GuestsManager.OrderBase(deskCode: 4);
var mismatchedDeskCapture = AddCapture(
    mismatchedDeskOrder,
    capturedAtA.AddSeconds(3),
    deskCode: 5,
    guestId: 108,
    foodTagId: 15,
    beverageTagId: 37);
var mismatchedDeskTrace = RuntimeOrderTraceIdService.GetRareTraceId(mismatchedDeskCapture);
var mismatchedDeskElement = CreateElement(controller, mismatchedDeskOrder, deskCode: 5);
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, mismatchedDeskTrace, deskCode: 5);
RuntimeOrderHighlightService.Tick();
Assert(RuntimeOrderHighlightService.Status.Contains("matched 0 registered HUD elements", StringComparison.Ordinal),
    "A capture desk that disagrees with exact OrderBase.DeskCode must fail closed without using Initialize's desk argument.");
TeardownElement(mismatchedDeskElement, "DestroySelf");
SpecialOrderRuntimeCapture.Captures.Remove(mismatchedDeskCapture);
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, traceA, deskCode: 0);
RuntimeOrderHighlightService.Tick();

// Reusing a pooled card for B must retire A and bind by B's native pointer, even on the same desk.
var orderB = new GuestsManager.OrderBase();
var captureB = AddCapture(
    orderB,
    capturedAtA.AddSeconds(2),
    deskCode: 0,
    guestId: 107,
    foodTagId: 14,
    beverageTagId: 36);
var traceB = RuntimeOrderTraceIdService.GetRareTraceId(captureB);
SpecialOrderRuntimeCapture.Captures.Remove(captureA);
var overlayBeforeRebind = FindLatestOverlay();
CreateElement(controller, orderB, deskCode: 0, pooledElement: elementA);
Assert(overlayBeforeRebind.m_CachedPtr == IntPtr.Zero, "Initialize/rebind must destroy A's private overlay before the card is reused.");
RuntimeOrderHighlightService.UpdateTarget(2, enabled: true, traceB, deskCode: 0);
RuntimeOrderHighlightService.Tick();
Assert(RuntimeOrderHighlightService.Status.Contains($"order:0x{orderB.Pointer:x}", StringComparison.Ordinal), "A pooled card must bind to B's exact native order pointer.");
Assert(!RuntimeOrderHighlightService.Status.Contains($"order:0x{orderA.Pointer:x}", StringComparison.Ordinal), "A pooled card must not retain A's identity.");

// Both explicit teardown boundaries retire the visual and allow a later Initialize to recover.
var overlayBeforeOut = FindLatestOverlay();
TeardownElement(elementA, "Out");
Assert(overlayBeforeOut.m_CachedPtr == IntPtr.Zero, "Out must destroy the private overlay before the native card exits.");
CreateElement(controller, orderB, deskCode: 0, pooledElement: elementA);
RuntimeOrderHighlightService.Tick();
var overlayBeforeDestroySelf = FindLatestOverlay();
TeardownElement(elementA, "DestroySelf");
Assert(overlayBeforeDestroySelf.m_CachedPtr == IntPtr.Zero, "DestroySelf must destroy the private overlay before native destruction.");
CreateElement(controller, orderB, deskCode: 0, pooledElement: elementA);
RuntimeOrderHighlightService.Tick();

// An unexpected stale wrapper is detected by the health check without an unsafe OnDestroy hook.
var overlayBeforeUnexpectedDestroy = FindLatestOverlay();
elementA.InvalidateNativePointer();
Time.realtimeSinceStartup = 1f;
RuntimeOrderHighlightService.Tick();
Assert(overlayBeforeUnexpectedDestroy.m_CachedPtr == IntPtr.Zero, "A stale OrderingElement must retire its live overlay during the bounded health check.");
Assert(RuntimeOrderHighlightService.Status.Contains("invalid", StringComparison.Ordinal), "Unexpected card destruction must fail closed.");

var replacementElement = CreateElement(controller, orderB, deskCode: 0);
Time.realtimeSinceStartup = 2f;
RuntimeOrderHighlightService.Tick();
var overlayBeforeClosing = FindLatestOverlay();
RuntimeOrderHighlightService.Suspend("night business closing");
Assert(overlayBeforeClosing.m_CachedPtr == IntPtr.Zero, "Closing on the Unity thread must destroy the Mod-owned overlay.");

RuntimeOrderHighlightService.Resume("next scene before native destruction");
CreateElement(controller, orderB, deskCode: 0, pooledElement: replacementElement);
Time.realtimeSinceStartup = 3f;
RuntimeOrderHighlightService.Tick();
var overlayBeforeAbandon = FindLatestOverlay();
var destroyedBeforeAbandon = GameObject.Destroyed.Count;
RuntimeOrderHighlightService.Abandon("native scene already destroyed");
Assert(GameObject.Destroyed.Count == destroyedBeforeAbandon, "Destroyed-scene abandonment must not dereference or destroy stale Unity wrappers.");
Assert(overlayBeforeAbandon.m_CachedPtr != IntPtr.Zero, "Abandon must only drop managed ownership and leave native destruction to Unity.");

var serviceSource = File.ReadAllText("mods/bepinex/src/Save/RuntimeOrderHighlightService.cs");
var traceSource = File.ReadAllText("mods/bepinex/src/Save/RuntimeOrderTraceIdService.cs");
foreach (var required in new[]
         {
             "CreateOrderingElement",
             "TryResolveCurrentRareCapture",
             "candidate.OrderPointer != orderPointer",
             "HasExactSafeImageComponents",
             "sourceObject.transform.childCount != 0",
             "components.Length != 3",
             "HasNoNativeLayoutGroup(parent, layoutGroupType, out failure)",
             "LayoutGroupTypeName",
             "enumeratedPointers.SetEquals(exactPointers)",
             "GetComponent(Il2CppType.From(componentType))",
             "GetComponent(Il2CppType.From(layoutGroupType))",
             "Il2CppClassPointerStore.GetNativeClassPointer(componentType)",
             "IL2CPP.il2cpp_object_get_class(pointer)",
             "pointer = component.Pointer",
             "UnityEngine.Object.Instantiate(sourceObject, parent)",
             "SetRaycastTarget!.Invoke(clonedImage, new object?[] { false })",
             "new Color(1f, 0.86f, 0.18f, alpha)",
         })
{
    Assert(serviceSource.Contains(required, StringComparison.Ordinal), $"Order highlighting must retain its exact runtime contract: {required}");
}
Assert(traceSource.Contains("character < '0' || character > '9'", StringComparison.Ordinal), "Rare trace validation must remain ASCII-only.");
Assert(traceSource.Contains("string.Equals(candidate.RuntimeKey, $\"ptr:{orderPointer:x}\", StringComparison.Ordinal)", StringComparison.Ordinal),
    "Capture resolution must retain the nonzero native-key cross-check.");

foreach (var forbidden in new[]
         {
             "PatchKey(\"OnDestroy\"",
             "TryPatchTeardown(_harmony, elementType, \"OnDestroy\"",
             "AfterElementInitialize",
             "activeOrderProperty.SetValue",
             "ChangeBorderStyle(",
             "TryFocusToOrder(",
             "SetPartnerHighlight(",
             "FindObjectOfType",
             "FindObjectsOfType",
             "GameObject.Find(",
             "GetComponentInChildren",
             "TryReadNativeObjectPointer(component, out pointer)",
         })
{
    Assert(!serviceSource.Contains(forbidden, StringComparison.Ordinal), $"Order highlighting must not use unsafe focus mutation, shared stubs, or scene scans: {forbidden}");
}

Console.WriteLine("Runtime order highlight smoke passed.");

static CapturedRuntimeSpecialOrder AddCapture(
    GuestsManager.OrderBase order,
    DateTime capturedAt,
    int deskCode,
    int guestId,
    int foodTagId,
    int beverageTagId)
{
    var capture = new CapturedRuntimeSpecialOrder(
        RuntimeKey: $"ptr:{order.Pointer:x}",
        OrderObject: order,
        ControllerObject: new GuestsManager.OrderBase(),
        FirstCapturedAt: capturedAt,
        DeskCode: deskCode,
        GuestId: guestId,
        HasFoodTagId: true,
        FoodTagId: foodTagId,
        HasBeverageTagId: true,
        BeverageTagId: beverageTagId,
        IsFreeOrder: false);
    SpecialOrderRuntimeCapture.Captures.Add(capture);
    return capture;
}

static OrderingElement CreateElement(
    OrderController controller,
    GuestsManager.OrderBase order,
    int deskCode,
    OrderingElement? pooledElement = null)
{
    controller.NextDeskCode = deskCode;
    controller.ElementFactory = () => pooledElement ?? new OrderingElement();
    controller.InitializePrefix = (element, request, initializedDeskCode) =>
        InvokeHarmonyCallback(
            FindPatch(typeof(OrderingElement), "Initialize", parameterCount: 5).Prefix!.methodInfo,
            instance: element,
            argument0: request,
            result: null,
            deskCode: initializedDeskCode);

    var created = controller.CreateOrderingElement(order);
    InvokeHarmonyCallback(
        FindPatch(typeof(OrderController), "CreateOrderingElement", parameterCount: 1).Postfix!.methodInfo,
        instance: controller,
        argument0: order,
        result: created,
        deskCode: deskCode);
    return created;
}

static void TeardownElement(OrderingElement element, string methodName)
{
    InvokeService("BeforeElementTeardown", element);
    var method = typeof(OrderingElement).GetMethod(
        methodName,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    method!.Invoke(element, null);
}

static object? InvokeService(string methodName, params object?[] arguments)
{
    var matches = typeof(RuntimeOrderHighlightService)
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
        .Where(method => method.Name == methodName && method.GetParameters().Length == arguments.Length)
        .ToArray();
    Assert(matches.Length == 1, $"Expected one private service method {methodName}/{arguments.Length}.");
    return matches[0].Invoke(null, arguments);
}

static void InvokeHarmonyCallback(
    MethodInfo callback,
    object instance,
    object? argument0,
    object? result,
    int deskCode)
{
    var arguments = callback.GetParameters().Select(parameter => parameter.Name switch
    {
        "__instance" => instance,
        "__0" => argument0,
        "__2" => deskCode,
        "__result" => result,
        _ => throw new InvalidOperationException($"Unsupported Harmony callback parameter {callback.Name}.{parameter.Name}."),
    }).ToArray();
    callback.Invoke(null, arguments);
}

static Image InspectPrivateOverlay(GameObject overlay)
{
    Assert(overlay.name == "MystiaStewardCompanion.TargetOrderHighlight", "The overlay must have a private Mod-owned identity.");
    var components = overlay.GetComponents<Component>();
    Assert(components.Length == 3, "The private overlay must contain exactly RectTransform, CanvasRenderer, and Image.");
    Assert(components.Select(component => component.Pointer).ToHashSet().Count == 3,
        "The private overlay must retain three unique native component identities when wrappers are erased.");
    var imageComponent = overlay.GetComponent(typeof(Image));
    var image = RuntimeReflectionUtility.TryCastRuntimeObject(imageComponent, "UnityEngine.UI.Image") as Image;
    if (image == null) throw new InvalidOperationException("The private overlay Image exact native cast failed.");
    Assert(image.Pointer == imageComponent?.Pointer,
        "The private overlay Image must be recovered by exact native cast without changing identity.");
    Assert(!image.raycastTarget, "The private overlay must never intercept UI input.");
    Assert(image.enabled && overlay.activeInHierarchy, "The private overlay must be renderable.");
    return image;
}

static GameObject FindLatestOverlay()
{
    return GameObject.Instantiated.Last(item => item.name == "MystiaStewardCompanion.TargetOrderHighlight");
}

static PatchRecord FindPatch(Type declaringType, string name, int parameterCount)
{
    var matches = Harmony.Patches
        .Where(patch => patch.Target.DeclaringType == declaringType
            && patch.Target.Name == name
            && patch.Target.GetParameters().Length == parameterCount)
        .ToArray();
    Assert(matches.Length == 1, $"Expected one exact {declaringType.FullName}.{name}/{parameterCount} hook.");
    return matches[0];
}

static void AssertPatch(
    Type declaringType,
    string name,
    int parameterCount,
    bool hasPrefix,
    bool hasPostfix)
{
    var patch = FindPatch(declaringType, name, parameterCount);
    Assert((patch.Prefix != null) == hasPrefix, $"{declaringType.FullName}.{name}/{parameterCount} prefix contract mismatch.");
    Assert((patch.Postfix != null) == hasPostfix, $"{declaringType.FullName}.{name}/{parameterCount} postfix contract mismatch.");
}

static void AssertTrace(string value, bool expected)
{
    var accepted = RuntimeOrderTraceIdService.TryNormalizeRareTraceId(
        value,
        enabled: true,
        out var normalized,
        out _);
    Assert(accepted == expected, $"Unexpected exact trace validation result for '{value}'.");
    Assert(!accepted || normalized == value, "Accepted trace ids must remain byte-for-byte unchanged.");
    Assert(accepted || normalized == "", "Rejected trace ids must not leak a normalized alias.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
