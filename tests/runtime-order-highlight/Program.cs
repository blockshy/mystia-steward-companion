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
Assert(RuntimeOrderTraceIdService.TryNormalizeTargetTraceId(
        RuntimeUiTargetKind.Rare,
        "ignored",
        enabled: false,
        out var disabledTrace,
        out _)
       && disabledTrace == "", "A disabled target must erase its trace without parsing it.");

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

long hudTargetGeneration = 0;
RuntimeUiTargetSetSnapshot CreateHudTargetSet(
    long sessionGeneration,
    params RuntimeUiTargetSnapshot[] targets) => new(
        ++hudTargetGeneration,
        sessionGeneration,
        targets);

var capturedAtA = new DateTime(2026, 8, 2, 8, 0, 0, DateTimeKind.Utc);
var orderA = new GuestsManager.OrderBase();
var captureA = AddCapture(orderA, capturedAtA, deskCode: 0, guestId: 105, foodTagId: 12, beverageTagId: 34);
var traceA = RuntimeOrderTraceIdService.GetRareTraceId(captureA);
Assert(traceA == "R-0001", "The first captured rare order must receive a stable rare trace.");
Assert(RuntimeOrderTraceIdService.GetRareTraceId(captureA) == traceA, "The same capture must retain its trace.");
var targetA = CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, "resolver-a");
Assert(RuntimeUiTargetOrderResolver.TryResolveCurrentCapture(targetA, TimeSpan.FromHours(6), out var resolvedA, out _)
       && resolvedA?.OrderPointer == orderA.Pointer, "A unique current capture must resolve by exact trace, lifecycle, and desk.");
var wrongDeskTarget = CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 1, "resolver-wrong-desk");
Assert(!RuntimeUiTargetOrderResolver.TryResolveCurrentCapture(wrongDeskTarget, TimeSpan.FromHours(6), out _, out var wrongDeskFailure)
       && wrongDeskFailure.Contains("matched 0 active captures", StringComparison.Ordinal), "Desk identity must be an exact cross-check, not a fallback.");

var duplicateCaptureOrder = new GuestsManager.OrderBase();
var duplicateCapture = AddCapture(duplicateCaptureOrder, capturedAtA, deskCode: 0, guestId: 105, foodTagId: 12, beverageTagId: 34);
Assert(RuntimeOrderTraceIdService.GetRareTraceId(duplicateCapture) == traceA, "Equivalent capture metadata must expose the ambiguity under the same trace.");
Assert(!RuntimeUiTargetOrderResolver.TryResolveCurrentCapture(targetA, TimeSpan.FromHours(6), out _, out var duplicateCaptureFailure)
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
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, $"hud:{traceA}:0")));
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == 1, "The exact target order must create one private overlay despite another order sharing its desk.");
var firstOverlay = FindLatestOverlay();
var firstOverlayImage = InspectPrivateOverlay(firstOverlay);
Assert(Math.Abs(firstOverlay.transform.rect.width - 32f) < 0.001f
       && Math.Abs(firstOverlay.transform.localPosition.y + firstOverlay.transform.rect.yMin + 33f) < 0.001f
       && Math.Abs(firstOverlay.transform.localPosition.y + firstOverlay.transform.rect.yMax - 15f) < 0.001f,
    "订单覆盖层必须保留原宽度/底边，并在原生桌号区域下沿结束；不能给整张卡片染色。");
Assert(RuntimeOrderHighlightService.Status.Contains("active", StringComparison.Ordinal), "A unique registered target must become active.");
Assert(RuntimeOrderHighlightService.Status.Contains($"order:0x{orderA.Pointer:x}", StringComparison.Ordinal), "Diagnostics must identify the exact captured order pointer.");
AssertColor(firstOverlayImage.color, RuntimeTargetHighlightColor.DefaultRare,
    "The private border must use the configured rare-order highlight color.");
Assert(Math.Abs(firstOverlayImage.color.a - 0.81f) < .0001f, "订单高亮必须使用固定透明度0.81。");

Time.realtimeSinceStartup = 0.2f;
var firstAlpha = firstOverlayImage.color.a;
RuntimeOrderHighlightService.Tick();
Assert(firstOverlayImage.color.a == firstAlpha, "订单高亮不能因时间推进而明暗呼吸。");
var stableColorWrites = firstOverlayImage.ColorWrites;
for (var frame = 0; frame < 120; frame++)
{
    Time.realtimeSinceStartup = .2f + frame / 60f;
    RuntimeOrderHighlightService.Tick();
    Assert(firstOverlayImage.color.a == firstAlpha, "持续时钟推进不能改变订单透明度。");
}
Assert(firstOverlayImage.ColorWrites == stableColorWrites, "静态订单颜色不应逐帧重复写入UI图层。");
Assert(GameObject.Instantiated.Count == 1, "Pulse updates must not recreate a healthy overlay.");
Assert(elementA.current == nativeCurrent && elementA.ChangeBorderStyleCalls == 0, "Highlighting must not change the game's current order focus.");
Assert(nativeImage.color == nativeColor
       && nativeImage.raycastTarget == nativeRaycast
       && nativeImage.enabled == nativeEnabled
       && nativeImage.transform.localScale == nativeScale,
    "Highlighting must not mutate the native current-order border.");

// 同一稀客订单内容发生变化只更新目标与颜色，不能反复克隆同一 HUD 边框。
// 这些 Tick 不推进健康检查时钟，确保发布代次变更路径本身完成即时身份核验。
var rareContentColor = new RuntimeTargetHighlightColor(0x83, 0x51, 0xBF);
var destroyedBeforeRareContent = GameObject.Destroyed.Count;
foreach (var content in new[]
         {
             ChangeTargetContent(targetA, "rare:recipe-changed", 26, 3, new[] { 15, 20, 28 }, Array.Empty<int>()),
             ChangeTargetContent(targetA, "rare:beverage-delivered", 26, -1, new[] { 15, 20, 28 }, Array.Empty<int>()),
             ChangeTargetContent(targetA, "rare:extras-color-changed", 31, -1, new[] { 7, 15 }, new[] { 7, 7 }, rareContentColor),
         })
{
    RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(2, content));
    RuntimeOrderHighlightService.Tick();
    Assert(GameObject.Instantiated.Count == 1 && GameObject.Destroyed.Count == destroyedBeforeRareContent,
        "同一稀客订单只改变料理/酒水/加料/修订时不能销毁或新建边框。");
    Assert(ReferenceEquals(firstOverlay, FindLiveOverlayFor(elementA)),
        "稀客内容变动后仍应持有原来的精确 HUD 边框。");
}
AssertColor(firstOverlayImage.color, rareContentColor, "复用边框仍须立即应用新目标颜色。");

