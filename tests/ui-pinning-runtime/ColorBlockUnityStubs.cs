using System.Reflection;

// 本文件只提供正式 UI 背景使用的对象树、完整顶点通道及生命周期模型。不会模拟 Canvas/GPU，
// 也不伪造 Harmony 注入；直接回调模式明确单独标记，完整 Harmony 测试入口仍保持原样。
namespace UnityEngine
{
    internal class Object
    {
        private static long _nextPointer = 200000;
        internal static readonly List<Object> All = new();
        public IntPtr m_CachedPtr { get; } = new(Interlocked.Increment(ref _nextPointer));
        public IntPtr Pointer => m_CachedPtr;
        public string name = "";
        public bool Destroyed;
        protected Object() => All.Add(this);
        public static void Destroy(Object value)
        {
            if (value.Destroyed) return;
            value.Destroyed = true;
            if (value is GameObject game)
            {
                foreach (var child in game.transform.Children.ToArray()) Destroy(child.gameObject);
                foreach (var component in game.Components) component.Destroyed = true;
                game.transform.Destroyed = true;
            }
        }
        public static bool operator ==(Object? left, Object? right) => ReferenceEquals(left, right)
            || (ReferenceEquals(left, null) || left.Destroyed) && (ReferenceEquals(right, null) || right.Destroyed);
        public static bool operator !=(Object? left, Object? right) => !(left == right);
        public override bool Equals(object? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => m_CachedPtr.GetHashCode();
    }
    internal class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
    }
    internal class Transform : Component
    {
        internal readonly List<Transform> Children = new();
        public Transform? parent;
        public Vector3 localScale = Vector3.one;
        public Quaternion localRotation = Quaternion.identity;
        public void SetParent(Transform? owner, bool worldPositionStays)
        { parent?.Children.Remove(this); parent = owner; owner?.Children.Add(this); }
        public bool IsChildOf(Transform ancestor)
        {
            for (Transform? current = this; current != null; current = current.parent)
                if (ReferenceEquals(current, ancestor)) return true;
            return false;
        }
        public int GetSiblingIndex() => parent?.Children.IndexOf(this) ?? 0;
        public void SetAsFirstSibling()
        { if (parent != null) { parent.Children.Remove(this); parent.Children.Insert(0, this); } }
    }
    internal sealed class RectTransform : Transform
    { public Vector2 anchorMin = Vector2.zero, anchorMax = Vector2.zero, offsetMin = Vector2.zero, offsetMax = Vector2.zero; }
    internal sealed class GameObject : Object
    {
        public readonly List<Component> Components = new();
        public readonly RectTransform transform;
        public bool activeSelf = true;
        public bool activeInHierarchy => !Destroyed && activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
        public int layer = 0;
        public GameObject(string value) { name = value; transform = new RectTransform { gameObject = this }; Components.Add(transform); }
        public GameObject(string value, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Type> types) : this(value)
        {
            for (var index = 0; index < types.Length; index++)
                if (types[index].Managed != typeof(RectTransform)) Add((Component)Activator.CreateInstance(types[index].Managed)!);
        }
        public T Add<T>(T component) where T : Component { component.gameObject = this; Components.Add(component); return component; }
        public Component? GetComponent(Il2CppSystem.Type type) => Components.SingleOrDefault(value => type.Managed.IsInstanceOfType(value));
        public void SetActive(bool active) => activeSelf = active;
    }
    internal sealed class CanvasGroup : Component
    {
        public float alpha { get; set; } = 1;
        public bool blocksRaycasts { get; set; } = true;
        public bool interactable { get; set; } = true;
    }
    internal sealed class Sprite : Object { }
    internal readonly record struct Vector2(float x, float y) { public static Vector2 zero => new(0, 0); }
    internal readonly record struct Vector3(float x, float y, float z) { public static Vector3 one => new(1, 1, 1); }
    internal readonly record struct Vector4(float x, float y, float z, float w);
    internal readonly record struct Color32(byte r, byte g, byte b, byte a);
    internal readonly record struct Quaternion(float x, float y, float z, float w) { public static Quaternion identity => new(0, 0, 0, 1); }
}
namespace UnityEngine.UI
{
    internal class Selectable : Component { public Image image { get; set; } = null!; }
    internal sealed class LayoutElement : Component { public bool ignoreLayout { get; set; } }
    internal class Image : Component
    {
        private Color _color = new(1, 1, 1, 1);
        internal bool VerticesDirty = true;
        internal int DirtyCount;
        internal VertexHelper Mesh { get; } = new();
        public bool enabled { get; set; } = true;
        public bool raycastTarget { get; set; } = true;
        public bool maskable { get; set; }
        public Sprite? sprite { get; set; }
        public RectTransform rectTransform => (RectTransform)transform;
        public Color color { get => ReadColor(); set => WriteColor(value); }
        protected virtual Color ReadColor() => _color;
        protected virtual void WriteColor(Color value)
        {
            _color = value;
            SetVerticesDirty();
        }
        public void SetVerticesDirty() { VerticesDirty = true; DirtyCount++; }

