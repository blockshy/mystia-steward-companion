using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeUiPinningService
{
    private const string CookingSelectionPanelTypeName = "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel";
    private const string StoragePanelTypeName = "NightScene.UI.CookingUtility.WorkSceneStoragePannel";
    private const string RunTimePlayerDataTypeName = "GameData.RunTime.Common.RunTimePlayerData";

    private static readonly object SyncRoot = new();
    private static readonly object TargetPublicationRoot = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly int[] EmptyIngredientIds = Array.Empty<int>();

    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static bool _enabled;
    private static bool _highlightEnabled;
    private static int _recipeId = -1;
    private static int _beverageId = -1;
    private static int[] _ingredientIds = EmptyIngredientIds;
    private static int _cookerTypeId = -1;
    private static string _recipeName = "";
    private static string _beverageName = "";
    private static string _cookerName = "";
    private static string _checkPinnedPatchStatus = "not attached";
    private static string _cookingScopePatchStatus = "not attached";
    private static string _beverageScopePatchStatus = "not attached";
    private static PinningTargetSnapshot _pinningTarget = PinningTargetSnapshot.Disabled;
    private static long _recipeForces;
    private static long _ingredientForces;
    private static long _beverageForces;
    private static long _scopeCleanupImbalances;

    [ThreadStatic]
    private static int _cookingRefreshDepth;

    [ThreadStatic]
    private static PinningTargetSnapshot? _cookingScopeTarget;

    [ThreadStatic]
    private static int _beverageRefreshDepth;

    [ThreadStatic]
    private static PinningTargetSnapshot? _beverageScopeTarget;

    public static string Status
    {
        get
        {
            string coreStatus;
            lock (SyncRoot)
            {
                coreStatus = $"patches=checkPinnedPrefix:{_checkPinnedPatchStatus}, cookingScope:{_cookingScopePatchStatus}, beverageScope:{_beverageScopePatchStatus}; pinning={(_enabled ? "on" : "off")}; cookerHighlight={(_highlightEnabled ? "on" : "off")}; target=recipe:{_recipeId}/{_recipeName}, beverage:{_beverageId}/{_beverageName}, cooker:{_cookerTypeId}/{_cookerName}, ingredients:{string.Join(",", _ingredientIds)}";
            }

            return $"{coreStatus}; highlight={RuntimeCookerHighlightService.Status}; listHighlight={RuntimePinnedListHighlightService.Status}; forcedTotal=recipe:{Interlocked.Read(ref _recipeForces)}, ingredients:{Interlocked.Read(ref _ingredientForces)}, beverage:{Interlocked.Read(ref _beverageForces)}; scopeImbalance={Interlocked.Read(ref _scopeCleanupImbalances)}";
        }
    }

    public static void Attach(ManualLogSource log)
    {
        _log = log;
        try
        {
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.runtime-ui-pinning");
            var patchedNow = new List<string>();
            var missing = new List<string>();

            PatchScopeMethod(
                _harmony,
                CookingSelectionPanelTypeName,
                "UpdateAllVisual",
                0,
                nameof(OnCookingRefreshStarted),
                nameof(OnCookingRefreshFinalized),
                PatchSlot.CookingScope,
                patchedNow,
                missing);
            PatchPrefixMethod(
                _harmony,
                RunTimePlayerDataTypeName,
                "CheckPinned",
                2,
                nameof(OnCheckPinned),
                PatchSlot.CheckPinned,
                patchedNow,
                missing);
            PatchScopeMethod(
                _harmony,
                StoragePanelTypeName,
                "UpdateBevField",
                0,
                nameof(OnBeverageRefreshStarted),
                nameof(OnBeverageRefreshFinalized),
                PatchSlot.BeverageScope,
                patchedNow,
                missing);

            if (patchedNow.Count > 0)
            {
                log.LogInfo($"Runtime UI pinning patched: {string.Join(", ", patchedNow)}.");
            }
            if (missing.Count > 0)
            {
                log.LogWarning($"Runtime UI pinning unavailable; game members were not found: {string.Join(", ", missing.Take(3))}.");
            }
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                var status = $"error:{ex.GetBaseException().Message}";
                _checkPinnedPatchStatus = status;
                _cookingScopePatchStatus = status;
                _beverageScopePatchStatus = status;
            }

            log.LogWarning($"Runtime UI pinning attach failed: {ex.Message}");
        }
    }

    public static string UpdateTarget(
        bool enabled,
        bool highlightEnabled,
        int recipeId,
        int beverageId,
        IEnumerable<int> ingredientIds,
        string recipeName,
        string beverageName,
        int cookerTypeId,
        string cookerName)
    {
        lock (TargetPublicationRoot)
        {
            return PublishTarget(
                enabled,
                highlightEnabled,
                recipeId,
                beverageId,
                ingredientIds,
                recipeName,
                beverageName,
                cookerTypeId,
                cookerName);
        }
    }

    internal static PinningTargetSnapshot ReadPinningTarget()
    {
        return Volatile.Read(ref _pinningTarget);
    }

    private static string PublishTarget(
        bool enabled,
        bool highlightEnabled,
        int recipeId,
        int beverageId,
        IEnumerable<int> ingredientIds,
        string recipeName,
        string beverageName,
        int cookerTypeId,
        string cookerName)
    {
        var hasTarget = enabled || highlightEnabled;
        var normalizedRecipeId = hasTarget ? recipeId : -1;
        var normalizedBeverageId = hasTarget ? beverageId : -1;
        var normalizedCookerTypeId = hasTarget ? cookerTypeId : -1;
        var normalizedIngredientIds = hasTarget
            ? ingredientIds.Where(id => id >= 0).Distinct().Take(12).ToArray()
            : EmptyIngredientIds;
        var normalizedRecipeName = hasTarget ? recipeName.Trim() : "";
        var normalizedBeverageName = hasTarget ? beverageName.Trim() : "";
        var normalizedCookerName = hasTarget ? cookerName.Trim() : "";

        ManualLogSource? log = null;
        string logMessage = "";
        var targetChanged = false;
        lock (SyncRoot)
        {
            if (!HasSamePublishedTargetLocked(
                    enabled,
                    highlightEnabled,
                    normalizedRecipeId,
                    normalizedBeverageId,
                    normalizedCookerTypeId,
                    normalizedIngredientIds,
                    normalizedRecipeName,
                    normalizedBeverageName,
                    normalizedCookerName))
            {
                _enabled = enabled;
                _highlightEnabled = highlightEnabled;
                _recipeId = normalizedRecipeId;
                _beverageId = normalizedBeverageId;
                _cookerTypeId = normalizedCookerTypeId;
                _ingredientIds = normalizedIngredientIds;
                _recipeName = normalizedRecipeName;
                _beverageName = normalizedBeverageName;
                _cookerName = normalizedCookerName;
                var currentPinningTarget = Volatile.Read(ref _pinningTarget);
                if (!currentPinningTarget.HasSameValues(enabled, _recipeId, _beverageId, normalizedIngredientIds))
                {
                    Volatile.Write(
                        ref _pinningTarget,
                        new PinningTargetSnapshot(
                            currentPinningTarget.Generation + 1,
                            enabled,
                            _recipeId,
                            _beverageId,
                            normalizedIngredientIds));
                }
                log = _log;
                logMessage = hasTarget
                    ? $"Runtime UI target updated: pinning={enabled}, cookerHighlight={highlightEnabled}, recipe={_recipeId}/{_recipeName}, beverage={_beverageId}/{_beverageName}, cooker={_cookerTypeId}/{_cookerName}, ingredients={string.Join(",", _ingredientIds)}."
                    : "Runtime UI target disabled.";
                targetChanged = true;
            }
        }

        if (!targetChanged) return Status;
        log?.LogInfo(logMessage);
        RuntimeCookerHighlightService.UpdateTarget(highlightEnabled && hasTarget, normalizedCookerTypeId, normalizedCookerName);
        return Status;
    }

    private static bool HasSamePublishedTargetLocked(
        bool enabled,
        bool highlightEnabled,
        int recipeId,
        int beverageId,
        int cookerTypeId,
        int[] ingredientIds,
        string recipeName,
        string beverageName,
        string cookerName)
    {
        return _enabled == enabled
            && _highlightEnabled == highlightEnabled
            && _recipeId == recipeId
            && _beverageId == beverageId
            && _cookerTypeId == cookerTypeId
            && _ingredientIds.SequenceEqual(ingredientIds)
            && string.Equals(_recipeName, recipeName, StringComparison.Ordinal)
            && string.Equals(_beverageName, beverageName, StringComparison.Ordinal)
            && string.Equals(_cookerName, cookerName, StringComparison.Ordinal);
    }

    private static void PatchPrefixMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        int parameterCount,
        string prefixName,
        PatchSlot patchSlot,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/{parameterCount}";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key))
            {
                SetPatchStatusLocked(patchSlot, "patched");
                return;
            }
        }

        try
        {
            var type = RuntimeReflectionUtility.FindType(typeName);
            var target = FindMethod(type, methodName, parameterCount);
            var prefix = typeof(RuntimeUiPinningService).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
            if (target == null || prefix == null)
            {
                lock (SyncRoot)
                {
                    SetPatchStatusLocked(patchSlot, target == null ? "method missing" : "prefix missing");
                }

                missing.Add(key);
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            lock (SyncRoot)
            {
                PatchedMethods.Add(key);
                SetPatchStatusLocked(patchSlot, "patched");
            }

            patchedNow.Add(key);
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                SetPatchStatusLocked(patchSlot, $"error:{ex.GetBaseException().Message}");
            }

            missing.Add($"{key} (patch error)");
        }
    }

    private static void PatchScopeMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        int parameterCount,
        string prefixName,
        string finalizerName,
        PatchSlot patchSlot,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/{parameterCount}:{patchSlot}";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key))
            {
                SetPatchStatusLocked(patchSlot, "patched");
                return;
            }
        }

        try
        {
            var type = RuntimeReflectionUtility.FindType(typeName);
            var target = FindMethod(type, methodName, parameterCount);
            var prefix = typeof(RuntimeUiPinningService).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
            var finalizer = typeof(RuntimeUiPinningService).GetMethod(finalizerName, BindingFlags.NonPublic | BindingFlags.Static);
            if (target == null || prefix == null || finalizer == null)
            {
                lock (SyncRoot)
                {
                    SetPatchStatusLocked(patchSlot, target == null ? "method missing" : "hook missing");
                }

                missing.Add(key);
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(prefix), finalizer: new HarmonyMethod(finalizer));
            lock (SyncRoot)
            {
                PatchedMethods.Add(key);
                SetPatchStatusLocked(patchSlot, "patched");
            }

            patchedNow.Add(key);
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                SetPatchStatusLocked(patchSlot, $"error:{ex.GetBaseException().Message}");
            }

            missing.Add($"{key} (patch error)");
        }
    }

    private static MethodInfo? FindMethod(Type? type, string methodName, int parameterCount)
    {
        return type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == parameterCount);
    }

    private static void OnCookingRefreshStarted()
    {
        if (_cookingRefreshDepth == 0)
        {
            _cookingScopeTarget = Volatile.Read(ref _pinningTarget);
        }

        _cookingRefreshDepth++;
    }

    private static Exception? OnCookingRefreshFinalized(Exception? __exception)
    {
        if (_cookingRefreshDepth > 0)
        {
            _cookingRefreshDepth--;
            if (_cookingRefreshDepth == 0)
            {
                _cookingScopeTarget = null;
            }
        }
        else
        {
            Interlocked.Increment(ref _scopeCleanupImbalances);
        }

        return __exception;
    }

    private static void OnBeverageRefreshStarted()
    {
        if (_beverageRefreshDepth == 0)
        {
            _beverageScopeTarget = Volatile.Read(ref _pinningTarget);
        }

        _beverageRefreshDepth++;
    }

    private static Exception? OnBeverageRefreshFinalized(Exception? __exception)
    {
        if (_beverageRefreshDepth > 0)
        {
            _beverageRefreshDepth--;
            if (_beverageRefreshDepth == 0)
            {
                _beverageScopeTarget = null;
            }
        }
        else
        {
            Interlocked.Increment(ref _scopeCleanupImbalances);
        }

        return __exception;
    }

    private static bool OnCheckPinned(int pinnedType, int pinnedID, ref bool __result)
    {
        if (pinnedID < 0) return true;

        var cookingTarget = _cookingRefreshDepth > 0 ? _cookingScopeTarget : null;
        var beverageTarget = _beverageRefreshDepth > 0 ? _beverageScopeTarget : null;
        if (cookingTarget == null && beverageTarget == null) return true;

        if (cookingTarget is { Enabled: true }
            && pinnedType == (int)PinnedType.Recipes
            && pinnedID == cookingTarget.RecipeId)
        {
            __result = true;
            Interlocked.Increment(ref _recipeForces);
            return false;
        }

        if (cookingTarget is { Enabled: true }
            && IsIngredientType(pinnedType)
            && cookingTarget.ContainsIngredient(pinnedID))
        {
            __result = true;
            Interlocked.Increment(ref _ingredientForces);
            return false;
        }

        if (beverageTarget is { Enabled: true }
            && pinnedType == (int)PinnedType.Beverages
            && pinnedID == beverageTarget.BeverageId)
        {
            __result = true;
            Interlocked.Increment(ref _beverageForces);
            return false;
        }

        return true;
    }

    private static bool IsIngredientType(int pinnedType)
    {
        return pinnedType == (int)PinnedType.IngredientsSeafood
            || pinnedType == (int)PinnedType.IngredientsMeat
            || pinnedType == (int)PinnedType.IngredientsVegetable
            || pinnedType == (int)PinnedType.IngredientsOther;
    }

    private static void SetPatchStatusLocked(PatchSlot patchSlot, string status)
    {
        switch (patchSlot)
        {
            case PatchSlot.CheckPinned:
                _checkPinnedPatchStatus = status;
                break;
            case PatchSlot.CookingScope:
                _cookingScopePatchStatus = status;
                break;
            case PatchSlot.BeverageScope:
                _beverageScopePatchStatus = status;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(patchSlot), patchSlot, null);
        }
    }

    internal sealed class PinningTargetSnapshot
    {
        private readonly int[] _ingredientIds;

        public static readonly PinningTargetSnapshot Disabled = new(0, false, -1, -1, EmptyIngredientIds);

        public PinningTargetSnapshot(long generation, bool enabled, int recipeId, int beverageId, int[] ingredientIds)
        {
            Generation = generation;
            Enabled = enabled;
            RecipeId = recipeId;
            BeverageId = beverageId;
            _ingredientIds = ingredientIds.ToArray();
        }

        public long Generation { get; }

        public bool Enabled { get; }

        public int RecipeId { get; }

        public int BeverageId { get; }

        public bool ContainsIngredient(int ingredientId)
        {
            return Array.IndexOf(_ingredientIds, ingredientId) >= 0;
        }

        public bool HasSameValues(bool enabled, int recipeId, int beverageId, int[] ingredientIds)
        {
            if (Enabled != enabled) return false;
            return !enabled
                || RecipeId == recipeId
                && BeverageId == beverageId
                && _ingredientIds.SequenceEqual(ingredientIds);
        }
    }

    private enum PatchSlot
    {
        CheckPinned,
        CookingScope,
        BeverageScope,
    }

    private enum PinnedType
    {
        IngredientsSeafood = 0,
        Recipes = 1,
        Beverages = 2,
        Cookers = 3,
        IngredientsMeat = 4,
        IngredientsVegetable = 5,
        IngredientsOther = 6,
    }
}
