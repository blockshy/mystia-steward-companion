using System.Collections.Concurrent;
using System.Reflection;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 为订单动作缓存反射方法与构造器的纯托管元数据，避免每次原生动作都枚举完整声明表。
/// </summary>
/// <remarks>
/// 缓存只保存 Type、MethodInfo、ConstructorInfo 和参数声明，不持有游戏实例、IL2CPP wrapper、调用参数或返回值。
/// 不缓存最终的参数匹配结果：null、by-ref 与 IConvertible 必须继续按每次调用的实际参数解释，
/// 且重载选择保持原 GetMethods 顺序中的首个匹配，不新增按参数个数或名称猜测的降级路径。
/// </remarks>
internal sealed class RuntimeInvocationMetadataCache
{
    private readonly ConcurrentDictionary<LookupKey, Lazy<MethodCandidate[]>> _entries = new();
    private readonly ConcurrentDictionary<ConstructorLookupKey, Lazy<ConstructorCandidate[]>> _constructors = new();
    private readonly object _admissionLock = new();
    private readonly int _capacity;

    /// <summary>
    /// 设置严格的元数据条目上限。超限查询仍可执行原规则解析，但不保留新键，也不淘汰热键。
    /// </summary>
    public RuntimeInvocationMetadataCache(int capacity = 128)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>
    /// 按原有兼容规则查找首个重载；只解析元数据，本方法不会调用目标方法或读取对象状态。
    /// </summary>
    public MethodInfo? FindMethod(Type type, string methodName, BindingFlags flags, object?[] arguments)
    {
        var key = new LookupKey(type, methodName, flags);
        var entry = GetOrCreateEntry(key);
        try
        {
            // Lazy 仅合并同一个键的并发冷解析；解析在容量锁之外，不串行阻塞其它方法键。
            var candidates = entry.Value;
            if (candidates.Length == 0) RemoveExactEntry(key, entry);
            foreach (var candidate in candidates)
            {
                if (CanUseParameterTypes(candidate.ParameterTypes, arguments)) return candidate.Method;
            }
            return null;
        }
        catch
        {
            // 元数据暂不可读不作为永久失败。仅删除本次 Lazy，避免误删其它线程已恢复的新条目。
            RemoveExactEntry(key, entry);
            throw;
        }
    }

    /// <summary>
    /// 按精确的参数声明查找首个方法，供扣料等必须保持固定原生签名的调用使用。
    /// </summary>
    /// <remarks>
    /// 与普通动作匹配共用声明缓存，但不展开 by-ref、不接受 IConvertible，也不调用目标方法。
    /// 因此 int 与 ref int、bool 与其它可转换类型始终是不同签名。
    /// </remarks>
    public MethodInfo? FindExactMethod(Type type, string methodName, BindingFlags flags, Type[] parameterTypes)
    {
        var key = new LookupKey(type, methodName, flags);
        var entry = GetOrCreateEntry(key);
        try
        {
            var candidates = entry.Value;
            if (candidates.Length == 0) RemoveExactEntry(key, entry);
            foreach (var candidate in candidates)
            {
                if (candidate.DeclaredParameterTypes.SequenceEqual(parameterTypes)) return candidate.Method;
            }
            return null;
        }
        catch
        {
            RemoveExactEntry(key, entry);
            throw;
        }
    }

    /// <summary>
    /// 按原反射顺序查找构造器，并由本次调用者按实际对象及数组候选构造参数。
    /// </summary>
    /// <remarks>
    /// selectArguments 返回 null 表示继续下一个构造器；委托与生成的参数均不进入缓存。
    /// 这里只读取声明，不执行构造器。调用者必须在返回后 Invoke 一次，使原生异常原样传播，
    /// 不会因缓存重试而重复创建料理或其它原生对象。
    /// </remarks>
    public ConstructorInfo? FindConstructor(
        Type type,
        BindingFlags flags,
        Func<ParameterInfo[], object?[]?> selectArguments,
        out object?[]? arguments)
    {
        var key = new ConstructorLookupKey(type, flags);
        var entry = GetOrCreateConstructorEntry(key);
        arguments = null;
        try
        {
            var candidates = entry.Value;
            if (candidates.Length == 0) RemoveExactConstructorEntry(key, entry);
            foreach (var candidate in candidates)
            {
                // 参数声明保持惰性，前一个构造器已匹配时不读取后续声明，保留旧循环的短路行为。
                var selected = selectArguments(candidate.Parameters);
                if (selected == null) continue;
                arguments = selected;
                return candidate.Constructor;
            }
            return null;
        }
        catch
        {
            RemoveExactConstructorEntry(key, entry);
            throw;
        }
    }

    /// <summary>
    /// 供构造器路径按每次实际参数复用同一匹配规则；不会缓存参数或其调用结果。
    /// </summary>
    public static bool CanUseParameters(ParameterInfo[] parameters, object?[] arguments)
    {
        if (parameters.Length != arguments.Length) return false;
        for (var index = 0; index < parameters.Length; index++)
        {
            if (!CanUseParameterType(NormalizeParameterType(parameters[index].ParameterType), arguments[index]))
                return false;
        }
        return true;
    }