        /// <summary>
        /// 仅提供原生背景输入的四角网格；正式生产代码随后通过 VertexHelper 修改同一个网格。
        /// 此方法不代替 Unity 的图集采样、遮罩或 GPU，只令生命周期测试能观察顶点变化。
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public virtual void OnPopulateMesh(VertexHelper toFill)
        {
            VertexHelper.NativeGenerationDepth++;
            try
            {
                toFill.Clear();
                var source = VertexHelper.ToBytes(color);
                foreach (var point in new[] { new Vector3(-1, -.5f, 0), new Vector3(1, -.5f, 0),
                    new Vector3(1, .5f, 0), new Vector3(-1, .5f, 0) })
                {
                    var uv = new Vector4((point.x + 1) * .5f, point.y + .5f, .2f, .3f);
                    toFill.AddVert(point, source, uv, uv, uv, uv, new Vector3(0, 0, -1), new Vector4(1, 0, 0, -1));
                }
                toFill.AddTriangle(0, 1, 2); toFill.AddTriangle(0, 2, 3);
            }
            finally { VertexHelper.NativeGenerationDepth--; }
        }

        internal VertexHelper RebuildIfDirty()
        {
            if (!VerticesDirty) return Mesh;
            VerticesDirty = false;
            OnPopulateMesh(Mesh);
            MystiaStewardCompanion.Save.ListColorBlockProbe.Invoke("AfterNativeBackgroundMesh", this, Mesh);
            return Mesh;
        }
    }

