using System.Reflection;
using System.Runtime.InteropServices;

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
        public void Patch(MethodInfo target, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null) =>
            Patches.Add(new PatchRecord(target, prefix, postfix));
    }
}

namespace Il2CppInterop.Runtime
{
    internal static class Il2CppClassPointerStore
    {
        private static readonly Dictionary<Type, IntPtr> Pointers = new();
        private static readonly Dictionary<IntPtr, Type> Types = new();
        private static long _next = 10_000;

        public static IntPtr GetNativeClassPointer(Type type)
        {
            if (Pointers.TryGetValue(type, out var pointer)) return pointer;
            pointer = new IntPtr(Interlocked.Increment(ref _next));
            Pointers.Add(type, pointer);
            Types.Add(pointer, type);
            return pointer;
        }

        public static Type? GetManagedType(IntPtr pointer) =>
            Types.TryGetValue(pointer, out var type) ? type : null;
    }

    internal static class Il2CppType
    {
        public static Il2CppSystem.Type From(Type type) => new(type);
    }

    internal static class IL2CPP
    {
        private static readonly Dictionary<string, IntPtr> NativeStrings = new(StringComparer.Ordinal);

        public static IntPtr il2cpp_object_get_class(IntPtr pointer)
        {
            var type = UnityEngine.Object.GetNativeType(pointer);
            return type == null ? IntPtr.Zero : Il2CppClassPointerStore.GetNativeClassPointer(type);
        }

        public static IntPtr il2cpp_class_get_name(IntPtr pointer) =>
            GetNativeString(Il2CppClassPointerStore.GetManagedType(pointer)?.Name ?? "");

        public static IntPtr il2cpp_class_get_namespace(IntPtr pointer) =>
            GetNativeString(Il2CppClassPointerStore.GetManagedType(pointer)?.Namespace ?? "");

        public static string il2cpp_class_get_name_(IntPtr pointer) =>
            Marshal.PtrToStringAnsi(il2cpp_class_get_name(pointer)) ?? "";

        public static string il2cpp_class_get_namespace_(IntPtr pointer) =>
            Marshal.PtrToStringAnsi(il2cpp_class_get_namespace(pointer)) ?? "";

        private static IntPtr GetNativeString(string value)
        {
            if (NativeStrings.TryGetValue(value, out var pointer)) return pointer;
            pointer = Marshal.StringToHGlobalAnsi(value);
            NativeStrings.Add(value, pointer);
            return pointer;
        }
    }
}

namespace UnityEngine
{
    internal readonly record struct Vector2(float x, float y);
    internal readonly record struct Vector3(float x, float y, float z)
    {
        public static Vector3 one => new(1f, 1f, 1f);
    }
    internal readonly record struct Color(float r, float g, float b, float a);
    internal readonly record struct Rect(float x, float y, float width, float height);

    internal class Object
    {
        private static long _nextPointer = 100;
        private static readonly Dictionary<IntPtr, Type> NativeTypes = new();
        private IntPtr _nativePointer;
        private IntPtr _cachedPointer;

        public Object() : this(new IntPtr(Interlocked.Increment(ref _nextPointer))) { }

        protected Object(IntPtr sharedPointer)
        {
            _nativePointer = sharedPointer;
            _cachedPointer = sharedPointer;
            NativeTypes[sharedPointer] = GetType();
        }

        public IntPtr Pointer => _nativePointer;
        public IntPtr m_CachedPtr => _cachedPointer;
        public string name { get; set; } = "";
        public void InvalidateNativePointer() => _cachedPointer = IntPtr.Zero;
        public IntPtr RebindNativeIdentity()
        {
            var pointer = new IntPtr(Interlocked.Increment(ref _nextPointer));
            _nativePointer = pointer;
            _cachedPointer = pointer;
            NativeTypes[pointer] = GetType();
            return pointer;
        }
        public static T Instantiate<T>(T original, Transform parent) where T : Object
        {
            if (original is not GameObject source)
            {
                throw new InvalidOperationException(
                    "Only GameObject cloning is supported by the Throw smoke fixture.");
            }

            var clone = source.DeepClone(parent);
            GameObject.Instantiated.Add(clone);
            var afterInstantiate = AfterInstantiate;
            AfterInstantiate = null;
            afterInstantiate?.Invoke();
            return (T)(Object)clone;
        }

        public static Action? AfterInstantiate { get; set; }
        public static void Destroy(Object target)
        {
            if (target is GameObject gameObject)
            {
                gameObject.DestroyRecursively();
                GameObject.Destroyed.Add(gameObject);
            }
            else
            {
                target.InvalidateNativePointer();
            }
        }
        public void OverrideNativeType(Type type) => NativeTypes[Pointer] = type;
        internal static Type? GetNativeType(IntPtr pointer) =>
            NativeTypes.TryGetValue(pointer, out var type) ? type : null;
    }

