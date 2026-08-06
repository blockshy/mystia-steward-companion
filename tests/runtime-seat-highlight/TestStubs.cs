namespace UnityEngine
{
    internal class Object
    {
        private static long _nextPointer = 1;
        private static readonly Dictionary<IntPtr, Type> NativeTypes = new();
        private IntPtr _cachedPtr;

        public Object()
        {
            Pointer = new IntPtr(Interlocked.Increment(ref _nextPointer));
            _cachedPtr = Pointer;
            NativeTypes[Pointer] = GetType();
        }

        protected Object(IntPtr pointer, Type nativeType)
        {
            Pointer = pointer;
            _cachedPtr = pointer;
            NativeTypes[pointer] = nativeType;
        }

        public IntPtr Pointer { get; }

        public IntPtr m_CachedPtr
        {
            get
            {
                if (ThrowOnCachedPointerRead) throw new InvalidOperationException("stale cached pointer getter");
                return _cachedPtr;
            }
        }

        public bool ThrowOnCachedPointerRead { get; set; }

        public string name { get; set; } = "";

        public void InvalidateNativePointer() => _cachedPtr = IntPtr.Zero;

        public static Type? GetNativeType(IntPtr pointer) =>
            NativeTypes.TryGetValue(pointer, out var type) ? type : null;

        public static T Instantiate<T>(T original, Transform parent) where T : Object
        {
            if (original is not GameObject source)
            {
                throw new InvalidOperationException("Only GameObject cloning is supported by this smoke.");
            }

            var clone = source.CloneFactory?.Invoke(parent) ?? source.DeepClone(parent);
            GameObject.Instantiated.Add(clone);
            return (T)(Object)clone;
        }

        public static T Instantiate<T>(T original) where T : Object
        {
            if (original is not GameObject source)
            {
                throw new InvalidOperationException("Only GameObject cloning is supported by this smoke.");
            }

            var clone = source.CloneFactory?.Invoke(null!) ?? source.DeepClone(null);
            GameObject.Instantiated.Add(clone);
            return (T)(Object)clone;
        }

        public static void Destroy(Object target)
        {
            target.InvalidateNativePointer();
            if (target is Material material)
            {
                Material.Destroyed.Add(material);
                return;
            }
            if (target is Sprite sprite)
            {
                Sprite.Destroyed.Add(sprite);
                return;
            }
            if (target is Texture2D texture)
            {
                Texture2D.Destroyed.Add(texture);
                return;
            }
            if (target is not GameObject gameObject) return;

            gameObject.DestroyHierarchy();
            GameObject.Destroyed.Add(gameObject);
        }
    }

    internal sealed class GameObject : Object
    {
        private readonly List<Component> _components = new();

        public GameObject()
        {
            transform = new Transform(this);
        }

        public GameObject(
            string objectName,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Type> componentTypes)
            : this()
        {
            name = objectName;
            foreach (var componentType in componentTypes)
            {
                if (componentType.ManagedType != typeof(SpriteRenderer))
                {
                    throw new InvalidOperationException($"Unsupported constructed component {componentType.ManagedType.FullName}.");
                }

                var renderer = new SpriteRenderer
                {
                    sharedMaterial = SpriteRenderer.DefaultMaterial,
                    BoundsReadCallback = SpriteRenderer.ConstructedRendererBoundsReadCallback,
                    CreatePropertyBlockOnVisualWrite = SpriteRenderer.ConstructedRendererCreatesPropertyBlockOnVisualWrite,
                };
                renderer.AutoBounds = true;
                AddComponent(renderer);
            }
            Created.Add(this);
        }

        public static List<GameObject> Instantiated { get; } = new();

        public static List<GameObject> Created { get; } = new();

        public static List<GameObject> Destroyed { get; } = new();

        public static bool DegradeSpriteRendererTypedQuery { get; set; } = true;

        public static int DegradedSpriteRendererTypedQueries { get; private set; }

        public Func<Transform, GameObject>? CloneFactory { get; set; }

        public Transform transform { get; }

        public bool activeSelf { get; private set; } = true;

        public int layer { get; set; }

        public bool activeInHierarchy => activeSelf && (transform.parent?.Owner?.activeInHierarchy ?? true);

        public void SetActive(bool value) => activeSelf = value;

        public void AddComponent(Component component)
        {
            if (!_components.Contains(component)) _components.Add(component);
            component.gameObject = this;
        }

        public void SetComponent(Component component) => AddComponent(component);

        public Component? GetComponent(Type type) => _components.FirstOrDefault(type.IsInstanceOfType);

        public Component? GetComponent(Il2CppSystem.Type type)
        {
            var component = GetComponent(type.ManagedType);
            if (DegradeSpriteRendererTypedQuery
                && type.ManagedType == typeof(SpriteRenderer)
                && component is SpriteRenderer renderer)
            {
                DegradedSpriteRendererTypedQueries++;
                return new NativeComponentWrapper(renderer, typeof(SpriteRenderer));
            }
            return component;
        }

        internal Component? FindNativeComponent(IntPtr pointer, Type type) =>
            _components.SingleOrDefault(component =>
                component.Pointer == pointer && type.IsInstanceOfType(component));

        internal GameObject DeepClone(Transform? parent)
        {
            var objectMap = new Dictionary<GameObject, GameObject>();
            var componentMap = new Dictionary<Component, Component>();
            var clone = CloneHierarchy(this, parent, objectMap, componentMap);

            foreach (var pair in componentMap)
            {
                if (pair.Key is DEYU.UniversalUISystem.UIElementCluster sourceCluster
                    && pair.Value is DEYU.UniversalUISystem.UIElementCluster cloneCluster)
                {
                    foreach (var sourceObject in sourceCluster.Objects)
                    {
                        if (componentMap.TryGetValue(sourceObject, out var cloneObject)) cloneCluster.Add(cloneObject);
                    }
                    if (sourceCluster.SerializedObjectsOverride != null)
                    {
                        cloneCluster.SerializedObjectsOverride = sourceCluster.SerializedObjectsOverride
                            .Select(sourceObject => componentMap.TryGetValue(sourceObject, out var cloneObject)
                                ? cloneObject
                                : throw new InvalidOperationException("Serialized UIElementCluster object is outside the cloned hierarchy."))
                            .ToArray();
                    }
                }
            }

            return clone;
        }

        private static GameObject CloneHierarchy(
            GameObject source,
            Transform? parent,
            Dictionary<GameObject, GameObject> objectMap,
            Dictionary<Component, Component> componentMap)
        {
            var clone = new GameObject { name = source.name, layer = source.layer };
            clone.SetActive(source.activeSelf);
            clone.transform.parent = parent;
            clone.transform.localPosition = source.transform.localPosition;
            clone.transform.localScale = source.transform.localScale;
            clone.transform.localRotation = source.transform.localRotation;
            objectMap[source] = clone;

            foreach (var component in source._components)
            {
                Component cloneComponent = component switch
                {
                    SpriteRenderer renderer => new SpriteRenderer
                    {
                        enabled = renderer.enabled,
                        sprite = renderer.sprite,
                        drawMode = renderer.drawMode,
                        flipX = renderer.flipX,
                        flipY = renderer.flipY,
                        isVisible = renderer.isVisible,
                        sortingLayerID = renderer.sortingLayerID,
                        sortingOrder = renderer.sortingOrder,
                        color = renderer.color,
                        sharedMaterial = renderer.sharedMaterial,
                        AutoBounds = renderer.AutoBounds,
                        StaticBounds = renderer.StaticBounds,
                    },
                    DEYU.UniversalUISystem.UIElementCluster => new DEYU.UniversalUISystem.UIElementCluster(),
                    _ => throw new InvalidOperationException($"Unsupported deep-cloned component {component.GetType().FullName}.")
                };
                clone.AddComponent(cloneComponent);
                if (component is SpriteRenderer sourceRenderer
                    && cloneComponent is SpriteRenderer clonedRenderer)
                {
                    sourceRenderer.CopyPropertyBlockTo(clonedRenderer);
                }
                componentMap[component] = cloneComponent;
            }

            foreach (var child in source.transform.Children)
            {
                if (child.Owner != null) CloneHierarchy(child.Owner, clone.transform, objectMap, componentMap);
            }

            return clone;
        }

        internal void DestroyHierarchy()
        {
            InvalidateNativePointer();
            transform.InvalidateNativePointer();
            foreach (var component in _components) component.InvalidateNativePointer();
            foreach (var child in transform.Children.ToArray()) child.Owner?.DestroyHierarchy();
        }
    }

    internal class Component : Object
    {
        public Component()
        {
            gameObject = new GameObject();
            gameObject.AddComponent(this);
        }

        protected Component(GameObject owner, IntPtr pointer, Type nativeType)
            : base(pointer, nativeType)
        {
            gameObject = owner;
        }

        public GameObject gameObject { get; internal set; }

        public Transform transform => gameObject.transform;
    }

    internal sealed class NativeComponentWrapper : Component
    {
        public NativeComponentWrapper(Component source, Type nativeType)
            : base(source.gameObject, source.Pointer, nativeType)
        {
        }
    }

    internal sealed class Transform : Object
    {
        private Transform? _parent;

        public Transform() : this(null)
        {
        }

        internal Transform(GameObject? owner)
        {
            Owner = owner;
        }

        internal GameObject? Owner { get; }

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

        public Vector3 localScale { get; set; } = new(1f, 1f, 1f);

        public Vector3 position
        {
            get => parent == null ? localPosition : Add(parent.position, localPosition);
            set => localPosition = parent == null ? value : Subtract(value, parent.position);
        }

        public Vector3 PointScale
        {
            get => localScale;
            set => localScale = value;
        }

        public int QuarterTurnsZ
        {
            get => (int)MathF.Round(MathF.Atan2(
                2f * ((localRotation.w * localRotation.z) + (localRotation.x * localRotation.y)),
                1f - (2f * ((localRotation.y * localRotation.y) + (localRotation.z * localRotation.z))))
                / (MathF.PI / 2f));
            set => localRotation = Quaternion.Euler(0f, 0f, value * 90f);
        }

        public Vector3 TransformPoint(Vector3 point)
        {
            var scaled = new Vector3(
                point.x * localScale.x,
                point.y * localScale.y,
                point.z * localScale.z);
            var local = localRotation.Rotate(scaled);
            return Add(position, local);
        }

        public bool IsChildOf(Transform ancestor)
        {
            for (Transform? current = this; current != null; current = current.parent)
            {
                if (current.Pointer == ancestor.Pointer) return true;
            }

            return false;
        }

        private static Vector3 Add(Vector3 left, Vector3 right) =>
            new(left.x + right.x, left.y + right.y, left.z + right.z);

        private static Vector3 Subtract(Vector3 left, Vector3 right) =>
            new(left.x - right.x, left.y - right.y, left.z - right.z);
    }

    internal class Texture : Object
    {
    }

    internal sealed class Texture2D : Texture
    {
        public Texture2D(int width = 2, int height = 2)
        {
            this.width = width;
            this.height = height;
            format = TextureFormat.RGBA32;
            isReadable = true;
        }

        public Texture2D(
            int width,
            int height,
            TextureFormat textureFormat,
            bool mipChain,
            bool linear)
            : this(width, height)
        {
            format = textureFormat;
            MipChain = mipChain;
            Linear = linear;
            Created.Add(this);
        }

        public static Texture2D whiteTexture { get; } = new(2, 2)
        {
            name = "UnityWhiteTexture",
        };

        public int width { get; }

        public int height { get; }

        public TextureFormat format { get; }

        public bool isReadable { get; private set; }

        public int mipmapCount => MipChain ? 2 : 1;

        public bool MipChain { get; }

        public bool Linear { get; }

        public int ApplyCount { get; private set; }

        public IReadOnlyList<Color32> Pixels { get; private set; } = Array.Empty<Color32>();

        public static List<Texture2D> Created { get; } = new();

        public static List<Texture2D> Destroyed { get; } = new();

        public static bool ThrowOnSetPixels32 { get; set; }

        public static bool ThrowOnApply { get; set; }

        public void SetPixels32(
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> colors)
        {
            if (ThrowOnSetPixels32) throw new InvalidOperationException("rejected white pixel upload");
            if (!isReadable) throw new InvalidOperationException("texture is not readable");
            if (colors.Length != width * height)
            {
                throw new InvalidOperationException("pixel array length does not match the texture size");
            }

            var pixels = new Color32[colors.Length];
            for (var index = 0; index < colors.Length; index += 1)
            {
                pixels[index] = colors[index];
            }
            Pixels = pixels;
        }

        public void Apply(bool updateMipmaps, bool makeNoLongerReadable)
        {
            if (ThrowOnApply) throw new InvalidOperationException("rejected white texture apply");
            if (updateMipmaps != MipChain)
            {
                throw new InvalidOperationException("unexpected mipmap update mode");
            }
            ApplyCount++;
            if (makeNoLongerReadable) isReadable = false;
        }
    }

    internal sealed class Sprite : Object
    {
        private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector2> _vertices;
        private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ushort> _triangles;
        private readonly SpriteMeshType _meshType;

        public Sprite() : this(new Bounds(default, new Vector3(1f, 1f, 0f)), new Texture2D())
        {
        }

        public Sprite(Bounds bounds) : this(bounds, new Texture2D())
        {
        }

        public Sprite(Bounds bounds, Texture texture)
        {
            _meshType = SpriteMeshType.Tight;
            this.texture = (Texture2D)texture;
            rect = new Rect(0f, 0f, bounds.size.x, bounds.size.y);
            pixelsPerUnit = 1f;
            pivot = new Vector2(
                (0f - (bounds.center.x - (bounds.size.x * 0.5f))),
                (0f - (bounds.center.y - (bounds.size.y * 0.5f))));
            _vertices = BuildArray(new[]
            {
                new Vector2(bounds.center.x - (bounds.size.x * 0.5f), bounds.center.y - (bounds.size.y * 0.5f)),
                new Vector2(bounds.center.x - (bounds.size.x * 0.5f), bounds.center.y + (bounds.size.y * 0.5f)),
                new Vector2(bounds.center.x + (bounds.size.x * 0.5f), bounds.center.y + (bounds.size.y * 0.5f)),
                new Vector2(bounds.center.x + (bounds.size.x * 0.5f), bounds.center.y - (bounds.size.y * 0.5f)),
            });
            _triangles = BuildArray<ushort>(new ushort[] { 0, 1, 2, 0, 2, 3 });
            this.bounds = bounds;
        }

        public Sprite(
            Texture2D texture,
            Rect rect,
            Vector2 pivot,
            float pixelsPerUnit,
            Vector2[] localVertices,
            ushort[] triangles)
        {
            _meshType = SpriteMeshType.Tight;
            this.texture = texture;
            this.rect = rect;
            this.pivot = pivot;
            this.pixelsPerUnit = pixelsPerUnit;
            _vertices = BuildArray(localVertices);
            _triangles = BuildArray(triangles);
            bounds = CalculateBounds(localVertices);
        }

        private Sprite(
            Texture2D texture,
            Rect rect,
            Vector2 normalizedPivot,
            float pixelsPerUnit,
            SpriteMeshType meshType)
        {
            _meshType = meshType;
            this.texture = texture;
            this.rect = rect;
            pivot = new Vector2(
                normalizedPivot.x * rect.width,
                normalizedPivot.y * rect.height);
            this.pixelsPerUnit = pixelsPerUnit;
            _vertices = BuildArray(new[]
            {
                new Vector2(-pivot.x / pixelsPerUnit, -pivot.y / pixelsPerUnit),
                new Vector2(-pivot.x / pixelsPerUnit, (rect.height - pivot.y) / pixelsPerUnit),
                new Vector2((rect.width - pivot.x) / pixelsPerUnit, (rect.height - pivot.y) / pixelsPerUnit),
                new Vector2((rect.width - pivot.x) / pixelsPerUnit, -pivot.y / pixelsPerUnit),
            });
            _triangles = BuildArray<ushort>(new ushort[] { 0, 1, 2, 0, 2, 3 });
            bounds = CalculateBounds(_vertices.ToArray());
        }

        public static List<Sprite> Created { get; } = new();

        public static List<Sprite> Destroyed { get; } = new();

        public static bool ThrowOnOverrideGeometry { get; set; }

        public static int RejectedFullRectOverrideCount { get; private set; }

        public static Sprite? Create(
            Texture2D texture,
            Rect rect,
            Vector2 pivot,
            float pixelsPerUnit,
            uint extrude,
            SpriteMeshType meshType)
        {
            if (texture == null
                || rect.x < 0f
                || rect.y < 0f
                || rect.width <= 0f
                || rect.height <= 0f
                || rect.x + rect.width > texture.width
                || rect.y + rect.height > texture.height
                || pixelsPerUnit <= 0f
                || extrude != 0u
                || (meshType != SpriteMeshType.FullRect && meshType != SpriteMeshType.Tight))
            {
                return null;
            }

            var effectiveMeshType = meshType == SpriteMeshType.Tight
                && (rect.width < 32f || rect.height < 32f)
                    ? SpriteMeshType.FullRect
                    : meshType;
            var sprite = new Sprite(
                texture,
                rect,
                pivot,
                pixelsPerUnit,
                effectiveMeshType);
            Created.Add(sprite);
            return sprite;
        }

        public void OverrideGeometry(
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector2> rectVertices,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ushort> triangles)
        {
            // Unity 2021.3 rejects OverrideGeometry for a Sprite created as FullRect.
            // It reports an engine error and retains the generated rectangle instead of throwing.
            if (_meshType == SpriteMeshType.FullRect)
            {
                RejectedFullRectOverrideCount++;
                return;
            }
            if (ThrowOnOverrideGeometry)
            {
                throw new InvalidOperationException("rejected sprite geometry override");
            }
            if (rectVertices.Length < 3 || triangles.Length < 3 || triangles.Length % 3 != 0)
            {
                throw new InvalidOperationException("invalid override geometry shape");
            }

            var localVertices = new Vector2[rectVertices.Length];
            for (var index = 0; index < rectVertices.Length; index += 1)
            {
                var vertex = rectVertices[index];
                if (vertex.x < 0f
                    || vertex.y < 0f
                    || vertex.x > rect.width
                    || vertex.y > rect.height)
                {
                    throw new InvalidOperationException("override vertex is outside the Sprite rect");
                }
                localVertices[index] = new Vector2(
                    (vertex.x - pivot.x) / pixelsPerUnit,
                    (vertex.y - pivot.y) / pixelsPerUnit);
            }
            for (var index = 0; index < triangles.Length; index += 1)
            {
                if (triangles[index] >= localVertices.Length)
                {
                    throw new InvalidOperationException("override triangle index is invalid");
                }
            }

            _vertices = BuildArray(localVertices);
            _triangles = BuildArray(triangles.ToArray());
            bounds = CalculateBounds(localVertices);
        }

        public Bounds bounds { get; private set; }

        public Texture2D texture { get; }

        public Rect rect { get; }

        public Vector2 pivot { get; }

        public float pixelsPerUnit { get; }

        public SpriteMeshType createdMeshType => _meshType;

        public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector2> vertices =>
            BuildArray(_vertices.ToArray());

        public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ushort> triangles =>
            BuildArray(_triangles.ToArray());

        private static Bounds CalculateBounds(IReadOnlyList<Vector2> vertices)
        {
            var minX = vertices.Min(vertex => vertex.x);
            var minY = vertices.Min(vertex => vertex.y);
            var maxX = vertices.Max(vertex => vertex.x);
            var maxY = vertices.Max(vertex => vertex.y);
            return new Bounds(
                new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, 0f),
                new Vector3(maxX - minX, maxY - minY, 0f));
        }

        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<T> BuildArray<T>(T[] values)
        {
            var result = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<T>(values.Length);
            for (var index = 0; index < values.Length; index += 1) result[index] = values[index];
            return result;
        }
    }

    internal sealed class SpriteRenderer : Component
    {
        private MaterialPropertyBlock? _propertyBlock;
        private Material? _sharedMaterial;
        private Sprite? _sprite;
        private Color _color = new(1f, 1f, 1f, 1f);
        private bool _isVisible = true;

        public SpriteRenderer()
        {
        }

        internal SpriteRenderer(GameObject owner, IntPtr pointer, Type nativeType)
            : base(owner, pointer, nativeType)
        {
        }

        public static Material? DefaultMaterial { get; set; }

        public static bool RejectMaterialInstantiation { get; set; }

        public static Action? ConstructedRendererBoundsReadCallback { get; set; }

        public static bool ConstructedRendererCreatesPropertyBlockOnVisualWrite { get; set; }

        public bool enabled { get; set; }

        public Sprite? sprite
        {
            get => _sprite;
            set
            {
                _sprite = value;
                CreatePropertyBlockForVisualWrite();
            }
        }

        public SpriteDrawMode drawMode { get; set; } = SpriteDrawMode.Simple;

        public bool flipX { get; set; }

        public bool flipY { get; set; }

        public bool isVisible
        {
            get => _isVisible;
            set => _isVisible = value;
        }

        public int sortingLayerID { get; set; }

        public int sortingOrder { get; set; }

        public Color color
        {
            get => _color;
            set
            {
                if (ThrowOnColorWrite) throw new InvalidOperationException("rejected color write");
                _color = value;
                CreatePropertyBlockForVisualWrite();
            }
        }

        public bool ThrowOnColorWrite { get; set; }

        public Material? sharedMaterial
        {
            get => _sharedMaterial;
            set => _sharedMaterial = value;
        }

        public Material? material
        {
            get
            {
                if (_sharedMaterial == null || RejectMaterialInstantiation) return _sharedMaterial;
                if (_sharedMaterial.IsRendererInstance) return _sharedMaterial;

                _sharedMaterial = new Material(_sharedMaterial) { IsRendererInstance = true };
                return _sharedMaterial;
            }
        }

        public bool AutoBounds { get; set; }

        public Bounds StaticBounds { get; set; } = new(default, new Vector3(1f, 1f, 0f));

        public Bounds bounds
        {
            get
            {
                BoundsReadCount += 1;
                var callback = BoundsReadCallback;
                BoundsReadCallback = null;
                callback?.Invoke();
                return AutoBounds && sprite != null
                    ? new Bounds(transform.TransformPoint(sprite.bounds.center), StaticBounds.size)
                    : StaticBounds;
            }
            set => StaticBounds = value;
        }

        public int BoundsReadCount { get; private set; }

        public Action? BoundsReadCallback { get; set; }

        internal bool CreatePropertyBlockOnVisualWrite { get; set; }

        public bool HasPropertyBlock() => _propertyBlock != null;

        public void GetPropertyBlock(MaterialPropertyBlock target)
        {
            target.CopyFrom(_propertyBlock);
        }

        public void SetPropertyBlock(MaterialPropertyBlock? value)
        {
            if (value == null)
            {
                _propertyBlock = null;
                return;
            }

            _propertyBlock ??= new MaterialPropertyBlock();
            _propertyBlock.CopyFrom(value);
        }

        internal void CopyPropertyBlockTo(SpriteRenderer target)
        {
            target.SetPropertyBlock(_propertyBlock);
        }

        private void CreatePropertyBlockForVisualWrite()
        {
            if (CreatePropertyBlockOnVisualWrite) _propertyBlock ??= new MaterialPropertyBlock();
        }
    }

    internal static class Time
    {
        public static float realtimeSinceStartup { get; set; }
    }

    internal readonly record struct Vector3(float x, float y, float z);
    internal readonly record struct Vector3Int(int x, int y, int z);
    internal readonly record struct Vector2(float x, float y);
    internal readonly record struct Rect(float x, float y, float width, float height);
    internal readonly record struct Bounds(Vector3 center, Vector3 size);
    internal readonly record struct Matrix4x4(float m03, float m13, float m23);
    internal readonly record struct Color(float r, float g, float b, float a);
    internal readonly record struct Color32(byte r, byte g, byte b, byte a);
    internal readonly record struct Quaternion(float x, float y, float z, float w)
    {
        public static Quaternion identity => new(0f, 0f, 0f, 1f);

        public static Quaternion Euler(float xDegrees, float yDegrees, float zDegrees)
        {
            var x = xDegrees * MathF.PI / 360f;
            var y = yDegrees * MathF.PI / 360f;
            var z = zDegrees * MathF.PI / 360f;
            var sinX = MathF.Sin(x);
            var cosX = MathF.Cos(x);
            var sinY = MathF.Sin(y);
            var cosY = MathF.Cos(y);
            var sinZ = MathF.Sin(z);
            var cosZ = MathF.Cos(z);
            return new Quaternion(
                (sinX * cosY * cosZ) - (cosX * sinY * sinZ),
                (cosX * sinY * cosZ) + (sinX * cosY * sinZ),
                (cosX * cosY * sinZ) - (sinX * sinY * cosZ),
                (cosX * cosY * cosZ) + (sinX * sinY * sinZ));
        }

        public Vector3 Rotate(Vector3 value)
        {
            var xx = x * x;
            var yy = y * y;
            var zz = z * z;
            var xy = x * y;
            var xz = x * z;
            var yz = y * z;
            var wx = w * x;
            var wy = w * y;
            var wz = w * z;
            return new Vector3(
                ((1f - (2f * (yy + zz))) * value.x) + (2f * (xy - wz) * value.y) + (2f * (xz + wy) * value.z),
                (2f * (xy + wz) * value.x) + ((1f - (2f * (xx + zz))) * value.y) + (2f * (yz - wx) * value.z),
                (2f * (xz - wy) * value.x) + (2f * (yz + wx) * value.y) + ((1f - (2f * (xx + yy))) * value.z));
        }
    }

    internal sealed class Material : Object
    {
        private readonly Dictionary<string, Color> _colors = new(StringComparer.Ordinal);
        private readonly HashSet<string> _properties = new(StringComparer.Ordinal);

        public Material(Shader shader)
        {
            this.shader = shader;
            Created.Add(this);
        }

        public Material(Material source)
        {
            shader = source.shader;
            renderQueue = source.renderQueue;
            name = source.name + " (Instance)";
            foreach (var property in source._properties) _properties.Add(property);
            foreach (var pair in source._colors) _colors[pair.Key] = pair.Value;
            Created.Add(this);
        }

        public static List<Material> Created { get; } = new();

        public static List<Material> Destroyed { get; } = new();

        public Shader? shader { get; set; }
        public int renderQueue { get; set; }
        internal bool IsRendererInstance { get; set; }

        public bool HasProperty(string name) => _properties.Contains(name);

        public bool HasColor(string name) => _colors.ContainsKey(name);

        public Color GetColor(string name) => _colors[name];

        public void SetColor(string name, Color color)
        {
            _properties.Add(name);
            _colors[name] = color;
        }

        public void SetNonColorProperty(string name)
        {
            _properties.Add(name);
            _colors.Remove(name);
        }
    }

    internal sealed class Shader : Object
    {
        private static readonly Dictionary<string, Shader> Registered = new(StringComparer.Ordinal);

        public static Shader? Find(string name) => Registered.TryGetValue(name, out var shader) ? shader : null;

        public static void Register(Shader shader) => Registered[shader.name] = shader;

        public static void ClearRegistered() => Registered.Clear();
    }

    internal sealed class MaterialPropertyBlock
    {
        private readonly Dictionary<string, Color> _colors = new(StringComparer.Ordinal);
        private readonly HashSet<string> _properties = new(StringComparer.Ordinal);

        public bool isEmpty => _properties.Count == 0;

        public bool HasProperty(string name) => _properties.Contains(name);

        public bool HasColor(string name) => _colors.ContainsKey(name);

        public Color GetColor(string name) => _colors[name];

        public void SetColor(string name, Color color)
        {
            _properties.Add(name);
            _colors[name] = color;
        }

        public void SetNonColorProperty(string name)
        {
            _properties.Add(name);
            _colors.Remove(name);
        }

        public void CopyFrom(MaterialPropertyBlock? source)
        {
            _properties.Clear();
            _colors.Clear();
            if (source == null) return;
            foreach (var property in source._properties) _properties.Add(property);
            foreach (var pair in source._colors) _colors[pair.Key] = pair.Value;
        }
    }

    internal enum SpriteDrawMode
    {
        Simple,
        Sliced,
        Tiled,
    }

    internal enum SpriteMeshType
    {
        FullRect,
        Tight,
    }

    internal enum TextureFormat
    {
        RGBA32,
    }
}

