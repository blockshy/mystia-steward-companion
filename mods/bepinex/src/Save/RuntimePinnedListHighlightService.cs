using System.Collections.Concurrent;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

internal static class RuntimePinnedListHighlightService
{
    private const string CookingSelectionPanelTypeName = "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel";
    private const string StoragePanelTypeName = "NightScene.UI.CookingUtility.WorkSceneStoragePannel";
    private const int ExpectedHookCount = 9;
    private const int MaximumExactRecipeLifecycleLogsPerBusiness = 32;

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<nint, TrackedImage> TrackedImages = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Type, ButtonAccessors> ButtonAccessorCache = new();
    private static readonly ConcurrentDictionary<Type, ImageAccessors> ImageAccessorCache = new();
    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static long _missingImages;
    private static long _bindingErrors;
    private static long _visualErrors;
    private static long _restoreErrors;
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
                return $"hooks={_hookStatus}; state={state}; tracked=recipe:{recipeCount}, ingredients:{ingredientCount}, beverage:{beverageCount}; missingImage={_missingImages}; bindingErrors={_bindingErrors}; visualErrors={_visualErrors}; restoreErrors={_restoreErrors}; exactRecipeLogs={_exactRecipeLifecycleLogs}/{MaximumExactRecipeLifecycleLogsPerBusiness}; exactRecipeLogGeneration={_exactRecipeLogBusinessGeneration}; exactRecipeLogsSuppressed={_suppressedExactRecipeLifecycleLogs}";
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
            PatchPanelPrefixMethod(_harmony, CookingSelectionPanelTypeName, "OnPanelDestroyed", nameof(BeforeCookingPanelTeardown), patchedNow, missing);
            PatchPanelPrefixMethod(_harmony, StoragePanelTypeName, "OnPanelClose", nameof(BeforeStoragePanelTeardown), patchedNow, missing);
            PatchPanelPrefixMethod(_harmony, StoragePanelTypeName, "OnPanelDestroyed", nameof(BeforeStoragePanelTeardown), patchedNow, missing);

            lock (SyncRoot)
            {
                _hookStatus = PatchedMethods.Count == ExpectedHookCount
                    ? "patched"
                    : $"partial:{PatchedMethods.Count}/{ExpectedHookCount}";
            }

            if (patchedNow.Count > 0)
            {
                log.LogInfo($"Runtime pinned list highlight patched: {string.Join(", ", patchedNow)}.");
            }
            if (missing.Count > 0)
            {
                log.LogWarning($"Runtime pinned list highlight unavailable; game members were not found: {string.Join(", ", missing.Take(3))}.");
            }
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
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        List<TrackedImageRelease> staleImages;
        List<TrackedImage> candidates;
        var target = RuntimeUiPinningService.ReadTargetSet();
        var generation = target.Generation;
        lock (SyncRoot)
        {
            if (_suspended) return;

            staleImages = TrackedImages.Values
                .Select(item => new TrackedImageRelease(
                    item,
                    ClassifyTargetRelease(item, target)))
                .Where(item => item.Reason != ExactRecipeReleaseReason.None)
                .ToList();
            foreach (var release in staleImages)
            {
                RemoveTrackedImageLocked(release.Item);
            }

            candidates = TrackedImages.Values.ToList();
            if (!HasListPinningTargets(target))
            {
                _state = "disabled";
            }
        }

        RestoreAndLogTrackedImages(staleImages);
        if (!IsGenerationEnabled(generation)) return;

        foreach (var item in candidates)
        {
            if (!TryReadCurrentClaims(item, generation, out var claims, out var palette))
            {
                RemoveStaleTrackedImage(
                    item,
                    ClassifyLeaseRelease(item, RuntimeUiPinningService.ReadTargetSet()));
                continue;
            }
            if (!TryPrepareVisual(item, generation, out var originalColor)) continue;

            var color = RuntimeTargetHighlightStyle.BuildListItemPulseColor(
                originalColor,
                claims,
                palette,
                Time.realtimeSinceStartup);
            if (!TryApplyHighlight(item, generation, color)) continue;
        }

