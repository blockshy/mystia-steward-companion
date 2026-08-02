namespace UnityEngine
{
    internal class Object
    {
        private static long _nextPointer = 1;
        private IntPtr _cachedPtr;

        public Object()
        {
            Pointer = new IntPtr(Interlocked.Increment(ref _nextPointer));
            _cachedPtr = Pointer;
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

        public static List<GameObject> Instantiated { get; } = new();

        public static List<GameObject> Destroyed { get; } = new();

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
                    cloneCluster.GetObjectsOverride = sourceCluster.GetObjectsOverride;
                    cloneCluster.PrimaryOverride = sourceCluster.PrimaryOverride;
                    foreach (var sourceObject in sourceCluster.Objects)
                    {
                        if (componentMap.TryGetValue(sourceObject, out var cloneObject)) cloneCluster.Add(cloneObject);
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
            clone.transform.PointScale = source.transform.PointScale;
            clone.transform.QuarterTurnsZ = source.transform.QuarterTurnsZ;
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

        public GameObject gameObject { get; internal set; }

        public Transform transform => gameObject.transform;
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

        public Vector3 position
        {
            get => parent == null ? localPosition : Add(parent.position, localPosition);
            set => localPosition = parent == null ? value : Subtract(value, parent.position);
        }

        public Vector3 PointScale { get; set; } = new(1f, 1f, 1f);

        public int QuarterTurnsZ { get; set; }

        public Vector3 TransformPoint(Vector3 point)
        {
            var scaledX = point.x * PointScale.x;
            var scaledY = point.y * PointScale.y;
            var turns = ((QuarterTurnsZ % 4) + 4) % 4;
            var (rotatedX, rotatedY) = turns switch
            {
                1 => (-scaledY, scaledX),
                2 => (-scaledX, -scaledY),
                3 => (scaledY, -scaledX),
                _ => (scaledX, scaledY),
            };
            var local = new Vector3(rotatedX, rotatedY, point.z * PointScale.z);
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

    internal sealed class Sprite : Object
    {
        public Sprite() : this(new Bounds(default, new Vector3(1f, 1f, 0f)))
        {
        }

        public Sprite(Bounds bounds) => this.bounds = bounds;

        public Bounds bounds { get; }
    }

    internal sealed class SpriteRenderer : Component
    {
        public bool enabled { get; set; }

        public Sprite? sprite { get; set; }

        public SpriteDrawMode drawMode { get; set; } = SpriteDrawMode.Simple;

        public bool flipX { get; set; }

        public bool flipY { get; set; }

        public bool isVisible { get; set; } = true;

        public int sortingLayerID { get; set; }

        public int sortingOrder { get; set; }

        public Color color { get; set; } = new(1f);

        public Material? sharedMaterial { get; set; }

        public bool AutoBounds { get; set; }

        public Bounds StaticBounds { get; set; } = new(default, new Vector3(1f, 1f, 0f));

        public Bounds bounds
        {
            get => AutoBounds && sprite != null
                ? new Bounds(transform.TransformPoint(sprite.bounds.center), StaticBounds.size)
                : StaticBounds;
            set => StaticBounds = value;
        }
    }

    internal static class Time
    {
        public static float realtimeSinceStartup { get; set; }
    }

    internal readonly record struct Vector3(float x, float y, float z);
    internal readonly record struct Vector3Int(int x, int y, int z);
    internal readonly record struct Bounds(Vector3 center, Vector3 size);
    internal readonly record struct Matrix4x4(float m03, float m13, float m23);
    internal readonly record struct Color(float a);

    internal sealed class Material : Object
    {
        public Shader? shader { get; set; }
        public int renderQueue { get; set; }
    }

    internal sealed class Shader : Object
    {
    }

    internal enum SpriteDrawMode
    {
        Simple,
        Sliced,
        Tiled,
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

        public SpriteRenderer[]? GetObjectsOverride { get; set; }

        public SpriteRenderer? PrimaryOverride { get; set; }

        public void Add(Component component) => _objects.Add(component);

        public T? GetObject<T>(int skip) where T : Component
        {
            if (typeof(T) == typeof(SpriteRenderer) && PrimaryOverride != null)
            {
                return (T)(Component)PrimaryOverride;
            }
            foreach (var component in _objects)
            {
                if (component is not T typed) continue;
                if (skip-- > 0) continue;
                return typed;
            }

            return null;
        }

        public Il2CppArrayBase<T> GetObjects<T>() where T : Component
        {
            if (typeof(T) == typeof(SpriteRenderer) && GetObjectsOverride != null)
            {
                return new Il2CppArrayBase<T>(GetObjectsOverride.Cast<T>().ToArray());
            }
            return new Il2CppArrayBase<T>(_objects.OfType<T>().ToArray());
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
    internal static class Il2CppType
    {
        public static Type From(Type type) => type;
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
}

namespace Il2CppSystem.Collections.Generic
{
    internal class Dictionary<TKey, TValue> : System.Collections.Generic.Dictionary<TKey, TValue>
        where TKey : notnull
    {
        public new bool ContainsKey(TKey key) => base.ContainsKey(key);

        public new TValue this[TKey key]
        {
            get => base[key];
            set => base[key] = value;
        }
    }

    internal class List<T> : System.Collections.Generic.List<T>
    {
        public bool ThrowOnCountRead { get; set; }

        public new int Count => ThrowOnCountRead
            ? throw new InvalidOperationException("stale worker count getter")
            : base.Count;
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

    internal sealed class StencilPainterController : Component
    {
        public static int ShowCount { get; set; }
        public static Vector3 LastPosition { get; set; }
        public static bool EnableFirstWorkerOnShow { get; set; } = true;
        public static StencilPainterController? LastController { get; set; }
        public static bool ThrowOnShow { get; set; }

        public Il2CppSystem.Collections.Generic.List<SpriteRenderer> worker { get; set; } = new();

        public void Show(Vector3 coordinate, Sprite visual)
        {
            if (ThrowOnShow) throw new InvalidOperationException("native Show failed");
            if (visual.m_CachedPtr == IntPtr.Zero) throw new InvalidOperationException("Destroyed visual.");
            for (var index = 0; index < worker.Count; index += 1)
            {
                worker[index].sprite = visual;
                if (index == 0 && EnableFirstWorkerOnShow) worker[index].enabled = true;
            }
            transform.position = coordinate;
            ShowCount += 1;
            LastPosition = coordinate;
            LastController = this;
        }
    }

    internal sealed class TileManager : MonoSingleton<TileManager>
    {
        public GameObject stencilPainterParent { get; set; } = new();
        public Transform stencilPainterField { get; set; } = new();
        public Il2CppSystem.Collections.Generic.Dictionary<Sprite, Sprite> interactablesHighlightedVisual { get; set; } = new();
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
    internal readonly record struct NightBusinessLifecycleSnapshot(bool IsActive, long Generation, int ThreadId);

    internal static class RuntimeNightBusinessLifecycle
    {
        public static NightBusinessLifecycleSnapshot Snapshot { get; set; }
    }

    internal static class RuntimeReflectionUtility
    {
        public static Type? FindType(string fullName) => fullName switch
        {
            "NightScene.Tiles.TileManager" => typeof(NightScene.Tiles.TileManager),
            "NightScene.Tiles.InteractableTile" => typeof(NightScene.Tiles.InteractableTile),
            "NightScene.Tiles.StencilPainterController" => typeof(NightScene.Tiles.StencilPainterController),
            _ => null,
        };

        public static bool TryReadNativeObjectPointer(object? target, out nint pointer)
        {
            pointer = target is UnityEngine.Object unityObject ? unityObject.Pointer : IntPtr.Zero;
            return pointer != IntPtr.Zero;
        }

        public static object? TryCastRuntimeObject(object? value, string targetTypeName) =>
            value?.GetType().FullName == targetTypeName ? value : null;
    }
}
