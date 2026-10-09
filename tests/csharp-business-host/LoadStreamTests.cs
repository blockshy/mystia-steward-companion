using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 大目录连续输入验证：每250/500毫秒更新纯订单观察时间、送达状态和不影响候选的经营进度。
/// 冷计算允许失效，但随后必须在持续流中发布当前帧；库存变化必须立即失效且稳定后重算。
/// </summary>
internal static class LoadStreamTests
{
    internal static async Task Run(JsonElement profile, int readInterval = 10)
    {
        var fixture = J.Obj(JsonNode.Parse(File.ReadAllText("temp/csharp-business-load-fixture.json")));
        foreach (var interval in new[] { 250, 500 })
        {
            var root = Path.GetFullPath(Path.Combine("temp", "mystia-business-stream-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root); var log = Logger.CreateLogSource("business-load-stream");
            var host = new LocalApiServer(root, log, _ => throw new InvalidOperationException("负载测试不允许任何游戏动作。"));
            try
            {
                var settings = J.Obj(JsonNode.Parse(profile.GetRawText())); settings["automationEnabled"] = false;
                host.Authority.Register("11111111-1111-1111-1111-111111111111", "stream", new CompanionDeviceRegisterRequest
                { ProtocolVersion = 1, ProfileSchemaVersion = 1, Platform = "windows", AppVersion = "offline", Profile = JsonSerializer.SerializeToElement(settings) }, DateTime.UtcNow);
                var catalog = J.Obj(J.Clone(fixture["data"])); catalog["isComplete"] = true;
                var snapshot = J.Object(("automationSessionId", "stream"), ("nightBusinessGeneration", 1), ("nightBusinessAutomationAllowed", true),
                    ("nightBusiness", J.Object(("orders", fixture["orders"]), ("activeRareGuests", new JsonArray()))),
                    ("normalBusiness", J.Object(("orders", new JsonArray()))), ("recommendationState", fixture["runtime"]),
                    ("specialBusiness", null), ("automationEvents", new JsonArray()), ("automationCookingJobs", new JsonArray()));
                var order = J.Obj(J.Arr(J.Obj(snapshot["nightBusiness"])["orders"])[0]);
                snapshot["snapshotSignature"] = "stream-initial"; host.Publish(snapshot, catalog); host.Start();
                // 页面计算与自动化共享后台线程；连续输入下手动稀客查询也必须能返回当前结果。
                var pageIntent = J.Object(("protocolVersion", 1), ("kind", "rare"), ("customerId", 3),
                    ("foodTag", "鲜"), ("beverageTag", "水果"));
                var pageVersions = new HashSet<string>();
                _ = host.Query("11111111-1111-1111-1111-111111111111", pageIntent);
                var watch = Stopwatch.StartNew(); var lastPublish = -interval; var lastRead = -readInterval;
                var updates = 0; var current = new HashSet<string>();
                var firstCurrent = -1d; var maximumCalculationMs = 0d;
                while (watch.ElapsedMilliseconds < 10000)
                {
                    // 独立控制读取与输入频率：默认10ms是极端读取压力，750ms对应当前前端轮询。
                    // 两种模式都继续每250/500ms发布输入，不通过暂停输入来获得current结果。
                    if (watch.ElapsedMilliseconds - lastRead >= readInterval)
                    {
                        var status = host.Status();
                        var page = host.Query("11111111-1111-1111-1111-111111111111", pageIntent);
                        if (J.Bool(page["isCurrent"])) pageVersions.Add(J.Str(page["sourceSnapshotSignature"]));
                        if (J.Bool(status["isCurrent"]))
                        {
                            if (firstCurrent < 0) firstCurrent = watch.Elapsed.TotalMilliseconds;
                            current.Add(J.Str(status["sourceSnapshotSignature"]));
                            maximumCalculationMs = Math.Max(maximumCalculationMs, J.Num(status["calculationMs"]));
                        }
                        lastRead = (int)watch.ElapsedMilliseconds;
                    }
                    if (watch.ElapsedMilliseconds - lastPublish >= interval)
                    {
                        order["lastSeenAtUtc"] = "observed-" + updates;
                        order["hasServedFood"] = updates % 2 == 1;
                        snapshot["nightBusinessProgress"] = updates;
                        snapshot["snapshotSignature"] = "stream-" + updates++;
                        host.Publish(snapshot, catalog); lastPublish = (int)watch.ElapsedMilliseconds;
                    }
                    await Task.Delay(10);
                }
                if (current.Count < 2) throw new InvalidOperationException($"{interval}ms连续流发生推荐饥饿：updates={updates},current={current.Count},firstMs={firstCurrent},cache={host.CacheStatistics}。");
                if (pageVersions.Count < 2) throw new InvalidOperationException($"{interval}ms连续流发生页面推荐饥饿：currentPages={pageVersions.Count},current={current.Count},firstMs={firstCurrent},cache={host.CacheStatistics}。");
                var beforeInventory = host.InputVersion;
                J.Obj(J.Obj(snapshot["recommendationState"])["ownedIngredientQty"])["1"] = 0;
                snapshot["snapshotSignature"] = "inventory-changed"; host.Publish(snapshot, catalog);
                if (host.InputVersion <= beforeInventory || J.Bool(host.Status()["isCurrent"])) throw new InvalidOperationException("库存变更没有立即撤销旧帧。");
                watch.Restart();
                while (!J.Bool(host.Status()["isCurrent"]))
                { if (watch.ElapsedMilliseconds > 12000) throw new TimeoutException("库存变化后未恢复当前推荐。"); await Task.Delay(20); }
                var restored = host.Status();
                if (J.Str(restored["sourceSnapshotSignature"]) != "inventory-changed") throw new InvalidOperationException("库存重算发布了旧帧签名。");
                Console.WriteLine(J.Object(("intervalMs", interval), ("readIntervalMs", readInterval), ("updates", updates), ("publishedVersions", current.Count),
                    ("currentPageVersions", pageVersions.Count), ("firstCurrentMs", firstCurrent),
                    ("maximumObservedCalculationMs", maximumCalculationMs), ("inventoryRecoveryMs", watch.Elapsed.TotalMilliseconds),
                    ("cache", host.CacheStatistics)).ToJsonString());
            }
            finally
            {
                await host.Stop(); Logger.Sources.Remove(log);
                if (root.StartsWith(Path.GetFullPath("temp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
            }
        }
    }
}
