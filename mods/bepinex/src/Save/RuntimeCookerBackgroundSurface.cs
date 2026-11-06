using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 一次主线程扫描得到的厨具摆放区视觉描述。只读取精确交互格及原生选区模板，
/// 不替换 Tile 资产，不依赖节点名称，也不把厨具、火光或成品图片当作背景。
/// </summary>
internal sealed record RuntimeCookerBackgroundSurface(Sprite Sprite, Vector3 Position, Matrix4x4 Geometry,
    int Layer, int SortingLayer, int SortingOrder, nint TemplatePointer, string SortingContext)
{
    internal static RuntimeCookerBackgroundSurface Read(RuntimeCookerGridPosition position, SpriteRenderer cooker)
    {
        var managerType = RuntimeReflectionUtility.FindType("NightScene.Tiles.TileManager")
            ?? throw new InvalidOperationException("cooker tile manager type unavailable");
        var tileType = RuntimeReflectionUtility.FindType("NightScene.Tiles.InteractableTile")
            ?? throw new InvalidOperationException("cooker interactable tile type unavailable");
        var manager = RuntimeReflectionUtility.GetStaticMemberValue(managerType, "Instance")
            ?? throw new InvalidOperationException("cooker tile manager unavailable");
        var point = new Vector3Int(position.X, position.Y, position.Z);
        var readTile = managerType.GetMethod("GetInteractableType", BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(Vector3Int), tileType.MakeByRefType() }, null)
            ?? throw new InvalidOperationException("cooker tile lookup unavailable");
        object?[] arguments = { point, null };
        var kind = readTile.Invoke(manager, arguments);
        // metadata 与 Native 0x52B010 共同确认：返回值直接来自该格 InteractableTile.type；0 为 CookDesk。
        // 不能因为目标目录中存在同坐标就对普通桌子、墙面或已经更换的图块绘制。
        if (kind == null || Convert.ToInt32(kind) != 0 || arguments[1] == null || !tileType.IsInstanceOfType(arguments[1]))
            throw new InvalidOperationException("cooker position is not a CookDesk tile");
        var tile = arguments[1]!;
        var sprite = RuntimeReflectionUtility.GetMemberValue(tile, "sprite") as Sprite
            ?? throw new InvalidOperationException("cooker placement sprite unavailable");
        if (sprite == null || sprite.Pointer == IntPtr.Zero) throw new InvalidOperationException("cooker placement sprite destroyed");
        var map = RuntimeReflectionUtility.GetMemberValue(manager, "interactable")
            ?? throw new InvalidOperationException("cooker interactable tilemap unavailable");
        if (map.GetType().FullName != "UnityEngine.Tilemaps.Tilemap")
            throw new InvalidOperationException("cooker tilemap type mismatch");
        var mapComponent = RuntimeReflectionUtility.TryCastRuntimeObject(map, "UnityEngine.Component") as Component
            ?? throw new InvalidOperationException("cooker tilemap component unavailable");
        var mapRendererType = RuntimeReflectionUtility.FindType("UnityEngine.Tilemaps.TilemapRenderer")
            ?? throw new InvalidOperationException("cooker tilemap renderer type unavailable");
        var mapRendererObject = mapComponent.gameObject.GetComponent(Il2CppType.From(mapRendererType));
        var mapRenderer = RuntimeReflectionUtility.TryCastRuntimeObject(mapRendererObject, "UnityEngine.Renderer") as Renderer
            ?? throw new InvalidOperationException("cooker tilemap renderer unavailable");
        var center = Invoke<Vector3>(map, "GetCellCenterWorld", point);
        var tileMatrix = Invoke<Matrix4x4>(map, "GetTransformMatrix", point);
        // Native UpdateCurser(0x52E1D0)使用格中心加图块矩阵的平移分量；不把旋转/缩放再应用一次。
        var world = new Vector3(center.x + tileMatrix.m03, center.y + tileMatrix.m13, center.z + tileMatrix.m23);
        var selection = RuntimeReflectionUtility.GetMemberValue(manager, "onSelection");
        if (selection is not Component component || component == null)
            throw new InvalidOperationException("cooker selection template unavailable");
        var getObjects = selection.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == "GetObjects" && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 0)
            ?? throw new InvalidOperationException("cooker selection renderer accessor unavailable");
        var members = getObjects.MakeGenericMethod(typeof(SpriteRenderer)).Invoke(selection, Array.Empty<object>());
        SpriteRenderer? regional = null;
        var outlineCount = 0;
        var count = 0;
        foreach (var item in RuntimeReflectionUtility.EnumerateObjects(members))
        {
            if (RuntimeReflectionUtility.TryCastRuntimeObject(item, "UnityEngine.SpriteRenderer") is not SpriteRenderer renderer
                || renderer == null || !renderer.transform.IsChildOf(component.transform))
                throw new InvalidOperationException("cooker selection renderer identity mismatch");
            count++;
            var shader = renderer.sharedMaterial?.shader;
            if (shader == null) throw new InvalidOperationException("cooker selection shader unavailable");
            if (shader.name == "THIZKY/Effects/OutlineBlinkOnly") outlineCount++;
            else if (shader.name == "THIZKY/Effects/RegionalHSVFillter" && regional == null) regional = renderer;
            else throw new InvalidOperationException("cooker selection renderer is ambiguous");
        }
        if (count != 2 || outlineCount != 1 || regional == null || regional.drawMode != SpriteDrawMode.Simple)
            throw new InvalidOperationException("cooker selection template does not match verified surface");
        // 不读取模板当前 sprite：玩家选中有厨具的桌格时，游戏会临时把它换成厨具图标。
        // 始终使用本次 CookDesk 原图并复制模板变换；排序在下方单独验证，不复制描边闪烁材质。
        var rootPosition = component.transform.position;
        var relative = Matrix4x4.Translate(new Vector3(-rootPosition.x, -rootPosition.y, -rootPosition.z))
            * regional.transform.localToWorldMatrix;
        var flip = Matrix4x4.Scale(new Vector3(regional.flipX ? -1 : 1, regional.flipY ? -1 : 1, 1));
        if (!Finite(world) || !Finite(relative)) throw new InvalidOperationException("cooker placement geometry is invalid");
        // Work.unity 的精确 CookAnimator 模板为根 → CookerLayout(SortingGroup) → m_CookerSpriteRenderer。
        // LayerSortingController.GroupSortingMethod(0x3E8250)每帧根据世界 Y 更新整组排序；
        // 该 SpriteRenderer 的 order=0 只是组内顺序，不能拿它与组外 Tilemap 的 order 比较。
        // 显示对象必须挂到该 renderer.transform 下，才可以用 order-1 稳定处于同组厨具后方。
        // 保留最小值检查，防止未来资源使减一溢出；不复制原生特殊混合材质或修改游戏的排序组。
        if (cooker.sortingOrder <= -32768) throw new InvalidOperationException("cooker has no lower placement sorting slot");
        var order = cooker.sortingOrder - 1;
        var layer = cooker.sortingLayerID;
        var context = $"source:{cooker.Pointer:x}:{layer}:{cooker.sortingOrder}"
            + $"/tile:{mapRenderer.sortingLayerID}:{mapRenderer.sortingOrder}"
            + ReadParentSortingContext(cooker.transform);
        return new(sprite, world, relative * flip, regional.gameObject.layer, layer, order, regional.Pointer, context);
    }

    /// <summary>
    /// 只读取已由 m_CookerSpriteRenderer 精确定位的直接父级；此诊断不做场景搜索，也不参与排序决策。
    /// 保存纯文本而非游戏包装，便于后续实机日志区分“网格已创建”和“实际位于哪个排序组”。
    /// 即使未来资源调整了组的位置，父子绑定仍自然继承最近排序组，不以诊断缺项猜测或改写场景。
    /// </summary>
    private static string ReadParentSortingContext(Transform source)
    {
        var parent = source.parent;
        var type = RuntimeReflectionUtility.FindType("UnityEngine.Rendering.SortingGroup");
        if (parent == null || type == null) return "/group:unavailable";
        var component = parent.gameObject.GetComponent(Il2CppType.From(type));
        // GetComponent(Type) 的 IL2CPP 返回包装可能只暴露 Component，读取专有属性前必须确切转型。
        var group = RuntimeReflectionUtility.TryCastRuntimeObject(component, "UnityEngine.Rendering.SortingGroup") as Component;
        if (group == null) return $"/parent:{parent.Pointer:x}/group:not-direct-parent";
        return $"/parent:{parent.Pointer:x}/group:{group.Pointer:x}:"
            + $"{RuntimeReflectionUtility.GetMemberValue(group, "sortingLayerID")}:"
            + $"{RuntimeReflectionUtility.GetMemberValue(group, "sortingOrder")}";
    }

    private static T Invoke<T>(object instance, string name, Vector3Int point) where T : struct
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(Vector3Int) }, null);
        if (method?.ReturnType != typeof(T) || method.Invoke(instance, new object[] { point }) is not T value)
            throw new InvalidOperationException("cooker placement " + name + " unavailable");
        return value;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool Finite(Matrix4x4 value)
    {
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 4; column++)
            if (!float.IsFinite(value[row, column])) return false;
        return true;
    }
}