// A duplicate HUD card for the same native order must be rejected rather than picking either card.
var duplicateElement = CreateElement(controller, orderA, deskCode: 0);
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(2));
RuntimeOrderHighlightService.Tick();
var destroyedBeforeDuplicate = GameObject.Destroyed.Count;
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, $"hud:{traceA}:0")));
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == 1, "Duplicate HUD elements must not create a replacement overlay.");
Assert(GameObject.Destroyed.Count == destroyedBeforeDuplicate, "Duplicate HUD resolution must fail before creating a visual.");
Assert(RuntimeOrderHighlightService.Status.Contains("matched 2 registered HUD elements", StringComparison.Ordinal), "Duplicate HUD identity must be diagnosed precisely.");
TeardownElement(duplicateElement, "Out");
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == 2, "Removing the duplicate card must let the unchanged exact target recover.");

// Cloning is only safe for a leaf image outside a parent-controlled layout hierarchy.
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(2));
RuntimeOrderHighlightService.Tick();
var templateChild = new GameObject { name = "UnsafeTemplateChild" };
templateChild.transform.parent = nativeImage.transform;
var instantiatedBeforeChildShape = GameObject.Instantiated.Count;
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, $"hud:{traceA}:0")));
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeChildShape, "A border template with child objects must fail before cloning.");
Assert(RuntimeOrderHighlightService.Status.Contains("child objects instead of a leaf-only visual shape", StringComparison.Ordinal),
    "A non-leaf template must expose the exact visual safety gate.");

RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(2));
RuntimeOrderHighlightService.Tick();
templateChild.transform.parent = null;
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, $"hud:{traceA}:0")));
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeChildShape + 1, "The unchanged leaf-only border must recover after the unsafe child is removed.");

RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(2));
RuntimeOrderHighlightService.Tick();
var unknownVisualComponent = new Component();
nativeImage.gameObject.Attach(unknownVisualComponent);
var instantiatedBeforeUnknownComponent = GameObject.Instantiated.Count;
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, $"hud:{traceA}:0")));
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeUnknownComponent,
    "A border template with an additional unknown native component must fail before cloning.");
Assert(RuntimeOrderHighlightService.Status.Contains("has 4 components instead of the exact visual-only shape", StringComparison.Ordinal),
    "An additional native component must remain visible as an exact component-count failure.");

RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(2));
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
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, $"hud:{traceA}:0")));
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeWrongComponent,
    "A three-component template missing the exact CanvasRenderer must fail before cloning.");
Assert(RuntimeOrderHighlightService.Status.Contains("has no exact live native UnityEngine.CanvasRenderer", StringComparison.Ordinal),
    "A wrong native component must be rejected by the typed CanvasRenderer query, not accepted by count alone.");

RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(2));
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
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, $"hud:{traceA}:0")));
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeManagedLayout, "A LayoutGroup-managed parent must fail before sibling insertion.");
Assert(RuntimeOrderHighlightService.Status.Contains("managed by UnityEngine.UI.LayoutGroup", StringComparison.Ordinal),
    "The parent safety gate must use a typed native LayoutGroup query even when enumeration exposes base wrappers.");

RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(2));
RuntimeOrderHighlightService.Tick();
elementA.gameObject.Detach(managedLayout);
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, $"hud:{traceA}:0")));
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeManagedLayout + 1, "A normal parent with a leaf border must remain supported.");
Assert(RuntimeOrderHighlightService.Status.Contains("active", StringComparison.Ordinal), "The normal leaf visual must stay active after both safety-gate probes.");

// A typed Image lookup may return a derived native UI class; exact class identity must reject it.
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(2));
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
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(
        derivedImageTrace,
        derivedImageCapture.OrderLifecycleSequence,
        3,
        $"hud:{derivedImageTrace}:3")));
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeDerivedImage,
    "A derived native Image class must fail before cloning even when typed Image lookup succeeds.");
Assert(RuntimeOrderHighlightService.Status.Contains("native component is not the exact UnityEngine.UI.Image class", StringComparison.Ordinal),
    "The exact native Image class mismatch must remain visible in diagnostics.");
TeardownElement(derivedImageElement, "Out");
SpecialOrderRuntimeCapture.Captures.Remove(derivedImageCapture);
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, $"hud:{traceA}:0")));
RuntimeOrderHighlightService.Tick();

// Cross-generation and main-thread gates must stop before touching Unity visuals.
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    3,
    CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, $"hud:{traceA}:0")));
RuntimeOrderHighlightService.Tick();
Assert(RuntimeOrderHighlightService.Status.Contains("different night-business session", StringComparison.Ordinal), "A target from another business generation must fail closed.");
var instantiatedBeforeWrongThread = GameObject.Instantiated.Count;
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, 2, unityThreadId + 1);
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, $"hud:{traceA}:0")));
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeWrongThread, "A non-Unity-thread tick must not create a visual.");
Assert(RuntimeOrderHighlightService.Status.Contains("Unity main thread", StringComparison.Ordinal), "The thread gate must be visible in diagnostics.");
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, 2, unityThreadId);
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeWrongThread + 1, "The same valid target must recover on the Unity thread.");

// Invalid protocol input must clear the current visual and must not be silently normalized.
var destroyedBeforeInvalidTrace = GameObject.Destroyed.Count;
var invalidTraceRejected = false;
try
{
    RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
        2,
        CreateRareTarget(" R-0001", captureA.OrderLifecycleSequence, 0, "invalid-trace")));
}
catch (ArgumentException)
{
    invalidTraceRejected = true;
}
Assert(invalidTraceRejected, "An invalid exact trace must be rejected at immutable target construction.");
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(2));
RuntimeOrderHighlightService.Tick();
Assert(GameObject.Destroyed.Count == destroyedBeforeInvalidTrace + 1, "An invalid exact trace must retire the live overlay.");
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, $"hud:{traceA}:0")));
RuntimeOrderHighlightService.Tick();

// Registration reads the exact get-only OrderBase.DeskCode, including non-zero desks.
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(2));
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
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(
        nonZeroDeskTrace,
        nonZeroDeskCapture.OrderLifecycleSequence,
        2,
        $"hud:{nonZeroDeskTrace}:2")));
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
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(
        mismatchedDeskTrace,
        mismatchedDeskCapture.OrderLifecycleSequence,
        5,
        $"hud:{mismatchedDeskTrace}:5")));
