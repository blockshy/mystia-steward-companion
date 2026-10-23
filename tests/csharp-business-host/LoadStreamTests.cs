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
    internal static async Task Run(JsonElement profile, int readInterval = 10, bool volatileRuntime = false)
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
                { ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion, Platform = "windows", AppVersion = "offline", Profile = JsonSerializer.SerializeToElement(settings) }, DateTime.UtcNow);
                var catalog = J.Obj(J.Clone(fixture["data"])); catalog["isComplete"] = true;
                var snapshot = J.Object(("automationSessionId", "stream"), ("nightBusinessGeneration", 1), ("nightBusinessLifecyclePhase", "Active"), ("nightBusinessAutomationAllowed", true),
                    ("nightBusiness", J.Object(("orders", fixture["orders"]), ("activeRareGuests", new JsonArray()))),
                    ("normalBusiness", J.Object(("orders", new JsonArray()))), ("recommendationState", fixture["runtime"]),
                    ("specialBusiness", null), ("automationEvents", new JsonArray()), ("automationCookingJobs", new JsonArray()));
                var order = J.Obj(J.Arr(J.Obj(snapshot["nightBusiness"])["orders"])[0]);
                if (volatileRuntime)
                {
                    // 模拟最新日志中的幽幽子第三阶段和八种同时存在的需求。连续变动的是
                    // 观察进度与厨具实例忙闲；规则本身不变，必须复用纯投影但重绑本轮订单。
                    J.Obj(snapshot["nightBusiness"])["orders"] = J.Array(Enumerable.Range(0, 8).Select(index =>
                    {
                        var entry = J.Obj(J.Clone(order));
                        entry["traceId"] = "R-" + index; entry["orderLifecycleSequence"] = index + 1;
                        entry["deskCode"] = index; entry["specialBusinessRole"] = "yuyuko-boss-order";
                        entry["foodTag"] = new[] { "鲜", "甜", "素", "下酒" }[index % 4];
                        entry["beverageTag"] = index < 4 ? "水果" : "直饮";
                        return entry;
                    }));
                    order = J.Obj(J.Arr(snapshot["nightBusiness"]?["orders"])[0]);
                    snapshot["specialBusiness"] = J.Object(("active", true), ("challengeTypeAvailable", true),
                        ("challengeType", "Challenge_Yuyuko"), ("phase", "Phase 3"),
                        ("foodTargetTags", new JsonArray()), ("beverageTargetTags", new JsonArray()));
                }
                snapshot["snapshotSignature"] = "stream-initial"; host.Publish(snapshot, catalog); host.Start();
                // 页面计算与自动化共享后台线程；连续输入下手动稀客查询也必须能返回当前结果。
                var pageIntent = J.Object(("protocolVersion", 1), ("kind", "rare"), ("customerId", 3),
                    ("foodTag", "鲜"), ("beverageTag", "水果"));
                var pageVersions = new HashSet<string>();
                _ = host.Query("11111111-1111-1111-1111-111111111111", pageIntent);
                var watch = Stopwatch.StartNew(); var lastPublish = -interval; var lastRead = -readInterval;
                var updates = 0; var current = new HashSet<string>();
                var firstCurrent = -1d; var maximumCalculationMs = 0d; var sawCalculating = false;
                // 八种特殊经营冷需求比普通单例重得多；给首轮完整搜索及随后连续输入留出
                // 独立观测窗口。仍要求至少两个当前业务/页面版本，不能以只读旧结果代替成功。
                while (watch.ElapsedMilliseconds < (volatileRuntime ? 40000 : 10000))
                {
                    // 独立控制读取与输入频率：默认10ms是极端读取压力，750ms对应当前前端轮询。
                    // 两种模式都继续每250/500ms发布输入，不通过暂停输入来获得current结果。
                    if (watch.ElapsedMilliseconds - lastRead >= readInterval)
                    {
                        var status = host.Status();
                        sawCalculating |= J.Bool(status["isCalculating"]);
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
                        if (volatileRuntime)
                        {
                            J.Obj(snapshot["specialBusiness"])["lastTargetUpdated"] = "observed-" + updates;
                            J.Obj(snapshot["specialBusiness"])["targetTimeProgress"] = updates / 100d;
                            J.Obj(snapshot["specialBusiness"])["currentAnger"] = updates % 100;
                            foreach (var cooker in J.Objects(snapshot["recommendationState"]?["placedCookers"]))
                            {
                                cooker["automationAvailable"] = updates % 2 == 0;
                                cooker["automationAvailabilityDiagnostic"] = "synthetic-busy-" + updates;
                            }
                        }
                        snapshot["snapshotSignature"] = "stream-" + updates++;
                        host.Publish(snapshot, catalog); lastPublish = (int)watch.ElapsedMilliseconds;
                    }
                    await Task.Delay(10);
                }
                if (current.Count < 2) throw new InvalidOperationException($"{interval}ms连续流发生推荐饥饿：updates={updates},current={current.Count},firstMs={firstCurrent},cache={host.CacheStatistics}。");
                if (pageVersions.Count < 2) throw new InvalidOperationException($"{interval}ms连续流发生页面推荐饥饿：currentPages={pageVersions.Count},current={current.Count},firstMs={firstCurrent},cache={host.CacheStatistics}。");
                // 先记录连续流成绩，后续库存冷算异常也不能抹掉这一阶段的证据。
                Console.WriteLine(J.Object(("stage", "continuous"), ("intervalMs", interval),
                    ("updates", updates), ("currentVersions", current.Count), ("currentPages", pageVersions.Count),
                    ("firstCurrentMs", firstCurrent), ("sawCalculating", sawCalculating), ("cache", host.CacheStatistics)).ToJsonString());
                var beforeInventory = host.InputVersion;
                J.Obj(J.Obj(snapshot["recommendationState"])["ownedIngredientQty"])["1"] = 0;
                snapshot["snapshotSignature"] = "inventory-changed"; host.Publish(snapshot, catalog);
                if (host.InputVersion <= beforeInventory || J.Bool(host.Status()["isCurrent"])) throw new InvalidOperationException("库存变更没有立即撤销旧帧。");
                watch.Restart();
                while (!J.Bool(host.Status()["isCurrent"]))
                {
                    // 八种幽幽子需求的独立冷算实测已超过12秒；库存耗尽是有效输入变化，
                    // 必须重新完成搜索。此处只放宽该极端夹具的观测期限，不延缓实际轮询或放行旧帧。
                    if (watch.ElapsedMilliseconds > (volatileRuntime ? 45000 : 12000))
                        throw new TimeoutException("库存变化后未恢复当前推荐。");
                    await Task.Delay(20);
                }
                var restored = host.Status();
                if (J.Str(restored["sourceSnapshotSignature"]) != "inventory-changed") throw new InvalidOperationException("库存重算发布了旧帧签名。");
                if (volatileRuntime && !sawCalculating) throw new InvalidOperationException("长冷算未发布真实计算活动标志。");
                Console.WriteLine(J.Object(("intervalMs", interval), ("readIntervalMs", readInterval), ("volatileRuntime", volatileRuntime), ("updates", updates), ("publishedVersions", current.Count),
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
