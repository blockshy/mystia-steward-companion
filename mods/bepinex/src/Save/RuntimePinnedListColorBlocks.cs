using Il2CppInterop.Runtime;
using UnityEngine;
using Image = MystiaStewardCompanion.Save.RuntimePinnedListImage;
using Selectable = MystiaStewardCompanion.Save.RuntimePinnedListButton;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 精确列表背景的原生网格分色租约。名称沿用历史 helper，但实现不再创建色块子树：
/// 在 Image 完成 Simple/Sliced/Tiled/Filled 网格后，仅裁剪并染色原三角形。
/// 原材质、Sprite、透明边缘、九宫格 UV、图标及输入行为始终由游戏维护。
/// </summary>
internal sealed class RuntimePinnedListColorBlocks
{
    private readonly Selectable _button;
    private readonly Image _source;
    private readonly RectTransform _anchor;
    private readonly Image _icon;
    private readonly nint _iconPointer, _spritePointer, _buttonPointer, _imagePointer, _anchorPointer;
    private readonly RuntimeTargetHighlightColor[] _colors = new RuntimeTargetHighlightColor[8];
    private readonly List<BackgroundBandVertex> _originalVertices = new();
    private readonly List<int> _originalIndices = new();
    private readonly List<BackgroundBandVertex> _coloredVertices = new();
    private int _colorCount = -1;
    private bool _disposed, _applyingMesh, _hasOriginalMesh;

    private RuntimePinnedListColorBlocks(Selectable button, Image source, RectTransform anchor, Image icon, Sprite sprite)
    {
        _button = button; _source = source; _anchor = anchor; _icon = icon;
        _iconPointer = Pointer(icon); _spritePointer = Pointer(sprite); _buttonPointer = Pointer(button);
        _imagePointer = Pointer(source); _anchorPointer = Pointer(anchor);
    }

    /// <summary>
    /// Native 料理/食材/酒水绑定回调明确使用 button.image 与 Text.Visual；只绑定该确切
    /// 背景和 cluster 声明的独立图标，不按名称、索引、父场景扫描猜测 UI。
    /// </summary>
    internal static RuntimePinnedListColorBlocks? TryCreate(object buttonObject, object imageObject,
        object clusterObject, object? expectedSpriteObject, out string failure)
    {
        failure = "native button/image type unavailable";
        var button = Selectable.TryBind(buttonObject);
        var source = Image.TryBind(imageObject);
        if (button == null || source == null || Pointer(button) == 0 || Pointer(source) == 0) return null;
        var anchor = source.rectTransform;
        failure = "native background is not the exact button rectangle";
        if (anchor == null || Pointer(anchor) == 0 || Pointer(anchor) != Pointer(button.transform)
            || Pointer(button.image) != Pointer(source)) return null;
        failure = "current item visual or declared cluster unavailable";
        var expectedSprite = RuntimeReflectionUtility.TryCastRuntimeObject(expectedSpriteObject, "UnityEngine.Sprite") as Sprite;
        var cluster = RuntimeReflectionUtility.TryCastRuntimeObject(clusterObject, "UnityEngine.Component") as Component;
        if (expectedSprite == null || cluster == null) return null;
        Image? icon = null;
        var members = RuntimeReflectionUtility.GetMemberValue(clusterObject, "allElements");
        if (members == null) return null;
        foreach (var member in RuntimeReflectionUtility.EnumerateObjects(members))
        {
            var element = RuntimeReflectionUtility.TryCastRuntimeObject(member, "UnityEngine.Transform") as Transform;
            if (element == null) continue;
            var candidate = Image.TryBind(element.gameObject.GetComponent(
                Il2CppType.From(RuntimePinnedListUiAccess.Type("UnityEngine.UI.Image"))));
            if (candidate == null || Pointer(candidate.sprite) != Pointer(expectedSprite)) continue;
            failure = "current item icon is ambiguous or overlaps the native background";
            if (icon != null || Pointer(candidate) == Pointer(source) || !candidate.transform.IsChildOf(anchor)) return null;
            icon = candidate;
        }
        failure = "current item icon not found in the declared cluster";
        if (icon == null) return null;
        failure = "";
        return new RuntimePinnedListColorBlocks(button, source, anchor, icon, expectedSprite);
    }

    /// <summary>
    /// 原 Image 自己处理 alpha、enabled、布局及裁剪，无须另建 CanvasGroup 或修改颜色。
    /// 此处只确认租约仍指向同一背景和同一条数据的独立图标。
    /// </summary>
    internal bool RefreshVisibility() => !_disposed && _button.Component != null && _source.Component != null
        && _anchor != null && _icon.Component != null && Pointer(_button) == _buttonPointer && Pointer(_source) == _imagePointer
        && Pointer(_anchor) == _anchorPointer && Pointer(_button.image) == _imagePointer
        && Pointer(_source.rectTransform) == _anchorPointer && Pointer(_button.transform) == _anchorPointer
        && Pointer(_icon) == _iconPointer && Pointer(_icon.sprite) == _spritePointer && _icon.transform.IsChildOf(_anchor);

