using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// Owns private copies of the game's table selection and stencil visuals for the current recommendation target.
/// </summary>
internal static class RuntimeSeatHighlightService
{
    private const float RetryIntervalSeconds = 1.25f;
    private const float VisibilityGraceSeconds = 1f;
    private const float ActiveHealthCheckIntervalSeconds = 0.25f;
    private const int MaxSelectionRendererCount = 64;
    private const string TileManagerTypeName = "NightScene.Tiles.TileManager";
    private const string InteractableTileTypeName = "NightScene.Tiles.InteractableTile";
    private const string TileBaseTypeName = "UnityEngine.Tilemaps.Tile";
    private const string TilemapTypeName = "UnityEngine.Tilemaps.Tilemap";
    private const string UiElementClusterTypeName = "DEYU.UniversalUISystem.UIElementCluster";
    private const string StencilPainterControllerTypeName = "NightScene.Tiles.StencilPainterController";

    private static readonly object DesiredRoot = new();
    private static readonly object VisualRoot = new();

    private static SeatHighlightTargetSnapshot _desiredTarget = SeatHighlightTargetSnapshot.Disabled;
    private static long _appliedTargetGeneration;
    private static bool _suspended = true;
    private static string _suspendReason = "night business inactive";
    private static GameObject? _activeSelectionClone;
    private static List<SpriteRenderer>? _activeSelectionRenderers;
    private static SpriteRenderer? _activeSelectionPrimary;
    private static Sprite? _activeSelectionSprite;
    private static GameObject? _activeStencilClone;
    private static object? _activeController;
    private static Il2CppSystem.Collections.Generic.List<SpriteRenderer>? _activeWorkers;
    private static Sprite? _activeVisual;
    private static long _activeSessionGeneration;
    private static int _activeDeskCode = -1;
    private static int _activeWorkerCount;
    private static int _activeRenderableWorkerCount;
    private static int _activeSelectionRendererCount;
    private static int _activeRenderableSelectionRendererCount;
    private static Vector3Int _activeCellPosition;
    private static Vector3 _activeAnchor;
    private static float _activeVisibilityDeadline;
    private static float _nextVisibilityCheckAt;
    private static float _nextAttemptAt;
    private static long _destroyErrors;
    private static string _status = "disabled";
    private static string _renderDiagnostics = "none";

    public static string Status
    {
        get
        {
            var desired = Volatile.Read(ref _desiredTarget);
            lock (VisualRoot)
            {
                return $"{_status}; desired={desired.Generation}/session:{desired.SessionGeneration}/desk:{desired.DeskCode}; applied={_appliedTargetGeneration}; active=session:{_activeSessionGeneration}/desk:{_activeDeskCode}/selection:{_activeRenderableSelectionRendererCount}/{_activeSelectionRendererCount}/stencil:{_activeRenderableWorkerCount}/{_activeWorkerCount}/cell:{FormatVector(_activeCellPosition)}/anchor:{FormatVector(_activeAnchor)}; render={_renderDiagnostics}; suspended={_suspended}; destroyErrors={_destroyErrors}";
            }
        }
    }

    /// <summary>
    /// Publishes managed desired state only. Unity objects are reconciled later by <see cref="Tick"/>.
    /// </summary>
    public static void UpdateTarget(long sessionGeneration, bool enabled, int deskCode)
    {
        var normalizedEnabled = enabled && sessionGeneration > 0 && deskCode >= 0;
        var normalizedDeskCode = normalizedEnabled ? deskCode : -1;
        lock (DesiredRoot)
        {
            var current = Volatile.Read(ref _desiredTarget);
            if (current.HasSameValues(sessionGeneration, normalizedEnabled, normalizedDeskCode)) return;

            Volatile.Write(
                ref _desiredTarget,
                new SeatHighlightTargetSnapshot(
                    checked(current.Generation + 1),
                    sessionGeneration,
                    normalizedEnabled,
                    normalizedDeskCode));
        }
    }

