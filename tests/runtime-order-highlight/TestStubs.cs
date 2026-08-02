using System.Reflection;

namespace BepInEx.Logging
{
    internal sealed class ManualLogSource
    {
        public List<string> Information { get; } = new();
        public List<string> Warnings { get; } = new();

        public void LogInfo(object value) => Information.Add(value?.ToString() ?? "");
        public void LogWarning(object value) => Warnings.Add(value?.ToString() ?? "");
    }
}

namespace HarmonyLib
{
    internal static class Priority
    {
        public const int First = 800;
        public const int Last = 0;
    }

    internal sealed class HarmonyMethod
    {
        public HarmonyMethod(MethodInfo method) => methodInfo = method;

        public MethodInfo methodInfo { get; }
        public int priority { get; set; }
    }

    internal sealed record PatchRecord(MethodInfo Target, HarmonyMethod? Prefix, HarmonyMethod? Postfix);

    internal sealed class Harmony
    {
        public Harmony(string id) => Id = id;

        public static List<PatchRecord> Patches { get; } = new();
        public string Id { get; }

        public void Patch(MethodInfo target, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null)
        {
            Patches.Add(new PatchRecord(target, prefix, postfix));
        }
    }
}

namespace Il2CppInterop.Runtime
{
    internal static class Il2CppClassPointerStore
    {
        private static readonly Dictionary<Type, IntPtr> ClassPointers = new();
        private static long _nextClassPointer = 10_000;

        public static IntPtr GetNativeClassPointer(Type type)
        {
            if (ClassPointers.TryGetValue(type, out var pointer)) return pointer;
            pointer = new IntPtr(Interlocked.Increment(ref _nextClassPointer));
            ClassPointers.Add(type, pointer);
            return pointer;
        }
    }

    internal static class Il2CppType
    {
        public static Type From(Type type) => type;
    }

    internal static class IL2CPP
    {
        public static IntPtr il2cpp_object_get_class(IntPtr pointer)
        {
            var type = UnityEngine.Object.GetNativeType(pointer);
            return type == null ? IntPtr.Zero : Il2CppClassPointerStore.GetNativeClassPointer(type);
        }
    }
}

namespace Il2CppSystem
{
    internal sealed class Action
    {
    }
}

namespace UnityEngine
{
    internal class Object
    {
        private static long _nextPointer = 100;
        private static readonly Dictionary<IntPtr, Type> NativeTypes = new();
        private IntPtr _cachedPointer;

        public Object()
            : this(new IntPtr(Interlocked.Increment(ref _nextPointer)))
        {
        }

        protected Object(IntPtr pointer)
        {
            Pointer = pointer;
            _cachedPointer = pointer;
            if (!NativeTypes.ContainsKey(pointer)) NativeTypes.Add(pointer, GetType());
        }

        public IntPtr Pointer { get; }
        public IntPtr m_CachedPtr => _cachedPointer;
        public string name { get; set; } = "";

        public void InvalidateNativePointer() => _cachedPointer = IntPtr.Zero;

        internal static Type? GetNativeType(IntPtr pointer)
        {
            return NativeTypes.TryGetValue(pointer, out var type) ? type : null;
        }

        public static T Instantiate<T>(T original, Transform parent) where T : Object
        {
            if (original is not GameObject source)
            {
                throw new InvalidOperationException("Only GameObject cloning is supported by this smoke.");
            }

            var clone = source.DeepClone(parent);
            GameObject.Instantiated.Add(clone);
            return (T)(Object)clone;
        }

        public static void Destroy(Object target)
        {
            target.InvalidateNativePointer();
            if (target is not GameObject gameObject) return;
            gameObject.DestroyHierarchy();
            GameObject.Destroyed.Add(gameObject);
        }
    }

    internal sealed class GameObject : Object
    {
        private readonly List<Component> _components = new();
        private bool _activeSelf = true;

