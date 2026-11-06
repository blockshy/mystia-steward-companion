using System.Collections.Concurrent;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

internal static class RuntimePinnedListHighlightService
{
    private const string CookingSelectionPanelTypeName = "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel";
    private const string StoragePanelTypeName = "NightScene.UI.CookingUtility.WorkSceneStoragePannel";
    private const int ExpectedHookCount = 9;
    private const string SharedCookingDestroyHookStatus =
        "cookingDestroy=unpatched-shared-native-alias";
    private const int MaximumExactRecipeLifecycleLogsPerBusiness = 32;

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<nint, TrackedImage> TrackedImages = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Type, ButtonAccessors> ButtonAccessorCache = new();
    // Tick 仅由主线程调用；重复使用工作缓冲，避免每帧 ToList/Select 分配。
    private static readonly List<TrackedImage> TickCandidates = new();
    private static readonly List<TrackedImageRelease> TickReleases = new();
    private static readonly List<TrackedImage> PendingDisposals = new();
    private static bool _ticking;
    // Graphic 共用 VertexHelper；只有最外层主线程背景回调可以读取和提交其临时网格。
    private static bool _populatingNativeMesh;
    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static long _missingImages;
    private static long _bindingErrors;
    private static long _visualErrors;
    private static long _restoreErrors;
    private static long _visualBindingWarningGeneration;
    private static int _visualBindingWarnings;
    private static string _lastBindingFailure = "none";
    private static long _exactRecipeLogBusinessGeneration;
    private static int _exactRecipeLifecycleLogs;
    private static long _suppressedExactRecipeLifecycleLogs;
    private static bool _bindingWarningLogged;
    private static bool _suspended;
    private static string _suspendReason = "scene unavailable";
    private static string _hookStatus = "not attached";
    private static string _state = "disabled";

    public static string Status
    {
        get
        {
            var target = RuntimeUiPinningService.ReadTargetSet();
            lock (SyncRoot)
            {
                var recipeCount = TrackedImages.Values.Count(item => item.ItemKind == ItemKind.Recipe);
                var ingredientCount = TrackedImages.Values.Count(item => item.ItemKind == ItemKind.Ingredient);
                var beverageCount = TrackedImages.Values.Count(item => item.ItemKind == ItemKind.Beverage);
                var state = _suspended
                    ? _state
                    : !HasListPinningTargets(target)
                        ? TrackedImages.Count == 0 ? "disabled" : "pending disable"
                        : TrackedImages.Values.Any(item => item.TargetGeneration == target.Generation)
                            ? _state
                            : TrackedImages.Count == 0 ? "waiting for target elements" : "pending target refresh";
                return $"hooks={_hookStatus}; state={state}; tracked=recipe:{recipeCount}, ingredients:{ingredientCount}, beverage:{beverageCount}; missingImage={_missingImages}; bindingErrors={_bindingErrors}; visualErrors={_visualErrors}; restoreErrors={_restoreErrors}; lastBindingFailure={_lastBindingFailure}; exactRecipeLogs={_exactRecipeLifecycleLogs}/{MaximumExactRecipeLifecycleLogsPerBusiness}; exactRecipeLogGeneration={_exactRecipeLogBusinessGeneration}; exactRecipeLogsSuppressed={_suppressedExactRecipeLifecycleLogs}";
            }
        }
    }

