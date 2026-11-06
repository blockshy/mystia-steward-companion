using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Updates;

/// <summary>
/// 连接生产共享配置存储、控制状态和 API 队列入口的合成回归。只临时替换纯托管经营快照；
/// 不挂原生 Hook、不启动 HTTP、不读取真实存档。所有游戏动作委托一律抛错并计数。
/// </summary>
internal static class RarePatienceAutoResumeTests
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        const long generation = 7201;
        const string client = "patience-primary-0001";
        var first = new RuntimeRareGuestParticipationOrderIdentity(generation, "R-0001", 1001, 30);
        var second = first with { TraceId = "R-0002", OrderLifecycleSequence = 1002 };
        var path = Path.Combine(Path.GetTempPath(), "mystia-patience-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var log = Logger.CreateLogSource("patience-policy-synthetic");
        // BepInEx 插值日志会在没有接收器时直接跳过格式化；安装内存空接收器才能核对日志契约。
        // 不添加控制台/磁盘监听器，也不改变生产日志开关。
        using var listener = new SyntheticLogListener(); Logger.Listeners.Add(listener);
        var logs = new List<string>(); log.LogEvent += (_, entry) => logs.Add(entry.Data?.ToString() ?? "");
        var lifecycleField = StaticField(typeof(RuntimeNightBusinessLifecycle), "_publishedSnapshot");
        var gateField = StaticField(typeof(RuntimeNightBusinessAutomationGate), "_snapshot");
        var originalLifecycle = lifecycleField.GetValue(null);
        var originalGate = gateField.GetValue(null);
        var effects = 0; var epochChanges = 0; var throwAdvance = false;
        Exception Forbidden() { effects++; return new InvalidOperationException("本测试禁止游戏及外部副作用。"); }
        try
        {
            var profile = GameFocusOverlayContractTests.CreateSchedulingTestProfile();
            profile["rareGuestParticipationModuleEnabled"] = true;
            profile["managedRareGuestIds"] = new JsonArray(30);
            var store = new CompanionDeviceAuthorityStore(Path.Combine(path, "devices.json"), log);
            var authority = store.Register(client, "合成主设备", Registration(), DateTime.UtcNow);
            CompanionDeviceRegisterRequest Registration() => new() { ProtocolVersion = 1,
                ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion, Platform = "windows", AppVersion = "test",
                Profile = JsonSerializer.SerializeToElement(profile) };
            using var update = new UpdateService(new UpdateServiceSettings { Enabled = false, AutoCheck = false }, log,
                Path.Combine(path, "updates"), () => DateTime.UtcNow, _ => throw Forbidden());
            using var server = new LocalApiServer(false, "auto", 19878, "patience-test", "synthetic-patience-token",
                () => throw Forbidden(), (_, _) => throw Forbidden(), _ => throw Forbidden(),
                () => throw Forbidden(), _ => throw Forbidden(), _ => throw Forbidden(),
                (_, _, _, _, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(),
                _ => throw Forbidden(), _ => throw Forbidden(), _ => throw Forbidden(), 1,
                _ => { epochChanges++; if (throwAdvance) throw new InvalidOperationException("synthetic epoch failure"); return 0; },
                _ => throw Forbidden(), () => throw Forbidden(),
                () => RuntimeAvailableMissionSnapshot.Unavailable(0, 0, RuntimeAvailableMissionSnapshot.NotAttachedStatus, "synthetic"),
                (_, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(), update,
                new FavoriteStore(Path.Combine(path, "favorites.json"), log), new CustomRecipeStore(Path.Combine(path, "custom.json"), log), store, log);
            var active = new NightBusinessLifecycleSnapshot(generation, 1, NightBusinessLifecyclePhase.Active,
                "synthetic-patience-policy", DateTime.UnixEpoch, Environment.CurrentManagedThreadId);
            var allowed = new RuntimeNightBusinessAutomationGateSnapshot(true, "", "synthetic ready", generation, 1, false, true);
            lifecycleField.SetValue(null, active); gateField.SetValue(null, allowed);
            void Publish() => RuntimeAutomationControlState.PublishAuthority(authority.ActiveProfile, authority.AuthorityRevision, "synthetic", "合成主配置");
            void Start()
            {
                RuntimeRareGuestParticipationState.Reset();
                var revision = RuntimeRareGuestParticipationState.BeginBusiness(generation, new[] { 30 });
                RuntimeRareGuestParticipationState.ReconcileCurrentOrders(generation, revision, true,
                    new[] { first, second }.Select(identity => new RuntimeRareGuestParticipationOrderObservation(identity, null)));
            }
            NightBusinessContext Context(double? ratio = .1, bool complete = true, bool served = false, bool permitted = true) => new()
            {
                OrdersReadComplete = complete,
                Orders = new[] { first, second }.Select(identity => new NightBusinessOrder { TraceId = identity.TraceId,
                    GuestId = identity.GuestId, OrderLifecycleSequence = identity.OrderLifecycleSequence,
                    PatienceRemainingRatio = ratio, HasServedFood = served, HasServedBeverage = served, AutomationAllowed = permitted }).ToList(),
            };
            bool Try(NightBusinessContext? context = null) => server.TryAutoResumeRareGuestParticipation(generation, context ?? Context());
            void SetEnabled(bool enabled)
            {
                profile["rareGuestParticipationAutoResumeEnabled"] = enabled;
                authority = store.UpdatePrimaryProfile(client, new() { ProtocolVersion = 1,
                    ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                    ExpectedAuthorityRevision = authority.AuthorityRevision, ExpectedProfileRevision = authority.ActiveProfileRevision,
                    Profile = JsonSerializer.SerializeToElement(profile) }, DateTime.UtcNow);
                Publish();
            }
            Publish(); Start(); Check(!Try(), "默认关闭不得恢复。");
            SetEnabled(true);
            Check(!Try(Context(complete: false)), "不完整源不恢复。");
            Check(!Try(Context(ratio: null)), "未知比例不恢复。");
            Check(!Try(Context(ratio: .3)), "严格低于阈值，等于不恢复。");
            Check(!Try(Context(served: true)), "已送齐不能恢复。");
            Check(!Try(Context(permitted: false)), "特殊经营禁止参与时不恢复。");
            var mediaField = InstanceField(server, "_gameMediaReady");
            mediaField.SetValue(server, (Func<bool>)(() => false)); Check(!Try(), "资源未就绪不能恢复。");
            mediaField.SetValue(server, null);
            gateField.SetValue(null, allowed with { Allowed = false }); Check(!Try(), "教程或经营门槛阻断时不能恢复。");
            gateField.SetValue(null, allowed);
            lifecycleField.SetValue(null, active with { Generation = generation + 1 }); Check(!Try(), "旧经营代次不能恢复。");
            lifecycleField.SetValue(null, active);
            RuntimeAutomationControlState.PublishAuthority(authority.ActiveProfile, authority.AuthorityRevision + 1, "synthetic", "未确认配置");
            Check(!Try(), "尚未与共享存储对齐的权威版本不能恢复。"); Publish();
            var seen = (Dictionary<string, DateTime>)InstanceField(store, "_lastSeenUtc").GetValue(store)!;
            seen[client] = DateTime.UtcNow.AddMinutes(-2); var offline = seen[client];
            Check(!Try() && seen[client] == offline, "主设备离线时不恢复且不能伪造心跳。");
            authority = store.Register(client, "合成主设备", Registration(), DateTime.UtcNow); Publish();
            var seenBefore = seen[client];
            CheckDeferred(InstanceField(server, "_authorityTransitionLock").GetValue(server)!, "权威转换忙碌");
            CheckDeferred(InstanceField(store, "_lock").GetValue(store)!, "配置存储忙碌");
            void CheckDeferred(object gate, string reason)
            {
                using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                var holder = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); } });
                Check(entered.Wait(TimeSpan.FromSeconds(2)), "未建立锁争用夹具。");
                try { var clock = Stopwatch.StartNew(); Check(!Try(), reason + "应跳过本轮。"); Check(clock.ElapsedMilliseconds < 1000, reason + "阻塞了主线程。"); }
                finally { release.Set(); holder.GetAwaiter().GetResult(); }
            }
            var inputField = InstanceField(server, "_businessInputVersion");
            var beforeInput = (long)inputField.GetValue(server)!;
            Check(Try(), "无执行lease且所有制作开关关闭时，有效独立调度仍应恢复。");
            Check(epochChanges == 1 && (long)inputField.GetValue(server)! > beforeInput, "一次批量恢复必须撤销旧排队命令并失效业务输入。");
            Check(seen[client] == seenBefore, "自动策略不能延长客户端在线时间。");
            Check(RuntimeRareGuestParticipationState.Snapshot.Orders.All(entry => entry.Participating), "同客两单未完整恢复。");
            Check(logs.Count(line => line.StartsWith("Rare participation auto-resume: order=")) == 2, "每个实际恢复订单必须有一条简洁诊断。");
            Check(!Try(), "同一现场重复采样不能重复提交。");
            RuntimeRareGuestParticipationState.MutateParticipation(generation, RuntimeRareGuestParticipationState.Snapshot.Revision,
                RuntimeRareGuestParticipationAction.Pause, RuntimeRareGuestParticipationTargetScope.Order, first.GuestId, new[] { first });
            Check(!Try(), "用户再次暂停必须持续有效。");
            Start(); throwAdvance = true; beforeInput = (long)inputField.GetValue(server)!;
            Check(Try() && (long)inputField.GetValue(server)! > beforeInput, "提交后epoch回调失败也必须发布队列及失效旧输入。");
            Check(logs.Any(line => line.Contains("command invalidation failed")), "提交后的发布故障不能被误报为未提交的静默冲突。");
            Start(); SetEnabled(false); Check(!Try(), "关闭当前共享策略后不可沿用旧主配置。");
            Check(effects == 0, "策略测试触发了原生或外部操作。");
            Console.WriteLine($"PASS: rare patience auto-resume production boundary {checks} assertions; no game effects.");
            return 0;
        }
        finally
        {
            lifecycleField.SetValue(null, originalLifecycle); gateField.SetValue(null, originalGate);
            RuntimeAutomationControlState.Reset("合成测试结束"); RuntimeRareGuestParticipationState.Reset();
            Logger.Listeners.Remove(listener); Logger.Sources.Remove(log);
        }
    }

    private static FieldInfo StaticField(Type type, string name) => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("缺少明确的托管测试边界：" + name);
    private static FieldInfo InstanceField(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("缺少明确的托管测试边界：" + name);

    private sealed class SyntheticLogListener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;
        public void LogEvent(object sender, LogEventArgs eventArgs) { }
        public void Dispose() { }
    }
}

internal static partial class GameFocusOverlayContractTests
{
    /// <summary>仅复用合成的完整协议配置夹具；不复用专注运行逻辑，不读取用户配置。</summary>
    internal static JsonObject CreateSchedulingTestProfile() => FocusTestProfile();
}
