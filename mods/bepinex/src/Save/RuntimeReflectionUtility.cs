using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeReflectionUtility
{
    private static readonly ConcurrentDictionary<string, Type> TypeCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<MemberCacheKey, CachedLookup<PropertyInfo>> PropertyCache = new();
    private static readonly ConcurrentDictionary<MemberCacheKey, CachedLookup<FieldInfo>> FieldCache = new();
    private static readonly ConcurrentDictionary<MethodCacheKey, CachedLookup<MethodInfo>> MethodCache = new();
    private static readonly ConcurrentDictionary<Type, CachedLookup<MethodInfo>> TryCastMethodCache = new();

    public static Type? FindType(string fullName)
    {
        if (TypeCache.TryGetValue(fullName, out var cachedType)) return cachedType;

        var direct = Type.GetType(fullName, throwOnError: false);
        if (direct != null)
        {
            TypeCache.TryAdd(fullName, direct);
            return direct;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type == null) continue;
                TypeCache.TryAdd(fullName, type);
                return type;
            }
            catch
            {
                // Ignore assemblies that cannot resolve unrelated IL2CPP types.
            }
        }

        // Assemblies can load after plugin startup, so failed lookups must remain retryable.
        return null;
    }

    public static object? TryCastRuntimeObject(object? value, string targetTypeName)
    {
        var targetType = FindType(targetTypeName);
        if (targetType == null) return null;
        if (value is not Il2CppObjectBase il2CppObject) return null;
        if (targetType.IsInstanceOfType(value)) return value;

        var tryCast = TryCastMethodCache.GetOrAdd(
            targetType,
            static type => new CachedLookup<MethodInfo>(ResolveTryCastMethod(type))).Value;
        if (tryCast == null) return null;

        try
        {
            return tryCast.Invoke(il2CppObject, Array.Empty<object?>());
        }
        catch
        {
            return null;
        }
    }

    private static MethodInfo? ResolveTryCastMethod(Type targetType)
    {
        var definition = typeof(Il2CppObjectBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "TryCast"
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1
                && method.GetParameters().Length == 0);
        if (definition == null) return null;

        try
        {
            return definition.MakeGenericMethod(targetType);
        }
        catch
        {
            return null;
        }
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

                var field = FindField(current, memberName, flags);
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

                var field = FindField(type, memberName, flags);
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

    public static nint ReadObjectPointer(object target)
    {
        var pointer = GetMemberValue(target, "Pointer") ?? GetMemberValue(target, "NativePointer") ?? GetMemberValue(target, "m_CachedPtr");
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
                return new IntPtr(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(target));
            }
        }

        return new IntPtr(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(target));
    }

    public static bool TryReadNativeObjectPointer(object? target, out nint pointer)
    {
        pointer = 0;
        if (target == null) return false;

        try
        {
            var value = GetMemberValue(target, "Pointer")
                ?? GetMemberValue(target, "NativePointer")
                ?? GetMemberValue(target, "m_CachedPtr");
            pointer = value switch
            {
                IntPtr intPtr => intPtr,
                IConvertible convertible => new IntPtr(convertible.ToInt64(null)),
                _ => 0,
            };
            return pointer != 0;
        }
        catch
        {
            pointer = 0;
            return false;
        }
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
            var field = FindField(type, fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadField(instance, field, out value)) return true;
        }

        return false;
    }

    private static bool TryReadKnownStaticField(Type type, string name, out object? value)
    {
        value = null;
        foreach (var fieldName in BuildFieldNameCandidates(name))
        {
            var field = FindField(type, fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
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
        return PropertyCache.GetOrAdd(new MemberCacheKey(type, name, flags), static key =>
        {
            try
            {
                return new CachedLookup<PropertyInfo>(key.Type.GetProperty(key.Name, key.Flags));
            }
            catch (AmbiguousMatchException)
            {
                return new CachedLookup<PropertyInfo>(key.Type.GetProperties(key.Flags).FirstOrDefault(property => property.Name == key.Name));
            }
        }).Value;
    }

    private static FieldInfo? FindField(Type type, string name, BindingFlags flags)
    {
        return FieldCache.GetOrAdd(new MemberCacheKey(type, name, flags), static key =>
        {
            try
            {
                return new CachedLookup<FieldInfo>(key.Type.GetField(key.Name, key.Flags));
            }
            catch (AmbiguousMatchException)
            {
                return new CachedLookup<FieldInfo>(key.Type.GetFields(key.Flags).FirstOrDefault(field => field.Name == key.Name));
            }
        }).Value;
    }

    private static MethodInfo? FindMethod(Type type, string methodName, bool isStatic)
    {
        return MethodCache.GetOrAdd(MethodCacheKey.ForName(type, methodName, isStatic), static key =>
        {
            try
            {
                return new CachedLookup<MethodInfo>(key.Type.GetMethod(key.MethodName, key.Flags));
            }
            catch (AmbiguousMatchException)
            {
                return new CachedLookup<MethodInfo>(key.Type.GetMethods(key.Flags).FirstOrDefault(method => method.Name == key.MethodName));
            }
        }).Value;
    }

    private static MethodInfo? FindMethod(Type type, string methodName, int argCount, bool isStatic)
    {
        return MethodCache.GetOrAdd(MethodCacheKey.ForCount(type, methodName, isStatic, argCount), static key =>
        {
            return new CachedLookup<MethodInfo>(key.Type
                .GetMethods(key.Flags)
                .FirstOrDefault(method => method.Name == key.MethodName && method.GetParameters().Length == key.ArgumentCount));
        }).Value;
    }

    private static MethodInfo? FindMethod(Type type, string methodName, object?[] args, bool isStatic)
    {
        return MethodCache.GetOrAdd(MethodCacheKey.ForArguments(type, methodName, isStatic, args), key =>
        {
            foreach (var method in key.Type.GetMethods(key.Flags).Where(method => method.Name == key.MethodName))
            {
                var parameters = method.GetParameters();
                if (parameters.Length != args.Length) continue;
                if (ArgumentsAreCompatible(parameters, args)) return new CachedLookup<MethodInfo>(method);
            }

            return new CachedLookup<MethodInfo>(null);
        }).Value;
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

    private sealed class CachedLookup<T> where T : class
    {
        public CachedLookup(T? value)
        {
            Value = value;
        }

        public T? Value { get; }
    }

    private readonly record struct MemberCacheKey(Type Type, string Name, BindingFlags Flags);

    private readonly record struct MethodCacheKey(
        Type Type,
        string MethodName,
        BindingFlags Flags,
        int? ArgumentCount,
        string ArgumentSignature)
    {
        public static MethodCacheKey ForName(Type type, string methodName, bool isStatic)
        {
            return new MethodCacheKey(type, methodName, BuildFlags(isStatic), null, "");
        }

        public static MethodCacheKey ForCount(Type type, string methodName, bool isStatic, int argumentCount)
        {
            return new MethodCacheKey(type, methodName, BuildFlags(isStatic), argumentCount, "");
        }

        public static MethodCacheKey ForArguments(Type type, string methodName, bool isStatic, IReadOnlyList<object?> args)
        {
            return new MethodCacheKey(type, methodName, BuildFlags(isStatic), args.Count, BuildArgumentSignature(args));
        }

        private static BindingFlags BuildFlags(bool isStatic)
        {
            return BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        }

        private static string BuildArgumentSignature(IReadOnlyList<object?> args)
        {
            return string.Join("|", args.Select(arg => arg?.GetType().AssemblyQualifiedName ?? "<null>"));
        }
    }
}