    internal class Component : Object
    {
        private GameObject? _gameObject;
        public Component() { }
        internal Component(IntPtr sharedPointer) : base(sharedPointer) { }
        public GameObject gameObject => _gameObject ??= new GameObject();
        public Transform transform => gameObject.transform;
        internal void Rebind(GameObject owner) => _gameObject = owner;
    }

    internal class Transform : Component
    {
        private Transform? _parent;
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

        public int childCount => Children.Count;
        public Transform GetChild(int index) => Children[index];
        public int GetSiblingIndex() => _parent?.Children.IndexOf(this) ?? 0;
        public Vector3 localScale { get; set; } = new(1f, 1f, 1f);
        public static string? FailNextSetParentForObjectName { get; set; }
        public void SetParent(Transform? newParent, bool worldPositionStays)
        {
            if (string.Equals(
                    FailNextSetParentForObjectName,
                    gameObject.name,
                    StringComparison.Ordinal))
            {
                FailNextSetParentForObjectName = null;
                throw new InvalidOperationException("Injected SetParent failure.");
            }
            parent = newParent;
        }
        public void SetAsLastSibling()
        {
            if (_parent == null) return;
            _parent.Children.Remove(this);
            _parent.Children.Add(this);
        }
        public void SetSiblingIndex(int index)
        {
            if (FailNextSetSiblingIndex)
            {
                FailNextSetSiblingIndex = false;
                throw new InvalidOperationException("Injected SetSiblingIndex failure.");
            }
            if (_parent == null) return;
            _parent.Children.Remove(this);
            _parent.Children.Insert(index, this);
        }

        public static bool FailNextSetSiblingIndex { get; set; }
    }

    internal sealed class RectTransform : Transform
    {
        internal RectTransform(GameObject owner) : base(owner) { }
        private Rect _rect = new(-160f, -90f, 320f, 180f);
        public bool ThrowOnRectRead { get; set; }
        public Rect rect
        {
            get
            {
                if (ThrowOnRectRead)
                {
                    throw new InvalidOperationException(
                        "Injected RectTransform.rect getter failure.");
                }
                return _rect;
            }
            set => _rect = value;
        }
        public Vector2 anchorMin { get; set; } = new(0f, 0f);
        public Vector2 anchorMax { get; set; } = new(1f, 1f);
        public Vector2 pivot { get; set; } = new(0.5f, 0.5f);
        public Vector2 anchoredPosition { get; set; } = new(12f, -8f);
        public Vector2 sizeDelta { get; set; } = new(-24f, -16f);
        public Vector2 offsetMin { get; set; } = new(4f, 5f);
        public Vector2 offsetMax { get; set; } = new(-6f, -7f);
    }

    internal sealed class GameObject : Object
    {
        private readonly List<Component> _components = new();
        private bool _activeSelf = true;
        public static List<GameObject> All { get; } = new();
        public static List<GameObject> Instantiated { get; } = new();
        public static List<GameObject> Destroyed { get; } = new();
        public static bool ReverseComponentEnumeration { get; set; }
        public static bool DegradeTypedImageQuery { get; set; }
        public static int LogicalUnitProxyQueryCount { get; set; }
        public static CloneFaultMode NextCloneFault { get; set; }

        public GameObject()
        {
            transform = new RectTransform(this);
            Attach(transform);
            All.Add(this);
        }

        public GameObject(
            string objectName,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Type> componentTypes)
        {
            name = objectName;
            transform = new RectTransform(this);
            Attach(transform);
            foreach (var componentType in componentTypes)
            {
                var type = componentType.ManagedType;
                if (type == typeof(RectTransform)) continue;
                if (Activator.CreateInstance(type) is not Component component)
                {
                    throw new InvalidOperationException($"Cannot create component {type.FullName}.");
                }
                Attach(component);
            }
            All.Add(this);
        }

        public RectTransform transform { get; }
        public bool activeSelf => _activeSelf;
        public bool activeInHierarchy =>
            _activeSelf && (transform.parent?.gameObject.activeInHierarchy ?? true);
        public int SetActiveCalls { get; private set; }
        public void SetActive(bool active)
        {
            SetActiveCalls++;
            _activeSelf = active;
        }

        public void Attach(Component component)
        {
            component.Rebind(this);
            if (!_components.Contains(component)) _components.Add(component);
        }

        public void Detach(Component component) => _components.Remove(component);

