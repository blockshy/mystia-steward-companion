using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
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

    private static nint ReadObjectPointer(object target)
    {
        var pointer = ReadMember(target, "Pointer") ?? ReadMember(target, "NativePointer") ?? ReadMember(target, "m_CachedPtr");
        if (pointer is IntPtr intPtr) return intPtr;
        if (pointer is nint native) return native;
        if (pointer is IConvertible convertible) return new IntPtr(convertible.ToInt64(null));
        return new IntPtr(RuntimeHelpers.GetHashCode(target));
    }

    private static object? ReadMember(object target, string name)
    {
        try
        {
            var utilityValue = RuntimeReflectionUtility.GetMemberValue(target, name);
            if (utilityValue != null) return utilityValue;
        }
        catch
        {
            // Fall back to the local exact-field reader below.
        }

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

    private static bool WriteMember(object target, string name, object? value)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property?.SetMethod != null)
            {
                property.SetValue(target, value);
                return true;
            }

            foreach (var fieldName in BuildFieldNameCandidates(name))
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field == null) continue;

                field.SetValue(target, value);
                return true;
            }
        }

        return false;
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

    private static IEnumerable<int> ReadIntEnumerable(object? value)
    {
        if (value == null) yield break;
        if (value is string) yield break;
        foreach (var item in EnumerateManaged(value).Concat(EnumerateByIndexer(value)))
        {
            yield return ToInt(item);
        }
    }

    private static IEnumerable<object> ReadObjectEnumerable(object? value)
    {
        if (value == null || value is string) yield break;

        var seen = new HashSet<nint>();
        foreach (var item in EnumerateManaged(value).Concat(EnumerateByIndexer(value)).Concat(ReadDictionaryValues(value)))
        {
            if (item == null) continue;
            if (!TryRememberObject(item, seen)) continue;
            yield return item;
        }
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

        var entries = ReadMember(dictionary, "entries")
            ?? ReadMember(dictionary, "_entries")
            ?? ReadMember(dictionary, "m_Entries");
        var count = ToInt(ReadMember(dictionary, "count")
            ?? ReadMember(dictionary, "_count")
            ?? ReadMember(dictionary, "Count"));
        if (entries == null || count <= 0) yield break;

        var entryIndex = 0;
        foreach (var entry in EnumerateByIndexer(entries))
        {
            if (entryIndex++ >= Math.Min(count, 256)) yield break;
            if (entry == null) continue;

            var hashCode = ToInt(ReadMember(entry, "hashCode") ?? ReadMember(entry, "_hashCode"));
            if (hashCode < 0) continue;

            var value = ReadMember(entry, "value")
                ?? ReadMember(entry, "Value")
                ?? ReadMember(entry, "_value");
            if (value != null) yield return value;
        }
    }

    private static IEnumerable<object?> EnumerateManaged(object value)
    {
        if (LooksLikeIl2CppObject(value)) yield break;
        if (value is not IEnumerable enumerable) yield break;

        foreach (var item in enumerable)
        {
            yield return item;
        }
    }

    private static IEnumerable<object?> EnumerateByIndexer(object value)
    {
        var count = ToInt(TryInvokeInstanceValue(value, "get_Count")
            ?? ReadMember(value, "Count")
            ?? ReadMember(value, "Length")
            ?? ReadMember(value, "_size"));
        if (count <= 0) yield break;

        for (var index = 0; index < Math.Min(count, 256); index++)
        {
            yield return TryInvokeInstanceValue(value, "get_Item", new object?[] { index });
        }
    }

    private static IEnumerable<object?> FindUnityObjects(Type type)
    {
        var method = typeof(UnityEngine.Object).GetMethod("FindObjectsOfType", new[] { typeof(Type) });
        if (method == null) yield break;

        object? objects;
        try
        {
            objects = method.Invoke(null, new object[] { type });
        }
        catch
        {
            yield break;
        }

        foreach (var item in ReadObjectEnumerable(objects))
        {
            yield return item;
        }
    }

    private static bool LooksLikeIl2CppObject(object value)
    {
        var type = value.GetType();
        var fullName = type.FullName ?? "";
        if (fullName.StartsWith("Il2Cpp", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("NightScene.", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("DayScene.", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("GameData.", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("DEYU.", StringComparison.Ordinal)) return true;
        return type.Assembly.GetName().Name?.Contains("Il2Cpp", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static object? GetSingletonInstance(string typeName)
    {
        var type = FindType(typeName)
            ?? throw new InvalidOperationException($"{typeName} type is not loaded.");
        return RuntimeReflectionUtility.GetSingletonInstance(type)
            ?? RuntimeReflectionUtility.FindUnityObject(type);
    }

    private static object? InvokeStatic(string typeName, string methodName, object?[] args)
    {
        var type = FindType(typeName)
            ?? throw new InvalidOperationException($"{typeName} type is not loaded.");
        var utilityValue = RuntimeReflectionUtility.InvokeStaticMethod(type, methodName, args);
        if (utilityValue != null) return utilityValue;

        var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                && CanUseParameters(candidate.GetParameters(), args))
            ?? throw new MissingMethodException(typeName, methodName);
        return method.Invoke(null, args);
    }

    private static object? InvokeInstance(object target, string methodName, object?[] args)
    {
        var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                && CanUseParameters(candidate.GetParameters(), args))
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        return method.Invoke(target, args);
    }

    private static bool TryInvokeInstance(object target, string methodName, object?[] args)
    {
        var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                && CanUseParameters(candidate.GetParameters(), args));
        if (method == null) return false;

        try
        {
            method.Invoke(target, args);
            return true;
        }
        catch
        {
            return false;
        }
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

    private static bool CanUseParameters(ParameterInfo[] parameters, object?[] args)
    {
        if (parameters.Length != args.Length) return false;
        for (var i = 0; i < parameters.Length; i++)
        {
            var arg = args[i];
            var parameterType = parameters[i].ParameterType;
            if (parameterType.IsByRef)
            {
                parameterType = parameterType.GetElementType() ?? parameterType;
            }

            if (arg == null)
            {
                if (parameterType.IsValueType) return false;
                continue;
            }

            var argType = arg.GetType();
            if (parameterType.IsAssignableFrom(argType)) continue;
            if (parameterType.IsPrimitive && arg is IConvertible) continue;
            return false;
        }

        return true;
    }

    private static object? GetDefaultValue(Type type)
    {
        if (type == typeof(bool)) return false;
        if (type == typeof(int)) return 0;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static int ToInt(object? value)
    {
        if (value == null) return 0;
        if (value is int number) return number;
        if (value is Enum enumValue) return Convert.ToInt32(enumValue);
        if (value is IConvertible convertible) return Convert.ToInt32(convertible);
        return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static int ToInt(object? value, int fallback)
    {
        if (value == null) return fallback;
        try
        {
            if (value is int number) return number;
            if (value is Enum enumValue) return Convert.ToInt32(enumValue);
            if (value is IConvertible convertible) return Convert.ToInt32(convertible);
            return int.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ReadBool(object? value)
    {
        if (value is bool boolValue) return boolValue;
        if (value is IConvertible convertible) return convertible.ToBoolean(null);
        return bool.TryParse(value?.ToString(), out var parsed) && parsed;
    }
}
