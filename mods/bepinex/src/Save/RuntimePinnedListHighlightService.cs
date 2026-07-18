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

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<nint, TrackedImage> TrackedImages = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Type, ButtonAccessors> ButtonAccessorCache = new();
    private static readonly ConcurrentDictionary<Type, ImageAccessors> ImageAccessorCache = new();
    private static readonly Color HighlightColor = new(1f, 0.86f, 0.18f, 1f);

    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static long _missingImages;
    private static long _bindingErrors;
    private static long _visualErrors;
    private static long _restoreErrors;
    private static bool _bindingWarningLogged;
    private static bool _suspended;
    private static string _suspendReason = "scene unavailable";
    private static string _hookStatus = "not attached";
    private static string _state = "disabled";

    public static string Status
    {
        get
        {
            var target = RuntimeUiPinningService.ReadPinningTarget();
            lock (SyncRoot)
            {
                var recipeCount = TrackedImages.Values.Count(item => item.ItemKind == ItemKind.Recipe);
                var ingredientCount = TrackedImages.Values.Count(item => item.ItemKind == ItemKind.Ingredient);
                var beverageCount = TrackedImages.Values.Count(item => item.ItemKind == ItemKind.Beverage);
                var state = _suspended
                    ? _state
                    : !target.Enabled
                        ? TrackedImages.Count == 0 ? "disabled" : "pending disable"
                        : TrackedImages.Values.Any(item => item.TargetGeneration == target.Generation)
                            ? _state
                            : TrackedImages.Count == 0 ? "waiting for target elements" : "pending target refresh";
                return $"hooks={_hookStatus}; state={state}; tracked=recipe:{recipeCount}, ingredients:{ingredientCount}, beverage:{beverageCount}; missingImage={_missingImages}; bindingErrors={_bindingErrors}; visualErrors={_visualErrors}; restoreErrors={_restoreErrors}";
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

        List<TrackedImage> staleImages;
        List<TrackedImage> candidates;
        var target = RuntimeUiPinningService.ReadPinningTarget();
        var generation = target.Generation;
        lock (SyncRoot)
        {
            if (_suspended) return;

            staleImages = TrackedImages.Values
                .Where(item => !target.Enabled
                    || item.TargetGeneration != generation
                    || !MatchesTarget(target, item.ItemKind, item.ItemId))
                .ToList();
            foreach (var item in staleImages)
            {
                RemoveTrackedImageLocked(item);
            }

            candidates = TrackedImages.Values.ToList();
            if (!target.Enabled)
            {
                _state = "disabled";
            }
        }

        RestoreTrackedImages(staleImages);
        if (!IsGenerationEnabled(generation)) return;

        var pulse = 0.55f + (Mathf.Sin(Time.realtimeSinceStartup * 5.5f) + 1f) * 0.225f;
        foreach (var item in candidates)
        {
            if (!TryPrepareVisual(item, generation, out var originalColor)) continue;

            var color = Color.Lerp(originalColor, HighlightColor, pulse);
            color.a = originalColor.a;
            if (!TryApplyHighlight(item, generation, color)) continue;
        }

        var latestTarget = RuntimeUiPinningService.ReadPinningTarget();
        lock (SyncRoot)
        {
            if (_suspended) return;
            _state = !latestTarget.Enabled
                ? "disabled"
                : TrackedImages.Values.Any(item => item.TargetGeneration == latestTarget.Generation)
                    ? "active"
                    : "waiting for target elements";
        }
    }

    public static void Suspend(string reason)
    {
        var target = RuntimeUiPinningService.ReadPinningTarget();
        List<TrackedImage> images;
        lock (SyncRoot)
        {
            _suspended = true;
            _suspendReason = string.IsNullOrWhiteSpace(reason) ? "scene unavailable" : reason.Trim();
            images = TakeAllTrackedImagesLocked();
            _state = target.Enabled ? $"suspended: {_suspendReason}" : "disabled";
        }

        RestoreTrackedImages(images);
    }

    public static void Resume(string reason)
    {
        var target = RuntimeUiPinningService.ReadPinningTarget();
        lock (SyncRoot)
        {
            _suspended = false;
            _suspendReason = string.IsNullOrWhiteSpace(reason) ? "night business active" : reason.Trim();
            _state = target.Enabled ? "waiting for target elements" : "disabled";
        }
    }

    /// <summary>
    /// Drops destroyed IL2CPP image wrappers without calling their getters or setters.
    /// </summary>
    public static void Abandon(string reason)
    {
        lock (SyncRoot)
        {
            TrackedImages.Clear();
            _suspended = true;
            _suspendReason = string.IsNullOrWhiteSpace(reason) ? "night business destroyed" : reason.Trim();
            _state = $"abandoned: {_suspendReason}";
        }
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
                postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
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

        var target = RuntimeUiPinningService.ReadPinningTarget();
        lock (SyncRoot)
        {
            _suspended = false;
            _suspendReason = "scene unavailable";
            _state = target.Enabled ? "waiting for target elements" : "disabled";
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

            if (trackedImage != null) RestoreTrackedImage(trackedImage);
        }
        catch (Exception ex)
        {
            NoteBindingError("item prefix", ex);
        }
    }

    private static void AfterRecipeItemEnabled(object __0, object __2)
    {
        TryRegister(__0, __2, PanelKind.Cooking, ItemKind.Recipe);
    }

    private static void AfterIngredientItemEnabled(object __0, object __2)
    {
        TryRegister(__0, __2, PanelKind.Cooking, ItemKind.Ingredient);
    }

    private static void AfterStorageItemEnabled(object __0, object __2)
    {
        TryRegister(__0, __2, PanelKind.Storage, ItemKind.Beverage);
    }

    private static void BeforeCookingPanelTeardown()
    {
        ReconcilePanelTeardown(PanelKind.Cooking);
    }

    private static void BeforeStoragePanelTeardown()
    {
        ReconcilePanelTeardown(PanelKind.Storage);
    }

    private static void TryRegister(object data, object button, PanelKind panelKind, ItemKind itemKind)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        try
        {
            var item = itemKind == ItemKind.Recipe
                ? data
                : RuntimeReflectionUtility.GetMemberValue(data, "Key");
            if (item == null) return;

            if (itemKind == ItemKind.Beverage
                && RuntimeReflectionUtility.ToInt(RuntimeReflectionUtility.GetMemberValue(item, "Type"), -1) != 1)
            {
                return;
            }

            var itemId = RuntimeReflectionUtility.ToInt(RuntimeReflectionUtility.GetMemberValue(item, "id"), -1);
            if (itemId < 0) return;

            var target = RuntimeUiPinningService.ReadPinningTarget();
            lock (SyncRoot)
            {
                if (_suspended || !MatchesTarget(target, itemKind, itemId)) return;
            }

            var image = ReadButtonImage(button);
            if (image == null || !TryReadColor(image, out var originalColor))
            {
                IncrementMissingImage();
                return;
            }

            var pointer = RuntimeReflectionUtility.ReadObjectPointer(image);
            if (pointer == IntPtr.Zero) return;

            var latestTarget = RuntimeUiPinningService.ReadPinningTarget();
            lock (SyncRoot)
            {
                if (_suspended
                    || target.Generation != latestTarget.Generation
                    || !MatchesTarget(latestTarget, itemKind, itemId))
                {
                    return;
                }

                TrackedImages.Remove(pointer);
                TrackedImages[pointer] = new TrackedImage(
                    image,
                    pointer,
                    panelKind,
                    itemKind,
                    itemId,
                    target.Generation,
                    originalColor);
                _state = "active";
            }
        }
        catch (Exception ex)
        {
            NoteBindingError($"{itemKind} postfix", ex);
        }
    }

    private static bool TryPrepareVisual(TrackedImage item, long generation, out Color originalColor)
    {
        originalColor = default;
        bool captureFinalColor;
        var target = RuntimeUiPinningService.ReadPinningTarget();
        lock (SyncRoot)
        {
            if (!IsCurrentTargetLocked(item, generation, target)) return false;
            captureFinalColor = !item.IsHighlighted;
            originalColor = item.OriginalColor;
        }

        if (!captureFinalColor) return true;

        try
        {
            if (!TryReadColor(item.Image, out var currentColor))
            {
                RemoveFailedVisual(item);
                return false;
            }

            var latestTarget = RuntimeUiPinningService.ReadPinningTarget();
            lock (SyncRoot)
            {
                if (!IsCurrentTargetLocked(item, generation, latestTarget)) return false;
                item.OriginalColor = currentColor;
                originalColor = currentColor;
                return true;
            }
        }
        catch
        {
            RemoveFailedVisual(item);
            return false;
        }
    }

    private static bool TryApplyHighlight(TrackedImage item, long generation, Color color)
    {
        var target = RuntimeUiPinningService.ReadPinningTarget();
        lock (SyncRoot)
        {
            if (!IsCurrentTargetLocked(item, generation, target)) return false;
        }

        try
        {
            if (!TryWriteColor(item.Image, color))
            {
                RemoveFailedVisual(item);
                return false;
            }

            item.IsHighlighted = true;
        }
        catch
        {
            RemoveFailedVisual(item, forceRestore: true);
            return false;
        }

        var restore = false;
        var latestTarget = RuntimeUiPinningService.ReadPinningTarget();
        lock (SyncRoot)
        {
            if (IsCurrentTargetLocked(item, generation, latestTarget))
            {
                return true;
            }

            restore = RemoveTrackedImageLocked(item);
        }

        if (restore) RestoreTrackedImage(item);
        return false;
    }

    private static bool IsGenerationEnabled(long generation)
    {
        var target = RuntimeUiPinningService.ReadPinningTarget();
        lock (SyncRoot)
        {
            return !_suspended && target.Enabled && target.Generation == generation;
        }
    }

    private static bool IsCurrentTargetLocked(
        TrackedImage item,
        long generation,
        RuntimeUiPinningService.PinningTargetSnapshot target)
    {
        return !_suspended
            && target.Enabled
            && target.Generation == generation
            && item.TargetGeneration == generation
            && TrackedImages.TryGetValue(item.Pointer, out var current)
            && ReferenceEquals(current, item)
            && MatchesTarget(target, item.ItemKind, item.ItemId);
    }

    private static bool MatchesTarget(RuntimeUiPinningService.PinningTargetSnapshot target, ItemKind itemKind, int itemId)
    {
        if (!target.Enabled) return false;
        return itemKind switch
        {
            ItemKind.Recipe => itemId == target.RecipeId,
            ItemKind.Ingredient => target.ContainsIngredient(itemId),
            ItemKind.Beverage => itemId == target.BeverageId,
            _ => false,
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

    private static void RestorePanel(PanelKind panelKind)
    {
        var target = RuntimeUiPinningService.ReadPinningTarget();
        List<TrackedImage> images;
        lock (SyncRoot)
        {
            images = TrackedImages.Values.Where(item => item.PanelKind == panelKind).ToList();
            foreach (var item in images)
            {
                RemoveTrackedImageLocked(item);
            }

            _state = _suspended
                ? target.Enabled ? $"suspended: {_suspendReason}" : "disabled"
                : target.Enabled ? "waiting for target elements" : "disabled";
        }

        RestoreTrackedImages(images);
    }

    private static void ReconcilePanelTeardown(PanelKind panelKind)
    {
        if (RuntimeNightBusinessLifecycle.Snapshot.Phase == NightBusinessLifecyclePhase.Destroyed)
        {
            lock (SyncRoot)
            {
                foreach (var pointer in TrackedImages
                             .Where(pair => pair.Value.PanelKind == panelKind)
                             .Select(pair => pair.Key)
                             .ToList())
                {
                    TrackedImages.Remove(pointer);
                }
            }
            return;
        }

        RestorePanel(panelKind);
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

    private static void RemoveFailedVisual(TrackedImage item, bool forceRestore = false)
    {
        var removed = false;
        lock (SyncRoot)
        {
            removed = RemoveTrackedImageLocked(item);
            if (removed) _visualErrors++;
        }

        if (removed) RestoreTrackedImage(item, forceRestore);
    }

    private static void RestoreTrackedImages(IEnumerable<TrackedImage> images)
    {
        foreach (var item in images)
        {
            RestoreTrackedImage(item);
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
            Color originalColor)
        {
            Image = image;
            Pointer = pointer;
            PanelKind = panelKind;
            ItemKind = itemKind;
            ItemId = itemId;
            TargetGeneration = targetGeneration;
            OriginalColor = originalColor;
        }

        public object Image { get; }
        public nint Pointer { get; }
        public PanelKind PanelKind { get; }
        public ItemKind ItemKind { get; }
        public int ItemId { get; }
        public long TargetGeneration { get; }
        public Color OriginalColor { get; set; }
        public bool IsHighlighted { get; set; }
    }

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
}
