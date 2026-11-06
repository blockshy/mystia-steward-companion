using System.Reflection;

// 替身验证资源所有权、几何属性及线程访问，不模拟 GPU 排序、真实 IL2CPP 生命周期或输入。
namespace UnityEngine
{
    public class Object
    {
        public static readonly List<Object> Objects = new();
        public static int Created;
        public bool Destroyed;
        public string name = "";
        public HideFlags hideFlags;
        public readonly nint Pointer;
        protected Object() { Pointer = ++Created; Objects.Add(this); }
        public static void Destroy(Object value)
        {
            if (value.Destroyed) return;
            value.Destroyed = true;
            if (value is GameObject game)
            {
                foreach (var child in game.transform.Children.ToArray()) Destroy(child.gameObject);
                Destroy(game.transform);
                foreach (var component in game.Components) Destroy(component);
            }
        }
        public static bool operator ==(Object? left, Object? right) =>
            (ReferenceEquals(left, null) || left.Destroyed) && (ReferenceEquals(right, null) || right.Destroyed)
            || ReferenceEquals(left, right);
        public static bool operator !=(Object? left, Object? right) => !(left == right);
        public override bool Equals(object? other) => ReferenceEquals(this, other);
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
        public Transform? Parent;
        public Transform? parent => Parent;
        private Vector3 _localPosition;
        public Vector3 position
        {
            get => localToWorldMatrix.MultiplyPoint3x4(default);
            set => _localPosition = Parent == null ? value : Parent.localToWorldMatrix.Inverse().MultiplyPoint3x4(value);
        }
        public Vector3 localPosition { get => _localPosition; set => _localPosition = value; }
        public Vector3 localScale = new(1, 1, 1);
        // 本测试覆盖资源中确证的平移/缩放父链，不假装模拟未使用的旋转、骨骼或 GPU 变换。
        public Matrix4x4 localToWorldMatrix => (Parent?.localToWorldMatrix ?? Matrix4x4.identity)
            * Matrix4x4.Translate(_localPosition) * Matrix4x4.Scale(localScale);
        private Vector3 WorldScale
        {
            get
            {
                var inherited = Parent?.WorldScale ?? new Vector3(1, 1, 1);
                return new(localScale.x * inherited.x, localScale.y * inherited.y, localScale.z * inherited.z);
            }
        }
        public bool IsChildOf(Transform ancestor)
        { for (Transform? value = this; value != null; value = value.Parent) if (ReferenceEquals(value, ancestor)) return true; return false; }
        public void SetParent(Transform? parent, bool worldPositionStays)
        {
            var oldPosition = position;
            var oldScale = WorldScale;
            Parent?.Children.Remove(this);
            Parent = parent;
            parent?.Children.Add(this);
            if (!worldPositionStays) return;
            position = oldPosition;
            var inherited = Parent?.WorldScale ?? new Vector3(1, 1, 1);
            localScale = new(oldScale.x / inherited.x, oldScale.y / inherited.y, oldScale.z / inherited.z);
        }
    }
    public class GameObject : Object
    {
        public bool activeSelf = true;
        public bool activeInHierarchy => activeSelf && (transform.Parent == null || transform.Parent.gameObject.activeInHierarchy);
        public int layer;
        public readonly Transform transform;
        public readonly List<Component> Components = new();
        public GameObject(string value) { name = value; transform = new Transform { gameObject = this }; }
        public GameObject(string value, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Type> types) : this(value)
        {
            for (var index = 0; index < types.Length; index++)
                Add((Component)Activator.CreateInstance(types[index].Managed)!);
        }
        public T Add<T>(T component) where T : Component { component.gameObject = this; Components.Add(component); return component; }
        public Component? GetComponent(Il2CppSystem.Type type) => Components.SingleOrDefault(component => type.Managed.IsInstanceOfType(component));
        public void SetActive(bool value) => activeSelf = value;
    }
    public class Renderer : Component
    { public Material? sharedMaterial; public bool enabled = true; public int sortingLayerID; public int sortingOrder; }
    public class SpriteRenderer : Renderer
    {
        public Sprite? sprite;
        public Color color = new(1, 1, 1, 1);
        public SpriteDrawMode drawMode;
        public bool flipX, flipY;
    }
    public enum SpriteDrawMode { Simple, Sliced }
    public sealed class MeshRenderer : Renderer { }
    public static class SortingLayer
    {
        public static readonly Dictionary<int, int> Values = new();
        public static int GetLayerValueFromID(int id) => Values.TryGetValue(id, out var value) ? value : id;
    }
    public sealed class MeshFilter : Component { public Mesh sharedMesh = null!; }
    public sealed class Mesh : Object
    {
        public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> vertices = new(0);
        public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector2> uv = new(0);
        public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> colors32 = new(0);
        public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> triangles = new(0);
        public int Uploads;
        public void Clear() => Uploads++;
        public void RecalculateBounds() { }
    }
    public class Texture : Object
    {
        public int width { get; protected set; }
        public int height { get; protected set; }
    }
    public sealed class Texture2D : Texture
    {
        private Color32[] _pixels;
        public static int ReadPixelsCalls;
        public static bool FailNextReadPixels, FailNextGetPixels32, FailNextGetPixels, FailNextSetPixels32, FailNextApply;
        public FilterMode filterMode = FilterMode.Bilinear;
        public TextureWrapMode wrapMode;
        public TextureFormat format { get; }
        public bool isReadable { get; private set; } = true;
        public int PixelWrites { get; private set; }
        public int Uploads { get; private set; }
        public int CpuPixelReads { get; private set; }

