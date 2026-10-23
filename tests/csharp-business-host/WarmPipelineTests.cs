using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.Business.Application;
using MystiaStewardCompanion.Business.Application.Automation;
using MystiaStewardCompanion.Business.Domain.GameUi;
using MystiaStewardCompanion.Business.Domain.Support;
using MystiaStewardCompanion.Contracts;
using MystiaStewardCompanion.LocalApi;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 使用人工完整目录和八种点单的真实计算结果测量缓存命中后的整条消费链：捕获、绑定、自动化、
/// 游戏目标、发布及 HTTP 正文。旧链只作测试对照，保留本次修改前各边界的实际深复制操作。
/// 不启动后台线程、不授予执行租约、不调用 Unity；累计分配和耗时不能解释为实机帧率。
/// </summary>
internal static class WarmPipelineTests
{
    internal static async Task Run(JsonElement profile)
    {
        var fixture = J.Obj(JsonNode.Parse(File.ReadAllText("temp/csharp-business-load-fixture.json")));
        var root = Path.GetFullPath(Path.Combine("temp", "business-warm-pipeline-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var log = Logger.CreateLogSource("business-warm-pipeline");
        var host = new LocalApiServer(root, log, _ => throw new InvalidOperationException("本测试禁止游戏动作。"));
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        try
        {
            var settings = J.Obj(JsonNode.Parse(profile.GetRawText()));
            settings["automationEnabled"] = true; settings["gameUiEnabled"] = true;
            foreach (var kind in new[] { "rare", "normal" })
                foreach (var suffix in new[] { "GameUiPinningEnabled", "RecipeVariantEnabled", "CookerHighlightEnabled", "SeatHighlightEnabled", "OrderHighlightEnabled" })
                    settings[kind + suffix] = true;
            host.Authority.Register("11111111-1111-1111-1111-111111111111", "offline", new CompanionDeviceRegisterRequest
            { ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion, Platform = "windows", AppVersion = "test", Profile = JsonSerializer.SerializeToElement(settings) }, DateTime.UtcNow);
            var catalog = J.Obj(J.Clone(fixture["data"])); catalog["isComplete"] = true;
            var orders = new JsonArray(); var tags = new[] { "鲜", "甜", "素", "下酒" };
            for (var index = 0; index < 8; index++)
            {
                var order = J.Obj(J.Clone(J.Arr(fixture["orders"])[0]));
                order["traceId"] = "R-" + (index + 1); order["orderLifecycleSequence"] = index + 1;
                order["deskCode"] = index; order["foodTag"] = tags[index % 4]; order["beverageTag"] = index < 4 ? "水果" : "直饮";
                orders.Add(order);
            }
            var snapshot = LeaseExpiryTests.Snapshot();
            snapshot["snapshotSignature"] = "warm-中文-\"转义\\测试";
            snapshot["nightBusiness"] = J.Object(("orders", orders), ("activeRareGuests", new JsonArray()));
            snapshot["normalBusiness"] = J.Object(("orders", new JsonArray()));
            snapshot["recommendationState"] = J.Clone(fixture["runtime"]);
            host.Publish(snapshot, catalog);
            var expected = J.Obj(JsonNode.Parse(host.ProbeWarmPipeline(true)));
            var actual = J.Obj(JsonNode.Parse(host.ProbeWarmPipeline(false)));
            Check(expected["recommendations"]!.ToJsonString() == actual["recommendations"]!.ToJsonString(), "新旧整链推荐/当前目标不一致。");
            Check(J.Arr(actual["recommendations"]?["recommendations"]).Count == 8, "压力夹具未形成八种需求。");
            foreach (var field in expected)
                Check(field.Value?.ToJsonString() == actual[field.Key]?.ToJsonString(), "发布字段变化：" + field.Key);
            var immutableBefore = actual["recommendations"]!.ToJsonString();
            actual["recommendations"] = new JsonObject();
            Check(host.Status()["recommendations"]!.ToJsonString() == immutableBefore, "读取方修改污染冻结发布。");
            for (var index = 0; index < 2; index++) { host.ProbeWarmPipeline(true); host.ProbeWarmPipeline(false); }
            const int loops = 12;
            (long Bytes, double Milliseconds) Measure(bool legacy)
            {
                var watch = Stopwatch.StartNew(); var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < loops; index++) _ = host.ProbeWarmPipeline(legacy);
                return (GC.GetAllocatedBytesForCurrentThread() - before, watch.Elapsed.TotalMilliseconds);
            }
            var oldCost = Measure(true); var newCost = Measure(false);
            Check(newCost.Bytes < oldCost.Bytes * 0.2, "整链分配未降低至少80%，存在遗漏的巨树复制。");
            Console.WriteLine($"WARM-PIPELINE loops={loops}; legacyBytes={oldCost.Bytes}; frozenBytes={newCost.Bytes}; legacyMs={oldCost.Milliseconds:F3}; frozenMs={newCost.Milliseconds:F3}; reduction={1d-newCost.Bytes/(double)oldCost.Bytes:F5}; same-thread; not-FPS");

            // HTTP 同时读取不可变字符串；解析/修改结果不会影响其它读者或正在替换的发布引用。
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                for (var index = 0; index < 32; index++)
                {
                    var status = host.Status(); var signature = J.Str(status["sourceSnapshotSignature"]);
                    var rows = J.Objects(status["recommendations"]?["recommendations"]).ToArray();
                    if (rows.Length != 8 || signature.StartsWith("warm-concurrent-", StringComparison.Ordinal)
                        && rows.Any(row => J.Str(row["order"]?["lastSeenAtUtc"]) != signature))
                        throw new InvalidOperationException("并发HTTP读取观察到混合版本或可变树。");
                    await Task.Yield();
                }
            })).ToArray();
            var writer = Task.Run(async () =>
            {
                await start.Task;
                for (var index = 0; index < 8; index++)
                {
                    var signature = "warm-concurrent-" + index;
                    snapshot["snapshotSignature"] = signature;
                    foreach (var row in J.Objects(snapshot["nightBusiness"]?["orders"])) row["lastSeenAtUtc"] = signature;
                    host.Publish(snapshot, catalog); _ = host.ProbeWarmPipeline(false);
                    await Task.Yield();
                }
            });
            start.SetResult(true);
            await Task.WhenAll(readers.Append(writer)); Check(true, "并发读写失败。");
            snapshot["snapshotSignature"] = "warm-next"; host.Publish(snapshot, catalog);
            Check(!J.Bool(host.Status()["isCurrent"]), "新输入未立即撤销旧发布资格。");
            _ = host.ProbeWarmPipeline(false);
            Check(J.Bool(host.Status()["isCurrent"]) && J.Str(host.Status()["sourceSnapshotSignature"]) == "warm-next", "新帧未绑定当前签名。");
            // 用非空特殊普客目标补测序列化片段替换；大稀客夹具本身不包含普客，不能以空数组代替此边界。
            var normalSnapshot = LeaseExpiryTests.Snapshot();
            normalSnapshot["snapshotSignature"] = "normal-publication";
            normalSnapshot["specialBusiness"] = J.Object(("active", true), ("challengeTypeAvailable", true),
                ("challengeType", "Story_WackyCookingCompetition"), ("phase", "Phase 1"), ("foodTargetTags", new JsonArray("素")));
            var normalOrder = J.Objects(normalSnapshot["normalBusiness"]?["orders"]).Single();
            normalOrder["traceId"] = "N-123"; normalOrder["orderKey"] = "ptr:123"; normalOrder["specialBusinessRole"] = "wacky-target-order";
            var selected = J.Object(("matchFoodId", 101), ("matchBeverageId", 21), ("foodId", 101), ("recipeId", 201), ("beverageId", 21));
            var projection = J.Object(("recommendations", new JsonArray()), ("normalExecutionTargets", new JsonArray(J.Object(
                ("orderKey", MystiaStewardCompanion.Business.Domain.Orders.OrderIdentityAndSorting.NormalKey(normalOrder)), ("target", selected)))),
                ("futureField", J.Object(("text", "原样保留\"换行\n中文"))));
            var sourceBefore = projection.ToJsonString();
            foreach (var mode in new[] { "initial", "generation", "identity" })
            {
                if (mode == "generation") normalSnapshot["nightBusinessGeneration"] = 2;
                if (mode == "identity") normalOrder["foodId"] = 999;
                var published = host.ProbeBoundPublication(projection, normalSnapshot, LeaseExpiryTests.Catalog());
                var expectedBound = BusinessQueries.BindCurrentNormalTargets(projection, normalSnapshot);
                var publishedBound = J.Obj(published["recommendations"]);
                Check(publishedBound.Count == expectedBound.Count && expectedBound.All(field =>
                    publishedBound[field.Key]?.ToJsonString() == field.Value?.ToJsonString()), "非空普客目标发布替换失败：" + mode);
                var publishedTarget = published["recommendations"]?["normalExecutionTargets"]?[0]?["target"];
                Check(mode == "identity" ? publishedTarget == null : publishedTarget != null, "特殊目标未按当前身份拒绝/保留：" + mode);
            }
            Check(projection.ToJsonString() == sourceBefore, "普客重绑修改了冻结源投影。");
            Console.WriteLine($"PASS warm pipeline: {checks} assertions; full output/ownership/concurrent HTTP/currentness.");
        }
        finally
        {
            await host.Stop(); Logger.Sources.Remove(log);
            if (root.StartsWith(Path.GetFullPath("temp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
}

namespace MystiaStewardCompanion.LocalApi
{
    internal sealed partial class LocalApiServer
    {
        /// <summary>仅注入人工选优结果，绑定、发布、当前性和HTTP路径仍使用产品代码。</summary>
        internal JsonObject ProbeBoundPublication(JsonObject projection, JsonObject snapshot, JsonObject catalog)
        {
            Publish(snapshot, catalog);
            var frame = CaptureBusinessFrame(); MaterializeBusinessTree(projection);
            var targets = BusinessQueries.BindNormalTargetRows(projection["normalExecutionTargets"], frame.Snapshot);
            _ = AdvanceBusinessAutomation(frame, projection, targets, BusinessNow);
            var publication = CreateBusinessPublication(frame, projection, targets, new JsonObject(), 0);
            lock (_businessLock) { _businessFrame = _businessResultFrame = frame; _businessResult = publication; _businessError = null; }
            return Status();
        }

        /// <summary>生产消费链与旧复制边界对照；只在隔离测试程序集内提供，不是产品替代实现。</summary>
        internal string ProbeWarmPipeline(bool legacy)
        {
            var frame = CaptureBusinessFrame();
            var result = EvaluateBusinessOrders(frame, out _, out _);
            if (!legacy)
            {
                var normal = BusinessQueries.BindNormalTargetRows(result["normalExecutionTargets"], frame.Snapshot);
                _ = AdvanceBusinessAutomation(frame, result, normal, BusinessNow);
                var targets = PublishBusinessGameUi(frame, result, normal);
                var publication = CreateBusinessPublication(frame, result, normal, targets, 0);
                lock (_businessLock) { _businessFrame = _businessResultFrame = frame; _businessResult = publication; _businessError = null; }
                return GetBusinessStatusJson("protocolVersion=1");
            }
            result = BusinessQueries.BindCurrentNormalTargets(result, frame.Snapshot);
            var recommendations = J.Object(("recommendations", result["recommendations"]), ("isCurrent", true), ("pending", false), ("error", null));
            var normalTargets = J.Object(("targets", result["normalExecutionTargets"]), ("isCurrent", true), ("pending", false), ("error", null));
            _ = _businessCoordinator.Advance(J.Object(("snapshot", frame.Snapshot), ("preferences", frame.Preferences),
                ("recommendations", recommendations), ("normalExecutionTargets", normalTargets), ("data", frame.Data),
                ("favorites", frame.Favorites), ("authority", BuildBusinessAutomationAuthority(frame))), BusinessNow);
            var slots = J.Object(("rare", null), ("normal", null));
            foreach (var kind in new[] { "rare", "normal" })
            {
                var preferences = frame.Preferences;
                var features = J.Object(("listPinningEnabled", preferences[kind + "GameUiPinningEnabled"]),
                    ("recipeVariantEnabled", preferences[kind + "RecipeVariantEnabled"]), ("cookerHighlightEnabled", preferences[kind + "CookerHighlightEnabled"]),
                    ("seatHighlightEnabled", preferences[kind + "SeatHighlightEnabled"]), ("orderHighlightEnabled", preferences[kind + "OrderHighlightEnabled"]));
                var input = J.Object(("operation", kind), ("data", frame.Data), ("recommendations", result["recommendations"]),
                    ("color", preferences[kind + "TargetHighlightColor"]), ("features", features),
                    ("orders", frame.Snapshot["normalBusiness"]?["orders"]), ("executionTargets", result["normalExecutionTargets"]), ("executionTargetsCurrent", true),
                    ("specialBusiness", frame.Snapshot["specialBusiness"]), ("businessGeneration", frame.Snapshot["nightBusinessGeneration"]),
                    ("options", J.Object(("prioritizeMissionRecipe", J.Bool(preferences["missionRecipePriorityEnabled"]) && !J.Bool(frame.Snapshot["specialBusiness"]?["active"])),
                        ("specialBusiness", frame.Snapshot["specialBusiness"]))));
                slots[kind] = GameUiTargetService.Evaluate(input);
            }
            var published = J.Object(("protocolVersion", BusinessProtocol.Version), ("inputVersion", frame.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("sourceSnapshotSignature", frame.SnapshotSignature), ("sourceContext", frame.SourceContext), ("authorityRevision", frame.Authority.AuthorityRevision),
                ("runtimeSets", Cookers.BuildRuntimeSets(frame.Snapshot["recommendationState"], frame.Data)), ("gameUiTargets", slots), ("recommendations", result), ("calculationMs", 0));
            var response = J.Obj(J.Clone(published)); response["isCurrent"] = true; response["pending"] = false; response["error"] = null;
            response["automation"] = _businessCoordinator.Snapshot();
            return response.ToJsonString();
        }
    }
}