RuntimeOrderHighlightService.Tick();
Assert(RuntimeOrderHighlightService.Status.Contains("matched 0 registered HUD elements", StringComparison.Ordinal),
    "A capture desk that disagrees with exact OrderBase.DeskCode must fail closed without using Initialize's desk argument.");
TeardownElement(mismatchedDeskElement, "DestroySelf");
SpecialOrderRuntimeCapture.Captures.Remove(mismatchedDeskCapture);
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(traceA, captureA.OrderLifecycleSequence, 0, $"hud:{traceA}:0")));
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
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    2,
    CreateRareTarget(traceB, captureB.OrderLifecycleSequence, 0, $"hud:{traceB}:0")));
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
UnityEngine.Object.Destroy(overlayBeforeAbandon);

// Rare and normal targets own independent HUD overlays and consume their real capture stores.
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, 100, unityThreadId);
RuntimeOrderHighlightService.Resume("dual target HUD coverage");
SpecialOrderRuntimeCapture.Captures.Clear();
NormalOrderRuntimeCapture.Captures.Clear();
var dualRareOrder = new GuestsManager.OrderBase(deskCode: 0);
var dualRareCapture = AddCapture(
    dualRareOrder,
    capturedAtA.AddMinutes(1),
    deskCode: 0,
    guestId: 201,
    foodTagId: 51,
    beverageTagId: 61);
var dualNormalOrder = new GuestsManager.OrderBase(deskCode: 1);
var dualNormalController = new GuestsManager.OrderBase();
var dualNormalCapture = AddNormalCapture(
    dualNormalOrder,
    dualNormalController,
    capturedAtA.AddMinutes(1),
    deskCode: 1,
    lifecycleSequence: 101);
var dualRareColor = new RuntimeTargetHighlightColor(0xE2, 0x9B, 0x24);
var dualNormalColor = new RuntimeTargetHighlightColor(0x31, 0x8A, 0xC4);
var dualRareTarget = CreateRareTarget(
    RuntimeOrderTraceIdService.GetRareTraceId(dualRareCapture),
    dualRareCapture.OrderLifecycleSequence,
    0,
    "dual-hud-rare",
    dualRareColor);
var dualNormalTarget = CreateNormalTarget(dualNormalCapture, "dual-hud-normal", dualNormalColor);
var dualController = new OrderController();
var dualRareElement = CreateElement(dualController, dualRareOrder, deskCode: 0);
var dualNormalElement = CreateElement(dualController, dualNormalOrder, deskCode: 1);
Time.realtimeSinceStartup = 10f;
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(100, dualRareTarget, dualNormalTarget));
RuntimeOrderHighlightService.Tick();
var dualRareOverlay = FindLiveOverlayFor(dualRareElement);
var dualNormalOverlay = FindLiveOverlayFor(dualNormalElement);
AssertColor(InspectPrivateOverlay(dualRareOverlay).color, dualRareColor,
    "The rare HUD card must use its injected color.");
AssertColor(InspectPrivateOverlay(dualNormalOverlay).color, dualNormalColor,
    "The normal HUD card must use its injected color from a real normal capture.");
Assert(RuntimeOrderHighlightService.Status.StartsWith("active:2/2", StringComparison.Ordinal)
       && RuntimeOrderHighlightService.Status.Contains("Rare:R-", StringComparison.Ordinal)
       && RuntimeOrderHighlightService.Status.Contains("Normal:N-", StringComparison.Ordinal),
    "Both exact HUD target kinds must remain active simultaneously.");

// 对应真实日志中普客酒水 2→-1 的相同订单修订：原生目标与 HUD 绑定未变时不创建新框。
var normalContentCreated = GameObject.Instantiated.Count;
var normalContentDestroyed = GameObject.Destroyed.Count;
foreach (var content in new[]
         {
             ChangeTargetContent(dualNormalTarget, "normal:recipe", 26, 2, new[] { 15, 20, 28 }, Array.Empty<int>()),
             ChangeTargetContent(dualNormalTarget, "normal:beverage-delivered", 26, -1, new[] { 15, 20, 28 }, Array.Empty<int>()),
             ChangeTargetContent(dualNormalTarget, "normal:revision-only", 26, -1, new[] { 15, 20, 28 }, Array.Empty<int>()),
         })
{
    RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(100, dualRareTarget, content));
    RuntimeOrderHighlightService.Tick();
    Assert(GameObject.Instantiated.Count == normalContentCreated && GameObject.Destroyed.Count == normalContentDestroyed,
        "同一普客只改变配方/酒水/修订时不能重建任一通道的边框。");
    Assert(ReferenceEquals(dualNormalOverlay, FindLiveOverlayFor(dualNormalElement))
        && ReferenceEquals(dualRareOverlay, FindLiveOverlayFor(dualRareElement)),
        "内容更新必须保留两个独立通道各自的原生视觉身份。");
}

RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(100, dualNormalTarget));
Time.realtimeSinceStartup = 10.1f;
RuntimeOrderHighlightService.Tick();
Assert(dualRareOverlay.m_CachedPtr == IntPtr.Zero,
    "Removing the rare slot must retire only its owned HUD overlay.");
Assert(dualNormalOverlay.m_CachedPtr != IntPtr.Zero
       && ReferenceEquals(FindLiveOverlayFor(dualNormalElement), dualNormalOverlay),
    "Removing one target slot must preserve the other slot's exact HUD overlay identity.");

// If both typed targets resolve to one native order, neither target may win by iteration order.
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(100));
RuntimeOrderHighlightService.Tick();
SpecialOrderRuntimeCapture.Captures.Clear();
NormalOrderRuntimeCapture.Captures.Clear();
var collisionOrder = new GuestsManager.OrderBase(deskCode: 2);
var collisionRareCapture = AddCapture(
    collisionOrder,
    capturedAtA.AddMinutes(2),
    deskCode: 2,
    guestId: 202,
    foodTagId: 52,
    beverageTagId: 62);
var collisionNormalCapture = AddNormalCapture(
    collisionOrder,
    new GuestsManager.OrderBase(),
    capturedAtA.AddMinutes(2),
    deskCode: 2,
    lifecycleSequence: 102);