        var latestTarget = RuntimeUiPinningService.ReadTargetSet();
        lock (SyncRoot)
        {
            if (_suspended) return;
            _state = !HasListPinningTargets(latestTarget)
                ? "disabled"
                : TrackedImages.Values.Any(item => item.TargetGeneration == latestTarget.Generation)
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
    /// Drops destroyed IL2CPP image wrappers without calling their getters or setters.
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

        LogTrackedImageReleases(
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
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

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

    private static void AfterRecipeItemEnabled(object __instance, object __0, object __2)
    {
        TryRegisterRecipe(__instance, __0, __2);
    }

    private static void AfterIngredientItemEnabled(object __0, object __2)
    {
        TryRegisterByItemId(__0, __2, PanelKind.Cooking, ItemKind.Ingredient);
    }

    private static void AfterStorageItemEnabled(object __0, object __2)
    {
        TryRegisterByItemId(__0, __2, PanelKind.Storage, ItemKind.Beverage);
    }

    private static void BeforeCookingPanelTeardown(MethodBase __originalMethod)
    {
        ReconcilePanelTeardown(
            PanelKind.Cooking,
            __originalMethod.Name == "OnPanelDestroyed"
                ? ExactRecipeReleaseReason.PanelDestroyed
                : ExactRecipeReleaseReason.PanelClosed);
    }

    private static void BeforeStoragePanelTeardown(MethodBase __originalMethod)
    {
        ReconcilePanelTeardown(
            PanelKind.Storage,
            __originalMethod.Name == "OnPanelDestroyed"
                ? ExactRecipeReleaseReason.PanelDestroyed
                : ExactRecipeReleaseReason.PanelClosed);
    }

    private static void TryRegisterRecipe(object panel, object recipe, object button)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

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
        object button,
        PanelKind panelKind,
        ItemKind itemKind)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

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
        if (image == null || !TryReadColor(image, out var originalColor))
        {
            IncrementMissingImage();
            return;
        }

        var pointer = RuntimeReflectionUtility.ReadObjectPointer(image);
        if (pointer == IntPtr.Zero) return;

        var latestTarget = RuntimeUiPinningService.ReadTargetSet();
        var latestClaims = RuntimeUiTargetKinds.None;
        if (usesExactRecipeRowLease
            && (!RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(
                    recipeRowLease,
                    out latestClaims)
                || latestClaims == RuntimeUiTargetKinds.None))
        {
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
                originalColor,
                usesExactRecipeRowLease,
                recipeRowLease,
                exactRecipeClaims);
            TrackedImages[pointer] = registered;
            _state = "active";
        }

        if (replaced != null)
        {
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

    private static bool TryPrepareVisual(TrackedImage item, long generation, out Color originalColor)
    {
        originalColor = default;
        bool captureFinalColor;
        var target = RuntimeUiPinningService.ReadTargetSet();
        lock (SyncRoot)
        {
            if (!IsCurrentTrackingLocked(item, generation, target)) return false;
            captureFinalColor = !item.IsHighlighted;
            originalColor = item.OriginalColor;
        }

        if (!captureFinalColor) return true;

        try
        {
            if (!TryReadColor(item.Image, out var currentColor))
            {
                RemoveFailedVisual(item, ExactRecipeReleaseReason.VisualReadFailed);
                return false;
            }

            var latestTarget = RuntimeUiPinningService.ReadTargetSet();
            lock (SyncRoot)
            {
                if (!IsCurrentTrackingLocked(item, generation, latestTarget)) return false;
                item.OriginalColor = currentColor;
                originalColor = currentColor;
                return true;
            }
        }
        catch
        {
            RemoveFailedVisual(item, ExactRecipeReleaseReason.VisualReadFailed);
            return false;
        }
    }

    private static bool TryApplyHighlight(TrackedImage item, long generation, Color color)
    {
        var target = RuntimeUiPinningService.ReadTargetSet();
        lock (SyncRoot)
        {
            if (!IsCurrentTrackingLocked(item, generation, target)) return false;
        }

        try
        {
            if (!TryWriteColor(item.Image, color))
            {
                RemoveFailedVisual(item, ExactRecipeReleaseReason.VisualWriteFailed);
                return false;
            }

            item.IsHighlighted = true;
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

    private static bool TryReadColor(object image, out Color color)
    {
        color = default;
        var getColor = GetImageAccessors(image.GetType()).GetColor;
        if (getColor == null) return false;

        var value = getColor.Invoke(image, null);
        if (value is not Color imageColor) return false;
        color = imageColor;
        return true;
    }

    private static bool TryWriteColor(object image, Color color)
    {
        var setColor = GetImageAccessors(image.GetType()).SetColor;
        if (setColor == null) return false;

        setColor.Invoke(image, new object?[] { color });
        return true;
    }

    private static ImageAccessors GetImageAccessors(Type type)
    {
        return ImageAccessorCache.GetOrAdd(type, static imageType =>
        {
            var methods = imageType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var getColor = methods.FirstOrDefault(method => method.Name == "get_color" && method.GetParameters().Length == 0);
            var setColor = methods.FirstOrDefault(method =>
            {
                if (method.Name != "set_color") return false;
                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(Color);
            });
            return new ImageAccessors(getColor, setColor);
        });
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
        return targetSet.Targets.Any(target => target.ListPinningEnabled);
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
            LogTrackedImageReleases(
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
        if (!force && !item.IsHighlighted) return;

        try
        {
            if (TryWriteColor(item.Image, item.OriginalColor)) return;
        }
        catch
        {
            // Count below; restoring a destroyed pooled image is best effort.
        }

        lock (SyncRoot)
        {
            _restoreErrors++;
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
            Color originalColor,
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
            OriginalColor = originalColor;
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
        public Color OriginalColor { get; set; }
        public bool UsesExactRecipeRowLease { get; }
        public TargetRecipeVariantRowLease RecipeRowLease { get; }
        public RuntimeUiTargetKinds ExactRecipeClaims { get; }
        public bool IsHighlighted { get; set; }
        public int ExactRecipeAppliedLogged;
    }

    private readonly record struct TrackedImageRelease(
        TrackedImage Item,
        ExactRecipeReleaseReason Reason);

    private sealed record ButtonAccessors(MethodInfo? GetImage);

    private sealed record ImageAccessors(MethodInfo? GetColor, MethodInfo? SetColor);

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
