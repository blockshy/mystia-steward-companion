using BepInEx.Logging;
using DEYU.UniversalUISystem;
using MystiaStewardCompanion.Save;
using NightScene.Tiles;
using UnityEngine;

const string outlineShaderName = "THIZKY/Effects/OutlineBlinkOnly";
const string regionalShaderName = "THIZKY/Effects/RegionalHSVFillter";
const string standardShaderName = "Sprites/Default";
const string cloneName = "MystiaStewardCompanion.TargetDeskFill";
const string fillObjectName = "MystiaStewardCompanion.TargetDeskStandardFill";

// A player build does not need Shader.Find for the new path: the blank SpriteRenderer supplies this material.
Shader.ClearRegistered();
var standardShader = new Shader { name = standardShaderName };
var defaultMaterial = new Material(standardShader)
{
    name = "Sprites-Default-Material",
    renderQueue = 3000,
};
defaultMaterial.SetColor("_Tint", new Color(0.12f, 0.34f, 0.56f, 0.78f));
SpriteRenderer.DefaultMaterial = defaultMaterial;
SpriteRenderer.ConstructedRendererCreatesPropertyBlockOnVisualWrite = true;

var outlineShader = new Shader { name = outlineShaderName };
var regionalShader = new Shader { name = regionalShaderName };
var outlineMaterial = new Material(outlineShader) { name = "SelectionOutlineMaterial", renderQueue = 3010 };
var regionalMaterial = new Material(regionalShader) { name = "SelectionRegionalMaterial", renderQueue = 3020 };
var sourceTexture = new Texture2D(256, 128) { name = "DeskSourceTexture" };
var sourceSprite = new Sprite(
    sourceTexture,
    new Rect(13f, 7f, 120f, 80f),
    new Vector2(37f, 58f),
    40f,
    new[]
    {
        new Vector2(-0.8f, -1.2f),
        new Vector2(-0.75f, 0.4f),
        new Vector2(0.4f, 0.55f),
        new Vector2(1.9f, 0.3f),
        new Vector2(2f, -1.1f),
    },
    new ushort[] { 0, 1, 2, 0, 2, 4, 2, 3, 4 })
{
    name = "DeskSourceSprite",
};

// Unity 2021.3 treats a Sprite smaller than 32x32 as FullRect even when Tight is
// requested, then keeps that geometry unchanged when OverrideGeometry is called.
// Lock the exact 2x2 shared-white failure before exercising the owned 64x64 path.
var smallTightProbe = Sprite.Create(
    Texture2D.whiteTexture,
    new Rect(0f, 0f, Texture2D.whiteTexture.width, Texture2D.whiteTexture.height),
    new Vector2(0.5f, 0.5f),
    1f,
    0u,
    SpriteMeshType.Tight)!;
var rejectedSmallTightOverridesBefore = Sprite.RejectedFullRectOverrideCount;
smallTightProbe.OverrideGeometry(sourceSprite.vertices, sourceSprite.triangles);
Assert(smallTightProbe.createdMeshType == SpriteMeshType.FullRect
       && Sprite.RejectedFullRectOverrideCount == rejectedSmallTightOverridesBefore + 1
       && smallTightProbe.vertices.Length == 4
       && smallTightProbe.triangles.Length == 6,
    "The smoke stub must reproduce a 2x2 Tight request becoming a non-overridable FullRect Sprite on Unity 2021.3.");
UnityEngine.Object.Destroy(smallTightProbe);

var fullRectProbe = Sprite.Create(
    Texture2D.whiteTexture,
    new Rect(0f, 0f, Texture2D.whiteTexture.width, Texture2D.whiteTexture.height),
    new Vector2(0.5f, 0.5f),
    1f,
    0u,
    SpriteMeshType.FullRect)!;
var rejectedFullRectOverridesBefore = Sprite.RejectedFullRectOverrideCount;
fullRectProbe.OverrideGeometry(sourceSprite.vertices, sourceSprite.triangles);
Assert(Sprite.RejectedFullRectOverrideCount == rejectedFullRectOverridesBefore + 1
       && fullRectProbe.vertices.Length == 4
       && fullRectProbe.triangles.Length == 6,
    "The smoke stub must reproduce Unity 2021.3 rejecting FullRect OverrideGeometry without mutating the Sprite.");
UnityEngine.Object.Destroy(fullRectProbe);
Sprite.Created.Clear();
Sprite.Destroyed.Clear();
Texture2D.Created.Clear();
Texture2D.Destroyed.Clear();

var manager = new TileManager();
manager.Desks[0] = (new InteractableTile(sourceSprite), new Vector3Int(2, 4, 0));
manager.Desks[1] = (new InteractableTile(sourceSprite), new Vector3Int(6, 8, 0));
manager.interactable.Transforms[new Vector3Int(2, 4, 0)] = new Matrix4x4(1f, 2f, 0.25f);
manager.interactable.Transforms[new Vector3Int(6, 8, 0)] = new Matrix4x4(0.5f, 1f, 0.5f);

var selectionRoot = manager.onSelection.gameObject;
selectionRoot.name = "Game.SharedSelection";
selectionRoot.transform.position = new Vector3(20f, 30f, 4f);
var outline = CreateSelectionRenderer(
    manager.onSelection,
    selectionRoot.transform,
    sourceSprite,
    outlineMaterial,
    localPosition: new Vector3(2f, -1f, 0.5f),
    localScale: new Vector3(2f, 3f, 1f),
    localRotation: Quaternion.Euler(0f, 0f, 90f),
    layer: 9,
    sortingLayerId: 14,
    sortingOrder: 23,
    flipX: false,
    flipY: true,
    color: new Color(0f, 0f, 0f, 0.46f));
var regional = CreateSelectionRenderer(
    manager.onSelection,
    selectionRoot.transform,
    sourceSprite,
    regionalMaterial,
    localPosition: new Vector3(-1f, 1f, 0.2f),
    localScale: new Vector3(1.25f, 0.75f, 1f),
    localRotation: Quaternion.Euler(0f, 0f, 180f),
    layer: 10,
    sortingLayerId: 15,
    sortingOrder: 24,
    flipX: true,
    flipY: false,
    color: new Color(0.35f, 0.16f, 0.52f, 0.94f));
var sharedRegionalPropertyBlock = new MaterialPropertyBlock();
sharedRegionalPropertyBlock.SetColor("_Color", new Color(0f, 0f, 0f, 1f));
regional.SetPropertyBlock(sharedRegionalPropertyBlock);

DEYU.Singletons.MonoSingleton<TileManager>.Instance = manager;
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(
    IsActive: true,
    Generation: 7,
    ThreadId: Environment.CurrentManagedThreadId);
var log = new ManualLogSource();
RuntimeSeatHighlightService.Attach(log);
RuntimeSeatHighlightService.Resume("test business active");

long seatTargetGeneration = 0;
RuntimeUiTargetSetSnapshot CreateSeatTargetSet(
    long sessionGeneration,
    params RuntimeUiTargetSnapshot[] targets) => new(
        ++seatTargetGeneration,
        sessionGeneration,
        targets);

static RuntimeUiTargetSnapshot CreateSeatTarget(
    RuntimeUiTargetKind kind,
    int deskCode,
    RuntimeTargetHighlightColor? color = null,
    string? revision = null) => new(
    kind,
    color ?? (kind == RuntimeUiTargetKind.Rare
        ? RuntimeTargetHighlightColor.DefaultRare
        : RuntimeTargetHighlightColor.DefaultNormal),
    listPinningEnabled: false,
    recipeVariantEnabled: false,
    cookerHighlightEnabled: false,
    seatHighlightEnabled: true,
    orderHighlightEnabled: false,
    $"{(kind == RuntimeUiTargetKind.Rare ? 'R' : 'N')}-{deskCode + 1}",
    kind == RuntimeUiTargetKind.Rare ? "" : $"ptr:{deskCode + 1:x}",
    deskCode + 1L,
    deskCode,
    -1,
    Array.Empty<int>(),
    Array.Empty<int>(),
    -1,
    -1,
    revision ?? $"seat:{kind}:{deskCode}");

static Color BuildRareSeatPulse(float time) =>
    RuntimeTargetHighlightStyle.BuildSeatFillPulseColor(
        RuntimeUiTargetKinds.Rare,
        new RuntimeTargetHighlightPalette(
            RuntimeTargetHighlightColor.DefaultRare,
            RuntimeTargetHighlightColor.DefaultNormal),
        time);

var sharedRootPosition = selectionRoot.transform.position;
var sharedOutlineEnabled = outline.enabled;
var sharedRegionalEnabled = regional.enabled;
var sharedOutlineColor = outline.color;
var sharedRegionalColor = regional.color;
var defaultName = defaultMaterial.name;
var defaultQueue = defaultMaterial.renderQueue;
var defaultTint = defaultMaterial.GetColor("_Tint");