        public GameObject()
        {
            transform = new RectTransform(this);
            Attach(transform);
        }

        public static List<GameObject> Instantiated { get; } = new();
        public static List<GameObject> Destroyed { get; } = new();

        public RectTransform transform { get; }
        public bool activeSelf => _activeSelf;
        public bool activeInHierarchy => _activeSelf && (transform.parent?.gameObject.activeInHierarchy ?? true);
        public bool ReturnBaseComponentWrappers { get; set; }
        public bool ReturnBaseTypedComponentWrappers { get; set; }

        public void SetActive(bool value) => _activeSelf = value;

        public void Attach(Component component)
        {
            if (ReferenceEquals(component.gameObject, this) && _components.Contains(component)) return;
            component.Rebind(this);
            if (!_components.Contains(component)) _components.Add(component);
        }

        public void Detach(Component component) => _components.Remove(component);

        public Component? GetComponent(Type type)
        {
            var component = _components.SingleOrDefault(type.IsInstanceOfType);
            return component == null || !ReturnBaseTypedComponentWrappers
                ? component
                : new Component(component.Pointer, component);
        }

        public T[] GetComponents<T>() where T : Component
        {
            if (ReturnBaseComponentWrappers && typeof(T) == typeof(Component))
            {
                return _components
                    .Select(component => (T)(object)new Component(component.Pointer, component))
                    .ToArray();
            }

            return _components.OfType<T>().ToArray();
        }

        internal GameObject DeepClone(Transform parent)
        {
            var clone = new GameObject { name = name };
            clone.transform.parent = parent;
            clone.transform.localPosition = transform.localPosition;
            clone.transform.localRotation = transform.localRotation;
            clone.transform.localScale = transform.localScale;
            clone.SetActive(activeSelf);
            clone.ReturnBaseComponentWrappers = ReturnBaseComponentWrappers;
            clone.ReturnBaseTypedComponentWrappers = ReturnBaseTypedComponentWrappers;

            foreach (var component in _components)
            {
                switch (component)
                {
                    case RectTransform:
                        break;
                    case CanvasRenderer:
                        clone.Attach(new CanvasRenderer());
                        break;
                    case UnityEngine.UI.Image sourceImage:
                        clone.Attach(new UnityEngine.UI.Image
                        {
                            sprite = sourceImage.sprite,
                            color = sourceImage.color,
                            raycastTarget = sourceImage.raycastTarget,
                            enabled = sourceImage.enabled,
                        });
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported cloned component {component.GetType().FullName}.");
                }
            }

            return clone;
        }

        internal void DestroyHierarchy()
        {
            InvalidateNativePointer();
            foreach (var component in _components) component.InvalidateNativePointer();
            foreach (var child in transform.Children.ToArray()) child.gameObject.DestroyHierarchy();
        }
    }

    internal class Component : Object
    {
        private GameObject? _gameObject;

        public Component()
        {
        }

        internal Component(IntPtr pointer, Component nativeComponent)
            : base(pointer)
        {
            NativeComponent = nativeComponent;
        }

        internal Component? NativeComponent { get; }
        public GameObject gameObject => _gameObject ??= new GameObject();
        public Transform transform => gameObject.transform;

        internal void Rebind(GameObject gameObject) => _gameObject = gameObject;
    }

    internal class Transform : Component
    {
        private Transform? _parent;

        public Transform()
        {
        }

        internal Transform(GameObject owner) => Rebind(owner);

        internal List<Transform> Children { get; } = new();

        public Transform? parent
        {
            get => _parent;
            set
            {
                _parent?.Children.Remove(this);
                _parent = value;
                if (value != null && !value.Children.Contains(this)) value.Children.Add(this);
            }
        }

        public Vector3 localPosition { get; set; }
        public Quaternion localRotation { get; set; } = Quaternion.identity;
        public Vector3 localScale { get; set; } = Vector3.one;
        public int childCount => Children.Count;

