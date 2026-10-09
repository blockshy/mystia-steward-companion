using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 租约自然到期的排队边界回归。运行真实业务循环并暂停模拟的主线程适配器，期间不发布快照、
/// 不调用租约清理，直接验证宿主随请求附带的最终许可；整个测试不加载 Unity 或产生游戏副作用。
/// </summary>
internal static class LeaseExpiryTests
{
    internal static async Task Run(JsonElement profile)
    {
        var directory = Path.GetFullPath(Path.Combine("temp", "mystia-business-lease-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var log = Logger.CreateLogSource("business-lease-expiry-offline");
        var checks = 0;
        void Check(bool value, string label)
        {
            checks++;
            if (!value) throw new InvalidOperationException(label);
        }

        // 该闩锁只替代 Unity 队列等待；真正的许可闭包来自生产 ExecuteBusinessCommand。
        var entered = new TaskCompletionSource<OrderPreparationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var effects = 0;
        var host = new LocalApiServer(directory, log, request =>
        {
            entered.TrySetResult(request);
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("租约测试未释放模拟主线程队列。");
            var allowed = request.IsBusinessInputCurrent?.Invoke() == true;
            if (allowed) Interlocked.Increment(ref effects);
            var result = new OrderPreparationResult { Ok = allowed, Prepared = allowed };
            result.Automation.Outcome = allowed ? "waiting" : "cancelled";
            result.Automation.Stage = "command";
            result.Automation.ReasonCode = allowed ? "cooking-in-progress" : "automation-command-superseded";
            completed.TrySetResult(allowed);
            return result;
        });
        try
        {
            const string client = "11111111-1111-1111-1111-111111111111";
            var state = host.Authority.Register(client, "lease test", new CompanionDeviceRegisterRequest
            {
                ProtocolVersion = 1, ProfileSchemaVersion = 1, Platform = "windows", AppVersion = "offline", Profile = profile,
            }, DateTime.UtcNow);
            host.Publish(Snapshot(), Catalog());
            host.Start();
            await Until(() => J.Bool(host.Status()["isCurrent"]), "无租约预热未取得当前业务结果。");
            Check(!entered.Task.IsCompleted, "没有租约时不能进入适配器。");

            host.GrantLease(client, state.AuthorityRevision, TimeSpan.FromSeconds(2));
            var expiry = host.ReadLeaseExpiryForTest();
            var request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var version = host.InputVersion;
            Check(request.IsBusinessInputCurrent?.Invoke() == true, "有效租约内的排队许可应为真。");
            Check(Volatile.Read(ref effects) == 0, "等待模拟主线程期间不得提前产生副作用。");

            // 等待实际授予的期限而非假定固定延迟；此时业务线程被适配器阻塞，无法主动清理租约。
            await Until(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiry + 10, "短租约未自然到期。");
            Check(host.HasLease, "自然到期验证前不能依赖后台 Prune 或显式移除租约。");
            Check(host.InputVersion == version, "自然到期验证前不能借快照失效拒绝命令。");
            Check(request.IsBusinessInputCurrent?.Invoke() == false, "租约自然到期必须使已排队请求的最终许可失败。");

            // 新租约只允许之后的计算；即使权威、epoch、快照均相同，也不能让旧截止时间失效。
            host.GrantLease(client, state.AuthorityRevision, TimeSpan.FromSeconds(15));
            Check(host.ReadLeaseExpiryForTest() > expiry && request.IsBusinessInputCurrent?.Invoke() == false,
                "后续续约不能追认旧排队命令。");
            host.ExpireLease();
            release.Set();
            Check(!await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)) && Volatile.Read(ref effects) == 0,
                "模拟主线程恢复时不得执行租约已过期的命令。");
            Console.WriteLine($"PASS lease expiry: {checks} assertions; queued permit expires without snapshot update or lease pruning.");
        }
        finally
        {
            release.Set();
            await host.Stop();
            Logger.Sources.Remove(log);
            // 仅清理由本测试创建且仍位于仓库 temp 目录内的独占目录。
            var temporaryRoot = Path.GetFullPath("temp") + Path.DirectorySeparatorChar;
            if (directory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException(message);
            await Task.Delay(10);
        }
    }

    /// <summary>最小完整目录用于真实候选与订单计算，不注入预先计算的执行目标。</summary>
    internal static JsonObject Catalog() => J.Object(("isComplete", true), ("source", "offline"),
        ("ingredients", new JsonArray(J.Object(("id", 1), ("name", "豆腐"), ("tags", new JsonArray("素")), ("price", 2)))),
        ("recipes", new JsonArray(J.Object(("id", 101), ("recipeId", 201), ("name", "测试料理"),
            ("ingredients", new JsonArray("豆腐")), ("positiveTags", new JsonArray("素")),
            ("negativeTags", new JsonArray()), ("cooker", "煮锅"), ("level", 1), ("price", 20)))),
        ("beverages", new JsonArray(J.Object(("id", 21), ("name", "测试酒水"), ("tags", new JsonArray("水果")), ("level", 1), ("price", 10)))),
        ("rareCustomers", new JsonArray(J.Object(("id", 3), ("name", "测试稀客"), ("positiveTags", new JsonArray("素")),
            ("negativeTags", new JsonArray()), ("beverageTags", new JsonArray("水果")), ("places", new JsonArray("妖怪兽道"))))),
        ("normalCustomers", new JsonArray(J.Object(("id", 3), ("name", "测试普客"), ("positiveTags", new JsonArray("素")),
            ("beverageTags", new JsonArray("水果")), ("places", new JsonArray("妖怪兽道"))))));

    /// <summary>固定的一笔普客订单与精确厨具槽位；测试开始后始终不再发布快照。</summary>
    internal static JsonObject Snapshot() => J.Object(("automationSessionId", "lease-expiry"), ("nightBusinessGeneration", 1),
        ("nightBusinessAutomationAllowed", true), ("nightBusiness", J.Object(("orders", new JsonArray()), ("activeRareGuests", new JsonArray()))),
        ("specialBusiness", null), ("automationEvents", new JsonArray()), ("automationCookingJobs", new JsonArray()),
        ("normalBusiness", J.Object(("orders", new JsonArray(J.Object(("traceId", "N-lease"), ("orderKey", "ptr:lease"),
            ("orderLifecycleSequence", 1), ("deskCode", 0), ("guestId", 3), ("runtimeGuestId", 3), ("guestName", "测试普客"),
            ("foodId", 101), ("beverageId", 21), ("foodName", "测试料理"), ("beverageName", "测试酒水"),
            ("hasServedFood", false), ("hasServedBeverage", false), ("hasEvaluated", false), ("readyToEvaluate", false), ("canAutomate", true)))))),
        ("recommendationState", J.Object(("availableRecipeIds", new JsonArray(101)), ("availableIngredientIds", new JsonArray(1)),
            ("availableBeverageIds", new JsonArray(21)), ("ownedIngredientQty", J.Object(("1", 20))), ("ownedBeverageQty", J.Object(("21", 20))),
            ("placedCookerSnapshotComplete", true), ("placedCookerControllerCount", 1), ("placedCookerEmptyControllerCount", 0),
            ("placedCookerLockedControllerCount", 0), ("placedCookerReadFailureCount", 0), ("placedCookerTypeIds", new JsonArray(1)),
            ("placedCookers", new JsonArray(J.Object(("controllerIndex", 0), ("controllerIdentity", "0x10"),
                ("gridPosition", J.Object(("x", 0), ("y", 0), ("z", 0))), ("name", "煮锅"), ("typeIds", new JsonArray(1)),
                ("typeNames", new JsonArray("煮锅")), ("automationAvailable", true), ("couldOpen", true), ("challengeLocked", false)))))));
}

namespace MystiaStewardCompanion.LocalApi
{
    internal sealed partial class LocalApiServer
    {
        /// <summary>仅测试读取已授予的精确期限，不触发清理、续租、权威活动或输入版本变化。</summary>
        internal long ReadLeaseExpiryForTest()
        {
            lock (_automationLeaseLock)
                return _automationLease == null ? 0 : new DateTimeOffset(_automationLease.ExpiresAtUtc).ToUnixTimeMilliseconds();
        }
    }
}