        /// <summary>
        /// 保存真实尺寸和 RGBA 数据，避免把“纹理对象存在”误当成“采样后必然可见”。
        /// 新纹理以全透明像素开始；后续测试必须显式提供样本，不凭对象数量验证颜色。
        /// </summary>
        public Texture2D(int width, int height, TextureFormat format, bool mip, bool linear)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            this.width = width;
            this.height = height;
            this.format = format;
            _pixels = new Color32[checked(width * height)];
        }

        public void SetPixels32(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> pixels)
        {
            if (FailNextSetPixels32) { FailNextSetPixels32 = false; throw new InvalidOperationException("injected SetPixels32 failure"); }
            RequireReadable();
            if (pixels.Length != _pixels.Length) throw new ArgumentException("纹理像素数与尺寸不一致。", nameof(pixels));
            for (var index = 0; index < pixels.Length; index++) _pixels[index] = pixels[index];
            PixelWrites++;
        }

        public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> GetPixels32()
        {
            CpuPixelReads++;
            if (FailNextGetPixels32) { FailNextGetPixels32 = false; throw new InvalidOperationException("injected GetPixels32 failure"); }
            RequireReadable();
            var result = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(_pixels.Length);
            for (var index = 0; index < _pixels.Length; index++) result[index] = _pixels[index];
            return result;
        }

        public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color> GetPixels()
        {
            CpuPixelReads++;
            if (FailNextGetPixels) { FailNextGetPixels = false; throw new InvalidOperationException("injected GetPixels failure"); }
            RequireReadable();
            var result = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color>(_pixels.Length);
            for (var index = 0; index < _pixels.Length; index++)
            { var pixel = _pixels[index]; result[index] = new(pixel.r / 255f, pixel.g / 255f, pixel.b / 255f, pixel.a / 255f); }
            return result;
        }

        public Color GetPixel(int x, int y)
        {
            CpuPixelReads++;
            RequireReadable();
            if (x < 0 || x >= width || y < 0 || y >= height) throw new ArgumentOutOfRangeException(nameof(x));
            var value = _pixels[y * width + x];
            return new Color(value.r / 255f, value.g / 255f, value.b / 255f, value.a / 255f);
        }

        public void Apply(bool updateMipmaps, bool makeNoLongerReadable)
        {
            if (FailNextApply) { FailNextApply = false; throw new InvalidOperationException("injected Apply failure"); }
            RequireReadable();
            Uploads++;
            isReadable = !makeNoLongerReadable;
        }

        /// <summary>仅精确复制当前RT中的指定像素区域，不模拟GPU、色彩空间或真实ReadPixels同步成本。</summary>
        public void ReadPixels(Rect source, int destinationX, int destinationY, bool recalculateMipMaps)
        {
            ReadPixelsCalls++;
            if (FailNextReadPixels) { FailNextReadPixels = false; throw new InvalidOperationException("injected ReadPixels failure"); }
            RequireReadable();
            var target = RenderTexture.active ?? throw new InvalidOperationException("没有活动RT。");
            var sx = checked((int)source.x); var sy = checked((int)source.y);
            var sw = checked((int)source.width); var sh = checked((int)source.height);
            if (sx < 0 || sy < 0 || sx + sw > target.width || sy + sh > target.height
                || destinationX < 0 || destinationY < 0 || destinationX + sw > width || destinationY + sh > height)
                throw new ArgumentException("RT裁剪超出范围。");
            for (var y = 0; y < sh; y++)
            for (var x = 0; x < sw; x++)
                _pixels[(destinationY + y) * width + destinationX + x] = target.Pixels[(sy + y) * target.width + sx + x];
        }