Time.realtimeSinceStartup = 0f;
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    7,
    CreateSeatTarget(RuntimeUiTargetKind.Rare, 0)));
RuntimeSeatHighlightService.Tick();

Assert(GameObject.Instantiated.Count == 1, "A target must create exactly one private selection clone.");
Assert(GameObject.Created.Count == 1, "A target must create exactly one independent SpriteRenderer object.");
Assert(GameObject.DegradedSpriteRendererTypedQueries == 1,
    "The successful path must exact-cast the BepInEx-style base Component wrapper returned by the typed native query.");
Assert(GameObject.Destroyed.Count == 0
       && Material.Destroyed.Count == 0
       && Sprite.Destroyed.Count == 0
       && Texture2D.Destroyed.Count == 0,
    "A successful target must retain all owned resources.");
Assert(Sprite.Created.Count == 1, "A target must create exactly one Mod-owned white sprite.");
Assert(Texture2D.Created.Count == 1, "A target must create exactly one Mod-owned white texture.");
Assert(Sprite.Created[0].createdMeshType == SpriteMeshType.Tight,
    "The production seat fill must create the only owned white Sprite as Tight before overriding geometry.");
Assert(log.InfoMessages.Count == 1, "A successful target must emit exactly one bounded binding log.");
var firstBinding = log.InfoMessages[0];
Assert(firstBinding.Contains("Runtime seat highlight fill bound: businessGeneration=7; targetGeneration=1; desk=0; claims=Rare", StringComparison.Ordinal), "Binding evidence lost target ownership.");
Assert(firstBinding.Contains("createTexture=64x64/RGBA32/mips:1/readable:True", StringComparison.Ordinal)
       && firstBinding.Contains("createMesh=Tight", StringComparison.Ordinal)
       && firstBinding.Contains("geometry=5v/9i", StringComparison.Ordinal)
       && firstBinding.Contains("bounds=", StringComparison.Ordinal),
    "Binding evidence must retain only already-validated texture, mesh, geometry, and bounds scalars.");

var firstClone = FindLatestClone();
var firstCluster = (UIElementCluster)firstClone.GetComponent(typeof(UIElementCluster))!;
var firstTemplates = firstCluster.GetObjects<SpriteRenderer>();
Assert(firstTemplates.Length == 2, "The cloned native selection must retain its exact two-renderer shape.");
var firstOutline = SingleRenderer(firstTemplates, outlineShaderName);
var firstRegional = SingleRenderer(firstTemplates, regionalShaderName);
Assert(ReferenceEquals(firstTemplates[0], firstRegional)
       && ReferenceEquals(firstTemplates[1], firstOutline),
    "GetObjects<SpriteRenderer>() must model the native Stack.ToArray reverse of serialized Outline/Regional order.");
var firstFillObject = FindLatestFillObject();
var firstFill = GetFillRenderer(firstFillObject);
var firstOwnedSprite = firstFill.sprite!;
var firstOwnedTexture = firstOwnedSprite.texture;
var firstOwnedMaterial = firstFill.sharedMaterial!;
Assert(firstClone.name == cloneName && firstClone.transform.parent == null, "The private native clone must remain a named scene root.");
Assert(firstClone.transform.position == new Vector3(3.5f, 6.5f, 0.25f), "The private root must use desk center plus Tilemap transform.");
Assert(!firstOutline.enabled && !firstRegional.enabled, "Both cloned custom-shader layers must remain disabled.");
Assert(firstFill.enabled && firstFillObject.activeInHierarchy, "Only the independent standard SpriteRenderer may draw.");
Assert(ReferenceEquals(firstFill.transform.parent, firstRegional.transform.parent), "The standard fill must copy Regional's exact parent.");
Assert(firstFill.transform.localPosition == firstRegional.transform.localPosition
       && firstFill.transform.localRotation == firstRegional.transform.localRotation
       && firstFill.transform.localScale == firstRegional.transform.localScale,
    "The standard fill must copy Regional's complete local transform.");
Assert(firstFillObject.layer == firstRegional.gameObject.layer
       && firstFill.sortingLayerID == firstRegional.sortingLayerID
       && firstFill.sortingOrder == firstRegional.sortingOrder
       && firstFill.drawMode == firstRegional.drawMode
       && firstFill.flipX == firstRegional.flipX
       && firstFill.flipY == firstRegional.flipY,
    "The standard fill must copy Regional's exact sprite render settings.");
Assert(!ReferenceEquals(firstOwnedSprite, sourceSprite)
       && ReferenceEquals(firstOwnedTexture, Texture2D.Created.Single())
       && !ReferenceEquals(firstOwnedTexture, Texture2D.whiteTexture)
       && !ReferenceEquals(firstOwnedTexture, sourceTexture)
       && firstOwnedTexture.name == "MystiaStewardCompanion.TargetDeskFillTexture"
       && firstOwnedTexture.width == 64
       && firstOwnedTexture.height == 64
       && firstOwnedTexture.format == TextureFormat.RGBA32
       && firstOwnedTexture.mipmapCount == 1
       && firstOwnedTexture.isReadable
       && !firstOwnedTexture.MipChain
       && !firstOwnedTexture.Linear
       && firstOwnedTexture.ApplyCount == 1
       && firstOwnedTexture.Pixels.Count == 64 * 64
       && firstOwnedTexture.Pixels.All(pixel =>
           pixel == new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue))
       && firstOwnedSprite.name == "MystiaStewardCompanion.TargetDeskFillSprite",
    "The visible fill must bind one distinct Mod-owned sprite backed by one exact 64x64 opaque-white owned texture.");
AssertSpriteGeometry(firstOwnedSprite, sourceSprite,
    "The white sprite must reproduce a non-square, non-centered, non-unit-PPU source mesh in local coordinates.");
Assert(firstFill.HasPropertyBlock(),
    "The regression fixture must model Unity creating a property block after SpriteRenderer sprite/color writes without blocking active publication.");
Assert(!ReferenceEquals(firstOwnedMaterial, defaultMaterial)
       && !ReferenceEquals(firstOwnedMaterial, regionalMaterial)
       && ReferenceEquals(firstOwnedMaterial.shader, standardShader),
    "renderer.material must create a distinct owned instance of Unity's default sprite material.");
Assert(firstOwnedMaterial.name == "MystiaStewardCompanion.TargetDeskFillMaterial", "The instance must have an explicit Mod-owned name.");
Assert(defaultMaterial.name == defaultName
       && defaultMaterial.renderQueue == defaultQueue
       && defaultMaterial.GetColor("_Tint") == defaultTint
       && ReferenceEquals(defaultMaterial.shader, standardShader)
       && !Material.Destroyed.Contains(defaultMaterial),
    "Creating the fill must not mutate or destroy Unity's default shared material.");
AssertColor(firstFill.color, BuildRareSeatPulse(0f), "The standard fill must receive canonical yellow immediately.");
Assert(RuntimeSeatHighlightService.Status.StartsWith("active", StringComparison.Ordinal)
       && RuntimeSeatHighlightService.Status.Contains("active=session:7/desk:0/claims:Rare/root:3.5,6.5,0.25", StringComparison.Ordinal),
    "Status must expose the atomic active target without re-reading diagnostic Unity properties.");

Assert(selectionRoot.transform.position == sharedRootPosition, "Creating a target must not move the game's shared selection root.");
Assert(outline.enabled == sharedOutlineEnabled && regional.enabled == sharedRegionalEnabled, "Creating a target must not alter shared renderer visibility.");
Assert(regional.HasPropertyBlock(), "Creating a target must not clear the shared Regional property block.");
AssertColor(outline.color, sharedOutlineColor, "Creating a target must not tint the shared outline.");
AssertColor(regional.color, sharedRegionalColor, "Creating a target must not tint the shared Regional fill.");
Assert(ReferenceEquals(outline.sharedMaterial, outlineMaterial) && ReferenceEquals(regional.sharedMaterial, regionalMaterial), "Creating a target must not replace shared materials.");

var seatPulseCrest = BuildRareSeatPulse(MathF.PI / (2f * 5.5f));
var seatPulseTrough = BuildRareSeatPulse(3f * MathF.PI / (2f * 5.5f));
Assert(MathF.Abs(seatPulseCrest.a - 0.70f) <= 0.0001f && MathF.Abs(seatPulseTrough.a - 0.45f) <= 0.0001f,
    "The independent fill must retain the 0.45..0.70 alpha yellow pulse.");