    private Lazy<MethodCandidate[]> GetOrCreateEntry(LookupKey key)
    {
        if (_entries.TryGetValue(key, out var cached)) return cached;
        lock (_admissionLock)
        {
            if (_entries.TryGetValue(key, out cached)) return cached;
            var created = new Lazy<MethodCandidate[]>(
                () => ReadCandidates(key), LazyThreadSafetyMode.ExecutionAndPublication);
            // 检查与接纳处于同一短锁内，因此并发首次调用也不能突破容量上限。
            if (_entries.Count + _constructors.Count < _capacity) _entries.TryAdd(key, created);
            return created;
        }
    }

    private Lazy<ConstructorCandidate[]> GetOrCreateConstructorEntry(ConstructorLookupKey key)
    {
        if (_constructors.TryGetValue(key, out var cached)) return cached;
        lock (_admissionLock)
        {
            if (_constructors.TryGetValue(key, out cached)) return cached;
            var created = new Lazy<ConstructorCandidate[]>(
                () => key.Type.GetConstructors(key.Flags)
                    .Select(constructor => new ConstructorCandidate(constructor)).ToArray(),
                LazyThreadSafetyMode.ExecutionAndPublication);
            // 方法与构造器使用同一总容量，加入构造器路径不会使缓存上限隐式翻倍。
            if (_entries.Count + _constructors.Count < _capacity) _constructors.TryAdd(key, created);
            return created;
        }
    }

    private void RemoveExactConstructorEntry(ConstructorLookupKey key, Lazy<ConstructorCandidate[]> entry)
    {
        ((ICollection<KeyValuePair<ConstructorLookupKey, Lazy<ConstructorCandidate[]>>>)_constructors)
            .Remove(new KeyValuePair<ConstructorLookupKey, Lazy<ConstructorCandidate[]>>(key, entry));
    }

    private void RemoveExactEntry(LookupKey key, Lazy<MethodCandidate[]> entry)
    {
        ((ICollection<KeyValuePair<LookupKey, Lazy<MethodCandidate[]>>>)_entries)
            .Remove(new KeyValuePair<LookupKey, Lazy<MethodCandidate[]>>(key, entry));
    }

    private static MethodCandidate[] ReadCandidates(LookupKey key)
    {
        // 不排序、不按“更精确类型”重排：已有调用依赖的是原反射枚举的首个兼容方法。
        return key.Type.GetMethods(key.Flags)
            .Where(method => string.Equals(method.Name, key.MethodName, StringComparison.Ordinal))
            .Select(method => new MethodCandidate(method))
            .ToArray();
    }

    private static bool CanUseParameterTypes(Type[] parameterTypes, object?[] arguments)
    {
        if (parameterTypes.Length != arguments.Length) return false;
        for (var index = 0; index < parameterTypes.Length; index++)
        {
            if (!CanUseParameterType(parameterTypes[index], arguments[index])) return false;
        }
        return true;
    }

    private static Type NormalizeParameterType(Type parameterType)
        => parameterType.IsByRef ? parameterType.GetElementType() ?? parameterType : parameterType;

    private static bool CanUseParameterType(Type parameterType, object? argument)
    {
        // 精确保留原规则：null 不匹配任何值类型（包括 Nullable<T>），其余先检查可赋值关系，
        // 原始类型再接受 IConvertible；这里只选方法，不提前转换或吞掉 Invoke 的转换异常。
        if (argument == null) return !parameterType.IsValueType;
        if (parameterType.IsAssignableFrom(argument.GetType())) return true;
        return parameterType.IsPrimitive && argument is IConvertible;
    }

    private readonly record struct LookupKey(Type Type, string MethodName, BindingFlags Flags);
    private readonly record struct ConstructorLookupKey(Type Type, BindingFlags Flags);
    private sealed class MethodCandidate
    {
        private readonly Lazy<Type[]> _declaredParameterTypes;
        private readonly Lazy<Type[]> _parameterTypes;
        public MethodInfo Method { get; }
        public Type[] DeclaredParameterTypes => _declaredParameterTypes.Value;
        public Type[] ParameterTypes => _parameterTypes.Value;

        public MethodCandidate(MethodInfo method)
        {
            Method = method;
            // 只读取匹配过程实际走到的重载，保持原 FirstOrDefault 的短路语义。
            // 后面的不相关重载即便参数元数据不可读，也不能使已匹配的前一个重载失败。
            _declaredParameterTypes = new Lazy<Type[]>(
                () => method.GetParameters().Select(parameter => parameter.ParameterType).ToArray(),
                LazyThreadSafetyMode.ExecutionAndPublication);
            _parameterTypes = new Lazy<Type[]>(
                () => DeclaredParameterTypes.Select(NormalizeParameterType).ToArray(),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    private sealed class ConstructorCandidate
    {
        private readonly Lazy<ParameterInfo[]> _parameters;
        public ConstructorInfo Constructor { get; }
        public ParameterInfo[] Parameters => _parameters.Value;

        public ConstructorCandidate(ConstructorInfo constructor)
        {
            Constructor = constructor;
            _parameters = new Lazy<ParameterInfo[]>(constructor.GetParameters,
                LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }
}
