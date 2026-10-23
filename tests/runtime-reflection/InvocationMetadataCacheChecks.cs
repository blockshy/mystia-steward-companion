using System.Reflection;
using MystiaStewardCompanion.Save;

/// <summary>
/// 使用纯托管目标验证动作反射缓存；不创建或调用 Unity/IL2CPP 对象。
/// </summary>
internal static class InvocationMetadataCacheChecks
{
    private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    public static void Run()
    {
        AssertLegacySelectionAndNullSemantics();
        AssertInvocationAndByRefStayLive();
        AssertFailureAndMissingLookupRecover();
        AssertConcurrentColdLookup();
        AssertBoundedAdmission();
        Console.WriteLine("PASS: invocation metadata retains legacy overload/null/by-ref rules, live exceptions, retry, concurrency and bounded admission.");
    }

    private static void AssertLegacySelectionAndNullSemantics()
    {
        var cache = new RuntimeInvocationMetadataCache();
        var type = new CountingType(typeof(InvocationTarget));
        var arguments = new object?[][]
        {
            new object?[] { "文字" }, new object?[] { 17 }, new object?[] { new object() },
            new object?[] { null }, Array.Empty<object?>(), new object?[] { "值", 2 },
        };
        foreach (var values in arguments)
        {
            // 独立保留修改前的解析规则作为差分基准，不假定 CLR 对重载的枚举顺序。
            Equal(LegacyFind(typeof(InvocationTarget), "Choose", InstanceFlags, values),
                cache.FindMethod(type, "Choose", InstanceFlags, values), "重载或 null 选择偏离原规则。");
        }
        Equal(1, type.Scans, "相同方法名不同参数重复枚举了完整元数据。");
        Equal<MethodInfo?>(null, cache.FindMethod(type, "NumericOnly", InstanceFlags, new object?[] { null }),
            "null 不得因已缓存 int 重载而被当成 0。");
        True(cache.FindMethod(type, "NumericOnly", InstanceFlags, new object?[] { 2 }) != null,
            "不匹配参数的首次查找不得毒化之后的合法调用。");
        Equal<MethodInfo?>(null, cache.FindMethod(type, "NullableOnly", InstanceFlags, new object?[] { null }),
            "原规则拒绝 null 值类型，缓存不得擅自放宽 Nullable<T>。");
        True(cache.FindMethod(type, "StaticOnly", StaticFlags, new object?[] { 3 }) != null,
            "静态方法未按原 flags 解析。");
        Equal<MethodInfo?>(null, cache.FindMethod(type, "StaticOnly", InstanceFlags, new object?[] { 3 }),
            "实例与静态方法缓存键必须隔离。");
    }

    private static void AssertInvocationAndByRefStayLive()
    {
        var cache = new RuntimeInvocationMetadataCache();
        var first = new InvocationTarget();
        var second = new InvocationTarget();
        var method = cache.FindMethod(typeof(InvocationTarget), "Next", InstanceFlags, Array.Empty<object?>())!;
        Equal(1, (int)method.Invoke(first, Array.Empty<object?>())!, "首次实例调用未执行。");
        Equal(2, (int)method.Invoke(first, Array.Empty<object?>())!, "方法缓存不能缓存返回值。");
        Equal(1, (int)method.Invoke(second, Array.Empty<object?>())!, "方法缓存不能绑定先前实例。");

        var values = new object?[] { 4 };
        var byRef = cache.FindMethod(typeof(InvocationTarget), "Increment", InstanceFlags, values)!;
        byRef.Invoke(first, values);
        Equal(5, (int)values[0]!, "by-ref 原始参数更新被改变。");
        Equal<MethodInfo?>(null, cache.FindMethod(typeof(InvocationTarget), "Increment", InstanceFlags, new object?[] { null }),
            "by-ref int 的 null 匹配不得被上次成功调用缓存。");

        var throws = cache.FindMethod(typeof(InvocationTarget), "ThrowOnce", InstanceFlags, Array.Empty<object?>())!;
        try
        {
            throws.Invoke(first, Array.Empty<object?>());
            throw new InvalidOperationException("故障调用应抛出 TargetInvocationException。");
        }
        catch (TargetInvocationException exception)
        {
            True(exception.InnerException is ProbeException, "原始目标异常类型被吞掉或改写。");
        }
        Equal(1, first.ThrowCalls, "异常调用不得隐式执行第二次。");
        Equal(2, (int)throws.Invoke(first, Array.Empty<object?>())!, "方法调用失败不应毒化元数据缓存。");

        // 原兼容规则允许 primitive + IConvertible。无效值的转换错误仍交给原 Invoke，
        // 元数据缓存既不能以值内容换重载，也不能把失败静默改成默认值。
        var invalid = new object?[] { "不是数字" };
        var numeric = cache.FindMethod(typeof(InvocationTarget), "NumericOnly", InstanceFlags, invalid)!;
        try
        {
            numeric.Invoke(first, invalid);
            throw new InvalidOperationException("无效参数转换应失败。");
        }
        catch (ArgumentException) { }
    }