var readsBeforePulse = firstFill.BoundsReadCount;
Time.realtimeSinceStartup = 0.1f;
RuntimeSeatHighlightService.Tick();
AssertColor(firstFill.color, BuildRareSeatPulse(0.1f), "Pulse color must update each Tick.");
Assert(firstFill.BoundsReadCount == readsBeforePulse + 1, "The first due health check must inspect bounds once.");
var readsAfterHealth = firstFill.BoundsReadCount;
Time.realtimeSinceStartup = 0.2f;
RuntimeSeatHighlightService.Tick();
Assert(firstFill.BoundsReadCount == readsAfterHealth, "Structural checks must stay low frequency while pulse updates every frame.");

// Target switches retire the clone, fill object, owned texture/Sprite, and owned material.
Time.realtimeSinceStartup = 0.5f;
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    7,
    CreateSeatTarget(RuntimeUiTargetKind.Rare, 1)));
RuntimeSeatHighlightService.Tick();
Assert(GameObject.Destroyed.Contains(firstClone) && GameObject.Destroyed.Contains(firstFillObject), "Switching desks must destroy both owned GameObjects.");
Assert(Material.Destroyed.Contains(firstOwnedMaterial) && !Material.Destroyed.Contains(defaultMaterial), "Switching desks must destroy only the material instance.");
Assert(Sprite.Destroyed.Contains(firstOwnedSprite)
       && Texture2D.Destroyed.Contains(firstOwnedTexture)
       && !Sprite.Destroyed.Contains(sourceSprite)
       && !Texture2D.Destroyed.Contains(sourceTexture)
       && !Texture2D.Destroyed.Contains(Texture2D.whiteTexture)
       && Texture2D.whiteTexture.m_CachedPtr != IntPtr.Zero,
    "Switching desks must destroy the Mod-owned sprite/texture and retain all source/shared assets.");
Assert(GameObject.Instantiated.Count == 2 && GameObject.Created.Count == 2, "Switching desks must create exactly one replacement pair.");

// Wrapper loss is diagnosed once for this target/phase and reacquires a clean pair without duplicate success evidence.
var secondClone = FindLatestClone();
var secondFillObject = FindLatestFillObject();
var secondOwnedSprite = GetFillRenderer(secondFillObject).sprite!;
var secondOwnedTexture = secondOwnedSprite.texture;
var secondOwnedMaterial = GetFillRenderer(secondFillObject).sharedMaterial!;
var successCountBeforeRebind = SuccessLogs(log, 7).Length;
secondClone.InvalidateNativePointer();
Time.realtimeSinceStartup = 0.6f;
RuntimeSeatHighlightService.Tick();
Assert(secondFillObject.m_CachedPtr == IntPtr.Zero
       && secondOwnedSprite.m_CachedPtr == IntPtr.Zero
       && secondOwnedTexture.m_CachedPtr == IntPtr.Zero
       && secondOwnedMaterial.m_CachedPtr == IntPtr.Zero,
    "Clone loss must retire the remaining live owned resources.");
Assert(SuccessLogs(log, 7).Length == successCountBeforeRebind, "Rebinding one target generation must not duplicate success evidence.");
Assert(FailureLogs(log, 7, "health").Length == 1, "Destroyed-wrapper health failure must be logged once.");

// Active health requires both disabled templates plus exact geometry and owned material identity.
var driftClone = FindLatestClone();
var driftFillObject = FindLatestFillObject();
var driftFill = GetFillRenderer(driftFillObject);
var driftOwnedTexture = driftFill.sprite!.texture;
var driftOwnedMaterial = driftFill.sharedMaterial!;
driftFill.sharedMaterial = defaultMaterial;
Time.realtimeSinceStartup = 0.9f;
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("instantiated material", StringComparison.Ordinal), "Material identity drift must fail closed.");
Assert(driftClone.m_CachedPtr == IntPtr.Zero
       && driftFillObject.m_CachedPtr == IntPtr.Zero
       && driftOwnedTexture.m_CachedPtr == IntPtr.Zero
       && driftOwnedMaterial.m_CachedPtr == IntPtr.Zero,
    "Health failure must retire the full owned resource group.");
Assert(!Material.Destroyed.Contains(defaultMaterial), "Health cleanup must never destroy the default shared material now bound by drift.");

Time.realtimeSinceStartup = 2.2f;
RuntimeSeatHighlightService.Tick();
var templateDriftClone = FindLatestClone();
var templateDriftFillObject = FindLatestFillObject();
FindRegional(templateDriftClone).enabled = true;
Time.realtimeSinceStartup = 2.5f;
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("RegionalHSVFillter template was unexpectedly re-enabled", StringComparison.Ordinal),
    "Re-enabling either custom-shader template must fail closed.");
Assert(templateDriftClone.m_CachedPtr == IntPtr.Zero && templateDriftFillObject.m_CachedPtr == IntPtr.Zero, "Template drift must clean the complete owned group.");

Time.realtimeSinceStartup = 3.8f;
RuntimeSeatHighlightService.Tick();
var parentDriftClone = FindLatestClone();
var parentDriftFillObject = FindLatestFillObject();
GetFillRenderer(parentDriftFillObject).transform.parent = new GameObject().transform;
Time.realtimeSinceStartup = 4.1f;
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("parent or local transform", StringComparison.Ordinal), "Independent fill transform drift must fail closed.");
Assert(parentDriftClone.m_CachedPtr == IntPtr.Zero && parentDriftFillObject.m_CachedPtr == IntPtr.Zero, "Transform drift must clean the complete owned group.");

// Pulse, create, and health failures each get a bounded target/phase diagnostic.
Time.realtimeSinceStartup = 5.4f;
RuntimeSeatHighlightService.Tick();
var pulseClone = FindLatestClone();
var pulseFillObject = FindLatestFillObject();
GetFillRenderer(pulseFillObject).ThrowOnColorWrite = true;
var pulseLogStart = log.InfoMessages.Count;
Time.realtimeSinceStartup = 5.5f;
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("seat fill pulse failed", StringComparison.Ordinal), "A pulse write exception must fail closed.");
Assert(pulseClone.m_CachedPtr == IntPtr.Zero && pulseFillObject.m_CachedPtr == IntPtr.Zero, "Pulse failure must clean the complete owned group.");
Assert(log.InfoMessages.Skip(pulseLogStart).Count(message => message.Contains("phase=pulse", StringComparison.Ordinal)) == 1, "Pulse failure must emit one phase diagnostic.");

SpriteRenderer.DefaultMaterial = null;
Time.realtimeSinceStartup = 7f;
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    7,
    CreateSeatTarget(RuntimeUiTargetKind.Rare, 0)));
var partialClonesBefore = GameObject.Instantiated.Count;
var partialFillsBefore = GameObject.Created.Count;
var destroyedBeforeMissingDefault = GameObject.Destroyed.Count;
var spritesBeforeMissingDefault = Sprite.Created.Count;
var spritesDestroyedBeforeMissingDefault = Sprite.Destroyed.Count;
var texturesBeforeMissingDefault = Texture2D.Created.Count;
var texturesDestroyedBeforeMissingDefault = Texture2D.Destroyed.Count;
var materialsBeforeMissingDefault = Material.Created.Count;
RuntimeSeatHighlightService.Tick();
Assert(GameObject.Instantiated.Count == partialClonesBefore + 1 && GameObject.Created.Count == partialFillsBefore + 1,
    "Default-material failure must happen only after exact clone verification and one fill-object creation.");
Assert(GameObject.Destroyed.Count == destroyedBeforeMissingDefault + 2, "Create failure must destroy both partial GameObjects.");
Assert(Sprite.Created.Count == spritesBeforeMissingDefault + 1
       && Sprite.Destroyed.Count == spritesDestroyedBeforeMissingDefault + 1,
    "Default-material failure must destroy the white sprite created earlier in the same transaction.");
Assert(Texture2D.Created.Count == texturesBeforeMissingDefault + 1
       && Texture2D.Destroyed.Count == texturesDestroyedBeforeMissingDefault + 1,
    "Default-material failure must destroy the white texture created earlier in the same transaction.");
Assert(Material.Created.Count == materialsBeforeMissingDefault, "Missing default material must not synthesize a substitute.");
Assert(RuntimeSeatHighlightService.Status.Contains("does not expose the verified Unity default sprite material", StringComparison.Ordinal),
    "Missing new-renderer default material must fail closed without a fallback.");
var createFailures = FailureLogs(log, 7, "create").Length;
Time.realtimeSinceStartup = 8.3f;
RuntimeSeatHighlightService.Tick();
Assert(FailureLogs(log, 7, "create").Length == createFailures, "Retrying the same target/phase/failure must not duplicate failure evidence.");

// A different failure in the same target/phase remains observable, while the shared result is never destroyed.
SpriteRenderer.DefaultMaterial = defaultMaterial;
SpriteRenderer.RejectMaterialInstantiation = true;
Time.realtimeSinceStartup = 10f;
var defaultDestroyCount = Material.Destroyed.Count(material => ReferenceEquals(material, defaultMaterial));
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("distinct Mod-owned material", StringComparison.Ordinal), "A non-instantiating material getter must fail closed.");
Assert(Sprite.Destroyed.Contains(Sprite.Created[^1]),
    "A rejected renderer.material result must destroy its already-created white sprite.");
