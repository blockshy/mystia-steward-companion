using MystiaStewardCompanion.Save;

/// <summary>
/// 名单读取边界的离线回归。FakeBaseWrapper 只保存类型转换映射，模拟 CLR 基类包装无法反射
/// 派生字段的事实；这不是 IL2CPP 实机验证。真实 wrapper 集合签名由 Program 的元数据检查覆盖。
/// </summary>
internal static class RareGuestReaderChecks
{
    private static int _assertions;

    public static void Run()
    {
        _assertions = 0;
        var rare = new GuestValue(3);
        var normal = new GuestValue(3); // 普客与稀客故意同 ID，确保目录匹配不能替代真实类型。
        var typedSpecial = new TypedSpecial(rare);
        var wrappedSpecial = new FakeBaseWrapper(typedSpecial);
        var wrappedNormal = new FakeBaseWrapper(new TypedNormal(normal));
        Check(Resolve(wrappedSpecial, out var controller, out var guest) == RuntimeRareGuestKind.Special
            && ReferenceEquals(controller, typedSpecial) && ReferenceEquals(guest, rare), "base special cast");
        Check(Resolve(wrappedNormal, out _, out guest) == RuntimeRareGuestKind.Normal && guest == null, "normal same ID");
        Check(Resolve(new FakeBaseWrapper(null), out _, out guest) == RuntimeRareGuestKind.Unknown && guest == null,
            "unknown wrapper must remain incomplete");
        Check(Resolve(null, out _, out _) == RuntimeRareGuestKind.Unknown, "missing controller");
        Check(Resolve(new FakeBaseWrapper(new TypedSpecial(null)), out _, out guest) == RuntimeRareGuestKind.Unknown
            && guest == null, "missing special guest");
        var guestBase = new FakeGuestBaseWrapper(rare);
        Check(Resolve(new OrderingOnly(guestBase), out _, out guest) == RuntimeRareGuestKind.Special
            && ReferenceEquals(guest, rare), "exact special guest fallback");
        Check(Resolve(new OrderingOnly(normal), out _, out guest) == RuntimeRareGuestKind.Unknown && guest == null,
            "ordinary guest ID is not proof");
        Check(RuntimeRareGuestReader.Resolve(wrappedSpecial, out controller, out guest, out _,
            (_, _) => throw new InvalidOperationException("cast"), Read) == RuntimeRareGuestKind.Unknown
            && controller == null && guest == null, "cast failure clears result");
        Check(RuntimeRareGuestReader.Resolve(wrappedSpecial, out _, out guest, out _, Cast,
            (_, _) => throw new InvalidOperationException("read")) == RuntimeRareGuestKind.Unknown && guest == null,
            "property failure clears result");

        // 稀疏桌号不能按 Count 索引；每一种确认过的集合必须完整返回实际控制器。
        var map = new Dictionary<int, object> { [7] = wrappedSpecial, [28] = wrappedNormal };
        var desks = RuntimeRareGuestReader.ReadControllers(map, RuntimeGuestControllerCollection.DeskDictionary);
        Check(desks.Count == 2 && desks.Contains(wrappedSpecial) && desks.Contains(wrappedNormal), "sparse desk keys");
        var set = new HashSet<object> { wrappedSpecial, wrappedNormal };
        Check(RuntimeRareGuestReader.ReadControllers(set, RuntimeGuestControllerCollection.PresentedSet).Count == 2,
            "presented concrete set");
        Check(RuntimeRareGuestReader.ReadControllers(new List<object> { wrappedSpecial },
            RuntimeGuestControllerCollection.QueueList).Count == 1, "concrete queue list");
        foreach (var kind in Enum.GetValues<RuntimeGuestControllerCollection>())
        {
            Reject(() => RuntimeRareGuestReader.ReadControllers(null, kind), "missing collection cannot mean empty");
            Reject(() => RuntimeRareGuestReader.ReadControllers(new object(), kind), "unsupported collection");
        }
        Reject(() => RuntimeRareGuestReader.ReadControllers(new Dictionary<string, object> { ["7"] = wrappedSpecial },
            RuntimeGuestControllerCollection.DeskDictionary), "wrong desk key type");
        Reject(() => RuntimeRareGuestReader.ReadControllers(new Dictionary<string, object>(),
            RuntimeGuestControllerCollection.DeskDictionary), "empty wrong desk key type");
        Check(RuntimeRareGuestReader.ReadControllers(new HashSet<object>(),
            RuntimeGuestControllerCollection.PresentedSet).Count == 0, "confirmed empty set");

        // 使用具有准确泛型/枚举器形状的托管替身注入每种失败，不能把部分值发布成完整名单。
        foreach (var fault in new[] { "move", "current", "dispose", "short", "extra", "count" })
        {
            var probe = new Il2CppSystem.Collections.Generic.HashSet<object>(new object[] { wrappedSpecial, wrappedNormal }, fault);
            Check(!RuntimeConcreteCollectionReader.TryReadHashSet(probe, out var values, out var failure)
                && values.Count == 0 && failure != RuntimeCollectionReadFailure.None && probe.DisposeCalls == 1,
                $"set fault {fault}");
            Reject(() => RuntimeRareGuestReader.ReadControllers(probe, RuntimeGuestControllerCollection.PresentedSet),
                $"provider collection fault {fault}");
        }
        Console.WriteLine($"PASS: rare guest exact-type, sparse collection, incomplete/failed read boundaries ({_assertions} assertions).");
    }

    private static RuntimeRareGuestKind Resolve(object? value, out object? controller, out object? guest)
        => RuntimeRareGuestReader.Resolve(value, out controller, out guest, out _, Cast, Read);

    private static object? Cast(object? value, string name)
    {
        var actual = value is FakeBaseWrapper wrapper ? wrapper.Actual : value;
        if (name == RuntimeRareGuestReader.SpecialControllerType) return actual as TypedSpecial;
        if (name == RuntimeRareGuestReader.NormalControllerType) return actual as TypedNormal;
        // GuestValue 本身不携带身份；仅真实稀客控制器的属性或已确认 guest wrapper 允许转换。
        if (name == RuntimeRareGuestReader.SpecialGuestType)
            return value is ConfirmedSpecial confirmed ? confirmed.Guest
                : value is FakeGuestBaseWrapper guestBase ? guestBase.Actual : null;
        return null;
    }

    private static object? Read(object? value, string name) => (value, name) switch
    {
        (TypedSpecial special, "SpecialGuest") => special.Guest == null ? null : new ConfirmedSpecial(special.Guest),
        (OrderingOnly ordering, "OrderingGuest") => ordering.Guest,
        _ => null,
    };

    private static void Check(bool result, string label)
    {
        if (!result) throw new InvalidOperationException(label);
        _assertions++;
    }

    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException) { _assertions++; return; }
        throw new InvalidOperationException(label);
    }

    private sealed record GuestValue(int Id);
    private sealed record FakeBaseWrapper(object? Actual);
    private sealed record FakeGuestBaseWrapper(object Actual);
    private sealed record TypedSpecial(GuestValue? Guest);
    private sealed record TypedNormal(GuestValue Guest);
    private sealed record ConfirmedSpecial(GuestValue Guest);
    private sealed record OrderingOnly(object Guest);
}
