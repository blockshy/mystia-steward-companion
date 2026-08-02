using DEYU.UniversalUISystem;
using MystiaStewardCompanion.Save;
using NightScene.Tiles;
using UnityEngine;

var sourceSprite = new Sprite(new Bounds(
    new Vector3(0.25f, 0.75f, 0f),
    new Vector3(2f, 3f, 0f)));
var highlightedSprite = new Sprite();
var manager = new TileManager();
manager.Desks[0] = (new InteractableTile(sourceSprite), new Vector3Int(2, 4, 0));
manager.Desks[1] = (new InteractableTile(sourceSprite), new Vector3Int(6, 8, 0));
manager.interactablesHighlightedVisual[sourceSprite] = highlightedSprite;
manager.interactable.Transforms[new Vector3Int(2, 4, 0)] = new Matrix4x4(1f, 2f, 0.25f);
manager.interactable.Transforms[new Vector3Int(6, 8, 0)] = new Matrix4x4(0.5f, 1f, 0.5f);

var selectionRoot = manager.onSelection.gameObject;
selectionRoot.name = "Game.SharedSelection";
selectionRoot.transform.position = new Vector3(20f, 30f, 4f);

var selectionSprite = new Sprite(new Bounds(
    new Vector3(0.5f, -0.25f, 0.1f),
    new Vector3(1f, 1f, 0f)));
var primary = CreateSelectionRenderer(
    manager.onSelection,
    selectionRoot.transform,
    selectionSprite,
    localPosition: new Vector3(2f, -1f, 0.5f),
    pointScale: new Vector3(2f, 3f, 1f),
    quarterTurns: 1);
var secondary = CreateSelectionRenderer(
    manager.onSelection,
    selectionRoot.transform,
    selectionSprite,
    localPosition: new Vector3(-1f, 1f, 0f),
    pointScale: new Vector3(1f, 1f, 1f),
    quarterTurns: 0);

manager.stencilPainterParent.CloneFactory = parent => CreateStencilClone(parent, workerCount: 1);
DEYU.Singletons.MonoSingleton<TileManager>.Instance = manager;
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(
    IsActive: true,
    Generation: 7,
    ThreadId: Environment.CurrentManagedThreadId);
RuntimeSeatHighlightService.Resume("test business active");

var sharedRootPosition = selectionRoot.transform.position;
var sharedPrimarySprite = primary.sprite;
var sharedSecondarySprite = secondary.sprite;
StencilPainterController.EnableFirstWorkerOnShow = false;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 0);
RuntimeSeatHighlightService.Tick();

Assert(GameObject.Instantiated.Count == 2, "A target must create one private selection clone and one private stencil clone.");
Assert(StencilPainterController.ShowCount == 1, "The first target must call Show exactly once.");
Assert(StencilPainterController.LastPosition == new Vector3(3.25f, 6f, 0.75f), "The stencil anchor must come from the moved private selection hierarchy.");
Assert(selectionRoot.transform.position == sharedRootPosition, "Creating a target must not move the game's shared selection root.");
Assert(ReferenceEquals(primary.sprite, sharedPrimarySprite), "Creating a target must not replace the shared primary sprite.");
Assert(ReferenceEquals(secondary.sprite, sharedSecondarySprite), "Creating a target must not replace another shared selection sprite.");
Assert(primary.enabled && secondary.enabled, "Creating a target must not alter shared renderer visibility.");

var firstSelectionClone = FindLatestClone("MystiaStewardCompanion.TargetDeskSelection");
var firstStencilClone = FindLatestClone("MystiaStewardCompanion.TargetDeskHighlight");
var firstCloneCluster = (UIElementCluster?)firstSelectionClone.GetComponent(typeof(UIElementCluster));
var firstCloneRenderers = firstCloneCluster!.GetObjects<SpriteRenderer>();
Assert(firstCloneRenderers.Length == 2, "The full cloned selection renderer array must be retained.");
Assert(!ReferenceEquals(firstCloneRenderers[0], primary) && !ReferenceEquals(firstCloneRenderers[1], secondary), "Selection renderers must be deep-cloned instead of shared.");
Assert(ReferenceEquals(firstCloneRenderers[0].sprite, sourceSprite) && ReferenceEquals(firstCloneRenderers[1].sprite, sourceSprite), "Every private selection renderer must bind the desk sprite.");
Assert(firstCloneRenderers[0].transform.IsChildOf(firstSelectionClone.transform), "The private primary renderer must remain in the cloned hierarchy.");
Assert(firstSelectionClone.transform.parent == null, "A root-level game selection must remain root-level when cloned.");
Assert(RuntimeSeatHighlightService.Status.Contains("pending", StringComparison.Ordinal), "Stencil visibility must remain pending while its coroutine has not exposed a worker.");