_ = CreateElement(dualController, collisionOrder, deskCode: 2);
var liveOverlaysBeforeCollision = LiveOverlays().Length;
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(
    100,
    CreateRareTarget(
        RuntimeOrderTraceIdService.GetRareTraceId(collisionRareCapture),
        collisionRareCapture.OrderLifecycleSequence,
        2,
        "collision-rare"),
    CreateNormalTarget(collisionNormalCapture, "collision-normal")));
Time.realtimeSinceStartup = 11f;
RuntimeOrderHighlightService.Tick();
Assert(LiveOverlays().Length == liveOverlaysBeforeCollision,
    "Two target kinds resolving to one native order must fail before either overlay is created.");
Assert(RuntimeOrderHighlightService.Status.Contains(
        "exact native order is claimed by both target kinds",
        StringComparison.Ordinal),
    "The cross-kind native-order collision must remain visible in diagnostics.");
RuntimeOrderHighlightService.Dispose("dual target HUD coverage complete");

// 八张真实注册卡片、一张活动高亮：非活动卡限频清理，但活动卡每一帧仍读取确切订单。
// 这是离线操作次数对比，不代表 Unity 帧率或游戏内耗时。
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, 200, unityThreadId);
RuntimeOrderHighlightService.Resume("eight-card maintenance regression");
SpecialOrderRuntimeCapture.Captures.Clear();
NormalOrderRuntimeCapture.Captures.Clear();
var perfController = new OrderController();
var perfOrders = Enumerable.Range(0, 8).Select(index => new GuestsManager.OrderBase(deskCode: index)).ToArray();
var perfElements = perfOrders.Select((order, index) => CreateElement(perfController, order, index)).ToArray();
var perfCapture = AddCapture(perfOrders[0], capturedAtA.AddMinutes(3), 0, 301, 71, 81);
var perfTarget = CreateRareTarget(RuntimeOrderTraceIdService.GetRareTraceId(perfCapture),
    perfCapture.OrderLifecycleSequence, 0, "eight-card-target");
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(200, perfTarget));
Time.realtimeSinceStartup = 20f;
RuntimeOrderHighlightService.Tick();
var perfOverlay = FindLiveOverlayFor(perfElements[0]);
foreach (var element in perfElements) element.ActiveOrderReadCount = 0;
const int simulatedFrames = 600;
for (var frame = 1; frame <= simulatedFrames; frame++)
{
    Time.realtimeSinceStartup = 20f + frame / 60f;
    RuntimeOrderHighlightService.Tick();
}
var inactiveReads = perfElements.Skip(1).Sum(element => element.ActiveOrderReadCount);
Assert(perfElements[0].ActiveOrderReadCount >= simulatedFrames,
    "活动卡片必须逐帧读取 ActiveOrder，不能跟随非活动清理时钟降频。");
Assert(inactiveReads > 0 && inactiveReads <= 7 * 41,
    "八张卡片场景中的七张非活动卡只按 250ms 清理，不得每帧反射读取。");
Assert(ReferenceEquals(perfOverlay, FindLiveOverlayFor(perfElements[0])),
    "稳定目标的限频维护不得重建活动视觉。");
Console.WriteLine($"MAINTENANCE-COUNTS frames={simulatedFrames}; cards=8; oldInactiveReads={7 * simulatedFrames}; newInactiveReads={inactiveReads}; activeReads={perfElements[0].ActiveOrderReadCount}");

// 即使不调用任何 teardown hook，活动卡被游戏重绑也必须在下一个 Tick 撤销旧高亮。
perfElements[0].BindActiveOrder(new GuestsManager.OrderBase(deskCode: 0));
Time.realtimeSinceStartup += 0.001f;
RuntimeOrderHighlightService.Tick();
Assert(perfOverlay.m_CachedPtr == IntPtr.Zero,
    "非清理帧发现活动订单重绑时必须即时销毁旧视觉。");
RuntimeOrderHighlightService.Dispose("eight-card maintenance regression complete");

// 内容可复用不等于原生身份可复用：在未到 250ms 健康检查的同一时刻更换控制器、
// 订单生命周期或撤回权限，发布代次处理必须立即撤销旧框并按新精确捕获重新绑定。
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, 201, unityThreadId);
RuntimeOrderHighlightService.Resume("target revision identity regression");
SpecialOrderRuntimeCapture.Captures.Clear();
NormalOrderRuntimeCapture.Captures.Clear();
var revisionOrder = new GuestsManager.OrderBase(deskCode: 1);
var revisionController = new OrderController();
var revisionCapture = AddNormalCapture(revisionOrder, new GuestsManager.OrderBase(),
    capturedAtA.AddMinutes(4), 1, 301);
var revisionElement = CreateElement(revisionController, revisionOrder, 1);
Time.realtimeSinceStartup = 40f;
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(201, CreateNormalTarget(revisionCapture, "revision:initial")));
RuntimeOrderHighlightService.Tick();
var revisionOverlay = FindLiveOverlayFor(revisionElement);
var beforeControllerReplacement = GameObject.Instantiated.Count;
NormalOrderRuntimeCapture.Captures.Remove(revisionCapture);
revisionCapture = revisionCapture with { ControllerObject = new GuestsManager.OrderBase() };
NormalOrderRuntimeCapture.Captures.Add(revisionCapture);
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(201, CreateNormalTarget(revisionCapture, "revision:new-controller")));
RuntimeOrderHighlightService.Tick();
Assert(revisionOverlay.m_CachedPtr == IntPtr.Zero && GameObject.Instantiated.Count == beforeControllerReplacement + 1,
    "控制器指针改变时，即使托管订单身份相同也必须在发布代次变更时立即重建。");
revisionOverlay = FindLiveOverlayFor(revisionElement);
var beforeLifecycleReplacement = GameObject.Instantiated.Count;
NormalOrderRuntimeCapture.Captures.Remove(revisionCapture);
revisionCapture = revisionCapture with { OrderLifecycleSequence = 302 };
NormalOrderRuntimeCapture.Captures.Add(revisionCapture);
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(201, CreateNormalTarget(revisionCapture, "revision:new-lifecycle")));
RuntimeOrderHighlightService.Tick();
Assert(revisionOverlay.m_CachedPtr == IntPtr.Zero && GameObject.Instantiated.Count == beforeLifecycleReplacement + 1,
    "订单指针相同但生命周期变化时仍须重建，不能把原生地址重用当同一订单。");