        public Component? GetComponent(Type type) => _components.SingleOrDefault(type.IsInstanceOfType);
        public Component? GetComponent(Il2CppSystem.Type type)
        {
            var exact = GetComponent(type.ManagedType);
            if (type.ManagedType
                    == typeof(DEYU.AdpUISystem.LogicalCollection.UILogicalUnit)
                && exact != null)
            {
                var nativeType = Object.GetNativeType(exact.Pointer);
                var proxy = new Component(exact.Pointer);
                proxy.Rebind(this);
                if (nativeType != null) proxy.OverrideNativeType(nativeType);
                LogicalUnitProxyQueryCount++;
                return proxy;
            }
            if (!DegradeTypedImageQuery
                || type.ManagedType != typeof(UnityEngine.UI.Image)
                || exact == null)
            {
                return exact;
            }

            var degraded = new Component(exact.Pointer);
            degraded.Rebind(this);
            degraded.OverrideNativeType(type.ManagedType);
            return degraded;
        }
        public T[] GetComponents<T>() where T : Component
        {
            var result = _components.OfType<T>().ToArray();
            if (ReverseComponentEnumeration) Array.Reverse(result);
            return result;
        }

        internal GameObject DeepClone(Transform parent)
        {
            var clone = new GameObject { name = name };
            clone.transform.parent = parent;
            clone.transform.rect = transform.rect;
            clone.transform.anchorMin = transform.anchorMin;
            clone.transform.anchorMax = transform.anchorMax;
            clone.transform.pivot = transform.pivot;
            clone.transform.anchoredPosition = transform.anchoredPosition;
            clone.transform.sizeDelta = transform.sizeDelta;
            clone.transform.offsetMin = transform.offsetMin;
            clone.transform.offsetMax = transform.offsetMax;
            clone.transform.localScale = transform.localScale;
            clone.SetActive(activeSelf);

            foreach (var component in _components)
            {
                switch (component)
                {
                    case RectTransform:
                        break;
                    case CanvasRenderer:
                        clone.Attach(new CanvasRenderer());
                        break;
                    case UnityEngine.UI.Image image:
                        clone.Attach(new UnityEngine.UI.Image
                        {
                            color = image.color,
                            raycastTarget = image.raycastTarget,
                            enabled = image.enabled,
                            sprite = image.sprite,
                            material = image.material,
                            type = image.type,
                        });
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported cloned component {component.GetType().FullName}.");
                }
            }

            var fault = NextCloneFault;
            NextCloneFault = CloneFaultMode.None;
            var clonedImage = clone.GetComponent(typeof(UnityEngine.UI.Image))
                as UnityEngine.UI.Image;
            switch (fault)
            {
                case CloneFaultMode.None:
                    break;
                case CloneFaultMode.RemoveImage:
                    if (clonedImage != null)
                    {
                        clone.Detach(clonedImage);
                        clonedImage.InvalidateNativePointer();
                    }
                    break;
                case CloneFaultMode.AddUnknownComponent:
                    clone.Attach(new Component());
                    break;
                case CloneFaultMode.InvalidateImage:
                    clonedImage?.InvalidateNativePointer();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
            }

            return clone;
        }

        internal void DestroyRecursively()
        {
            foreach (var child in transform.Children.ToArray()) child.gameObject.DestroyRecursively();
            transform.parent = null;
            SetActive(false);
            foreach (var component in _components) component.InvalidateNativePointer();
            InvalidateNativePointer();
        }
    }

    internal enum CloneFaultMode
    {
        None,
        RemoveImage,
        AddUnknownComponent,
        InvalidateImage,
    }

    internal sealed class CanvasRenderer : Component { }
    internal sealed class Sprite : Object { }
    internal sealed class Shader : Object { }
    internal sealed class Material : Object
    {
        public Shader? shader { get; set; }
    }
    internal sealed class Canvas : Component { }
    internal sealed class CanvasGroup : Component
    {
        public float alpha { get; set; } = 1f;
        public bool interactable { get; set; } = true;
        public bool blocksRaycasts { get; set; } = true;
    }

    internal static class Time
    {
        public static float realtimeSinceStartup { get; set; }
    }
}

namespace UnityEngine.UI
{
    internal class Image : UnityEngine.Component
    {
        internal enum Type
        {
            Simple = 0,
            Sliced = 1,
            Tiled = 2,
            Filled = 3,
        }

