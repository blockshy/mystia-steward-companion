using System.Reflection;
using MystiaStewardCompanion.Save;

/// <summary>
/// 验证扣料精确签名与料理构造器的元数据缓存，不加载或执行真实游戏对象。
/// </summary>
/// <remarks>
/// 差分基准保留修改前的反射循环，检查的是选择、调用与失败边界，不预设 CLR 的重载顺序。
/// 扫描计数仅证明重复元数据解析被消除，不能换算为游戏帧率或真实动作节省的毫秒数。
/// </remarks>
internal static class CookingMetadataCacheChecks
{
    private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags ConstructorFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type[] StorageSignature = { typeof(int), typeof(bool) };

    public static void Run()
    {
        AssertExactStorageSignatureAndInvocation();
        AssertConstructorSelectionAndInstanceOwnership();
        AssertConstructorExceptionIsNotRetried();
        AssertMetadataFailuresAndMissingDeclarationsRecover();
        AssertConcurrentColdLookups();
        AssertSharedCapacity();
        Console.WriteLine("PASS: cooking metadata preserves exact storage signatures, constructor order, live arguments, exceptions, retry, concurrency and shared capacity.");
    }

    private static void AssertExactStorageSignatureAndInvocation()
    {
        var cache = new RuntimeInvocationMetadataCache();
        var type = new CountingType(typeof(StorageTarget));
        var expected = typeof(StorageTarget).GetMethods(StaticFlags).FirstOrDefault(method =>
        {
            if (method.Name != "IngredientOut") return false;
            var parameters = method.GetParameters();
            return parameters.Length == 2 && parameters[0].ParameterType == typeof(int)
                && parameters[1].ParameterType == typeof(bool);
        });
        StorageTarget.Calls.Clear();
        foreach (var id in new[] { 15, 20, 28 })
        {
            var actual = cache.FindExactMethod(type, "IngredientOut", StaticFlags, StorageSignature);
            Equal(expected, actual, "扣料必须保持旧首个精确签名，不能选可转换的 long/object/ref 重载。");
            actual!.Invoke(null, new object?[] { id, false });
        }
        Equal(1, type.MethodScans, "三种材料不应重复枚举同一扣料方法表。");
        True(StorageTarget.Calls.SequenceEqual(new[] { 15, 20, 28 }), "材料必须逐项实际调用，不能缓存参数或结果。");
        Equal<MethodInfo?>(null, cache.FindExactMethod(type, "ByRefOnly", StaticFlags, StorageSignature),
            "精确签名不能把 ref int 展开成 int。");
        Equal<MethodInfo?>(null, cache.FindExactMethod(type, "ConvertibleOnly", StaticFlags, StorageSignature),
            "精确签名不能通过 IConvertible 放宽 long 为 int。");
    }

    private static void AssertConstructorSelectionAndInstanceOwnership()
    {
        var cache = new RuntimeInvocationMetadataCache();
        var type = new CountingType(typeof(ComboTarget));
        foreach (var recipe in new object[] { new Recipe(), new Recipe(), new object() })
        {
            var extras = new[] { 7, 7, 12 };
            var expected = LegacyFindConstructor(typeof(ComboTarget), recipe, extras, out _);
            var actual = cache.FindConstructor(type, ConstructorFlags,
                parameters => SelectArguments(parameters, recipe, extras), out var arguments);
            Equal(expected, actual, "构造器选择必须与旧循环顺序完全一致。");
            if (actual == null)
            {
                Equal<object?[]?>(null, arguments, "不匹配的实际配方不能复用上一轮参数。");
                continue;
            }
            var result = (ComboTarget)actual.Invoke(arguments);
            True(ReferenceEquals(recipe, result.Recipe), "构造器缓存不能保留上一轮配方实例。");
            True(result.Extras.SequenceEqual(extras), "重复食材及原顺序不能在声明缓存中去重或排序。");
        }
        Equal(1, type.ConstructorScans, "不同配方实例应共用一次构造声明扫描。");
    }

    private static void AssertConstructorExceptionIsNotRetried()
    {
        var cache = new RuntimeInvocationMetadataCache();
        ThrowingCombo.Calls = 0;
        var constructor = cache.FindConstructor(typeof(ThrowingCombo), ConstructorFlags,
            _ => Array.Empty<object?>(), out var arguments)!;
        try
        {
            constructor.Invoke(arguments);
            throw new InvalidOperationException("首个目标构造器应抛出异常。");
        }
        catch (TargetInvocationException exception)
        {
            True(exception.InnerException is ProbeException, "原始构造器异常不能被改写。");
        }
        Equal(1, ThrowingCombo.Calls, "一次失败的原生动作不能被元数据缓存隐式重试。");
        var next = cache.FindConstructor(typeof(ThrowingCombo), ConstructorFlags,
            _ => Array.Empty<object?>(), out var nextArguments)!;
        Equal(constructor, next, "调用异常不应丢失有效元数据。");
        next.Invoke(nextArguments);
        Equal(2, ThrowingCombo.Calls, "下一次显式调用仍应正常执行。");
    }

