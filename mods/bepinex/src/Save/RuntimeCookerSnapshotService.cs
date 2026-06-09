using System.Collections;
using System.Runtime.CompilerServices;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeCookerSnapshotService
{
    private const string CookSystemManagerTypeName = "NightScene.CookingUtility.CookSystemManager";

    private static readonly object SyncRoot = new();

    private static readonly Dictionary<int, string> CookerTypeNames = new()
    {
        [1] = "煮锅",
        [2] = "烧烤架",
        [3] = "油锅",
        [4] = "蒸锅",
        [5] = "料理台",
    };

    public static string Status
    {
        get
        {
            lock (SyncRoot)
            {
                return _status;
            }
        }
    }

    private static string _status = "not read";

    public static void ApplyTo(RecommendationState state)
    {
        var cookers = ReadPlacedCookers();
        state.PlacedCookerStatus = Status;
        foreach (var cooker in cookers)
        {
            state.PlacedCookers.Add(cooker);
            foreach (var typeId in cooker.TypeIds)
            {
                state.PlacedCookerTypeIds.Add(typeId);
            }
        }
    }

    private static List<PlacedCookerInfo> ReadPlacedCookers()
    {
        var result = new List<PlacedCookerInfo>();
        object? cookSystem;
        try
        {
            cookSystem = GetSingletonInstance(CookSystemManagerTypeName);
        }
        catch (Exception ex)
        {
            SetStatus($"manager error: {ex.Message}");
            return result;
        }

        if (cookSystem == null)
        {
            SetStatus("manager missing");
            return result;
        }

        object? controllers;
        try
        {
            controllers = InvokeInstance(cookSystem, "get_AllCookerControllers", Array.Empty<object?>());
        }
        catch (Exception ex)
        {
            SetStatus($"controllers error: {ex.Message}");
            return result;
        }

        var controllerItems = ReadObjectEnumerable(controllers).ToList();
        var index = 0;
        foreach (var controller in controllerItems)
        {
            var controllerIndex = index++;
            object? cooker = null;
            var isOpen = false;

            try
            {
                cooker = InvokeInstance(controller, "get_Cooker", Array.Empty<object?>());
                isOpen = ReadBool(TryInvokeInstanceValue(controller, "get_CouldCookerOpen"));
            }
            catch
            {
                // Keep scanning other controllers; a single stale controller should not hide all cookers.
            }

            if (cooker == null) continue;

            var typeIds = ReadCookerTypeIds(cooker).Distinct().OrderBy(id => id).ToList();
            if (typeIds.Count == 0) continue;

            var typeNames = typeIds.Select(ResolveCookerTypeName).Where(name => name.Length > 0).Distinct().ToList();
            result.Add(new PlacedCookerInfo
            {
                ControllerIndex = controllerIndex,
                TypeIds = typeIds,
                TypeNames = typeNames,
                Name = typeNames.Count > 0 ? string.Join("/", typeNames) : cooker.GetType().Name,
                IsOpen = isOpen,
                Source = "CookSystemManager",
            });
        }

        var typeSummary = result
            .SelectMany(cooker => cooker.TypeNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToArray();
        SetStatus(result.Count == 0
            ? $"empty; controllers={controllerItems.Count}; controllersType={controllers?.GetType().FullName ?? "null"}"
            : $"ok; controllers={controllerItems.Count}; cookers={result.Count}; types={string.Join("/", typeSummary)}");
        return result;
    }

    private static List<int> ReadCookerTypeIds(object cooker)
    {
        try
        {
            var cookerTypes = InvokeInstance(cooker, "get_AllAvailableCookerType", Array.Empty<object?>());
            return ReadIntEnumerable(cookerTypes).Where(id => id >= 0).ToList();
        }
        catch
        {
            return new List<int>();
        }
    }

    private static string ResolveCookerTypeName(int typeId)
    {
        return CookerTypeNames.TryGetValue(typeId, out var name) ? name : $"#{typeId}";
    }

    private static object? TryInvokeInstanceValue(object target, string methodName)
    {
        return TryInvokeInstanceValue(target, methodName, Array.Empty<object?>());
    }

    private static object? TryInvokeInstanceValue(object target, string methodName, object?[] args)
    {
        try
        {
            return InvokeInstance(target, methodName, args);
        }
        catch
        {
            return null;
        }
    }

    private static object? GetSingletonInstance(string typeName)
    {
        var type = FindType(typeName);
        if (type == null) return null;

        var property = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (property != null) return property.GetValue(null);

        var method = type.GetMethod("get_Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        return method?.Invoke(null, Array.Empty<object?>());
    }

    private static object? InvokeInstance(object target, string methodName, object?[] args)
    {
        var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                && CanUseParameters(candidate.GetParameters(), args));
        return method == null ? null : method.Invoke(target, args);
    }

    private static bool CanUseParameters(ParameterInfo[] parameters, object?[] args)
    {
        if (parameters.Length != args.Length) return false;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (args[i] == null) continue;
            if (!parameters[i].ParameterType.IsInstanceOfType(args[i])) return false;
        }

        return true;
    }

    private static Type? FindType(string fullName)
    {
        var direct = Type.GetType(fullName, false);
        if (direct != null) return direct;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try
            {
                type = assembly.GetType(fullName, false);
            }
            catch
            {
                continue;
            }

            if (type != null) return type;
        }

        return null;
    }

    private static IEnumerable<object> ReadObjectEnumerable(object? value)
    {
        if (value == null || value is string) yield break;

        var seen = new HashSet<nint>();
        foreach (var item in EnumerateManaged(value).Concat(EnumerateByEnumerator(value)).Concat(EnumerateByIndexer(value)))
        {
            if (item == null) continue;
            if (!seen.Add(ReadObjectPointer(item))) continue;
            yield return NormalizeDictionaryItem(item) ?? item;
        }
    }

    private static IEnumerable<int> ReadIntEnumerable(object? value)
    {
        if (value == null || value is string) yield break;

        foreach (var item in EnumerateManaged(value).Concat(EnumerateByEnumerator(value)).Concat(EnumerateByIndexer(value)))
        {
            yield return ToInt(item);
        }
    }

    private static IEnumerable<object?> EnumerateManaged(object value)
    {
        if (value is not IEnumerable enumerable) yield break;

        foreach (var item in enumerable)
        {
            yield return item;
        }
    }

    private static IEnumerable<object?> EnumerateByEnumerator(object value)
    {
        foreach (var item in EnumerateByIl2CppEnumerator(value))
        {
            yield return item;
        }

        object? enumerator;
        try
        {
            enumerator = TryInvokeInstanceValue(value, "GetEnumerator");
        }
        catch
        {
            yield break;
        }

        if (enumerator == null) yield break;

        while (ReadBool(TryInvokeInstanceValue(enumerator, "MoveNext")))
        {
            yield return ReadMember(enumerator, "Current") ?? TryInvokeInstanceValue(enumerator, "get_Current");
        }
    }

    private static IEnumerable<object?> EnumerateByIl2CppEnumerator(object value)
    {
        if (value is not Il2CppObjectBase il2CppObject) yield break;

        Il2CppSystem.Collections.IEnumerable? enumerable;
        try
        {
            enumerable = il2CppObject.TryCast<Il2CppSystem.Collections.IEnumerable>();
        }
        catch
        {
            yield break;
        }

        if (enumerable == null) yield break;

        Il2CppSystem.Collections.IEnumerator? enumerator;
        try
        {
            enumerator = enumerable.GetEnumerator();
        }
        catch
        {
            yield break;
        }

        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = enumerator.MoveNext();
            }
            catch
            {
                yield break;
            }

            if (!hasNext) yield break;

            object? current;
            try
            {
                current = enumerator.Current;
            }
            catch
            {
                current = null;
            }

            yield return current;
        }
    }

    private static IEnumerable<object?> EnumerateByIndexer(object value)
    {
        var count = ToInt(TryInvokeInstanceValue(value, "get_Count")
            ?? ReadMember(value, "Count")
            ?? ReadMember(value, "_size"));
        if (count <= 0) yield break;

        for (var index = 0; index < Math.Min(count, 128); index++)
        {
            yield return TryInvokeInstanceValue(value, "get_Item", new object?[] { index });
        }
    }

    private static object? NormalizeDictionaryItem(object item)
    {
        return ReadMember(item, "Value") ?? ReadMember(item, "value");
    }

    private static object? ReadMember(object target, string name)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            foreach (var fieldName in BuildFieldNameCandidates(name))
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target);
            }

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property != null) return property.GetValue(target);

            var pascalName = char.ToUpperInvariant(name[0]) + name[1..];
            if (!string.Equals(pascalName, name, StringComparison.Ordinal))
            {
                property = type.GetProperty(pascalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (property != null) return property.GetValue(target);
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildFieldNameCandidates(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;

        yield return name;
        yield return $"<{name}>k__BackingField";
        yield return $"m_{name}";
        yield return $"_{name}";

        var camelName = char.ToLowerInvariant(name[0]) + name[1..];
        if (!string.Equals(camelName, name, StringComparison.Ordinal))
        {
            yield return camelName;
            yield return $"<{camelName}>k__BackingField";
            yield return $"m_{camelName}";
            yield return $"_{camelName}";
        }
    }

    private static bool ReadBool(object? value)
    {
        if (value is bool boolean) return boolean;
        if (value is IConvertible convertible)
        {
            try
            {
                return convertible.ToInt32(null) != 0;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static int ToInt(object? value)
    {
        if (value == null) return -1;
        if (value is int number) return number;
        if (value is Enum) return Convert.ToInt32(value);
        if (value is IConvertible convertible)
        {
            try
            {
                return convertible.ToInt32(null);
            }
            catch
            {
                return -1;
            }
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : -1;
    }

    private static nint ReadObjectPointer(object target)
    {
        var pointer = ReadMember(target, "Pointer") ?? ReadMember(target, "NativePointer") ?? ReadMember(target, "m_CachedPtr");
        if (pointer is IntPtr intPtr) return intPtr;
        if (pointer is nint native) return native;
        if (pointer is IConvertible convertible)
        {
            try
            {
                return new IntPtr(convertible.ToInt64(null));
            }
            catch
            {
                return new IntPtr(RuntimeHelpers.GetHashCode(target));
            }
        }

        return new IntPtr(RuntimeHelpers.GetHashCode(target));
    }

    private static void SetStatus(string status)
    {
        lock (SyncRoot)
        {
            _status = status;
        }
    }
}