revisionOverlay = FindLiveOverlayFor(revisionElement);
var beforeMissingIdentity = GameObject.Instantiated.Count;
NormalOrderRuntimeCapture.Captures.Remove(revisionCapture);
var incompleteCapture = revisionCapture with { ControllerObject = null };
NormalOrderRuntimeCapture.Captures.Add(incompleteCapture);
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(201, CreateNormalTarget(incompleteCapture, "revision:missing-controller")));
RuntimeOrderHighlightService.Tick();
Assert(revisionOverlay.m_CachedPtr == IntPtr.Zero && GameObject.Instantiated.Count == beforeMissingIdentity,
    "控制器身份缺失时必须撤销旧框，不得按桌号或旧目标续用。");
NormalOrderRuntimeCapture.Captures.Remove(incompleteCapture);
NormalOrderRuntimeCapture.Captures.Add(revisionCapture);
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(201, CreateNormalTarget(revisionCapture, "revision:recovered")));
RuntimeOrderHighlightService.Tick();
revisionOverlay = FindLiveOverlayFor(revisionElement);
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(201));
RuntimeOrderHighlightService.Tick();
Assert(revisionOverlay.m_CachedPtr == IntPtr.Zero, "权限撤回为空目标集必须立即移除高亮，不能因身份可复用而保留。");
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(201, CreateNormalTarget(revisionCapture, "revision:authorized-again")));
RuntimeOrderHighlightService.Tick();
revisionOverlay = FindLiveOverlayFor(revisionElement);
var beforeBusinessReplacement = GameObject.Instantiated.Count;
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, 202, unityThreadId);
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(202, CreateNormalTarget(revisionCapture, "revision:new-business")));
RuntimeOrderHighlightService.Tick();
Assert(revisionOverlay.m_CachedPtr == IntPtr.Zero && GameObject.Instantiated.Count == beforeBusinessReplacement,
    "经营会话变化必须撤销旧框，且不能复用上一会话登记的 HUD 元素。");
RuntimeOrderHighlightService.Dispose("target revision identity regression complete");

// 八单同类与混合单都应独立绑定；重排、改色及撤回一单不重建其余原生卡片的自有覆盖层。
foreach (var composition in new[] { "rare", "normal", "mixed" })
{
    const long businessGeneration = 303;
    RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, businessGeneration, unityThreadId);
    RuntimeOrderHighlightService.Resume("multi-order exact identity regression");
    SpecialOrderRuntimeCapture.Captures.Clear(); NormalOrderRuntimeCapture.Captures.Clear();
    var owner = new OrderController();
    var targets = new List<RuntimeUiTargetSnapshot>();
    var elements = new List<OrderingElement>();
    for (var slot = 0; slot < 8; slot++)
    {
        var order = new GuestsManager.OrderBase(deskCode: slot);
        elements.Add(CreateElement(owner, order, slot));
        var color = new RuntimeTargetHighlightColor((byte)(20 + 20 * slot), 120, 210);
        if (composition == "normal" || composition == "mixed" && slot % 2 == 1)
        {
            var capture = AddNormalCapture(order, new GuestsManager.OrderBase(), DateTime.UtcNow, slot, 400 + slot);
            targets.Add(CreateNormalTarget(capture, "many-normal-" + slot, color, slot));
        }
        else
        {
            var capture = AddCapture(order, DateTime.UtcNow, slot, 500 + slot, 10, 20);
            targets.Add(CreateRareTarget(RuntimeOrderTraceIdService.GetRareTraceId(capture), capture.OrderLifecycleSequence,
                slot, "many-rare-" + slot, color, slot));
        }
    }
    RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(businessGeneration, targets.ToArray()));
    Time.realtimeSinceStartup = 300f;
    RuntimeOrderHighlightService.Tick();
    var overlays = elements.Select(FindLiveOverlayFor).ToArray();
    Assert(overlays.Select(item => item.Pointer).Distinct().Count() == 8, composition + " 八单必须有八个精确独立覆盖层。");
    var createdCount = GameObject.Instantiated.Count;
    targets[5] = ChangeTargetContent(targets[5], "changed-color", -1, -1, Array.Empty<int>(), Array.Empty<int>(),
        new RuntimeTargetHighlightColor(220, 20, 100));
    RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(businessGeneration, targets.AsEnumerable().Reverse().ToArray()));
    RuntimeOrderHighlightService.Tick();
    Assert(GameObject.Instantiated.Count == createdCount && elements.Select(FindLiveOverlayFor).SequenceEqual(overlays),
        composition + " 改色或目标顺序变化不得销毁并重建已有覆盖层。");
    AssertColor(InspectPrivateOverlay(overlays[5]).color, targets[5].Color, "改色必须同步到同一订单的原覆盖层。");
    RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(businessGeneration, targets.Where(target => target.ColorSlot != 3).ToArray()));
    RuntimeOrderHighlightService.Tick();
    Assert(overlays[3].m_CachedPtr == IntPtr.Zero && overlays.Where((_, index) => index != 3).All(item => item.m_CachedPtr != IntPtr.Zero),
        composition + " 撤回一单不能清除同类其他订单的覆盖层。");
    RuntimeOrderHighlightService.Dispose("multi-order regression complete");
    Assert(overlays.All(item => item.m_CachedPtr == IntPtr.Zero), "模块停用必须释放全部八单自有覆盖层。");
}

