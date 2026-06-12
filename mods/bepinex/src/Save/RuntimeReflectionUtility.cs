using System.Collections;
using System.Reflection;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeReflectionUtility
{
    public static Type? FindType(string fullName)
    {
        var direct = Type.GetType(fullName, throwOnError: false);
        if (direct != null) return direct;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type != null) return type;
            }
            catch
            {
                // Ignore assemblies that cannot resolve unrelated IL2CPP types.
            }
        }

        return null;
    }

    public static object? GetSingletonInstance(Type type)
    {
        foreach (var name in new[] { "Instance", "UniqueInstance", "instance", "m_Instance", "m_instance", "s_Instance", "m_UniqueInstance", "Main", "Singleton", "Current" })
        {
            var value = GetStaticMemberValue(type, name);
            if (value != null) return value;
        }

        return null;
    }

    public static object? FindUnityObject(Type type)
    {
        var method = typeof(UnityEngine.Object).GetMethod("FindObjectOfType", new[] { typeof(Type) });
        if (method == null) return null;

        try
        {
            return method.Invoke(null, new object[] { type });
        }
        catch
        {
            return null;
        }
    }

    public static IEnumerable<object?> FindUnityObjects(Type type)
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

        foreach (var item in EnumerateObjects(objects))
        {
            yield return item;
        }
    }

    public static IEnumerable<object?> FindUnityObjectsIncludingInactive(Type type)
    {
        foreach (var item in FindUnityObjects(type))
        {
            if (IsRuntimeSceneObject(item)) yield return item;
        }

        var method = typeof(UnityEngine.Resources).GetMethod("FindObjectsOfTypeAll", new[] { typeof(Type) });
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

        foreach (var item in EnumerateObjects(objects))
        {
            if (IsRuntimeSceneObject(item)) yield return item;
        }
    }

    public static bool IsRuntimeSceneObject(object? value)
    {
        if (value == null) return false;

        var gameObject = value is UnityEngine.GameObject ? value : GetMemberValue(value, "gameObject");
        if (gameObject == null) return true;

        var scene = GetMemberValue(gameObject, "scene");
        if (scene == null) return true;

        var isLoaded = GetMemberValue(scene, "isLoaded");
        if (isLoaded != null) return ToBool(isLoaded);

        var sceneName = GetMemberValue(scene, "name")?.ToString();
        return !string.IsNullOrWhiteSpace(sceneName);
    }

    public static object? InvokeStaticMethod(Type type, string methodName, params object?[] args)
    {
        var method = FindMethod(type, methodName, args, isStatic: true)
            ?? FindMethod(type, methodName, args.Length, isStatic: true)
            ?? FindMethod(type, methodName, isStatic: true);
        if (method == null) return null;

        try
        {
            return method.Invoke(null, args);
        }
        catch
        {
            return null;
        }
    }

    public static object? InvokeMethod(object? instance, string methodName, params object?[] args)
    {
        if (instance == null) return null;
        var method = FindMethod(instance.GetType(), methodName, args, isStatic: false)
            ?? FindMethod(instance.GetType(), methodName, args.Length, isStatic: false)
            ?? FindMethod(instance.GetType(), methodName, isStatic: false);
        if (method == null) return null;

        try
        {
            return method.Invoke(instance, args);
        }
        catch
        {
            return null;
        }
    }

    public static object? GetStaticMemberValue(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var memberName in BuildMemberNameCandidates(name))
            {
                if (TryReadKnownStaticField(current, memberName, out var knownValue)) return knownValue;

                var property = FindProperty(current, memberName, flags);
                if (TryReadProperty(null, property, out var propertyValue)) return propertyValue;

                var field = current.GetField(memberName, flags);
                if (TryReadField(null, field, out var fieldValue)) return fieldValue;
            }
        }

        return null;
    }

    public static object? GetMemberValue(object? instance, string name)
    {
        if (instance == null) return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            foreach (var memberName in BuildMemberNameCandidates(name))
            {
                if (TryReadKnownField(instance, type, memberName, out var knownValue)) return knownValue;

                var property = FindProperty(type, memberName, flags);
                if (TryReadProperty(instance, property, out var propertyValue)) return propertyValue;

                var field = type.GetField(memberName, flags);
                if (TryReadField(instance, field, out var fieldValue)) return fieldValue;

                var method = FindMethod(type, memberName, 0, isStatic: false);
                if (method == null) continue;

                try
                {
                    return method.Invoke(instance, null);
                }
                catch
                {
                    // Try the next candidate.
                }
            }
        }

        return null;
    }

    public static IEnumerable<object?> EnumerateObjects(object? value)
    {
        if (value == null || value is string) yield break;
        if (!LooksLikeIl2CppObject(value) && value is IEnumerable enumerable)
        {
            IEnumerator enumerator;
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
                object? current;
                try
                {
                    hasNext = enumerator.MoveNext();
                    current = hasNext ? enumerator.Current : null;
                }
                catch
                {
                    yield break;
                }

                if (!hasNext) break;
                yield return current;
            }

            yield break;
        }

        var values = GetMemberValue(value, "Values");
        if (values != null && !ReferenceEquals(values, value))
        {
            foreach (var item in EnumerateObjects(values))
            {
                yield return item;
            }
        }

        var count = ToInt(GetMemberValue(value, "Count") ?? GetMemberValue(value, "Length"), -1);
        if (count > 0)
        {
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                var success = false;
                try
                {
                    item = InvokeMethod(value, "get_Item", index);
                    success = item != null;
                }
                catch
                {
                    // Try the next index.
                }

                if (success) yield return item;
            }
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

    public static object? NormalizeKeyValueValue(object? value)
    {
        if (value == null) return null;
        return GetMemberValue(value, "Value")
            ?? GetMemberValue(value, "value")
            ?? GetMemberValue(value, "m_Value")
            ?? GetMemberValue(value, "Item2")
            ?? value;
    }

    public static int CountObjects(object? value)
    {
        if (value == null) return 0;
        var count = ToInt(GetMemberValue(value, "Count"), int.MinValue);
        if (count != int.MinValue) return count;
        var length = ToInt(GetMemberValue(value, "Length"), int.MinValue);
        if (length != int.MinValue) return length;
        return EnumerateObjects(value).Take(256).Count();
    }

    public static int ToInt(object? value, int fallback = 0)
    {
        if (value == null) return fallback;
        if (value is int intValue) return intValue;
        if (value is long longValue) return (int)longValue;
        if (value is short shortValue) return shortValue;
        if (value is byte byteValue) return byteValue;
        if (value is Enum enumValue) return Convert.ToInt32(enumValue);
        return int.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
    }

    public static bool ToBool(object? value)
    {
        if (value == null) return false;
        if (value is bool boolValue) return boolValue;
        return bool.TryParse(value.ToString(), out var parsed) && parsed;
    }

    public static string Trim(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..maxLength] + "...";
    }

    private static IEnumerable<string> BuildMemberNameCandidates(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;

        yield return name;

        var pascalName = char.ToUpperInvariant(name[0]) + name[1..];
        if (!string.Equals(pascalName, name, StringComparison.Ordinal)) yield return pascalName;

        var camelName = char.ToLowerInvariant(name[0]) + name[1..];
        if (!string.Equals(camelName, name, StringComparison.Ordinal)) yield return camelName;
    }

    private static IEnumerable<string> BuildFieldNameCandidates(string name)
    {
        foreach (var memberName in BuildMemberNameCandidates(name))
        {
            yield return memberName;
            yield return $"<{memberName}>k__BackingField";
            yield return $"m_{memberName}";
            yield return $"_{memberName}";
        }
    }

    private static bool TryReadKnownField(object instance, Type type, string name, out object? value)
    {
        value = null;
        foreach (var fieldName in BuildFieldNameCandidates(name))
        {
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadField(instance, field, out value)) return true;
        }

        return false;
    }

    private static bool TryReadKnownStaticField(Type type, string name, out object? value)
    {
        value = null;
        foreach (var fieldName in BuildFieldNameCandidates(name))
        {
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (TryReadField(null, field, out value)) return true;
        }

        return false;
    }

    private static bool TryReadProperty(object? instance, PropertyInfo? property, out object? value)
    {
        value = null;
        if (property == null) return false;

        try
        {
            value = property.GetValue(instance);
            return value != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadField(object? instance, FieldInfo? field, out object? value)
    {
        value = null;
        if (field == null) return false;

        try
        {
            value = field.GetValue(instance);
            return value != null;
        }
        catch
        {
            return false;
        }
    }

    private static PropertyInfo? FindProperty(Type type, string name, BindingFlags flags)
    {
        try
        {
            return type.GetProperty(name, flags);
        }
        catch (AmbiguousMatchException)
        {
            return type.GetProperties(flags).FirstOrDefault(property => property.Name == name);
        }
    }

    private static MethodInfo? FindMethod(Type type, string methodName, bool isStatic)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        try
        {
            return type.GetMethod(methodName, flags);
        }
        catch (AmbiguousMatchException)
        {
            return type.GetMethods(flags).FirstOrDefault(method => method.Name == methodName);
        }
    }

    private static MethodInfo? FindMethod(Type type, string methodName, int argCount, bool isStatic)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        return type
            .GetMethods(flags)
            .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == argCount);
    }

    private static MethodInfo? FindMethod(Type type, string methodName, object?[] args, bool isStatic)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        foreach (var method in type.GetMethods(flags).Where(method => method.Name == methodName))
        {
            var parameters = method.GetParameters();
            if (parameters.Length != args.Length) continue;
            if (ArgumentsAreCompatible(parameters, args)) return method;
        }

        return null;
    }

    private static bool ArgumentsAreCompatible(IReadOnlyList<ParameterInfo> parameters, IReadOnlyList<object?> args)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            var arg = args[i];
            if (arg == null) continue;
            var parameterType = parameters[i].ParameterType;
            if (parameterType.IsInstanceOfType(arg)) continue;
            if (parameterType.IsEnum && arg is string) continue;
            if (parameterType == typeof(int) && arg is IConvertible) continue;
            if (parameterType == typeof(string)) continue;
            return false;
        }

        return true;
    }
}