        public Image() { }
        internal Image(IntPtr sharedPointer) : base(sharedPointer) { }
        private UnityEngine.Color _color = new(1f, 1f, 1f, 1f);
        public int ColorWriteCount { get; private set; }
        public UnityEngine.Color color
        {
            get => _color;
            set
            {
                ColorWriteCount++;
                _color = value;
            }
        }
        public bool raycastTarget { get; set; } = true;
        public bool enabled { get; set; } = true;
        public UnityEngine.Sprite? sprite { get; set; }
        public UnityEngine.Material? material { get; set; }
        public Type type { get; set; } = Type.Simple;
    }
    internal sealed class DerivedImage : Image { }
    internal sealed class Mask : UnityEngine.Component { }
    internal abstract class LayoutGroup : UnityEngine.Component { }
    internal sealed class VerticalLayoutGroup : LayoutGroup { }
}

namespace UnityEngine.Events
{
    internal class UnityEventBase
    {
        private PersistentCallGroup _persistentCalls = new();
        public bool ThrowOnPersistentCallsRead { get; set; }
        public PersistentCallGroup m_PersistentCalls
        {
            get
            {
                if (ThrowOnPersistentCallsRead)
                {
                    throw new InvalidOperationException(
                        "Injected UnityEventBase.m_PersistentCalls field failure.");
                }
                return _persistentCalls;
            }
            set => _persistentCalls = value;
        }
    }

    internal class UnityEvent<T> : UnityEventBase { }

    internal sealed class PersistentCallGroup
    {
        private Il2CppSystem.Collections.Generic.List<PersistentCall> _calls = new();
        public bool ThrowOnCallsRead { get; set; }
        public Il2CppSystem.Collections.Generic.List<PersistentCall> m_Calls
        {
            get
            {
                if (ThrowOnCallsRead)
                {
                    throw new InvalidOperationException(
                        "Injected PersistentCallGroup.m_Calls field failure.");
                }
                return _calls;
            }
            set => _calls = value;
        }
    }

    internal sealed class PersistentCall
    {
        private UnityEngine.Object? _target;
        private string? _methodName;
        public bool ThrowOnTargetRead { get; set; }
        public bool ThrowOnMethodNameRead { get; set; }
        public UnityEngine.Object? m_Target
        {
            get
            {
                if (ThrowOnTargetRead)
                {
                    throw new InvalidOperationException(
                        "Injected PersistentCall.m_Target field failure.");
                }
                return _target;
            }
            set => _target = value;
        }
        public string? m_MethodName
        {
            get
            {
                if (ThrowOnMethodNameRead)
                {
                    throw new InvalidOperationException(
                        "Injected PersistentCall.m_MethodName field failure.");
                }
                return _methodName;
            }
            set => _methodName = value;
        }
    }
}

namespace Il2CppSystem
{
    internal sealed class Type
    {
        public Type(System.Type managedType) => ManagedType = managedType;
        public System.Type ManagedType { get; }
    }

    internal readonly struct ValueTuple<T1, T2, T3>
    {
        public ValueTuple(T1 item1, T2 item2, T3 item3)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
        }

        public T1 Item1 { get; }
        public T2 Item2 { get; }
        public T3 Item3 { get; }
    }
}

namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    internal sealed class Il2CppReferenceArray<T> : IEnumerable<T>
    {
        private readonly T[] _values;
        public Il2CppReferenceArray(int length) => _values = new T[length];
        public T this[int index]
        {
            get => _values[index];
            set => _values[index] = value;
        }
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_values).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

namespace Il2CppSystem.Collections.Generic
{
    internal sealed class Dictionary<TKey, TValue> where TKey : notnull
    {
        private readonly System.Collections.Generic.Dictionary<TKey, TValue> _values = new();
        public int Count => _values.Count;
        public bool ContainsKey(TKey key) => _values.ContainsKey(key);
        public TValue get_Item(TKey key) => _values[key];
        public void Add(TKey key, TValue value) => _values.Add(key, value);
        public void Clear() => _values.Clear();
        public bool Remove(TKey key) => _values.Remove(key);
    }

    internal sealed class List<T>
    {
        private readonly System.Collections.Generic.List<T> _values = new();
        private int _countReads;
        public bool ThrowOnCountRead { get; set; }
        public bool ThrowOnItemRead { get; set; }
        public int DriftOnCountReadNumber { get; set; }
        public int Count
        {
            get
            {
                _countReads++;
                if (ThrowOnCountRead)
                {
                    throw new InvalidOperationException("Injected List<PersistentCall>.Count failure.");
                }
                return _countReads == DriftOnCountReadNumber
                    ? _values.Count + 1
                    : _values.Count;
            }
        }
        public T get_Item(int index)
        {
            if (ThrowOnItemRead)
            {
                throw new InvalidOperationException("Injected List<PersistentCall>.get_Item failure.");
            }
            return _values[index];
        }
        public void Add(T value) => _values.Add(value);
        public void Clear()
        {
            _values.Clear();
            _countReads = 0;
        }
        public bool Remove(T value) => _values.Remove(value);
    }
}