    /// <summary>准确保留 #783 VertexHelper 字段名和公开签名，记录真实服务写入以测试代次竞争。</summary>
    internal sealed class VertexHelper
    {
        public Il2CppSystem.Collections.Generic.List<Vector3> m_Positions = new();
        public Il2CppSystem.Collections.Generic.List<Color32> m_Colors = new();
        public Il2CppSystem.Collections.Generic.List<Vector4> m_Uv0S = new(), m_Uv1S = new(), m_Uv2S = new(), m_Uv3S = new();
        public Il2CppSystem.Collections.Generic.List<Vector3> m_Normals = new();
        public Il2CppSystem.Collections.Generic.List<Vector4> m_Tangents = new();
        public Il2CppSystem.Collections.Generic.List<int> m_Indices = new();
        public int currentVertCount => m_Positions.Count;
        public int currentIndexCount => m_Indices.Count;
        [ThreadStatic] internal static int NativeGenerationDepth;
        // 单次原生写入观察点：在已写入第一个顶点后触发，覆盖重入与写后复核异常。
        internal static Action? NextWriteAction;
        private static ManualResetEventSlim? _entered, _release;
        internal static bool WriteBarrierTimedOut;
        internal static readonly List<int> WriteThreadIds = new();
        public void Clear()
        { m_Positions.Clear(); m_Colors.Clear(); m_Uv0S.Clear(); m_Uv1S.Clear(); m_Uv2S.Clear(); m_Uv3S.Clear(); m_Normals.Clear(); m_Tangents.Clear(); m_Indices.Clear(); }
        public void AddVert(Vector3 position, Color32 color, Vector4 uv0, Vector4 uv1, Vector4 uv2,
            Vector4 uv3, Vector3 normal, Vector4 tangent)
        {
            m_Positions.Add(position); m_Colors.Add(color); m_Uv0S.Add(uv0); m_Uv1S.Add(uv1);
            m_Uv2S.Add(uv2); m_Uv3S.Add(uv3); m_Normals.Add(normal); m_Tangents.Add(tangent);
            if (NativeGenerationDepth > 0) return;
            WriteThreadIds.Add(Environment.CurrentManagedThreadId);
            Interlocked.Exchange(ref NextWriteAction, null)?.Invoke();
            var entered = Interlocked.Exchange(ref _entered, null);
            var release = Interlocked.Exchange(ref _release, null);
            if (entered == null || release == null) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            { WriteBarrierTimedOut = true; throw new TimeoutException("原生背景网格写入屏障超时。"); }
        }
        public void AddTriangle(int a, int b, int c) { m_Indices.Add(a); m_Indices.Add(b); m_Indices.Add(c); }
        internal static void BlockNextWrite(ManualResetEventSlim entered, ManualResetEventSlim release)
        { WriteBarrierTimedOut = false; _entered = entered; _release = release; }
        internal static Color32 ToBytes(Color value) => new(ToByte(value.r), ToByte(value.g), ToByte(value.b), ToByte(value.a));
        private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
    }
}
namespace Il2CppSystem { internal sealed record Type(System.Type Managed); }
namespace Il2CppSystem.Collections.Generic
{ internal sealed class List<T> : System.Collections.Generic.List<T> { } }
namespace Il2CppInterop.Runtime
{ internal static class Il2CppType { public static Il2CppSystem.Type From(Type type) => new(type); } }
namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    internal sealed class Il2CppReferenceArray<T>
    {
        private readonly T[] _values;
        public Il2CppReferenceArray(int count) => _values = new T[count];
        public int Length => _values.Length;
        public T this[int index] { get => _values[index]; set => _values[index] = value; }
    }
}
namespace MystiaStewardCompanion.Save
{
    internal sealed class VisualTextProbe
    { public UnityEngine.Sprite Visual { get; } = new(); }

    /// <summary>原生 cluster 已声明的图标引用；不靠名字或层级搜索为测试补出未知绑定。</summary>
    internal sealed class UiClusterProbe : UnityEngine.Component
    {
        internal readonly UnityEngine.UI.Image Icon;
        public UnityEngine.Transform[] allElements { get; }
        internal UiClusterProbe(UnityEngine.GameObject buttonObject)
        {
            buttonObject.Add(this);
            var iconObject = new UnityEngine.GameObject("NativeIcon");
            iconObject.transform.SetParent(buttonObject.transform, false);
            Icon = iconObject.Add(new UnityEngine.UI.Image());
            // cluster 可以同时声明文字等非 Image 成员；生产路径必须只精确匹配本次 sprite。
            var textObject = new UnityEngine.GameObject("NativeText");
            textObject.transform.SetParent(buttonObject.transform, false);
            allElements = new UnityEngine.Transform[] { textObject.transform, iconObject.transform };
        }
        internal void Bind(object data)
        {
            var text = RuntimeReflectionUtility.GetMemberValue(data, "Text");
            Icon.sprite = RuntimeReflectionUtility.GetMemberValue(text, "Visual") as UnityEngine.Sprite;
        }
    }

    internal sealed class DuplicateClusterProbe : UnityEngine.Component
    {
        public UnityEngine.Transform[] allElements { get; }
        internal DuplicateClusterProbe(UnityEngine.GameObject owner, UnityEngine.Transform icon)
        { owner.Add(this); allElements = new[] { icon, icon }; }
    }

    /// <summary>隔离模式直接调用已编译正式服务的回调；不声称验证了 Harmony 原生注入。</summary>
    internal static class ListColorBlockProbe
    {
        internal static bool DirectCallbacks;
        internal static int Assertions;
        internal static void Invoke(string method, params object[] arguments)
        {
            if (!DirectCallbacks) return;
            var callback = typeof(RuntimePinnedListHighlightService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(method);
            callback.Invoke(null, arguments);
        }
    }
}
