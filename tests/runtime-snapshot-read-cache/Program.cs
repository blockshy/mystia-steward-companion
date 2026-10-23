using System.Text.Json;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.Save;

var assertions = 0;
try
{
    VerifyNormalAuthorityAndVisibleReads();
    VerifyFailuresAndCaptureBoundaries();
    VerifyRareControllerReads();
    VerifyRepositoryRefresh();
    VerifyOrderCountScaling();
    VerifyGuestImageIdentity();
    Console.WriteLine($"PASS: {assertions} snapshot-read-cache assertions; native-key reuse, source/DTO preservation, fail-closed reads, capture reset and catalog replacement.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

void VerifyNormalAuthorityAndVisibleReads()
{
    var cache = new RuntimeSnapshotReadCache();
    var reads = 0;
    cache.RememberCapturedNormal(11);
    cache.NormalCaptureIsAuthoritative = true;
    foreach (var source in new[] { "OrderController", "OrderControllerElement", "HUD" })
    {
        var hidden = cache.ReadVisibleNormal(11, source, () => { reads++; return Normal(source); });
        Check(hidden == null, "已确认捕获的同指针可视项未提前剔除。");
    }
    Equal(0, reads, "已确认捕获仍被可视来源重复反射读取。");

    var original = cache.ReadVisibleNormal(22, "OrderController", () => { reads++; return Normal("OrderController"); })!;
    var reused = cache.ReadVisibleNormal(22, "HUD", () => throw new InvalidOperationException("同原生对象不应重读。"))!;
    SameDtoExceptSource(original, reused, "HUD");
    Check(!reused.CanAutomate && !reused.ControllerAvailable && reused.OrderLifecycleSequence == -1,
        "无控制器可视订单因复用获得执行权限。");
    Check(!ReferenceEquals(original.FoodPreferenceTags, reused.FoodPreferenceTags), "可视副本共享可变标签列表。");
    cache.ReadVisibleNormal(23, "HUD", () => { reads++; return Normal("HUD"); });
    Equal(2, reads, "同桌号/同料理的不同原生订单被错误合并。");

    cache.Clear();
    cache.RememberCapturedNormal(11);
    var notReady = cache.ReadVisibleNormal(11, "HUD", () => { reads++; return Normal("HUD"); });
    Check(notReady != null && !notReady.CanAutomate, "Hook 未完整就绪时丢失只读可视订单。");
}

void VerifyFailuresAndCaptureBoundaries()
{
    var cache = new RuntimeSnapshotReadCache();
    var reads = 0;
    Equal<NormalBusinessOrder?>(null, cache.ReadVisibleNormal(1, "HUD", () => { reads++; return null; }), "首次空结果异常。");
    Check(cache.ReadVisibleNormal(1, "OrderController", () => { reads++; return Normal("OrderController"); }) != null,
        "一次失败阻止后续来源重新读取。");
    Equal(2, reads, "空结果被错误缓存。");
    for (var index = 0; index < 2; index++)
    {
        cache.ReadVisibleNormal(0, "HUD", () => { reads++; return Normal("HUD"); });
    }
    Equal(4, reads, "缺原生指针的对象被合并。");
    try
    {
        cache.ReadVisibleNormal(2, "HUD", () => throw new InvalidOperationException("synthetic read failure"));
        throw new Exception("读取异常被吞掉。");
    }
    catch (InvalidOperationException ex) when (ex.Message == "synthetic read failure") { }
    Check(cache.ReadVisibleNormal(2, "HUD", () => Normal("HUD")) != null, "读取异常污染后续来源。");

    cache.NormalCaptureIsAuthoritative = true;
    cache.RememberCapturedNormal(3);
    cache.Clear();
    Check(!cache.NormalCaptureIsAuthoritative, "新采集继承了旧就绪状态。");
    Check(cache.ReadVisibleNormal(3, "HUD", () => Normal("HUD")) != null, "新采集继承旧原生键，误删对象池新订单。");
    var newValue = cache.ReadVisibleNormal(1, "HUD", () => new NormalBusinessOrder { FoodId = 999, Source = "HUD" });
    Equal(999, newValue!.FoodId, "缓存跨越采集边界后仍使用旧订单字段。");
}

void VerifyRareControllerReads()
{
    var cache = new RuntimeSnapshotReadCache();
    var reads = 0;
    NightBusinessGuest Read(string source)
    {
        reads++;
        return new NightBusinessGuest
        {
            DeskCode = 2, GuestId = 19, GuestName = "合成客人", Source = source,
            Fund = 200, BaseFundCarry = 150, MaxFundCarry = 250, ExtraFundByBuff = 50, WillPayMoney = false,
        };
    }
    var original = cache.ReadRareGuest(8, "Presented", () => Read("Presented"))!;
    foreach (var source in new[] { "DeskMap", "Repellable", "ManualDesk", "Queue" })
    {
        var reused = cache.ReadRareGuest(8, source, () => Read(source))!;
        SameDtoExceptSource(original, reused, source);
    }
    Equal(1, reads, "同次采集同控制器被五个集合重复读取。");
    cache.ReadRareGuest(9, "Presented", () => Read("Presented"));
    Equal(2, reads, "同客人目录 ID 的另一控制器被错误合并。");
    cache.ReadRareGuest(10, "Presented", () => { reads++; return null; });
    Check(cache.ReadRareGuest(10, "Queue", () => Read("Queue")) != null, "稀客失败结果被缓存，无法保持来源完整性。");
    Equal(4, reads, "稀客失败来源没有重新读取。");
    cache.ReadRareGuest(0, "Queue", () => Read("Queue"));
    cache.ReadRareGuest(0, "Queue", () => Read("Queue"));
    Equal(6, reads, "未知稀客原生标识被合并。");
    cache.Clear();
    cache.ReadRareGuest(8, "Presented", () => Read("Presented"));
    Equal(7, reads, "稀客资金快照跨采集复用。");
}

void VerifyRepositoryRefresh()
{
    var catalog = new RuntimeDataCatalog
    {
        Recipes = new() { new() { Id = 3, Name = "旧料理" }, new() { Id = 3, Name = "重复项" } },
        Beverages = new() { new() { Id = 4, Name = "旧酒水" } },
        NormalCustomers = new()
        {
            new() { Id = 5, Name = "旧客人", PositiveTags = new() { "旧喜好" } },
            new() { Id = 6, Name = "重复客群甲" }, new() { Id = 6, Name = "重复客群乙" },
        },
        RareCustomers = new()
        {
            new() { Id = 5, Name = "同数字稀客" },
            new() { Id = 6, Name = "重复稀客甲" }, new() { Id = 6, Name = "重复稀客乙" },
        },
    };
    var first = DataRepository.FromRuntime(catalog);
    Check(ReferenceEquals(first.RecipesById, first.RecipesById), "仓库索引被每次查询重新创建。");
    Check(ReferenceEquals(first.NormalGuestImageNamesById, first.NormalGuestImageNamesById)
        && ReferenceEquals(first.RareGuestImageNamesById, first.RareGuestImageNamesById), "图片名字索引被每次查询重建。");
    Equal("旧客人", first.NormalGuestImageNamesById[5], "普客图片名字未使用自己的目录。");
    Equal("同数字稀客", first.RareGuestImageNamesById[5], "同数字稀客错误使用普客名字。");
    Check(!first.NormalGuestImageNamesById.ContainsKey(6) && !first.RareGuestImageNamesById.ContainsKey(6),
        "图片身份索引对重复目录 ID 取了首项。");
    Equal("旧料理", first.RecipesById[3].Name, "索引改变原有重复 ID 取首项语义。");
    catalog.Recipes.Add(new Recipe { Id = 8, Name = "后加入原目录" });
    Check(!first.RecipesById.ContainsKey(8) && first.Recipes.Count == 2, "源集合修改穿透仓库快照。");
    var next = DataRepository.FromRuntime(new RuntimeDataCatalog
    {
        Recipes = new() { new() { Id = 3, Name = "新料理" } },
        Beverages = new() { new() { Id = 4, Name = "新酒水" } },
        NormalCustomers = new() { new() { Id = 5, Name = "新客人", PositiveTags = new() { "新喜好" } } },
    });
    Check(!ReferenceEquals(first.RecipesById, next.RecipesById), "目录替换未生成新索引。");
    Equal("新料理", next.RecipesById[3].Name, "新目录读取旧料理。");
    Equal("新酒水", next.BeveragesById[4].Name, "新目录读取旧酒水。");
    Equal("新喜好", next.NormalCustomersById[5].PositiveTags.Single(), "新目录读取旧喜好。");
    Check(!ReferenceEquals(first.NormalGuestImageNamesById, next.NormalGuestImageNamesById), "图片索引跨目录实例共享。");
    Equal("新客人", next.NormalGuestImageNamesById[5], "目录更新仍显示旧图片名字。");
    Check(next.RareGuestImageNamesById.Count == 0, "新目录没有稀客仍继承旧图片目录。");
    Check(DataRepository.Empty().RecipesById.Count == 0, "空仓库索引异常。");
}

void VerifyOrderCountScaling()
{
    // 记录读取次数而非机器耗时；这是纯托管计数回归，不宣称真实 Unity 帧率结果。
    foreach (var count in new[] { 1, 8, 32, 64 })
    {
        var cache = new RuntimeSnapshotReadCache();
        var reads = 0;
        foreach (var source in new[] { "OrderController", "OrderControllerElement", "HUD" })
        {
            for (var index = 1; index <= count; index++)
            {
                cache.ReadVisibleNormal((nint)index, source, () => { reads++; return Normal(source); });
            }
        }
        Equal(count, reads, $"{count} 笔未绑定订单仍按来源数量倍增读取。");
    }
}

NormalBusinessOrder Normal(string source) => new()
{
    TraceId = "synthetic", OrderKey = "ptr:16", OrderLifecycleSequence = -1, DeskCode = 1,
    GuestId = 7, RuntimeGuestId = 17, GuestName = "合成普客", SpecialBusinessRole = "observer",
    GuestImageKind = "rare-guest", GuestImageId = 27,
    SpecialBusinessRoleLabel = "只读角色", FoodPreferenceTags = new() { "肉", "饱腹" },
    BeveragePreferenceTags = new() { "清酒" }, Fund = 101, BaseFundCarry = 80, MaxFundCarry = 120,
    ExtraFundByBuff = 21, WillPayMoney = false, RemainingOrderCount = 3, FoodId = 10,
    FoodName = "合成料理", BeverageId = 11, BeverageName = "合成酒水", HasServedFood = true,
    HasServedBeverage = false, ReadyToEvaluate = false, HasEvaluated = true,
    ControllerAvailable = false, CanAutomate = false, ActionBlockReason = "缺少活动控制器",
    FirstSeenAtUtc = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc), Source = source,
};

void VerifyGuestImageIdentity()
{
    // 两类目录故意复用数字7，模拟 GuestBase 包装；分类只能依据生产传入的精确原生转换。
    var normals = new Dictionary<int, string> { [7] = "普通客群七" };
    var rares = new Dictionary<int, string> { [7] = "复制稀客七", [8] = "稀客八" };
    object? Cast(object? value, string name) => value is ImageGuest guest
        && (guest.Kind == "both" || guest.Kind == name) ? guest : null;
    object? Read(object? value, string name) => value is ImageGuest guest && name == "Id" ? guest.Id : null;
    RuntimeGuestImageIdentity Resolve(object? first, object? second = null) =>
        RuntimeGuestImageIdentityReader.Resolve(first, second, normals, rares, Cast, Read);
    var normal = new ImageGuest(RuntimeGuestImageIdentityReader.NormalType, 7);
    var rare = new ImageGuest(RuntimeGuestImageIdentityReader.RareType, 7);
    Equal("normal-group", Resolve(normal).Kind, "真实普客没有保留独立图片命名空间。");
    Equal("普通客群七", Resolve(normal).Name, "普客错误使用同ID稀客名称。");
    Equal("rare-guest", Resolve(rare).Kind, "普通订单承载的稀客分身被误归为普客。");
    Equal("复制稀客七", Resolve(rare).Name, "复制稀客错误使用同ID普客名称。");
    Equal(7, Resolve(rare, rare).Id, "一致的订单/控制器身份没有保留精确图片ID。");
    Equal("rare-guest", Resolve(null, rare).Kind, "仅有可确认控制器客人时未使用精确身份。");
    foreach (var candidate in new[] { new ImageGuest("unknown", 7), new ImageGuest("both", 7),
        new ImageGuest(RuntimeGuestImageIdentityReader.RareType, -1),
        new ImageGuest(RuntimeGuestImageIdentityReader.RareType, "7"),
        new ImageGuest(RuntimeGuestImageIdentityReader.RareType, 99),
        new ImageGuest(RuntimeGuestImageIdentityReader.NormalType, 8) })
        Equal(RuntimeGuestImageIdentity.Unknown, Resolve(candidate), "未知/冲突/非整数/跨目录身份不应得到图片。");
    Equal(RuntimeGuestImageIdentity.Unknown, Resolve(normal, rare), "订单和控制器种类冲突仍选择了一张图。");
    Equal(RuntimeGuestImageIdentity.Unknown, Resolve(rare, new ImageGuest(RuntimeGuestImageIdentityReader.RareType, 8)),
        "同种类不同客人ID冲突仍选择了一张图。");
    Equal(RuntimeGuestImageIdentity.Unknown, Resolve(new ImageGuest("unknown", 7), rare),
        "订单对象存在但无法核验时不得用另一个控制器身份掩盖不确定性。");
    var failed = RuntimeGuestImageIdentityReader.Resolve(rare, null, normals, rares, Cast,
        (_, _) => throw new InvalidOperationException("synthetic read failure"));
    Equal(RuntimeGuestImageIdentity.Unknown, failed, "图片反射失败不得中止采集或猜图。");
    Equal(Resolve(rare), RuntimeGuestImageIdentityReader.Merge(new[] { RuntimeGuestImageIdentity.Unknown, Resolve(rare) }),
        "可视来源没有保留另一已确认来源的身份。");
    Equal(RuntimeGuestImageIdentity.Unknown, RuntimeGuestImageIdentityReader.Merge(new[] { Resolve(normal), Resolve(rare) }),
        "同一订单不同已知图片身份被错误取首项。");
    var customers = new Dictionary<int, NormalCustomer>
    {
        [7] = new() { Id = 7, PositiveTags = new() { "家常" }, BeverageTags = new() { "低酒精" } },
    };
    var normalCustomer = RuntimeGuestImageIdentityReader.ResolveNormalCustomer(Resolve(normal), customers);
    Equal("家常", normalCustomer?.PositiveTags.Single(), "已确认普客丢失既有料理喜好。");
    Equal("低酒精", normalCustomer?.BeverageTags.Single(), "已确认普客丢失既有酒水喜好。");
    Check(RuntimeGuestImageIdentityReader.ResolveNormalCustomer(Resolve(rare), customers) == null,
        "复制稀客不应继承同数字ID普客的喜好。");
    Check(RuntimeGuestImageIdentityReader.ResolveNormalCustomer(RuntimeGuestImageIdentity.Unknown, customers) == null,
        "未知客人不应继承目录首项喜好。");
}

void SameDtoExceptSource<T>(T expected, T actual, string source)
{
    var expectedNode = JsonSerializer.SerializeToNode(expected)!.AsObject();
    expectedNode["Source"] = source;
    Equal(expectedNode.ToJsonString(), JsonSerializer.SerializeToNode(actual)!.ToJsonString(), "复用 DTO 改变来源以外的字段或遗漏字段。");
}

void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

void Equal<T>(T expected, T actual, string message)
{
    Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} expected={expected}; actual={actual}");
}

// 仅离线委托桩，不按 CLR 类型名称模拟游戏继承，也不加载真实 IL2CPP 对象。
sealed record ImageGuest(string Kind, object Id);