Time.realtimeSinceStartup = 0.1f;
RuntimeSeatHighlightService.Tick();
Assert(StencilPainterController.ShowCount == 1, "A pending target must not be recreated during its grace period.");
StencilPainterController.LastController!.worker[0].enabled = true;
Time.realtimeSinceStartup = 0.2f;
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("active", StringComparison.Ordinal), "Both private layers becoming renderable must activate the target.");
Assert(RuntimeSeatHighlightService.Status.Contains("selection:2/2/stencil:1/1", StringComparison.Ordinal), "Diagnostics must report both private visual layers.");
Assert(RuntimeSeatHighlightService.Status.Contains("render=selection[", StringComparison.Ordinal), "Diagnostics must describe the active selection and stencil renderers.");

StencilPainterController.EnableFirstWorkerOnShow = true;
Time.realtimeSinceStartup = 0.5f;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 1);
RuntimeSeatHighlightService.Tick();
Assert(GameObject.Destroyed.Count == 2, "Switching desks must destroy both private roots together.");
Assert(GameObject.Instantiated.Count == 4 && StencilPainterController.ShowCount == 2, "Switching desks must create exactly one replacement pair.");
Assert(firstSelectionClone.m_CachedPtr == IntPtr.Zero && firstStencilClone.m_CachedPtr == IntPtr.Zero, "The previous pair must not survive a desk switch.");

Time.realtimeSinceStartup = 0.6f;
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("active", StringComparison.Ordinal), "The replacement pair must become active.");
var staleController = StencilPainterController.LastController!;
var destroyedBeforeStaleController = GameObject.Destroyed.Count;
staleController.InvalidateNativePointer();
RuntimeSeatHighlightService.Tick();
Assert(GameObject.Destroyed.Count == destroyedBeforeStaleController + 2, "A stale stencil controller must retire both live private roots.");
Assert(GameObject.Instantiated.Count == 6 && StencilPainterController.ShowCount == 3, "A stale stencil controller must recreate the complete pair.");

Time.realtimeSinceStartup = 0.7f;
RuntimeSeatHighlightService.Tick();
var staleSelectionClone = FindLatestClone("MystiaStewardCompanion.TargetDeskSelection");
var liveStencilBeforeStaleSelection = FindLatestClone("MystiaStewardCompanion.TargetDeskHighlight");
var destroyedBeforeStaleSelection = GameObject.Destroyed.Count;
staleSelectionClone.InvalidateNativePointer();
RuntimeSeatHighlightService.Tick();
Assert(GameObject.Destroyed.Count == destroyedBeforeStaleSelection + 1, "A stale selection root must be abandoned while its still-live stencil peer is destroyed.");
Assert(liveStencilBeforeStaleSelection.m_CachedPtr == IntPtr.Zero, "A stale selection root must not leave the stencil peer active.");
Assert(GameObject.Instantiated.Count == 8 && StencilPainterController.ShowCount == 4, "A stale selection root must reacquire both visual layers.");

Time.realtimeSinceStartup = 0.8f;
RuntimeSeatHighlightService.Tick();
var throwingController = StencilPainterController.LastController!;
throwingController.worker.ThrowOnCountRead = true;
var destroyedBeforeInspectionFailure = GameObject.Destroyed.Count;
var showsBeforeInspectionFailure = StencilPainterController.ShowCount;
Time.realtimeSinceStartup = 1.1f;
RuntimeSeatHighlightService.Tick();
Assert(GameObject.Destroyed.Count == destroyedBeforeInspectionFailure + 2, "A worker inspection exception must destroy both private roots.");
Assert(StencilPainterController.ShowCount == showsBeforeInspectionFailure, "A failing health-check tick must not immediately replay Show.");
Assert(RuntimeSeatHighlightService.Status.Contains("unavailable", StringComparison.Ordinal), "A health-check exception must fail closed.");