namespace BepInEx.Logging
{
    internal sealed class ManualLogSource
    {
        public List<string> InfoMessages { get; } = new();

        public void LogInfo(object data)
        {
            InfoMessages.Add(data?.ToString() ?? "");
        }
    }
}

namespace DEYU.UniversalUISystem
{
    using Il2CppInterop.Runtime.InteropTypes.Arrays;
    using UnityEngine;

    internal sealed class UIElementCluster : Component
    {
        private readonly List<Component> _objects = new();

        internal IReadOnlyList<Component> Objects => _objects;

        public Component[]? SerializedObjectsOverride { get; set; }

        public void Add(Component component) => _objects.Add(component);

        public Il2CppArrayBase<T> GetObjects<T>() where T : Component
        {
            var stack = new Stack<T>();
            IEnumerable<Component> serializedObjects = SerializedObjectsOverride ?? _objects.ToArray();
            foreach (var component in serializedObjects)
            {
                if (component is T typed) stack.Push(typed);
            }
            return new Il2CppArrayBase<T>(stack.ToArray());
        }
    }
}

namespace DEYU.Singletons
{
    internal class MonoSingleton<T> : UnityEngine.Object where T : class
    {
        public static T? Instance { get; set; }
    }
}

namespace Il2CppInterop.Runtime
{
    internal static class Il2CppClassPointerStore
    {
        private static readonly Dictionary<Type, IntPtr> Pointers = new();
        private static readonly Dictionary<IntPtr, Type> Types = new();
        private static long _nextPointer = 10_000;