namespace NightScene.GuestManagementUtility
{
    internal static class GuestsManager
    {
        internal class OrderBase : UnityEngine.Object
        {
            public OrderBase(int deskCode) => DeskCode = deskCode;
            public int DeskCode { get; }
            public NormalOrder? NormalConversion { get; set; }
            public SpecialOrder? SpecialConversion { get; set; }
        }

        internal sealed class NormalOrder : OrderBase
        {
            public NormalOrder(int deskCode) : base(deskCode) { }
        }

        internal sealed class SpecialOrder : OrderBase
        {
            public SpecialOrder(int deskCode) : base(deskCode) { }
        }
    }

    internal sealed class GuestGroupController : UnityEngine.Object
    {
        public GuestGroupController(int deskCode) => DeskCode = deskCode;
        public int DeskCode { get; set; }
    }
}

namespace DEYU.Collections
{
    internal sealed class AlignedList<TKey, TData> where TKey : notnull
    {
        private readonly System.Collections.Generic.Dictionary<TKey, TData> _values = new();
        public bool TryGetValue(TKey key, out TData value) => _values.TryGetValue(key, out value!);
        public void Add(TKey key, TData value) => _values.Add(key, value);
        public void Clear() => _values.Clear();
        public bool Remove(TKey key) => _values.Remove(key);
    }
}

namespace DEYU.AdpUISystem.LogicalCollection
{
    internal class UILogicalUnit : UnityEngine.Component
    {
        public UILogicalUnit() { }
        internal UILogicalUnit(IntPtr sharedPointer) : base(sharedPointer) { }
        public UnityEngine.RectTransform RectTransform => gameObject.transform;
        public DEYU.AdpUISystem.Utils.AdpUISystemUtils.UnityEvent_Bool
            m_OnSelectionUpdateCallback { get; set; } = new();
        public int LogicalState { get; set; }
    }

    internal sealed class UIButtonSimple : UILogicalUnit
    {
        public UIButtonSimple() { }
        internal UIButtonSimple(IntPtr sharedPointer) : base(sharedPointer) { }
    }

    internal sealed class BareUnityEventLogicalUnitShape
    {
        public UnityEngine.Events.UnityEvent<bool> m_OnSelectionUpdateCallback { get; set; } = new();
    }

    internal sealed class UnrelatedUnityEventLogicalUnitShape
    {
        public UnrelatedUnityEventBool m_OnSelectionUpdateCallback { get; set; } = new();
    }

    internal sealed class UnrelatedUnityEventBool : UnityEngine.Events.UnityEvent<bool> { }
}

namespace DEYU.AdpUISystem.Utils
{
    using DEYU.AdpUISystem.LogicalCollection;
    using DEYU.Collections;

    internal static class AdpUISystemUtils
    {
        internal sealed class UnityEvent_Bool : UnityEngine.Events.UnityEvent<bool>
        {
            public void SetPersistentCalls(
                params (UnityEngine.Object? Target, string? MethodName)[] calls)
            {
                var targetCalls = m_PersistentCalls.m_Calls;
                targetCalls.Clear();
                foreach (var call in calls)
                {
                    targetCalls.Add(new UnityEngine.Events.PersistentCall
                    {
                        m_Target = call.Target,
                        m_MethodName = call.MethodName,
                    });
                }
            }
        }
    }

    internal sealed class UILogicalGroupT<T> : UnityEngine.Object where T : notnull
    {
        public AlignedList<T, UILogicalUnit> m_Children { get; set; } = new();
        public void AddChild(UILogicalUnit unit, T key) => m_Children.Add(key, unit);
        public void CleanChildren() => m_Children.Clear();
    }
}

namespace NightScene.UI.HUDUtility
{
    using DEYU.AdpUISystem.LogicalCollection;
    using DEYU.AdpUISystem.Utils;
    using Il2CppSystem;
    using Il2CppSystem.Collections.Generic;
    using NightScene.GuestManagementUtility;
    using UnityEngine;

    internal sealed record ThrowDeliverCardSpec(
        int DeskCode,
        GuestsManager.OrderBase Order,
        GuestGroupController Controller,
        Vector3 WorldPosition);

    internal sealed record ThrowDeliverButtonVisuals(
        GameObject Button,
        UnityEngine.UI.Image? ValidBackground,
        UnityEngine.UI.Image? InvalidBackground,
        UnityEngine.UI.Image? SelectionOutline,
        GameObject Content);

    internal class WorkSceneThrowDeliverPanel : Component
    {
        private readonly System.Collections.Generic.Dictionary<int, GameObject> _builtButtons = new();
        private readonly System.Collections.Generic.Dictionary<int, ThrowDeliverButtonVisuals>
            _builtVisuals = new();

