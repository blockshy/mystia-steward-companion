using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.Contracts;
using MystiaStewardCompanion.LocalApi;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 运行真实后台业务宿主与领域计算，仅人工推进页面截止时间。证明撤销、乱序和失联
/// 不会留下持续计算；没有真实游戏对象、网络端口、用户文件或时间等待型 TTL 测试。
/// </summary>
internal static class PageSubscriptionTests
{
    internal static async Task Run(JsonElement profile)
    {
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        void Rejected(Action action, string message)
        {
            try { action(); } catch (CompanionDeviceAuthorityException error) when (error.StatusCode == 409) { checks++; return; }
            throw new InvalidOperationException(message);
        }
        async Task Until(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition()) { if (watch.ElapsedMilliseconds > 10000) throw new TimeoutException("页面订阅宿主未完成。"); await Task.Delay(20); }
        }
        var root = Path.GetFullPath(Path.Combine("temp", "page-subscriptions-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var log = Logger.CreateLogSource("page-subscriptions-offline");
        var calls = 0;
        var host = new LocalApiServer(root, log, _ => { calls++; return new OrderPreparationResult { Ok = false }; });
        const string client = "11111111-1111-1111-1111-111111111111";
        const string other = "22222222-2222-2222-2222-222222222222";
        try
        {
            host.Authority.Register(client, "页面订阅测试", new CompanionDeviceRegisterRequest
            { ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                Platform = "windows", AppVersion = "test", Profile = profile }, DateTime.UtcNow);
            var snapshot = LeaseExpiryTests.Snapshot();
            host.Publish(snapshot, LeaseExpiryTests.Catalog()); host.Start();
            await Until(() => J.Bool(host.Status()["isCurrent"]));
            var intent = J.Object(("protocolVersion", 1), ("kind", "normal"), ("selectedPlace", "妖怪兽道"));
            var first = host.OpenPage(client); var second = host.OpenPage(other);
            Check(host.QueryCount == 0, "打开空会话不应触发推荐计算。");
            var a = 1L; var b = 1L;
            host.UpdatePage(client, first, a++, intent); host.UpdatePage(other, second, b++, intent);
            await Until(() => J.Bool(host.UpdatePage(client, first, a++, intent)["isCurrent"])
                && J.Bool(host.UpdatePage(other, second, b++, intent)["isCurrent"]));
            Check(host.QueryCount == 2, "两个设备应各自拥有独立可见订阅。");
            host.UpdatePage(client, first, a++, null);
            Check(host.QueryCount == 1, "撤销本设备不能清除另一设备页面。");
            Rejected(() => host.UpdatePage(client, first, a - 2, intent), "迟到更新越过撤销序号重新登记。");
            Rejected(() => host.UpdatePage(client, second, b++, null), "一个设备撤销了另一个设备随机会话。");
            Check(host.QueryCount == 1, "拒绝跨身份操作后其它设备订阅应仍存在。");
            host.ExpirePageForTest(second);
            Check(host.QueryCount == 0, "失联意图到期后仍保留计算。");
            var sequence = host.PageEvaluationSequence;
            snapshot["snapshotSignature"] = "after-cancel-and-expiry";
            host.Publish(snapshot, LeaseExpiryTests.Catalog());
            await Until(() => J.Bool(host.Status()["isCurrent"]));
            Check(host.PageEvaluationSequence == sequence, "新业务帧仍计算已经撤销或到期的页面。");
            Check(calls == 0 && !host.HasLease, "页面会话隐式获取自动化许可或调用游戏动作。");
            host.UpdatePage(other, second, b++, intent);
            Check(host.QueryCount == 1, "未过期的空槽不能由新的显式可见请求恢复。");
            host.ExpirePageForTest(second, entireSession: true);
            Rejected(() => host.UpdatePage(other, second, b++, intent), "过期会话由更新隐式复活。");
            Check(host.QueryCount == 0, "失效会话拒绝后重新留下了页面意图。");

            // 精确字段校验覆盖重复、缺少、未来协议与非法意图；不能默认为一次有效续期。
            var valid = J.Object(("protocolVersion", 1), ("sessionId", first), ("sequence", 1), ("intent", intent));
            foreach (var raw in new[] { "null", "[]", "{}", valid.ToJsonString().Replace("\"sequence\":1", "\"sequence\":0"),
                valid.ToJsonString().Replace("\"sequence\":1", "\"sequence\":1,\"sequence\":1"),
                valid.ToJsonString().Replace("\"intent\":", "\"unknown\":"),
                valid.ToJsonString().Replace("\"protocolVersion\":1", "\"protocolVersion\":2") })
            {
                var rejected = false;
                try { BusinessProtocol.ParsePageSessionUpdate(raw); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException) { rejected = true; }
                Check(rejected, "损坏页面信封未拒绝：" + raw);
            }
            Console.WriteLine($"PAGE-SUBSCRIPTIONS PASS checks={checks}; live-game=false");
        }
        finally
        {
            await host.Stop(); Logger.Sources.Remove(log);
            var parent = Path.GetFullPath("temp") + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试目录越界。");
            Directory.Delete(root, true);
        }
    }
}
