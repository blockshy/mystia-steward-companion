using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 白天推荐与夜间目标发布的边界回归。生命周期由真实托管状态机驱动，目标入口执行生产共享
/// ValidateSession；只替换最后的 Unity 发布和游戏动作，不能用“所有阶段都接受”的替身通过。
/// </summary>
internal static class DaytimeUiLifecycleTests
{
    internal static async Task Run(JsonElement profile, bool expectBeforeFix)
    {
        var directory = Path.GetFullPath(Path.Combine("temp", "mystia-daytime-ui-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var log = Logger.CreateLogSource("daytime-ui-offline");
        var checks = 0; var calls = 0; var effects = 0; var sequence = 0; var holdAction = false;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var actionEntered = new ManualResetEventSlim();
        using var actionRelease = new ManualResetEventSlim();
        using var actionChecked = new ManualResetEventSlim();
        var host = new LocalApiServer(directory, log, request =>
        {
            Interlocked.Increment(ref calls);
            if (holdAction)
            {
                actionEntered.Set();
                if (!actionRelease.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("排队动作未释放。");
            }
            if (request.IsBusinessInputCurrent?.Invoke() == true) Interlocked.Increment(ref effects);
            if (holdAction) actionChecked.Set();
            var result = new OrderPreparationResult { Ok = false, Prepared = false };
            result.Automation.Outcome = "cancelled"; result.Automation.Stage = "lease";
            return result;
        });
        try
        {
            const string client = "11111111-1111-1111-1111-111111111111";
            var settings = J.Obj(JsonNode.Parse(profile.GetRawText())); settings["normalGameUiPinningEnabled"] = true;
            var authority = host.Authority.Register(client, "daytime lifecycle", new CompanionDeviceRegisterRequest
            { ProtocolVersion = 1, ProfileSchemaVersion = 1, Platform = "windows", AppVersion = "offline", Profile = JsonSerializer.SerializeToElement(settings) }, DateTime.UtcNow);
            var catalog = LeaseExpiryTests.Catalog();
            var normalIntent = J.Object(("protocolVersion", 1), ("kind", "normal"), ("selectedPlace", "妖怪兽道"));
            var rareIntent = J.Object(("protocolVersion", 1), ("kind", "rare"), ("customerId", 3), ("foodTag", "素"), ("beverageTag", "水果"));
            void Publish(string? phase, long generation, bool orders = false)
            {
                var snapshot = LeaseExpiryTests.Snapshot();
                snapshot["snapshotSignature"] = "daytime-ui-" + ++sequence;
                snapshot["nightBusinessLifecyclePhase"] = phase;
                snapshot["nightBusinessGeneration"] = generation;
                snapshot["nightBusinessAutomationAllowed"] = orders;
                if (orders)
                {
                    var order = J.Obj(J.Arr(snapshot["normalBusiness"]?["orders"])[0]);
                    order["traceId"] = "N-1"; order["orderKey"] = "ptr:123";
                }
                else { snapshot["nightBusiness"] = null; snapshot["normalBusiness"] = null; }
                host.Publish(snapshot, catalog);
            }
            async Task ReadOnlyReady(string label)
            {
                await Until(() => J.Bool(host.Status()["isCurrent"])
                    && J.Bool(host.Query(client, normalIntent)["isCurrent"])
                    && J.Bool(host.Query(client, rareIntent)["isCurrent"]), label + "未恢复只读推荐。");
                Check(host.Status()["error"] == null, label + "仍携带夜间发布错误。");
                Check(J.Arr(host.Query(client, normalIntent)["result"]?["recipes"]).Count > 0, label + "缺少普客页面结果。");
                Check(J.Arr(host.Query(client, rareIntent)["result"]?["recipes"]).Count > 0, label + "缺少稀客页面结果。");
            }
            async Task CheckSkipped(string label, int beforePublications, int beforeWithdrawals)
            {
                await ReadOnlyReady(label);
                await Task.Delay(550);
                Check(RuntimeUiPinningService.PublicationCount == beforePublications, label + "不应提交夜间目标，包括空集合。");
                Check(RuntimeUiPinningService.WithdrawalCount == beforeWithdrawals, label + "不应循环创建权威撤销屏障。");
                var slots = host.Status()["gameUiTargets"];
                Check(slots?["rare"] == null && slots?["normal"] == null, label + "不应返回可用游戏目标。");
                Check(Volatile.Read(ref effects) == 0, label + "意外产生游戏副作用。");
            }

            RuntimeNightBusinessLifecycle.ResetForTest();
            if (expectBeforeFix)
            {
                // 此显式模式用于修复前证据，不进入正常 aggregate：严格生产门禁必须重现用户原文。
                RuntimeNightBusinessLifecycle.ActivateForTest(); RuntimeNightBusinessLifecycle.DestroyForTest();
                Publish("Destroyed", 1); host.Start();
                await Until(() => J.Str(host.Status()["error"]).Contains("phase=Destroyed", StringComparison.Ordinal), "未复现 Destroyed/gen1 原异常。");
                Check(RuntimeUiPinningService.RejectionCount > 0, "原异常必须来自生产共享门禁。");
                Console.WriteLine("REPRODUCED: " + J.Str(host.Status()["error"]));
                return;
            }

            // 从未进入营业的白天；即使已授予测试租约，也不能发出夜间目标或动作。
            host.GrantLease(client, authority.AuthorityRevision, TimeSpan.FromSeconds(60));
            Publish("Inactive", 0); host.Start();
            await CheckSkipped("从未营业的白天", RuntimeUiPinningService.PublicationCount, RuntimeUiPinningService.WithdrawalCount);
            host.ExpireLease();
            RuntimeNightBusinessLifecycle.ActivateForTest(); Publish("Active", 1, orders: true);
            await Until(() => J.Bool(host.Status()["isCurrent"]) && RuntimeUiPinningService.Targets.Count > 0, "Active同代没有发布目标。");
            Check(RuntimeUiPinningService.Targets.Count > 0, "真实Active目标应正常恢复。");

            foreach (var phase in new[] { "Closing", "Destroyed" })
            {
                if (phase == "Closing") RuntimeNightBusinessLifecycle.CloseForTest(); else RuntimeNightBusinessLifecycle.DestroyForTest();
                var publications = RuntimeUiPinningService.PublicationCount; var withdrawals = RuntimeUiPinningService.WithdrawalCount;
                Publish(phase, 1); await CheckSkipped(phase + "白天/过渡", publications, withdrawals);
                var rejected = false;
                try { RuntimeUiPinningService.UpdateTargets(1, Array.Empty<RuntimeUiTargetSnapshot>()); }
                catch (InvalidOperationException error) { rejected = error.Message.Contains("phase=" + phase, StringComparison.Ordinal); }
                Check(rejected, phase + "的真实空目标发布仍必须被拒绝。");
            }

            RuntimeNightBusinessLifecycle.ActivateForTest();
            host.GrantLease(client, authority.AuthorityRevision, TimeSpan.FromSeconds(60));
            foreach (var phase in new string?[] { null, "Unknown", "active" })
            {
                var publications = RuntimeUiPinningService.PublicationCount; var withdrawals = RuntimeUiPinningService.WithdrawalCount;
                Publish(phase, 2, orders: true);
                await CheckSkipped("缺失或非精确Active的快照", publications, withdrawals);
            }
            host.ExpireLease(); Publish("Active", 2, orders: true);
            await Until(() => J.Bool(host.Status()["isCurrent"]) && RuntimeUiPinningService.Targets.Count > 0, "下一代Active未恢复目标。");
            Check(RuntimeNightBusinessLifecycle.Generation == 2, "重新营业必须进入下一代。");

            // 真实生命周期先变化，快照尚保留旧Active和允许自动化；两者不同步时仍只允许读取。
            RuntimeNightBusinessLifecycle.DestroyForTest();
            host.GrantLease(client, authority.AuthorityRevision, TimeSpan.FromSeconds(60));
            var before = RuntimeUiPinningService.PublicationCount; var withdrawn = RuntimeUiPinningService.WithdrawalCount;
            Publish("Active", 2, orders: true);
            await CheckSkipped("快照落后于真实Destroyed", before, withdrawn);
            RuntimeNightBusinessLifecycle.ActivateForTest();
            before = RuntimeUiPinningService.PublicationCount; withdrawn = RuntimeUiPinningService.WithdrawalCount;
            Publish("Active", 2, orders: true);
            await CheckSkipped("旧代快照遇到下一代Active", before, withdrawn);

            // 通过宿主预检查后，在真实门禁的两次校验之间切场。此轮拒绝不能被吞掉；下轮须自愈。
            var rejections = RuntimeUiPinningService.RejectionCount;
            RuntimeUiPinningService.BeforePublicationForTest = () =>
            { RuntimeUiPinningService.BeforePublicationForTest = null; RuntimeNightBusinessLifecycle.DestroyForTest(); };
            Publish("Active", 3, orders: true);
            await Until(() => RuntimeUiPinningService.RejectionCount > rejections, "没有覆盖发布时切场竞争。");
            await ReadOnlyReady("Active发布竞争后的恢复");
            Check(host.Status()["gameUiTargets"]?["normal"] == null, "切场后不得保留游戏目标。");
            Check(Volatile.Read(ref calls) == 0, "非Active、旧代或发布失败不应提交任何动作。");

            // 同代Active的真正发布失败保持明确错误，并撤销本轮未提交命令，不能被白天兼容吞掉。
            RuntimeNightBusinessLifecycle.ActivateForTest();
            RuntimeUiPinningService.PublicationFailureForTest = new InvalidOperationException("controlled-active-publication-failure");
            Publish("Active", 4, orders: true);
            await Until(() => J.Str(host.Status()["error"]) == "controlled-active-publication-failure", "Active真实错误被隐藏。");
            Check(!J.Bool(host.Status()["isCurrent"]) && !J.Bool(host.Status()["pending"]), "Active失败必须明确非当前且有错误。");
            Check(Volatile.Read(ref calls) == 0, "Active发布失败之后仍提交了游戏动作。");
            RuntimeUiPinningService.PublicationFailureForTest = null;
            host.ExpireLease(); Publish("Active", 4, orders: true);
            await Until(() => J.Bool(host.Status()["isCurrent"]) && RuntimeUiPinningService.Targets.Count > 0, "真正失败恢复后未发布新目标。");

            // 队列已接收动作后生命周期才关闭，且没有新快照/失效版本：执行许可闭包也必须拒绝。
            holdAction = true; host.GrantLease(client, authority.AuthorityRevision, TimeSpan.FromSeconds(60));
            Publish("Active", 4, orders: true);
            await Until(() => actionEntered.IsSet, "未进入受控排队动作。");
            var queuedVersion = host.InputVersion;
            RuntimeNightBusinessLifecycle.CloseForTest(); actionRelease.Set();
            // 必须在 Stop 撤销输入版本之前完成许可复核，否则测试可能只验证了停止门禁而漏掉生命周期。
            await Until(() => actionChecked.IsSet, "排队动作未完成生命周期许可复核。");
            Check(host.InputVersion == queuedVersion, "排队关闭回归不能借新快照或停止来撤销输入版本。");
            Check(Volatile.Read(ref effects) == 0, "未停止宿主时生命周期本身就必须撤销排队许可。");
            await host.Stop();
            Check(Volatile.Read(ref calls) == 1 && Volatile.Read(ref effects) == 0, "排队后关闭营业不得依赖快照刷新才能撤销动作许可。");
            Console.WriteLine($"PASS daytime UI lifecycle: {checks} assertions; real guard/tracker keep daytime reads separate from night effects.");
        }
        finally
        {
            actionRelease.Set(); RuntimeUiPinningService.BeforePublicationForTest = null; RuntimeUiPinningService.PublicationFailureForTest = null;
            await host.Stop(); RuntimeNightBusinessLifecycle.ResetForTest(); RuntimeNightBusinessLifecycle.ActivateForTest();
            Logger.Sources.Remove(log);
            var parent = Path.GetFullPath("temp") + Path.DirectorySeparatorChar;
            if (directory.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, true);
        }
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = Environment.TickCount64 + 8000;
        while (!condition()) { if (Environment.TickCount64 >= deadline) throw new TimeoutException(message); await Task.Delay(20); }
    }
}
