using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 确定性复现 main active/queued 语义：计算取帧后，最新快照持续前进，完成结果仍应发布为
/// 非当前只读展示。通过测试程序集的宿主 partial 分开捕获/完成两个时刻，不依赖机器速度或睡眠。
/// 执行帧、游戏辅助目标和命令数量另行断言，防止用放行旧权限来掩盖展示饥饿。
/// </summary>
internal static class CompletedDisplayTests
{
    internal static async Task Run(JsonElement profile)
    {
        var root = Path.GetFullPath(Path.Combine("temp", "completed-display-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var log = Logger.CreateLogSource("completed-display");
        var effects = 0; var checks = 0;
        var host = new LocalApiServer(root, log, _ => { effects++; throw new InvalidOperationException("禁止真实动作。"); });
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        try
        {
            var settings = J.Obj(JsonNode.Parse(profile.GetRawText())); settings["automationEnabled"] = false;
            host.Authority.Register("11111111-1111-1111-1111-111111111111", "offline", new CompanionDeviceRegisterRequest
            { ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion, Platform = "windows", AppVersion = "test", Profile = JsonSerializer.SerializeToElement(settings) }, DateTime.UtcNow);
            var snapshot = LeaseExpiryTests.Snapshot(); var catalog = LeaseExpiryTests.Catalog();
            J.Obj(snapshot["normalBusiness"])["orders"] = new JsonArray();
            var orders = new JsonArray(J.Object(("traceId", "R-1"), ("orderLifecycleSequence", 1), ("deskCode", 0),
                ("guestId", 3), ("runtimeGuestId", 3), ("guestName", "测试稀客"), ("foodTag", "素"), ("beverageTag", "水果")));
            J.Obj(snapshot["nightBusiness"])["orders"] = orders;
            // 每次新观察都发生在计算捕获之后、完成之前，保证每个结果都过期而非碰巧落在静止间隙。
            for (var index = 0; index < 5; index++)
            {
                snapshot["snapshotSignature"] = "captured-" + index;
                J.Obj(orders[0])["lastSeenAtUtc"] = "captured-" + index;
                host.Publish(snapshot, catalog);
                var complete = host.CaptureCompletedDisplayForTest();
                snapshot["snapshotSignature"] = "queued-" + index;
                J.Obj(orders[0])["lastSeenAtUtc"] = "queued-" + index;
                host.Publish(snapshot, catalog);
                Check(complete(), "持续输入丢弃了完成的只读结果。");
                var status = host.Status();
                Check(!J.Bool(status["isCurrent"]) && J.Bool(status["pending"]), "旧结果获得当前资格或错误关闭pending。");
                Check(!J.Bool(status["isCalculating"]), "完成的旧结果因pending被误报为仍在求解。");
                Check(J.Str(status["sourceSnapshotSignature"]) == "captured-" + index, "旧结果被重新盖上最新签名。");
                Check(J.Str(status["recommendations"]?["recommendations"]?[0]?["order"]?["lastSeenAtUtc"]) == "captured-" + index,
                    "只读完成结果没有持续前进。");
                Check(J.Arr(status["recommendations"]?["normalExecutionTargets"]).Count == 0
                    && status["gameUiTargets"]?["rare"] == null && status["gameUiTargets"]?["normal"] == null,
                    "过期展示携带了可误用的执行/高亮目标。");
            }
            Check(!host.HasExecutionFrameForCompletedDisplayTest && effects == 0, "只读完成发布推进了执行帧或产生动作。");

            foreach (var field in new[] { "automationSessionId", "nightBusinessGeneration", "nightBusinessLifecyclePhase", "runtimeDataSignature" })
            {
                host.Publish(snapshot, catalog); var complete = host.CaptureCompletedDisplayForTest();
                var changed = J.Obj(J.Clone(snapshot)); changed[field] = field == "nightBusinessGeneration" ? JsonValue.Create(2) : JsonValue.Create("other");
                host.Publish(changed, catalog);
                Check(!complete() && host.Status()["recommendations"] == null, "跨来源旧完成结果重新出现：" + field);
            }
            host.Publish(snapshot, catalog); var previousComplete = host.CaptureCompletedDisplayForTest();
            snapshot["snapshotSignature"] = "newer-completion"; host.Publish(snapshot, catalog);
            var newerComplete = host.CaptureCompletedDisplayForTest();
            snapshot["snapshotSignature"] = "queued-after-newer"; host.Publish(snapshot, catalog);
            Check(newerComplete() && !previousComplete(), "较旧完成结果覆盖了较新展示。");
            Check(J.Str(host.Status()["sourceSnapshotSignature"]) == "newer-completion", "结果回退到较旧版本。");
            var stoppedComplete = host.CaptureCompletedDisplayForTest(); await host.Stop();
            Check(!stoppedComplete(), "宿主停止后恢复了旧完成结果。");
            Console.WriteLine($"PASS completed display: {checks} assertions; continuous invalidation, read-only progress and context rejection.");
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
        /// <summary>仅测试捕获与完成时刻；调用真实求解/发布逻辑，不新增产品中的可注入权限或调度钩子。</summary>
        internal Func<bool> CaptureCompletedDisplayForTest()
        {
            var frame = CaptureBusinessFrame();
            return () =>
            {
                var result = EvaluateBusinessOrders(frame, out _, out var milliseconds);
                return PublishCompletedBusinessDisplay(frame, result, milliseconds);
            };
        }
        internal bool HasExecutionFrameForCompletedDisplayTest { get { lock (_businessLock) return _businessFrame != null; } }
    }
}
