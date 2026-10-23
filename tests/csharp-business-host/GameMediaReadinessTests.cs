using System.Text.Json;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 真实业务宿主排队许可的资源边界：只替换 Unity 动作为受控闩锁，许可闭包仍由生产
/// ExecuteBusinessCommand 创建。资源重读不必等待客户端释放旧租约，也不能在恢复后追认旧请求。
/// </summary>
internal static class GameMediaReadinessTests
{
    internal static async Task Run(JsonElement profile)
    {
        var root = Path.Combine(Path.GetTempPath(), "mystia-media-business-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = Logger.CreateLogSource("media-business-isolated");
        var checks = 0; var effects = 0; var mediaReady = 1;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<OrderPreparationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new LocalApiServer(root, log, request =>
        {
            entered.TrySetResult(request);
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("资源门禁队列未释放");
            var allowed = request.IsBusinessInputCurrent?.Invoke() == true;
            if (allowed) Interlocked.Increment(ref effects);
            completed.TrySetResult(allowed);
            var result = new OrderPreparationResult { Ok = allowed, Prepared = allowed };
            result.Automation.Outcome = allowed ? "waiting" : "cancelled";
            result.Automation.Stage = "command";
            result.Automation.ReasonCode = allowed ? "cooking-in-progress" : "business-input-stale";
            return result;
        });
        try
        {
            const string client = "11111111-1111-1111-1111-111111111111";
            var authority = host.Authority.Register(client, "media gate", new CompanionDeviceRegisterRequest {
                ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                Platform = "windows", AppVersion = "isolated", Profile = profile }, DateTime.UtcNow);
            host.SetMediaReadinessForTest(() => Volatile.Read(ref mediaReady) == 1);
            host.Publish(LeaseExpiryTests.Snapshot(), LeaseExpiryTests.Catalog());
            host.Start();
            var deadline = Environment.TickCount64 + 10000;
            while (!J.Bool(host.Status()["isCurrent"]))
            {
                if (Environment.TickCount64 > deadline) throw new TimeoutException("资源就绪预热失败");
                await Task.Delay(10);
            }
            Check(!entered.Task.IsCompleted, "无租约不排队");
            host.GrantLease(client, authority.AuthorityRevision, TimeSpan.FromSeconds(20));
            var request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(request.IsBusinessInputCurrent?.Invoke() == true, "已就绪有效命令具有许可");
            Volatile.Write(ref mediaReady, 0);
            // 不先撤销租约、不先更新输入版本，证明 ready 本身就是实际副作用许可的一部分。
            Check(host.HasLease && request.IsBusinessInputCurrent?.Invoke() == false, "旧租约不能越过资源门禁");
            Check(!J.Bool(host.Status()["isCurrent"]), "未就绪即时撤销业务current");
            host.InvalidateMediaForTest();
            Volatile.Write(ref mediaReady, 1);
            Check(request.IsBusinessInputCurrent?.Invoke() == false, "恢复新资源不能追认旧输入版本命令");
            host.ExpireLease(); release.Set();
            Check(!await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)) && Volatile.Read(ref effects) == 0,
                "主线程队列恢复后旧命令无副作用");
            Console.WriteLine($"PASS game-media-business-gate: {checks} checks");
        }
        finally
        {
            release.Set(); await host.Stop(); Logger.Sources.Remove(log);
            Directory.Delete(root, recursive: true);
        }
    }
}

namespace MystiaStewardCompanion.LocalApi
{
    internal sealed partial class LocalApiServer
    {
        // 测试只注入纯托管状态源；生产由真实 GameMediaService 同一只读门禁赋值。
        internal void SetMediaReadinessForTest(Func<bool> ready) => _gameMediaReady = ready;
        internal void InvalidateMediaForTest() => InvalidateBusinessInput();
    }
}