    private static void AssertMetadataFailuresAndMissingDeclarationsRecover()
    {
        var cache = new RuntimeInvocationMetadataCache();
        var failedMethod = new CountingType(typeof(StorageTarget)) { FailFirstMethodScan = true };
        try
        {
            cache.FindExactMethod(failedMethod, "IngredientOut", StaticFlags, StorageSignature);
            throw new InvalidOperationException("首次方法元数据故障应原样传播。");
        }
        catch (ProbeException) { }
        True(cache.FindExactMethod(failedMethod, "IngredientOut", StaticFlags, StorageSignature) != null,
            "方法元数据一次故障不得永久失败。");
        Equal(2, failedMethod.MethodScans, "方法故障后必须重新读取声明。");

        var failedConstructor = new CountingType(typeof(ThrowingCombo)) { FailFirstConstructorScan = true };
        try
        {
            cache.FindConstructor(failedConstructor, ConstructorFlags, _ => Array.Empty<object?>(), out _);
            throw new InvalidOperationException("首次构造元数据故障应原样传播。");
        }
        catch (ProbeException) { }
        True(cache.FindConstructor(failedConstructor, ConstructorFlags, _ => Array.Empty<object?>(), out _) != null,
            "构造元数据一次故障不得永久失败。");
        Equal(2, failedConstructor.ConstructorScans, "构造故障后必须重新读取声明。");

        // TypeDelegator 沿用底层 Type 的相等性；这里用独立缓存模拟另一轮冷查询，
        // 避免上一项已恢复的 ThrowingCombo 声明遮住本项“首次暂缺”的故障注入。
        cache = new RuntimeInvocationMetadataCache();
        var missingConstructor = new CountingType(typeof(ThrowingCombo)) { EmptyFirstConstructorScan = true };
        Equal<ConstructorInfo?>(null, cache.FindConstructor(missingConstructor, ConstructorFlags,
            _ => Array.Empty<object?>(), out _), "暂缺构造器时不得猜测替代声明。");
        True(cache.FindConstructor(missingConstructor, ConstructorFlags, _ => Array.Empty<object?>(), out _) != null,
            "暂缺构造器不应负缓存。");
        Equal(2, missingConstructor.ConstructorScans, "缺失声明恢复时必须重新解析。");
    }

    private static void AssertConcurrentColdLookups()
    {
        var cache = new RuntimeInvocationMetadataCache();
        var storage = new CountingType(typeof(StorageTarget));
        var combos = new CountingType(typeof(ComboTarget));
        using var start = new ManualResetEventSlim(false);
        var workers = Enumerable.Range(0, 24).Select(index => Task.Run(() =>
        {
            start.Wait();
            True(cache.FindExactMethod(storage, "IngredientOut", StaticFlags, StorageSignature) != null,
                "并发扣料声明查找失败。");
            var recipe = new Recipe();
            var extras = new[] { index, index };
            var constructor = cache.FindConstructor(combos, ConstructorFlags,
                parameters => SelectArguments(parameters, recipe, extras), out var arguments)!;
            var result = (ComboTarget)constructor.Invoke(arguments);
            True(ReferenceEquals(recipe, result.Recipe) && result.Extras.SequenceEqual(extras),
                "并发构造器查找污染了其它调用的配方或加料参数。");
        })).ToArray();
        start.Set();
        Task.WaitAll(workers);
        Equal(1, storage.MethodScans, "同键并发精确方法冷解析应只扫描一次。");
        Equal(1, combos.ConstructorScans, "同键并发构造器冷解析应只扫描一次。");
    }

