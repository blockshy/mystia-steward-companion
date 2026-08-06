using System.Reflection;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// Owns one private, standard-sprite table fill per desk claimed by the current rare/normal targets.
/// </summary>
internal static class RuntimeSeatHighlightService
{
    private const float RetryIntervalSeconds = 1.25f;
    private const float ActiveHealthCheckIntervalSeconds = 0.25f;
    private const int MaxSpriteVertexCount = 4096;
    private const int MaxSpriteTriangleIndexCount = 12288;
    private const int OwnedFillTextureSize = 64;
    private const int MaxBindingLogsPerBusiness = 8;
    private const int MaxFailureLogsPerBusiness = 8;
    private const string TileManagerTypeName = "NightScene.Tiles.TileManager";
    private const string InteractableTileTypeName = "NightScene.Tiles.InteractableTile";
    private const string TileBaseTypeName = "UnityEngine.Tilemaps.Tile";
    private const string TilemapTypeName = "UnityEngine.Tilemaps.Tilemap";
    private const string UiElementClusterTypeName = "DEYU.UniversalUISystem.UIElementCluster";
    private const string SpriteRendererTypeName = "UnityEngine.SpriteRenderer";
    private const string OutlineShaderName = "THIZKY/Effects/OutlineBlinkOnly";
    private const string RegionalFillShaderName = "THIZKY/Effects/RegionalHSVFillter";
    private const string StandardSpriteShaderName = "Sprites/Default";
    private const string OwnedFillObjectName = "MystiaStewardCompanion.TargetDeskStandardFill";
    private const string OwnedFillTextureName = "MystiaStewardCompanion.TargetDeskFillTexture";
    private const string OwnedFillSpriteName = "MystiaStewardCompanion.TargetDeskFillSprite";
    private const string OwnedFillMeshTypeName = nameof(SpriteMeshType.Tight);
    private const string OwnedMaterialName = "MystiaStewardCompanion.TargetDeskFillMaterial";

    private static readonly object DesiredRoot = new();
    private static readonly object VisualRoot = new();

    private static ManualLogSource? _log;
    private static RuntimeUiTargetSetSnapshot _desiredTargetSet = RuntimeUiTargetSetSnapshot.Disabled;
    private static long _appliedTargetGeneration;
    private static bool _suspended = true;
    private static string _suspendReason = "night business inactive";
    private static readonly Dictionary<int, ActiveSeatVisual> ActiveVisuals = new();
    private static readonly Dictionary<int, float> NextAttemptAt = new();
    private static long _destroyErrors;
    private static string _status = "disabled";
    private static long _bindingLogBusinessGeneration;
    private static readonly HashSet<string> BindingLogKeys = new(StringComparer.Ordinal);
    private static int _bindingLogCount;
    private static readonly HashSet<string> FailureLogKeys = new(StringComparer.Ordinal);
    private static int _failureLogCount;

    public static string Status
    {
        get
        {
            var desired = Volatile.Read(ref _desiredTargetSet);
            lock (VisualRoot)
            {
                var activeStatus = string.Join(",", ActiveVisuals.Values
                    .OrderBy(active => active.DeskCode)
                    .Select(active => $"session:{active.SessionGeneration}/desk:{active.DeskCode}/claims:{active.Claims}/root:{FormatVector(active.RootPosition)}"));
                return $"{_status}; desired={desired.Generation}/session:{desired.SessionGeneration}/targets:{desired.Targets.Count}; applied={_appliedTargetGeneration}; active={activeStatus}; bindingLog=business:{_bindingLogBusinessGeneration}/keys:{BindingLogKeys.Count}/budget:{_bindingLogCount}/{MaxBindingLogsPerBusiness}; failureLog={_failureLogCount}/{MaxFailureLogsPerBusiness}; suspended={_suspended}; destroyErrors={_destroyErrors}";
            }
        }
    }

    public static void Attach(ManualLogSource log)
    {
        lock (VisualRoot)
        {
            _log = log;
        }
    }

    /// <summary>
    /// Publishes managed desired state only. Unity objects are reconciled later by <see cref="Tick"/>.
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
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        var desired = Volatile.Read(ref _desiredTargetSet);
        lock (VisualRoot)
        {
            if (_suspended) return;
            if (Environment.CurrentManagedThreadId != lifecycle.ThreadId)
            {
                _status = "waiting for Unity main thread";
                return;
            }

            if (_appliedTargetGeneration != desired.Generation)
            {
                ReconcileTargetSetChangeLocked(desired);
                _appliedTargetGeneration = desired.Generation;
            }

            var claims = BuildSeatClaims(desired);
            var desiredEnabled = lifecycle.IsActive
                && desired.SessionGeneration == lifecycle.Generation
                && claims.Count > 0;
            if (!desiredEnabled)
            {
                DestroyAllActiveVisualsLocked();
                _status = HasSeatHighlightTargets(desired)
                    ? lifecycle.IsActive
                        ? "waiting: target belongs to a different night-business session"
                        : "waiting: night business inactive"
                    : "disabled";
                return;
            }

            var now = Time.realtimeSinceStartup;
            foreach (var (deskCode, active) in ActiveVisuals.ToList())
            {
                if (!claims.TryGetValue(deskCode, out var claim)
                    || active.SessionGeneration != desired.SessionGeneration)
                {
                    DestroyActiveVisualLocked(deskCode);
                    NextAttemptAt[deskCode] = 0f;
                    continue;
                }
                active.Claims = claim.Claims;

                if (!TryApplyPulseLocked(active, desired.Palette, now, out var pulseFailure))
                {
                    TryLogFailureLocked(claim, "pulse", pulseFailure);
                    DestroyActiveVisualLocked(deskCode);
                    NextAttemptAt[deskCode] = now + RetryIntervalSeconds;
                    _status = $"unavailable: {NormalizeStatus(pulseFailure)}";
                    continue;
                }
                if (now < active.NextHealthCheckAt) continue;

                if (TryInspectActiveVisualLocked(
                        active,
                        out var containsDestroyedObject,
                        out var healthFailure))
                {
                    active.NextHealthCheckAt = now + ActiveHealthCheckIntervalSeconds;
                    continue;
                }

                DestroyActiveVisualLocked(deskCode);
                TryLogFailureLocked(claim, "health", healthFailure);
                _status = $"unavailable: {NormalizeStatus(healthFailure)}";
                NextAttemptAt[deskCode] = containsDestroyedObject
                    ? 0f
                    : now + RetryIntervalSeconds;
            }

            foreach (var claim in claims.Values.OrderBy(claim => claim.DeskCode))
            {
                if (ActiveVisuals.ContainsKey(claim.DeskCode)
                    || NextAttemptAt.TryGetValue(claim.DeskCode, out var retryAt) && now < retryAt)
                {
                    continue;
                }

                BeginBindingLogTargetLocked(claim);
                if (!TryCreateActiveVisualLocked(desired, claim, out var failure))
                {
                    var latestDesired = Volatile.Read(ref _desiredTargetSet);
                    if (latestDesired.Generation != desired.Generation)
                    {
                        NextAttemptAt[claim.DeskCode] = 0f;
                        _status = "waiting: target changed while creating visual";
                        continue;
                    }

                    TryLogFailureLocked(claim, "create", failure);
                    NextAttemptAt[claim.DeskCode] = now + RetryIntervalSeconds;
                    _status = $"unavailable: {NormalizeStatus(failure)}";
                }
            }

            if (ActiveVisuals.Count > 0)
            {
                _status = $"active:{ActiveVisuals.Count}/{claims.Count}";
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

            NextAttemptAt.Clear();
            _status = HasSeatHighlightTargets(Volatile.Read(ref _desiredTargetSet))
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

            _suspended = false;
            _suspendReason = NormalizeReason(reason);
            NextAttemptAt.Clear();
            _status = HasSeatHighlightTargets(Volatile.Read(ref _desiredTargetSet))
                ? "waiting for main-thread reconcile"
                : "disabled";
        }
    }

