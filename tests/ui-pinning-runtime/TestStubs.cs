using System.Reflection;
using System.Runtime.CompilerServices;

namespace UnityEngine
{
    internal struct Color
    {
        public Color(float r, float g, float b, float a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public float r;
        public float g;
        public float b;
        public float a;

        public static Color Lerp(Color left, Color right, float amount)
        {
            var clamped = Math.Clamp(amount, 0f, 1f);
            return new Color(
                left.r + ((right.r - left.r) * clamped),
                left.g + ((right.g - left.g) * clamped),
                left.b + ((right.b - left.b) * clamped),
                left.a + ((right.a - left.a) * clamped));
        }
    }

    internal static class Mathf
    {
        public static float Sin(float value) => MathF.Sin(value);
    }

    internal static class Time
    {
        public static float realtimeSinceStartup { get; set; }
    }
}

namespace BepInEx.Logging
{
    internal sealed class ManualLogSource
    {
        private static readonly object SyncRoot = new();
        private static readonly List<string> InformationMessages = new();

        public static int InformationCount { get; private set; }

        public static int WarningCount { get; private set; }

        public static Func<bool>? InformationLogUnsafeProbe { get; set; }

        public static bool InformationLoggedWhileUnsafe { get; private set; }

        public static string[] SnapshotInformationMessages()
        {
            lock (SyncRoot) return InformationMessages.ToArray();
        }

        public static void ResetInformationLogSafetyObservation()
        {
            InformationLoggedWhileUnsafe = false;
        }

        public void LogInfo(object value)
        {
            var unsafeWrite = InformationLogUnsafeProbe?.Invoke() == true;
            lock (SyncRoot)
            {
                InformationCount += 1;
                InformationMessages.Add(value?.ToString() ?? "");
                InformationLoggedWhileUnsafe |= unsafeWrite;
            }
        }

        public void LogWarning(object value)
        {
            lock (SyncRoot) WarningCount += 1;
        }
    }
}

namespace MystiaStewardCompanion.Save
{
    internal static class RuntimeNightBusinessLifecycle
    {
        private static readonly NightBusinessLifecycleTracker Tracker = new();
        private static NightBusinessLifecycleSnapshot _snapshot;

        static RuntimeNightBusinessLifecycle()
        {
            Tracker.TryActivate("test active", DateTime.UtcNow, Environment.CurrentManagedThreadId, out _snapshot);
        }

        public static NightBusinessLifecycleSnapshot Snapshot => Volatile.Read(ref _snapshot);

        public static bool IsActive => Snapshot.IsActive;

        public static long Generation => Snapshot.Generation;

        public static void BeginClosing()
        {
            if (Tracker.TryBeginClosing("test closing", DateTime.UtcNow, Environment.CurrentManagedThreadId, out var snapshot))
            {
                Volatile.Write(ref _snapshot, snapshot);
            }
        }

        public static void ActivateNextGeneration()
        {
            if (Tracker.TryMarkDestroyed("test destroyed", DateTime.UtcNow, Environment.CurrentManagedThreadId, out var destroyed))
            {
                Volatile.Write(ref _snapshot, destroyed);
            }
            if (Tracker.TryActivate("test active next", DateTime.UtcNow, Environment.CurrentManagedThreadId, out var active))
            {
                Volatile.Write(ref _snapshot, active);
            }
        }
    }

    internal static class RuntimeReflectionUtility
    {
        public static Type? FindType(string fullName)
        {
            return fullName switch
            {
                "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel" => typeof(CookingSelectionPanelProbe),
                "NightScene.UI.CookingUtility.WorkSceneStoragePannel" => typeof(StoragePanelProbe),
                "GameData.RunTime.Common.RunTimePlayerData" => typeof(RunTimePlayerDataProbe),
                _ => null,
            };
        }

        public static object? GetMemberValue(object? instance, string name)
        {
            if (instance == null) return null;
            for (var type = instance.GetType(); type != null; type = type.BaseType)
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null) return property.GetValue(instance);

                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null) return field.GetValue(instance);
            }