throwingController.worker.ThrowOnCountRead = false;
Time.realtimeSinceStartup = 2f;
RuntimeSeatHighlightService.Tick();
Assert(StencilPainterController.ShowCount == showsBeforeInspectionFailure, "The bounded retry interval must be respected.");
Time.realtimeSinceStartup = 2.5f;
RuntimeSeatHighlightService.Tick();
Assert(StencilPainterController.ShowCount == showsBeforeInspectionFailure + 1, "The unchanged target must recover with a fresh pair after retry.");

var destroyedBeforeSuspend = GameObject.Destroyed.Count;
RuntimeSeatHighlightService.Suspend("closing");
Assert(GameObject.Destroyed.Count == destroyedBeforeSuspend + 2, "Suspend on the Unity thread must destroy the selection and stencil roots together.");
RuntimeSeatHighlightService.Resume("next scene");
RuntimeSeatHighlightService.Tick();
Assert(StencilPainterController.ShowCount == showsBeforeInspectionFailure + 2, "Resume must reacquire a complete pair.");
var destroyedBeforeAbandon = GameObject.Destroyed.Count;
RuntimeSeatHighlightService.Abandon("scene objects destroyed");
Assert(GameObject.Destroyed.Count == destroyedBeforeAbandon, "Abandon must drop stale wrappers without Unity destruction calls.");

// Session and thread gates must reject the target before touching scene objects.
RuntimeSeatHighlightService.Resume("fail-closed matrix");
var instantiatedBeforeGateChecks = GameObject.Instantiated.Count;
RuntimeSeatHighlightService.UpdateTarget(8, enabled: true, deskCode: 0);
RuntimeSeatHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeGateChecks, "A target from another business generation must not clone scene objects.");
Assert(RuntimeSeatHighlightService.Status.Contains("different night-business session", StringComparison.Ordinal), "A cross-generation target must expose its gate.");
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, 7, Environment.CurrentManagedThreadId + 1);
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 1);
RuntimeSeatHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeGateChecks, "A non-main-thread tick must not clone scene objects.");
Assert(RuntimeSeatHighlightService.Status.Contains("Unity main thread", StringComparison.Ordinal), "The main-thread gate must be visible.");
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, 7, Environment.CurrentManagedThreadId);

// Missing native data must fail before cloning.
manager.interactablesHighlightedVisual.Clear();
Time.realtimeSinceStartup = 4f;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 0);
RuntimeSeatHighlightService.Tick();
Assert(GameObject.Instantiated.Count == instantiatedBeforeGateChecks, "A missing highlighted sprite must fail before either clone is created.");
Assert(RuntimeSeatHighlightService.Status.Contains("no highlighted visual", StringComparison.Ordinal), "A missing highlighted sprite must be diagnosed precisely.");
manager.interactablesHighlightedVisual[sourceSprite] = highlightedSprite;

// Once selection cloning succeeds, every stencil failure must roll both private roots back.
manager.stencilPainterParent.CloneFactory = parent => CreateStencilClone(parent, workerCount: 0);
Time.realtimeSinceStartup = 6f;
var destroyedBeforeZeroWorkers = GameObject.Destroyed.Count;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 1);
RuntimeSeatHighlightService.Tick();
AssertDestroyedPairSince(destroyedBeforeZeroWorkers, "A stencil with zero workers");
Assert(RuntimeSeatHighlightService.Status.Contains("no SpriteRenderer workers", StringComparison.Ordinal), "Zero stencil workers must fail closed.");

manager.stencilPainterParent.CloneFactory = parent => CreateStencilClone(parent, workerCount: 1, active: false);
Time.realtimeSinceStartup = 8f;
var destroyedBeforeInactiveStencil = GameObject.Destroyed.Count;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 0);
RuntimeSeatHighlightService.Tick();
AssertDestroyedPairSince(destroyedBeforeInactiveStencil, "An inactive stencil");
Assert(RuntimeSeatHighlightService.Status.Contains("inactive in the scene hierarchy", StringComparison.Ordinal), "An inactive stencil must be diagnosed precisely.");

manager.stencilPainterParent.CloneFactory = parent => CreateStencilClone(parent, workerCount: 1, renderable: false);
Time.realtimeSinceStartup = 10f;
var destroyedBeforeVisibilityTimeout = GameObject.Destroyed.Count;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 1);
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("pending", StringComparison.Ordinal), "A non-renderable stencil must first enter its grace period.");
Time.realtimeSinceStartup = 11.1f;
RuntimeSeatHighlightService.Tick();
AssertDestroyedPairSince(destroyedBeforeVisibilityTimeout, "A stencil visibility timeout");
Assert(RuntimeSeatHighlightService.Status.Contains("unavailable", StringComparison.Ordinal), "Visibility timeout must fail closed.");