Assert(Texture2D.Destroyed.Contains(Texture2D.Created[^1]),
    "A rejected renderer.material result must destroy its already-created white texture.");
Assert(Material.Destroyed.Count(material => ReferenceEquals(material, defaultMaterial)) == defaultDestroyCount,
    "A rejected renderer.material result must never destroy Unity's shared default material.");
Assert(FailureLogs(log, 7, "create").Length == createFailures + 1,
    "A distinct failure in the same target and create phase must receive its own bounded diagnostic.");
SpriteRenderer.RejectMaterialInstantiation = false;

// A listener-thread target update during the last local Unity read must win before ownership publication.
var successLogsBeforeMidCreateSwitch = SuccessLogs(log, 7).Length;
var createFailuresBeforeMidCreateSwitch = FailureLogs(log, 7, "create").Length;
var midSwitchClonesBefore = GameObject.Instantiated.Count;
var midSwitchFillsBefore = GameObject.Created.Count;
var midSwitchMaterialsBefore = Material.Created.Count;
SpriteRenderer.ConstructedRendererBoundsReadCallback = () =>
    RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
        7,
        CreateSeatTarget(RuntimeUiTargetKind.Rare, 1)));
Time.realtimeSinceStartup = 12.7f;
RuntimeSeatHighlightService.Tick();
SpriteRenderer.ConstructedRendererBoundsReadCallback = null;
Assert(GameObject.Instantiated.Count == midSwitchClonesBefore + 1
       && GameObject.Created.Count == midSwitchFillsBefore + 1
       && Material.Created.Count == midSwitchMaterialsBefore + 1,
    "A mid-create target switch must occur after all three locally owned resources exist.");
var midSwitchClone = GameObject.Instantiated[^1];
var midSwitchFillObject = GameObject.Created[^1];
var midSwitchOwnedSprite = Sprite.Created[^1];
var midSwitchOwnedTexture = midSwitchOwnedSprite.texture;
var midSwitchOwnedMaterial = Material.Created[^1];
Assert(GameObject.Destroyed.Contains(midSwitchClone)
       && GameObject.Destroyed.Contains(midSwitchFillObject)
       && Sprite.Destroyed.Contains(midSwitchOwnedSprite)
       && Texture2D.Destroyed.Contains(midSwitchOwnedTexture)
       && Material.Destroyed.Contains(midSwitchOwnedMaterial),
    "A superseded create must clean the complete locally owned resource group.");
Assert(!Material.Destroyed.Contains(defaultMaterial), "Superseded create cleanup must retain Unity's default shared material.");
Assert(SuccessLogs(log, 7).Length == successLogsBeforeMidCreateSwitch,
    "A superseded target must not emit stale successful binding evidence.");
Assert(FailureLogs(log, 7, "create").Length == createFailuresBeforeMidCreateSwitch,
    "A superseded target must not consume failure evidence for the newly desired target.");
Assert(RuntimeSeatHighlightService.Status.StartsWith("waiting: target changed while creating visual", StringComparison.Ordinal)
       && RuntimeSeatHighlightService.Status.Contains("desired=4/session:7/targets:1", StringComparison.Ordinal)
       && RuntimeSeatHighlightService.Status.Contains("applied=3", StringComparison.Ordinal)
       && RuntimeSeatHighlightService.Status.Contains("active=;", StringComparison.Ordinal),
    "Mid-create target replacement must leave the new generation unapplied and no active stale visual.");

Time.realtimeSinceStartup = 12.8f;
RuntimeSeatHighlightService.Tick();
Assert(SuccessLogs(log, 7).Length == successLogsBeforeMidCreateSwitch + 1
       && SuccessLogs(log, 7)[^1].Contains("targetGeneration=4; desk=1", StringComparison.Ordinal),
    "The next main-thread reconcile must bind only the replacement target generation.");

// The native clone shape remains exact, while shader-role binding stays independent of serialized order.
ConfigureSelectionClone(selectionRoot, (_, cluster) =>
{
    var renderers = cluster.GetObjects<SpriteRenderer>();
    var extra = new SpriteRenderer { enabled = true, sprite = sourceSprite, sharedMaterial = regionalMaterial };
    extra.transform.parent = cluster.transform;
    cluster.SerializedObjectsOverride = new Component[] { renderers[1], renderers[0], extra };
});
Time.realtimeSinceStartup = 13f;
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    7,
    CreateSeatTarget(RuntimeUiTargetKind.Rare, 0)));
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("exactly two SpriteRenderers, got 3", StringComparison.Ordinal), "A third native renderer must fail closed.");

ConfigureSelectionClone(selectionRoot, (_, cluster) =>
{
    var renderers = cluster.GetObjects<SpriteRenderer>();
    cluster.SerializedObjectsOverride = new Component[] { renderers[0], renderers[1] };
});
Time.realtimeSinceStartup = 14.5f;
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    7,
    CreateSeatTarget(RuntimeUiTargetKind.Rare, 1)));
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.StartsWith("active", StringComparison.Ordinal),
    "Reversing serialized renderer order must still bind by unique exact shader roles.");
var reverseOrderClone = FindLatestClone();
var reverseOrderCluster = (UIElementCluster)reverseOrderClone.GetComponent(typeof(UIElementCluster))!;
var reverseOrderRenderers = reverseOrderCluster.GetObjects<SpriteRenderer>();
var reverseOrderOutline = SingleRenderer(reverseOrderRenderers, outlineShaderName);
var reverseOrderRegional = SingleRenderer(reverseOrderRenderers, regionalShaderName);
Assert(ReferenceEquals(reverseOrderRenderers[0], reverseOrderOutline)
       && ReferenceEquals(reverseOrderRenderers[1], reverseOrderRegional),
    "The opposite serialized Regional/Outline order must produce the opposite native array order.");
Assert(!reverseOrderOutline.enabled && !reverseOrderRegional.enabled,
    "Both uniquely classified templates must stay disabled for either serialized order.");
Assert(ReferenceEquals(GetFillRenderer(FindLatestFillObject()).transform.parent, reverseOrderRegional.transform.parent),
    "The independent fill must copy the uniquely classified Regional renderer for either serialized order.");

ConfigureSelectionClone(selectionRoot, null);
Time.realtimeSinceStartup = 16f;
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    7,
    CreateSeatTarget(RuntimeUiTargetKind.Rare, 0)));
RuntimeSeatHighlightService.Tick();
var toggleClone = FindLatestClone();
var toggleFillObject = FindLatestFillObject();
var toggleOwnedSprite = GetFillRenderer(toggleFillObject).sprite!;
var toggleOwnedTexture = toggleOwnedSprite.texture;
var toggleOwnedMaterial = GetFillRenderer(toggleFillObject).sharedMaterial!;
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(7));
RuntimeSeatHighlightService.Tick();
Assert(toggleClone.m_CachedPtr == IntPtr.Zero
       && toggleFillObject.m_CachedPtr == IntPtr.Zero
       && toggleOwnedSprite.m_CachedPtr == IntPtr.Zero
       && toggleOwnedTexture.m_CachedPtr == IntPtr.Zero
       && toggleOwnedMaterial.m_CachedPtr == IntPtr.Zero,
    "Disabling the feature must destroy both objects, the white sprite/texture, and the owned material.");
Assert(!Material.Destroyed.Contains(defaultMaterial), "Toggle cleanup must retain Unity's default material.");

// Failure evidence is capped at eight per business and resets independently with the business generation.
const long failureBudgetBusiness = 70;
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, failureBudgetBusiness, Environment.CurrentManagedThreadId);
SpriteRenderer.DefaultMaterial = null;
var failureBudgetStart = log.InfoMessages.Count;
for (var index = 0; index < 9; index += 1)
{
    Time.realtimeSinceStartup = 20f + (index * 2f);
    RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
        failureBudgetBusiness,
        CreateSeatTarget(RuntimeUiTargetKind.Rare, index % 2)));
    RuntimeSeatHighlightService.Tick();
}
Assert(log.InfoMessages.Skip(failureBudgetStart).Count(message =>
        message.Contains($"Runtime seat highlight fill failed: businessGeneration={failureBudgetBusiness};", StringComparison.Ordinal)) == 8,
    "A business must emit at most eight target/phase failure records.");
Assert(RuntimeSeatHighlightService.Status.Contains("failureLog=8/8", StringComparison.Ordinal), "Status must expose the exhausted failure budget.");