    public static void Tick()
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        var desired = Volatile.Read(ref _desiredTarget);
        lock (VisualRoot)
        {
            if (_suspended) return;
            if (Environment.CurrentManagedThreadId != lifecycle.ThreadId)
            {
                _status = "waiting for Unity main thread";
                return;
            }

            var desiredEnabled = lifecycle.IsActive
                && desired.Enabled
                && desired.SessionGeneration == lifecycle.Generation
                && desired.DeskCode >= 0;
            if (_appliedTargetGeneration != desired.Generation)
            {
                DestroyActiveVisualsLocked();
                _appliedTargetGeneration = desired.Generation;
                _nextAttemptAt = 0f;
            }

            if (!desiredEnabled)
            {
                DestroyActiveVisualsLocked();
                _status = desired.Enabled
                    ? lifecycle.IsActive
                        ? "waiting: target belongs to a different night-business session"
                        : "waiting: night business inactive"
                    : "disabled";
                return;
            }

            if (_activeSelectionClone != null
                && _activeStencilClone != null
                && _activeController != null
                && _activeSessionGeneration == desired.SessionGeneration
                && _activeDeskCode == desired.DeskCode)
            {
                var selectionCloneIsLive = IsLiveUnityObject(_activeSelectionClone);
                var cloneIsLive = IsLiveUnityObject(_activeStencilClone);
                if (selectionCloneIsLive && cloneIsLive && IsLiveUnityObject(_activeController))
                {
                    var now = Time.realtimeSinceStartup;
                    if (now < _nextVisibilityCheckAt) return;

                    if (TryInspectVisualsLocked(
                            out var selectionRendererCount,
                            out var renderableSelectionRendererCount,
                            out var workerCount,
                            out var renderableWorkerCount,
                            out var visibilityFailure))
                    {
                        _activeSelectionRendererCount = selectionRendererCount;
                        _activeRenderableSelectionRendererCount = renderableSelectionRendererCount;
                        _activeWorkerCount = workerCount;
                        _activeRenderableWorkerCount = renderableWorkerCount;
                        if (renderableSelectionRendererCount > 0 && renderableWorkerCount > 0)
                        {
                            _activeVisibilityDeadline = 0f;
                            _nextVisibilityCheckAt = now + ActiveHealthCheckIntervalSeconds;
                            _status = "active";
                            return;
                        }

                        if (_activeVisibilityDeadline <= 0f)
                        {
                            _activeVisibilityDeadline = now + VisibilityGraceSeconds;
                        }
                        if (now < _activeVisibilityDeadline)
                        {
                            _nextVisibilityCheckAt = 0f;
                            _status = $"pending: {NormalizeStatus(visibilityFailure)}";
                            return;
                        }

                        DestroyActiveVisualsLocked();
                        _nextAttemptAt = now + RetryIntervalSeconds;
                        _status = $"unavailable: {NormalizeStatus(visibilityFailure)}";
                        return;
                    }

                    DestroyActiveVisualsLocked();
                    _nextAttemptAt = now + RetryIntervalSeconds;
                    _status = $"unavailable: {NormalizeStatus(visibilityFailure)}";
                    return;
                }

                DestroyActiveVisualsLocked();
                _nextAttemptAt = 0f;
            }

            DestroyActiveVisualsLocked();
            if (Time.realtimeSinceStartup < _nextAttemptAt) return;
            if (!TryCreateActiveVisualsLocked(desired, out var failure))
            {
                _nextAttemptAt = Time.realtimeSinceStartup + RetryIntervalSeconds;
                _status = $"unavailable: {NormalizeStatus(failure)}";
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
                DestroyActiveVisualsLocked();
            }
            else
            {
                AbandonActiveVisualsLocked();
            }

            _nextAttemptAt = 0f;
            _status = Volatile.Read(ref _desiredTarget).Enabled
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
                DestroyActiveVisualsLocked();
            }
            else
            {
                AbandonActiveVisualsLocked();
            }

