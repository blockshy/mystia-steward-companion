using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 为运行时适配层提供兼容性反射读取；缺失成员和调用失败通常折叠为 null。
/// 本类不调度线程，也不延长 Unity/IL2CPP 对象寿命；调用方须在允许访问对象的线程和生命周期内使用。
/// 精确身份、集合完整性及可执行状态仍需由专用读取器验证，不能仅凭兼容性读取成功推断。
/// </summary>
internal static class RuntimeReflectionUtility
{
    // 缓存仅保存类型和反射元数据，不持有游戏实例；并发字典不代表被反射对象可跨线程访问。
    private static readonly ConcurrentDictionary<string, Type> TypeCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<MemberCacheKey, CachedLookup<PropertyInfo>> PropertyCache = new();
    private static readonly ConcurrentDictionary<MemberCacheKey, CachedLookup<FieldInfo>> FieldCache = new();
    private static readonly ConcurrentDictionary<MethodCacheKey, CachedLookup<MethodInfo>> MethodCache = new();
    private static readonly ConcurrentDictionary<Type, CachedLookup<MethodInfo>> TryCastMethodCache = new();

    /// <summary>查找已经加载的类型；仅缓存命中，以便后续程序集加载后能够重试。</summary>
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
                // 单个程序集无法解析目标类型时继续搜索，不将其视为全局不存在的证据。
            }
        }

        // 插件启动之后仍可能加载程序集，失败结果不能进入永久类型缓存。
        return null;
    }

    /// <summary>通过 IL2CPP 包装器的泛型转换尝试取得目标视图；不支持的类型或转换失败返回 null。</summary>
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

    /// <summary>按既有命名候选查找首个非空静态实例；结果仍须由业务读取器核对对象身份和状态。</summary>
    public static object? GetSingletonInstance(Type type)
    {
        foreach (var name in new[] { "Instance", "UniqueInstance", "instance", "m_Instance", "m_instance", "s_Instance", "m_UniqueInstance", "Main", "Singleton", "Current" })
        {
            var value = GetStaticMemberValue(type, name);
            if (value != null) return value;
        }

        return null;
    }

    /// <summary>调用 Unity 的单对象查找入口；空值同时涵盖未找到、入口缺失和调用失败。</summary>
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

    /// <summary>延迟枚举 Unity 查找结果；枚举过程也必须留在允许访问 Unity 对象的线程。</summary>
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

    /// <summary>
    /// 合并常规查找与 Resources 查找，尽量覆盖非活动对象；两路结果可能重复。
    /// 场景过滤仅用于排除可识别的非运行场景对象，不保证结果完整或对象仍然存活。
    /// </summary>
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

    /// <summary>使用可读的场景信息作兼容性过滤；缺少 gameObject 或 scene 时保留候选。</summary>
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

    /// <summary>按参数兼容性、参数数量及名称依次选取候选；无法调用时返回 null。</summary>
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

    /// <summary>
    /// 尝试调用实例成员；候选匹配属于兼容性策略，不构成精确原生签名的验证。
    /// 返回 null 无法区分合法空返回值、成员缺失和执行异常。
    /// </summary>
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

    /// <summary>沿继承链按候选名称读取非空静态字段或属性；空值和读取失败均继续尝试。</summary>
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

    /// <summary>
    /// 依次尝试已知字段命名、属性、字段和零参数方法，以适配包装器差异。
    /// 属性和方法可能执行游戏代码；本方法并非纯内存字段读取，也不证明读取结果来自同一时刻。
    /// </summary>
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
                    // 当前候选无法调用时继续兼容性查找，不把异常解释为业务空值。
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 尽力枚举兼容对象；中途异常、空索引项或不支持的形状可能使结果不完整。
    /// 需要证明集合完整性的路径应使用 RuntimeConcreteCollectionReader，并检查其失败原因。
    /// </summary>
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

        // IL2CPP 包装器不强转为托管 IEnumerable；Values 与索引器是独立尝试，结果可能重复。
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
                    // 保留后续可读项；调用方不得把已经产出的部分项视作完整集合。
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

    /// <summary>优先读取 Count/Length；最后的枚举回退最多计数 256 项，只适合兼容性诊断。</summary>
    public static int CountObjects(object? value)
    {
        if (value == null) return 0;
        var count = ToInt(GetMemberValue(value, "Count"), int.MinValue);
        if (count != int.MinValue) return count;
        var length = ToInt(GetMemberValue(value, "Length"), int.MinValue);
        if (length != int.MinValue) return length;
        return EnumerateObjects(value).Take(256).Count();
    }

    /// <summary>用于宽松展示转换；无法匹配或解析时采用默认值，不提供范围、溢出异常和精确身份保证。</summary>
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

    /// <summary>将空值和无法解析的值折叠为 false；需要区分未知状态的读取器应自行保留可读性。</summary>
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

    /// <summary>
    /// 获取兼容性对象标识；原生指针不可读时回退到托管对象哈希，不能用作精确原生身份凭据。
    /// </summary>
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

    /// <summary>仅接受可读且非零的原生指针值；成功不保证对象存活或指针不会在以后复用。</summary>
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

    // 此处只筛选候选，不执行参数转换；宽松匹配仍可能在反射调用阶段失败。
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