    /// <summary>
    /// Drops destroyed IL2CPP wrappers without dereferencing their Unity objects.
    /// </summary>
    public static void Abandon(string reason)
    {
        lock (VisualRoot)
        {
            AbandonAllActiveVisualsLocked();
            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            NextAttemptAt.Clear();
            _status = $"abandoned: {_suspendReason}";
        }
    }

    /// <summary>
    /// Releases the Mod-owned selection clone, renderer object, texture, Sprite, and material on the Unity thread.
    /// </summary>
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

            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            NextAttemptAt.Clear();
            _status = $"disposed: {_suspendReason}";
        }
    }

    private static bool TryCreateActiveVisualLocked(
        RuntimeUiTargetSetSnapshot targetSet,
        SeatHighlightClaim target,
        out string failure)
    {
        GameObject? selectionClone = null;
        GameObject? fillObject = null;
        Texture2D? fillTexture = null;
        Sprite? fillSprite = null;
        Material? fillMaterial = null;
        try
        {
            var tileManagerType = RuntimeReflectionUtility.FindType(TileManagerTypeName);
            var tileType = RuntimeReflectionUtility.FindType(InteractableTileTypeName);
            if (tileManagerType == null || tileType == null)
            {
                failure = "required BepInEx 783 interop types are not loaded";
                return false;
            }

            var singletonBase = tileManagerType.BaseType;
            var singletonArguments = singletonBase?.IsGenericType == true
                ? singletonBase.GetGenericArguments()
                : Type.EmptyTypes;
            if (singletonBase == null
                || !singletonBase.IsGenericType
                || singletonBase.GetGenericTypeDefinition().FullName != "DEYU.Singletons.MonoSingleton`1"
                || singletonArguments.Length != 1
                || singletonArguments[0] != tileManagerType)
            {
                failure = "TileManager does not directly inherit MonoSingleton<TileManager>";
                return false;
            }

            var instanceProperty = FindDeclaredStaticProperty(singletonBase, "Instance");
            if (instanceProperty?.PropertyType != tileManagerType)
            {
                failure = "MonoSingleton<TileManager>.Instance has an unexpected return type";
                return false;
            }
            var tileManager = instanceProperty.GetValue(null);
            if (tileManager == null || !IsLiveUnityObject(tileManager))
            {
                failure = "TileManager.Instance is unavailable";
                return false;
            }

            var getCustomerDesk = FindDeclaredInstanceMethod(
                tileManagerType,
                "GetCustomerDesk",
                typeof(int),
                typeof(Vector3Int).MakeByRefType());
            var getCellCenterWorld = FindDeclaredInstanceMethod(
                tileManagerType,
                "GetCellCenterWorld",
                typeof(Vector3Int));
            if (getCustomerDesk == null
                || getCustomerDesk.ReturnType.FullName != InteractableTileTypeName
                || getCellCenterWorld == null
                || getCellCenterWorld.ReturnType != typeof(Vector3))
            {
                failure = "TileManager desk lookup methods do not match the verified declarations";
                return false;
            }

            object?[] deskArguments = { target.DeskCode, default(Vector3Int) };
            var tile = getCustomerDesk.Invoke(tileManager, deskArguments);
            if (tile == null
                || !tileType.IsInstanceOfType(tile)
                || !IsLiveUnityObject(tile)
                || deskArguments[1] is not Vector3Int cellPosition)
            {
                failure = $"desk {target.DeskCode} is not present in TileManager";
                return false;
            }

            var coordinate = getCellCenterWorld.Invoke(tileManager, new object?[] { cellPosition });
            if (coordinate is not Vector3 cellCenterWorld || !IsFinite(cellCenterWorld))
            {
                failure = "TileManager.GetCellCenterWorld returned an invalid value";
                return false;
            }

            var tileBaseType = tileType.BaseType;
            if (tileBaseType?.FullName != TileBaseTypeName)
            {
                failure = "InteractableTile does not directly inherit UnityEngine.Tilemaps.Tile";
                return false;
            }
            var tileSpriteProperty = FindDeclaredInstanceProperty(tileBaseType, "sprite");
            if (tileSpriteProperty?.PropertyType != typeof(Sprite))
            {
                failure = "customer desk sprite property does not match UnityEngine.Sprite";
                return false;
            }
            var tileSprite = tileSpriteProperty.GetValue(tile) as Sprite;
            if (tileSprite == null
                || tileSprite.GetType().FullName != "UnityEngine.Sprite"
                || !IsLiveUnityObject(tileSprite))
            {
                failure = "customer desk sprite is unavailable";
                return false;
            }

            var interactableProperty = FindDeclaredInstanceProperty(tileManagerType, "interactable");
            if (interactableProperty?.PropertyType.FullName != TilemapTypeName)
            {
                failure = "TileManager.interactable does not match UnityEngine.Tilemaps.Tilemap";
                return false;
            }
            var interactable = interactableProperty.GetValue(tileManager);
            if (interactable == null || !IsLiveUnityObject(interactable))
            {
                failure = "TileManager.interactable is unavailable";
                return false;
            }

            var getTransformMatrix = FindDeclaredInstanceMethod(
                interactableProperty.PropertyType,
                "GetTransformMatrix",
                typeof(Vector3Int));
            if (getTransformMatrix == null || getTransformMatrix.ReturnType != typeof(Matrix4x4))
            {
                failure = "Tilemap.GetTransformMatrix does not match the verified declaration";
                return false;
            }
            if (getTransformMatrix.Invoke(interactable, new object?[] { cellPosition }) is not Matrix4x4 cellTransform
                || !IsFinite(cellTransform.m03)
                || !IsFinite(cellTransform.m13)
                || !IsFinite(cellTransform.m23))
            {
                failure = "Tilemap.GetTransformMatrix returned an invalid value";
                return false;
            }

            var onSelectionProperty = FindDeclaredInstanceProperty(tileManagerType, "onSelection");
            if (onSelectionProperty?.PropertyType.FullName != UiElementClusterTypeName)
            {
                failure = "TileManager.onSelection does not match UIElementCluster";
                return false;
            }
            var onSelection = onSelectionProperty.GetValue(tileManager);
            if (onSelection is not Component selectionComponent || !IsLiveUnityObject(selectionComponent))
            {
                failure = "TileManager.onSelection is unavailable";
                return false;
            }

            var getSelectionRenderersDefinition = FindDeclaredGenericArrayInstanceMethod(
                onSelectionProperty.PropertyType,
                "GetObjects");
            if (getSelectionRenderersDefinition == null)
            {
                failure = "UIElementCluster renderer array accessor does not match the verified declaration";
                return false;
            }
            var selectionRoot = selectionComponent.transform;
            var selectionSourceObject = selectionComponent.gameObject;
            if (selectionRoot == null
                || selectionSourceObject == null
                || !IsLiveUnityObject(selectionRoot)
                || !IsLiveUnityObject(selectionSourceObject))
            {
                failure = "TileManager.onSelection root is unavailable";
                return false;
            }
            if (selectionRoot.parent != null)
            {
                failure = "TileManager.onSelection is not the verified scene-root visual";
                return false;
            }

            var desiredRootPosition = new Vector3(
                cellCenterWorld.x + cellTransform.m03,
                cellCenterWorld.y + cellTransform.m13,
                cellCenterWorld.z + cellTransform.m23);
            if (!IsFinite(desiredRootPosition))
            {
                failure = "customer desk selection position is invalid";
                return false;
            }

            selectionClone = UnityEngine.Object.Instantiate(selectionSourceObject);
            if (selectionClone == null || !IsLiveUnityObject(selectionClone))
            {
                failure = "failed to clone the game selection visual";
                return false;
            }

            selectionClone.name = "MystiaStewardCompanion.TargetDeskFill";
            selectionClone.SetActive(true);
            if (!selectionClone.activeInHierarchy)
            {
                failure = "cloned selection visual is inactive in the scene hierarchy";
                return false;
            }

            var clonedSelectionComponent = selectionClone.GetComponent(Il2CppType.From(onSelectionProperty.PropertyType));
            var clonedSelection = RuntimeReflectionUtility.TryCastRuntimeObject(
                clonedSelectionComponent,
                UiElementClusterTypeName);
            if (clonedSelection is not Component clonedSelectionRoot
                || !IsLiveUnityObject(clonedSelectionRoot))
            {
                failure = "cloned selection UIElementCluster is unavailable";
                return false;
            }
            if (clonedSelectionRoot.transform.parent != null)
            {
                failure = "cloned selection fill is not a scene-root visual";
                return false;
            }

            clonedSelectionRoot.transform.position = desiredRootPosition;
            if (!TryBindSelectionRenderers(
                    clonedSelection,
                    getSelectionRenderersDefinition,
                    clonedSelectionRoot.transform,
                    tileSprite,
                    out var outlineRenderer,
                    out var regionalRenderer,
                    out failure))
            {
                return false;
            }

            if (regionalRenderer.drawMode != SpriteDrawMode.Simple)
            {
                failure = $"private RegionalHSVFillter template draw mode is not Simple: {regionalRenderer.drawMode}";
                return false;
            }

            outlineRenderer.enabled = false;
            regionalRenderer.enabled = false;

            fillObject = CreateOwnedSpriteRendererGameObject(OwnedFillObjectName);
            if (fillObject == null || !IsLiveUnityObject(fillObject))
            {
                failure = "failed to create the Mod-owned standard SpriteRenderer object";
                return false;
            }
            fillObject.SetActive(false);

            var fillComponent = fillObject.GetComponent(Il2CppType.From(typeof(SpriteRenderer)));
            if (fillComponent is not Component queriedFillComponent
                || !IsLiveUnityObject(queriedFillComponent))
            {
                failure = "Mod-owned fill object typed query returned no live Component wrapper";
                return false;
            }

            var exactFillComponent = RuntimeReflectionUtility.TryCastRuntimeObject(
                queriedFillComponent,
                SpriteRendererTypeName);
            if (exactFillComponent is not SpriteRenderer fillRenderer
                || !IsLiveUnityObject(fillRenderer))
            {
                failure = "Mod-owned fill Component wrapper cannot exact-cast to a live SpriteRenderer";
                return false;
            }
            if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(
                    queriedFillComponent,
                    out var queriedFillPointer)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                    fillRenderer,
                    out var exactFillPointer)
                || queriedFillPointer != exactFillPointer)
            {
                failure = "Mod-owned fill SpriteRenderer cast changed native component identity";
                return false;
            }
            if (!TryReadNativeClassName(fillRenderer, out var fillClassName)
                || !string.Equals(fillClassName, SpriteRendererTypeName, StringComparison.Ordinal))
            {
                failure = "Mod-owned fill component native class is not exact UnityEngine.SpriteRenderer";
                return false;
            }
            if (!TryReadSameNativeObject(
                    fillObject,
                    fillRenderer.gameObject,
                    out var fillOwnsComponent)
                || !fillOwnsComponent)
            {
                failure = "Mod-owned fill SpriteRenderer belongs to a different GameObject";
                return false;
            }

            fillRenderer.enabled = false;
            var regionalTransform = regionalRenderer.transform;
            var fillTransform = fillRenderer.transform;
            if (regionalTransform == null
                || fillTransform == null
                || !IsLiveUnityObject(regionalTransform)
                || !IsLiveUnityObject(fillTransform))
            {
                failure = "Regional template or Mod-owned fill transform is unavailable";
                return false;
            }

            fillTransform.parent = regionalTransform.parent;
            fillTransform.localPosition = regionalTransform.localPosition;
            fillTransform.localRotation = regionalTransform.localRotation;
            fillTransform.localScale = regionalTransform.localScale;
            fillObject.layer = regionalRenderer.gameObject.layer;
            fillRenderer.sortingLayerID = regionalRenderer.sortingLayerID;
            fillRenderer.sortingOrder = regionalRenderer.sortingOrder;
            fillRenderer.drawMode = regionalRenderer.drawMode;
            fillRenderer.flipX = regionalRenderer.flipX;
            fillRenderer.flipY = regionalRenderer.flipY;
            if (!TryCreateOwnedWhiteSprite(
                    tileSprite,
                    out fillSprite,
                    out fillTexture,
                    out var sourceVertexCount,
                    out var sourceTriangleIndexCount,
                    out failure))
            {
                return false;
            }
            fillRenderer.sprite = fillSprite;
            fillRenderer.color = RuntimeTargetHighlightStyle.BuildSeatFillPulseColor(
                target.Claims,
                targetSet.Palette,
                Time.realtimeSinceStartup);

            var defaultFillMaterial = fillRenderer.sharedMaterial;
            var defaultFillShader = defaultFillMaterial?.shader;
            if (defaultFillMaterial == null
                || defaultFillShader == null
                || !IsLiveUnityObject(defaultFillMaterial)
                || !IsLiveUnityObject(defaultFillShader)
                || !string.Equals(defaultFillShader.name, StandardSpriteShaderName, StringComparison.Ordinal)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                    defaultFillMaterial,
                    out var defaultMaterialPointer))
            {
                failure = "new SpriteRenderer does not expose the verified Unity default sprite material";
                return false;
            }

            var instantiatedFillMaterial = fillRenderer.material;
            if (instantiatedFillMaterial == null
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(instantiatedFillMaterial, out var ownedMaterialPointer)
                || defaultMaterialPointer == ownedMaterialPointer)
            {
                failure = "failed to instantiate a distinct Mod-owned material from the default SpriteRenderer material";
                return false;
            }
            fillMaterial = instantiatedFillMaterial;
            if (!IsLiveUnityObject(fillMaterial))
            {
                failure = "instantiated Mod-owned material is not a live Unity object";
                return false;
            }
            fillMaterial.name = OwnedMaterialName;
            var ownedShader = fillMaterial.shader;
            if (ownedShader == null
                || !IsLiveUnityObject(ownedShader)
                || !TryReadSameNativeObject(defaultFillShader, ownedShader, out var sameDefaultShader)
                || !sameDefaultShader
                || !string.Equals(ownedShader.name, StandardSpriteShaderName, StringComparison.Ordinal))
            {
                failure = "Mod-owned fill material does not retain the Unity default sprite shader";
                return false;
            }

            fillRenderer.enabled = true;
            fillObject.SetActive(true);

            var fillLayer = fillRenderer.gameObject.layer;
            var fillSortingLayerId = fillRenderer.sortingLayerID;
            var fillSortingOrder = fillRenderer.sortingOrder;
            var fillLocalPosition = fillTransform.localPosition;
            var fillLocalRotation = fillTransform.localRotation;
            var fillLocalScale = fillTransform.localScale;
            var fillDrawMode = fillRenderer.drawMode;
            var fillFlipX = fillRenderer.flipX;
            var fillFlipY = fillRenderer.flipY;
            if (!TryValidateBoundFill(
                    selectionClone,
                    outlineRenderer,
                    regionalRenderer,
                    fillObject,
                    fillRenderer,
                    fillMaterial,
                    tileSprite,
                    fillSprite,
                    fillTexture,
                    desiredRootPosition,
                    fillLayer,
                    fillSortingLayerId,
                    fillSortingOrder,
                    fillLocalPosition,
                    fillLocalRotation,
                    fillLocalScale,
                    fillDrawMode,
                    fillFlipX,
                    fillFlipY,
                    out var fillBounds,
                    out failure))
            {
                return false;
            }

            var nextHealthCheckAt = Time.realtimeSinceStartup;

            // UpdateTargets runs on the local API listener thread and owns DesiredRoot. Close
            // the final recheck/publication window so a superseded target can never acquire
            // the locally owned Unity resources or emit successful binding evidence.
            lock (DesiredRoot)
            {
                var latestTargetSet = Volatile.Read(ref _desiredTargetSet);
                var latestClaims = BuildSeatClaims(latestTargetSet);
                if (latestTargetSet.Generation != targetSet.Generation
                    || !latestClaims.TryGetValue(target.DeskCode, out var latestTarget)
                    || latestTarget.Claims != target.Claims)
                {
                    failure = "target changed while creating seat fill";
                    return false;
                }

                ActiveVisuals[target.DeskCode] = new ActiveSeatVisual(
                    selectionClone,
                    outlineRenderer,
                    regionalRenderer,
                    fillObject,
                    fillRenderer,
                    fillMaterial,
                    tileSprite,
                    fillSprite,
                    fillTexture,
                    target.SessionGeneration,
                    target.DeskCode,
                    target.Claims,
                    desiredRootPosition,
                    fillLayer,
                    fillSortingLayerId,
                    fillSortingOrder,
                    fillLocalPosition,
                    fillLocalRotation,
                    fillLocalScale,
                    fillDrawMode,
                    fillFlipX,
                    fillFlipY,
                    nextHealthCheckAt);
                selectionClone = null;
                fillObject = null;
                fillTexture = null;
                fillSprite = null;
                fillMaterial = null;
            }

            failure = "";
            TryLogFillBindingLocked(
                target,
                sourceVertexCount,
                sourceTriangleIndexCount,
                fillBounds);
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
            if (fillObject != null)
            {
                SafeDestroyGameObject(fillObject);
            }
            if (selectionClone != null)
            {
                SafeDestroyGameObject(selectionClone);
            }
            if (fillSprite != null)
            {
                SafeDestroySprite(fillSprite);
            }
            if (fillMaterial != null)
            {
                SafeDestroyMaterial(fillMaterial);
            }
            if (fillTexture != null)
            {
                SafeDestroyTexture(fillTexture);
            }
        }
    }

    private static GameObject CreateOwnedSpriteRendererGameObject(string name)
    {
        var componentTypes = new Il2CppReferenceArray<Il2CppSystem.Type>(1);
        componentTypes[0] = Il2CppType.From(typeof(SpriteRenderer));
        return new GameObject(name, componentTypes);
    }

    private static bool TryCreateOwnedWhiteSprite(
        Sprite sourceSprite,
        out Sprite ownedSprite,
        out Texture2D ownedTexture,
        out int sourceVertexCount,
        out int sourceTriangleIndexCount,
        out string failure)
    {
        ownedSprite = null!;
        ownedTexture = null!;
        sourceVertexCount = 0;
        sourceTriangleIndexCount = 0;
        Texture2D? candidateTexture = null;
        Sprite? candidateSprite = null;
        try
        {
            var sourceVertices = sourceSprite.vertices;
            var sourceTriangles = sourceSprite.triangles;
            if (!TryValidateSpriteGeometry(
                    sourceVertices,
                    sourceTriangles,
                    out failure))
            {
                return false;
            }

            var sourceBounds = sourceSprite.bounds;
            if (!IsFinite(sourceBounds.center)
                || !IsFinite(sourceBounds.size)
                || !HasNonZeroSize(sourceBounds.size))
            {
                failure = "customer desk sprite has invalid local bounds";
                return false;
            }

            var sourceRect = sourceSprite.rect;
            var sourcePivot = sourceSprite.pivot;
            var sourcePixelsPerUnit = sourceSprite.pixelsPerUnit;
            var sourceTexture = sourceSprite.texture;
            const int whiteWidth = OwnedFillTextureSize;
            const int whiteHeight = OwnedFillTextureSize;
            if (!IsFinite(sourceRect.width)
                || !IsFinite(sourceRect.height)
                || sourceRect.width <= 0f
                || sourceRect.height <= 0f
                || !IsFinite(sourcePivot.x)
                || !IsFinite(sourcePivot.y)
                || !IsFinite(sourcePixelsPerUnit)
                || sourcePixelsPerUnit <= 0f
                || sourceTexture == null
                || !IsLiveUnityObject(sourceTexture))
            {
                failure = "customer desk sprite has invalid geometry metadata";
                return false;
            }

            var scale = Math.Min(
                whiteWidth / sourceRect.width,
                whiteHeight / sourceRect.height);
            var offset = new Vector2(
                (whiteWidth - (sourceRect.width * scale)) * 0.5f,
                (whiteHeight - (sourceRect.height * scale)) * 0.5f);
            var destinationPixelsPerUnit = sourcePixelsPerUnit * scale;
            var destinationPivotPixels = new Vector2(
                offset.x + (sourcePivot.x * scale),
                offset.y + (sourcePivot.y * scale));
            var destinationPivot = new Vector2(
                destinationPivotPixels.x / whiteWidth,
                destinationPivotPixels.y / whiteHeight);
            if (!IsFinite(scale)
                || scale <= 0f
                || !IsFinite(destinationPixelsPerUnit)
                || destinationPixelsPerUnit <= 0f
                || !IsFinite(destinationPivot.x)
                || !IsFinite(destinationPivot.y))
            {
                failure = "white seat sprite coordinate mapping is invalid";
                return false;
            }

            var destinationVertices = new Il2CppStructArray<Vector2>(sourceVertices.Length);
            const float pixelTolerance = 0.001f;
            for (var index = 0; index < sourceVertices.Length; index += 1)
            {
                var sourceVertex = sourceVertices[index];
                var mappedX = offset.x
                    + ((sourceVertex.x * sourcePixelsPerUnit + sourcePivot.x) * scale);
                var mappedY = offset.y
                    + ((sourceVertex.y * sourcePixelsPerUnit + sourcePivot.y) * scale);
                if (!IsFinite(mappedX)
                    || !IsFinite(mappedY)
                    || mappedX < -pixelTolerance
                    || mappedY < -pixelTolerance
                    || mappedX > whiteWidth + pixelTolerance
                    || mappedY > whiteHeight + pixelTolerance)
                {
                    failure = "customer desk sprite geometry maps outside the Mod-owned white texture rect";
                    return false;
                }
                destinationVertices[index] = new Vector2(
                    Math.Clamp(mappedX, 0f, whiteWidth),
                    Math.Clamp(mappedY, 0f, whiteHeight));
            }

            candidateTexture = new Texture2D(
                OwnedFillTextureSize,
                OwnedFillTextureSize,
                TextureFormat.RGBA32,
                false,
                false);
            if (candidateTexture == null
                || candidateTexture.GetType().FullName != "UnityEngine.Texture2D"
                || !IsLiveUnityObject(candidateTexture)
                || candidateTexture.width != OwnedFillTextureSize
                || candidateTexture.height != OwnedFillTextureSize
                || candidateTexture.format != TextureFormat.RGBA32
                || candidateTexture.mipmapCount != 1
                || !candidateTexture.isReadable)
            {
                failure = "failed to create the exact Mod-owned white seat texture";
                return false;
            }
            candidateTexture.name = OwnedFillTextureName;

            var whitePixels = new Il2CppStructArray<Color32>(
                OwnedFillTextureSize * OwnedFillTextureSize);
            var opaqueWhite = new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);
            for (var index = 0; index < whitePixels.Length; index += 1)
            {
                whitePixels[index] = opaqueWhite;
            }
            candidateTexture.SetPixels32(whitePixels);
            candidateTexture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            if (!IsLiveUnityObject(candidateTexture)
                || candidateTexture.width != OwnedFillTextureSize
                || candidateTexture.height != OwnedFillTextureSize
                || candidateTexture.format != TextureFormat.RGBA32
                || candidateTexture.mipmapCount != 1
                || !candidateTexture.isReadable)
            {
                failure = "Mod-owned white seat texture changed identity or format after upload";
                return false;
            }

            candidateSprite = Sprite.Create(
                candidateTexture,
                new Rect(0f, 0f, whiteWidth, whiteHeight),
                destinationPivot,
                destinationPixelsPerUnit,
                0u,
                SpriteMeshType.Tight);
            if (candidateSprite == null || !IsLiveUnityObject(candidateSprite))
            {
                failure = "failed to create the Mod-owned white seat sprite";
                return false;
            }
            candidateSprite.name = OwnedFillSpriteName;
            candidateSprite.OverrideGeometry(destinationVertices, sourceTriangles);

            var spriteTexture = candidateSprite.texture;
            if (spriteTexture == null
                || !IsLiveUnityObject(spriteTexture)
                || !TryReadSameNativeObject(
                    candidateTexture,
                    spriteTexture,
                    out var sameOwnedTexture)
                || !sameOwnedTexture
                || !TryReadSameNativeObject(
                    sourceTexture,
                    candidateTexture,
                    out var sharesSourceTexture)
                || sharesSourceTexture
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                    sourceSprite,
                    out var sourceSpritePointer)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                    candidateSprite,
                    out var candidateSpritePointer)
                || sourceSpritePointer == candidateSpritePointer)
            {
                failure = "Mod-owned seat sprite does not use the exact owned white texture or has invalid identity";
                return false;
            }

            var candidateVertices = candidateSprite.vertices;
            var candidateTriangles = candidateSprite.triangles;
            if (!HasSameSpriteGeometry(
                    sourceVertices,
                    sourceTriangles,
                    candidateVertices,
                    candidateTriangles)
                || !Approximately(candidateSprite.bounds, sourceBounds))
            {
                failure = "Mod-owned white seat sprite did not retain the exact source geometry";
                return false;
            }

            ownedSprite = candidateSprite;
            ownedTexture = candidateTexture;
            sourceVertexCount = sourceVertices.Length;
            sourceTriangleIndexCount = sourceTriangles.Length;
            candidateSprite = null;
            candidateTexture = null;
            failure = "";
            return true;
        }
        catch (Exception ex)
        {
            failure = $"white seat sprite creation failed: {NormalizeStatus(ex.GetBaseException().Message)}";
            return false;
        }
        finally
        {
            if (candidateSprite != null)
            {
                SafeDestroySprite(candidateSprite);
            }
            if (candidateTexture != null)
            {
                SafeDestroyTexture(candidateTexture);
            }
        }
    }

    private static bool TryValidateSpriteGeometry(
        Il2CppStructArray<Vector2> vertices,
        Il2CppStructArray<ushort> triangles,
        out string failure)
    {
        if (vertices == null || vertices.Length < 3)
        {
            failure = "customer desk sprite has fewer than three geometry vertices";
            return false;
        }
        if (vertices.Length > MaxSpriteVertexCount)
        {
            failure = $"customer desk sprite exceeds the {MaxSpriteVertexCount} vertex safety limit";
            return false;
        }
        if (triangles == null
            || triangles.Length < 3
            || triangles.Length % 3 != 0
            || triangles.Length > MaxSpriteTriangleIndexCount)
        {
            failure = "customer desk sprite has an invalid triangle index array";
            return false;
        }

        for (var index = 0; index < vertices.Length; index += 1)
        {
            var vertex = vertices[index];
            if (!IsFinite(vertex.x) || !IsFinite(vertex.y))
            {
                failure = "customer desk sprite contains a non-finite geometry vertex";
                return false;
            }
        }
        for (var index = 0; index < triangles.Length; index += 1)
        {
            if (triangles[index] >= vertices.Length)
            {
                failure = "customer desk sprite triangle index exceeds the vertex array";
                return false;
            }
        }

        failure = "";
        return true;
    }

    private static bool HasSameSpriteGeometry(
        Il2CppStructArray<Vector2> sourceVertices,
        Il2CppStructArray<ushort> sourceTriangles,
        Il2CppStructArray<Vector2> candidateVertices,
        Il2CppStructArray<ushort> candidateTriangles)
    {
        if (candidateVertices == null
            || candidateTriangles == null
            || sourceVertices.Length != candidateVertices.Length
            || sourceTriangles.Length != candidateTriangles.Length)
        {
            return false;
        }
        for (var index = 0; index < sourceVertices.Length; index += 1)
        {
            if (!Approximately(sourceVertices[index], candidateVertices[index])) return false;
        }
        for (var index = 0; index < sourceTriangles.Length; index += 1)
        {
            if (sourceTriangles[index] != candidateTriangles[index]) return false;
        }
        return true;
    }

    private static bool TryBindSelectionRenderers(
        object selection,
        MethodInfo getAllDefinition,
        Transform selectionRoot,
        Sprite sourceSprite,
        out SpriteRenderer outline,
        out SpriteRenderer regional,
        out string failure)
    {
        outline = null!;
        regional = null!;
        var getAll = getAllDefinition.MakeGenericMethod(typeof(SpriteRenderer));
        if (!typeof(Il2CppArrayBase<SpriteRenderer>).IsAssignableFrom(getAll.ReturnType))
        {
            failure = "UIElementCluster SpriteRenderer array accessor has an unexpected closed return type";
            return false;
        }

        if (getAll.Invoke(selection, Array.Empty<object?>()) is not Il2CppArrayBase<SpriteRenderer> sourceRenderers)
        {
            failure = "cloned selection has no SpriteRenderer array";
            return false;
        }
        if (sourceRenderers.Length != 2)
        {
            failure = $"cloned selection must contain exactly two SpriteRenderers, got {sourceRenderers.Length}";
            return false;
        }

        var identities = new HashSet<nint>();
        for (var index = 0; index < sourceRenderers.Length; index += 1)
        {
            var renderer = sourceRenderers[index];
            if (renderer == null
                || !IsLiveUnityObject(renderer)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(renderer, out var rendererPointer)
                || !identities.Add(rendererPointer))
            {
                failure = $"cloned selection SpriteRenderer {index} is unavailable or duplicated";
                return false;
            }
            var rendererTransform = renderer.transform;
            if (rendererTransform == null
                || !IsLiveUnityObject(rendererTransform)
                || !TryReadSameNativeObject(selectionRoot, rendererTransform, out var sameTransform)
                || (!sameTransform && !rendererTransform.IsChildOf(selectionRoot)))
            {
                failure = $"cloned selection SpriteRenderer {index} is outside the cloned hierarchy";
                return false;
            }

            var material = renderer.sharedMaterial;
            var shader = material?.shader;
            if (material == null
                || shader == null
                || !IsLiveUnityObject(material)
                || !IsLiveUnityObject(shader))
            {
                failure = $"cloned selection SpriteRenderer {index} has no live material shader";
                return false;
            }

            if (string.Equals(shader.name, OutlineShaderName, StringComparison.Ordinal))
            {
                if (outline != null)
                {
                    failure = $"cloned selection has duplicate {OutlineShaderName} renderers";
                    return false;
                }
                outline = renderer;
            }
            else if (string.Equals(shader.name, RegionalFillShaderName, StringComparison.Ordinal))
            {
                if (regional != null)
                {
                    failure = $"cloned selection has duplicate {RegionalFillShaderName} renderers";
                    return false;
                }
                regional = renderer;
            }
            else
            {
                failure = $"cloned selection renderer shader is not one of the two verified seat shaders: {NormalizeStatus(shader.name)}";
                return false;
            }

            renderer.sprite = sourceSprite;
        }

        if (outline == null || regional == null)
        {
            failure = "cloned selection does not contain exactly one OutlineBlinkOnly and one RegionalHSVFillter renderer";
            return false;
        }

        failure = "";
        return true;
    }

    private static bool TryApplyPulseLocked(
        ActiveSeatVisual active,
        RuntimeTargetHighlightPalette palette,
        float realtimeSinceStartup,
        out string failure)
    {
        if (!IsLiveUnityObject(active.FillRenderer))
        {
            failure = "active seat fill renderer is unavailable";
            return false;
        }

        try
        {
            active.FillRenderer.color = RuntimeTargetHighlightStyle.BuildSeatFillPulseColor(
                active.Claims,
                palette,
                realtimeSinceStartup);
            failure = "";
            return true;
        }
        catch (Exception ex)
        {
            failure = $"seat fill pulse failed: {NormalizeStatus(ex.GetBaseException().Message)}";
            return false;
        }
    }

    private static bool TryInspectActiveVisualLocked(
        ActiveSeatVisual active,
        out bool containsDestroyedObject,
        out string failure)
    {
        containsDestroyedObject = false;
        try
        {
            if (!IsLiveUnityObject(active.SelectionClone)
                || !IsLiveUnityObject(active.OutlineRenderer)
                || !IsLiveUnityObject(active.RegionalRenderer)
                || !IsLiveUnityObject(active.FillObject)
                || !IsLiveUnityObject(active.FillRenderer)
                || !IsLiveUnityObject(active.FillMaterial)
                || !IsLiveUnityObject(active.SourceSprite)
                || !IsLiveUnityObject(active.FillSprite)
                || !IsLiveUnityObject(active.FillTexture))
            {
                containsDestroyedObject = true;
                failure = "active seat fill contains a destroyed Unity object";
                return false;
            }
            return TryValidateBoundFill(
                active.SelectionClone,
                active.OutlineRenderer,
                active.RegionalRenderer,
                active.FillObject,
                active.FillRenderer,
                active.FillMaterial,
                active.SourceSprite,
                active.FillSprite,
                active.FillTexture,
                active.RootPosition,
                active.FillLayer,
                active.FillSortingLayerId,
                active.FillSortingOrder,
                active.FillLocalPosition,
                active.FillLocalRotation,
                active.FillLocalScale,
                active.FillDrawMode,
                active.FillFlipX,
                active.FillFlipY,
                out _,
                out failure);
        }
        catch (Exception ex)
        {
            failure = $"seat fill inspection failed: {NormalizeStatus(ex.GetBaseException().Message)}";
            return false;
        }
    }

    private static bool TryValidateBoundFill(
        GameObject clone,
        SpriteRenderer outline,
        SpriteRenderer regional,
        GameObject fillObject,
        SpriteRenderer fill,
        Material ownedMaterial,
        Sprite sourceSprite,
        Sprite fillSprite,
        Texture2D fillTexture,
        Vector3 expectedRootPosition,
        int expectedLayer,
        int expectedSortingLayerId,
        int expectedSortingOrder,
        Vector3 expectedLocalPosition,
        Quaternion expectedLocalRotation,
        Vector3 expectedLocalScale,
        SpriteDrawMode expectedDrawMode,
        bool expectedFlipX,
        bool expectedFlipY,
        out Bounds bounds,
        out string failure)
    {
        bounds = default;
        if (!IsLiveUnityObject(clone)
            || !IsLiveUnityObject(outline)
            || !IsLiveUnityObject(regional)
            || !IsLiveUnityObject(fillObject)
            || !IsLiveUnityObject(fill)
            || !IsLiveUnityObject(ownedMaterial)
            || !IsLiveUnityObject(sourceSprite)
            || !IsLiveUnityObject(fillSprite)
            || !IsLiveUnityObject(fillTexture))
        {
            failure = "active seat fill contains a destroyed Unity object";
            return false;
        }
        if (!clone.activeInHierarchy)
        {
            failure = "cloned selection fill is inactive in the scene hierarchy";
            return false;
        }
        if (clone.transform.parent != null)
        {
            failure = "cloned selection fill was reparented away from the scene root";
            return false;
        }
        if (!Approximately(clone.transform.position, expectedRootPosition))
        {
            failure = "cloned selection fill moved away from the target desk";
            return false;
        }
        if (!TryValidateDisabledTemplateRenderer(
                clone,
                outline,
                OutlineShaderName,
                sourceSprite,
                out failure)
            || !TryValidateDisabledTemplateRenderer(
                clone,
                regional,
                RegionalFillShaderName,
                sourceSprite,
                out failure))
        {
            return false;
        }

        var fillTransform = fill.transform;
        var regionalTransform = regional.transform;
        if (fillTransform == null
            || regionalTransform == null
            || !IsLiveUnityObject(fillTransform)
            || !IsLiveUnityObject(regionalTransform)
            || !TryReadSameNativeObject(fillObject, fill.gameObject, out var fillOwnsComponent)
            || !fillOwnsComponent
            || !fillObject.activeInHierarchy
            || !fill.enabled)
        {
            failure = "Mod-owned seat fill renderer is not active on its exact object";
            return false;
        }
        if (!TryReadSameParent(fillTransform.parent, regionalTransform.parent, out var sameParent)
            || !sameParent
            || !Approximately(fillTransform.localPosition, regionalTransform.localPosition)
            || !Approximately(fillTransform.localRotation, regionalTransform.localRotation)
            || !Approximately(fillTransform.localScale, regionalTransform.localScale)
            || !Approximately(fillTransform.localPosition, expectedLocalPosition)
            || !Approximately(fillTransform.localRotation, expectedLocalRotation)
            || !Approximately(fillTransform.localScale, expectedLocalScale))
        {
            failure = "Mod-owned seat fill no longer matches the RegionalHSVFillter parent or local transform";
            return false;
        }
        if (fillObject.layer != expectedLayer
            || fill.sortingLayerID != expectedSortingLayerId
            || fill.sortingOrder != expectedSortingOrder
            || fill.drawMode != expectedDrawMode
            || fill.flipX != expectedFlipX
            || fill.flipY != expectedFlipY
            || fillObject.layer != regional.gameObject.layer
            || fill.sortingLayerID != regional.sortingLayerID
            || fill.sortingOrder != regional.sortingOrder
            || fill.drawMode != regional.drawMode
            || fill.flipX != regional.flipX
            || fill.flipY != regional.flipY)
        {
            failure = "Mod-owned seat fill no longer matches the RegionalHSVFillter render settings";
            return false;
        }

        var rendererSprite = fill.sprite;
        if (rendererSprite == null
            || !IsLiveUnityObject(rendererSprite)
            || !TryReadSameNativeObject(fillSprite, rendererSprite, out var sameFillSprite)
            || !sameFillSprite
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                sourceSprite,
                out var sourceSpritePointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(
                fillSprite,
                out var fillSpritePointer)
            || sourceSpritePointer == fillSpritePointer)
        {
            failure = "Mod-owned seat fill no longer binds its distinct white sprite";
            return false;
        }
        var rendererTexture = fillSprite.texture;
        if (rendererTexture == null
            || !IsLiveUnityObject(rendererTexture)
            || fillTexture.GetType().FullName != "UnityEngine.Texture2D"
            || fillTexture.width != OwnedFillTextureSize
            || fillTexture.height != OwnedFillTextureSize
            || fillTexture.format != TextureFormat.RGBA32
            || fillTexture.mipmapCount != 1
            || !fillTexture.isReadable
            || !TryReadSameNativeObject(fillTexture, rendererTexture, out var sameFillTexture)
            || !sameFillTexture
            || !Approximately(fillSprite.bounds, sourceSprite.bounds))
        {
            failure = "Mod-owned seat fill sprite no longer retains its exact white texture and source bounds";
            return false;
        }
        var sourceTexture = sourceSprite.texture;
        if (sourceTexture == null
            || !IsLiveUnityObject(sourceTexture)
            || !TryReadSameNativeObject(sourceTexture, fillTexture, out var sharesSourceTexture)
            || sharesSourceTexture)
        {
            failure = "Mod-owned seat fill texture is unavailable or aliases the source desk texture";
            return false;
        }
        var sourceVertices = sourceSprite.vertices;
        var sourceTriangles = sourceSprite.triangles;
        var fillVertices = fillSprite.vertices;
        var fillTriangles = fillSprite.triangles;
        if (!TryValidateSpriteGeometry(sourceVertices, sourceTriangles, out failure)
            || !HasSameSpriteGeometry(
                sourceVertices,
                sourceTriangles,
                fillVertices,
                fillTriangles))
        {
            if (string.IsNullOrEmpty(failure))
            {
                failure = "Mod-owned white seat sprite geometry drifted from the customer desk sprite";
            }
            return false;
        }

        var rendererMaterial = fill.sharedMaterial;
        if (rendererMaterial == null
            || !IsLiveUnityObject(rendererMaterial)
            || !TryReadSameNativeObject(ownedMaterial, rendererMaterial, out var sameMaterial)
            || !sameMaterial)
        {
            failure = "Mod-owned seat fill no longer binds its instantiated material";
            return false;
        }
        var ownedShader = ownedMaterial.shader;
        if (ownedShader == null
            || !IsLiveUnityObject(ownedShader)
            || !string.Equals(ownedShader.name, StandardSpriteShaderName, StringComparison.Ordinal))
        {
            failure = $"Mod-owned seat fill no longer retains the exact {StandardSpriteShaderName} default shader";
            return false;
        }

        bounds = fill.bounds;
        return TryValidateRenderableBounds(bounds, out failure);
    }

    private static bool TryValidateDisabledTemplateRenderer(
        GameObject clone,
        SpriteRenderer renderer,
        string expectedShaderName,
        Sprite sourceSprite,
        out string failure)
    {
        var rendererTransform = renderer.transform;
        if (rendererTransform == null
            || !IsLiveUnityObject(rendererTransform)
            || !TryReadSameNativeObject(clone.transform, rendererTransform, out var sameTransform)
            || (!sameTransform && !rendererTransform.IsChildOf(clone.transform)))
        {
            failure = $"private {expectedShaderName} template left the cloned selection hierarchy";
            return false;
        }
        if (renderer.enabled)
        {
            failure = $"private {expectedShaderName} template was unexpectedly re-enabled";
            return false;
        }

        var rendererSprite = renderer.sprite;
        var material = renderer.sharedMaterial;
        var shader = material?.shader;
        if (rendererSprite == null
            || material == null
            || shader == null
            || !IsLiveUnityObject(rendererSprite)
            || !IsLiveUnityObject(material)
            || !IsLiveUnityObject(shader)
            || !TryReadSameNativeObject(sourceSprite, rendererSprite, out var sameSprite)
            || !sameSprite
            || !string.Equals(shader.name, expectedShaderName, StringComparison.Ordinal))
        {
            failure = $"private {expectedShaderName} template identity changed";
            return false;
        }

        failure = "";
        return true;
    }

    private static bool TryReadSameParent(Transform? left, Transform? right, out bool same)
    {
        if (left == null || right == null)
        {
            same = left == null && right == null;
            return true;
        }
        if (!IsLiveUnityObject(left) || !IsLiveUnityObject(right))
        {
            same = false;
            return false;
        }
        return TryReadSameNativeObject(left, right, out same);
    }

    private static bool TryReadNativeClassName(Component component, out string fullName)
    {
        fullName = "";
        if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(component, out var pointer))
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

    private static bool TryValidateRenderableBounds(Bounds bounds, out string failure)
    {
        if (!IsFinite(bounds.center)
            || !IsFinite(bounds.size)
            || !HasNonZeroSize(bounds.size))
        {
            failure = "Mod-owned seat fill renderer has invalid world bounds";
            return false;
        }

        failure = "";
        return true;
    }

    private static void BeginBindingLogTargetLocked(SeatHighlightClaim target)
    {
        if (_bindingLogBusinessGeneration != target.SessionGeneration)
        {
            _bindingLogBusinessGeneration = target.SessionGeneration;
            _bindingLogCount = 0;
            _failureLogCount = 0;
            BindingLogKeys.Clear();
            FailureLogKeys.Clear();
        }
    }

    private static void TryLogFillBindingLocked(
        SeatHighlightClaim target,
        int sourceVertexCount,
        int sourceTriangleIndexCount,
        Bounds fillBounds)
    {
        var key = $"{target.TargetSetGeneration}:{target.DeskCode}:{target.Claims}";
        if (!BindingLogKeys.Add(key)) return;
        if (_bindingLogCount >= MaxBindingLogsPerBusiness) return;
        _bindingLogCount += 1;

        var log = _log;
        if (log == null) return;
        try
        {
            log.LogInfo(
                $"Runtime seat highlight fill bound: businessGeneration={target.SessionGeneration}; targetGeneration={target.TargetSetGeneration}; desk={target.DeskCode}; claims={target.Claims}; createTexture={OwnedFillTextureSize}x{OwnedFillTextureSize}/{TextureFormat.RGBA32}/mips:1/readable:True; createMesh={OwnedFillMeshTypeName}; geometry={sourceVertexCount}v/{sourceTriangleIndexCount}i; bounds={FormatVector(fillBounds.center)}/{FormatVector(fillBounds.size)}");
        }
        catch
        {
            // Bounded evidence must never affect the target visual or game loop.
        }
    }

    private static void TryLogFailureLocked(
        SeatHighlightClaim target,
        string phase,
        string failure)
    {
        if (_bindingLogBusinessGeneration != target.SessionGeneration)
        {
            _bindingLogBusinessGeneration = target.SessionGeneration;
            _bindingLogCount = 0;
            _failureLogCount = 0;
            BindingLogKeys.Clear();
            FailureLogKeys.Clear();
        }

        var normalizedFailure = NormalizeStatus(failure);
        var key = $"{target.TargetSetGeneration}:{target.DeskCode}:{target.Claims}:{phase}:{normalizedFailure}";
        if (_failureLogCount >= MaxFailureLogsPerBusiness || !FailureLogKeys.Add(key)) return;
        _failureLogCount += 1;

        var log = _log;
        if (log == null) return;
        try
        {
            log.LogInfo(
                $"Runtime seat highlight fill failed: businessGeneration={target.SessionGeneration}; targetGeneration={target.TargetSetGeneration}; desk={target.DeskCode}; claims={target.Claims}; phase={phase}; createTexture={OwnedFillTextureSize}x{OwnedFillTextureSize}/{TextureFormat.RGBA32}/mips:1/readable:True; createMesh={OwnedFillMeshTypeName}; failure={normalizedFailure}");
        }
        catch
        {
            // Bounded diagnostics must never affect the target visual or game loop.
        }
    }

    private static PropertyInfo? FindDeclaredStaticProperty(Type type, string name)
    {
        var property = type.GetProperty(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        return property != null
            && property.GetMethod?.IsStatic == true
            && property.GetIndexParameters().Length == 0
                ? property
                : null;
    }

    private static PropertyInfo? FindDeclaredInstanceProperty(Type type, string name)
    {
        var property = type.GetProperty(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        return property != null
            && property.GetMethod?.IsStatic == false
            && property.GetIndexParameters().Length == 0
                ? property
                : null;
    }

    private static MethodInfo? FindDeclaredInstanceMethod(Type type, string name, params Type[] parameterTypes)
    {
        var matches = type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == name
                && !method.IsGenericMethod
                && ParametersMatch(method.GetParameters(), parameterTypes))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static MethodInfo? FindDeclaredGenericArrayInstanceMethod(Type type, string name)
    {
        var matches = type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == name
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1
                && method.GetParameters().Length == 0)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
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
            if (cachedPointerProperty?.PropertyType != typeof(IntPtr)
                || cachedPointerProperty.GetMethod?.IsStatic != false
                || cachedPointerProperty.GetIndexParameters().Length != 0)
            {
                return false;
            }

            return cachedPointerProperty.GetValue(target) is IntPtr cachedPointer
                && cachedPointer != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadSameNativeObject(object left, object right, out bool same)
    {
        same = false;
        if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(left, out var leftPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(right, out var rightPointer))
        {
            return false;
        }

        same = leftPointer == rightPointer;
        return true;
    }

    private static bool ParametersMatch(ParameterInfo[] parameters, Type[] expected)
    {
        if (parameters.Length != expected.Length) return false;
        for (var index = 0; index < parameters.Length; index += 1)
        {
            if (parameters[index].ParameterType != expected[index]) return false;
        }

        return true;
    }

    private static Dictionary<int, SeatHighlightClaim> BuildSeatClaims(
        RuntimeUiTargetSetSnapshot targetSet)
    {
        var claims = new Dictionary<int, SeatHighlightClaim>();
        foreach (var target in targetSet.Targets.Where(target =>
                     target.SeatHighlightEnabled && target.DeskCode >= 0))
        {
            var combined = target.Claim;
            if (claims.TryGetValue(target.DeskCode, out var existing))
            {
                combined |= existing.Claims;
            }
            claims[target.DeskCode] = new SeatHighlightClaim(
                targetSet.Generation,
                targetSet.SessionGeneration,
                target.DeskCode,
                combined);
        }
        return claims;
    }

    private static bool HasSeatHighlightTargets(RuntimeUiTargetSetSnapshot targetSet)
    {
        return targetSet.Targets.Any(target => target.SeatHighlightEnabled);
    }

    private static void ReconcileTargetSetChangeLocked(RuntimeUiTargetSetSnapshot targetSet)
    {
        var claims = BuildSeatClaims(targetSet);
        foreach (var (deskCode, active) in ActiveVisuals.ToList())
        {
            if (targetSet.SessionGeneration != active.SessionGeneration
                || !claims.TryGetValue(deskCode, out var claim))
            {
                DestroyActiveVisualLocked(deskCode);
                NextAttemptAt[deskCode] = 0f;
                continue;
            }
            active.Claims = claim.Claims;
        }

        foreach (var deskCode in NextAttemptAt.Keys.ToList())
        {
            if (!claims.ContainsKey(deskCode)) NextAttemptAt.Remove(deskCode);
        }
        foreach (var deskCode in claims.Keys)
        {
            if (!ActiveVisuals.ContainsKey(deskCode)) NextAttemptAt[deskCode] = 0f;
        }
    }

    private static void DestroyActiveVisualLocked(int deskCode)
    {
        if (!ActiveVisuals.Remove(deskCode, out var active)) return;
        SafeDestroyGameObject(active.FillObject);
        SafeDestroyGameObject(active.SelectionClone);
        SafeDestroySprite(active.FillSprite);
        SafeDestroyMaterial(active.FillMaterial);
        SafeDestroyTexture(active.FillTexture);
    }

    private static void SafeDestroyGameObject(GameObject ownedObject)
    {
        try
        {
            if (!IsLiveUnityObject(ownedObject)) return;
            ownedObject.SetActive(false);
            UnityEngine.Object.Destroy(ownedObject);
        }
        catch
        {
            Interlocked.Increment(ref _destroyErrors);
        }
    }

    private static void SafeDestroyMaterial(Material material)
    {
        try
        {
            if (!IsLiveUnityObject(material)) return;
            UnityEngine.Object.Destroy(material);
        }
        catch
        {
            Interlocked.Increment(ref _destroyErrors);
        }
    }

    private static void SafeDestroySprite(Sprite sprite)
    {
        try
        {
            if (!IsLiveUnityObject(sprite)) return;
            UnityEngine.Object.Destroy(sprite);
        }
        catch
        {
            Interlocked.Increment(ref _destroyErrors);
        }
    }

    private static void SafeDestroyTexture(Texture2D texture)
    {
        try
        {
            if (!IsLiveUnityObject(texture)) return;
            UnityEngine.Object.Destroy(texture);
        }
        catch
        {
            Interlocked.Increment(ref _destroyErrors);
        }
    }

    private static void AbandonActiveVisualLocked(int deskCode)
    {
        ActiveVisuals.Remove(deskCode);
    }

    private static void DestroyAllActiveVisualsLocked()
    {
        foreach (var deskCode in ActiveVisuals.Keys.ToList()) DestroyActiveVisualLocked(deskCode);
    }

    private static void AbandonAllActiveVisualsLocked()
    {
        ActiveVisuals.Clear();
    }

    private static bool Approximately(Vector3 left, Vector3 right)
    {
        const float tolerance = 0.001f;
        return Math.Abs(left.x - right.x) <= tolerance
            && Math.Abs(left.y - right.y) <= tolerance
            && Math.Abs(left.z - right.z) <= tolerance;
    }

    private static bool Approximately(Vector2 left, Vector2 right)
    {
        const float tolerance = 0.001f;
        return Math.Abs(left.x - right.x) <= tolerance
            && Math.Abs(left.y - right.y) <= tolerance;
    }

    private static bool Approximately(Bounds left, Bounds right)
    {
        return Approximately(left.center, right.center)
            && Approximately(left.size, right.size);
    }

    private static bool Approximately(Quaternion left, Quaternion right)
    {
        const float tolerance = 0.001f;
        var direct = Math.Abs(left.x - right.x) <= tolerance
            && Math.Abs(left.y - right.y) <= tolerance
            && Math.Abs(left.z - right.z) <= tolerance
            && Math.Abs(left.w - right.w) <= tolerance;
        if (direct) return true;

        return Math.Abs(left.x + right.x) <= tolerance
            && Math.Abs(left.y + right.y) <= tolerance
            && Math.Abs(left.z + right.z) <= tolerance
            && Math.Abs(left.w + right.w) <= tolerance;
    }

    private static bool HasNonZeroSize(Vector3 value)
    {
        return Math.Abs(value.x) > float.Epsilon
            || Math.Abs(value.y) > float.Epsilon
            || Math.Abs(value.z) > float.Epsilon;
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static string FormatVector(Vector3 value)
    {
        return $"{value.x:0.###},{value.y:0.###},{value.z:0.###}";
    }

    private static string NormalizeReason(string reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? "scene unavailable" : reason.Trim();
    }

    private static string NormalizeStatus(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "unknown error" : value.Trim();
        return normalized.Length <= 180 ? normalized : normalized[..180] + "...";
    }

    private readonly record struct SeatHighlightClaim(
        long TargetSetGeneration,
        long SessionGeneration,
        int DeskCode,
        RuntimeUiTargetKinds Claims);

    private sealed class ActiveSeatVisual
    {
        public ActiveSeatVisual(
            GameObject selectionClone,
            SpriteRenderer outlineRenderer,
            SpriteRenderer regionalRenderer,
            GameObject fillObject,
            SpriteRenderer fillRenderer,
            Material fillMaterial,
            Sprite sourceSprite,
            Sprite fillSprite,
            Texture2D fillTexture,
            long sessionGeneration,
            int deskCode,
            RuntimeUiTargetKinds claims,
            Vector3 rootPosition,
            int fillLayer,
            int fillSortingLayerId,
            int fillSortingOrder,
            Vector3 fillLocalPosition,
            Quaternion fillLocalRotation,
            Vector3 fillLocalScale,
            SpriteDrawMode fillDrawMode,
            bool fillFlipX,
            bool fillFlipY,
            float nextHealthCheckAt)
        {
            SelectionClone = selectionClone;
            OutlineRenderer = outlineRenderer;
            RegionalRenderer = regionalRenderer;
            FillObject = fillObject;
            FillRenderer = fillRenderer;
            FillMaterial = fillMaterial;
            SourceSprite = sourceSprite;
            FillSprite = fillSprite;
            FillTexture = fillTexture;
            SessionGeneration = sessionGeneration;
            DeskCode = deskCode;
            Claims = claims;
            RootPosition = rootPosition;
            FillLayer = fillLayer;
            FillSortingLayerId = fillSortingLayerId;
            FillSortingOrder = fillSortingOrder;
            FillLocalPosition = fillLocalPosition;
            FillLocalRotation = fillLocalRotation;
            FillLocalScale = fillLocalScale;
            FillDrawMode = fillDrawMode;
            FillFlipX = fillFlipX;
            FillFlipY = fillFlipY;
            NextHealthCheckAt = nextHealthCheckAt;
        }

        public GameObject SelectionClone { get; }
        public SpriteRenderer OutlineRenderer { get; }
        public SpriteRenderer RegionalRenderer { get; }
        public GameObject FillObject { get; }
        public SpriteRenderer FillRenderer { get; }
        public Material FillMaterial { get; }
        public Sprite SourceSprite { get; }
        public Sprite FillSprite { get; }
        public Texture2D FillTexture { get; }
        public long SessionGeneration { get; }
        public int DeskCode { get; }
        public RuntimeUiTargetKinds Claims { get; set; }
        public Vector3 RootPosition { get; }
        public int FillLayer { get; }
        public int FillSortingLayerId { get; }
        public int FillSortingOrder { get; }
        public Vector3 FillLocalPosition { get; }
        public Quaternion FillLocalRotation { get; }
        public Vector3 FillLocalScale { get; }
        public SpriteDrawMode FillDrawMode { get; }
        public bool FillFlipX { get; }
        public bool FillFlipY { get; }
        public float NextHealthCheckAt { get; set; }
    }
}
