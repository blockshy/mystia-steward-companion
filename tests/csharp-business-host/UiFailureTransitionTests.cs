using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 持续输入异常的状态迁移回归。运行生产业务循环，以计数替身观察真实 UI 发布/撤销边界，
/// 确认修复阻止的是重复屏障创建和版本推进，而非仅隐藏日志。所有输入与配置均为独占离线样本。
/// </summary>
internal static class UiFailureTransitionTests
{
    internal static async Task Run(JsonElement profile)
    {
        var directory = Path.GetFullPath(Path.Combine("temp", "mystia-business-ui-failure-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var log = Logger.CreateLogSource("business-ui-failure-offline");
        var checks = 0;
        var actions = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException(message);
        }
        var host = new LocalApiServer(directory, log, _ =>
        {
            Interlocked.Increment(ref actions);
            throw new InvalidOperationException("UI 异常测试不得产生游戏动作。");
        });
        var baseline = RuntimeUiPinningService.WithdrawalCount;
        RuntimeNightBusinessLifecycle.SetGenerationForTest(1);
        try
        {
            // 沿用已通过租约测试的最小完整目录/精确订单，禁用自动化，仅打开普客 UI 目标。
            var snapshot = LeaseExpiryTests.Snapshot();
            var catalog = LeaseExpiryTests.Catalog();
            // UI 投影还要求可验证的原生身份格式；租约测试的占位名字不用于游戏界面绑定。
            var order = J.Obj(J.Arr(J.Obj(snapshot["normalBusiness"])["orders"])[0]);
            order["traceId"] = "N-1";
            order["orderKey"] = "ptr:123";
            host.Publish(snapshot, catalog);
            host.Start();
            await Until(() => host.Status()["error"] != null && RuntimeUiPinningService.WithdrawalCount > baseline,
                "缺少主设备时未进入输入不可用状态。");
            var version = host.InputVersion;
            await ObserveSeveralRetries();
            Check(RuntimeUiPinningService.WithdrawalCount == baseline + 1, "缺少主设备时重复轮询不能重复创建 UI 屏障。");
            Check(host.InputVersion == version, "持续相同异常不能自行推进业务输入版本。");
            Check(!J.Bool(host.Status()["isCurrent"]) && !J.Bool(host.Status()["pending"]), "持续错误必须保留明确非当前状态。");

            var settings = J.Obj(JsonNode.Parse(profile.GetRawText()));
            settings["automationEnabled"] = false;
            settings["normalGameUiPinningEnabled"] = true;
            host.Authority.Register("11111111-1111-1111-1111-111111111111", "ui recovery", new CompanionDeviceRegisterRequest
            {
                ProtocolVersion = 1, ProfileSchemaVersion = 1,
                Platform = "windows", AppVersion = "offline", Profile = JsonSerializer.SerializeToElement(settings),
            }, DateTime.UtcNow);
            host.Publish(snapshot, catalog);
            await Until(() => J.Bool(host.Status()["isCurrent"]) && RuntimeUiPinningService.Targets.Count > 0,
                "主设备恢复后未重新发布真实计算的 UI 目标。");
            Check(host.Status()["error"] == null && RuntimeUiPinningService.PublicationCount > 0,
                "成功恢复必须清除错误并重新发布目标。");

            // 新故障必须再次撤销刚恢复的目标，但保持坏输入时只能撤销一次。
            host.InjectSnapshotJsonForFailureTest("[");
            await Until(() => host.Status()["error"] != null && RuntimeUiPinningService.WithdrawalCount == baseline + 2,
                "恢复后的新故障未撤销旧目标。");
            version = host.InputVersion;
            var previousError = J.Str(host.Status()["error"]);
            await ObserveSeveralRetries();
            Check(RuntimeUiPinningService.WithdrawalCount == baseline + 2 && host.InputVersion == version,
                "持续损坏快照不能重复撤销目标或推进版本。");
            Check(RuntimeUiPinningService.Targets.Count == 0 && !J.Bool(host.Status()["isCurrent"]),
                "故障期间不能保留可执行的旧 UI 目标。");

            // 错误原因变化仍可诊断，但已不可用的同代次目标无需再创建屏障。
            host.InjectSnapshotJsonForFailureTest("[]");
            await Until(() => host.Status()["error"] != null && J.Str(host.Status()["error"]) != previousError,
                "不同错误原因没有更新到业务状态。");
            Check(RuntimeUiPinningService.WithdrawalCount == baseline + 2, "不同错误文本不应重复撤销已禁用目标。");

            RuntimeNightBusinessLifecycle.SetGenerationForTest(2);
            await Until(() => RuntimeUiPinningService.WithdrawalCount == baseline + 3,
                "持续故障跨经营代次后应为新代次撤销一次。");
            version = host.InputVersion;
            await ObserveSeveralRetries();
            Check(RuntimeUiPinningService.WithdrawalCount == baseline + 3 && host.InputVersion == version,
                "新经营代次的持续故障也必须保持幂等。");

            snapshot["nightBusinessGeneration"] = 2;
            var publications = RuntimeUiPinningService.PublicationCount;
            host.Publish(snapshot, catalog);
            await Until(() => J.Bool(host.Status()["isCurrent"]) && RuntimeUiPinningService.Targets.Count > 0,
                "新经营代次恢复后未重新发布目标。");
            Check(RuntimeUiPinningService.PublicationCount > publications && host.Status()["error"] == null,
                "第二次恢复必须解除故障撤销状态。");
            host.InjectSnapshotJsonForFailureTest("[");
            await Until(() => RuntimeUiPinningService.WithdrawalCount == baseline + 4,
                "第二次恢复后的故障未再次撤销目标。");
            Check(RuntimeUiPinningService.Targets.Count == 0, "每次真实恢复后的新故障均应撤销目标。");
            Check(Volatile.Read(ref actions) == 0, "输入异常及恢复测试不得产生游戏副作用。");
            Console.WriteLine($"PASS UI failure transitions: {checks} assertions; unavailable state withdraws once and recovers publications.");
        }
        finally
        {
            await host.Stop();
            RuntimeNightBusinessLifecycle.SetGenerationForTest(1);
            Logger.Sources.Remove(log);
            // 仅清理由本测试创建且仍位于仓库 temp 范围内的独占目录。
            var temporaryRoot = Path.GetFullPath("temp") + Path.DirectorySeparatorChar;
            if (directory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>覆盖至少三个正常的 250ms 重试周期，验证异常不会持续产生写边界。</summary>
    private static Task ObserveSeveralRetries() => Task.Delay(1000);

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException(message);
            await Task.Delay(20);
        }
    }
}

namespace MystiaStewardCompanion.LocalApi
{
    internal sealed partial class LocalApiServer
    {
        /// <summary>离线测试专用损坏输入注入，沿生产发布锁失效；不更改磁盘或调用游戏接口。</summary>
        internal void InjectSnapshotJsonForFailureTest(string json)
        {
            lock (_snapshotLock)
            {
                _snapshotJson = json;
                InvalidateBusinessInput();
            }
        }
    }
}