            return null;
        }

        public static int ToInt(object? value, int fallback = 0)
        {
            if (value == null) return fallback;
            if (value is int number) return number;
            if (value is Enum enumValue) return Convert.ToInt32(enumValue);
            return int.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
        }

        public static nint ReadObjectPointer(object target)
        {
            var pointer = GetMemberValue(target, "m_CachedPtr");
            if (pointer is IntPtr intPtr) return intPtr;
            return new IntPtr(RuntimeHelpers.GetHashCode(target));
        }
    }

    internal static class RuntimeCookerHighlightService
    {
        public static string Status => LastEnabled ? "active" : "disabled";

        public static bool LastEnabled { get; private set; }

        public static long LastSessionGeneration { get; private set; }

        public static int LastCookerTypeId { get; private set; } = -1;

        public static int UpdateCount { get; private set; }

        public static void UpdateTargets(RuntimeUiTargetSetSnapshot targetSet)
        {
            UpdateCount += 1;
            LastSessionGeneration = targetSet.SessionGeneration;
            var target = targetSet.Targets.FirstOrDefault(candidate =>
                candidate.CookerHighlightEnabled && candidate.CookerTypeId > 0);
            LastEnabled = target != null;
            LastCookerTypeId = LastEnabled ? target!.CookerTypeId : -1;
        }
    }

    internal static class RuntimeSeatHighlightService
    {
        public static string Status => LastEnabled ? "active" : "disabled";

        public static bool LastEnabled { get; private set; }

        public static long LastSessionGeneration { get; private set; }

        public static int LastDeskCode { get; private set; } = -1;

        public static int UpdateCount { get; private set; }

        public static void UpdateTargets(RuntimeUiTargetSetSnapshot targetSet)
        {
            UpdateCount += 1;
            LastSessionGeneration = targetSet.SessionGeneration;
            var target = targetSet.Targets.FirstOrDefault(candidate =>
                candidate.SeatHighlightEnabled && candidate.DeskCode >= 0);
            LastEnabled = target != null;
            LastDeskCode = LastEnabled ? target!.DeskCode : -1;
        }
    }

    internal static class RuntimeOrderTraceIdService
    {
        public static bool TryNormalizeTargetTraceId(
            RuntimeUiTargetKind kind,
            string traceId,
            bool enabled,
            out string normalized,
            out string failure)
        {
            if (!enabled)
            {
                normalized = "";
                failure = "";
                return true;
            }
            var prefix = kind == RuntimeUiTargetKind.Rare ? "R-" : "N-";
            if (!traceId.StartsWith(prefix, StringComparison.Ordinal)
                || traceId.Length is < 3 or > 18
                || traceId.Skip(2).Any(character => character is < '0' or > '9'))
            {
                normalized = "";
                failure = "invalid typed trace id";
                return false;
            }

            normalized = traceId;
            failure = "";
            return true;
        }
    }

    internal static class RuntimeOrderHighlightService
    {
        public static string Status => LastEnabled ? "active" : "disabled";

        public static bool LastEnabled { get; private set; }

        public static long LastSessionGeneration { get; private set; }

        public static string LastOrderTraceId { get; private set; } = "";

        public static int LastDeskCode { get; private set; } = -1;

        public static int UpdateCount { get; private set; }

        public static bool ThrowOnUpdate { get; set; }

        public static void UpdateTargets(RuntimeUiTargetSetSnapshot targetSet)
        {
            UpdateCount += 1;
            if (ThrowOnUpdate) throw new InvalidOperationException("HUD order-highlight update failed");
            LastSessionGeneration = targetSet.SessionGeneration;
            var target = targetSet.Targets.FirstOrDefault(candidate => candidate.OrderHighlightEnabled);
            LastEnabled = target != null;
            LastOrderTraceId = LastEnabled ? target!.OrderTraceId : "";
            LastDeskCode = LastEnabled ? target!.DeskCode : -1;
        }
    }

    internal static class RuntimeThrowDeliverOrderHighlightService
    {
        public static string Status => LastEnabled ? "active" : "disabled";

        public static bool LastEnabled { get; private set; }

        public static long LastSessionGeneration { get; private set; }

        public static string LastOrderTraceId { get; private set; } = "";

        public static int LastDeskCode { get; private set; } = -1;

        public static int UpdateCount { get; private set; }

        public static bool ThrowOnUpdate { get; set; }

        public static void UpdateTargets(RuntimeUiTargetSetSnapshot targetSet)
        {
            UpdateCount += 1;
            if (ThrowOnUpdate) throw new InvalidOperationException("throw-delivery order-highlight update failed");
            LastSessionGeneration = targetSet.SessionGeneration;
            var target = targetSet.Targets.FirstOrDefault(candidate => candidate.OrderHighlightEnabled);
            LastEnabled = target != null;
            LastOrderTraceId = LastEnabled ? target!.OrderTraceId : "";
            LastDeskCode = LastEnabled ? target!.DeskCode : -1;
        }
    }

    internal readonly record struct TargetRecipeVariantRowLease(
        nint PanelPointer,
        long PanelEpoch,
        nint RecipePointer,
        nint ButtonPointer,
        long TargetGeneration,
        string PlanIdentity);

    internal static class RuntimeTargetRecipeVariantService
    {
        internal const string HarmonyId =
            "com.tyukki.mystia-steward-companion.runtime-target-recipe-variant";

        private static readonly object SyncRoot = new();
        private static readonly Dictionary<nint, long> PanelEpochs = new();
        private static readonly Dictionary<nint, RowBinding> RowsByButton = new();

        public static string Status
        {
            get
            {
                lock (SyncRoot) return $"test bindings={RowsByButton.Count}";
            }
        }

        public static void Attach(BepInEx.Logging.ManualLogSource log)
        {
        }

        public static void RetireFailClosed(string reason)
        {
            lock (SyncRoot)
            {
                RowsByButton.Clear();
                PanelEpochs.Clear();
            }
        }

        public static TargetRecipeVariantRowLease BindRecipeRow(
            object panel,
            object recipe,
            object button,
            RuntimeUiTargetKinds claims,
            string planIdentity)
        {
            var panelPointer = RuntimeReflectionUtility.ReadObjectPointer(panel);
            var recipePointer = RuntimeReflectionUtility.ReadObjectPointer(recipe);
            var buttonPointer = RuntimeReflectionUtility.ReadObjectPointer(button);
            lock (SyncRoot)
            {
                if (!PanelEpochs.TryGetValue(panelPointer, out var panelEpoch))
                {
                    panelEpoch = 1;
                    PanelEpochs.Add(panelPointer, panelEpoch);
                }
                var lease = new TargetRecipeVariantRowLease(
                    panelPointer,
                    panelEpoch,
                    recipePointer,
                    buttonPointer,
                    RuntimeUiPinningService.ReadTargetSet().Generation,
                    planIdentity);
                RowsByButton[buttonPointer] = new RowBinding(lease, claims);
                return lease;
            }
        }

        public static void AdvancePanelEpoch(object panel)
        {
            var panelPointer = RuntimeReflectionUtility.ReadObjectPointer(panel);
            lock (SyncRoot)
            {
                var nextEpoch = PanelEpochs.TryGetValue(panelPointer, out var current)
                    ? checked(current + 1)
                    : 1;
                PanelEpochs[panelPointer] = nextEpoch;
                foreach (var buttonPointer in RowsByButton
                             .Where(entry => entry.Value.Lease.PanelPointer == panelPointer)
                             .Select(entry => entry.Key)
                             .ToArray())
                {
                    RowsByButton.Remove(buttonPointer);
                }
            }
        }

        public static bool TryResolveRecipeRowClaims(
            object panel,
            object recipe,
            object button,
            out RuntimeUiTargetKinds claims,
            out TargetRecipeVariantRowLease lease)
        {
            claims = RuntimeUiTargetKinds.None;
            lease = default;
            var panelPointer = RuntimeReflectionUtility.ReadObjectPointer(panel);
            var recipePointer = RuntimeReflectionUtility.ReadObjectPointer(recipe);
            var buttonPointer = RuntimeReflectionUtility.ReadObjectPointer(button);
            lock (SyncRoot)
            {
                if (!RowsByButton.TryGetValue(buttonPointer, out var binding)
                    || binding.Lease.PanelPointer != panelPointer
                    || binding.Lease.RecipePointer != recipePointer
                    || binding.Lease.ButtonPointer != buttonPointer
                    || !PanelEpochs.TryGetValue(panelPointer, out var panelEpoch)
                    || binding.Lease.PanelEpoch != panelEpoch
                    || binding.Lease.TargetGeneration != RuntimeUiPinningService.ReadTargetSet().Generation)
                {
                    return false;
                }

                claims = binding.Claims;
                lease = binding.Lease;
                return claims != RuntimeUiTargetKinds.None;
            }
        }

        public static bool TryValidateRecipeRowClaims(
            TargetRecipeVariantRowLease lease,
            out RuntimeUiTargetKinds claims)
        {
            claims = RuntimeUiTargetKinds.None;
            lock (SyncRoot)
            {
                if (!RowsByButton.TryGetValue(lease.ButtonPointer, out var binding)
                    || binding.Lease != lease
                    || !PanelEpochs.TryGetValue(lease.PanelPointer, out var panelEpoch)
                    || panelEpoch != lease.PanelEpoch
                    || lease.TargetGeneration != RuntimeUiPinningService.ReadTargetSet().Generation)
                {
                    return false;
                }

                claims = binding.Claims;
                return claims != RuntimeUiTargetKinds.None;
            }
        }

        private sealed record RowBinding(
            TargetRecipeVariantRowLease Lease,
            RuntimeUiTargetKinds Claims);
    }

    internal sealed class CookingIngredientLogicalGroupProbe
    {
        private static long _nextPointer = 2500;
        private readonly CookingSelectionPanelProbe _owner;

        public CookingIngredientLogicalGroupProbe(CookingSelectionPanelProbe owner)
        {
            _owner = owner;
        }

        public nint m_CachedPtr { get; } = new IntPtr(Interlocked.Increment(ref _nextPointer));

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateElements()
        {
            _owner.ApplyIngredientSurfaceRefresh();
        }
    }

    internal sealed class CookingRecipeLogicalGroupProbe
    {
        private static long _nextPointer = 3000;
        private readonly CookingSelectionPanelProbe _owner;

        public CookingRecipeLogicalGroupProbe(CookingSelectionPanelProbe owner)
        {
            _owner = owner;
        }

        public nint m_CachedPtr { get; } = new IntPtr(Interlocked.Increment(ref _nextPointer));

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateElements()
        {
            _owner.ApplyRecipeSurfaceRefresh();
        }
    }

    internal sealed class CookingSelectionPanelProbe
    {
        private static long _nextPointer = 1000;
        private readonly List<int> _ingredientSurfaceSource = new();
        private readonly List<int> _visibleIngredientIds = new();
        private readonly List<int> _recipeSurfaceSource = new();
        private readonly List<int> _visibleRecipeIds = new();

        public CookingSelectionPanelProbe()
        {
            m_StaticIngredientsGroup = new CookingIngredientLogicalGroupProbe(this);
            m_StaticRecipeGroup = new CookingRecipeLogicalGroupProbe(this);
        }

        public static UnityEngine.Color RecipeBoundColor { get; set; } = new(0.8f, 0.8f, 0.8f, 0.5f);

        public static bool ApplyIngredientBoundColor { get; set; }

        public static UnityEngine.Color IngredientBoundColor { get; set; } = new(0.4f, 0.6f, 0.8f, 0.35f);

        public static Func<bool>? IngredientRefreshAction { get; set; }

        public static Func<bool>? RecipeRefreshAction { get; set; }

        public static Action? OpenAction { get; set; }

        public static bool? LastIngredientResult { get; private set; }

        public static bool? LastRecipeResult { get; private set; }

        public static bool ThrowOnIngredientRefresh { get; set; }

        public static bool ThrowOnRecipeRefresh { get; set; }

        public static bool ThrowOnIngredientSurfaceRefresh { get; set; }

        public static bool ThrowOnRecipeSurfaceRefresh { get; set; }

        public static int IngredientRefreshCount { get; private set; }

        public static int RecipeRefreshCount { get; private set; }

        public static int FullVisualRefreshCount { get; private set; }

        public static int SelectedSurfaceRefreshCount { get; private set; }

        public static int OutputSurfaceRefreshCount { get; private set; }

        public static int IngredientSurfaceRefreshCount { get; private set; }

        public static int RecipeSurfaceRefreshCount { get; private set; }

        public static List<int> IngredientRefreshThreadIds { get; } = new();

        public static List<int> RecipeRefreshThreadIds { get; } = new();

        public static List<int> IngredientSurfaceRefreshThreadIds { get; } = new();

        public static List<int> RecipeSurfaceRefreshThreadIds { get; } = new();

        public static Action? IngredientSurfaceRefreshAction { get; set; }

        public static Action? RecipeSurfaceRefreshAction { get; set; }

        public static List<string> RefreshSequence { get; } = new();

        public nint m_CachedPtr { get; private set; } = new IntPtr(Interlocked.Increment(ref _nextPointer));

        public CookingIngredientLogicalGroupProbe m_StaticIngredientsGroup { get; }

        public CookingRecipeLogicalGroupProbe m_StaticRecipeGroup { get; }

        public IReadOnlyList<int> VisibleIngredientIds => _visibleIngredientIds;

        public IReadOnlyList<int> VisibleRecipeIds => _visibleRecipeIds;

        public static void ResetRefreshProbe()
        {
            ThrowOnIngredientRefresh = false;
            ThrowOnRecipeRefresh = false;
            ThrowOnIngredientSurfaceRefresh = false;
            ThrowOnRecipeSurfaceRefresh = false;
            IngredientRefreshCount = 0;
            RecipeRefreshCount = 0;
            FullVisualRefreshCount = 0;
            SelectedSurfaceRefreshCount = 0;
            OutputSurfaceRefreshCount = 0;
            IngredientSurfaceRefreshCount = 0;
            RecipeSurfaceRefreshCount = 0;
            IngredientRefreshThreadIds.Clear();
            RecipeRefreshThreadIds.Clear();
            IngredientSurfaceRefreshThreadIds.Clear();
            RecipeSurfaceRefreshThreadIds.Clear();
            IngredientRefreshAction = null;
            RecipeRefreshAction = null;
            IngredientSurfaceRefreshAction = null;
            RecipeSurfaceRefreshAction = null;
            RefreshSequence.Clear();
            LastIngredientResult = null;
            LastRecipeResult = null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateAllVisual()
        {
            FullVisualRefreshCount++;
            UpdateIngField();
            UpdateRecipeField();
            SelectedSurfaceRefreshCount++;
            OutputSurfaceRefreshCount++;
            m_StaticIngredientsGroup.UpdateElements();
            m_StaticRecipeGroup.UpdateElements();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateIngField()
        {
            IngredientRefreshCount++;
            IngredientRefreshThreadIds.Add(Environment.CurrentManagedThreadId);
            RefreshSequence.Add("ingredient-data");
            if (ThrowOnIngredientRefresh) throw new InvalidOperationException("ingredient data refresh failed");
            LastIngredientResult = IngredientRefreshAction?.Invoke();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateRecipeField()
        {
            RecipeRefreshCount++;
            RecipeRefreshThreadIds.Add(Environment.CurrentManagedThreadId);
            RefreshSequence.Add("recipe-data");
            if (ThrowOnRecipeRefresh) throw new InvalidOperationException("recipe data refresh failed");
            LastRecipeResult = RecipeRefreshAction?.Invoke();
        }

        public void SetIngredientSurfaceSource(params int[] ingredientIds)
        {
            _ingredientSurfaceSource.Clear();
            _ingredientSurfaceSource.AddRange(ingredientIds);
        }

        public void SetRecipeSurfaceSource(params int[] recipeIds)
        {
            _recipeSurfaceSource.Clear();
            _recipeSurfaceSource.AddRange(recipeIds);
        }

        internal void ApplyIngredientSurfaceRefresh()
        {
            IngredientSurfaceRefreshCount++;
            IngredientSurfaceRefreshThreadIds.Add(Environment.CurrentManagedThreadId);
            RefreshSequence.Add("ingredient-visible");
            IngredientSurfaceRefreshAction?.Invoke();
            if (ThrowOnIngredientSurfaceRefresh) throw new InvalidOperationException("ingredient surface refresh failed");
            _visibleIngredientIds.Clear();
            _visibleIngredientIds.AddRange(_ingredientSurfaceSource);
        }

        internal void ApplyRecipeSurfaceRefresh()
        {
            RecipeSurfaceRefreshCount++;
            RecipeSurfaceRefreshThreadIds.Add(Environment.CurrentManagedThreadId);
            RefreshSequence.Add("recipe-visible");
            RecipeSurfaceRefreshAction?.Invoke();
            if (ThrowOnRecipeSurfaceRefresh) throw new InvalidOperationException("recipe surface refresh failed");
            _visibleRecipeIds.Clear();
            _visibleRecipeIds.AddRange(_recipeSurfaceSource);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnPanelOpen()
        {
            UpdateAllVisual();
            OpenAction?.Invoke();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnRecipeElementEnabled(RecipeProbe recipe, object cluster, UIButtonSimpleProbe uiButtonSimple)
        {
            uiButtonSimple.image.set_color(RecipeBoundColor);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnIngElementEnabled(KeyValuePair<IngredientProbe, int> objectBase, object cluster, UIButtonSimpleProbe uiButtonSimple)
        {
            if (ApplyIngredientBoundColor) uiButtonSimple.image.set_color(IngredientBoundColor);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnPanelClose()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnPanelDestroyed()
        {
            m_CachedPtr = 0;
        }
    }

    internal sealed class BeverageLogicalGroupProbe
    {
        private static long _nextPointer = 4000;
        private readonly StoragePanelProbe _owner;

        public BeverageLogicalGroupProbe(StoragePanelProbe owner)
        {
            _owner = owner;
        }

        public nint m_CachedPtr { get; } = new IntPtr(Interlocked.Increment(ref _nextPointer));

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateElements()
        {
            _owner.ApplyBeverageSurfaceRefresh();
        }
    }

    internal sealed class StoragePanelProbe
    {
        private static long _nextPointer = 2000;
        private readonly List<int> _beverageSurfaceSource = new();
        private readonly List<int> _visibleBeverageIds = new();

        public StoragePanelProbe(SellableTypeProbe panelType = SellableTypeProbe.Beverage)
        {
            openType = panelType;
            m_BevsGroup = new BeverageLogicalGroupProbe(this);
        }

        public static UnityEngine.Color BoundColor { get; set; } = new(0.35f, 0.55f, 0.75f, 0.6f);

        public static Func<bool>? RefreshAction { get; set; }

        public static Action? OpenAction { get; set; }

        public static bool? LastResult { get; private set; }

        public static bool ThrowOnRefresh { get; set; }

        public static bool ThrowOnSurfaceRefresh { get; set; }

        public static int RefreshCount { get; private set; }

        public static int SurfaceRefreshCount { get; private set; }

        public static List<int> RefreshThreadIds { get; } = new();

        public static List<int> SurfaceRefreshThreadIds { get; } = new();

        public static Action? SurfaceRefreshAction { get; set; }

        public nint m_CachedPtr { get; private set; } = new IntPtr(Interlocked.Increment(ref _nextPointer));

        public BeverageLogicalGroupProbe m_BevsGroup { get; }

        public SellableTypeProbe openType { get; }

        public IReadOnlyList<int> VisibleBeverageIds => _visibleBeverageIds;

        public static void ResetRefreshProbe()
        {
            ThrowOnRefresh = false;
            ThrowOnSurfaceRefresh = false;
            RefreshCount = 0;
            SurfaceRefreshCount = 0;
            RefreshThreadIds.Clear();
            SurfaceRefreshThreadIds.Clear();
            RefreshAction = null;
            SurfaceRefreshAction = null;
            LastResult = null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateBevField()
        {
            RefreshCount++;
            RefreshThreadIds.Add(Environment.CurrentManagedThreadId);
            if (ThrowOnRefresh) throw new InvalidOperationException("storage refresh failed");
            LastResult = RefreshAction?.Invoke();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnPanelOpen()
        {
            if (openType == SellableTypeProbe.Beverage)
            {
                UpdateBevField();
                m_BevsGroup.UpdateElements();
            }
            OpenAction?.Invoke();
        }

        public void SetBeverageSurfaceSource(params int[] beverageIds)
        {
            _beverageSurfaceSource.Clear();
            _beverageSurfaceSource.AddRange(beverageIds);
        }

        internal void ApplyBeverageSurfaceRefresh()
        {
            SurfaceRefreshCount++;
            SurfaceRefreshThreadIds.Add(Environment.CurrentManagedThreadId);
            SurfaceRefreshAction?.Invoke();
            if (ThrowOnSurfaceRefresh) throw new InvalidOperationException("storage surface refresh failed");
            _visibleBeverageIds.Clear();
            _visibleBeverageIds.AddRange(_beverageSurfaceSource);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnElementEnabled(KeyValuePair<SellableProbe, int> objectBase, object cluster, UIButtonSimpleProbe uiButtonSimple)
        {
            uiButtonSimple.image.set_color(BoundColor);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnPanelClose()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnPanelDestroyed()
        {
            m_CachedPtr = 0;
        }
    }

    internal static class RunTimePlayerDataProbe
    {
        public static bool NativeResult { get; set; }

        public static int NativeCallCount { get; private set; }

        public static void Reset(bool nativeResult)
        {
            NativeResult = nativeResult;
            NativeCallCount = 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CheckPinned(PlayerSaveFileDefaultPropProbe pinnedType, int pinnedID)
        {
            NativeCallCount++;
            return NativeResult;
        }
    }

    internal enum PlayerSaveFileDefaultPropProbe
    {
        IngredientsSeafood = 0,
        Recipes = 1,
        Beverages = 2,
        IngredientsMeat = 4,
        IngredientsVegetable = 5,
        IngredientsOther = 6,
    }

    internal sealed class RecipeProbe
    {
        private readonly int _id;

        public RecipeProbe(int id, bool throwOnIdRead = false)
        {
            _id = id;
            ThrowOnIdRead = throwOnIdRead;
        }

        public bool ThrowOnIdRead { get; }

        public int id => ThrowOnIdRead
            ? throw new InvalidOperationException("recipe id is unavailable")
            : _id;
    }

    internal sealed class IngredientProbe
    {
        public IngredientProbe(int id)
        {
            this.id = id;
        }

        public int id { get; }
    }

    internal sealed class SellableProbe
    {
        public SellableProbe(int id, SellableTypeProbe type)
        {
            this.id = id;
            Type = type;
        }

        public int id { get; }

        public SellableTypeProbe Type { get; }
    }

    internal enum SellableTypeProbe
    {
        Food,
        Beverage,
    }

    internal abstract class SelectableProbeBase
    {
        protected SelectableProbeBase(UnityEngine.Color color)
        {
            image = new UiImageProbe(color);
        }

        public UiImageProbe image { get; }
    }

    internal sealed class UIButtonSimpleProbe : SelectableProbeBase
    {
        public UIButtonSimpleProbe(UnityEngine.Color color)
            : base(color)
        {
        }
    }

    internal abstract class GraphicProbeBase
    {
        private static long _nextPointer;
        private UnityEngine.Color _color;
        private readonly List<int> _setterThreadIds = new();
        private ManualResetEventSlim? _setterEntered;
        private ManualResetEventSlim? _setterRelease;

        protected GraphicProbeBase(UnityEngine.Color color)
        {
            _color = color;
            m_CachedPtr = new IntPtr(Interlocked.Increment(ref _nextPointer));
        }

        public IntPtr m_CachedPtr { get; }

        public bool ThrowOnRead { get; set; }

        public bool BarrierTimedOut { get; private set; }

        public int SetterCount
        {
            get
            {
                lock (_setterThreadIds)
                {
                    return _setterThreadIds.Count;
                }
            }
        }

        public int[] SetterThreadIds
        {
            get
            {
                lock (_setterThreadIds)
                {
                    return _setterThreadIds.ToArray();
                }
            }
        }

        public void BlockNextSetter(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _setterEntered = entered;
            _setterRelease = release;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public UnityEngine.Color get_color()
        {
            if (ThrowOnRead) throw new InvalidOperationException("image is unavailable");
            return _color;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void set_color(UnityEngine.Color color)
        {
            _color = color;
            lock (_setterThreadIds)
            {
                _setterThreadIds.Add(Environment.CurrentManagedThreadId);
            }

            var entered = Interlocked.Exchange(ref _setterEntered, null);
            var release = Interlocked.Exchange(ref _setterRelease, null);
            if (entered == null || release == null) return;

            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                BarrierTimedOut = true;
                throw new TimeoutException("Timed out waiting to release the image setter barrier.");
            }
        }
    }

    internal sealed class UiImageProbe : GraphicProbeBase
    {
        public UiImageProbe(UnityEngine.Color color)
            : base(color)
        {
        }
    }
}