        public WorkSceneThrowDeliverPanel()
        {
            var fieldOwner = new GameObject { name = "ThrowDeliverButtonField" };
            fieldOwner.transform.parent = transform;
            m_BtnField = fieldOwner.transform;
        }

        public Dictionary<int, ValueTuple<Vector3, GuestsManager.OrderBase, GuestGroupController>> m_Data { get; set; } = new();
        public List<GameObject> m_BtnInstances { get; set; } = new();
        public UILogicalGroupT<int> m_Group { get; set; } = new();
        public RectTransform m_BtnField { get; set; }

        public System.Collections.Generic.List<ThrowDeliverCardSpec> Entries { get; } = new();
        public System.Collections.Generic.List<int> PoolDeskOrder { get; } = new();
        public bool IncludeImages { get; set; } = true;
        public bool UseValidBackground { get; set; } = true;
        public System.Action? BeforeOriginalClose { get; set; }
        public int OriginalCloseCalls { get; private set; }
        public IReadOnlyDictionary<int, GameObject> BuiltButtons => _builtButtons;
        public IReadOnlyDictionary<int, ThrowDeliverButtonVisuals> BuiltVisuals => _builtVisuals;

        public virtual void OnPanelOpen()
        {
            m_Data.Clear();
            m_BtnInstances.Clear();
            m_Group.CleanChildren();
            _builtButtons.Clear();
            _builtVisuals.Clear();

            foreach (var entry in Entries)
            {
                m_Data.Add(
                    entry.DeskCode,
                    new ValueTuple<Vector3, GuestsManager.OrderBase, GuestGroupController>(
                        entry.WorldPosition,
                        entry.Order,
                        entry.Controller));
                var button = new GameObject { name = $"Desk-{entry.DeskCode}-Button" };
                button.transform.parent = m_BtnField;
                button.transform.rect = new Rect(-58f, -42f, 116f, 84f);
                button.Attach(new CanvasRenderer());
                if (IncludeImages)
                {
                    button.Attach(new UnityEngine.UI.Image
                    {
                        color = new Color(0.8f, 0.7f, 0.6f, 0.9f),
                        raycastTarget = true,
                        enabled = true,
                    });
                }
                button.Attach(new CanvasGroup
                {
                    alpha = 0.73f,
                    interactable = true,
                    blocksRaycasts = true,
                });
                var unit = new UILogicalUnit { LogicalState = entry.DeskCode + 1000 };
                unit.OverrideNativeType(typeof(UIButtonSimple));
                button.Attach(unit);
                var sharedBackgroundMaterial = new Material
                {
                    name = $"SharedBackgroundMaterial-{entry.DeskCode}",
                    shader = new Shader { name = "UI/Default" },
                };
                _ = CreateFullStretchLeaf(
                    button,
                    $"Desk-{entry.DeskCode}-Valid",
                    $"ValidSprite-{entry.DeskCode}",
                    sharedBackgroundMaterial,
                    enabled: UseValidBackground,
                    new Color(0.92f, 0.84f, 0.66f, 1f),
                    out var validBackground);
                _ = CreateFullStretchLeaf(
                    button,
                    $"Desk-{entry.DeskCode}-Invalid",
                    $"InvalidSprite-{entry.DeskCode}",
                    sharedBackgroundMaterial,
                    enabled: !UseValidBackground,
                    new Color(0.63f, 0.58f, 0.52f, 1f),
                    out var invalidBackground);
                _ = CreateFullStretchLeaf(
                    button,
                    $"Desk-{entry.DeskCode}-Selection",
                    $"SelectionSprite-{entry.DeskCode}",
                    new Material
                    {
                        name = $"SelectionMaterial-{entry.DeskCode}",
                        shader = new Shader { name = "UI/Default" },
                    },
                    enabled: false,
                    new Color(1f, 1f, 1f, 1f),
                    out var selectionOutline);
                var nativeContent = new GameObject
                {
                    name = $"Desk-{entry.DeskCode}-NativeContent",
                };
                nativeContent.transform.parent = button.transform;
                var nativeIcon = new GameObject
                {
                    name = $"Desk-{entry.DeskCode}-NativeIcon",
                };
                nativeIcon.transform.parent = nativeContent.transform;
                nativeIcon.transform.rect = new Rect(-12f, -12f, 24f, 24f);
                nativeIcon.transform.anchorMin = new Vector2(0.5f, 0.5f);
                nativeIcon.transform.anchorMax = new Vector2(0.5f, 0.5f);
                nativeIcon.transform.pivot = new Vector2(0.5f, 0.5f);
                nativeIcon.transform.anchoredPosition = new Vector2(18f, -4f);
                nativeIcon.transform.sizeDelta = new Vector2(24f, 24f);
                nativeIcon.transform.offsetMin = new Vector2(6f, 7f);
                nativeIcon.transform.offsetMax = new Vector2(30f, 31f);
                if (IncludeImages)
                {
                    nativeIcon.Attach(new CanvasRenderer());
                    nativeIcon.Attach(new UnityEngine.UI.Image
                    {
                        color = new Color(1f, 1f, 1f, 0.75f),
                        raycastTarget = true,
                        enabled = true,
                        sprite = new Sprite { name = $"OrderIcon-{entry.DeskCode}" },
                        material = null,
                    });
                    if (selectionOutline == null)
                    {
                        throw new InvalidOperationException(
                            "The exact selection fixture Image was not created.");
                    }
                    unit.m_OnSelectionUpdateCallback.SetPersistentCalls(
                        (selectionOutline, "set_enabled"));
                }
                m_Group.AddChild(unit, entry.DeskCode);
                _builtButtons.Add(entry.DeskCode, button);
                _builtVisuals.Add(
                    entry.DeskCode,
                    new ThrowDeliverButtonVisuals(
                        button,
                        validBackground,
                        invalidBackground,
                        selectionOutline,
                        nativeContent));
            }

            var order = PoolDeskOrder.Count == 0
                ? Entries.Select(entry => entry.DeskCode)
                : PoolDeskOrder;
            foreach (var deskCode in order)
            {
                m_BtnInstances.Add(_builtButtons[deskCode]);
            }
        }