        public bool IsChildOf(Transform ancestor)
        {
            for (Transform? current = this; current != null; current = current.parent)
            {
                if (current.Pointer == ancestor.Pointer) return true;
            }
            return false;
        }

        public int GetSiblingIndex() => parent?.Children.IndexOf(this) ?? 0;

        public void SetSiblingIndex(int index)
        {
            if (parent == null) return;
            parent.Children.Remove(this);
            parent.Children.Insert(Math.Clamp(index, 0, parent.Children.Count), this);
        }
    }

    internal sealed class RectTransform : Transform
    {
        public RectTransform()
        {
        }

        internal RectTransform(GameObject owner) : base(owner)
        {
        }
    }

    internal sealed class CanvasRenderer : Component
    {
    }

    internal sealed class Sprite : Object
    {
    }

    internal readonly record struct Vector3(float x, float y, float z)
    {
        public static Vector3 one => new(1f, 1f, 1f);
    }

    internal readonly record struct Quaternion(float x, float y, float z, float w)
    {
        public static Quaternion identity => new(0f, 0f, 0f, 1f);
    }

    internal readonly record struct Color(float r, float g, float b, float a);

    internal static class Mathf
    {
        public static float Sin(float value) => MathF.Sin(value);
    }

    internal static class Time
    {
        public static float realtimeSinceStartup { get; set; }
    }
}

namespace UnityEngine.UI
{
    internal class LayoutGroup : UnityEngine.Component
    {
    }

    internal sealed class VerticalLayoutGroup : LayoutGroup
    {
    }

    internal class Image : UnityEngine.Component
    {
        public UnityEngine.Sprite? sprite { get; set; }
        public UnityEngine.Color color { get; set; } = new(1f, 1f, 1f, 1f);
        public bool raycastTarget { get; set; } = true;
        public bool enabled { get; set; } = true;
    }

    internal sealed class DerivedImage : Image
    {
    }
}

namespace NightScene.GuestManagementUtility
{
    internal static class GuestsManager
    {
        internal sealed class OrderBase : UnityEngine.Object
        {
            public OrderBase(int deskCode = 0) => DeskCode = deskCode;

            public int DeskCode { get; }
        }
    }
}

namespace NightScene.UI.GuestManagementUtility
{
    using NightScene.GuestManagementUtility;
    using UnityEngine;
    using UnityEngine.UI;

    internal sealed class OrderingElement : Component
    {
        private readonly GameObject _borderRoot;
        private GuestsManager.OrderBase? _activeOrder;

        public OrderingElement(Image? borderImage = null)
        {
            var owner = gameObject;
            owner.name = "OrderingElement";
            _borderRoot = new GameObject { name = "CurrentBorder" };
            _borderRoot.transform.parent = owner.transform;
            _borderRoot.Attach(new CanvasRenderer());
            var image = borderImage ?? new Image();
            image.sprite ??= new Sprite();
            _borderRoot.Attach(image);
            borderStyleImageForCurrent = image;
        }

        public GuestsManager.OrderBase? ActiveOrder => _activeOrder;
        public Image borderStyleImageForCurrent { get; }
        public bool current { get; set; }
        public int ChangeBorderStyleCalls { get; private set; }

        public void Initialize(GuestsManager.OrderBase request, Transform ghostField, int deskCode, Sprite overridePic, Il2CppSystem.Action onOutFinish)
        {
        }

        internal void BindActiveOrder(GuestsManager.OrderBase request) => _activeOrder = request;

        public void Out()
        {
        }

        public void DestroySelf()
        {
        }

        private void OnDestroy()
        {
        }

        public void ChangeBorderStyle(bool value)
        {
            ChangeBorderStyleCalls += 1;
            current = value;
        }

        public void SetPartnerHighlight(Sprite sprite, int sellableType)
        {
        }
    }
}