            _suspended = false;
            _suspendReason = NormalizeReason(reason);
            _nextAttemptAt = 0f;
            _status = Volatile.Read(ref _desiredTarget).Enabled
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
            AbandonActiveVisualsLocked();
            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            _nextAttemptAt = 0f;
            _status = $"abandoned: {_suspendReason}";
        }
    }

    /// <summary>
    /// Releases the Mod-owned clone when the persistent controller is disposed on the Unity thread.
    /// </summary>
    public static void Dispose(string reason)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        lock (VisualRoot)
        {
            if (Environment.CurrentManagedThreadId == lifecycle.ThreadId)
            {
                DestroyActiveVisualsLocked();
            }
            else
            {
                AbandonActiveVisualsLocked();
            }

            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            _nextAttemptAt = 0f;
            _status = $"disposed: {_suspendReason}";
        }
    }

    private static bool TryCreateActiveVisualsLocked(SeatHighlightTargetSnapshot target, out string failure)
    {
        GameObject? selectionClone = null;
        GameObject? clone = null;
        try
        {
            var tileManagerType = RuntimeReflectionUtility.FindType(TileManagerTypeName);
            var tileType = RuntimeReflectionUtility.FindType(InteractableTileTypeName);
            var controllerType = RuntimeReflectionUtility.FindType(StencilPainterControllerTypeName);
            if (tileManagerType == null || tileType == null || controllerType == null)
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
            var tileManager = instanceProperty?.GetValue(null);
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

            var getSelectionRendererDefinition = FindDeclaredGenericInstanceMethod(
                onSelectionProperty.PropertyType,
                "GetObject",
                typeof(int));
            var getSelectionRenderersDefinition = FindDeclaredGenericArrayInstanceMethod(
                onSelectionProperty.PropertyType,
                "GetObjects");
            if (getSelectionRendererDefinition == null || getSelectionRenderersDefinition == null)
            {
                failure = "UIElementCluster renderer accessors do not match the verified declarations";
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

            var desiredRootPosition = new Vector3(
                cellCenterWorld.x + cellTransform.m03,
                cellCenterWorld.y + cellTransform.m13,
                cellCenterWorld.z + cellTransform.m23);
            if (!IsFinite(desiredRootPosition))
            {
                failure = "customer desk selection position is invalid";
                return false;
            }

            var visualDictionaryProperty = FindDeclaredInstanceProperty(tileManagerType, "interactablesHighlightedVisual");
            if (visualDictionaryProperty?.PropertyType
                != typeof(Il2CppSystem.Collections.Generic.Dictionary<Sprite, Sprite>))
            {
                failure = "TileManager highlighted visual property does not match Dictionary<Sprite, Sprite>";
                return false;
            }
            var visualDictionary = visualDictionaryProperty.GetValue(tileManager);
            if (visualDictionary == null)
            {
                failure = "TileManager highlighted visual dictionary is unavailable";
                return false;
            }

            if (!TryReadHighlightedVisual(visualDictionary, tileSprite, out var highlightedVisual, out failure))
            {
                return false;
            }

            selectionClone = UnityEngine.Object.Instantiate(selectionSourceObject);
            if (selectionClone == null || !IsLiveUnityObject(selectionClone))
            {
                failure = "failed to clone the game selection visual";
                return false;
            }

            selectionClone.name = "MystiaStewardCompanion.TargetDeskSelection";
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

            clonedSelectionRoot.transform.position = desiredRootPosition;
            if (!TryBindSelectionRenderers(
                    clonedSelection,
                    getSelectionRendererDefinition,
                    getSelectionRenderersDefinition,
                    clonedSelectionRoot.transform,
                    tileSprite,
                    out var selectionRenderers,
                    out var selectionRenderer,
                    out failure))
            {
                return false;
            }

            var selectionBounds = selectionRenderer.bounds;
            var painterAnchor = selectionBounds.center;
            if (!selectionRenderer.enabled
                || !selectionRenderer.gameObject.activeInHierarchy
                || !IsFinite(painterAnchor)
                || !IsFinite(selectionBounds.size)
                || !HasNonZeroSize(selectionBounds.size))
            {
                failure = "cloned selection primary renderer is not renderable";
                return false;
            }

            var templateProperty = FindDeclaredInstanceProperty(tileManagerType, "stencilPainterParent");
            var parentProperty = FindDeclaredInstanceProperty(tileManagerType, "stencilPainterField");
            if (templateProperty?.PropertyType != typeof(GameObject)
                || parentProperty?.PropertyType != typeof(Transform))
            {
                failure = "TileManager stencil properties do not match the verified declarations";
                return false;
            }
            var template = templateProperty.GetValue(tileManager) as GameObject;
            var parent = parentProperty.GetValue(tileManager) as Transform;
            if (template == null
                || parent == null
                || !IsLiveUnityObject(template)
                || !IsLiveUnityObject(parent))
            {
                failure = "TileManager stencil prefab or parent is unavailable";
                return false;
            }

            clone = UnityEngine.Object.Instantiate(template, parent);
            if (clone == null || !IsLiveUnityObject(clone))
            {
                failure = "failed to clone the game stencil prefab";
                return false;
            }

            clone.name = "MystiaStewardCompanion.TargetDeskHighlight";
            if (!clone.activeInHierarchy)
            {
                failure = "cloned stencil is inactive in the scene hierarchy";
                return false;
            }
            var component = clone.GetComponent(Il2CppType.From(controllerType));
            var controller = RuntimeReflectionUtility.TryCastRuntimeObject(component, StencilPainterControllerTypeName);
            if (controller == null || !IsLiveUnityObject(controller))
            {
                failure = "cloned stencil controller is unavailable";
                return false;
            }

            var workerProperty = FindDeclaredInstanceProperty(controllerType, "worker");
            if (workerProperty?.PropertyType
                != typeof(Il2CppSystem.Collections.Generic.List<SpriteRenderer>))
            {
                failure = "cloned stencil worker property does not match List<SpriteRenderer>";
                return false;
            }
            var worker = workerProperty.GetValue(controller)
                as Il2CppSystem.Collections.Generic.List<SpriteRenderer>;
            if (worker == null || !TryReadExactCount(worker, out var workerCount) || workerCount <= 0)
            {
                failure = "cloned stencil has no SpriteRenderer workers";
                return false;
            }

            var show = FindDeclaredInstanceMethod(controllerType, "Show", typeof(Vector3), typeof(Sprite));
            if (show == null || show.ReturnType != typeof(void))
            {
                failure = "StencilPainterController.Show does not match the verified declaration";
                return false;
            }

            show.Invoke(controller, new[] { (object)painterAnchor, highlightedVisual });
            if (!IsLiveUnityObject(clone)
                || !IsLiveUnityObject(controller))
            {
                failure = "stencil clone became unavailable while applying the target";
                return false;
            }

            _activeStencilClone = clone;
            _activeSelectionClone = selectionClone;
            _activeSelectionRenderers = selectionRenderers;
            _activeSelectionPrimary = selectionRenderer;
            _activeSelectionSprite = tileSprite;
            _activeController = controller;
            _activeWorkers = worker;
            _activeVisual = (Sprite)highlightedVisual;
            _activeSessionGeneration = target.SessionGeneration;
            _activeDeskCode = target.DeskCode;
            _activeWorkerCount = workerCount;
            _activeRenderableWorkerCount = 0;
            _activeSelectionRendererCount = selectionRenderers.Count;
            _activeRenderableSelectionRendererCount = 0;
            _activeCellPosition = cellPosition;
            _activeAnchor = painterAnchor;
            _activeVisibilityDeadline = Time.realtimeSinceStartup + VisibilityGraceSeconds;
            _nextVisibilityCheckAt = 0f;
            _status = "pending: waiting for the native stencil show coroutine";
            selectionClone = null;
            clone = null;
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
            if (clone != null)
            {
                SafeDestroyClone(clone);
            }
            if (selectionClone != null)
            {
                SafeDestroyClone(selectionClone);
            }
        }
    }

    private static bool TryReadHighlightedVisual(
        object dictionary,
        object sourceSprite,
        out object highlightedVisual,
        out string failure)
    {
        highlightedVisual = new object();
        var dictionaryType = dictionary.GetType();
        var spriteType = sourceSprite.GetType();
        var containsKey = FindDeclaredInstanceMethod(dictionaryType, "ContainsKey", spriteType);
        var getItem = FindDeclaredInstanceMethod(dictionaryType, "get_Item", spriteType);
        if (containsKey == null
            || containsKey.ReturnType != typeof(bool)
            || getItem == null
            || getItem.ReturnType.FullName != "UnityEngine.Sprite")
        {
            failure = "highlighted visual dictionary does not match Dictionary<Sprite, Sprite>";
            return false;
        }

        if (containsKey.Invoke(dictionary, new[] { sourceSprite }) is not true)
        {
            failure = "customer desk sprite has no highlighted visual";
            return false;
        }

        var visual = getItem.Invoke(dictionary, new[] { sourceSprite });
        if (visual == null
            || visual.GetType().FullName != "UnityEngine.Sprite"
            || !IsLiveUnityObject(visual))
        {
            failure = "highlighted customer desk sprite is unavailable";
            return false;
        }

        highlightedVisual = visual;
        failure = "";
        return true;
    }

    private static bool TryBindSelectionRenderers(
        object selection,
        MethodInfo getPrimaryDefinition,
        MethodInfo getAllDefinition,
        Transform selectionRoot,
        Sprite sourceSprite,
        out List<SpriteRenderer> renderers,
        out SpriteRenderer primary,
        out string failure)
    {
        renderers = new List<SpriteRenderer>();
        primary = null!;
        var getPrimary = getPrimaryDefinition.MakeGenericMethod(typeof(SpriteRenderer));
        var getAll = getAllDefinition.MakeGenericMethod(typeof(SpriteRenderer));
        if (getPrimary.ReturnType != typeof(SpriteRenderer)
            || !typeof(Il2CppArrayBase<SpriteRenderer>).IsAssignableFrom(getAll.ReturnType))
        {
            failure = "UIElementCluster SpriteRenderer accessors have unexpected closed return types";
            return false;
        }

        if (getAll.Invoke(selection, Array.Empty<object?>()) is not Il2CppArrayBase<SpriteRenderer> sourceRenderers)
        {
            failure = "cloned selection has no SpriteRenderer array";
            return false;
        }
        if (sourceRenderers.Length <= 0 || sourceRenderers.Length > MaxSelectionRendererCount)
        {
            failure = $"cloned selection SpriteRenderer count is outside 1..{MaxSelectionRendererCount}";
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

            renderer.sprite = sourceSprite;
            renderers.Add(renderer);
        }

        if (getPrimary.Invoke(selection, new object?[] { 0 }) is not SpriteRenderer primaryRenderer
            || !IsLiveUnityObject(primaryRenderer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(primaryRenderer, out var primaryPointer)
            || !identities.Contains(primaryPointer))
        {
            failure = "cloned selection primary SpriteRenderer is unavailable or outside its renderer array";
            return false;
        }

        primary = primaryRenderer;
        failure = "";
        return true;
    }

    private static bool TryReadExactCount(object? collection, out int count)
    {
        count = 0;
        if (collection == null) return false;
        var getter = FindDeclaredInstanceMethod(collection.GetType(), "get_Count");
        if (getter == null || getter.ReturnType != typeof(int)) return false;
        var rawCount = getter.Invoke(collection, Array.Empty<object?>());
        if (rawCount is not int value || value < 0) return false;
        count = value;
        return true;
    }

    private static bool TryInspectVisualsLocked(
        out int selectionRendererCount,
        out int renderableSelectionRendererCount,
        out int workerCount,
        out int renderableWorkerCount,
        out string failure)
    {
        try
        {
            return TryInspectVisualsCoreLocked(
                out selectionRendererCount,
                out renderableSelectionRendererCount,
                out workerCount,
                out renderableWorkerCount,
                out failure);
        }
        catch (Exception ex)
        {
            selectionRendererCount = 0;
            renderableSelectionRendererCount = 0;
            workerCount = 0;
            renderableWorkerCount = 0;
            failure = $"seat visual inspection failed: {NormalizeStatus(ex.GetBaseException().Message)}";
            return false;
        }
    }

    private static bool TryInspectVisualsCoreLocked(
        out int selectionRendererCount,
        out int renderableSelectionRendererCount,
        out int workerCount,
        out int renderableWorkerCount,
        out string failure)
    {
        selectionRendererCount = 0;
        renderableSelectionRendererCount = 0;
        workerCount = 0;
        renderableWorkerCount = 0;
        var selectionClone = _activeSelectionClone;
        var selectionRenderers = _activeSelectionRenderers;
        var selectionPrimary = _activeSelectionPrimary;
        var sourceSprite = _activeSelectionSprite;
        var clone = _activeStencilClone;
        var workers = _activeWorkers;
        var highlightedVisual = _activeVisual;
        if (selectionClone == null
            || selectionRenderers == null
            || selectionPrimary == null
            || sourceSprite == null
            || clone == null
            || workers == null
            || highlightedVisual == null)
        {
            failure = "active seat visual state is incomplete";
            return false;
        }
        if (!selectionClone.activeInHierarchy)
        {
            failure = "cloned selection visual is inactive in the scene hierarchy";
            return true;
        }
        if (!clone.activeInHierarchy)
        {
            failure = "cloned stencil is inactive in the scene hierarchy";
            return true;
        }
        if (!TryReadExactCount(workers, out workerCount) || workerCount <= 0)
        {
            failure = "cloned stencil has no SpriteRenderer workers";
            return false;
        }
        selectionRendererCount = selectionRenderers.Count;
        if (selectionRendererCount <= 0 || selectionRendererCount > MaxSelectionRendererCount)
        {
            failure = "cloned selection has an invalid SpriteRenderer count";
            return false;
        }
        if (!IsLiveUnityObject(sourceSprite)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(sourceSprite, out var sourceSpritePointer))
        {
            failure = "customer desk source sprite has no native identity";
            return false;
        }
        if (!IsLiveUnityObject(highlightedVisual)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(highlightedVisual, out var highlightedVisualPointer))
        {
            failure = "highlighted customer desk sprite has no native identity";
            return false;
        }

        var firstFailure = "cloned selection has no renderable SpriteRenderer";
        for (var index = 0; index < selectionRendererCount; index += 1)
        {
            var renderer = selectionRenderers[index];
            if (renderer == null || !IsLiveUnityObject(renderer))
            {
                firstFailure = $"selection renderer {index} is unavailable";
                continue;
            }
            var rendererSprite = renderer.sprite;
            if (rendererSprite == null
                || !IsLiveUnityObject(rendererSprite)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(rendererSprite, out var rendererSpritePointer)
                || rendererSpritePointer != sourceSpritePointer)
            {
                firstFailure = $"selection renderer {index} has not bound the customer desk sprite";
                continue;
            }
            var rendererObject = renderer.gameObject;
            var bounds = renderer.bounds;
            if (rendererObject != null
                && IsLiveUnityObject(rendererObject)
                && rendererObject.activeInHierarchy
                && renderer.enabled
                && IsFinite(bounds.center)
                && IsFinite(bounds.size)
                && HasNonZeroSize(bounds.size))
            {
                renderableSelectionRendererCount += 1;
            }
            else
            {
                firstFailure = $"selection renderer {index} is not currently renderable";
            }
        }
        if (renderableSelectionRendererCount <= 0)
        {
            failure = firstFailure;
            return true;
        }
        if (!IsLiveUnityObject(selectionPrimary))
        {
            failure = "cloned selection primary renderer is unavailable";
            return false;
        }
        var primaryObject = selectionPrimary.gameObject;
        var primaryBounds = selectionPrimary.bounds;
        if (primaryObject == null
            || !IsLiveUnityObject(primaryObject)
            || !primaryObject.activeInHierarchy
            || !selectionPrimary.enabled
            || !IsFinite(primaryBounds.center)
            || !IsFinite(primaryBounds.size)
            || !HasNonZeroSize(primaryBounds.size))
        {
            failure = "cloned selection primary renderer is not currently renderable";
            return true;
        }
        var primaryBoundsCenter = primaryBounds.center;
        if (!IsFinite(primaryBoundsCenter) || !Approximately(primaryBoundsCenter, _activeAnchor))
        {
            failure = "cloned selection primary renderer moved away from the painter anchor";
            return true;
        }

        firstFailure = "cloned stencil has no renderable SpriteRenderer workers";
        SpriteRenderer? firstRenderableWorker = null;
        for (var index = 0; index < workerCount; index += 1)
        {
            try
            {
                var renderer = workers[index];
                if (renderer == null || !IsLiveUnityObject(renderer))
                {
                    firstFailure = $"stencil worker {index} is unavailable";
                    continue;
                }

                var rendererSprite = renderer.sprite;
                if (rendererSprite == null
                    || !IsLiveUnityObject(rendererSprite)
                    || !RuntimeReflectionUtility.TryReadNativeObjectPointer(rendererSprite, out var rendererSpritePointer)
                    || rendererSpritePointer != highlightedVisualPointer)
                {
                    firstFailure = $"stencil worker {index} has not bound the highlighted sprite";
                    continue;
                }

                var rendererObject = renderer.gameObject;
                if (rendererObject == null
                    || !IsLiveUnityObject(rendererObject)
                    || !rendererObject.activeInHierarchy)
                {
                    firstFailure = $"stencil worker {index} is inactive in the scene hierarchy";
                    continue;
                }

                var size = renderer.bounds.size;
                if (renderer.enabled
                    && IsFinite(size)
                    && (Math.Abs(size.x) > float.Epsilon
                        || Math.Abs(size.y) > float.Epsilon
                        || Math.Abs(size.z) > float.Epsilon))
                {
                    renderableWorkerCount += 1;
                    firstRenderableWorker ??= renderer;
                }
                else
                {
                    firstFailure = $"stencil worker {index} is not currently renderable";
                }
            }
            catch (Exception ex)
            {
                firstFailure = $"stencil worker {index} inspection failed: {NormalizeStatus(ex.GetBaseException().Message)}";
            }
        }

        if (renderableWorkerCount > 0 && firstRenderableWorker != null)
        {
            _renderDiagnostics = $"selection[{DescribeRenderer(selectionPrimary)}]|stencil[{DescribeRenderer(firstRenderableWorker)}]";
            failure = "";
        }
        else
        {
            failure = firstFailure;
        }
        return true;
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

    private static MethodInfo? FindDeclaredGenericInstanceMethod(Type type, string name, params Type[] parameterTypes)
    {
        var matches = type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == name
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1
                && method.ReturnType.IsGenericParameter
                && method.ReturnType.GenericParameterPosition == 0
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

    private static void DestroyActiveVisualsLocked()
    {
        var selectionClone = _activeSelectionClone;
        var clone = _activeStencilClone;
        AbandonActiveVisualsLocked();
        if (clone != null)
        {
            SafeDestroyClone(clone);
        }
        if (selectionClone != null)
        {
            SafeDestroyClone(selectionClone);
        }
    }

    private static void SafeDestroyClone(GameObject clone)
    {
        try
        {
            if (!IsLiveUnityObject(clone)) return;
            UnityEngine.Object.Destroy(clone);
        }
        catch
        {
            Interlocked.Increment(ref _destroyErrors);
        }
    }

    private static void AbandonActiveVisualsLocked()
    {
        _activeSelectionClone = null;
        _activeSelectionRenderers = null;
        _activeSelectionPrimary = null;
        _activeSelectionSprite = null;
        _activeStencilClone = null;
        _activeController = null;
        _activeWorkers = null;
        _activeVisual = null;
        _activeSessionGeneration = 0;
        _activeDeskCode = -1;
        _activeWorkerCount = 0;
        _activeRenderableWorkerCount = 0;
        _activeSelectionRendererCount = 0;
        _activeRenderableSelectionRendererCount = 0;
        _activeCellPosition = default;
        _activeAnchor = default;
        _activeVisibilityDeadline = 0f;
        _nextVisibilityCheckAt = 0f;
        _renderDiagnostics = "none";
    }

    private static bool Approximately(Vector3 left, Vector3 right)
    {
        const float tolerance = 0.001f;
        return Math.Abs(left.x - right.x) <= tolerance
            && Math.Abs(left.y - right.y) <= tolerance
            && Math.Abs(left.z - right.z) <= tolerance;
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

    private static string FormatVector(Vector3Int value)
    {
        return $"{value.x},{value.y},{value.z}";
    }

    private static string DescribeRenderer(SpriteRenderer renderer)
    {
        try
        {
            var rendererObject = renderer.gameObject;
            var bounds = renderer.bounds;
            var material = renderer.sharedMaterial;
            var shaderName = material?.shader?.name ?? "none";
            var renderQueue = material?.renderQueue ?? -1;
            return $"visible:{renderer.isVisible},enabled:{renderer.enabled},active:{rendererObject?.activeInHierarchy == true},layer:{rendererObject?.layer ?? -1},sorting:{renderer.sortingLayerID}/{renderer.sortingOrder},alpha:{renderer.color.a:0.###},bounds:{FormatVector(bounds.center)}/{FormatVector(bounds.size)},shader:{NormalizeStatus(shaderName)},queue:{renderQueue}";
        }
        catch (Exception ex)
        {
            return $"inspection-error:{NormalizeStatus(ex.GetBaseException().Message)}";
        }
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

    private sealed record SeatHighlightTargetSnapshot(
        long Generation,
        long SessionGeneration,
        bool Enabled,
        int DeskCode)
    {
        public static readonly SeatHighlightTargetSnapshot Disabled = new(0, 0, false, -1);

        public bool HasSameValues(long sessionGeneration, bool enabled, int deskCode)
        {
            return SessionGeneration == sessionGeneration
                && Enabled == enabled
                && DeskCode == deskCode;
        }
    }
}