        /// <summary>
        /// 仅用于软件颜色回归的最近邻采样：不可读纹理仍能被 GPU 采样，因此不检查 isReadable。
        /// 该函数不模拟双线性过滤、色彩空间、材质混合、深度或排序，不能作为真实 GPU 验收。
        /// </summary>
        internal Color32 SampleNearest(float u, float v)
        {
            if (!float.IsFinite(u) || !float.IsFinite(v)) throw new ArgumentException("采样坐标必须有限。");
            if (wrapMode == TextureWrapMode.Repeat)
            {
                u -= MathF.Floor(u);
                v -= MathF.Floor(v);
            }
            var x = Math.Clamp((int)MathF.Floor(u * width), 0, width - 1);
            var y = Math.Clamp((int)MathF.Floor(v * height), 0, height - 1);
            return _pixels[y * width + x];
        }

        private void RequireReadable()
        {
            if (!isReadable) throw new InvalidOperationException("纹理已停止保留可读像素。");
        }
    }
    public sealed class RenderTexture : Texture
    {
        public static RenderTexture? active;
        public static bool FailNextGetTemporary;
        public static int TemporaryAcquisitions, TemporaryReleases;
        public FilterMode filterMode;
        public TextureWrapMode wrapMode;
        internal Color32[] Pixels;
        public RenderTexture(int width, int height, int depth)
        { this.width = width; this.height = height; Pixels = new Color32[checked(width * height)]; }
        public static RenderTexture GetTemporary(int width, int height, int depth, RenderTextureFormat format, RenderTextureReadWrite readWrite)
        {
            if (FailNextGetTemporary) { FailNextGetTemporary = false; throw new InvalidOperationException("injected GetTemporary failure"); }
            TemporaryAcquisitions++;
            return new RenderTexture(width, height, depth);
        }
        public static void ReleaseTemporary(RenderTexture target) { TemporaryReleases++; Destroy(target); }
    }
    public static class GL
    {
        public const int TRIANGLES = 4;
        public static bool sRGBWrite;
        public static int MatrixDepth, BeginDepth, DrawCalls, EndCalls, PopCalls;
        public static bool FailNextClear, FailNextLoadPixelMatrix, FailNextBegin, FailNextVertex, FailNextEnd;
        public static readonly HashSet<nint> BlankSourcePointers = new();
        internal static Material? CurrentMaterial;
        private static readonly List<(Vector3 Point, Vector2 Uv)> Vertices = new();
        private static Vector2 _uv;
        private static Color _color = UnityEngine.Color.white;
        public static void Clear(bool clearDepth, bool clearColor, Color color)
        {
            if (FailNextClear) { FailNextClear = false; throw new InvalidOperationException("injected Clear failure"); }
            if (clearColor && RenderTexture.active != null) Array.Fill(RenderTexture.active.Pixels,
                new Color32((byte)(color.r * 255), (byte)(color.g * 255), (byte)(color.b * 255), (byte)(color.a * 255)));
        }
        public static void PushMatrix() => MatrixDepth++;
        public static void PopMatrix()
        {
            PopCalls++;
            if (MatrixDepth <= 0) throw new InvalidOperationException("GL矩阵栈下溢。");
            MatrixDepth--;
        }
        public static void LoadPixelMatrix(float left, float right, float bottom, float top)
        {
            if (FailNextLoadPixelMatrix) { FailNextLoadPixelMatrix = false; throw new InvalidOperationException("injected LoadPixelMatrix failure"); }
            if (left != 0 || bottom != 0 || right != RenderTexture.active?.width || top != RenderTexture.active?.height)
                throw new InvalidOperationException("测试只支持显式像素矩阵。");
        }
        public static void Begin(int mode)
        {
            if (FailNextBegin) { FailNextBegin = false; throw new InvalidOperationException("injected Begin failure"); }
            if (BeginDepth != 0 || mode != TRIANGLES) throw new InvalidOperationException("GL.Begin状态错误。");
            BeginDepth = 1; Vertices.Clear();
        }
        public static void Color(Color color) => _color = color;
        public static void TexCoord2(float x, float y) => _uv = new(x, y);
        public static void Vertex3(float x, float y, float z)
        {
            if (FailNextVertex) { FailNextVertex = false; throw new InvalidOperationException("injected Vertex3 failure"); }
            if (BeginDepth == 0) throw new InvalidOperationException("GL.Vertex不在Begin内。");
            Vertices.Add((new(x, y, z), _uv));
        }
        /// <summary>
        /// 仅用提交的三角形与UV在像素中心做软件采样；可模拟绘制成功但结果全透明，避免假定GPU必定复制成功。
        /// 不模拟真实Shader执行、颜色空间、图形驱动、裁剪状态或GPU同步，结果仍不是实机视觉验收。
        /// </summary>
        public static void End()
        {
            EndCalls++;
            if (FailNextEnd) { FailNextEnd = false; throw new InvalidOperationException("injected End failure"); }
            if (BeginDepth == 0) throw new InvalidOperationException("GL.End状态错误。");
            BeginDepth = 0;
            var target = RenderTexture.active;
            if (Vertices.Count < 3 || target == null) return;
            DrawCalls++;
            var material = CurrentMaterial ?? throw new InvalidOperationException("GL没有已设置的材质pass。");
            var texture = (Texture2D)material.mainTexture!;
            if (BlankSourcePointers.Contains(texture.Pointer)) return;
            var tint = material.GetColor("_Color"); var rendererTint = material.GetColor("_RendererColor");
            for (var py = 0; py < target.height; py++)
            for (var px = 0; px < target.width; px++)
            for (var triangle = 0; triangle + 2 < Vertices.Count; triangle += 3)
            {
                var a = Vertices[triangle]; var b = Vertices[triangle + 1]; var c = Vertices[triangle + 2];
                var x = px + .5f; var y = py + .5f;
                var denominator = (b.Point.y - c.Point.y) * (a.Point.x - c.Point.x)
                    + (c.Point.x - b.Point.x) * (a.Point.y - c.Point.y);
                if (MathF.Abs(denominator) < .000001f) continue;
                var wa = ((b.Point.y - c.Point.y) * (x - c.Point.x) + (c.Point.x - b.Point.x) * (y - c.Point.y)) / denominator;
                var wb = ((c.Point.y - a.Point.y) * (x - c.Point.x) + (a.Point.x - c.Point.x) * (y - c.Point.y)) / denominator;
                var wc = 1 - wa - wb;
                if (wa < -.00001f || wb < -.00001f || wc < -.00001f) continue;
                var pixel = texture.SampleNearest(a.Uv.x * wa + b.Uv.x * wb + c.Uv.x * wc,
                    a.Uv.y * wa + b.Uv.y * wb + c.Uv.y * wc);
                target.Pixels[py * target.width + px] = new Color32(
                    (byte)Math.Clamp((int)MathF.Round(pixel.r * tint.r * rendererTint.r * _color.r), 0, 255),
                    (byte)Math.Clamp((int)MathF.Round(pixel.g * tint.g * rendererTint.g * _color.g), 0, 255),
                    (byte)Math.Clamp((int)MathF.Round(pixel.b * tint.b * rendererTint.b * _color.b), 0, 255),
                    (byte)Math.Clamp((int)MathF.Round(pixel.a * tint.a * rendererTint.a * _color.a), 0, 255));
                break; // 两个三角形共边只采样一次，避免替身自行叠加alpha。
            }
        }
    }
    public static class SystemInfo
    {
        public static bool SupportsFloatTarget = true;
        public static bool SupportsRenderTextureFormat(RenderTextureFormat format) => SupportsFloatTarget;
    }
    public static class Graphics
    {
        public static int BlitCalls;
        public static bool FailNextBlit;
        /// <summary>软件替身只进行相同尺寸的精确RGBA复制，支持不可读源纹理，不冒充真实图形后端。</summary>
        public static void Blit(Texture source, RenderTexture destination)
        {
            BlitCalls++;
            if (FailNextBlit) { FailNextBlit = false; throw new InvalidOperationException("injected Blit failure"); }
            if (source is not Texture2D pixels || source.width != destination.width || source.height != destination.height)
                throw new InvalidOperationException("测试仅支持原尺寸纹理读回。");
            for (var y = 0; y < source.height; y++)
            for (var x = 0; x < source.width; x++)
                destination.Pixels[y * source.width + x] = pixels.SampleNearest((x + .5f) / source.width, (y + .5f) / source.height);
        }
    }
    public sealed class Sprite : Object
    {
        public Texture2D texture = new(4, 4, TextureFormat.RGBA32, false, false);
        // 切角轮廓留出完整下缘，既能检查每条左右分区在桌沿可见，也能识别bounds矩形填充错误。
        public Vector2[] vertices = { new(-.45f, -.5f), new(.45f, -.5f), new(.5f, -.45f), new(.5f, .45f),
            new(.45f, .5f), new(-.45f, .5f), new(-.5f, .45f), new(-.5f, -.45f) };
        public ushort[] triangles = { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 6, 0, 6, 7 };
        public Vector2[] uv = { new(.05f, 0), new(.95f, 0), new(1, .05f), new(1, .95f),
            new(.95f, 1), new(.05f, 1), new(0, .95f), new(0, .05f) };
        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float unit, uint extrude, SpriteMeshType mesh) => new();
    }
    public sealed class Material : Object
    {
        public static bool FailNextSetPass;
        private readonly Dictionary<string, Color> _colors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Vector4> _vectors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Texture?> _textures = new(StringComparer.Ordinal);
        public Shader shader;
        public Texture? mainTexture { get => GetTexture("_MainTex"); set => SetTexture("_MainTex", value); }
        // 显式保留调用记录，让测试能区分“确实写入单位乘数”与“替身忽略了设置”。
        public IReadOnlyDictionary<string, Color> ColorAssignments => _colors;
        public IReadOnlyDictionary<string, Vector4> VectorAssignments => _vectors;
        public Material(Shader value) { shader = value; }
        public bool HasProperty(string key) => shader.DeclaresProperty(key);
        public void SetColor(string key, Color color) => _colors[key] = color;
        public void SetVector(string key, Vector4 value) => _vectors[key] = value;
        public void SetTexture(string key, Texture? value) => _textures[key] = value;
        public Color GetColor(string key) => _colors.TryGetValue(key, out var value) ? value : default;
        public Vector4 GetVector(string key) => _vectors.TryGetValue(key, out var value) ? value : default;
        public Texture? GetTexture(string key) => _textures.TryGetValue(key, out var value) ? value : null;
        public bool SetPass(int pass)
        {
            if (FailNextSetPass) { FailNextSetPass = false; throw new InvalidOperationException("injected SetPass failure"); }
            GL.CurrentMaterial = this;
            return true;
        }
    }
    public sealed class Shader : Object
    {
        // 仅声明本测试实际涉及的 Sprites/Default 属性。其他着色器不因 SetColor 调用而自动获得属性。
        // 这能支持参数契约断言，但不是对游戏实际加载的 Shader 二进制或渲染管线的证明。
        private static readonly HashSet<string> SpriteProperties = new(StringComparer.Ordinal)
        { "_MainTex", "_Color", "_RendererColor", "_Flip", "_AlphaTex", "_EnableExternalAlpha", "PixelSnap" };
        private static readonly Shader Standard = new() { name = "Sprites/Default" };
        public static bool Fail;
        public bool isSupported = true;
        public static Shader? Find(string name) => !Fail && name == Standard.name ? Standard : null;
        internal bool DeclaresProperty(string key) => name == "Sprites/Default" && SpriteProperties.Contains(key);
    }
    public sealed class Grid : Object
    {
        public float Width = 1, Height = 1;
        public Vector3 GetCellCenterWorld(Vector3Int point) => new(point.x * Width + Width / 2, point.y * Height + Height / 2, point.z);
    }
    public readonly record struct Color(float r, float g, float b, float a)
    { public static Color white => new(1, 1, 1, 1); public static Color clear => default; }
    public readonly record struct Color32(byte r, byte g, byte b, byte a);
    public readonly record struct Vector2(float x, float y);
    public readonly record struct Vector3(float x, float y, float z) { public static Vector3 back => new(0, 0, -1); }
    public readonly record struct Vector4(float x, float y, float z, float w) { public static Vector4 zero => new(0, 0, 0, 0); }
    public readonly record struct Matrix4x4(System.Numerics.Matrix4x4 Value)
    {
        public float m03 => Value.M41; public float m13 => Value.M42; public float m23 => Value.M43;
        public static Matrix4x4 identity => new(System.Numerics.Matrix4x4.Identity);
        public static Matrix4x4 Translate(Vector3 value) => new(System.Numerics.Matrix4x4.CreateTranslation(value.x, value.y, value.z));
        public static Matrix4x4 Scale(Vector3 value) => new(System.Numerics.Matrix4x4.CreateScale(value.x, value.y, value.z));
        public static Matrix4x4 operator *(Matrix4x4 left, Matrix4x4 right) => new(right.Value * left.Value);
        public Matrix4x4 Inverse() => System.Numerics.Matrix4x4.Invert(Value, out var inverse)
            ? new(inverse) : throw new InvalidOperationException("测试变换不可逆。");
        public Vector3 MultiplyPoint3x4(Vector3 point)
        { var result = System.Numerics.Vector3.Transform(new(point.x, point.y, point.z), Value); return new(result.X, result.Y, result.Z); }
        public float this[int row, int column] => (row, column) switch {
            (0,0) => Value.M11, (0,1) => Value.M21, (0,2) => Value.M31, (0,3) => Value.M41,
            (1,0) => Value.M12, (1,1) => Value.M22, (1,2) => Value.M32, (1,3) => Value.M42,
            (2,0) => Value.M13, (2,1) => Value.M23, (2,2) => Value.M33, (2,3) => Value.M43,
            (3,0) => Value.M14, (3,1) => Value.M24, (3,2) => Value.M34, (3,3) => Value.M44, _ => throw new IndexOutOfRangeException() };
    }
    public readonly record struct Vector3Int(int x, int y, int z);
    public readonly record struct Rect(float x, float y, float width, float height);
    public enum TextureFormat { RGBA32, RGBAFloat }
    public enum RenderTextureFormat { ARGB32, ARGBFloat }
    public enum RenderTextureReadWrite { Linear }
    public enum HideFlags { None, HideAndDontSave }
    public enum FilterMode { Point, Bilinear }
    public enum TextureWrapMode { Repeat, Clamp }
    public enum SpriteMeshType { FullRect }
    public static class Time { public static float realtimeSinceStartup; }
}
namespace UnityEngine.Rendering
{
    /// <summary>
    /// 只标记排序组的精确祖先身份及可变外部排序。断言比较同一实例，绝不将组内 order 与场景 order 混比；
    /// 此替身不绘制画面，也不承诺重现 Unity 的排序、透明混合、渲染队列或包围盒计算。
    /// </summary>
    public sealed class SortingGroup : UnityEngine.Component
    {
        public bool enabled = true;
        public int sortingLayerID;
        public int sortingOrder;
    }
}
namespace Il2CppSystem { public sealed record Type(System.Type Managed); }
namespace Il2CppInterop.Runtime { public static class Il2CppType { public static Il2CppSystem.Type From(Type type) => new(type); } }
namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public class Il2CppStructArray<T>
    {
        private readonly T[] _values;
        public int Length => _values.Length;
        public Il2CppStructArray(int count) => _values = new T[count];
        public T this[int index] { get => _values[index]; set => _values[index] = value; }
    }
    public class Il2CppReferenceArray<T> : Il2CppStructArray<T>
    {
        public Il2CppReferenceArray(int count) : base(count) { }
    }
}
namespace NightScene.UI.CookingUtility
{
    public sealed class CookAnimator : UnityEngine.Component
    { public UnityEngine.SpriteRenderer m_CookerSpriteRenderer = null!; }
}
namespace NightScene
{
    public sealed class MapManager
    {
        public static MapManager Instance = new();
        public UnityEngine.Grid referenceGrid = new();
    }
}
namespace UnityEngine.Tilemaps
{
    public sealed class Tilemap : UnityEngine.Component
    {
        public Tilemap() { var owner = new UnityEngine.GameObject("NativeTilemap"); owner.Add(this); owner.Add(new TilemapRenderer()); }
        public UnityEngine.Matrix4x4 CellTransform = UnityEngine.Matrix4x4.identity;
        public UnityEngine.Vector3 GetCellCenterWorld(UnityEngine.Vector3Int point) => NightScene.MapManager.Instance.referenceGrid.GetCellCenterWorld(point);
        public UnityEngine.Matrix4x4 GetTransformMatrix(UnityEngine.Vector3Int point) => CellTransform;
    }
    public sealed class TilemapRenderer : UnityEngine.Renderer { }
}
namespace NightScene.Tiles
{
    public sealed class InteractableTile : UnityEngine.Object
    { public UnityEngine.Sprite sprite = new(); public int type; }
    public sealed class SelectionCluster : UnityEngine.Component
    {
        public UnityEngine.SpriteRenderer[] Renderers = Array.Empty<UnityEngine.SpriteRenderer>();
        public T[] GetObjects<T>() => Renderers.Cast<T>().ToArray();
    }
    public sealed class TileManager
    {
        public static TileManager Instance = new();
        public InteractableTile Tile = new();
        public readonly Dictionary<UnityEngine.Vector3Int, InteractableTile> TilesByPosition = new();
        public UnityEngine.Tilemaps.Tilemap interactable = new();
        public SelectionCluster onSelection = null!;
        public int GetInteractableType(UnityEngine.Vector3Int position, out InteractableTile tile)
        { tile = TilesByPosition.TryGetValue(position, out var mapped) ? mapped : Tile; return tile.type; }
    }
}
namespace MystiaStewardCompanion.Save
{
    internal readonly record struct Lifecycle(bool IsActive, long Generation, int ThreadId);
    internal static class RuntimeNightBusinessLifecycle
    { public static Lifecycle Snapshot = new(true, 1, Environment.CurrentManagedThreadId); }
    internal static class RuntimeOrderTraceIdService
    {
        public static bool TryNormalizeTargetTraceId(RuntimeUiTargetKind kind, string trace, bool enabled, out string normalized, out string failure)
        { normalized = trace; failure = ""; return trace.Length > 0; }
    }
    internal readonly record struct RuntimeCookerGridPosition(int X, int Y, int Z);
    internal sealed class RuntimeCookerControllerEntry
    {
        public object Controller { get; init; } = null!;
        public RuntimeCookerGridPosition GridPosition { get; init; }
        public string ControllerIdentity { get; init; } = "";
    }
    internal sealed class RuntimeCookerControllerState
    { public bool CouldOpen = true; public bool IsEmptyDesk; public IReadOnlyList<int> TypeIds = new[] { 1 }; }
    internal sealed class TestController
    {
        public NightScene.UI.CookingUtility.CookAnimator visual = null!;
        public RuntimeCookerControllerState State = new();
        public bool ThrowOnRead;
    }
    internal static class RuntimeCookerReflection
    {
        public static HashSet<RuntimeCookerGridPosition> Locks = new();
        public static readonly List<RuntimeCookerControllerEntry> Entries = new();
        public static bool FailLocks;
        public static int StateReads;
        public static bool TryReadLockedCookerPositions(out IReadOnlySet<RuntimeCookerGridPosition> locks, out string status)
        { locks = Locks; status = "lock unavailable"; return !FailLocks; }
        public static object GetCookSystemManager() => Entries;
        public static bool TryReadCookerControllerEntriesFromCookSystem(object manager, IReadOnlySet<RuntimeCookerGridPosition> locks,
            out IReadOnlyList<RuntimeCookerControllerEntry> entries, out string status)
        { entries = Entries; status = ""; return true; }
        public static bool TryReadCookerControllerState(object source, out RuntimeCookerControllerState state, out string status)
        {
            StateReads++; var controller = (TestController)source;
            if (controller.ThrowOnRead) throw new InvalidOperationException("locked controller was read");
            state = controller.State; status = ""; return true;
        }
    }
    internal static class RuntimeReflectionUtility
    {
        public static Type? FindType(string name) => typeof(RuntimeReflectionUtility).Assembly.GetType(name);
        public static object? GetStaticMemberValue(Type type, string name) => type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        public static object? GetMemberValue(object instance, string name) => instance.GetType().GetField(name)?.GetValue(instance);
        public static object? TryCastRuntimeObject(object? instance, string name) => typeof(RuntimeReflectionUtility).Assembly.GetType(name)?.IsInstanceOfType(instance) == true ? instance : null;
        public static IEnumerable<object> EnumerateObjects(object? value) => value is System.Collections.IEnumerable items ? items.Cast<object>() : Array.Empty<object>();
        public static bool TryReadNativeObjectPointer(object instance, out nint pointer)
        { pointer = ((UnityEngine.Object)instance).Pointer; return pointer != 0; }
    }
}