manager.stencilPainterParent.CloneFactory = parent => CreateStencilClone(parent, workerCount: 1);
StencilPainterController.ThrowOnShow = true;
Time.realtimeSinceStartup = 13f;
var destroyedBeforeShowFailure = GameObject.Destroyed.Count;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 0);
RuntimeSeatHighlightService.Tick();
AssertDestroyedPairSince(destroyedBeforeShowFailure, "A native Show exception");
Assert(RuntimeSeatHighlightService.Status.Contains("native Show failed", StringComparison.Ordinal), "A native Show exception must be exposed without escaping Tick.");
StencilPainterController.ThrowOnShow = false;

// Exact GetObjects<T>() shape and selection ownership checks.
ConfigureSelectionClone(selectionRoot, (_, cluster) => cluster.GetObjectsOverride = Array.Empty<SpriteRenderer>());
Time.realtimeSinceStartup = 15f;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 1);
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("outside 1..64", StringComparison.Ordinal), "An empty selection renderer array must be rejected.");

ConfigureSelectionClone(selectionRoot, (_, cluster) =>
{
    var renderer = cluster.GetObject<SpriteRenderer>(0)!;
    cluster.GetObjectsOverride = Enumerable.Repeat(renderer, 65).ToArray();
});
Time.realtimeSinceStartup = 17f;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 0);
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("outside 1..64", StringComparison.Ordinal), "An oversized selection renderer array must be rejected.");

ConfigureSelectionClone(selectionRoot, (_, cluster) =>
{
    var renderer = cluster.GetObject<SpriteRenderer>(0)!;
    cluster.GetObjectsOverride = new[] { renderer, renderer };
});
Time.realtimeSinceStartup = 19f;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 1);
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("unavailable or duplicated", StringComparison.Ordinal), "Duplicate renderer identities must be rejected.");

ConfigureSelectionClone(selectionRoot, (_, cluster) =>
{
    cluster.GetObject<SpriteRenderer>(0)!.transform.parent = new GameObject().transform;
});
Time.realtimeSinceStartup = 21f;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 0);
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("outside the cloned hierarchy", StringComparison.Ordinal), "A renderer outside the cloned hierarchy must be rejected.");

ConfigureSelectionClone(selectionRoot, (_, cluster) => cluster.PrimaryOverride = new SpriteRenderer { enabled = true, sprite = sourceSprite });
Time.realtimeSinceStartup = 23f;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 1);
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("outside its renderer array", StringComparison.Ordinal), "A primary renderer absent from GetObjects must be rejected.");

// Active selection corruption must retire the whole pair after the bounded grace period.
ConfigureSelectionClone(selectionRoot, null);
Time.realtimeSinceStartup = 25f;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 0);
RuntimeSeatHighlightService.Tick();
Time.realtimeSinceStartup = 25.1f;
RuntimeSeatHighlightService.Tick();
var spriteDriftCluster = (UIElementCluster)FindLatestClone("MystiaStewardCompanion.TargetDeskSelection").GetComponent(typeof(UIElementCluster))!;
spriteDriftCluster.GetObject<SpriteRenderer>(0)!.sprite = selectionSprite;
var destroyedBeforeSpriteDrift = GameObject.Destroyed.Count;
Time.realtimeSinceStartup = 25.4f;
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("pending", StringComparison.Ordinal), "Selection sprite drift must receive only the bounded visibility grace period.");
Time.realtimeSinceStartup = 26.5f;
RuntimeSeatHighlightService.Tick();
AssertDestroyedPairSince(destroyedBeforeSpriteDrift, "Selection sprite drift timeout");

Time.realtimeSinceStartup = 28f;
RuntimeSeatHighlightService.UpdateTarget(7, enabled: true, deskCode: 1);
RuntimeSeatHighlightService.Tick();
Time.realtimeSinceStartup = 28.1f;
RuntimeSeatHighlightService.Tick();
var anchorDriftCluster = (UIElementCluster)FindLatestClone("MystiaStewardCompanion.TargetDeskSelection").GetComponent(typeof(UIElementCluster))!;
anchorDriftCluster.GetObject<SpriteRenderer>(0)!.transform.localPosition = new Vector3(99f, 99f, 0f);
var destroyedBeforeAnchorDrift = GameObject.Destroyed.Count;
Time.realtimeSinceStartup = 28.4f;
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("moved away from the painter anchor", StringComparison.Ordinal), "Selection anchor drift must be diagnosed precisely.");
Time.realtimeSinceStartup = 29.5f;
RuntimeSeatHighlightService.Tick();
AssertDestroyedPairSince(destroyedBeforeAnchorDrift, "Selection anchor drift timeout");

