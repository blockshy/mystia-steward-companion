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
        public static int InformationCount { get; private set; }

        public void LogInfo(object value)
        {
            InformationCount += 1;
        }

        public void LogWarning(object value)
        {
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

        public static int UpdateCount { get; private set; }

        public static void UpdateTarget(long sessionGeneration, bool enabled, int cookerTypeId, string cookerName)
        {
            UpdateCount += 1;
            LastEnabled = enabled;
        }
    }

    internal sealed class CookingSelectionPanelProbe
    {
        private static long _nextPointer = 1000;

        public static UnityEngine.Color RecipeBoundColor { get; set; } = new(0.8f, 0.8f, 0.8f, 0.5f);

        public static bool ApplyIngredientBoundColor { get; set; }

        public static UnityEngine.Color IngredientBoundColor { get; set; } = new(0.4f, 0.6f, 0.8f, 0.35f);

        public static Func<bool>? RefreshAction { get; set; }

        public static Action? OpenAction { get; set; }

        public static bool? LastResult { get; private set; }

        public static bool ThrowOnRefresh { get; set; }

        public static int RefreshCount { get; private set; }

        public static List<int> RefreshThreadIds { get; } = new();

        public nint m_CachedPtr { get; private set; } = new IntPtr(Interlocked.Increment(ref _nextPointer));

        public static void ResetRefreshProbe()
        {
            ThrowOnRefresh = false;
            RefreshCount = 0;
            RefreshThreadIds.Clear();
            RefreshAction = null;
            LastResult = null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateAllVisual()
        {
            RefreshCount++;
            RefreshThreadIds.Add(Environment.CurrentManagedThreadId);
            if (ThrowOnRefresh) throw new InvalidOperationException("cooking refresh failed");
            LastResult = RefreshAction?.Invoke();
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

    internal sealed class StoragePanelProbe
    {
        private static long _nextPointer = 2000;

        public static UnityEngine.Color BoundColor { get; set; } = new(0.35f, 0.55f, 0.75f, 0.6f);

        public static Func<bool>? RefreshAction { get; set; }

        public static Action? OpenAction { get; set; }

        public static bool? LastResult { get; private set; }

        public static bool ThrowOnRefresh { get; set; }

        public static int RefreshCount { get; private set; }

        public static List<int> RefreshThreadIds { get; } = new();

        public nint m_CachedPtr { get; private set; } = new IntPtr(Interlocked.Increment(ref _nextPointer));

        public static void ResetRefreshProbe()
        {
            ThrowOnRefresh = false;
            RefreshCount = 0;
            RefreshThreadIds.Clear();
            RefreshAction = null;
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
            UpdateBevField();
            OpenAction?.Invoke();
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
        Recipes = 1,
        Beverages = 2,
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