namespace Night.UI.HUD.Ordering
{
    using NightScene.GuestManagementUtility;
    using NightScene.UI.GuestManagementUtility;
    using UnityEngine;

    internal sealed class OrderController : Component
    {
        public Func<OrderingElement> ElementFactory { get; set; } = static () => new OrderingElement();
        public System.Action<OrderingElement, GuestsManager.OrderBase, int>? InitializePrefix { get; set; }
        public int NextDeskCode { get; set; }

        public OrderingElement CreateOrderingElement(GuestsManager.OrderBase order)
        {
            var element = ElementFactory();
            InitializePrefix?.Invoke(element, order, NextDeskCode);
            element.Initialize(order, new Transform(), NextDeskCode, new Sprite(), new Il2CppSystem.Action());
            element.BindActiveOrder(order);
            return element;
        }
    }
}

namespace MystiaStewardCompanion.Core
{
    internal sealed class NightBusinessOrder
    {
        public DateTime? FirstSeenAtUtc { get; init; }
        public int DeskCode { get; init; }
        public int? RuntimeGuestId { get; init; }
        public int? FoodTagId { get; init; }
        public int? BeverageTagId { get; init; }
        public bool IsFreeOrder { get; init; }
    }

    internal sealed class NormalBusinessOrder
    {
        public string OrderKey { get; init; } = "";
        public DateTime? FirstSeenAtUtc { get; init; }
        public int DeskCode { get; init; }
        public string GuestName { get; init; } = "";
        public int FoodId { get; init; }
        public string FoodName { get; init; } = "";
        public int BeverageId { get; init; }
        public string BeverageName { get; init; } = "";
    }
}

namespace MystiaStewardCompanion.Save
{
    using NightScene.GuestManagementUtility;
    using Night.UI.HUD.Ordering;
    using NightScene.UI.GuestManagementUtility;

    internal readonly record struct NightBusinessLifecycleSnapshot(bool IsActive, long Generation, int ThreadId);

    internal static class RuntimeNightBusinessLifecycle
    {
        public static NightBusinessLifecycleSnapshot Snapshot { get; set; }
        public static bool IsActive => Snapshot.IsActive;
        public static long Generation => Snapshot.Generation;
    }

    internal sealed record CapturedRuntimeSpecialOrder(
        string RuntimeKey,
        object? OrderObject,
        object? ControllerObject,
        DateTime FirstCapturedAt,
        int DeskCode,
        int GuestId,
        bool HasFoodTagId,
        int FoodTagId,
        bool HasBeverageTagId,
        int BeverageTagId,
        bool IsFreeOrder);

    internal static class SpecialOrderRuntimeCapture
    {
        public static List<CapturedRuntimeSpecialOrder> Captures { get; } = new();

        public static IReadOnlyList<CapturedRuntimeSpecialOrder> Snapshot(TimeSpan maxAge) => Captures.ToArray();
    }

    internal static class RuntimeReflectionUtility
    {
        public static Type? FindType(string fullName) => fullName switch
        {
            "NightScene.UI.GuestManagementUtility.OrderingElement" => typeof(OrderingElement),
            "Night.UI.HUD.Ordering.OrderController" => typeof(OrderController),
            "UnityEngine.CanvasRenderer" => typeof(UnityEngine.CanvasRenderer),
            _ => null,
        };

        public static bool TryReadNativeObjectPointer(object? target, out nint pointer)
        {
            if (target is not UnityEngine.Object unityObject)
            {
                pointer = 0;
                return false;
            }

            pointer = unityObject.Pointer;
            return pointer != 0 && unityObject.m_CachedPtr != IntPtr.Zero;
        }

        public static object? TryCastRuntimeObject(object? value, string targetTypeName)
        {
            var candidate = value is UnityEngine.Component component
                ? component.NativeComponent ?? component
                : value;
            return targetTypeName == "UnityEngine.UI.Image" && candidate is UnityEngine.UI.Image
                ? candidate
                : null;
        }
    }
}