        public static IntPtr GetNativeClassPointer(Type type)
        {
            if (Pointers.TryGetValue(type, out var pointer)) return pointer;
            pointer = new IntPtr(Interlocked.Increment(ref _nextPointer));
            Pointers.Add(type, pointer);
            Types.Add(pointer, type);
            return pointer;
        }

        public static Type? GetManagedType(IntPtr pointer) =>
            Types.TryGetValue(pointer, out var type) ? type : null;
    }

    internal static class Il2CppType
    {
        public static Il2CppSystem.Type From(System.Type type) => new(type);
    }

    internal static class IL2CPP
    {
        public static IntPtr il2cpp_object_get_class(IntPtr pointer)
        {
            var type = UnityEngine.Object.GetNativeType(pointer);
            return type == null
                ? IntPtr.Zero
                : Il2CppClassPointerStore.GetNativeClassPointer(type);
        }

        public static string il2cpp_class_get_name_(IntPtr pointer) =>
            Il2CppClassPointerStore.GetManagedType(pointer)?.Name ?? "";

        public static string il2cpp_class_get_namespace_(IntPtr pointer) =>
            Il2CppClassPointerStore.GetManagedType(pointer)?.Namespace ?? "";
    }
}

namespace Il2CppSystem
{
    internal sealed class Type
    {
        public Type(System.Type managedType) => ManagedType = managedType;