// 实际桌号矩形决定正文上沿：字号增大、原焦点边框scale=0、卡片池重用均不猜固定像素。
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, 404, unityThreadId);
RuntimeOrderHighlightService.Resume("exact desk label body bounds");
SpecialOrderRuntimeCapture.Captures.Clear(); NormalOrderRuntimeCapture.Captures.Clear();
var bodyOwner = new OrderController();
var bodyOrder = new GuestsManager.OrderBase(deskCode: 0);
var bodyCapture = AddNormalCapture(bodyOrder, new GuestsManager.OrderBase(), capturedAtA.AddHours(1), 0, 401);
var bodyElement = CreateElement(bodyOwner, bodyOrder, 0);
var sourceBody = bodyElement.borderStyleImageForCurrent.gameObject.transform;
var deskHeader = bodyElement.text.gameObject.transform;
var originalSize = sourceBody.sizeDelta;
var originalPosition = sourceBody.anchoredPosition;
sourceBody.localScale = new Vector3(0, 0, 0); // 原生ChangeBorderStyle会如此隐藏当前边框。
Time.realtimeSinceStartup = 100;
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(404, CreateNormalTarget(bodyCapture, "body:initial")));
RuntimeOrderHighlightService.Tick();
var bodyOverlay = FindLiveOverlayFor(bodyElement);
foreach (var headerHeight in new[] { 20f, 28f, 16f })
{
    deskHeader.sizeDelta = new Vector2(64, headerHeight);
    Time.realtimeSinceStartup += 0.3f;
    RuntimeOrderHighlightService.Tick();
    var expectedTop = deskHeader.localPosition.y - headerHeight / 2;
    Assert(Math.Abs(bodyOverlay.transform.localPosition.y + bodyOverlay.transform.rect.yMax - expectedTop) < 0.001f,
        "桌号字号/区域变化后高亮上沿必须跟随精确下沿，不能保留旧裁剪或侵入桌号。");
    Assert(Math.Abs(bodyOverlay.transform.localPosition.y + bodyOverlay.transform.rect.yMin + 33f) < 0.001f
        && bodyOverlay.transform.rect.width == 32 && bodyOverlay.transform.localScale == Vector3.one,
        "裁剪必须始终保留底边与宽度，且不能继承隐藏边框的零缩放。");
    Assert(sourceBody.sizeDelta == originalSize && sourceBody.anchoredPosition == originalPosition
        && sourceBody.localScale == new Vector3(0, 0, 0)
        && deskHeader.sizeDelta == new Vector2(64, headerHeight),
        "正文裁剪不得写入原背景或桌号矩形。");
    AssertOriginalImageGeometry(bodyOverlay, sourceBody);
}
var realHeaderParent = deskHeader.parent;
deskHeader.parent = new GameObject().transform;
Time.realtimeSinceStartup += 0.3f;
RuntimeOrderHighlightService.Tick();
Assert(bodyOverlay.m_CachedPtr == IntPtr.Zero, "桌号字段被移到另一父级时必须撤回覆盖层，不能猜正文范围。");
deskHeader.parent = realHeaderParent;
Time.realtimeSinceStartup += 0.6f;
RuntimeOrderHighlightService.Tick();
bodyOverlay = FindLiveOverlayFor(bodyElement);
NormalOrderRuntimeCapture.Captures.Clear();
var pooledBodyOrder = new GuestsManager.OrderBase(deskCode: 2);
var pooledBodyCapture = AddNormalCapture(pooledBodyOrder, new GuestsManager.OrderBase(), capturedAtA.AddHours(2), 2, 402);
deskHeader.sizeDelta = new Vector2(64, 24);
var pooledBodyElement = CreateElement(bodyOwner, pooledBodyOrder, 2, bodyElement);
RuntimeOrderHighlightService.UpdateTargets(CreateHudTargetSet(404, CreateNormalTarget(pooledBodyCapture, "body:pooled")));
RuntimeOrderHighlightService.Tick();
var pooledBodyOverlay = FindLiveOverlayFor(pooledBodyElement);
Assert(bodyOverlay.m_CachedPtr == IntPtr.Zero
    && Math.Abs(pooledBodyOverlay.transform.localPosition.y + pooledBodyOverlay.transform.rect.yMax - 13f) < 0.001f,
    "卡片池重用必须销毁旧覆盖层，并根据新桌号区域重新计算正文，不能累积裁剪量。");

// 手工布置固定物理范围：背景x=-10..30、y=0..96，桌号下沿y=72。
// 用不同pivot/非中心父pivot/固定或拉伸锚点表达同一几何，预期始终为x=-10..30、y=0..72；
// 这可检出把anchoredPosition当localPosition，或把sizeDelta当真实rect的错误。
var geometryParent = (RectTransform)sourceBody.parent!;
geometryParent.sizeDelta = new Vector2(200, 240);
geometryParent.pivot = new Vector2(0.25f, 0.75f);
foreach (var stretched in new[] { false, true })
foreach (var sourcePivot in new[] { 0f, 0.5f, 1f })
foreach (var headerPivot in new[] { 0f, 0.5f, 1f })
{
    sourceBody.anchorMin = stretched ? new Vector2(0.2f, 0.15f) : new Vector2(0.7f, 0.8f);
    sourceBody.anchorMax = stretched ? new Vector2(0.8f, 0.85f) : sourceBody.anchorMin;
    sourceBody.pivot = new Vector2(sourcePivot, sourcePivot);
    sourceBody.sizeDelta = stretched ? new Vector2(-80, -72) : new Vector2(40, 96);
    sourceBody.anchoredPosition = stretched
        ? new Vector2(-80 * sourcePivot, 144 - 72 * sourcePivot)
        : new Vector2(-100 + 40 * sourcePivot, -12 + 96 * sourcePivot);
    deskHeader.anchorMin = stretched ? new Vector2(0.1f, 0.7f) : new Vector2(0.3f, 0.9f);
    deskHeader.anchorMax = stretched ? new Vector2(0.9f, 0.9f) : deskHeader.anchorMin;
    deskHeader.pivot = new Vector2(0.5f, headerPivot);
    deskHeader.sizeDelta = stretched ? new Vector2(-112, -24) : new Vector2(48, 24);
    deskHeader.anchoredPosition = stretched
        ? new Vector2(-40, 84 - 24 * headerPivot)
        : new Vector2(0, 36 + 24 * headerPivot);
    var sourceBefore = (sourceBody.sizeDelta, sourceBody.anchoredPosition, sourceBody.pivot, sourceBody.anchorMin, sourceBody.anchorMax);
    var headerBefore = (deskHeader.sizeDelta, deskHeader.anchoredPosition, deskHeader.pivot, deskHeader.anchorMin, deskHeader.anchorMax);
    Assert(Math.Abs(sourceBody.rect.height - 96) < 0.001f
        && Math.Abs(sourceBody.localPosition.y + sourceBody.rect.yMin) < 0.001f
        && Math.Abs(deskHeader.localPosition.y + deskHeader.rect.yMin - 72) < 0.001f,
        "人工锚点夹具必须保持已声明的背景和桌号物理范围。");
    Assert(Math.Abs(sourceBody.anchoredPosition.y - sourceBody.localPosition.y) > 0.001f,
        "该夹具必须实际区分anchoredPosition与localPosition，不能在零参考点上空测。");
    Time.realtimeSinceStartup += 0.3f;
    RuntimeOrderHighlightService.Tick();
    var rect = pooledBodyOverlay.transform.rect;
    var position = pooledBodyOverlay.transform.localPosition;
    Assert(pooledBodyOverlay.m_CachedPtr != IntPtr.Zero
        && Math.Abs(position.x + rect.xMin + 10) < 0.001f
        && Math.Abs(position.x + rect.xMax - 30) < 0.001f
        && Math.Abs(position.y + rect.yMin) < 0.001f
        && Math.Abs(position.y + rect.yMax - 72) < 0.001f,
        "固定/拉伸锚点和pivot0/0.5/1都必须保留正文左右/底边，且上沿精确止于桌号下沿。");
    Assert(pooledBodyOverlay.transform.pivot == sourceBody.pivot
        && pooledBodyOverlay.transform.anchorMin == sourceBody.anchorMin
        && pooledBodyOverlay.transform.anchorMax == sourceBody.anchorMax,
        "裁剪不应偷偷改为固定锚点或中心pivot。");
    Assert(sourceBefore == (sourceBody.sizeDelta, sourceBody.anchoredPosition, sourceBody.pivot, sourceBody.anchorMin, sourceBody.anchorMax)
        && headerBefore == (deskHeader.sizeDelta, deskHeader.anchoredPosition, deskHeader.pivot, deskHeader.anchorMin, deskHeader.anchorMax),
        "几何裁剪不得改写原背景或桌号的锚点、尺寸和位置。");
    AssertOriginalImageGeometry(pooledBodyOverlay, sourceBody);
}
// 私有裁剪器被关闭、缩放或子图脱离mask时必须整组撤回，不允许回退为整张订单染色。
foreach (var corruption in new Action<GameObject>[]
{
    overlay => ((RectMask2D)overlay.GetComponent(typeof(RectMask2D))!).enabled = false,
    overlay => overlay.transform.localScale = new Vector3(1, .5f, 1),
    overlay => InspectPrivateOverlay(overlay).maskable = false,
    overlay => InspectPrivateOverlay(overlay).raycastTarget = true,
})
{
    var oldContainer = FindLiveOverlayFor(pooledBodyElement);
    var oldImageObject = oldContainer.transform.Children.Single().gameObject;
    corruption(oldContainer);
    Time.realtimeSinceStartup += .3f;
    RuntimeOrderHighlightService.Tick();
    Assert(oldContainer.m_CachedPtr == IntPtr.Zero && oldImageObject.m_CachedPtr == IntPtr.Zero,
        "私有容器失效必须连同未裁剪的子图一起销毁，不能遗留重复桌号图形。");
    Time.realtimeSinceStartup += .6f;
    RuntimeOrderHighlightService.Tick();
    AssertOriginalImageGeometry(FindLiveOverlayFor(pooledBodyElement), sourceBody);
}
RuntimeOrderHighlightService.Dispose("exact body bounds complete");