const long resetBusiness = 71;
SpriteRenderer.DefaultMaterial = defaultMaterial;
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, resetBusiness, Environment.CurrentManagedThreadId);
Time.realtimeSinceStartup = 40f;
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    resetBusiness,
    CreateSeatTarget(RuntimeUiTargetKind.Rare, 1)));
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("failureLog=0/8", StringComparison.Ordinal)
       && RuntimeSeatHighlightService.Status.Contains("budget:1/8", StringComparison.Ordinal),
    "A new business must reset failure and binding budgets independently.");

for (var index = 0; index < 8; index += 1)
{
    Time.realtimeSinceStartup = 41f + index;
    RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
        resetBusiness,
        CreateSeatTarget(RuntimeUiTargetKind.Rare, index % 2)));
    RuntimeSeatHighlightService.Tick();
}
Assert(SuccessLogs(log, resetBusiness).Length == 8, "A business must emit at most eight successful binding records.");
Assert(RuntimeSeatHighlightService.Status.Contains("budget:8/8", StringComparison.Ordinal), "Status must expose the exhausted binding budget.");

var textureHealthClone = FindLatestClone();
var textureHealthFillObject = FindLatestFillObject();
var textureHealthFill = GetFillRenderer(textureHealthFillObject);
var textureHealthSprite = textureHealthFill.sprite!;
var textureHealthTexture = textureHealthSprite.texture;
var textureHealthMaterial = textureHealthFill.sharedMaterial!;
textureHealthTexture.InvalidateNativePointer();
Time.realtimeSinceStartup = 54f;
RuntimeSeatHighlightService.Tick();
Assert(textureHealthClone.m_CachedPtr == IntPtr.Zero
       && textureHealthFillObject.m_CachedPtr == IntPtr.Zero
       && textureHealthSprite.m_CachedPtr == IntPtr.Zero
       && textureHealthTexture.m_CachedPtr == IntPtr.Zero
       && textureHealthMaterial.m_CachedPtr == IntPtr.Zero,
    "Owned texture loss must retire every remaining live resource in the same visual group.");
Assert(!ReferenceEquals(GetFillRenderer(FindLatestFillObject()).sprite!.texture, textureHealthTexture),
    "The same Tick may rebind, but it must use a fresh owned texture identity.");
var spriteDriftClone = FindLatestClone();
var spriteDriftFillObject = FindLatestFillObject();
var spriteDriftFill = GetFillRenderer(spriteDriftFillObject);
var spriteDriftOwnedSprite = spriteDriftFill.sprite!;
var spriteDriftOwnedTexture = spriteDriftOwnedSprite.texture;
var spriteDriftOwnedMaterial = spriteDriftFill.sharedMaterial!;
spriteDriftFill.sprite = sourceSprite;
Time.realtimeSinceStartup = 55f;
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("distinct white sprite", StringComparison.Ordinal),
    "Rebinding the visible renderer to the source texture must fail the single white-sprite path closed.");
Assert(spriteDriftClone.m_CachedPtr == IntPtr.Zero
       && spriteDriftFillObject.m_CachedPtr == IntPtr.Zero
       && spriteDriftOwnedSprite.m_CachedPtr == IntPtr.Zero
       && spriteDriftOwnedTexture.m_CachedPtr == IntPtr.Zero
       && spriteDriftOwnedMaterial.m_CachedPtr == IntPtr.Zero,
    "White-sprite identity drift must retire every owned resource without touching the source sprite.");
Assert(sourceSprite.m_CachedPtr != IntPtr.Zero && sourceTexture.m_CachedPtr != IntPtr.Zero,
    "White-sprite drift cleanup must retain the exact source sprite and texture.");

Time.realtimeSinceStartup = 57f;
RuntimeSeatHighlightService.Tick();

var closingClone = FindLatestClone();
var closingFillObject = FindLatestFillObject();
var closingSprite = GetFillRenderer(closingFillObject).sprite!;
var closingTexture = closingSprite.texture;
var closingMaterial = GetFillRenderer(closingFillObject).sharedMaterial!;
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(false, resetBusiness, Environment.CurrentManagedThreadId);
RuntimeSeatHighlightService.Tick();
Assert(closingClone.m_CachedPtr == IntPtr.Zero
       && closingFillObject.m_CachedPtr == IntPtr.Zero
       && closingSprite.m_CachedPtr == IntPtr.Zero
       && closingTexture.m_CachedPtr == IntPtr.Zero
       && closingMaterial.m_CachedPtr == IntPtr.Zero,
    "Night-business Closing must destroy the full owned visual.");

RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(true, 72, Environment.CurrentManagedThreadId);
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    72,
    CreateSeatTarget(RuntimeUiTargetKind.Rare, 0)));
RuntimeSeatHighlightService.Tick();
var disposeClone = FindLatestClone();
var disposeFillObject = FindLatestFillObject();
var disposeSprite = GetFillRenderer(disposeFillObject).sprite!;
var disposeTexture = disposeSprite.texture;
var disposeMaterial = GetFillRenderer(disposeFillObject).sharedMaterial!;
RuntimeSeatHighlightService.Dispose("controller disposed");
Assert(disposeClone.m_CachedPtr == IntPtr.Zero
       && disposeFillObject.m_CachedPtr == IntPtr.Zero
       && disposeSprite.m_CachedPtr == IntPtr.Zero
       && disposeTexture.m_CachedPtr == IntPtr.Zero
       && disposeMaterial.m_CachedPtr == IntPtr.Zero,
    "Controller Dispose must destroy the full owned visual on the Unity thread.");

RuntimeSeatHighlightService.Resume("abandon test");
Time.realtimeSinceStartup = 50f;
RuntimeSeatHighlightService.Tick();
var abandonClone = FindLatestClone();
var abandonFillObject = FindLatestFillObject();
var abandonFill = GetFillRenderer(abandonFillObject);
var abandonSprite = abandonFill.sprite!;
var abandonTexture = abandonSprite.texture;
var abandonMaterial = abandonFill.sharedMaterial!;
abandonClone.InvalidateNativePointer();
abandonFillObject.InvalidateNativePointer();
abandonFill.InvalidateNativePointer();
abandonSprite.InvalidateNativePointer();
abandonTexture.InvalidateNativePointer();
abandonMaterial.InvalidateNativePointer();
var destroyedBeforeAbandon = GameObject.Destroyed.Count;
var spritesDestroyedBeforeAbandon = Sprite.Destroyed.Count;
var texturesDestroyedBeforeAbandon = Texture2D.Destroyed.Count;
var materialsDestroyedBeforeAbandon = Material.Destroyed.Count;
RuntimeSeatHighlightService.Abandon("scene destroyed");
Assert(GameObject.Destroyed.Count == destroyedBeforeAbandon
       && Sprite.Destroyed.Count == spritesDestroyedBeforeAbandon
       && Texture2D.Destroyed.Count == texturesDestroyedBeforeAbandon
       && Material.Destroyed.Count == materialsDestroyedBeforeAbandon,
    "Destroyed-scene abandonment must drop wrappers without Unity destruction calls.");
Assert(!Material.Destroyed.Contains(defaultMaterial)
       && !Sprite.Destroyed.Contains(sourceSprite)
       && !Texture2D.Destroyed.Contains(sourceTexture)
       && !Texture2D.Destroyed.Contains(Texture2D.whiteTexture)
       && Texture2D.whiteTexture.m_CachedPtr != IntPtr.Zero,
    "No lifecycle path may destroy a Unity-owned shared asset.");

// A dynamically created SpriteRenderer must be recovered only through an exact same-pointer
// native cast. Every ambiguity fails before material ownership and cleans both partial objects.
var castFailureCases = new[]
{
    (Mode: SpriteRendererCastMode.Reject, Failure: "cannot exact-cast to a live SpriteRenderer"),
    (Mode: SpriteRendererCastMode.DifferentPointer, Failure: "cast changed native component identity"),
    (Mode: SpriteRendererCastMode.WrongNativeClass, Failure: "native class is not exact UnityEngine.SpriteRenderer"),
    (Mode: SpriteRendererCastMode.WrongOwner, Failure: "belongs to a different GameObject"),
};
for (var index = 0; index < castFailureCases.Length; index += 1)
{
    var generation = 80L + index;
    var failureCase = castFailureCases[index];
    RuntimeReflectionUtility.SpriteRendererCastMode = failureCase.Mode;
    RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(
        true,
        generation,
        Environment.CurrentManagedThreadId);
    RuntimeSeatHighlightService.Resume("exact SpriteRenderer cast failure test");
    RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
        generation,
        CreateSeatTarget(RuntimeUiTargetKind.Rare, index % 2)));
    var clonesBeforeCastFailure = GameObject.Instantiated.Count;
    var fillsBeforeCastFailure = GameObject.Created.Count;
    var destroyedBeforeCastFailure = GameObject.Destroyed.Count;
    var spritesBeforeCastFailure = Sprite.Created.Count;
    var materialsBeforeCastFailure = Material.Created.Count;
    Time.realtimeSinceStartup = 60f + (index * 2f);
    RuntimeSeatHighlightService.Tick();
    Assert(GameObject.Instantiated.Count == clonesBeforeCastFailure + 1
           && GameObject.Created.Count == fillsBeforeCastFailure + 1,
        "An exact-cast failure must occur after one clone and one fill-object allocation.");
    Assert(GameObject.Destroyed.Count == destroyedBeforeCastFailure + 2,
        "An exact-cast failure must clean both partially owned GameObjects.");
    Assert(Sprite.Created.Count == spritesBeforeCastFailure,
        "An exact-cast failure must happen before the Mod-owned white sprite is created.");
    Assert(Material.Created.Count == materialsBeforeCastFailure,
        "An exact-cast failure must happen before a private material can be instantiated.");
    Assert(RuntimeSeatHighlightService.Status.Contains(failureCase.Failure, StringComparison.Ordinal),
        $"Exact-cast mode {failureCase.Mode} must expose its precise failure stage.");
    Assert(FailureLogs(log, generation, "create").Length == 1,
        "Each exact-cast failure must emit one bounded create diagnostic.");
}
RuntimeReflectionUtility.SpriteRendererCastMode = SpriteRendererCastMode.Exact;