        private GameObject CreateFullStretchLeaf(
            GameObject button,
            string objectName,
            string spriteName,
            Material material,
            bool enabled,
            Color color,
            out UnityEngine.UI.Image? image)
        {
            var owner = new GameObject { name = objectName };
            owner.transform.parent = button.transform;
            owner.transform.rect = new Rect(-58f, -42f, 116f, 84f);
            owner.transform.anchorMin = new Vector2(0f, 0f);
            owner.transform.anchorMax = new Vector2(1f, 1f);
            owner.transform.pivot = new Vector2(0.5f, 0.5f);
            owner.transform.anchoredPosition = new Vector2(0f, 0f);
            owner.transform.sizeDelta = new Vector2(0f, 0f);
            owner.transform.offsetMin = new Vector2(0f, 0f);
            owner.transform.offsetMax = new Vector2(0f, 0f);
            image = null;
            if (!IncludeImages) return owner;

            owner.Attach(new CanvasRenderer());
            image = new UnityEngine.UI.Image
            {
                color = color,
                raycastTarget = false,
                enabled = enabled,
                sprite = new Sprite { name = spriteName },
                material = material,
                type = UnityEngine.UI.Image.Type.Sliced,
            };
            owner.Attach(image);
            return owner;
        }

        public virtual void OnPanelClose()
        {
            BeforeOriginalClose?.Invoke();
            OriginalCloseCalls++;
        }
    }
}

namespace MystiaStewardCompanion.Core
{
    internal sealed class NightBusinessOrder
    {
        public DateTime? FirstSeenAtUtc { get; init; }
        public long OrderLifecycleSequence { get; init; } = -1;
        public int DeskCode { get; init; }
        public int? RuntimeGuestId { get; init; }
        public int? FoodTagId { get; init; }
        public int? BeverageTagId { get; init; }
        public bool IsFreeOrder { get; init; }
    }

    internal sealed class NormalBusinessOrder
    {
        public string OrderKey { get; init; } = "";
        public long OrderLifecycleSequence { get; init; } = -1;
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

    internal readonly record struct NightBusinessLifecycleSnapshot(
        bool IsActive,
        long Generation,
        int ThreadId);

    internal static class RuntimeNightBusinessLifecycle
    {
        public static NightBusinessLifecycleSnapshot Snapshot { get; set; }
        public static bool IsActive => Snapshot.IsActive;
        public static long Generation => Snapshot.Generation;
    }

    internal sealed record CapturedRuntimeSpecialOrder(
        int DeskCode,
        int? GuestId,
        string GuestName,
        int FoodTagId,
        int BeverageTagId,
        bool IsFreeOrder,
        bool IsFulfilled,
        DateTime FirstCapturedAt,
        DateTime CapturedAt,
        string RuntimeKey,
        string CaptureSource)
    {
        internal object? OrderObject { get; init; }
        internal object? ControllerObject { get; init; }
        internal long OrderLifecycleSequence { get; init; }
    }