var serviceSource = File.ReadAllText("mods/bepinex/src/Save/RuntimeOrderHighlightService.cs");
var traceSource = File.ReadAllText("mods/bepinex/src/Save/RuntimeOrderTraceIdService.cs");
var resolverSource = File.ReadAllText("mods/bepinex/src/Save/RuntimeUiTargetOrderResolver.cs");
foreach (var required in new[]
         {
             "CreateOrderingElement",
             "RuntimeUiTargetOrderResolver.TryResolveCurrentCapture",
             "exact native order is claimed by both target kinds",
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
             "UnityEngine.Object.Instantiate(sourceObject, cloneTransform)",
             "SetRaycastTarget!.Invoke(clonedImage, new object?[] { false })",
             "RuntimeTargetHighlightStyle.BuildOrderHighlightColor",
         })
{
    Assert(serviceSource.Contains(required, StringComparison.Ordinal), $"Order highlighting must retain its exact runtime contract: {required}");
}
Assert(traceSource.Contains("character < '0' || character > '9'", StringComparison.Ordinal), "Rare trace validation must remain ASCII-only.");
Assert(resolverSource.Contains("string.Equals(runtimeKey, $\"ptr:{orderPointer:x}\", StringComparison.Ordinal)", StringComparison.Ordinal),
    "Capture resolution must retain the nonzero native-key cross-check.");
Assert(resolverSource.Contains("NormalOrderRuntimeCapture.Snapshot(maxAge)", StringComparison.Ordinal)
       && resolverSource.Contains("RuntimeOrderTraceIdService.GetNormalTraceId(capture)", StringComparison.Ordinal)
       && resolverSource.Contains("capture.RuntimeKey, target.OrderKey", StringComparison.Ordinal),
    "Normal HUD resolution must consume the exact normal capture, N trace, and raw ptr order key.");

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

Console.WriteLine($"Runtime order highlight smoke passed: {HudAssertionCounter.Count} assertions; direct callbacks, no game/Harmony injection.");

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
        OrderLifecycleSequence: 1,
        DeskCode: deskCode,
        GuestId: guestId,
        FoodTagId: foodTagId,
        BeverageTagId: beverageTagId,
        IsFreeOrder: false);
    SpecialOrderRuntimeCapture.Captures.Add(capture);
    return capture;
}