ConfigureSelectionClone(selectionRoot, (_, cluster) =>
{
    SingleRenderer(cluster.GetObjects<SpriteRenderer>(), regionalShaderName).drawMode = SpriteDrawMode.Sliced;
});
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(
    true,
    90,
    Environment.CurrentManagedThreadId);
RuntimeSeatHighlightService.Resume("non-simple Regional draw-mode test");
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    90,
    CreateSeatTarget(RuntimeUiTargetKind.Rare, 0)));
var spritesBeforeSliced = Sprite.Created.Count;
var materialsBeforeSliced = Material.Created.Count;
Time.realtimeSinceStartup = 70f;
RuntimeSeatHighlightService.Tick();
Assert(RuntimeSeatHighlightService.Status.Contains("draw mode is not Simple", StringComparison.Ordinal),
    "A Sliced/Tiled Regional template must fail closed before creating a white sprite.");
Assert(Sprite.Created.Count == spritesBeforeSliced && Material.Created.Count == materialsBeforeSliced,
    "A non-Simple source must be rejected before Sprite or Material ownership begins.");

ConfigureSelectionClone(selectionRoot, null);
Sprite.ThrowOnOverrideGeometry = true;
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(
    true,
    91,
    Environment.CurrentManagedThreadId);
RuntimeSeatHighlightService.Resume("white Sprite override failure test");
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    91,
    CreateSeatTarget(RuntimeUiTargetKind.Rare, 1)));
var spritesBeforeOverrideFailure = Sprite.Created.Count;
var destroyedSpritesBeforeOverrideFailure = Sprite.Destroyed.Count;
var texturesBeforeOverrideFailure = Texture2D.Created.Count;
var destroyedTexturesBeforeOverrideFailure = Texture2D.Destroyed.Count;
var materialsBeforeOverrideFailure = Material.Created.Count;
Time.realtimeSinceStartup = 72f;
RuntimeSeatHighlightService.Tick();
Sprite.ThrowOnOverrideGeometry = false;
Assert(RuntimeSeatHighlightService.Status.Contains("rejected sprite geometry override", StringComparison.Ordinal),
    "OverrideGeometry exceptions must remain a precise create failure.");
Assert(FailureLogs(log, 91, "create").Single().Contains(
           "createTexture=64x64/RGBA32/mips:1/readable:True; createMesh=Tight",
           StringComparison.Ordinal),
    "A white Sprite creation failure must identify the single owned-texture/Tight mesh path.");
Assert(Sprite.Created.Count == spritesBeforeOverrideFailure + 1
       && Sprite.Destroyed.Count == destroyedSpritesBeforeOverrideFailure + 1
       && Sprite.Destroyed.Contains(Sprite.Created[^1]),
    "A failed geometry override must destroy the candidate Mod-owned Sprite inside the helper transaction.");
Assert(Texture2D.Created.Count == texturesBeforeOverrideFailure + 1
       && Texture2D.Destroyed.Count == destroyedTexturesBeforeOverrideFailure + 1
       && Texture2D.Destroyed.Contains(Texture2D.Created[^1]),
    "A failed geometry override must destroy the candidate Mod-owned texture after its Sprite.");
Assert(Material.Created.Count == materialsBeforeOverrideFailure,
    "A failed geometry override must happen before private material instantiation.");

Texture2D.ThrowOnSetPixels32 = true;
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(
    true,
    92,
    Environment.CurrentManagedThreadId);
RuntimeSeatHighlightService.Resume("white texture pixel upload failure test");
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    92,
    CreateSeatTarget(RuntimeUiTargetKind.Rare, 0)));
var texturesBeforePixelFailure = Texture2D.Created.Count;
var destroyedTexturesBeforePixelFailure = Texture2D.Destroyed.Count;
var spritesBeforePixelFailure = Sprite.Created.Count;
Time.realtimeSinceStartup = 74f;
RuntimeSeatHighlightService.Tick();
Texture2D.ThrowOnSetPixels32 = false;
Assert(RuntimeSeatHighlightService.Status.Contains("rejected white pixel upload", StringComparison.Ordinal),
    "SetPixels32 failures must remain precise create failures.");
Assert(Texture2D.Created.Count == texturesBeforePixelFailure + 1
       && Texture2D.Destroyed.Count == destroyedTexturesBeforePixelFailure + 1
       && Sprite.Created.Count == spritesBeforePixelFailure,
    "A pixel upload failure must destroy the partial texture before any Sprite is created.");

Texture2D.ThrowOnApply = true;
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(
    true,
    93,
    Environment.CurrentManagedThreadId);
RuntimeSeatHighlightService.Resume("white texture apply failure test");
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    93,
    CreateSeatTarget(RuntimeUiTargetKind.Rare, 1)));
var texturesBeforeApplyFailure = Texture2D.Created.Count;
var destroyedTexturesBeforeApplyFailure = Texture2D.Destroyed.Count;
var spritesBeforeApplyFailure = Sprite.Created.Count;
Time.realtimeSinceStartup = 76f;
RuntimeSeatHighlightService.Tick();
Texture2D.ThrowOnApply = false;
Assert(RuntimeSeatHighlightService.Status.Contains("rejected white texture apply", StringComparison.Ordinal),
    "Texture Apply failures must remain precise create failures.");
Assert(Texture2D.Created.Count == texturesBeforeApplyFailure + 1
       && Texture2D.Destroyed.Count == destroyedTexturesBeforeApplyFailure + 1
       && Sprite.Created.Count == spritesBeforeApplyFailure,
    "An Apply failure must destroy the partial texture before any Sprite is created.");

// Distinct rare and normal desks own independent complete resource groups and colors.
ConfigureSelectionClone(selectionRoot, null);
RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(
    true,
    100,
    Environment.CurrentManagedThreadId);
RuntimeSeatHighlightService.Resume("dual target seat coverage");
var dualRareColor = new RuntimeTargetHighlightColor(0xD8, 0x8A, 0x21);
var dualNormalColor = new RuntimeTargetHighlightColor(0x27, 0x86, 0xC8);
var dualRareSeatTarget = CreateSeatTarget(
    RuntimeUiTargetKind.Rare,
    0,
    dualRareColor,
    "dual-seat-rare");
var dualNormalSeatTarget = CreateSeatTarget(
    RuntimeUiTargetKind.Normal,
    1,
    dualNormalColor,
    "dual-seat-normal");
var liveClonesBeforeDual = LiveSeatClones().Length;
var liveFillsBeforeDual = LiveSeatFillObjects().Length;
var clonePointersBeforeDual = LiveSeatClones().Select(clone => clone.Pointer).ToHashSet();
Time.realtimeSinceStartup = 100f;
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    100,
    dualRareSeatTarget,
    dualNormalSeatTarget));
RuntimeSeatHighlightService.Tick();
Assert(LiveSeatClones().Length == liveClonesBeforeDual + 2
       && LiveSeatFillObjects().Length == liveFillsBeforeDual + 2,
    "Two desks must create two independent clone/fill resource groups.");
var dualRareClone = LiveSeatClones().Single(clone =>
    !clonePointersBeforeDual.Contains(clone.Pointer)
    && clone.transform.position == new Vector3(3.5f, 6.5f, 0.25f));
var dualNormalClone = LiveSeatClones().Single(clone =>
    !clonePointersBeforeDual.Contains(clone.Pointer)
    && clone.transform.position == new Vector3(7f, 9.5f, 0.5f));
