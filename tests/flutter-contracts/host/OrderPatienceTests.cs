using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Ui;

/// <summary>
/// 等待条专项隔离回归：调用完整生产采集/DTO/业务payload代码，但原生指针仅为托管测试值。
/// 不注入Harmony、不读取存档/配置、不连接或启动游戏。模拟重入只改变托管生命周期仓，
/// 用来证明读到数值本身并不足以越过精确身份与当前经营代次的限制。
/// </summary>
internal static class OrderPatienceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static int _checks;
    private static void Check(bool value, string message)
    {
        _checks++;
        if (!value) throw new InvalidOperationException(message);
    }

    internal static int Run()
    {
        _checks = 0;
        CheckRatiosAndWaitingEvidence();
        CheckExactReadIdentity();
        CheckProjectionAndCopyChains();
        CheckSnapshotsAndRecommendationPayload();
        Console.WriteLine($"PASS: order patience {_checks} assertions (native contract, exact identity, immutable copies, snapshot/payload separation).");
        return 0;
    }

    private static void CheckRatiosAndWaitingEvidence()
    {
        foreach (var pair in new[] { (0, 100, 0d), (1, 4, .25), (100, 100, 1d), (int.MaxValue, int.MaxValue, 1d) })
            Check(RuntimeOrderPatienceReader.Ratio(pair.Item1, pair.Item2) == pair.Item3, "合法原生Int32比例不正确。");
        foreach (var pair in new (object?, object?)[] {
            (null, 100), (10, null), (-1, 100), (101, 100), (1, 0), (1, -1),
            ("1", 100), (1d, 100), (1L, 100), (1, "100"), (1, 100d), (double.NaN, 100), (true, 100),
        }) Check(RuntimeOrderPatienceReader.Ratio(pair.Item1, pair.Item2) == null, "未知/非法耐心被解释成可用数值。");

        var order = new object(); var controller = new object();
        var values = new Dictionary<string, object?> {
            ["IsOrdering"] = true, ["IsFullfilled"] = false, ["OnPatientUpdatedCallback"] = new object(),
            ["CurrentPatient"] = 25, ["MaxPatient"] = 100,
        };
        object? Read(object? target, string field)
        {
            Check(ReferenceEquals(target, field == "IsFullfilled" ? order : controller), "耐心属性读到了错误的对象。");
            return values.GetValueOrDefault(field);
        }
        Check(RuntimeOrderPatienceReader.ReadVerifiedValues(order, controller, Read) == .25, "有效等待条未读取。");
        foreach (var pair in new (string, object?)[] {
            ("IsOrdering", false), ("IsOrdering", null), ("IsOrdering", "true"),
            ("IsFullfilled", true), ("IsFullfilled", null), ("OnPatientUpdatedCallback", null),
        })
        {
            var previous = values[pair.Item1]; values[pair.Item1] = pair.Item2;
            Check(RuntimeOrderPatienceReader.ReadVerifiedValues(order, controller, Read) == null,
                "不在等待条状态/字段缺失时沿用了耐心。");
            values[pair.Item1] = previous;
        }
        Check(RuntimeOrderPatienceReader.ReadVerifiedValues(order, controller, (_, _) => throw new Exception("synthetic")) == null,
            "原生getter失败未降为未知。");
        var invoked = false;
        values["OnPatientUpdatedCallback"] = (Action<int>)(_ => invoked = true);
        Check(RuntimeOrderPatienceReader.ReadVerifiedValues(order, controller, Read) == .25 && !invoked,
            "只读采样调用了等待条回调。");
    }

    /// <summary>
    /// 测试直接进入生产Read，再通过真实终态仓检查合成指针元组。用getter内重绑定与切场
    /// 模拟原生回调重入，断言读取前/后都核验；不把只测Ratio误称为原生身份测试。
    /// </summary>
    private static void CheckExactReadIdentity()
    {
        var published = typeof(RuntimeNightBusinessLifecycle).GetField("_publishedSnapshot", BindingFlags.NonPublic | BindingFlags.Static)!;
        var original = published.GetValue(null);
        var active = new NightBusinessLifecycleSnapshot(9901, 1, NightBusinessLifecyclePhase.Active,
            "synthetic-patience", DateTime.UnixEpoch, Environment.CurrentManagedThreadId);
        var order = new SyntheticOrder { Pointer = (nint)9101 };
        var controller = new SyntheticController { Pointer = (nint)9102 };
        try
        {
            published.SetValue(null, active);
            foreach (var kind in new[] { RuntimeOrderKind.Special, RuntimeOrderKind.Normal })
            {
                var sequence = RuntimeOrderTerminalReceiptStore.BeginLifecycle(active.Generation, kind, order.Pointer, controller.Pointer);
                Check(RuntimeOrderPatienceReader.Read(order, controller, kind, sequence) == .25, "精确活动生命周期未读到等待条。");
                Check(RuntimeOrderPatienceReader.Read(order, controller, kind, sequence + 1) == null, "错误生命周期获得比例。");
                Check(RuntimeOrderPatienceReader.Read(order, new SyntheticController { Pointer = (nint)9103 }, kind, sequence) == null,
                    "同订单不同控制器获得比例。");
                Check(RuntimeOrderPatienceReader.Read(new SyntheticOrder { Pointer = (nint)9104 }, controller, kind, sequence) == null,
                    "同控制器不同订单获得比例。");
                controller.OnRead = () => RuntimeOrderTerminalReceiptStore.BeginLifecycle(active.Generation, kind, order.Pointer, controller.Pointer);
                Check(RuntimeOrderPatienceReader.Read(order, controller, kind, sequence) == null, "读取中原生指针ABA复用仍返回旧值。");
                controller.OnRead = null;
                sequence = RuntimeOrderTerminalReceiptStore.BeginLifecycle(active.Generation, kind, order.Pointer, controller.Pointer);
                controller.OnRead = () => published.SetValue(null, active with { Phase = NightBusinessLifecyclePhase.Closing });
                Check(RuntimeOrderPatienceReader.Read(order, controller, kind, sequence) == null, "读取中经营结束仍返回旧值。");
                controller.OnRead = null; published.SetValue(null, active);
                controller.OnRead = () => published.SetValue(null, active with { Generation = active.Generation + 1 });
                Check(RuntimeOrderPatienceReader.Read(order, controller, kind, sequence) == null, "读取中经营代次改变仍返回旧值。");
                controller.OnRead = null; published.SetValue(null, active);
                Check(RuntimeOrderPatienceReader.Read(null, controller, kind, sequence) == null
                    && RuntimeOrderPatienceReader.Read(order, null, kind, sequence) == null
                    && RuntimeOrderPatienceReader.Read(order, controller, kind, 0) == null, "缺失对象或生命周期没有拒绝。");
            }
            published.SetValue(null, active with { Phase = NightBusinessLifecyclePhase.Inactive });
            Check(RuntimeOrderPatienceReader.Read(order, controller, RuntimeOrderKind.Special, 1) == null, "非经营状态暴露了耐心。");
        }
        finally
        {
            RuntimeOrderTerminalReceiptStore.Clear();
            published.SetValue(null, original);
        }
    }

    private static void CheckProjectionAndCopyChains()
    {
        // 所有属性均给出非默认夹具，逐字段比较能发现复制链未来新增属性时静默丢值。
        var rare = Seed<NightBusinessOrder>();
        rare = Alter(rare, ("traceId", "R-0010"), ("orderLifecycleSequence", 10L), ("guestId", 30), ("runtimeGuestId", 30),
            ("hasServedFood", false), ("hasServedBeverage", false), ("patienceRemainingRatio", .8));
        var normal = Alter(Seed<NormalBusinessOrder>(), ("traceId", "N-0020"), ("orderKey", "ptr:20"), ("orderLifecycleSequence", 20L),
            ("hasServedFood", false), ("hasServedBeverage", false), ("readyToEvaluate", false), ("hasEvaluated", false), ("patienceRemainingRatio", .8));
        NightBusinessContext Rare(params NightBusinessOrder[] rows) => new() { Orders = rows.ToList(), OrdersReadComplete = true,
            ActiveRareGuestsReadComplete = true, ActiveRareGuests = new() { new() { GuestId = 30, GuestName = "合成客人" } },
            Place = "synthetic", PlaceLabel = "测试地点", Source = "readonly" };
        NormalBusinessContext Normal(params NormalBusinessOrder[] rows) => new() { Orders = rows.ToList(), OrdersReadComplete = true, Source = "readonly" };
        var rareContext = Rare(rare); var normalContext = Normal(normal);
        var rareNext = RuntimeOrderPatienceReader.Project(rareContext, new Dictionary<long, double?> { [10] = .25 });
        var normalNext = RuntimeOrderPatienceReader.Project(normalContext, new Dictionary<long, double?> { [20] = .25 });
        Check(rareNext.Orders[0].PatienceRemainingRatio == .25 && normalNext.Orders[0].PatienceRemainingRatio == .25,
            "周期采样未替换缓存比例。");
        Check(rare.PatienceRemainingRatio == .8 && normal.PatienceRemainingRatio == .8, "投影修改了旧发布对象。");
        SameExcept(rareContext, rareNext, "patienceRemainingRatio");
        SameExcept(normalContext, normalNext, "patienceRemainingRatio");
        Check(ReferenceEquals(rareNext, RuntimeOrderPatienceReader.Project(rareNext, new Dictionary<long, double?> { [10] = .25 })),
            "稀客同值刷新不应更换上下文引用。");
        Check(ReferenceEquals(normalNext, RuntimeOrderPatienceReader.Project(normalNext, new Dictionary<long, double?> { [20] = .25 })),
            "普客同值刷新不应更换上下文引用。");
        var sameGuest = Enumerable.Range(0, 4).Select(i => Alter(rare, ("traceId", $"R-00{31 + i}"),
            ("orderLifecycleSequence", 31L + i), ("deskCode", i))).ToArray();
        var values = Enumerable.Range(0, 4).ToDictionary(i => 31L + i, i => (double?)(i / 4d));
        var four = RuntimeOrderPatienceReader.Project(Rare(sameGuest), values);
        Check(four.Orders.Select(row => row.PatienceRemainingRatio).SequenceEqual(new double?[] { 0, .25, .5, .75 }),
            "同guestId四笔订单的耐心被按姓名/桌号串联。");
        foreach (var value in new double?[] { null, double.NaN, double.PositiveInfinity, -.01, 1.01 })
        {
            Check(RuntimeOrderPatienceReader.Project(rareContext, new Dictionary<long, double?> { [10] = value }).Orders[0].PatienceRemainingRatio == null,
                "稀客无效观察未撤销旧比例。");
            Check(RuntimeOrderPatienceReader.Project(normalContext, new Dictionary<long, double?> { [20] = value }).Orders[0].PatienceRemainingRatio == null,
                "普客无效观察未撤销旧比例。");
        }
        Check(RuntimeOrderPatienceReader.Project(rareContext, new Dictionary<long, double?> { [11] = .1 }).Orders[0].PatienceRemainingRatio == null,
            "过期生命周期或未采到对象沿用了旧比例。");
        Check(RuntimeOrderPatienceReader.Project(Rare(rare, rare), new Dictionary<long, double?> { [10] = .1 }).Orders.All(row => row.PatienceRemainingRatio == null),
            "重复稀客生命周期被赋予比例。");
        Check(RuntimeOrderPatienceReader.Project(Normal(normal, normal), new Dictionary<long, double?> { [20] = .1 }).Orders.All(row => row.PatienceRemainingRatio == null),
            "重复普客生命周期被赋予比例。");
        foreach (var field in new[] { "ordersReadComplete", "error" })
        {
            object value = field == "error" ? "synthetic-unavailable" : false;
            Check(RuntimeOrderPatienceReader.Project(Alter(rareContext, (field, value)), new Dictionary<long, double?> { [10] = .1 })
                .Orders[0].PatienceRemainingRatio == null, "稀客不完整/错误快照未撤旧观察。");
            Check(RuntimeOrderPatienceReader.Project(Alter(normalContext, (field, value)), new Dictionary<long, double?> { [20] = .1 })
                .Orders[0].PatienceRemainingRatio == null, "普客不完整/错误快照未撤旧观察。");
        }
        Check(RuntimeOrderPatienceReader.Project(Rare(Alter(rare, ("hasServedFood", true))), new Dictionary<long, double?> { [10] = .1 })
            .Orders[0].PatienceRemainingRatio == .1, "仅料理送达但仍等待酒水时被误当终态。");
        Check(RuntimeOrderPatienceReader.Project(Rare(Alter(rare, ("hasServedFood", true), ("hasServedBeverage", true))),
            new Dictionary<long, double?> { [10] = .1 }).Orders[0].PatienceRemainingRatio == null, "已送齐稀客继续显示倒计时。");
        foreach (var row in new[] { Alter(normal, ("readyToEvaluate", true)), Alter(normal, ("hasEvaluated", true)),
            Alter(normal, ("hasServedFood", true), ("hasServedBeverage", true)) })
            Check(RuntimeOrderPatienceReader.Project(Normal(row), new Dictionary<long, double?> { [20] = .1 }).Orders[0].PatienceRemainingRatio == null,
                "普客终态继续显示倒计时。");
        Check(RuntimeOrderTraceIdService.GetRareTraceId(rare) == RuntimeOrderTraceIdService.GetRareTraceId(rareNext.Orders[0]),
            "耐心改变重置稀客trace。");
        Check(RuntimeOrderTraceIdService.GetNormalTraceId(normal) == RuntimeOrderTraceIdService.GetNormalTraceId(normalNext.Orders[0]),
            "耐心改变重置普客trace。");

        var cache = new RuntimeSnapshotReadCache();
        cache.ReadVisibleNormal((nint)24, "source-a", () => normal);
        var reused = cache.ReadVisibleNormal((nint)24, "source-b", () => throw new InvalidOperationException("must reuse"));
        SameExcept(normal, reused!, "source");
        var copied = (NormalBusinessOrder)typeof(RuntimeNormalOrderSnapshotService).GetMethod("CopyWithFirstSeen", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { normal, DateTime.UnixEpoch })!;
        SameExcept(normal, copied, "firstSeenAtUtc");
        var merge = typeof(RuntimeNormalOrderSnapshotService).GetMethod("MergeOrderGroup", BindingFlags.NonPublic | BindingFlags.Static)!;
        NormalBusinessOrder Merge(params NormalBusinessOrder[] rows) => (NormalBusinessOrder)merge.Invoke(null, new object[] { rows })!;
        Check(Merge(normal, normal).PatienceRemainingRatio == .8, "多来源一致观察丢失。");
        Check(Merge(normal, Alter(normal, ("patienceRemainingRatio", .7))).PatienceRemainingRatio == null, "冲突多来源取了较小耐心。");
        Check(Merge(normal, Alter(normal, ("orderLifecycleSequence", 21L))).PatienceRemainingRatio == null, "不同生命周期被合并出耐心。");
        var priorityCopy = (NightBusinessOrder)typeof(RuntimeMissionRecipePriorityProjection)
            .GetMethod("CopyOrder", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { rare, null })!;
        SameExcept(rare, priorityCopy, "missionRecipePriority");
        var seenCopy = typeof(NightBusinessReflectionProvider).GetMethod("CopyOrderWithSeenTimes", BindingFlags.NonPublic | BindingFlags.Static)!;
        NightBusinessOrder CopySeen(NightBusinessOrder current, NightBusinessOrder? fallback) => (NightBusinessOrder)seenCopy.Invoke(null,
            new object?[] { current, current.FirstSeenAtUtc, current.LastSeenAtUtc, current.IsFreeOrder, current.TraceId, fallback })!;
        SameExcept(rare, CopySeen(rare, null), "missionRecipePriority");
        Check(CopySeen(Alter(rare, ("patienceRemainingRatio", null)), rare).PatienceRemainingRatio == null,
            "稀客复制链从较旧fallback回填耐心。");
        Check(RuntimeOrderPatienceReader.Project(Rare(Alter(rare, ("orderLifecycleSequence", -1L))),
            new Dictionary<long, double?> { [-1] = .1 }).Orders[0].PatienceRemainingRatio == null,
            "缺失绑定的稀客生命周期获取比例。");
        Check(RuntimeOrderPatienceReader.Project(Normal(Alter(normal, ("orderLifecycleSequence", -1L))),
            new Dictionary<long, double?> { [-1] = .1 }).Orders[0].PatienceRemainingRatio == null,
            "缺失绑定的普客生命周期获取比例。");
    }

    /// <summary>同一观察变化必须刷新API，但不能变成推荐需求变化；测试直接调用生产边界。</summary>
    private static void CheckSnapshotsAndRecommendationPayload()
    {
        var signature = typeof(StewardOverlayController).GetMethod("BuildLocalApiSnapshotContentSignature", BindingFlags.NonPublic | BindingFlags.Static)!;
        LocalApiSnapshot Snapshot(double? rare, double? normal) => new() {
            NightBusiness = new() { OrdersReadComplete = true, Orders = new() { new() { TraceId = "R-0001", OrderLifecycleSequence = 1,
                GuestId = 30, FoodTagId = 1, BeverageTagId = 2, PatienceRemainingRatio = rare } } },
            NormalBusiness = new() { OrdersReadComplete = true, Orders = new() { new() { TraceId = "N-0002", OrderLifecycleSequence = 2,
                OrderKey = "ptr:2", FoodId = 1, BeverageId = 2, PatienceRemainingRatio = normal } } },
        };
        string Sign(LocalApiSnapshot value) => (string)signature.Invoke(null, new object[] { value })!;
        JsonObject Payload(JsonObject value) => BusinessQueries.BuildOrderPayload(value, new(), new(), new(), new(), new());
        foreach (var isRare in new[] { true, false })
        {
            var signatures = new List<string>(); var payloads = new List<string>();
            foreach (var ratio in new double?[] { null, 0, .25, 1 })
            {
                var snapshot = isRare ? Snapshot(ratio, .5) : Snapshot(.5, ratio);
                signatures.Add(Sign(snapshot));
                Check(Sign(snapshot) == Sign(snapshot), "相同观察生成不稳定快照签名。");
                var json = JsonSerializer.SerializeToNode(snapshot, JsonOptions)!.AsObject();
                var original = json.ToJsonString(); var payload = Payload(json); payloads.Add(payload.ToJsonString());
                Check(json.ToJsonString() == original, "构造推荐payload破坏原API比例。");
                var order = json[isRare ? "nightBusiness" : "normalBusiness"]!["orders"]![0]!;
                Check(order.AsObject().ContainsKey("patienceRemainingRatio") && (double?)order["patienceRemainingRatio"] == ratio,
                    "JSON比例未按数值/null透传。");
                Check(!payload["orders"]![0]!.AsObject().ContainsKey("patienceRemainingRatio")
                    && !payload["normalOrders"]![0]!.AsObject().ContainsKey("patienceRemainingRatio"), "观察值进入推荐payload。");
            }
            Check(signatures.Distinct().Count() == 4, "耐心变化未使正式快照签名失效。");
            Check(payloads.Distinct().Count() == 1, "单纯耐心变化使正式推荐缓存失效。");
        }
        var input = JsonSerializer.SerializeToNode(Snapshot(.3, .4), JsonOptions)!.AsObject();
        var initial = Payload(input).ToJsonString();
        input["nightBusiness"]!["orders"]![0]!["foodTagId"] = 99;
        Check(Payload(input).ToJsonString() != initial, "真实稀客需求变更被错误忽略。");
        initial = Payload(input).ToJsonString(); input["normalBusiness"]!["orders"]![0]!["foodId"] = 99;
        Check(Payload(input).ToJsonString() != initial, "真实普客料理变更被错误忽略。");
    }

    private static T Seed<T>() where T : new()
    {
        var result = new T(); var number = 11;
        foreach (var property in typeof(T).GetProperties())
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? value = type == typeof(string) ? "fixture-" + property.Name
                : type == typeof(int) ? ++number : type == typeof(long) ? (long)++number
                : type == typeof(double) ? .8d : type == typeof(bool) ? true
                : type == typeof(DateTime) ? DateTime.UnixEpoch.AddMinutes(++number)
                : type == typeof(List<string>) ? new List<string> { property.Name, "合成标签" }
                : type == typeof(MissionRecipePriority) ? new MissionRecipePriority { TraceId = "R-0010", FoodId = -1, ProductKind = "dark-cuisine",
                    MissionGeneration = 7, BusinessGeneration = 8, GuestId = 30, RuntimeGuestId = 30 } : null;
            if (value == null) throw new InvalidOperationException("新属性必须纳入复制完整性夹具：" + property.Name);
            property.SetValue(result, value);
        }
        return result;
    }

    private static T Alter<T>(T source, params (string Name, object? Value)[] edits)
    {
        var node = JsonSerializer.SerializeToNode(source, JsonOptions)!.AsObject();
        foreach (var edit in edits) node[edit.Name] = JsonSerializer.SerializeToNode(edit.Value, JsonOptions);
        return node.Deserialize<T>(JsonOptions)!;
    }

    private static void SameExcept<T>(T expected, T actual, params string[] fields)
    {
        JsonNode Clean(T value)
        {
            var node = JsonSerializer.SerializeToNode(value, JsonOptions)!;
            void Visit(JsonNode? item)
            {
                if (item is JsonObject map) { foreach (var field in fields) map.Remove(field); foreach (var child in map.ToArray()) Visit(child.Value); }
                else if (item is JsonArray array) foreach (var child in array) Visit(child);
            }
            Visit(node); return node;
        }
        Check(Clean(expected).ToJsonString() == Clean(actual).ToJsonString(), "只读复制意外丢失或修改非耐心字段。");
    }

    private sealed class SyntheticOrder
    {
        public nint Pointer { get; init; }
        public bool IsFullfilled => false;
    }
    private sealed class SyntheticController
    {
        public nint Pointer { get; init; }
        public bool IsOrdering => true;
        public object OnPatientUpdatedCallback { get; } = new();
        public Action? OnRead { get; set; }
        public int CurrentPatient { get { OnRead?.Invoke(); return 25; } }
        public int MaxPatient => 100;
    }
}
