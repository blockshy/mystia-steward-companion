using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 输入通知的真实宿主回归。使用生产后台循环、权威存储、推荐计算及页面发布，只在测试宿主的
/// 托管输入边界暂停采集以制造确定的突发场景；不检查信号量字段，不打开端口或访问真实游戏。
/// 本测试验证合并通知后的正确性和停止边界，持续输入的吞吐证据由 --load-stream 单独提供。
/// </summary>
internal static class InputWakeTests
{
    internal static async Task Run(JsonElement profile)
    {
        var directory = Path.GetFullPath(Path.Combine("temp", "mystia-business-wake-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var log = Logger.CreateLogSource("business-input-wake-offline");
        var checks = 0; var calls = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException(message);
        }
        var host = new LocalApiServer(directory, log, _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("输入唤醒测试不得提交游戏动作。");
        });
        try
        {
            const string client = "11111111-1111-1111-1111-111111111111";
            var settings = J.Obj(JsonNode.Parse(profile.GetRawText()));
            settings["automationEnabled"] = false;
            settings["gameUiEnabled"] = false;
            host.Authority.Register(client, "input wake", new CompanionDeviceRegisterRequest
            {
                ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                Platform = "windows", AppVersion = "offline", Profile = JsonSerializer.SerializeToElement(settings),
            }, DateTime.UtcNow);
            var catalog = LeaseExpiryTests.Catalog();
            var snapshot = LeaseExpiryTests.Snapshot();
            snapshot["snapshotSignature"] = "wake-initial";
            var intent = J.Object(("protocolVersion", 1), ("kind", "normal"), ("selectedPlace", "妖怪兽道"));
            host.Publish(snapshot, catalog);
            host.Start();
            await Until(() => J.Bool(host.Status()["isCurrent"]) && J.Bool(host.Query(client, intent)["isCurrent"]),
                "输入通知测试未完成初始业务和页面计算。");
            Check(J.Arr(host.Query(client, intent)["result"]?["recipes"]).Count > 0,
                "预热必须实际产生推荐，不能以空输入通过唤醒测试。");

            // 模拟后台尚未读取时主线程连续发布。暂停仅阻止下一次捕获/权威发布，不改变版本、
            // current 判断或已有结果；每次 Publish 都走原有输入锁和生产失效通知。
            var beforeBurst = host.InputVersion;
            const int burstCount = 64;
            host.WithCapturePausedForInputWakeTest(() =>
            {
                for (var index = 0; index < burstCount; index++)
                {
                    snapshot["snapshotSignature"] = "wake-burst-" + index;
                    J.Obj(J.Arr(snapshot["normalBusiness"]?["orders"])[0])["lastSeenAtUtc"] = "observed-" + index;
                    host.Publish(snapshot, catalog);
                    Check(!J.Bool(host.Status()["isCurrent"]), "突发输入发布后旧经营结果仍被标记为当前。");
                    Check(!J.Bool(host.Query(client, intent)["isCurrent"]), "突发输入发布后旧页面结果仍被标记为当前。");
                }
            });
            var finalVersion = host.InputVersion;
            Check(finalVersion == beforeBurst + burstCount, "合并通知不能合并或跳过输入版本撤销。");
            var finalSignature = "wake-burst-" + (burstCount - 1);
            await Until(() =>
            {
                var status = host.Status(); var page = host.Query(client, intent);
                if (J.Bool(status["isCurrent"])) Check(J.Str(status["sourceSnapshotSignature"]) == finalSignature,
                    "解除暂停后中间输入被错误发布为当前经营结果。");
                if (J.Bool(page["isCurrent"])) Check(J.Str(page["sourceSnapshotSignature"]) == finalSignature,
                    "解除暂停后中间输入被错误发布为当前页面结果。");
                return J.Bool(status["isCurrent"]) && J.Bool(page["isCurrent"]);
            }, "合并突发输入后未发布最终快照。");
            Check(J.Str(host.Status()["inputVersion"]) == finalVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "最终结果必须绑定最后一次输入版本。");

            // 库存属于计算输入。通知只缩短等待，发布当前结果前绝不能暂时恢复旧库存的执行资格。
            host.WithCapturePausedForInputWakeTest(() =>
            {
                J.Obj(snapshot["recommendationState"]?["ownedIngredientQty"])["1"] = 0;
                snapshot["snapshotSignature"] = "wake-inventory-zero";
                host.Publish(snapshot, catalog);
                Check(host.InputVersion > finalVersion, "库存变更没有推进输入版本。");
                Check(!J.Bool(host.Status()["isCurrent"]), "库存变更没有立即撤销旧经营结果。");
                Check(!J.Bool(host.Query(client, intent)["isCurrent"]), "库存变更没有立即撤销旧页面结果。");
            });
            await Until(() => J.Bool(host.Status()["isCurrent"]) && J.Bool(host.Query(client, intent)["isCurrent"]),
                "库存变更通知后没有恢复当前结果。");
            var inventoryResult = host.Status();
            Check(J.Str(inventoryResult["sourceSnapshotSignature"]) == "wake-inventory-zero"
                && J.Num(inventoryResult["runtimeSets"]?["ownedIngredientQty"]?["1"], -1) == 0,
                "库存重算必须发布新签名与实际零库存，不得复用旧数量。");

            // 在新通知仍待处理时停止。取消必须使旧结果立即不可用，后台等待应及时结束；
            // 之后的在途通知仍可安全抵达，但不得重新启动循环或产生游戏动作。
            var stopWatch = Stopwatch.StartNew();
            host.WithCapturePausedForInputWakeTest(() =>
            {
                snapshot["snapshotSignature"] = "wake-stop-pending";
                host.Publish(snapshot, catalog);
                host.Cancel();
                Check(!J.Bool(host.Status()["isCurrent"]), "通知后停止没有立即撤销当前经营资格。");
                Check(!J.Bool(host.Query(client, intent)["isCurrent"]), "通知后停止没有立即撤销当前页面资格。");
            });
            await host.Stop().WaitAsync(TimeSpan.FromSeconds(2));
            Check(stopWatch.Elapsed < TimeSpan.FromSeconds(2), "停止没有及时结束后台输入等待。");
            Parallel.For(0, burstCount, _ => host.Invalidate());
            await host.Stop().WaitAsync(TimeSpan.FromSeconds(2));
            Check(!J.Bool(host.Status()["isCurrent"]), "停止后的在途通知错误恢复了当前结果。");
            Check(Volatile.Read(ref calls) == 0 && !host.HasLease, "只读唤醒测试意外提交动作或创建租约。");
            Console.WriteLine($"PASS input wake: {checks} assertions; burst={burstCount}; stopMs={stopWatch.Elapsed.TotalMilliseconds:F1}; no game actions.");
        }
        finally
        {
            await host.Stop();
            Logger.Sources.Remove(log);
            // 只清理由本测试创建、绝对路径仍位于仓库 temp 下的独占目录，不访问用户配置。
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
}

namespace MystiaStewardCompanion.LocalApi
{
    internal sealed partial class LocalApiServer
    {
        /// <summary>
        /// 离线夹具专用：按生产的 authority → snapshot 锁顺序暂停下一次输入捕获，让同线程
        /// Publish/Status 通过可重入锁验证真实发布行为。不得在回调内 await、等待后台或逆序取锁。
        /// 此方法仅编入测试宿主，生产业务循环和信号量实现完全不替换。
        /// </summary>
        internal void WithCapturePausedForInputWakeTest(Action publishAndRead)
        {
            lock (_authorityTransitionLock)
            lock (_snapshotLock)
                publishAndRead();
        }
    }
}