var dualRareFillObject = FindLiveFillForClone(dualRareClone);
var dualNormalFillObject = FindLiveFillForClone(dualNormalClone);
var dualRareFill = GetFillRenderer(dualRareFillObject);
var dualNormalFill = GetFillRenderer(dualNormalFillObject);
AssertColor(
    dualRareFill.color,
    RuntimeTargetHighlightStyle.BuildSeatFillPulseColor(
        RuntimeUiTargetKinds.Rare,
        new RuntimeTargetHighlightPalette(dualRareColor, dualNormalColor),
        Time.realtimeSinceStartup),
    "The rare desk must use its injected color.");
AssertColor(
    dualNormalFill.color,
    RuntimeTargetHighlightStyle.BuildSeatFillPulseColor(
        RuntimeUiTargetKinds.Normal,
        new RuntimeTargetHighlightPalette(dualRareColor, dualNormalColor),
        Time.realtimeSinceStartup),
    "The normal desk must use its injected color.");
var retainedNormalSprite = dualNormalFill.sprite!;
var retainedNormalTexture = retainedNormalSprite.texture;
var retainedNormalMaterial = dualNormalFill.sharedMaterial!;
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(100, dualNormalSeatTarget));
Time.realtimeSinceStartup = 100.1f;
RuntimeSeatHighlightService.Tick();
Assert(dualRareClone.m_CachedPtr == IntPtr.Zero
       && dualRareFillObject.m_CachedPtr == IntPtr.Zero,
    "Removing the rare desk claim must retire only its complete owned group.");
Assert(dualNormalClone.m_CachedPtr != IntPtr.Zero
       && dualNormalFillObject.m_CachedPtr != IntPtr.Zero
       && ReferenceEquals(dualNormalFill.sprite, retainedNormalSprite)
       && ReferenceEquals(dualNormalFill.sprite!.texture, retainedNormalTexture)
       && ReferenceEquals(dualNormalFill.sharedMaterial, retainedNormalMaterial),
    "Removing one desk claim must preserve the other desk's object, texture, Sprite, and material identities.");

// When both order kinds claim one desk, exactly one group round-trips between both colors.
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(100));
RuntimeSeatHighlightService.Tick();
var clonesBeforeSharedDesk = LiveSeatClones().Length;
var fillsBeforeSharedDesk = LiveSeatFillObjects().Length;
var clonePointersBeforeSharedDesk = LiveSeatClones().Select(clone => clone.Pointer).ToHashSet();
var sharedRareTarget = CreateSeatTarget(
    RuntimeUiTargetKind.Rare,
    0,
    dualRareColor,
    "shared-seat-rare");
var sharedNormalTarget = CreateSeatTarget(
    RuntimeUiTargetKind.Normal,
    0,
    dualNormalColor,
    "shared-seat-normal");
var normalColorEndpoint = MathF.PI / (2f * 2.75f);
Time.realtimeSinceStartup = normalColorEndpoint;
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(
    100,
    sharedRareTarget,
    sharedNormalTarget));
RuntimeSeatHighlightService.Tick();
Assert(LiveSeatClones().Length == clonesBeforeSharedDesk + 1
       && LiveSeatFillObjects().Length == fillsBeforeSharedDesk + 1,
    "A desk shared by both target kinds must own exactly one resource group.");
var sharedClone = LiveSeatClones().Single(clone =>
    !clonePointersBeforeSharedDesk.Contains(clone.Pointer));
var sharedFillObject = FindLiveFillForClone(sharedClone);
var sharedFill = GetFillRenderer(sharedFillObject);
AssertColor(
    sharedFill.color,
    RuntimeTargetHighlightStyle.BuildSeatFillPulseColor(
        RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal,
        new RuntimeTargetHighlightPalette(dualRareColor, dualNormalColor),
        normalColorEndpoint),
    "The shared desk must reach the normal-order color endpoint.");
var sharedClonePointer = sharedClone.Pointer;
var sharedFillPointer = sharedFillObject.Pointer;
var rareColorEndpoint = 3f * MathF.PI / (2f * 2.75f);
Time.realtimeSinceStartup = rareColorEndpoint;
RuntimeSeatHighlightService.Tick();
Assert(sharedClone.Pointer == sharedClonePointer
       && sharedFillObject.Pointer == sharedFillPointer
       && LiveSeatClones().Length == clonesBeforeSharedDesk + 1,
    "Shared-desk color travel must not create a second clone.");
AssertColor(
    sharedFill.color,
    RuntimeTargetHighlightStyle.BuildSeatFillPulseColor(
        RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal,
        new RuntimeTargetHighlightPalette(dualRareColor, dualNormalColor),
        rareColorEndpoint),
    "The shared desk must reach the rare-order color endpoint on the same fill.");
Assert(RuntimeSeatHighlightService.Status.Contains("claims:Rare, Normal", StringComparison.Ordinal),
    "Shared-desk diagnostics must expose both target claims.");

Time.realtimeSinceStartup = normalColorEndpoint;
RuntimeSeatHighlightService.UpdateTargets(CreateSeatTargetSet(100, sharedNormalTarget));
RuntimeSeatHighlightService.Tick();
Assert(sharedClone.Pointer == sharedClonePointer
       && sharedFillObject.Pointer == sharedFillPointer
       && LiveSeatClones().Length == clonesBeforeSharedDesk + 1,
    "Removing one shared-desk claim must retain the remaining target's ownership group.");
AssertColor(
    sharedFill.color,
    RuntimeTargetHighlightStyle.BuildSeatFillPulseColor(
        RuntimeUiTargetKinds.Normal,
        new RuntimeTargetHighlightPalette(
            RuntimeTargetHighlightColor.DefaultRare,
            dualNormalColor),
        normalColorEndpoint),
    "Removing the rare shared-desk claim must switch the retained fill to the normal color.");
Assert(RuntimeSeatHighlightService.Status.Contains("claims:Normal", StringComparison.Ordinal),
    "Shared-desk diagnostics must update after one target claim is removed.");

RuntimeSeatHighlightService.Dispose("dual target seat coverage complete");

var serviceSource = File.ReadAllText("mods/bepinex/src/Save/RuntimeSeatHighlightService.cs");
var styleSource = File.ReadAllText("mods/bepinex/src/Save/RuntimeTargetHighlightStyle.cs");
var controllerSource = File.ReadAllText("mods/bepinex/src/Ui/StewardOverlayController.cs");
var pluginSource = File.ReadAllText("mods/bepinex/src/Plugin/MystiaStewardCompanionPlugin.cs");
Assert(controllerSource.Contains("RuntimeSeatHighlightService.Dispose(\"controller disposed\")", StringComparison.Ordinal), "Controller disposal must release the Mod-owned seat fill.");
Assert(pluginSource.Contains("RuntimeSeatHighlightService.Attach(Log);", StringComparison.Ordinal), "The plugin must attach bounded seat-highlight diagnostics.");
foreach (var required in new[]
         {
             "new Il2CppReferenceArray<Il2CppSystem.Type>(1)",
             "return new GameObject(name, componentTypes)",
             "selectionClone = UnityEngine.Object.Instantiate(selectionSourceObject)",
             "sourceRenderers.Length != 2",
             "string.Equals(shader.name, OutlineShaderName, StringComparison.Ordinal)",
             "string.Equals(shader.name, RegionalFillShaderName, StringComparison.Ordinal)",
             "outlineRenderer.enabled = false",
             "regionalRenderer.enabled = false",
             "fillTransform.parent = regionalTransform.parent",
             "fillTransform.localPosition = regionalTransform.localPosition",
             "fillTransform.localRotation = regionalTransform.localRotation",
             "fillTransform.localScale = regionalTransform.localScale",
             "TryCastRuntimeObject(",
             "SpriteRendererTypeName",
             "queriedFillPointer != exactFillPointer",
             "TryReadNativeClassName(fillRenderer",
             "OwnedFillTextureSize = 64",
             "new Texture2D(",
             "TextureFormat.RGBA32",
             "new Il2CppStructArray<Color32>",
             "SetPixels32(whitePixels)",
             "Apply(updateMipmaps: false, makeNoLongerReadable: false)",
             "SafeDestroyTexture(fillTexture)",
             "private static readonly Dictionary<int, ActiveSeatVisual> ActiveVisuals",
             "private static Dictionary<int, SeatHighlightClaim> BuildSeatClaims(",
             "combined |= existing.Claims",
             "private sealed class ActiveSeatVisual",
             "sourceSprite.vertices",
             "sourceSprite.triangles",
             "sourceSprite.pixelsPerUnit",
             "new Rect(0f, 0f, whiteWidth, whiteHeight)",
             "SpriteMeshType.Tight",
             "candidateSprite.OverrideGeometry(destinationVertices, sourceTriangles)",
             "fillRenderer.material",
             "defaultMaterialPointer == ownedMaterialPointer",
             "fillRenderer.sprite = fillSprite",
             "RuntimeTargetHighlightStyle.BuildSeatFillPulseColor",
             "TryValidateDisabledTemplateRenderer",
             "SafeDestroySprite(fillSprite)",
             "SafeDestroyMaterial(fillMaterial)",
             "Runtime seat highlight fill bound:",
             "Runtime seat highlight fill failed:",
             "phase",
         })
{
    Assert(serviceSource.Contains(required, StringComparison.Ordinal), $"Seat fill lost its independent standard-renderer contract: {required}");
}

