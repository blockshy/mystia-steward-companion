using System.Reflection;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 列表 UI 的 #783 运行时边界。正式 Mod 的锁定引用不包含 UnityEngine.UI/UIModule，
/// 因而只在绑定时解析已证实类型和签名，再缓存绑定到精确组件的委托；热帧不反射装箱。
/// 这些包装只在 Unity 主线程持有，不进入业务目标或跨线程消息。
/// </summary>
internal static class RuntimePinnedListUiAccess
{
    internal static Type Type(string fullName) => RuntimeReflectionUtility.FindType(fullName)
        ?? throw new InvalidOperationException($"列表色带运行时类型不可用：{fullName}。");

    internal static T Bind<T>(object owner, string methodName) where T : Delegate
    {
        var method = owner.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"列表色带成员不可用：{owner.GetType().FullName}.{methodName}。");
        return method.CreateDelegate<T>(owner);
    }

    internal static Component? Component(object? value, string fullName) =>
        RuntimeReflectionUtility.TryCastRuntimeObject(value, fullName) as Component;
}

/// <summary>精确 Image 包装；不改写原图像属性，仅按需请求原生网格重建。</summary>
internal sealed class RuntimePinnedListImage
{
    internal Component Component { get; }
    private readonly Func<Sprite?> _getSprite;
    private readonly Func<RectTransform> _getRect;
    private readonly Action _setVerticesDirty;

    private RuntimePinnedListImage(Component component)
    {
        Component = component;
        _getSprite = RuntimePinnedListUiAccess.Bind<Func<Sprite?>>(component, "get_sprite");
        _getRect = RuntimePinnedListUiAccess.Bind<Func<RectTransform>>(component, "get_rectTransform");
        _setVerticesDirty = RuntimePinnedListUiAccess.Bind<Action>(component, "SetVerticesDirty");
    }

    internal static RuntimePinnedListImage? TryBind(object? value)
    {
        var component = RuntimePinnedListUiAccess.Component(value, "UnityEngine.UI.Image");
        return component == null ? null : new RuntimePinnedListImage(component);
    }
    internal GameObject gameObject => Component.gameObject;
    internal Transform transform => Component.transform;
    internal RectTransform rectTransform => _getRect();
    internal Sprite? sprite => _getSprite();
    internal void SetVerticesDirty() => _setVerticesDirty();
}

/// <summary>只读 Selectable.image 与 Transform，不触碰选中状态和回调。</summary>
internal sealed class RuntimePinnedListButton
{
    internal Component Component { get; }
    private readonly Func<object?> _getImage;
    private RuntimePinnedListButton(Component component)
    {
        Component = component;
        // Image 是引用类型，绑定委托允许协变为 object；避免每次读取重新创建边界包装。
        _getImage = RuntimePinnedListUiAccess.Bind<Func<object?>>(component, "get_image");
    }
    internal static RuntimePinnedListButton? TryBind(object? value)
    {
        var component = RuntimePinnedListUiAccess.Component(value, "UnityEngine.UI.Selectable");
        return component == null ? null : new RuntimePinnedListButton(component);
    }
    internal object? image => _getImage();
    internal Transform transform => Component.transform;
}
