using System.Reflection;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

/// <summary>只包含托管值的厨具图鉴说明，不持有游戏对象、语言对象或 Unity wrapper。</summary>
internal sealed record RuntimeCookerDisplayInfo(int? CookerId, string Name, string Effect, string Status);

/// <summary>
/// 在现有厨具快照的主线程读取路径中，按精确实例读取游戏自己的名称与说明。
/// metadata/interop-783 确认 Cooker 继承 NonTradableObjectBase.Id/Text；Native 的
/// Cooker.GetText（0x67DEB0）会调用 CookerAssetBase.GetText（0x777990）补充实际效果，
/// 因此不能直接取语言字典原文，也不能按 CookerType/Series 拼接推测属性。
/// 反射形状仅解析一次，成功文本缓存30秒、未就绪文本5秒后重试；缓存最多128项，
/// 每次快照最多更新两项文本，批量过期也分帧刷新，期间保留同一精确ID的托管文本。
/// 只保存字符串及访问器，不缓存仍关联游戏内存的对象。该类不读取控制器、不扫描场景，
/// 说明失败仅影响展示，不改变原有厨具能力、准入与自动化安全判断。
/// </summary>
internal sealed class RuntimeCookerDisplayReader
{
    private const string CookerType = "GameData.Core.Collections.Cooker";
    private const string ObjectType = "GameData.Core.NonTradableObjectBase";
    private const string TextType = "GameData.CoreLanguage.ObjectLanguageBase";
    private const string LanguageType = "GameData.CoreLanguage.LanguageBase";
    private readonly Dictionary<Type, (PropertyInfo Id, PropertyInfo Text)?> _cookers = new();
    private readonly Dictionary<Type, (PropertyInfo Name, PropertyInfo Description)?> _languages = new();
    private readonly Dictionary<int, (long RetryAt, RuntimeCookerDisplayInfo Value)> _text = new();

    /// <summary>
    /// 调用方必须沿用快照的主线程上下文。单调时钟由调用方传入，方便隔离测试验证缓存、
    /// 重试及边界；不引入后台任务，也不以重试时间决定任何游戏操作是否允许。
    /// </summary>
    internal RuntimeCookerDisplayInfo Read(object? cooker, long nowMilliseconds, ref int textReadBudget)
    {
        int? id = null;
        try
        {
            if (cooker == null || cooker.GetType().FullName != CookerType) return Unknown(null);
            var type = cooker.GetType();
            if (!_cookers.TryGetValue(type, out var shape))
            {
                var idProperty = ExactProperty(type, "Id", typeof(int), ObjectType);
                var textProperty = type.GetProperty("Text", BindingFlags.Public | BindingFlags.Instance);
                shape = idProperty != null && textProperty != null && textProperty.PropertyType.FullName == TextType
                    && textProperty.DeclaringType?.FullName == ObjectType
                    && IsReadable(textProperty) ? (idProperty, textProperty) : null;
                _cookers[type] = shape;
            }
            if (shape == null || shape.Value.Id.GetValue(cooker) is not int exactId || exactId < 0)
                return Unknown(null);
            id = exactId;
            if (_text.TryGetValue(exactId, out var cached) && nowMilliseconds < cached.RetryAt)
                return cached.Value;
            // 一个快照中的全部厨具共享预算，不让首帧或30秒到期同时执行全部原生Text。
            // 没有缓存时仅说明未知，绝不阻断本轮原有能力读取或把未知能力改为可用。
            if (textReadBudget <= 0) return cached.Value ?? Unknown(id);
            textReadBudget--;

            var language = shape.Value.Text.GetValue(cooker);
            if (language == null || language.GetType().FullName != TextType)
                return Remember(Unknown(id), nowMilliseconds);
            var languageType = language.GetType();
            if (!_languages.TryGetValue(languageType, out var languageShape))
            {
                var name = ExactProperty(languageType, "Name", typeof(string), LanguageType);
                var description = ExactProperty(languageType, "Description", typeof(string), LanguageType);
                languageShape = name != null && description != null ? (name, description) : null;
                _languages[languageType] = languageShape;
            }
            if (languageShape == null
                || languageShape.Value.Name.GetValue(language) is not string displayName
                || string.IsNullOrWhiteSpace(displayName)
                || languageShape.Value.Description.GetValue(language) is not string descriptionText
                || displayName.Length > 512 || descriptionText.Length > 16384)
                return Remember(Unknown(id), nowMilliseconds);

            // 游戏可能保留Unity富文本标记；与任务说明共用纯文字转换，保留原句和换行。
            // 空说明是游戏实际返回值，不推断成“没有特殊效果”。
            var plainName = GameDisplayText.Normalize(displayName);
            if (plainName.Length == 0) return Remember(Unknown(id), nowMilliseconds);
            var result = new RuntimeCookerDisplayInfo(id, plainName,
                GameDisplayText.Normalize(descriptionText), "ready");
            return Remember(result, nowMilliseconds);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 游戏语言目录未初始化、getter异常或结构变化都显式降级为未知。
            // 不让附加展示读取破坏既有完整厨具快照，也不输出每帧重复异常日志。
            return Remember(Unknown(id), nowMilliseconds);
        }
    }

    private RuntimeCookerDisplayInfo Remember(RuntimeCookerDisplayInfo value, long now)
    {
        if (value.CookerId is not int id) return value;
        if (_text.Count >= 128 && !_text.ContainsKey(id)) _text.Remove(_text.Keys.First());
        _text[id] = (now + (value.Status == "ready" ? 30_000 : 5_000), value);
        return value;
    }

    private static RuntimeCookerDisplayInfo Unknown(int? id) => new(id, "", "", "unavailable");

    private static PropertyInfo? ExactProperty(Type type, string name, Type valueType, string owner)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return property != null && property.PropertyType == valueType && property.DeclaringType?.FullName == owner
            && IsReadable(property) ? property : null;
    }

    private static bool IsReadable(PropertyInfo property) => property.GetIndexParameters().Length == 0
        && property.GetMethod is { IsStatic: false, IsPublic: true };
}
