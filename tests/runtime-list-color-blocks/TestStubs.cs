using System.Collections;

// 可观测边界只实现正式代码调用的 Unity 属性，不模拟 GPU、真实 Mask 或原生输入系统。
namespace UnityEngine
{
    public class Object
    {
        public static readonly List<Object> Objects = new();
        public static int Created;
        public bool Destroyed;
        public string name = "";
        public nint Pointer { get; } = ++Created;
        protected Object() => Objects.Add(this);
        public static void Destroy(Object value)
        {
            if (value.Destroyed) return;
            value.Destroyed = true;
            if (value is GameObject owner)
            {
                foreach (var child in owner.transform.Children.ToArray()) Destroy(child.gameObject);
                foreach (var component in owner.Components) Destroy(component);
                owner.transform.parent?.Children.Remove(owner.transform);
            }
        }
        public static bool operator ==(Object? left, Object? right) =>
            (ReferenceEquals(left, null) || left.Destroyed) && (ReferenceEquals(right, null) || right.Destroyed)
            || ReferenceEquals(left, right);
        public static bool operator !=(Object? left, Object? right) => !(left == right);
        public override bool Equals(object? value) => ReferenceEquals(this, value);
        public override int GetHashCode() => Pointer.GetHashCode();
    }
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
    }
    public class Transform : Component
    {
        public readonly List<Transform> Children = new();
        public Transform? parent;
        public Vector3 localScale;
        public Quaternion localRotation;
        public void SetParent(Transform? next, bool worldPositionStays)
        { parent?.Children.Remove(this); parent = next; next?.Children.Add(this); }
        public bool IsChildOf(Transform ancestor)
        { for (Transform? item = this; item != null; item = item.parent) if (ReferenceEquals(item, ancestor)) return true; return false; }
        public void SetAsFirstSibling()
        { if (parent == null) return; parent.Children.Remove(this); parent.Children.Insert(0, this); }
        public int GetSiblingIndex() => parent?.Children.IndexOf(this) ?? 0;
    }
    public class RectTransform : Transform
    {
        public Vector2 anchorMin, anchorMax, offsetMin, offsetMax;
    }
    public class GameObject : Object
    {
        public bool activeSelf = true;
        public bool activeInHierarchy => !Destroyed && activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
        public int layer;
        public RectTransform transform;
        public readonly List<Component> Components = new();
        public GameObject(string value)
        { name = value; transform = new RectTransform { gameObject = this }; Components.Add(transform); }
        public GameObject(string value, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Type> types) : this(value)
        {
            for (var index = 0; index < types.Length; index++)
                if (types[index].Managed != typeof(RectTransform)) Add((Component)Activator.CreateInstance(types[index].Managed)!);
        }
        public T Add<T>(T component) where T : Component
        { component.gameObject = this; Components.Add(component); return component; }
        public Component? GetComponent(Il2CppSystem.Type type)
        {
            foreach (var component in Components) if (!component.Destroyed && type.Managed.IsInstanceOfType(component)) return component;
            return null;
        }
        public void SetActive(bool value) => activeSelf = value;
    }
    public class CanvasGroup : Component
    { public float alpha { get; set; } = 1f; public bool blocksRaycasts { get; set; } = true; public bool interactable { get; set; } = true; }
    public class Sprite : Object { }
    public readonly record struct Color(float r, float g, float b, float a);
    public readonly record struct Color32(byte r, byte g, byte b, byte a);
    public readonly record struct Vector2(float x, float y) { public static Vector2 zero => default; }
    public readonly record struct Vector3(float x, float y, float z) { public static Vector3 one => new(1, 1, 1); }
    public readonly record struct Vector4(float x, float y, float z, float w);
    public readonly record struct Quaternion { public static Quaternion identity => default; }
    public static class Mathf { public static float Sin(float value) => MathF.Sin(value); }
}
namespace UnityEngine.UI
{
    public class Image : UnityEngine.Component
    {
        public UnityEngine.RectTransform rectTransform => gameObject.transform;
        public UnityEngine.Color color { get; set; } = new(1, 1, 1, 1);
        public UnityEngine.Sprite? sprite { get; set; }
        public bool raycastTarget { get; set; } = true;
        public bool maskable { get; set; } = true;
        public bool enabled { get; set; } = true;
        public int DirtyCount;
        public void SetVerticesDirty() => DirtyCount++;
    }
    public class Selectable : UnityEngine.Component
    {
        public Image image { get; set; } = null!;
        public bool interactable = true;
        public Action? Submit;
    }
    public class LayoutElement : UnityEngine.Component { public bool ignoreLayout { get; set; } }
    // 仅提供生产网格边界会调用的真实字段和签名；不模拟Image生成、Canvas或GPU。
    public sealed class VertexHelper
    {
        public Il2CppSystem.Collections.Generic.List<UnityEngine.Vector3> m_Positions = new(), m_Normals = new();
        public Il2CppSystem.Collections.Generic.List<UnityEngine.Color32> m_Colors = new();
        public Il2CppSystem.Collections.Generic.List<UnityEngine.Vector4> m_Uv0S = new(), m_Uv1S = new(), m_Uv2S = new(), m_Uv3S = new(), m_Tangents = new();
        public Il2CppSystem.Collections.Generic.List<int> m_Indices = new();
        public int currentVertCount => m_Positions.Count;
        public int currentIndexCount => m_Indices.Count;
        public bool FailNextVertex;
        public void Clear()
        { m_Positions.Clear(); m_Normals.Clear(); m_Colors.Clear(); m_Uv0S.Clear(); m_Uv1S.Clear(); m_Uv2S.Clear(); m_Uv3S.Clear(); m_Tangents.Clear(); m_Indices.Clear(); }
        public void AddVert(UnityEngine.Vector3 position, UnityEngine.Color32 color, UnityEngine.Vector4 uv0,
            UnityEngine.Vector4 uv1, UnityEngine.Vector4 uv2, UnityEngine.Vector4 uv3, UnityEngine.Vector3 normal, UnityEngine.Vector4 tangent)
        {
            if (FailNextVertex) { FailNextVertex = false; throw new InvalidOperationException("synthetic write failure"); }
            m_Positions.Add(position); m_Colors.Add(color); m_Uv0S.Add(uv0); m_Uv1S.Add(uv1);
            m_Uv2S.Add(uv2); m_Uv3S.Add(uv3); m_Normals.Add(normal); m_Tangents.Add(tangent);
        }
        public void AddTriangle(int a, int b, int c) { m_Indices.Add(a); m_Indices.Add(b); m_Indices.Add(c); }
    }
}
namespace Il2CppSystem.Collections.Generic { public sealed class List<T> : System.Collections.Generic.List<T> { } }
namespace Il2CppSystem { public sealed record Type(System.Type Managed); }
namespace Il2CppInterop.Runtime
{ public static class Il2CppType { public static Il2CppSystem.Type From(Type type) => new(type); } }
namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public sealed class Il2CppReferenceArray<T>
    {
        private readonly T[] _items;
        public int Length => _items.Length;
        public Il2CppReferenceArray(int length) => _items = new T[length];
        public T this[int index] { get => _items[index]; set => _items[index] = value; }
    }
}
namespace MystiaStewardCompanion.Save
{
    internal static class RuntimeReflectionUtility
    {
        public static Type? FindType(string name) => typeof(RuntimeReflectionUtility).Assembly.GetType(name);
        public static object? TryCastRuntimeObject(object? value, string typeName) =>
            value != null && FindType(typeName)?.IsInstanceOfType(value) == true ? value : null;
        public static object? GetMemberValue(object value, string name) => value.GetType().GetField(name)?.GetValue(value);
        public static IEnumerable<object?> EnumerateObjects(object value)
        { foreach (var item in (IEnumerable)value) yield return item; }
    }
    internal static class RuntimeOrderTraceIdService
    {
        public static bool TryNormalizeTargetTraceId(RuntimeUiTargetKind kind, string trace, bool enabled,
            out string normalized, out string failure)
        { normalized = trace; failure = ""; return !string.IsNullOrEmpty(trace); }
    }
    internal sealed class ClusterProbe : UnityEngine.Component { public UnityEngine.Transform[] allElements = Array.Empty<UnityEngine.Transform>(); }
}
