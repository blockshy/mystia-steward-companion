using System.Collections;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeLanguageUtility
{
    public static IReadOnlyDictionary<int, string> ReadStringDictionary(Type? languageType, string dictionaryMethod)
    {
        var result = new Dictionary<int, string>();
        if (languageType == null) return result;

        foreach (var pair in EnumerateKeyValuePairs(RuntimeReflectionUtility.InvokeStaticMethod(languageType, dictionaryMethod)))
        {
            var id = ToNullableInt(pair.Key);
            if (!id.HasValue) continue;

            var text = CleanText(pair.Value);
            if (!string.IsNullOrWhiteSpace(text)) result[id.Value] = text;
        }

        return result;
    }

    public static string ResolveLanguageName(Type? languageType, string methodName, int? id, string? fallback)
    {
        if (languageType != null && id.HasValue)
        {
            var value = RuntimeReflectionUtility.InvokeStaticMethod(languageType, methodName, id.Value);
            var text = CleanText(value);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return string.IsNullOrWhiteSpace(fallback) ? "" : fallback.Trim();
    }

    public static string ResolveSpecialGuestName(
        Type? languageType,
        IReadOnlyDictionary<int, string> specialGuestNames,
        int? id,
        string? fallback)
    {
        if (id.HasValue
            && specialGuestNames.TryGetValue(id.Value, out var dictionaryName)
            && !string.IsNullOrWhiteSpace(dictionaryName))
        {
            return dictionaryName.Trim();
        }

        return ResolveLanguageName(languageType, "GetSpecialGuestLang", id, fallback);
    }

    public static string CleanText(object? value)
    {
        if (value == null) return "";
        if (value is string text) return text.Trim();

        foreach (var memberName in new[]
                 {
                     "Name",
                     "DisplayName",
                     "Title",
                     "Label",
                     "Text",
                     "Description",
                     "ShortDescription",
                     "name",
                     "title",
                     "text",
                     "description",
                 })
        {
            var memberValue = RuntimeReflectionUtility.GetMemberValue(value, memberName);
            if (memberValue == null || ReferenceEquals(memberValue, value)) continue;
            var memberText = memberValue.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(memberText)) return memberText;
        }

        try
        {
            var objectText = value.ToString()?.Trim() ?? "";
            return objectText.StartsWith(value.GetType().FullName ?? "", StringComparison.Ordinal)
                ? ""
                : objectText;
        }
        catch
        {
            return "";
        }
    }

    private static IEnumerable<(object? Key, object? Value)> EnumerateKeyValuePairs(object? value)
    {
        if (value == null) yield break;

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                yield return (entry.Key, entry.Value);
            }

            yield break;
        }

        foreach (var item in RuntimeReflectionUtility.EnumerateObjects(value))
        {
            var key = RuntimeReflectionUtility.GetMemberValue(item, "Key")
                ?? RuntimeReflectionUtility.GetMemberValue(item, "key")
                ?? RuntimeReflectionUtility.GetMemberValue(item, "Item1");
            var itemValue = RuntimeReflectionUtility.GetMemberValue(item, "Value")
                ?? RuntimeReflectionUtility.GetMemberValue(item, "value")
                ?? RuntimeReflectionUtility.GetMemberValue(item, "Item2");
            if (key != null || itemValue != null) yield return (key, itemValue);
        }
    }

    private static int? ToNullableInt(object? value)
    {
        if (value == null) return null;
        if (value is int intValue) return intValue;
        if (value is Enum enumValue) return Convert.ToInt32(enumValue);
        if (value is IConvertible convertible)
        {
            try
            {
                return convertible.ToInt32(null);
            }
            catch
            {
                return null;
            }
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }
}
