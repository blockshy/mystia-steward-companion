using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.Business.Application;
using MystiaStewardCompanion.Business.Application.Automation;
using MystiaStewardCompanion.LocalApi;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 精确输入复用的隔离回归：实际生产宿主、设备存储、领域计算，只把游戏副作用改成抛错委托。
/// 分配对照保留旧命中路径的 payload/序列化/重复 clone，不把 DTO 相等或调用次数当成性能结论。
/// </summary>
internal static class BusinessInputReuseTests
{
    internal static async Task Run(JsonElement profile)
    {
        var root = Path.GetFullPath(Path.Combine("temp", "business-input-reuse-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var log = Logger.CreateLogSource("business-input-reuse");
        var host = new LocalApiServer(root, log, _ => throw new InvalidOperationException("只读回归禁止游戏动作。"));
        var checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        try
        {
            const string client = "11111111-1111-1111-1111-111111111111";
            var settings = J.Obj(JsonNode.Parse(profile.GetRawText())); settings["automationEnabled"] = false;
            host.Authority.Register(client, "offline", new CompanionDeviceRegisterRequest
            { ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion, Platform = "windows", AppVersion = "test", Profile = JsonSerializer.SerializeToElement(settings) }, DateTime.UtcNow);
            var catalog = LeaseExpiryTests.Catalog();
            // 扩大只读目录以稳定分辨整树复制的分配量；重复名字不会参与本笔订单的精确 ID 查找。
            for (var index = 0; index < 220; index++)
            {
                var recipe = J.Obj(J.Clone(J.Arr(catalog["recipes"])[0]));
                recipe["id"] = 1000 + index; recipe["recipeId"] = 2000 + index; recipe["name"] = "仅离线目录" + index;
                J.Arr(catalog["recipes"]).Add(recipe);
            }
            var snapshot = LeaseExpiryTests.Snapshot();
            var order = J.Objects(snapshot["normalBusiness"]?["orders"]).Single();
            order["traceId"] = "N-1001"; order["orderKey"] = "ptr:1001";
            snapshot["snapshotSignature"] = "reuse-initial";
            host.Publish(snapshot, catalog);
            var initial = host.ProbeRead(false, out var firstReused);
            Check(!firstReused, "首轮必须实际计算。");
            var reused = host.ProbeRead(false, out var secondReused);
            Check(secondReused && initial.ToJsonString() == reused.ToJsonString(), "相同输入复用改变结果。");
            // 外部消费者拿到的绑定结果必须独立；不能污染下一次原始缓存结果。
            reused["normalExecutionTargets"] = new JsonArray();
            Check(initial.ToJsonString() == host.ProbeRead(false, out _).ToJsonString(), "返回结果修改污染了原始缓存。");
            for (var index = 0; index < 3; index++) { host.ProbeRead(true, out _); host.ProbeRead(false, out _); }
            const int repetitions = 24;
            long Measure(bool legacy)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < repetitions; index++) host.ProbeRead(legacy, out _);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            var legacyBytes = Measure(true); var reusedBytes = Measure(false);
            Check(reusedBytes < legacyBytes * 0.7, "精确输入命中未明显减少 payload/重复 clone 分配。");
            Console.WriteLine($"INPUT-REUSE-ALLOCATION loops={repetitions}; legacyBytes={legacyBytes}; reusedBytes={reusedBytes}; reduction={1d - reusedBytes / (double)legacyBytes:0.000}; same-thread; not-FPS");

            // 快照、目录、配置、收藏、自定义任一完整正文改变均必须参与重新判断。
            // 新签名虽可复用相同纯计算，也不能沿用旧执行帧版本。
            var oldVersion = host.InputVersion;
            order["foodId"] = 1000; snapshot["snapshotSignature"] = "reuse-order-changed";
            host.Publish(snapshot, catalog);
            Check(host.InputVersion > oldVersion && !J.Bool(host.Status()["isCurrent"]), "输入发布未立即撤销旧帧。");
            host.ProbeRead(false, out var orderReused); Check(!orderReused, "订单变化被精确复用吞掉。");
            J.Obj(snapshot["recommendationState"])["ownedIngredientQty"] = J.Object(("1", 0));
            host.Publish(snapshot, catalog); host.ProbeRead(false, out var inventoryReused);
            Check(!inventoryReused, "库存变化未失效。");
            J.Objects(catalog["recipes"]).First()["price"] = 99;
            host.Publish(snapshot, catalog); host.ProbeRead(false, out var catalogReused);
            Check(!catalogReused, "同目录 ID 内容变化未失效。");
            host.AddFavorite(); host.ProbeRead(false, out var favoriteReused);
            Check(!favoriteReused, "外部收藏内容变化未失效。");
            settings["recommendationBudgetPolicy"] = J.Str(settings["recommendationBudgetPolicy"]) == "warn" ? "block" : "warn";
            host.Import(J.Object(("profile", settings), ("snapshot", snapshot), ("catalog", catalog)), client);
            host.ProbeRead(false, out var profileReused); Check(!profileReused, "配置内容变化未失效。");
            host.ChangeCustomRecipeEnabledForReuseTest(); host.ProbeRead(false, out var customReused);
            Check(!customReused, "自定义配方内容变化未失效。");

            var coordinator = new AutomationCoordinator();
            var keys = coordinator.RejectedRecipeKeys();
            Check(keys.Length == 0 && coordinator.Snapshot()["rejectedRecipeKeys"]!.ToJsonString() == "[]", "窄只读接口改变拒绝键语义。");
            // 仅夹具注入内部只读源以覆盖非空/顺序；真正拒绝配方产生和清空行为继续由自动化差分覆盖。
            var internalKeys = (List<string>)typeof(AutomationCoordinator)
                .GetField("_rejectedRecipes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(coordinator)!;
            internalKeys.AddRange(new[] { "fixture:first", "fixture:second" });
            var copiedKeys = coordinator.RejectedRecipeKeys(); copiedKeys[0] = "consumer:changed";
            Check(coordinator.RejectedRecipeKeys().SequenceEqual(new[] { "fixture:first", "fixture:second" })
                && coordinator.Snapshot()["rejectedRecipeKeys"]!.ToJsonString() == "[\"fixture:first\",\"fixture:second\"]",
                "拒绝键窄接口未保留顺序或返回了可修改内部存储。");
            VerifyDiagnostics(Check);

            // 启动真正后台循环：相同输入仍要持续推进编排时间；停止后不产生任何游戏动作。
            host.Start();
            await Until(() => J.Bool(host.Status()["isCurrent"]), "启动后未发布当前帧。");
            var beforeTick = host.LastCoordinatorTimeForReuseTest();
            await Until(() => host.LastCoordinatorTimeForReuseTest() > beforeTick, "缓存命中使自动化单调时间停止推进。");
            Check(J.Str(host.Status()["sourceSnapshotSignature"]) == "reuse-order-changed", "复用发布了旧签名。");
            Console.WriteLine($"PASS input reuse: {checks} assertions; exact invalidation, isolated outputs, live ticks and bounded diagnostics.");
        }
        finally
        {
            await host.Stop(); Logger.Sources.Remove(log);
            var allowed = Path.GetFullPath("temp") + Path.DirectorySeparatorChar;
            if (root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }

    private static async Task Until(Func<bool> predicate, string error)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!predicate()) { if (Environment.TickCount64 >= deadline) throw new TimeoutException(error); await Task.Delay(20); }
    }

    private static void VerifyDiagnostics(Action<bool, string> check)
    {
        var diagnostics = new BusinessLoopDiagnostics();
        diagnostics.Configure(true);
        var now = Stopwatch.GetTimestamp();
        diagnostics.Record(BusinessLoopStage.Payload, 2, 1024); diagnostics.Complete(true, false, false, 3, 1);
        check(!diagnostics.TryDrain(now, Stopwatch.Frequency, out _), "后台诊断不能逐轮输出。");
        check(diagnostics.TryDrain(now + 6 * Stopwatch.Frequency, Stopwatch.Frequency, out var report)
            && report.Contains("allocatedBytes=1024") && report.Contains("orderResultReused: 1")
            && report.Contains("candidateCacheHits: 3"), "后台窗口丢失阶段分配或缓存计数。");
        diagnostics.Configure(false); diagnostics.Record(BusinessLoopStage.Payload, 4, 9999);
        check(!diagnostics.TryDrain(now + 20 * Stopwatch.Frequency, Stopwatch.Frequency, out _), "日志关闭后仍输出后台窗口。");
    }
}

namespace MystiaStewardCompanion.LocalApi
{
    internal sealed partial class LocalApiServer
    {
        // 此测试门面只调用生产捕获/计算/绑定；legacy 分支明确保留修改前命中的多余操作作对照。
        internal JsonObject ProbeRead(bool legacy, out bool reused)
        {
            var frame = CaptureBusinessFrame();
            var result = EvaluateBusinessOrders(frame, out reused, out _);
            if (legacy)
            {
                var rejected = J.Arr(_businessCoordinator.Snapshot()["rejectedRecipeKeys"]);
                var payload = BusinessQueries.BuildOrderPayload(frame.Snapshot, frame.Data, frame.Preferences,
                    frame.Favorites, frame.CustomRecipes, rejected);
                _ = payload.ToJsonString(); result = J.Obj(J.Clone(result));
            }
            return BusinessQueries.BindCurrentNormalTargets(result, frame.Snapshot);
        }
        internal long LastCoordinatorTimeForReuseTest()
        {
            // 与真实 Advance 使用同一锁读取，不能把反射的无同步跨线程读取当作时间推进证据。
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var gate = typeof(AutomationCoordinator).GetField("_gate", flags)!.GetValue(_businessCoordinator)!;
            lock (gate) return (long)typeof(AutomationCoordinator).GetField("_lastNow", flags)!.GetValue(_businessCoordinator)!;
        }
        internal void ChangeCustomRecipeEnabledForReuseTest()
        {
            var path = Path.Combine(_testRoot, "custom.json");
            var data = J.Obj(JsonNode.Parse(_customRecipeStore.GetJson()));
            data["enabled"] = !J.Bool(data["enabled"]);
            File.WriteAllText(path, data.ToJsonString());
        }
    }
}

namespace MystiaStewardCompanion.Save
{
    // 宿主没有 BepInEx 全局聚合写盘；真实阶段类另有确定数值回归，默认关闭避免测试造游戏日志。
    internal static class AggregateModLogService
    {
        internal static bool Enabled => false;
        internal static void AppendSection(string channel, string title, string content) { }
    }
}