static CapturedRuntimeNormalOrder AddNormalCapture(
    GuestsManager.OrderBase order,
    object controller,
    DateTime capturedAt,
    int deskCode,
    long lifecycleSequence)
{
    var capture = new CapturedRuntimeNormalOrder(
        RuntimeKey: $"ptr:{order.Pointer:x}",
        DeskCode: deskCode,
        FirstCapturedAt: capturedAt,
        CapturedAt: capturedAt)
    {
        OrderObject = order,
        ControllerObject = controller,
        OrderLifecycleSequence = lifecycleSequence,
    };
    NormalOrderRuntimeCapture.Captures.Add(capture);
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

static void AssertOriginalImageGeometry(GameObject overlay, RectTransform source)
{
    var image = InspectPrivateOverlay(overlay);
    var picture = (RectTransform)image.transform;
    // 独立比较共同父坐标中的完整图像范围。只检查裁剪容器高度无法识别上轮的
    // 九宫格重排缺陷：真正要保证的是原图保持全高，顶部只被遮罩截掉而没有压进正文。
    var x = overlay.transform.localPosition.x + picture.localPosition.x;
    var y = overlay.transform.localPosition.y + picture.localPosition.y;
    Assert(Math.Abs(picture.rect.width - source.rect.width) < .001f
        && Math.Abs(picture.rect.height - source.rect.height) < .001f,
        "九宫格子图必须保持原背景完整尺寸，不能跟着正文容器缩高。");
    Assert(Math.Abs(x + picture.rect.xMin - source.localPosition.x - source.rect.xMin) < .001f
        && Math.Abs(x + picture.rect.xMax - source.localPosition.x - source.rect.xMax) < .001f
        && Math.Abs(y + picture.rect.yMin - source.localPosition.y - source.rect.yMin) < .001f
        && Math.Abs(y + picture.rect.yMax - source.localPosition.y - source.rect.yMax) < .001f,
        "完整九宫格必须与原背景物理范围重合，包括非中心pivot、拉伸锚点和原图零缩放。");
    Assert(y + picture.rect.yMax > overlay.transform.localPosition.y + overlay.transform.rect.yMax,
        "原图桌号顶部应留在裁剪容器外，不能被缩放或平移到正文内部。");
    Assert(picture.localScale == Vector3.one, "私有子图不能继承原生焦点背景的零缩放。");
}

static Image InspectPrivateOverlay(GameObject overlay)
{
    Assert(overlay.name == "MystiaStewardCompanion.TargetOrderHighlight", "The overlay must have a private Mod-owned identity.");
    Assert(overlay.GetComponents<Component>().Length == 2 && overlay.GetComponent(typeof(RectMask2D)) is RectMask2D,
        "正文容器只拥有RectTransform和原生RectMask2D，不含原游戏文字或交互。");
    Assert(overlay.transform.childCount == 1, "裁剪容器只能拥有唯一的私有背景子图。");
    var picture = overlay.transform.Children.Single().gameObject;
    var components = picture.GetComponents<Component>();
    Assert(components.Length == 3, "The private overlay must contain exactly RectTransform, CanvasRenderer, and Image.");
    Assert(components.Select(component => component.Pointer).ToHashSet().Count == 3,
        "The private overlay must retain three unique native component identities when wrappers are erased.");
    var imageComponent = picture.GetComponent(typeof(Image));
    var image = RuntimeReflectionUtility.TryCastRuntimeObject(imageComponent, "UnityEngine.UI.Image") as Image;
    if (image == null) throw new InvalidOperationException("The private overlay Image exact native cast failed.");
    Assert(image.Pointer == imageComponent?.Pointer,
        "The private overlay Image must be recovered by exact native cast without changing identity.");
    Assert(!image.raycastTarget, "The private overlay must never intercept UI input.");
    Assert(image.maskable, "子图必须参与原生RectMask2D裁剪，不能重新显示被裁掉的桌号纹理。");
    Assert(image.enabled && overlay.activeInHierarchy, "The private overlay must be renderable.");
    return image;
}

static GameObject FindLatestOverlay()
{
    return GameObject.Instantiated.Last(item => item.name == "MystiaStewardCompanion.TargetOrderHighlight.Image").transform.parent!.gameObject;
}

static GameObject[] LiveOverlays() => GameObject.Instantiated
    .Where(item => item.name == "MystiaStewardCompanion.TargetOrderHighlight.Image"
        && item.m_CachedPtr != IntPtr.Zero)
    .Select(item => item.transform.parent!.gameObject)
    .ToArray();

static GameObject FindLiveOverlayFor(OrderingElement element)
{
    var expectedParent = element.borderStyleImageForCurrent.transform.parent;
    return LiveOverlays().Single(item => ReferenceEquals(item.transform.parent, expectedParent));
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
    var accepted = RuntimeOrderTraceIdService.TryNormalizeTargetTraceId(
        RuntimeUiTargetKind.Rare,
        value,
        enabled: true,
        out var normalized,
        out _);
    Assert(accepted == expected, $"Unexpected exact trace validation result for '{value}'.");
    Assert(!accepted || normalized == value, "Accepted trace ids must remain byte-for-byte unchanged.");
    Assert(accepted || normalized == "", "Rejected trace ids must not leak a normalized alias.");
}

static RuntimeUiTargetSnapshot CreateRareTarget(
    string traceId,
    long lifecycleSequence,
    int deskCode,
    string revision,
    RuntimeTargetHighlightColor? color = null,
    int colorSlot = -1) => new(
    RuntimeUiTargetKind.Rare,
    color ?? RuntimeTargetHighlightColor.DefaultRare,
    listPinningEnabled: false,
    recipeVariantEnabled: false,
    cookerHighlightEnabled: false,
    seatHighlightEnabled: false,
    orderHighlightEnabled: true,
    traceId,
    "",
    lifecycleSequence,
    deskCode,
    -1,
    Array.Empty<int>(),
    Array.Empty<int>(),
    -1,
    -1,
    revision, colorSlot);

static RuntimeUiTargetSnapshot CreateNormalTarget(
    CapturedRuntimeNormalOrder capture,
    string revision,
    RuntimeTargetHighlightColor? color = null,
    int colorSlot = -1) => new(
    RuntimeUiTargetKind.Normal,
    color ?? RuntimeTargetHighlightColor.DefaultNormal,
    listPinningEnabled: false,
    recipeVariantEnabled: false,
    cookerHighlightEnabled: false,
    seatHighlightEnabled: false,
    orderHighlightEnabled: true,
    RuntimeOrderTraceIdService.GetNormalTraceId(capture),
    capture.RuntimeKey,
    capture.OrderLifecycleSequence,
    capture.DeskCode,
    -1,
    Array.Empty<int>(),
    Array.Empty<int>(),
    -1,
    -1,
    revision, colorSlot);

/// <summary>仅替换配方展示内容，完整保留既有订单身份，模拟投影更新而非新订单。</summary>
static RuntimeUiTargetSnapshot ChangeTargetContent(
    RuntimeUiTargetSnapshot target,
    string revision,
    int recipeId,
    int beverageId,
    int[] ingredientIds,
    int[] extraIngredientIds,
    RuntimeTargetHighlightColor? color = null) => new(
        target.Kind,
        color ?? target.Color,
        target.ListPinningEnabled,
        target.RecipeVariantEnabled,
        target.CookerHighlightEnabled,
        target.SeatHighlightEnabled,
        target.OrderHighlightEnabled,
        target.OrderTraceId,
        target.OrderKey,
        target.OrderLifecycleSequence,
        target.DeskCode,
        recipeId,
        ingredientIds,
        extraIngredientIds,
        beverageId,
        target.CookerTypeId,
        revision, target.ColorSlot);

static void AssertColor(Color actual, RuntimeTargetHighlightColor expected, string message)
{
    Assert(actual.r == expected.R / 255f
           && actual.g == expected.G / 255f
           && actual.b == expected.B / 255f,
        message);
}

static void Assert(bool condition, string message)
{
    HudAssertionCounter.Count++;
    if (!condition) throw new InvalidOperationException(message);
}

internal static class HudAssertionCounter
{
    internal static int Count;
}