        public System.Type ManagedType { get; }
    }
}

namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    internal class Il2CppArrayBase<T>
    {
        private readonly T[] _items;

        public Il2CppArrayBase(T[] items) => _items = items;

        public int Length => _items.Length;

        public T this[int index] => _items[index];
    }

    internal sealed class Il2CppReferenceArray<T> : IEnumerable<T>
    {
        private readonly T[] _items;

        public Il2CppReferenceArray(int length) => _items = new T[length];

        public T this[int index]
        {
            get => _items[index];
            set => _items[index] = value;
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class Il2CppStructArray<T>
    {
        private readonly T[] _items;

        public Il2CppStructArray(int length) => _items = new T[length];

        public int Length => _items.Length;

        public T this[int index]
        {
            get => _items[index];
            set => _items[index] = value;
        }

        public T[] ToArray() => _items.ToArray();
    }
}

namespace UnityEngine.Tilemaps
{
    using UnityEngine;

    internal class Tile : Object
    {
        public Tile(Sprite sprite) => this.sprite = sprite;

        public Sprite sprite { get; }
    }

    internal sealed class Tilemap : Object
    {
        public Dictionary<Vector3Int, Matrix4x4> Transforms { get; } = new();

        public Matrix4x4 GetTransformMatrix(Vector3Int cellPosition) =>
            Transforms.TryGetValue(cellPosition, out var transform) ? transform : default;
    }
}

namespace NightScene.Tiles
{
    using DEYU.Singletons;
    using UnityEngine;
    using UnityEngine.Tilemaps;

    internal sealed class InteractableTile : Tile
    {
        public InteractableTile(Sprite sprite) : base(sprite)
        {
        }
    }

    internal sealed class TileManager : MonoSingleton<TileManager>
    {
        public Tilemap interactable { get; set; } = new();
        public DEYU.UniversalUISystem.UIElementCluster onSelection { get; set; } = new();
        public Dictionary<int, (InteractableTile Tile, Vector3Int Position)> Desks { get; } = new();

        public InteractableTile? GetCustomerDesk(int deskCode, out Vector3Int position)
        {
            if (Desks.TryGetValue(deskCode, out var desk))
            {
                position = desk.Position;
                return desk.Tile;
            }

            position = default;
            return null;
        }

        public Vector3 GetCellCenterWorld(Vector3Int cellPosition) =>
            new(cellPosition.x + 0.5f, cellPosition.y + 0.5f, cellPosition.z);
    }
}

namespace MystiaStewardCompanion.Save
{
    internal static class RuntimeOrderTraceIdService
    {
        public static bool TryNormalizeTargetTraceId(
            RuntimeUiTargetKind kind,
            string traceId,
            bool enabled,
            out string normalized,
            out string failure)
        {
            var prefix = kind == RuntimeUiTargetKind.Rare ? "R-" : "N-";
            var valid = !enabled || traceId.StartsWith(prefix, StringComparison.Ordinal)
                && traceId.Length is >= 3 and <= 18
                && traceId.AsSpan(2).ToString().All(character => character is >= '0' and <= '9');
            normalized = valid && enabled ? traceId : "";
            failure = valid ? "" : "invalid exact trace";
            return valid;
        }
    }

    internal enum SpriteRendererCastMode
    {
        Exact,
        Reject,
        DifferentPointer,
        WrongNativeClass,
        WrongOwner,
    }

    internal readonly record struct NightBusinessLifecycleSnapshot(bool IsActive, long Generation, int ThreadId);

    internal static class RuntimeNightBusinessLifecycle
    {
        public static NightBusinessLifecycleSnapshot Snapshot { get; set; }
    }

    internal static class RuntimeReflectionUtility
    {
        public static SpriteRendererCastMode SpriteRendererCastMode { get; set; }

        public static Type? FindType(string fullName) => fullName switch
        {
            "NightScene.Tiles.TileManager" => typeof(NightScene.Tiles.TileManager),
            "NightScene.Tiles.InteractableTile" => typeof(NightScene.Tiles.InteractableTile),
            "UnityEngine.SpriteRenderer" => typeof(UnityEngine.SpriteRenderer),
            _ => null,
        };

        public static bool TryReadNativeObjectPointer(object? target, out nint pointer)
        {
            pointer = target is UnityEngine.Object unityObject ? unityObject.Pointer : IntPtr.Zero;
            return pointer != IntPtr.Zero;
        }

        public static object? TryCastRuntimeObject(object? value, string targetTypeName)
        {
            if (targetTypeName != "UnityEngine.SpriteRenderer")
            {
                return value?.GetType().FullName == targetTypeName ? value : null;
            }
            if (value is UnityEngine.SpriteRenderer exact) return exact;
            if (value is not UnityEngine.Component wrapper
                || UnityEngine.Object.GetNativeType(wrapper.Pointer) != typeof(UnityEngine.SpriteRenderer)
                || wrapper.gameObject.FindNativeComponent(
                    wrapper.Pointer,
                    typeof(UnityEngine.SpriteRenderer)) is not UnityEngine.SpriteRenderer renderer)
            {
                return null;
            }

            return SpriteRendererCastMode switch
            {
                SpriteRendererCastMode.Exact => renderer,
                SpriteRendererCastMode.Reject => null,
                SpriteRendererCastMode.DifferentPointer => new UnityEngine.SpriteRenderer(),
                SpriteRendererCastMode.WrongNativeClass => new UnityEngine.SpriteRenderer(
                    renderer.gameObject,
                    renderer.Pointer,
                    typeof(UnityEngine.Component)),
                SpriteRendererCastMode.WrongOwner => new UnityEngine.SpriteRenderer(
                    new UnityEngine.GameObject(),
                    renderer.Pointer,
                    typeof(UnityEngine.SpriteRenderer)),
                _ => null,
            };
        }
    }
}