    private static void AssertSharedCapacity()
    {
        // 方法与构造器必须共享一个上限；超限仍按原规则查询，但不挤掉先前的热声明。
        var cache = new RuntimeInvocationMetadataCache(capacity: 1);
        var storage = new CountingType(typeof(StorageTarget));
        var combos = new CountingType(typeof(ThrowingCombo));
        cache.FindExactMethod(storage, "IngredientOut", StaticFlags, StorageSignature);
        for (var index = 0; index < 2; index++)
            cache.FindConstructor(combos, ConstructorFlags, _ => Array.Empty<object?>(), out _);
        cache.FindExactMethod(storage, "IngredientOut", StaticFlags, StorageSignature);
        Equal(1, storage.MethodScans, "构造器超限查询不能淘汰已缓存方法。");
        Equal(2, combos.ConstructorScans, "构造器不得绕过共享缓存容量。");

        cache = new RuntimeInvocationMetadataCache(capacity: 1);
        storage = new CountingType(typeof(StorageTarget));
        combos = new CountingType(typeof(ThrowingCombo));
        cache.FindConstructor(combos, ConstructorFlags, _ => Array.Empty<object?>(), out _);
        for (var index = 0; index < 2; index++)
            cache.FindExactMethod(storage, "IngredientOut", StaticFlags, StorageSignature);
        cache.FindConstructor(combos, ConstructorFlags, _ => Array.Empty<object?>(), out _);
        Equal(1, combos.ConstructorScans, "方法超限查询不能淘汰已缓存构造器。");
        Equal(2, storage.MethodScans, "方法不得绕过共享缓存容量。");
    }

    private static ConstructorInfo? LegacyFindConstructor(Type type, object recipe, int[] extras, out object?[]? arguments)
    {
        foreach (var constructor in type.GetConstructors(ConstructorFlags))
        {
            var selected = SelectArguments(constructor.GetParameters(), recipe, extras);
            if (selected == null) continue;
            arguments = selected;
            return constructor;
        }
        arguments = null;
        return null;
    }

    /// <summary>模拟生产的配方实例筛选与原顺序数组候选；每次返回独立的动作参数。</summary>
    private static object?[]? SelectArguments(ParameterInfo[] parameters, object recipe, int[] extras)
    {
        if (parameters.Length != 2 || !parameters[0].ParameterType.IsInstanceOfType(recipe)) return null;
        foreach (var candidate in new object[] { extras.ToArray(), extras.ToList() })
        {
            var arguments = new object?[] { recipe, candidate };
            if (RuntimeInvocationMetadataCache.CanUseParameters(parameters, arguments)) return arguments;
        }
        return null;
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} expected={expected}; actual={actual}");
    }

    /// <summary>只代理托管类型声明，计数及临时故障均不影响真实游戏环境。</summary>
    private sealed class CountingType : TypeDelegator
    {
        private int _methodScans;
        private int _constructorScans;
        public bool FailFirstMethodScan { get; init; }
        public bool FailFirstConstructorScan { get; init; }
        public bool EmptyFirstConstructorScan { get; init; }
        public int MethodScans => Volatile.Read(ref _methodScans);
        public int ConstructorScans => Volatile.Read(ref _constructorScans);
        public CountingType(Type type) : base(type) { }
        public override MethodInfo[] GetMethods(BindingFlags bindingAttr)
        {
            var count = Interlocked.Increment(ref _methodScans);
            if (count == 1 && FailFirstMethodScan) throw new ProbeException();
            return base.GetMethods(bindingAttr);
        }
        public override ConstructorInfo[] GetConstructors(BindingFlags bindingAttr)
        {
            var count = Interlocked.Increment(ref _constructorScans);
            if (count == 1 && FailFirstConstructorScan) throw new ProbeException();
            if (count == 1 && EmptyFirstConstructorScan) return Array.Empty<ConstructorInfo>();
            return base.GetConstructors(bindingAttr);
        }
    }

    private sealed class ProbeException : Exception { }
    private sealed class Recipe { }
    private sealed class ComboTarget
    {
        public object Recipe { get; }
        public IReadOnlyList<int> Extras { get; }
        public ComboTarget(Recipe recipe, int[] extras) { Recipe = recipe; Extras = extras; }
        public ComboTarget(Recipe recipe, List<int> extras) { Recipe = recipe; Extras = extras; }
    }
    private sealed class ThrowingCombo
    {
        public static int Calls;
        public ThrowingCombo() { if (++Calls == 1) throw new ProbeException(); }
    }
    private static class StorageTarget
    {
        public static readonly List<int> Calls = new();
        public static void IngredientOut(long id, bool flag) => throw new InvalidOperationException("错误 long 重载。");
        public static void IngredientOut(object id, bool flag) => throw new InvalidOperationException("错误 object 重载。");
        public static void IngredientOut(ref int id, bool flag) => throw new InvalidOperationException("错误 ref 重载。");
        public static void IngredientOut(int id, bool flag) { True(!flag, "扣料标志不得改变。"); Calls.Add(id); }
        public static void ByRefOnly(ref int id, bool flag) { }
        public static void ConvertibleOnly(long id, bool flag) { }
    }
}