    /// <summary>
    /// 普通帧只比较至多八个纯值颜色。目标变化才标记原 Image 顶点脏，由 Unity 既有
    /// Canvas 更新统一处理，不主动调用渲染、不创建对象，也不分配逐帧托管数组。
    /// </summary>
    internal bool Apply(RuntimeUiTargetKinds claims, RuntimeTargetHighlightPalette palette)
    {
        if (!RefreshVisibility()) return false;
        Span<RuntimeTargetHighlightColor> colors = stackalloc RuntimeTargetHighlightColor[8];
        var count = CollectColors(claims, palette, colors);
        var changed = _colorCount != count;
        for (var index = 0; index < count; index++) changed |= _colors[index] != colors[index];
        if (changed)
        {
            for (var index = 0; index < count; index++) _colors[index] = colors[index];
            _colorCount = count;
            _source.SetVerticesDirty();
        }
        return true;
    }

    /// <summary>
    /// 仅由登记背景的 Image.OnPopulateMesh postfix 调用。参数使用本次重新读取的 claims，
    /// 避免 Canvas 重建时使用 Tick 尚未更新的颜色。原网格完整备份并完成裁剪后才写入；
    /// 失败保留或恢复原数据。不保存共享 VertexHelper，也不在回调内再次标记顶点脏。
    /// </summary>
    internal bool ApplyToNativeMesh(object vertexHelper, RuntimeUiTargetKinds claims,
        RuntimeTargetHighlightPalette palette, out string failure)
    {
        failure = "native background lease unavailable";
        if (!RefreshVisibility() || _applyingMesh) return false;
        Span<RuntimeTargetHighlightColor> colors = stackalloc RuntimeTargetHighlightColor[8];
        var count = CollectColors(claims, palette, colors);
        _hasOriginalMesh = false;
        if (count == 0) { failure = ""; return true; }
        _applyingMesh = true;
        RuntimePinnedListMeshAccess? access = null;
        var writing = false;
        try
        {
            access = new RuntimePinnedListMeshAccess(vertexHelper);
            access.Read(_originalVertices, _originalIndices);
            _hasOriginalMesh = true;
            // Filled=0 或折叠布局可以合法产生空网格；保持登记，下次原生重建恢复后继续着色。
            if (_originalVertices.Count == 0) { failure = ""; return true; }
            // 料理、食材、酒水统一按订单生命周期从左到右等宽分区，与厨具台面的阅读顺序一致。
            // 仅选择裁剪方向，原生网格轮廓、图集 UV、alpha 和中心物品图标仍保持原有行为。
            if (!RuntimeBackgroundBandMesh.TryBuild(_originalVertices, _originalIndices, colors[..count], _coloredVertices,
                out failure, BackgroundBandDirection.LeftToRight))
                return false;
            writing = true;
            access.Write(_coloredVertices, null);
            failure = "";
            return true;
        }
        catch (Exception ex)
        {
            failure = "native background tint failed: " + ex.GetBaseException().Message;
            if (writing && access != null)
            {
                try { access.Write(_originalVertices, _originalIndices); }
                catch (Exception restore) { failure += "; restore failed: " + restore.GetBaseException().Message; }
            }
            return false;
        }
        finally { _applyingMesh = false; }
    }

    /// <summary>
    /// 写入回调内若发生目标撤回，调用方在同一栈中恢复本次纯值快照。正常退休只请求原生
    /// 重建；这里不把上一幅图的 VertexHelper 或引擎对象留到下一帧。
    /// </summary>
    internal bool RestoreNativeMesh(object vertexHelper, out string failure)
    {
        failure = "";
        if (!_hasOriginalMesh) return true;
        try
        {
            new RuntimePinnedListMeshAccess(vertexHelper).Write(_originalVertices, _originalIndices);
            _hasOriginalMesh = false;
            return true;
        }
        catch (Exception ex) { failure = "native background restore failed: " + ex.GetBaseException().Message; return false; }
    }

    private static int CollectColors(RuntimeUiTargetKinds claims, RuntimeTargetHighlightPalette palette,
        Span<RuntimeTargetHighlightColor> colors)
        // 与厨具共用显示顺序；槽号只用于声明匹配，新单复用槽后不会重新插到旧位置。
        => palette.CollectDisplayColors(claims, colors);

    private static nint Pointer(object? value)
    {
        var native = value switch
        {
            Image image => image.Component,
            Selectable button => button.Component,
            UnityEngine.Object unityObject => unityObject,
            _ => null,
        };
        return native == null ? 0 : native.Pointer;
    }

    /// <summary>
    /// 没有私有对象或材质需要销毁。移除登记后标记原背景重建，Unity 会按当前 Sprite、
    /// 布局和原色生成正常网格；不把绑定时的旧颜色回写到已变化的原生状态。
    /// </summary>
    internal void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_source.Component != null && Pointer(_source) == _imagePointer) _source.SetVerticesDirty();
    }
}
