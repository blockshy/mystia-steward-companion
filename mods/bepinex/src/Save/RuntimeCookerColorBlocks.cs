using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 对厨具摆放区的原生选区形状分区着色。显示层复用该格 Tile.sprite 的原网格和纹理透明度，
/// 不再绘制离散矩形，也不写入原生 Tile、厨具、蒸汽、成品或共享材质。所有操作只在主线程执行。
/// </summary>
internal sealed class RuntimeCookerColorBlocks
{
    private const string ObjectName = "MystiaStewardCompanion.CookerOrderColors";
    // 当前桌面精灵宽约50像素、格距48像素；两侧各4%的暗边在相邻格重叠后形成约2像素接缝。
    // 比例限制在原轮廓内，随画面缩放同步，不另绘制跨桌面或遮住锅体的悬浮线条。
    private const float EdgeSeparatorFraction = .04f;
    private readonly List<BackgroundBandVertex> _input = new();
    private readonly List<int> _indices = new();
    private readonly List<BackgroundBandVertex> _output = new();
    private readonly RuntimeTargetHighlightColor[] _colors = new RuntimeTargetHighlightColor[8];
    private GameObject? _root;
    private Mesh? _mesh;
    private MeshRenderer? _renderer;
    private Material? _material;
    private RuntimeCookerAlphaMask.Lease? _mask;
    // 白色遮罩是显示增强而非高亮前提；读回失败时继续使用已确证可绘制的原纹理和原 UV。
    private bool _usesOriginalTexture;
    private string _maskStatus = "unbound";
    // 原图集仅在主线程借用，属于游戏资产而非本 Mod；缓存可避免热帧 material.mainTexture 包装分配。
    private Texture? _borrowedTexture;
    private nint _spritePointer;
    private nint _texturePointer;
    private Matrix4x4 _geometry;
    private int _colorCount = -1;
    // 仅网格实际上传时生成，目录诊断复用纯文本；不为正常热帧引入额外 Unity 属性读取。
    internal string GeometryStatus { get; private set; } = "mesh:not-uploaded";

    internal bool IsAlive => _root != null && _mesh != null && _renderer != null && _material != null
        && _borrowedTexture != null && (_usesOriginalTexture || _mask?.IsAlive == true);

