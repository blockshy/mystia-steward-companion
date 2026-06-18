using System.Collections;
using System.Reflection;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeCookerReflection
{
    public const string CookSystemManagerTypeName = "NightScene.CookingUtility.CookSystemManager";
    public const string CookControllerTypeName = "NightScene.CookingUtility.CookController";
    public const string DataBaseCoreTypeName = "GameData.Core.Collections.DataBaseCore";

    private static readonly Dictionary<int, string> CookerTypeNames = new()
    {
        [1] = "煮锅",
        [2] = "烧烤架",
        [3] = "油锅",
        [4] = "蒸锅",
        [5] = "料理台",
    };

    public static object? GetCookSystemManager()
    {
        var type = RuntimeReflectionUtility.FindType(CookSystemManagerTypeName);
        return type == null ? null : RuntimeReflectionUtility.GetSingletonInstance(type);
    }

    public static object? ResolveCookerById(int cookerId)
    {
        try
        {
            var type = RuntimeReflectionUtility.FindType(DataBaseCoreTypeName);
            if (type == null) return null;
            var cooker = RuntimeReflectionUtility.InvokeStaticMethod(type, "RefCooker", cookerId);
            return cooker == Missing.Value ? null : cooker;
        }
        catch
        {
            return null;
        }
    }

    public static List<int> ReadCookerTypeIds(object? cooker)
    {
        if (cooker == null) return new List<int>();

        try
        {
            var directType = RuntimeReflectionUtility.ToInt(
                RuntimeReflectionUtility.InvokeMethod(cooker, "get_Type")
                ?? RuntimeReflectionUtility.GetMemberValue(cooker, "Type")
                ?? RuntimeReflectionUtility.GetMemberValue(cooker, "type"),
                -1);
            if (directType > 0) return new List<int> { directType };
        }
        catch
        {
            // Fall back to all available cooker types below.
        }

        var cookerTypes = RuntimeReflectionUtility.InvokeMethod(cooker, "get_AllAvailableCookerType");
        return RuntimeReflectionUtility.EnumerateObjects(cookerTypes)
            .Select(value => RuntimeReflectionUtility.ToInt(value, -1))
            .Where(id => id > 0)
            .Distinct()
            .ToList();
    }

    public static bool CookerSupportsRecipe(object? cooker, int recipeCookerType)
    {
        return recipeCookerType > 0 && ReadCookerTypeIds(cooker).Contains(recipeCookerType);
    }

    public static string ResolveCookerTypeName(int typeId)
    {
        return CookerTypeNames.TryGetValue(typeId, out var name) ? name : $"#{typeId}";
    }

    public static string ReadCookerName(object? cooker)
    {
        if (cooker == null) return "";
        return RuntimeReflectionUtility.InvokeMethod(cooker, "get_Name")?.ToString()
            ?? RuntimeReflectionUtility.InvokeMethod(cooker, "get_name")?.ToString()
            ?? RuntimeReflectionUtility.GetMemberValue(cooker, "Name")?.ToString()
            ?? RuntimeReflectionUtility.GetMemberValue(cooker, "name")?.ToString()
            ?? "";
    }

    public static string NormalizeCookerName(string value)
    {
        return value.Trim() switch
        {
            "烤架" => "烧烤架",
            "烧烤台" => "烧烤架",
            "锅" => "煮锅",
            "炸锅" => "油锅",
            var normalized => normalized,
        };
    }

    public static IReadOnlyList<object> ReadCookerControllersFromCookSystem(object? cookSystem, out string status)
    {
        var result = new List<object>();
        var seen = new HashSet<nint>();
        var sourceParts = new List<string>();

        void AddControllers(string source, IEnumerable<object?> controllers)
        {
            var scanned = 0;
            var added = 0;
            foreach (var controller in controllers)
            {
                scanned++;
                if (controller == null) continue;

                nint pointer;
                try
                {
                    pointer = RuntimeReflectionUtility.ReadObjectPointer(controller);
                }
                catch
                {
                    pointer = new IntPtr(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(controller));
                }

                if (!seen.Add(pointer)) continue;
                result.Add(controller);
                added++;
            }

            sourceParts.Add($"{source}:{scanned}/{added}");
        }

        if (cookSystem != null)
        {
            var directControllers = RuntimeReflectionUtility.InvokeMethod(cookSystem, "get_AllCookerControllers")
                ?? RuntimeReflectionUtility.GetMemberValue(cookSystem, "AllCookerControllers");
            AddControllers("AllCookerControllers", RuntimeReflectionUtility.EnumerateObjects(directControllers));

            var allCookers = RuntimeReflectionUtility.GetMemberValue(cookSystem, "AllCookers");
            AddControllers("AllCookers", ReadDictionaryValues(allCookers).Where(value => value != null));
        }

        var controllerType = RuntimeReflectionUtility.FindType(CookControllerTypeName);
        if (controllerType != null)
        {
            AddControllers("UnityFind", RuntimeReflectionUtility.FindUnityObjects(controllerType));
        }

        status = $"sources={string.Join(",", sourceParts)}";
        return result;
    }

    private static IEnumerable<object?> ReadDictionaryValues(object? dictionary)
    {
        if (dictionary == null || dictionary is string) yield break;

        if (dictionary is IDictionary managedDictionary)
        {
            foreach (DictionaryEntry entry in managedDictionary)
            {
                yield return entry.Value;
            }

            yield break;
        }

        var entries = RuntimeReflectionUtility.GetMemberValue(dictionary, "entries")
            ?? RuntimeReflectionUtility.GetMemberValue(dictionary, "_entries")
            ?? RuntimeReflectionUtility.GetMemberValue(dictionary, "m_Entries");
        var count = RuntimeReflectionUtility.ToInt(
            RuntimeReflectionUtility.GetMemberValue(dictionary, "count")
            ?? RuntimeReflectionUtility.GetMemberValue(dictionary, "_count")
            ?? RuntimeReflectionUtility.GetMemberValue(dictionary, "Count"),
            -1);
        if (entries != null && count > 0)
        {
            var entryIndex = 0;
            foreach (var entry in RuntimeReflectionUtility.EnumerateObjects(entries))
            {
                if (entryIndex++ >= Math.Min(count, 256)) break;
                if (entry == null) continue;

                var hashCode = RuntimeReflectionUtility.ToInt(
                    RuntimeReflectionUtility.GetMemberValue(entry, "hashCode")
                    ?? RuntimeReflectionUtility.GetMemberValue(entry, "_hashCode"),
                    -1);
                if (hashCode < 0) continue;

                var value = RuntimeReflectionUtility.GetMemberValue(entry, "value")
                    ?? RuntimeReflectionUtility.GetMemberValue(entry, "Value")
                    ?? RuntimeReflectionUtility.GetMemberValue(entry, "_value");
                if (value != null) yield return value;
            }
        }

        foreach (var item in RuntimeReflectionUtility.EnumerateObjects(dictionary))
        {
            var value = RuntimeReflectionUtility.NormalizeKeyValueValue(item);
            if (value != null) yield return value;
        }
    }
}