    private static void AssertFailureAndMissingLookupRecover()
    {
        var cache = new RuntimeInvocationMetadataCache();
        var type = new CountingType(typeof(InvocationTarget)) { FailNextScan = true };
        try
        {
            cache.FindMethod(type, "Next", InstanceFlags, Array.Empty<object?>());
            throw new InvalidOperationException("元数据故障应向调用方传播。");
        }
        catch (ProbeException) { }
        True(cache.FindMethod(type, "Next", InstanceFlags, Array.Empty<object?>()) != null,
            "一次元数据故障被永久缓存。");
        Equal(2, type.Scans, "失败后的元数据应重新解析一次。");
        cache.FindMethod(type, "Missing", InstanceFlags, Array.Empty<object?>());
        cache.FindMethod(type, "Missing", InstanceFlags, Array.Empty<object?>());
        Equal(4, type.Scans, "缺失方法名称不得永久占用成功缓存条目。");
    }

    private static void AssertConcurrentColdLookup()
    {
        var cache = new RuntimeInvocationMetadataCache();
        var type = new CountingType(typeof(InvocationTarget));
        using var start = new ManualResetEventSlim(false);
        var workers = Enumerable.Range(0, 24).Select(index => Task.Run(() =>
        {
            start.Wait();
            var arguments = new object?[] { index % 2 == 0 ? null : "并发" };
            Equal(LegacyFind(typeof(InvocationTarget), "Choose", InstanceFlags, arguments),
                cache.FindMethod(type, "Choose", InstanceFlags, arguments), "并发参数解析互相污染。");
        })).ToArray();
        start.Set();
        Task.WaitAll(workers);
        Equal(1, type.Scans, "同键并发冷查询应只枚举一次元数据。");
    }

    private static void AssertBoundedAdmission()
    {
        var cache = new RuntimeInvocationMetadataCache(capacity: 1);
        var type = new CountingType(typeof(InvocationTarget));
        cache.FindMethod(type, "Next", InstanceFlags, Array.Empty<object?>());
        cache.FindMethod(type, "NumericOnly", InstanceFlags, new object?[] { 1 });
        cache.FindMethod(type, "NumericOnly", InstanceFlags, new object?[] { 2 });
        cache.FindMethod(type, "Next", InstanceFlags, Array.Empty<object?>());
        Equal(3, type.Scans, "容量已满时新键应不缓存，原热键必须继续可用。");
    }

    private static MethodInfo? LegacyFind(Type type, string name, BindingFlags flags, object?[] arguments)
        => type.GetMethods(flags).FirstOrDefault(method => method.Name == name && LegacyCanUse(method.GetParameters(), arguments));

    private static bool LegacyCanUse(ParameterInfo[] parameters, object?[] arguments)
    {
        if (parameters.Length != arguments.Length) return false;
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index].ParameterType;
            if (parameter.IsByRef) parameter = parameter.GetElementType() ?? parameter;
            if (arguments[index] == null)
            {
                if (parameter.IsValueType) return false;
                continue;
            }
            if (parameter.IsAssignableFrom(arguments[index]!.GetType())) continue;
            if (parameter.IsPrimitive && arguments[index] is IConvertible) continue;
            return false;
        }
        return true;
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

    /// <summary>记录实际冷解析次数并注入一次元数据异常，不涉及任何游戏程序集加载。</summary>
    private sealed class CountingType : TypeDelegator
    {
        private int _scans;
        public bool FailNextScan { get; init; }
        public int Scans => Volatile.Read(ref _scans);
        public CountingType(Type type) : base(type) { }
        public override MethodInfo[] GetMethods(BindingFlags bindingAttr)
        {
            var count = Interlocked.Increment(ref _scans);
            if (FailNextScan && count == 1) throw new ProbeException();
            return base.GetMethods(bindingAttr);
        }
    }

    private sealed class ProbeException : Exception { }

    private sealed class InvocationTarget
    {
        private int _calls;
        public int ThrowCalls { get; private set; }
        public string Choose(string? value) => "string";
        public string Choose(object? value) => "object";
        public string Choose(int value) => "int";
        public int NumericOnly(int value) => value;
        public int? NullableOnly(int? value) => value;
        public static int StaticOnly(int value) => value;
        public int Next() => ++_calls;
        public void Increment(ref int value) => value++;
        public int ThrowOnce()
        {
            if (++ThrowCalls == 1) throw new ProbeException();
            return ThrowCalls;
        }
    }
}