    public static void Attach(ManualLogSource log)
    {
        _log = log;
        try
        {
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.runtime-pinned-list-highlight");
            var patchedNow = new List<string>();
            var missing = new List<string>();

            PatchPanelPrefixMethod(_harmony, CookingSelectionPanelTypeName, "OnPanelOpen", nameof(BeforePanelOpen), patchedNow, missing);
            PatchPanelPrefixMethod(_harmony, StoragePanelTypeName, "OnPanelOpen", nameof(BeforePanelOpen), patchedNow, missing);
            PatchElementMethod(_harmony, CookingSelectionPanelTypeName, "OnRecipeElementEnabled", nameof(AfterRecipeItemEnabled), patchedNow, missing);
            PatchElementMethod(_harmony, CookingSelectionPanelTypeName, "OnIngElementEnabled", nameof(AfterIngredientItemEnabled), patchedNow, missing);
            PatchElementMethod(_harmony, StoragePanelTypeName, "OnElementEnabled", nameof(AfterStorageItemEnabled), patchedNow, missing);
            PatchPanelPrefixMethod(_harmony, CookingSelectionPanelTypeName, "OnPanelClose", nameof(BeforeCookingPanelTeardown), patchedNow, missing);
            PatchPanelPrefixMethod(_harmony, StoragePanelTypeName, "OnPanelClose", nameof(BeforeStoragePanelTeardown), patchedNow, missing);
            PatchPanelPrefixMethod(_harmony, StoragePanelTypeName, "OnPanelDestroyed", nameof(BeforeStoragePanelTeardown), patchedNow, missing);
            PatchNativeBackgroundMesh(_harmony, patchedNow, missing);

            lock (SyncRoot)
            {
                _hookStatus = PatchedMethods.Count == ExpectedHookCount
                    ? $"patched:{ExpectedHookCount}/{ExpectedHookCount};{SharedCookingDestroyHookStatus}"
                    : $"partial:{PatchedMethods.Count}/{ExpectedHookCount};{SharedCookingDestroyHookStatus}";
            }

            if (patchedNow.Count > 0)
            {
                log.LogInfo($"Runtime pinned list highlight patched: {string.Join(", ", patchedNow)}.");
            }
            if (missing.Count > 0)
            {
                log.LogWarning($"Runtime pinned list highlight unavailable; game members were not found: {string.Join(", ", missing.Take(3))}.");
            }
            log.LogInfo(
                "Runtime pinned list highlight intentionally leaves "
                + "WorkSceneCookingSelectionPannel.OnPanelDestroyed unpatched because it shares an empty native alias.");
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                _hookStatus = $"error:{ex.GetBaseException().Message}";
            }

            log.LogWarning($"Runtime pinned list highlight attach failed: {ex.Message}");
        }
    }

    public static void Tick()
    {
        if (Environment.CurrentManagedThreadId != RuntimeNightBusinessLifecycle.Snapshot.ThreadId) return;
        // 原生脏标记或诊断接收器可能触发嵌套刷新；共享工作缓冲只允许最外层 Tick 使用。
        if (_ticking) return;
        _ticking = true;
        try { TickCore(); }
        finally
        {
            // 保留缓冲容量，但不让停业后不再执行的 Tick 间接持有已退休的原生 wrapper。
            TickCandidates.Clear();
            TickReleases.Clear();
            _ticking = false;
        }
    }

    private static void TickCore()
    {
        DrainPendingDisposals();
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        var target = RuntimeUiPinningService.ReadTargetSet();
        var generation = target.Generation;
        lock (SyncRoot)
        {
            if (_suspended) return;
            TickCandidates.Clear();
            TickReleases.Clear();
            foreach (var item in TrackedImages.Values)
            {
                var reason = ClassifyTargetRelease(item, target);
                if (reason == ExactRecipeReleaseReason.None) TickCandidates.Add(item);
                else TickReleases.Add(new TrackedImageRelease(item, reason));
            }
            foreach (var release in TickReleases)
            {
                RemoveTrackedImageLocked(release.Item);
            }
            if (!HasListPinningTargets(target))
            {
                _state = "disabled";
            }
        }

        foreach (var release in TickReleases)
        {
            RestoreTrackedImage(release.Item);
            TryLogExactRecipeLifecycle(release.Item, ExactRecipeLifecycleEvent.Released, release.Reason);
        }
        if (!IsGenerationEnabled(generation)) return;

        foreach (var item in TickCandidates)
        {
            if (!TryReadCurrentClaims(item, generation, out var claims, out var palette))
            {
                RemoveStaleTrackedImage(
                    item,
                    ClassifyLeaseRelease(item, RuntimeUiPinningService.ReadTargetSet()));
                continue;
            }
            if (!TryApplyHighlight(item, generation, claims, palette)) continue;
        }

        var latestTarget = RuntimeUiPinningService.ReadTargetSet();
        lock (SyncRoot)
        {
            if (_suspended) return;
            _state = !HasListPinningTargets(latestTarget)
                ? "disabled"
                : HasCurrentTrackingLocked(latestTarget.Generation)
                    ? "active"
                    : "waiting for target elements";
        }
    }

    public static void Suspend(string reason)
    {
        var target = RuntimeUiPinningService.ReadTargetSet();
        List<TrackedImage> images;
        lock (SyncRoot)
        {
            _suspended = true;
            _suspendReason = string.IsNullOrWhiteSpace(reason) ? "scene unavailable" : reason.Trim();
            images = TakeAllTrackedImagesLocked();
            _state = HasListPinningTargets(target) ? $"suspended: {_suspendReason}" : "disabled";
        }

        RestoreAndLogTrackedImages(
            images.Select(item => new TrackedImageRelease(
                item,
                ExactRecipeReleaseReason.ServiceSuspended)));
    }

    public static void Resume(string reason)
    {
        var target = RuntimeUiPinningService.ReadTargetSet();
        lock (SyncRoot)
        {
            _suspended = false;
            _suspendReason = string.IsNullOrWhiteSpace(reason) ? "night business active" : reason.Trim();
            _state = HasListPinningTargets(target) ? "waiting for target elements" : "disabled";
        }
    }

    /// <summary>
    /// 场景销毁后撤销背景登记；仍存活的 Image 仅请求网格重建，不回写旧图像属性。
    /// </summary>
    public static void Abandon(string reason)
    {
        List<TrackedImage> images;
        lock (SyncRoot)
        {
            images = TakeAllTrackedImagesLocked();
            _suspended = true;
            _suspendReason = string.IsNullOrWhiteSpace(reason) ? "night business destroyed" : reason.Trim();
            _state = $"abandoned: {_suspendReason}";
        }

        RestoreAndLogTrackedImages(
            images.Select(item => new TrackedImageRelease(
                item,
                ExactRecipeReleaseReason.ServiceAbandoned)));
    }

    private static void PatchElementMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        string postfixName,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/3";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        try
        {
            var type = RuntimeReflectionUtility.FindType(typeName);
            var target = FindMethod(type, methodName, 3);
            var prefix = typeof(RuntimePinnedListHighlightService).GetMethod(nameof(BeforeItemEnabled), BindingFlags.NonPublic | BindingFlags.Static);
            var postfix = typeof(RuntimePinnedListHighlightService).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
            if (target == null || prefix == null || postfix == null)
            {
                missing.Add(key);
                return;
            }

            harmony.Patch(
                target,
                prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                postfix: new HarmonyMethod(postfix)
                {
                    priority = Priority.Last,
                    after = new[] { RuntimeTargetRecipeVariantService.HarmonyId },
                });
            lock (SyncRoot)
            {
                PatchedMethods.Add(key);
            }

            patchedNow.Add(key);
        }
        catch (Exception ex)
        {
            missing.Add($"{key} ({ex.GetBaseException().Message})");
        }
    }

    private static void PatchPanelPrefixMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        string prefixName,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/0";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        try
        {
            var type = RuntimeReflectionUtility.FindType(typeName);
            var target = FindMethod(type, methodName, 0);
            var prefix = typeof(RuntimePinnedListHighlightService).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
            if (target == null || prefix == null)
            {
                missing.Add(key);
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
            lock (SyncRoot)
            {
                PatchedMethods.Add(key);
            }

            patchedNow.Add(key);
        }
        catch (Exception ex)
        {
            missing.Add($"{key} ({ex.GetBaseException().Message})");
        }
    }

    private static MethodInfo? FindMethod(Type? type, string methodName, int parameterCount)
    {
        return type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == parameterCount);
    }

    /// <summary>
    /// #783 的 Image.OnPopulateMesh(VertexHelper) 对应唯一 Native 入口 0x2CF3110、虚表槽 44。
    /// Graphic.DoMeshGeneration（0x2CEB0B0）在该方法后才执行网格特效和 CanvasRenderer 提交，
    /// 因而此处可以在原背景纹理、九宫格与填充形状已经生成后分色，继续使用游戏的遮罩及绘制层级。
    /// 必须精确匹配 VertexHelper 重载，不能误补丁旧 Mesh 重载或 Graphic 的共享基类入口。
    /// </summary>
    private static void PatchNativeBackgroundMesh(Harmony harmony,
        ICollection<string> patchedNow, ICollection<string> missing)
    {
        const string key = "UnityEngine.UI.Image.OnPopulateMesh(VertexHelper)";
        lock (SyncRoot) { if (PatchedMethods.Contains(key)) return; }
        try
        {
            var imageType = RuntimeReflectionUtility.FindType("UnityEngine.UI.Image");
            var helperType = RuntimeReflectionUtility.FindType("UnityEngine.UI.VertexHelper");
            var method = helperType == null ? null : imageType?.GetMethod("OnPopulateMesh",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, new[] { helperType }, null);
            var postfix = typeof(RuntimePinnedListHighlightService).GetMethod(
                nameof(AfterNativeBackgroundMesh), BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null || method.ReturnType != typeof(void) || postfix == null)
            { missing.Add(key); return; }
            harmony.Patch(method, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
            lock (SyncRoot) PatchedMethods.Add(key);
            patchedNow.Add(key);
        }
        catch (Exception exception)
        { missing.Add($"{key} ({exception.GetBaseException().Message})"); }
    }

    /// <summary>
    /// 只处理已登记的精确背景 Image，物品图标和其它原生 UI 直接返回。
    /// VertexHelper 是 Unity 共享缓冲，只能在本次回调中同步使用，不能保存到下一帧。
    /// 绘制前后复核当前订单声明；后台若在分色期间发布新目标，本次网格立即恢复原样，
    /// 避免已经撤回的订单在 Canvas 提交时仍闪现一帧。未知状态不向原生渲染循环抛异常。
    /// </summary>
    private static void AfterNativeBackgroundMesh(object __instance, object __0)
    {
        if (_populatingNativeMesh || !RuntimeNightBusinessLifecycle.IsActive
            || Environment.CurrentManagedThreadId != RuntimeNightBusinessLifecycle.Snapshot.ThreadId) return;
        TrackedImage? item = null;
        var meshApplied = false;
        _populatingNativeMesh = true;
        try
        {
            if (__instance is not UnityEngine.Object image || image == null) return;
            lock (SyncRoot)
            {
                if (_suspended || !TrackedImages.TryGetValue(image.Pointer, out item)) return;
            }
            var generation = item.TargetGeneration;
            if (!TryReadCurrentClaims(item, generation, out var claims, out var palette)) return;
            if (!item.Blocks.ApplyToNativeMesh(__0, claims, palette, out var failure))
            {
                NoteVisualBindingFailure(failure, item.BusinessGeneration);
                RemoveFailedVisual(item, ExactRecipeReleaseReason.VisualWriteFailed);
                return;
            }
            meshApplied = true;
            if (TryReadCurrentClaims(item, generation, out _, out _)) return;
            if (!item.Blocks.RestoreNativeMesh(__0, out var restoreFailure))
            {
                lock (SyncRoot) _restoreErrors++;
                NoteVisualBindingFailure(restoreFailure, item.BusinessGeneration);
            }
            RemoveStaleTrackedImage(item,
                ClassifyLeaseRelease(item, RuntimeUiPinningService.ReadTargetSet(), postWrite: true));
        }
        catch
        {
            // 绘制后的身份复核也可能失败。Canvas 正处于重建阶段，单独 SetVerticesDirty
            // 不足以保证本帧恢复，所以先同步还原共享缓冲，再撤销登记；诊断异常不得外溢。
            if (item != null)
            {
                try
                {
                    if (meshApplied && !item.Blocks.RestoreNativeMesh(__0, out _))
                        lock (SyncRoot) _restoreErrors++;
                }
                catch { lock (SyncRoot) _restoreErrors++; }
                try { RemoveFailedVisual(item, ExactRecipeReleaseReason.VisualWriteFailed); }
                catch { lock (SyncRoot) _visualErrors++; }
            }
        }
        finally { _populatingNativeMesh = false; }
    }

    private static void BeforePanelOpen()
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        var target = RuntimeUiPinningService.ReadTargetSet();
        lock (SyncRoot)
        {
            _suspended = false;
            _suspendReason = "scene unavailable";
            _state = HasListPinningTargets(target) ? "waiting for target elements" : "disabled";
        }
    }

    private static void BeforeItemEnabled(object __2)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive
            || Environment.CurrentManagedThreadId != RuntimeNightBusinessLifecycle.Snapshot.ThreadId) return;

        try
        {
            var image = ReadButtonImage(__2);
            if (image == null) return;

            var pointer = RuntimeReflectionUtility.ReadObjectPointer(image);
            if (pointer == IntPtr.Zero) return;

            TrackedImage? trackedImage;
            lock (SyncRoot)
            {
                TrackedImages.Remove(pointer, out trackedImage);
            }

            if (trackedImage != null)
            {
                RestoreTrackedImage(trackedImage);
                TryLogExactRecipeLifecycle(
                    trackedImage,
                    ExactRecipeLifecycleEvent.Released,
                    ExactRecipeReleaseReason.PoolRebind);
            }
        }
        catch (Exception ex)
        {
            NoteBindingError("item prefix", ex);
        }
    }

    private static void AfterRecipeItemEnabled(object __instance, object __0, object __1, object __2)
    {
        TryRegisterRecipe(__instance, __0, __1, __2);
    }

    private static void AfterIngredientItemEnabled(object __0, object __1, object __2)
    {
        TryRegisterByItemId(__0, __1, __2, PanelKind.Cooking, ItemKind.Ingredient);
    }

    private static void AfterStorageItemEnabled(object __0, object __1, object __2)
    {
        TryRegisterByItemId(__0, __1, __2, PanelKind.Storage, ItemKind.Beverage);
    }

    private static void BeforeCookingPanelTeardown()
    {
        ReconcilePanelTeardown(
            PanelKind.Cooking,
            ExactRecipeReleaseReason.PanelClosed);
    }

    private static void BeforeStoragePanelTeardown(MethodBase __originalMethod)
    {
        ReconcilePanelTeardown(
            PanelKind.Storage,
            __originalMethod.Name == "OnPanelDestroyed"
                ? ExactRecipeReleaseReason.PanelDestroyed
                : ExactRecipeReleaseReason.PanelClosed);
    }

    private static void TryRegisterRecipe(object panel, object recipe, object cluster, object button)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive
            || Environment.CurrentManagedThreadId != RuntimeNightBusinessLifecycle.Snapshot.ThreadId) return;

        try
        {
            var itemId = RuntimeReflectionUtility.ToInt(
                RuntimeReflectionUtility.GetMemberValue(recipe, "id"),
                -1);
            if (itemId < 0) return;

            var target = RuntimeUiPinningService.ReadTargetSet();
            if (!target.HasRecipeVariants(itemId))
            {
                if (target.GetRecipeClaims(itemId) == RuntimeUiTargetKinds.None) return;
                TryRegisterImage(
                    button,
                    cluster,
                    ReadItemVisual(recipe),
                    PanelKind.Cooking,
                    ItemKind.Recipe,
                    itemId,
                    target,
                    usesExactRecipeRowLease: false,
                    default);
                return;
            }

            if (!RuntimeTargetRecipeVariantService.TryResolveRecipeRowClaims(
                    panel,
                    recipe,
                    button,
                    out var claims,
                    out var lease)
                || claims == RuntimeUiTargetKinds.None)
            {
                return;
            }

            TryRegisterImage(
                button,
                cluster,
                ReadItemVisual(recipe),
                PanelKind.Cooking,
                ItemKind.Recipe,
                itemId,
                target,
                usesExactRecipeRowLease: true,
                lease);
        }
        catch (Exception ex)
        {
            NoteBindingError("recipe postfix", ex);
        }
    }

    private static void TryRegisterByItemId(
        object data,
        object cluster,
        object button,
        PanelKind panelKind,
        ItemKind itemKind)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive
            || Environment.CurrentManagedThreadId != RuntimeNightBusinessLifecycle.Snapshot.ThreadId) return;

        try
        {
            var item = RuntimeReflectionUtility.GetMemberValue(data, "Key");
            if (item == null) return;

            if (itemKind == ItemKind.Beverage
                && RuntimeReflectionUtility.ToInt(RuntimeReflectionUtility.GetMemberValue(item, "Type"), -1) != 1)
            {
                return;
            }

            var itemId = RuntimeReflectionUtility.ToInt(RuntimeReflectionUtility.GetMemberValue(item, "id"), -1);
            if (itemId < 0) return;

            var target = RuntimeUiPinningService.ReadTargetSet();
            lock (SyncRoot)
            {
                if (_suspended || !MatchesTarget(target, itemKind, itemId)) return;
            }

            TryRegisterImage(
                button,
                cluster,
                ReadItemVisual(item),
                panelKind,
                itemKind,
                itemId,
                target,
                usesExactRecipeRowLease: false,
                default);
        }
        catch (Exception ex)
        {
            NoteBindingError($"{itemKind} postfix", ex);
        }
    }

    private static void TryRegisterImage(
        object button,
        object cluster,
        object? expectedSprite,
        PanelKind panelKind,
        ItemKind itemKind,
        int itemId,
        RuntimeUiTargetSetSnapshot target,
        bool usesExactRecipeRowLease,
        TargetRecipeVariantRowLease recipeRowLease)
    {
        var exactRecipeClaims = RuntimeUiTargetKinds.None;
        if (usesExactRecipeRowLease
            && (!RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(
                    recipeRowLease,
                    out exactRecipeClaims)
                || exactRecipeClaims == RuntimeUiTargetKinds.None))
        {
            return;
        }

        var image = ReadButtonImage(button);
        if (image == null)
        {
            IncrementMissingImage();
            return;
        }

        var pointer = RuntimeReflectionUtility.ReadObjectPointer(image);
        if (pointer == IntPtr.Zero) return;

        // 只登记已证实的原背景绑定；分色在 Image 原生网格回调中完成，不添加覆盖矩形。
        var blocks = RuntimePinnedListColorBlocks.TryCreate(button, image, cluster, expectedSprite, out var failure);
        if (blocks == null) { NoteVisualBindingFailure(failure, target.SessionGeneration); return; }

        var latestTarget = RuntimeUiPinningService.ReadTargetSet();
        var latestClaims = RuntimeUiTargetKinds.None;
        if (usesExactRecipeRowLease
            && (!RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(
                    recipeRowLease,
                    out latestClaims)
                || latestClaims == RuntimeUiTargetKinds.None))
        {
            blocks.Dispose();
            return;
        }
        if (usesExactRecipeRowLease) exactRecipeClaims = latestClaims;

        TrackedImage? replaced = null;
        TrackedImage? registered = null;
        lock (SyncRoot)
        {
            if (_suspended
                || target.Generation != latestTarget.Generation
                || !MatchesTrackingMode(
                    latestTarget,
                    itemKind,
                    itemId,
                    usesExactRecipeRowLease))
            {
                blocks.Dispose();
                return;
            }

            TrackedImages.Remove(pointer, out replaced);
            registered = new TrackedImage(
                image,
                pointer,
                panelKind,
                itemKind,
                itemId,
                target.Generation,
                target.SessionGeneration,
                blocks,
                usesExactRecipeRowLease,
                recipeRowLease,
                exactRecipeClaims);
            TrackedImages[pointer] = registered;
            _state = "active";
        }

        if (replaced != null)
        {
            RestoreTrackedImage(replaced);
            TryLogExactRecipeLifecycle(
                replaced,
                ExactRecipeLifecycleEvent.Released,
                ExactRecipeReleaseReason.RegistrationReplaced);
        }
        if (registered != null)
        {
            TryLogExactRecipeLifecycle(
                registered,
                ExactRecipeLifecycleEvent.Bound,
                ExactRecipeReleaseReason.None);
        }
    }

    private static bool TryApplyHighlight(TrackedImage item, long generation,
        RuntimeUiTargetKinds claims, RuntimeTargetHighlightPalette palette)
    {
        var target = RuntimeUiPinningService.ReadTargetSet();
        lock (SyncRoot)
        {
            if (!IsCurrentTrackingLocked(item, generation, target)) return false;
        }

        try
        {
            if (!item.Blocks.Apply(claims, palette))
            {
                RemoveFailedVisual(item, ExactRecipeReleaseReason.VisualWriteFailed);
                return false;
            }

        }
        catch
        {
            RemoveFailedVisual(
                item,
                ExactRecipeReleaseReason.VisualWriteFailed,
                forceRestore: true);
            return false;
        }

        if (TryReadCurrentClaims(item, generation, out _, out _))
        {
            if (item.UsesExactRecipeRowLease
                && Interlocked.CompareExchange(ref item.ExactRecipeAppliedLogged, 1, 0) == 0)
            {
                TryLogExactRecipeLifecycle(
                    item,
                    ExactRecipeLifecycleEvent.Applied,
                    ExactRecipeReleaseReason.None);
            }
            return true;
        }

        var restore = false;
        lock (SyncRoot)
        {
            restore = RemoveTrackedImageLocked(item);
        }

        if (restore)
        {
            RestoreTrackedImage(item);
            TryLogExactRecipeLifecycle(
                item,
                ExactRecipeLifecycleEvent.Released,
                ClassifyLeaseRelease(item, RuntimeUiPinningService.ReadTargetSet(), postWrite: true));
        }
        return false;
    }

    private static bool IsGenerationEnabled(long generation)
    {
        var target = RuntimeUiPinningService.ReadTargetSet();
        lock (SyncRoot)
        {
            return !_suspended && HasListPinningTargets(target) && target.Generation == generation;
        }
    }

    private static bool IsCurrentTrackingLocked(
        TrackedImage item,
        long generation,
        RuntimeUiTargetSetSnapshot target)
    {
        return !_suspended
            && HasListPinningTargets(target)
            && target.Generation == generation
            && item.TargetGeneration == generation
            && TrackedImages.TryGetValue(item.Pointer, out var current)
            && ReferenceEquals(current, item)
            && MatchesTrackingMode(
                target,
                item.ItemKind,
                item.ItemId,
                item.UsesExactRecipeRowLease);
    }

    private static bool MatchesTarget(RuntimeUiTargetSetSnapshot target, ItemKind itemKind, int itemId)
    {
        return HasListPinningTargets(target)
            && GetUncontrolledClaims(target, itemKind, itemId) != RuntimeUiTargetKinds.None;
    }

    private static bool MatchesTrackingMode(
        RuntimeUiTargetSetSnapshot target,
        ItemKind itemKind,
        int itemId,
        bool usesExactRecipeRowLease)
    {
        if (!HasListPinningTargets(target)) return false;
        if (itemKind != ItemKind.Recipe)
        {
            return GetUncontrolledClaims(target, itemKind, itemId) != RuntimeUiTargetKinds.None;
        }

        var recipeIsVariantControlled = target.HasRecipeVariants(itemId);
        return usesExactRecipeRowLease
            ? recipeIsVariantControlled
            : !recipeIsVariantControlled
                && target.GetRecipeClaims(itemId) != RuntimeUiTargetKinds.None;
    }

    private static bool TryReadCurrentClaims(
        TrackedImage item,
        long generation,
        out RuntimeUiTargetKinds claims,
        out RuntimeTargetHighlightPalette palette)
    {
        claims = RuntimeUiTargetKinds.None;
        palette = default;
        var target = RuntimeUiPinningService.ReadTargetSet();
        if (item.UsesExactRecipeRowLease)
        {
            if (!RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(
                    item.RecipeRowLease,
                    out claims)
                || claims == RuntimeUiTargetKinds.None)
            {
                return false;
            }
        }
        else
        {
            claims = GetUncontrolledClaims(target, item.ItemKind, item.ItemId);
            if (claims == RuntimeUiTargetKinds.None) return false;
        }

        var latestTarget = RuntimeUiPinningService.ReadTargetSet();
        lock (SyncRoot)
        {
            if (!IsCurrentTrackingLocked(item, generation, latestTarget)) return false;
            palette = latestTarget.Palette;
            return true;
        }
    }

    private static RuntimeUiTargetKinds GetUncontrolledClaims(
        RuntimeUiTargetSetSnapshot target,
        ItemKind itemKind,
        int itemId)
    {
        return itemKind switch
        {
            ItemKind.Recipe => target.HasRecipeVariants(itemId)
                ? RuntimeUiTargetKinds.None
                : target.GetRecipeClaims(itemId),
            ItemKind.Ingredient => target.GetIngredientClaims(itemId),
            ItemKind.Beverage => target.GetBeverageClaims(itemId),
            _ => RuntimeUiTargetKinds.None,
        };
    }

    private static object? ReadButtonImage(object button)
    {
        var accessors = ButtonAccessorCache.GetOrAdd(button.GetType(), static type =>
        {
            var getImage = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == "get_image" && method.GetParameters().Length == 0);
            return new ButtonAccessors(getImage);
        });

        return accessors.GetImage?.Invoke(button, null);
    }

    /// <summary>
    /// 与三个 Native 元素绑定回调读取同一个当前数据对象的 Text.Visual。
    /// 不能按数值 ID 去数据库补图标，否则跨类 ID 或池复用会把别项误当当前图标。
    /// </summary>
    private static object? ReadItemVisual(object item)
    {
        var text = RuntimeReflectionUtility.GetMemberValue(item, "Text");
        return text == null ? null : RuntimeReflectionUtility.GetMemberValue(text, "Visual");
    }

    private static void RestorePanel(
        PanelKind panelKind,
        ExactRecipeReleaseReason releaseReason)
    {
        var target = RuntimeUiPinningService.ReadTargetSet();
        List<TrackedImage> images;
        lock (SyncRoot)
        {
            images = TrackedImages.Values.Where(item => item.PanelKind == panelKind).ToList();
            foreach (var item in images)
            {
                RemoveTrackedImageLocked(item);
            }

            _state = _suspended
                ? HasListPinningTargets(target) ? $"suspended: {_suspendReason}" : "disabled"
                : HasListPinningTargets(target) ? "waiting for target elements" : "disabled";
        }

        RestoreAndLogTrackedImages(
            images.Select(item => new TrackedImageRelease(item, releaseReason)));
    }

    private static bool HasListPinningTargets(RuntimeUiTargetSetSnapshot targetSet)
    {
        for (var index = 0; index < targetSet.Targets.Count; index++)
            if (targetSet.Targets[index].ListPinningEnabled) return true;
        return false;
    }

    private static bool HasCurrentTrackingLocked(long generation)
    {
        foreach (var item in TrackedImages.Values)
            if (item.TargetGeneration == generation) return true;
        return false;
    }

    private static void ReconcilePanelTeardown(
        PanelKind panelKind,
        ExactRecipeReleaseReason releaseReason)
    {
        if (RuntimeNightBusinessLifecycle.Snapshot.Phase == NightBusinessLifecyclePhase.Destroyed)
        {
            List<TrackedImage> images;
            lock (SyncRoot)
            {
                images = TrackedImages.Values
                    .Where(item => item.PanelKind == panelKind)
                    .ToList();
                foreach (var item in images)
                {
                    RemoveTrackedImageLocked(item);
                }
            }
            RestoreAndLogTrackedImages(
                images.Select(item => new TrackedImageRelease(
                    item,
                    ExactRecipeReleaseReason.BusinessDestroyed)));
            return;
        }

        RestorePanel(panelKind, releaseReason);
    }

    private static List<TrackedImage> TakeAllTrackedImagesLocked()
    {
        var images = TrackedImages.Values.ToList();
        TrackedImages.Clear();
        return images;
    }

    private static bool RemoveTrackedImageLocked(TrackedImage item)
    {
        if (!TrackedImages.TryGetValue(item.Pointer, out var current) || !ReferenceEquals(current, item))
        {
            return false;
        }

        TrackedImages.Remove(item.Pointer);
        return true;
    }

    private static void RemoveFailedVisual(
        TrackedImage item,
        ExactRecipeReleaseReason releaseReason,
        bool forceRestore = false)
    {
        var removed = false;
        lock (SyncRoot)
        {
            removed = RemoveTrackedImageLocked(item);
            if (removed) _visualErrors++;
        }

        if (removed)
        {
            RestoreTrackedImage(item, forceRestore);
            TryLogExactRecipeLifecycle(
                item,
                ExactRecipeLifecycleEvent.Released,
                releaseReason);
        }
    }

    private static void RemoveStaleTrackedImage(
        TrackedImage item,
        ExactRecipeReleaseReason releaseReason)
    {
        var removed = false;
        lock (SyncRoot)
        {
            removed = RemoveTrackedImageLocked(item);
        }

        if (removed)
        {
            RestoreTrackedImage(item);
            TryLogExactRecipeLifecycle(
                item,
                ExactRecipeLifecycleEvent.Released,
                releaseReason);
        }
    }

    private static void RestoreAndLogTrackedImages(
        IEnumerable<TrackedImageRelease> releases)
    {
        foreach (var release in releases)
        {
            RestoreTrackedImage(release.Item);
            TryLogExactRecipeLifecycle(
                release.Item,
                ExactRecipeLifecycleEvent.Released,
                release.Reason);
        }
    }

    private static void LogTrackedImageReleases(
        IEnumerable<TrackedImageRelease> releases)
    {
        foreach (var release in releases)
        {
            TryLogExactRecipeLifecycle(
                release.Item,
                ExactRecipeLifecycleEvent.Released,
                release.Reason);
        }
    }

    private static void RestoreTrackedImage(TrackedImage item, bool force = false)
    {
        // 非主线程关闭只排队，不访问 Unity wrapper。下一次主线程 Tick 即使场景已停也先清理。
        if (Environment.CurrentManagedThreadId != RuntimeNightBusinessLifecycle.Snapshot.ThreadId)
        {
            lock (SyncRoot) PendingDisposals.Add(item);
            return;
        }

        try
        {
            item.Blocks.Dispose();
            return;
        }
        catch
        {
            // 网格重建请求失败只记录诊断；绝不回写原生池图像的旧颜色作为兜底。
        }

        lock (SyncRoot)
        {
            _restoreErrors++;
        }
    }

    private static void DrainPendingDisposals()
    {
        lock (SyncRoot)
        {
            foreach (var item in PendingDisposals)
            {
                try { item.Blocks.Dispose(); }
                catch { _restoreErrors++; }
            }
            PendingDisposals.Clear();
        }
    }

    private static ExactRecipeReleaseReason ClassifyTargetRelease(
        TrackedImage item,
        RuntimeUiTargetSetSnapshot target)
    {
        if (!HasListPinningTargets(target))
        {
            return ExactRecipeReleaseReason.ListPinningDisabled;
        }
        if (item.TargetGeneration != target.Generation)
        {
            return ExactRecipeReleaseReason.TargetGenerationChanged;
        }
        return MatchesTrackingMode(
                target,
                item.ItemKind,
                item.ItemId,
                item.UsesExactRecipeRowLease)
            ? ExactRecipeReleaseReason.None
            : ExactRecipeReleaseReason.TrackingModeChanged;
    }

    private static ExactRecipeReleaseReason ClassifyLeaseRelease(
        TrackedImage item,
        RuntimeUiTargetSetSnapshot target,
        bool postWrite = false)
    {
        var targetReason = ClassifyTargetRelease(item, target);
        if (targetReason != ExactRecipeReleaseReason.None) return targetReason;
        return postWrite
            ? ExactRecipeReleaseReason.PostWriteLeaseInvalid
            : ExactRecipeReleaseReason.ExactLeaseInvalid;
    }

    private static void TryLogExactRecipeLifecycle(
        TrackedImage item,
        ExactRecipeLifecycleEvent lifecycleEvent,
        ExactRecipeReleaseReason releaseReason)
    {
        if (item.ItemKind != ItemKind.Recipe
            || !item.UsesExactRecipeRowLease
            || item.BusinessGeneration <= 0)
        {
            return;
        }

        ManualLogSource? log;
        lock (SyncRoot)
        {
            if (_log == null) return;
            if (_exactRecipeLogBusinessGeneration > item.BusinessGeneration)
            {
                _suppressedExactRecipeLifecycleLogs++;
                return;
            }
            if (_exactRecipeLogBusinessGeneration < item.BusinessGeneration)
            {
                _exactRecipeLogBusinessGeneration = item.BusinessGeneration;
                _exactRecipeLifecycleLogs = 0;
                _suppressedExactRecipeLifecycleLogs = 0;
            }
            if (_exactRecipeLifecycleLogs >= MaximumExactRecipeLifecycleLogsPerBusiness)
            {
                _suppressedExactRecipeLifecycleLogs++;
                return;
            }

            _exactRecipeLifecycleLogs++;
            log = _log;
        }

        var lease = item.RecipeRowLease;
        var eventName = lifecycleEvent switch
        {
            ExactRecipeLifecycleEvent.Bound => "bound",
            ExactRecipeLifecycleEvent.Applied => "applied",
            ExactRecipeLifecycleEvent.Released => "released",
            _ => "unknown",
        };
        var reason = releaseReason switch
        {
            ExactRecipeReleaseReason.None => "none",
            ExactRecipeReleaseReason.ListPinningDisabled => "list-pinning-disabled",
            ExactRecipeReleaseReason.TargetGenerationChanged => "target-generation-changed",
            ExactRecipeReleaseReason.TrackingModeChanged => "tracking-mode-changed",
            ExactRecipeReleaseReason.PoolRebind => "pool-rebind",
            ExactRecipeReleaseReason.RegistrationReplaced => "registration-replaced",
            ExactRecipeReleaseReason.ExactLeaseInvalid => "exact-lease-invalid",
            ExactRecipeReleaseReason.PostWriteLeaseInvalid => "post-write-lease-invalid",
            ExactRecipeReleaseReason.VisualReadFailed => "visual-read-failed",
            ExactRecipeReleaseReason.VisualWriteFailed => "visual-write-failed",
            ExactRecipeReleaseReason.PanelClosed => "panel-closed",
            ExactRecipeReleaseReason.PanelDestroyed => "panel-destroyed",
            ExactRecipeReleaseReason.BusinessDestroyed => "business-destroyed",
            ExactRecipeReleaseReason.ServiceSuspended => "service-suspended",
            ExactRecipeReleaseReason.ServiceAbandoned => "service-abandoned",
            _ => "unknown",
        };
        try
        {
            log?.LogInfo(
                $"Runtime pinned list exact recipe lifecycle event={eventName}; "
                + $"reason={reason}; business={item.BusinessGeneration}; "
                + $"targetGen={item.TargetGeneration}; panel={FormatPointer(lease.PanelPointer)}; "
                + $"epoch={lease.PanelEpoch}; recipe={item.ItemId}; "
                + $"recipePtr={FormatPointer(lease.RecipePointer)}; "
                + $"button={FormatPointer(lease.ButtonPointer)}; image={FormatPointer(item.Pointer)}; "
                + $"claims={item.ExactRecipeClaims}; plan={lease.PlanIdentity}");
        }
        catch
        {
            // Lifecycle logging is observational and never changes highlight ownership.
        }
    }

    private static string FormatPointer(nint pointer)
    {
        return $"0x{unchecked((ulong)(long)pointer):x}";
    }

    private static void IncrementMissingImage()
    {
        lock (SyncRoot)
        {
            _missingImages++;
        }
    }

    private static void NoteVisualBindingFailure(string reason, long businessGeneration)
    {
        ManualLogSource? log = null;
        lock (SyncRoot)
        {
            _missingImages++;
            _lastBindingFailure = reason;
            if (_visualBindingWarningGeneration != businessGeneration)
            {
                _visualBindingWarningGeneration = businessGeneration;
                _visualBindingWarnings = 0;
            }
            // 严格绑定失败必须可诊断，但滚动复用同一不支持模板时每场最多写八条，避免刷屏。
            if (_visualBindingWarnings++ < 8) log = _log;
        }
        log?.LogWarning($"Runtime pinned list color bands unavailable: {reason}; business={businessGeneration}.");
    }

    private static void NoteBindingError(string context, Exception exception)
    {
        ManualLogSource? log = null;
        lock (SyncRoot)
        {
            _bindingErrors++;
            if (!_bindingWarningLogged)
            {
                _bindingWarningLogged = true;
                log = _log;
            }
        }

        log?.LogWarning($"Runtime pinned list highlight binding failed ({context}): {exception.GetBaseException().Message}");
    }

    private sealed class TrackedImage
    {
        public TrackedImage(
            object image,
            nint pointer,
            PanelKind panelKind,
            ItemKind itemKind,
            int itemId,
            long targetGeneration,
            long businessGeneration,
            RuntimePinnedListColorBlocks blocks,
            bool usesExactRecipeRowLease,
            TargetRecipeVariantRowLease recipeRowLease,
            RuntimeUiTargetKinds exactRecipeClaims)
        {
            Image = image;
            Pointer = pointer;
            PanelKind = panelKind;
            ItemKind = itemKind;
            ItemId = itemId;
            TargetGeneration = targetGeneration;
            BusinessGeneration = businessGeneration;
            Blocks = blocks;
            UsesExactRecipeRowLease = usesExactRecipeRowLease;
            RecipeRowLease = recipeRowLease;
            ExactRecipeClaims = exactRecipeClaims;
        }

        public object Image { get; }
        public nint Pointer { get; }
        public PanelKind PanelKind { get; }
        public ItemKind ItemKind { get; }
        public int ItemId { get; }
        public long TargetGeneration { get; }
        public long BusinessGeneration { get; }
        public RuntimePinnedListColorBlocks Blocks { get; }
        public bool UsesExactRecipeRowLease { get; }
        public TargetRecipeVariantRowLease RecipeRowLease { get; }
        public RuntimeUiTargetKinds ExactRecipeClaims { get; }
        public int ExactRecipeAppliedLogged;
    }

    private readonly record struct TrackedImageRelease(
        TrackedImage Item,
        ExactRecipeReleaseReason Reason);

    private sealed record ButtonAccessors(MethodInfo? GetImage);

    private enum PanelKind
    {
        Cooking,
        Storage,
    }

    private enum ItemKind
    {
        Recipe,
        Ingredient,
        Beverage,
    }

    private enum ExactRecipeLifecycleEvent
    {
        Bound,
        Applied,
        Released,
    }

    private enum ExactRecipeReleaseReason
    {
        None,
        ListPinningDisabled,
        TargetGenerationChanged,
        TrackingModeChanged,
        PoolRebind,
        RegistrationReplaced,
        ExactLeaseInvalid,
        PostWriteLeaseInvalid,
        VisualReadFailed,
        VisualWriteFailed,
        PanelClosed,
        PanelDestroyed,
        BusinessDestroyed,
        ServiceSuspended,
        ServiceAbandoned,
    }
}
