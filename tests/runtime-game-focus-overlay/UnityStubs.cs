// 仅模拟本Mod拥有的UGUI节点与输入，不加载Unity/IL2CPP或真实游戏。
// 写入计数用于验证稳定帧不重建布局，不能当成GPU或游戏输入穿透实测证据。
namespace Il2CppSystem { public sealed record Type(System.Type Managed); }
namespace Il2CppInterop.Runtime
{
    internal static class Il2CppType { public static Il2CppSystem.Type From(System.Type type) => new(type); }
}
namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public sealed class Il2CppStructArray<T> where T : struct
    {
        private readonly T[] _items;
        public Il2CppStructArray(int count) => _items = new T[count];
        public T this[int index] { get => _items[index]; set => _items[index] = value; }
        public int Length => _items.Length;
    }
    public sealed class Il2CppReferenceArray<T>
    {
        private readonly T[] _items;
        public Il2CppReferenceArray(int count) => _items = new T[count];
        public T this[int index] { get => _items[index]; set => _items[index] = value; }
        public int Length => _items.Length;
    }
}
namespace MystiaStewardCompanion.Save
{
    internal static class RuntimeReflectionUtility
    {
        public static System.Type? FindType(string name) => typeof(RuntimeReflectionUtility).Assembly.GetType(name);
        public static object? TryCastRuntimeObject(object? value, string name) => value;
    }
}
namespace UnityEngine
{
    internal static class Probe
    {
        public static int Writes, Created, Destroyed, InputReads, Decoded, TextureCreated;
        public static int MainThread = Environment.CurrentManagedThreadId;
        public static readonly List<GameObject> Objects = new();
        public static bool RejectFont;
        public static void Reset() { Writes = Created = Destroyed = InputReads = Decoded = TextureCreated = 0; Objects.Clear(); RejectFont = false; }
        public static void MainOnly() { if (Environment.CurrentManagedThreadId != MainThread) throw new InvalidOperationException("后台访问Unity"); }
    }
    public class Object
    {
        public bool Destroyed;
        public HideFlags hideFlags;
        public static void Destroy(Object target) { Probe.MainOnly(); target.Destroyed = true; Probe.Destroyed++; }
        public static void DontDestroyOnLoad(Object target) { }
    }
    public class Component : Object { public GameObject gameObject = null!; }
    public class Transform : Component
    {
        public Transform? Parent;
        public void SetParent(Transform parent, bool worldPositionStays) { Parent = parent; Probe.Writes++; }
    }
    public sealed class RectTransform : Transform
    {
        private Vector2 _min, _max, _pivot, _position, _size;
        public Vector2 anchorMin { get => _min; set { _min = value; Probe.Writes++; } }
        public Vector2 anchorMax { get => _max; set { _max = value; Probe.Writes++; } }
        public Vector2 pivot { get => _pivot; set { _pivot = value; Probe.Writes++; } }
        public Vector2 anchoredPosition { get => _position; set { _position = value; Probe.Writes++; } }
        public Vector2 sizeDelta { get => _size; set { _size = value; Probe.Writes++; } }
    }
    public sealed class GameObject : Object
    {
        private readonly Dictionary<System.Type, Component> _components = new();
        public string name;
        public bool activeSelf = true;
        public Transform transform = null!;
        public GameObject(string value, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Type> types)
        {
            Probe.MainOnly();
            name = value; Probe.Created++; Probe.Objects.Add(this);
            for (var i = 0; i < types.Length; i++)
            {
                var type = types[i].Managed;
                var component = (Component)Activator.CreateInstance(type)!;
                component.gameObject = this;
                _components[type] = component;
                if (component is Transform current) transform = current;
            }
        }
        public Component GetComponent(Il2CppSystem.Type type) => _components[type.Managed];
        public void SetActive(bool active) { activeSelf = active; Probe.Writes++; }
        public T Get<T>() where T : Component => (T)_components[typeof(T)];
        public T? Maybe<T>() where T : Component => _components.TryGetValue(typeof(T), out var component) ? (T)component : null;
        public bool Active => activeSelf && !Destroyed && (transform.Parent == null || transform.Parent.gameObject.Active);
    }
    public readonly record struct Vector2(float x, float y);
    public readonly record struct Vector3(float x, float y, float z);
    public readonly record struct Vector4(float x, float y, float z, float w);
    public readonly record struct Rect(float x, float y, float width, float height);
    public readonly record struct Color(float r, float g, float b, float a);
    public readonly record struct Color32(byte r, byte g, byte b, byte a);
    public enum HideFlags { HideAndDontSave }
    public enum FilterMode { Point, Bilinear }
    public enum TextureWrapMode { Clamp }
    public enum TextureFormat { RGBA32 }
    public enum SpriteMeshType { FullRect }
    public class Texture : Object { }
    public sealed class Texture2D : Texture
    {
        public int width, height;
        public FilterMode filterMode;
        public TextureWrapMode wrapMode;
        public Color32[]? Pixels;
        public Texture2D(int width, int height, TextureFormat format, bool mipmap)
        { Probe.MainOnly(); this.width = width; this.height = height; Probe.TextureCreated++; }
        public void SetPixels32(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> pixels)
        { Pixels = Enumerable.Range(0, pixels.Length).Select(i => pixels[i]).ToArray(); }
        public void Apply(bool updateMipmaps, bool makeNoLongerReadable) { }
    }
    public sealed class Sprite : Object
    {
        public Texture2D texture = null!;
        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float ppu, uint extrude, SpriteMeshType mesh, Vector4 border, bool physics)
        { Probe.MainOnly(); return new() { texture = texture }; }
    }
    public static class ImageConversion
    {
        public static bool LoadImage(Texture2D target, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> data, bool markNonReadable)
        {
            Probe.MainOnly(); Probe.Decoded++;
            target.width = data[19]; target.height = data[23];
            return data[24] != 255;
        }
    }
    public static class Screen { public static int width = 1280, height = 720; }
    public static class Time { public static float timeScale = 1, unscaledTime = 100; }
    public static class Input
    {
        public static Vector3 Mouse;
        public static Vector2 Scroll;
        public static bool Down, Held, Up;
        public static Vector3 mousePosition { get { Probe.InputReads++; return Mouse; } }
        public static Vector2 mouseScrollDelta { get { Probe.InputReads++; return Scroll; } }
        public static bool GetMouseButtonDown(int value) { Probe.InputReads++; return Down; }
        public static bool GetMouseButton(int value) { Probe.InputReads++; return Held; }
        public static bool GetMouseButtonUp(int value) { Probe.InputReads++; return Up; }
        public static void Reset() { Mouse = default; Scroll = default; Down = Held = Up = false; }
    }
    public enum RenderMode { ScreenSpaceOverlay }
    public sealed class Canvas : Component
    {
        public RenderMode renderMode { get; set; }
        public bool overrideSorting { get; set; }
        public int sortingOrder { get; set; }
    }
    public sealed class Font : Object
    {
        public static Font? CreateDynamicFontFromOSFont(string family, int size) => Probe.RejectFont ? null : new();
    }
    public enum TextAnchor { UpperLeft = 0, MiddleCenter = 4 }
    public enum FontStyle { Normal, Bold }
    public enum HorizontalWrapMode { Wrap, Overflow }
    public enum VerticalWrapMode { Truncate, Overflow }
}
namespace Common.UI
{
    public static class UniversalGameManager { public static bool IsInputEnabled { get; set; } }
}
namespace UnityEngine.UI
{
    public sealed class GraphicRaycaster : UnityEngine.Component { }
    public sealed class RectMask2D : UnityEngine.Component { }
    public sealed class Image : UnityEngine.Component
    {
        public enum Type { Simple, Sliced }
        public Type type { get; set; }
        public UnityEngine.Sprite? sprite { get; set; }
        private UnityEngine.Color _color;
        private bool _raycast;
        public UnityEngine.Color color { get => _color; set { _color = value; UnityEngine.Probe.Writes++; } }
        public bool raycastTarget { get => _raycast; set { _raycast = value; UnityEngine.Probe.Writes++; } }
    }
    public sealed class RawImage : UnityEngine.Component
    {
        private UnityEngine.Color _color;
        private UnityEngine.Texture? _texture;
        public UnityEngine.Texture? texture { get => _texture; set { _texture = value; UnityEngine.Probe.Writes++; } }
        public UnityEngine.Color color { get => _color; set { _color = value; UnityEngine.Probe.Writes++; } }
        public bool raycastTarget { get; set; }
    }
    public sealed class Text : UnityEngine.Component
    {
        private string _text = "";
        private int _fontSize;
        private UnityEngine.Color _color;
        public string text { get => _text; set { _text = value; UnityEngine.Probe.Writes++; } }
        public int fontSize { get => _fontSize; set { _fontSize = value; UnityEngine.Probe.Writes++; } }
        public UnityEngine.Color color { get => _color; set { _color = value; UnityEngine.Probe.Writes++; } }
        public bool supportRichText { get; set; }
        public bool raycastTarget { get; set; }
        public float lineSpacing { get; set; }
        public UnityEngine.Font? font { get; set; }
        public UnityEngine.TextAnchor alignment { get; set; }
        public UnityEngine.FontStyle fontStyle { get; set; }
        public UnityEngine.HorizontalWrapMode horizontalOverflow { get; set; }
        public UnityEngine.VerticalWrapMode verticalOverflow { get; set; }
        public float preferredWidth => _text.Split('\n').Max(line => line.Sum(c => c > 127 ? fontSize : fontSize * .55f));
        public float preferredHeight
        {
            get
            {
                var width = gameObject.Get<UnityEngine.RectTransform>().sizeDelta.x;
                return Math.Max(1, _text.Split('\n').Sum(line => (int)Math.Ceiling(line.Length * fontSize / Math.Max(1, width)))) * fontSize * 1.3f;
            }
        }
    }
}