    /// <summary>只创建 Mod 自有 MeshRenderer，不克隆原生交互节点，不添加碰撞体或输入组件。</summary>
    internal static RuntimeCookerColorBlocks Create(Transform anchor)
    {
        var result = new RuntimeCookerColorBlocks();
        try
        {
            var types = new Il2CppReferenceArray<Il2CppSystem.Type>(2);
            types[0] = Il2CppType.From(typeof(MeshFilter));
            types[1] = Il2CppType.From(typeof(MeshRenderer));
            result._root = new GameObject(ObjectName, types);
            result._root.SetActive(false);
            // anchor 必须是精确的 m_CookerSpriteRenderer.transform，而非 CookAnimator 根。
            // 这样背景和锅体始终继承同一个最近 SortingGroup，原生按世界 Y 更新整组排序时同步生效。
            // 保留单位世界变换，避免源图标(-0.5,-0.5)偏移重复应用；最终位置由原生桌格与模板决定。
            result._root.transform.SetParent(anchor, true);
            result._renderer = RuntimeReflectionUtility.TryCastRuntimeObject(result._root.GetComponent(types[1]),
                "UnityEngine.MeshRenderer") as MeshRenderer ?? throw new InvalidOperationException("cooker background renderer unavailable");
            var filter = RuntimeReflectionUtility.TryCastRuntimeObject(result._root.GetComponent(types[0]),
                "UnityEngine.MeshFilter") as MeshFilter ?? throw new InvalidOperationException("cooker background mesh filter unavailable");
            result._mesh = new Mesh { name = ObjectName + ".Mesh" };
            filter.sharedMesh = result._mesh;
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) throw new InvalidOperationException("cooker background sprite shader unavailable");
            result._material = new Material(shader) { name = ObjectName + ".Material" };
            // 普通 MeshRenderer 不会自动填写 SpriteRenderer 专用颜色；显式使用单位乘数保留网格顶点色。
            result._material.SetColor("_Color", Color.white);
            result._material.SetColor("_RendererColor", Color.white);
            result._material.SetVector("_Flip", new Vector4(1, 1, 1, 1));
            result._renderer.sharedMaterial = result._material;
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>
    /// 仅当原图、原模板变换或颜色改变时重建分带网格；普通帧与不变的目录复核均不上传网格。
    /// 按原图完整横向范围左右等分，每色贯穿外露桌沿，避免顶部横条被锅体全部遮住。
    /// 透明轮廓来自只读裁剪的原图 alpha，RGB 归白避免棕色桌纹压暗选色；颜色变化不重新读回纹理。
    /// </summary>
    internal void Apply(RuntimeUiTargetKinds claims, RuntimeTargetHighlightPalette palette, RuntimeCookerBackgroundSurface surface)
    {
        if (_root == null || _mesh == null || _renderer == null || _material == null)
            throw new InvalidOperationException("cooker background resources destroyed");
        var source = surface.Sprite;
        if (source == null) throw new InvalidOperationException("cooker background source destroyed");
        var texture = source.texture;
        if (texture == null) throw new InvalidOperationException("cooker background texture destroyed");
        Span<RuntimeTargetHighlightColor> colors = stackalloc RuntimeTargetHighlightColor[8];
        var count = RuntimeCookerColorBlockLayout.CollectColors(claims, palette, colors);
        var changed = count != _colorCount || source.Pointer != _spritePointer || texture.Pointer != _texturePointer
            || surface.Geometry != _geometry || (!_usesOriginalTexture && _mask?.IsAlive != true);
        for (var index = 0; index < count; index++) changed |= colors[index] != _colors[index];
        if (changed)
        {
            var vertices = source.vertices;
            var triangles = source.triangles;
            var uv = source.uv;
            if (vertices.Length is < 3 or > 4096 || uv.Length != vertices.Length
                || triangles.Length is < 3 or > 12288 || triangles.Length % 3 != 0)
                throw new InvalidOperationException("cooker background source mesh invalid");
            if (source.Pointer != _spritePointer || texture.Pointer != _texturePointer
                || (!_usesOriginalTexture && _mask?.IsAlive != true))
            {
                var minimumUv = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                var maximumUv = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                for (var index = 0; index < uv.Length; index++)
                {
                    minimumUv = new Vector2(MathF.Min(minimumUv.x, uv[index].x), MathF.Min(minimumUv.y, uv[index].y));
                    maximumUv = new Vector2(MathF.Max(maximumUv.x, uv[index].x), MathF.Max(maximumUv.y, uv[index].y));
                }
                // 同一图集裁剪范围由所有厨具共享，只在首次绑定读回小区域；先取得新租约再释放旧租约。
                var acquired = RuntimeCookerAlphaMask.TryAcquireCached(texture, minimumUv, maximumUv,
                    out var nextMask, out var maskFailure);
                var previousMask = _mask;
                _mask = nextMask;
                _usesOriginalTexture = !acquired;
                _maskStatus = acquired ? "white-alpha" : "source-fallback:" + maskFailure;
                previousMask?.Dispose();
            }
            // 原纹理回退沿用同一原始三角轮廓；不填实透明区域、不改变排序，也不清除其它厨具。
            // 失败键在整个经营会话内记忆，目标刷新或短暂无订单不会再次触发昂贵的 GPU 读回。
            var displayTexture = _mask?.Texture ?? texture;
            _input.Clear(); _indices.Clear();
            for (var index = 0; index < vertices.Length; index++)
            {
                var position = surface.Geometry.MultiplyPoint3x4(new Vector3(vertices[index].x, vertices[index].y, 0));
                var maskUv = _mask?.MapUv(uv[index]) ?? uv[index];
                // 约86%的覆盖使浅色和深色都能辨认；原桌纹透过剩余透明度显示，孔洞和轮廓原样保留。
                _input.Add(new(position, Vector3.back, new Vector4(1, 0, 0, 1), new Color32(255, 255, 255, 220),
                    new Vector4(maskUv.x, maskUv.y, 0, 0), Vector4.zero, Vector4.zero, Vector4.zero));
            }
            for (var index = 0; index < triangles.Length; index++) _indices.Add(triangles[index]);
            if (!RuntimeBackgroundBandMesh.TryBuild(_input, _indices, colors[..count], _output, out var failure,
                BackgroundBandDirection.LeftToRight, EdgeSeparatorFraction))
                throw new InvalidOperationException("cooker background bands invalid: " + failure);
            var positions = new Il2CppStructArray<Vector3>(_output.Count);
            var texcoords = new Il2CppStructArray<Vector2>(_output.Count);
            var vertexColors = new Il2CppStructArray<Color32>(_output.Count);
            var outputIndices = new Il2CppStructArray<int>(_output.Count);
            var minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (var index = 0; index < _output.Count; index++)
            {
                var vertex = _output[index];
                positions[index] = vertex.Position;
                texcoords[index] = new Vector2(vertex.Uv0.x, vertex.Uv0.y);
                vertexColors[index] = vertex.Color;
                outputIndices[index] = index;
                minimum = new Vector3(MathF.Min(minimum.x, vertex.Position.x), MathF.Min(minimum.y, vertex.Position.y), MathF.Min(minimum.z, vertex.Position.z));
                maximum = new Vector3(MathF.Max(maximum.x, vertex.Position.x), MathF.Max(maximum.y, vertex.Position.y), MathF.Max(maximum.z, vertex.Position.z));
            }
            _mesh.Clear();
            _mesh.vertices = positions; _mesh.uv = texcoords; _mesh.colors32 = vertexColors; _mesh.triangles = outputIndices;
            _mesh.RecalculateBounds();
            _material.mainTexture = displayTexture;
            _borrowedTexture = texture;
            _spritePointer = source.Pointer; _texturePointer = texture.Pointer; _geometry = surface.Geometry;
            _colorCount = count;
            for (var index = 0; index < count; index++) _colors[index] = colors[index];
            GeometryStatus = $"mesh:{_output.Count}v/{count}bands/axis:x/edge:0.04/alpha:220/localBounds:"
                + $"{minimum.x:F3},{minimum.y:F3},{minimum.z:F3}..{maximum.x:F3},{maximum.y:F3},{maximum.z:F3}"
                + $"/texture:{texture.width}x{texture.height}/sample:{displayTexture.width}x{displayTexture.height}/mode:{_maskStatus}";
        }
        _root.layer = surface.Layer;
        _root.transform.position = surface.Position;
        _renderer.sortingLayerID = surface.SortingLayer;
        _renderer.sortingOrder = surface.SortingOrder;
        _root.SetActive(true);
    }

    /// <summary>只销毁自有根、网格和材质并归还遮罩租约；最后一个租约释放自有遮罩，原图集不变。</summary>
    internal void Dispose()
    {
        try { if (_root != null) _root.SetActive(false); } catch { }
        SafeDestroy(_root); SafeDestroy(_mesh); SafeDestroy(_material);
        _mask?.Dispose(); _mask = null;
        _root = null; _mesh = null; _renderer = null; _material = null; _borrowedTexture = null;
        _input.Clear(); _indices.Clear(); _output.Clear();
    }

    private static void SafeDestroy(UnityEngine.Object? owned)
    { try { if (owned != null) UnityEngine.Object.Destroy(owned); } catch { /* 场景析构后不回写原对象。 */ } }
}