foreach (var forbidden in new[]
         {
             "Shader.Find",
             "new Material(",
             "fillRenderer.sharedMaterial =",
             "HasPropertyBlock(",
             "SetPropertyBlock(",
             "StencilPainterController",
             "stencilPainterParent",
             "interactablesHighlightedVisual",
             "TryReadHighlightedVisual",
             "_activeStencilClone",
             "_activeWorkers",
             "PulseRendererBinding",
             "native stencil show coroutine",
             "RendererDiagnostic",
             "MaterialPropertyBlock",
             "materialColors",
             "propertyBlock",
             "UpdateCurser(",
             "ShowPainter(",
             "HidePainter(",
             "GuestTables",
             "FindObjectOfType",
             "FindObjectsOfType",
             "GetComponentInChildren",
             "\"GetObject\"",
             "FindDeclaredGenericInstanceMethod",
             "primaryPointer",
             "fillComponent is not SpriteRenderer",
             "fillRenderer.sprite = tileSprite",
             "SpriteMeshType.FullRect",
             "Texture2D.whiteTexture",
             "SeatFillRenderEvidence",
             "CaptureRenderEvidence",
             "BuildRenderStatus",
         })
{
    Assert(!serviceSource.Contains(forbidden, StringComparison.Ordinal), $"Seat fill must not retain a shader lookup, material replacement, or old fallback path: {forbidden}");
}

var bindingLog = Slice(
    serviceSource,
    "private static void TryLogFillBindingLocked(",
    "private static void TryLogFailureLocked(");
Assert(!bindingLog.Contains(".name", StringComparison.Ordinal)
       && !bindingLog.Contains("isVisible", StringComparison.Ordinal)
       && !bindingLog.Contains("renderQueue", StringComparison.Ordinal)
       && !bindingLog.Contains("TryReadNativeObjectPointer", StringComparison.Ordinal),
    "A successful binding log must use validated managed scalars rather than add Unity diagnostic getters to the visual gate.");

Assert(styleSource.Contains("DefaultRare = new(0xFF, 0xDB, 0x2E)", StringComparison.Ordinal), "The target palette must retain the exact default rare-order RGB.");
Assert(styleSource.Contains("DefaultNormal = new(0x5F, 0xAC, 0xD3)", StringComparison.Ordinal), "The target palette must retain the exact default normal-order RGB.");
Console.WriteLine("Runtime seat highlight smoke passed.");

static SpriteRenderer CreateSelectionRenderer(
    UIElementCluster cluster,
    Transform parent,
    Sprite sprite,
    Material material,
    Vector3 localPosition,
    Vector3 localScale,
    Quaternion localRotation,
    int layer,
    int sortingLayerId,
    int sortingOrder,
    bool flipX,
    bool flipY,
    Color color)
{
    var renderer = new SpriteRenderer
    {
        enabled = true,
        sprite = sprite,
        sharedMaterial = material,
        color = color,
        sortingLayerID = sortingLayerId,
        sortingOrder = sortingOrder,
        drawMode = SpriteDrawMode.Simple,
        flipX = flipX,
        flipY = flipY,
        AutoBounds = true,
        StaticBounds = new Bounds(default, new Vector3(2f, 3f, 0f)),
    };
    renderer.gameObject.layer = layer;
    renderer.transform.parent = parent;
    renderer.transform.localPosition = localPosition;
    renderer.transform.localScale = localScale;
    renderer.transform.localRotation = localRotation;
    cluster.Add(renderer);
    return renderer;
}

static void ConfigureSelectionClone(GameObject source, Action<GameObject, UIElementCluster>? configure)
{
    source.CloneFactory = configure == null
        ? null
        : parent =>
        {
            var clone = source.DeepClone(parent);
            var cluster = (UIElementCluster)clone.GetComponent(typeof(UIElementCluster))!;
            configure(clone, cluster);
            return clone;
        };
}

static GameObject FindLatestClone() =>
    GameObject.Instantiated.Last(clone => clone.name == cloneName && clone.m_CachedPtr != IntPtr.Zero);

static GameObject FindLatestFillObject() =>
    GameObject.Created.Last(fill => fill.name == fillObjectName && fill.m_CachedPtr != IntPtr.Zero);

static GameObject[] LiveSeatClones() => GameObject.Instantiated
    .Where(clone => clone.name == cloneName && clone.m_CachedPtr != IntPtr.Zero)
    .ToArray();

static GameObject[] LiveSeatFillObjects() => GameObject.Created
    .Where(fill => fill.name == fillObjectName && fill.m_CachedPtr != IntPtr.Zero)
    .ToArray();

static GameObject FindLiveFillForClone(GameObject clone) => LiveSeatFillObjects().Single(fill =>
    fill.transform.IsChildOf(clone.transform));

static SpriteRenderer GetFillRenderer(GameObject fillObject) =>
    (SpriteRenderer)fillObject.GetComponent(typeof(SpriteRenderer))!;

static SpriteRenderer FindRegional(GameObject clone)
{
    var cluster = (UIElementCluster)clone.GetComponent(typeof(UIElementCluster))!;
    return SingleRenderer(cluster.GetObjects<SpriteRenderer>(), regionalShaderName);
}

static SpriteRenderer SingleRenderer(
    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<SpriteRenderer> renderers,
    string shaderName)
{
    SpriteRenderer? match = null;
    for (var index = 0; index < renderers.Length; index += 1)
    {
        var renderer = renderers[index];
        if (!string.Equals(renderer.sharedMaterial?.shader?.name, shaderName, StringComparison.Ordinal)) continue;
        Assert(match == null, $"Expected exactly one renderer using {shaderName}.");
        match = renderer;
    }
    Assert(match != null, $"Expected one renderer using {shaderName}.");
    return match!;
}

static string[] SuccessLogs(ManualLogSource log, long businessGeneration) =>
    log.InfoMessages.Where(message => message.Contains(
        $"Runtime seat highlight fill bound: businessGeneration={businessGeneration};",
        StringComparison.Ordinal)).ToArray();

static string[] FailureLogs(ManualLogSource log, long businessGeneration, string phase) =>
    log.InfoMessages.Where(message => message.Contains(
            $"Runtime seat highlight fill failed: businessGeneration={businessGeneration};",
            StringComparison.Ordinal)
        && message.Contains($"phase={phase};", StringComparison.Ordinal)).ToArray();

static string Slice(string source, string startMarker, string endMarker)
{
    var start = source.IndexOf(startMarker, StringComparison.Ordinal);
    var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
    Assert(start >= 0 && end > start,
        $"Source audit markers missing: {startMarker} -> {endMarker}");
    return source[start..end];
}

static void AssertColor(Color actual, Color expected, string message)
{
    const float tolerance = 0.0001f;
    Assert(Math.Abs(actual.r - expected.r) <= tolerance
        && Math.Abs(actual.g - expected.g) <= tolerance
        && Math.Abs(actual.b - expected.b) <= tolerance
        && Math.Abs(actual.a - expected.a) <= tolerance, message);
}

static void AssertSpriteGeometry(Sprite actual, Sprite expected, string message)
{
    const float tolerance = 0.001f;
    var actualVertices = actual.vertices;
    var expectedVertices = expected.vertices;
    var actualTriangles = actual.triangles;
    var expectedTriangles = expected.triangles;
    var matches = actualVertices.Length == expectedVertices.Length
        && actualTriangles.Length == expectedTriangles.Length
        && Math.Abs(actual.bounds.center.x - expected.bounds.center.x) <= tolerance
        && Math.Abs(actual.bounds.center.y - expected.bounds.center.y) <= tolerance
        && Math.Abs(actual.bounds.size.x - expected.bounds.size.x) <= tolerance
        && Math.Abs(actual.bounds.size.y - expected.bounds.size.y) <= tolerance;
    for (var index = 0; matches && index < actualVertices.Length; index += 1)
    {
        matches = Math.Abs(actualVertices[index].x - expectedVertices[index].x) <= tolerance
            && Math.Abs(actualVertices[index].y - expectedVertices[index].y) <= tolerance;
    }
    for (var index = 0; matches && index < actualTriangles.Length; index += 1)
    {
        matches = actualTriangles[index] == expectedTriangles[index];
    }
    Assert(matches, message);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