    internal static class SpecialOrderRuntimeCapture
    {
        public static List<CapturedRuntimeSpecialOrder> Captures { get; } = new();
        public static IReadOnlyList<CapturedRuntimeSpecialOrder> Snapshot(TimeSpan maxAge) =>
            Captures.ToArray();
    }

    internal sealed record CapturedRuntimeNormalOrder(
        string RuntimeKey,
        int DeskCode,
        DateTime FirstCapturedAt,
        DateTime CapturedAt)
    {
        internal object? OrderObject { get; init; }
        internal object? ControllerObject { get; init; }
        internal long OrderLifecycleSequence { get; init; }
    }

    internal static class NormalOrderRuntimeCapture
    {
        public static List<CapturedRuntimeNormalOrder> Captures { get; } = new();
        public static IReadOnlyList<CapturedRuntimeNormalOrder> Snapshot(TimeSpan maxAge) => Captures.ToArray();
    }

    internal static class RuntimeReflectionUtility
    {
        public static bool ForceDualOrderConversion { get; set; }
        public static int LogicalUnitProxyCastCount { get; set; }
        public static DEYU.AdpUISystem.LogicalCollection.UILogicalUnit?
            LogicalUnitProxyCastOverride { get; set; }

        public static Type? FindType(string fullName) => fullName switch
        {
            "NightScene.UI.HUDUtility.WorkSceneThrowDeliverPanel" =>
                typeof(NightScene.UI.HUDUtility.WorkSceneThrowDeliverPanel),
            "NightScene.GuestManagementUtility.GuestsManager+OrderBase" =>
                typeof(GuestsManager.OrderBase),
            "NightScene.GuestManagementUtility.GuestsManager+NormalOrder" =>
                typeof(GuestsManager.NormalOrder),
            "NightScene.GuestManagementUtility.GuestsManager+SpecialOrder" =>
                typeof(GuestsManager.SpecialOrder),
            "UnityEngine.RectTransform" => typeof(UnityEngine.RectTransform),
            "UnityEngine.CanvasRenderer" => typeof(UnityEngine.CanvasRenderer),
            "UnityEngine.UI.Image" => typeof(UnityEngine.UI.Image),
            "UnityEngine.UI.LayoutGroup" => typeof(UnityEngine.UI.LayoutGroup),
            "DEYU.AdpUISystem.LogicalCollection.UILogicalUnit" =>
                typeof(DEYU.AdpUISystem.LogicalCollection.UILogicalUnit),
            _ => null,
        };

        public static bool TryReadNativeObjectPointer(object? target, out nint pointer)
        {
            if (target is not UnityEngine.Object unityObject
                || unityObject.m_CachedPtr == IntPtr.Zero)
            {
                pointer = 0;
                return false;
            }

            pointer = unityObject.m_CachedPtr;
            return pointer != 0;
        }

        public static object? TryCastRuntimeObject(object? value, string targetTypeName)
        {
            var targetType = FindType(targetTypeName);
            if (targetType?.IsInstanceOfType(value) == true) return value;
            if (value is UnityEngine.Component component
                && targetType != null
                && UnityEngine.Object.GetNativeType(component.Pointer) is Type nativeType
                && targetType.IsAssignableFrom(nativeType))
            {
                var result = targetType
                        == typeof(DEYU.AdpUISystem.LogicalCollection.UILogicalUnit)
                    && LogicalUnitProxyCastOverride is { } castOverride
                    && castOverride.Pointer == component.Pointer
                        ? castOverride
                        : component.gameObject
                            .GetComponents<UnityEngine.Component>()
                            .SingleOrDefault(candidate =>
                                targetType.IsInstanceOfType(candidate)
                                && candidate.Pointer == component.Pointer);
                if (targetType
                        == typeof(DEYU.AdpUISystem.LogicalCollection.UILogicalUnit)
                    && result != null)
                {
                    LogicalUnitProxyCastCount++;
                }
                return result;
            }
            if (value is not GuestsManager.OrderBase order
                || value.GetType() != typeof(GuestsManager.OrderBase))
            {
                return null;
            }

            if (ForceDualOrderConversion)
            {
                return targetTypeName == RuntimeOrderTypeResolver.NormalOrderTypeName
                    ? order.NormalConversion ?? new GuestsManager.NormalOrder(order.DeskCode)
                    : order.SpecialConversion ?? new GuestsManager.SpecialOrder(order.DeskCode);
            }

            return targetTypeName == RuntimeOrderTypeResolver.NormalOrderTypeName
                ? order.NormalConversion
                : targetTypeName == RuntimeOrderTypeResolver.SpecialOrderTypeName
                    ? order.SpecialConversion
                    : null;
        }
    }
}