selectionRoot.CloneFactory = null;
RuntimeSeatHighlightService.Suspend("fail-closed matrix complete");

var serviceSource = File.ReadAllText("mods/bepinex/src/Save/RuntimeSeatHighlightService.cs");
var controllerSource = File.ReadAllText("mods/bepinex/src/Ui/StewardOverlayController.cs");
Assert(controllerSource.Contains("RuntimeSeatHighlightService.Dispose(\"controller disposed\")", StringComparison.Ordinal), "Controller disposal must release both Mod-owned roots.");
foreach (var required in new[]
         {
             "FindDeclaredGenericArrayInstanceMethod",
             "Il2CppArrayBase<SpriteRenderer>",
             "selectionClone = UnityEngine.Object.Instantiate(selectionSourceObject)",
             "clone = UnityEngine.Object.Instantiate(template, parent)",
             "renderer.sprite = sourceSprite",
             "_activeSelectionClone",
             "SafeDestroyClone(selectionClone)",
             "pending: waiting for the native stencil show coroutine",
         })
{
    Assert(serviceSource.Contains(required, StringComparison.Ordinal), $"Seat highlighting must retain its exact dual-clone contract: {required}");
}

foreach (var forbidden in new[]
         {
             "UpdateCurser(",
             "ShowPainter(",
             "HidePainter(",
             "GetObjectAll",
             "activePainter",
             "GuestTables",
             "FindObjectOfType",
             "FindObjectsOfType",
             "GetComponentInChildren",
         })
{
    Assert(!serviceSource.Contains(forbidden, StringComparison.Ordinal), $"Seat highlighting must not mutate shared visuals or scan the scene: {forbidden}");
}

Console.WriteLine("Runtime seat highlight smoke passed.");

static SpriteRenderer CreateSelectionRenderer(
    UIElementCluster cluster,
    Transform parent,
    Sprite sprite,
    Vector3 localPosition,
    Vector3 pointScale,
    int quarterTurns)
{
    var renderer = new SpriteRenderer
    {
        enabled = true,
        sprite = sprite,
        AutoBounds = true,
        StaticBounds = new Bounds(default, new Vector3(2f, 3f, 0f)),
    };
    renderer.transform.parent = parent;
    renderer.transform.localPosition = localPosition;
    renderer.transform.PointScale = pointScale;
    renderer.transform.QuarterTurnsZ = quarterTurns;
    cluster.Add(renderer);
    return renderer;
}

static GameObject CreateStencilClone(Transform parent, int workerCount, bool active = true, bool renderable = true)
{
    var clone = new GameObject { name = "Test.StencilClone" };
    clone.transform.parent = parent;
    clone.SetActive(active);
    var controller = new StencilPainterController();
    clone.AddComponent(controller);
    for (var index = 0; index < workerCount; index += 1)
    {
        controller.worker.Add(new SpriteRenderer
        {
            bounds = new Bounds(default, renderable ? new Vector3(1f, 1f, 0f) : default),
        });
    }
    return clone;
}

static void ConfigureSelectionClone(GameObject source, Action<GameObject, UIElementCluster>? configure)
{
    source.CloneFactory = parent =>
    {
        var clone = source.DeepClone(parent);
        var cluster = (UIElementCluster)clone.GetComponent(typeof(UIElementCluster))!;
        configure?.Invoke(clone, cluster);
        return clone;
    };
}

static void AssertDestroyedPairSince(int startIndex, string context)
{
    var destroyed = GameObject.Destroyed.Skip(startIndex).ToArray();
    Assert(destroyed.Count(value => value.name == "MystiaStewardCompanion.TargetDeskSelection") == 1, $"{context} must destroy its private selection clone.");
    Assert(destroyed.Count(value => value.name == "MystiaStewardCompanion.TargetDeskHighlight") == 1, $"{context} must destroy its private stencil clone.");
}

static GameObject FindLatestClone(string name) =>
    GameObject.Instantiated.Last(clone => clone.name == name && clone.m_CachedPtr != IntPtr.Zero);

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
