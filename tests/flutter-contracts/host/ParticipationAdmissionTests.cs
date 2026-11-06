using System.Reflection;
using System.Text.Json;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Ui;

/// <summary>
/// 直接调用生产参与状态对齐与主线程准入入口，全部输入是托管快照和精确身份。
/// 不创建Unity对象，也不调用Prepare/Complete；因此这些检查证明等待/许可协议，
/// 不代表实际游戏动作或Harmony注入已经通过。
/// </summary>
internal static class ParticipationAdmissionTests
{
    private static int _assertions;

    internal static int Run()
    {
        _assertions = 0;
        var admission = RequiredProductionMethod("AcquireRareOrderParticipationAdmission");
        var reconcile = RequiredProductionMethod("ReconcileRareGuestParticipationOrders");
        const long generation = 61;
        var lifecycle = new NightBusinessLifecycleSnapshot(generation, 1,
            NightBusinessLifecyclePhase.Active, "synthetic-admission", DateTime.UnixEpoch, 1);
        var first = new RuntimeRareGuestParticipationOrderIdentity(generation, "R-0024", 51, 4);
        var second = new RuntimeRareGuestParticipationOrderIdentity(generation, "R-0025", 52, 4);
        using var enabled = Profile(true, 4);
        using var expandedRoster = Profile(true, 4, 5);
        using var disabled = Profile(false, 4);
        RuntimeRareGuestParticipationState.Reset();
        RuntimeAutomationControlState.Reset("synthetic admission setup");
        try
        {
            RuntimeAutomationControlState.PublishAuthority(enabled.RootElement, 1, "synthetic", "synthetic");
            var revision = RuntimeRareGuestParticipationState.BeginBusiness(generation, new[] { 4 });
            revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(generation, revision, true,
                new[] { Observation(first), Observation(second) });
            // 显式让B排首、A排尾：A完成后的对齐只能删除A，不能把B当成新订单，
            // 重置其人工启用原因、观察序号或排队位置。
            revision = RuntimeRareGuestParticipationState.MutateParticipation(generation, revision,
                RuntimeRareGuestParticipationAction.EnableTail, RuntimeRareGuestParticipationTargetScope.Order,
                4, new[] { second });
            RuntimeRareGuestParticipationState.MutateParticipation(generation, revision,
                RuntimeRareGuestParticipationAction.EnableTail, RuntimeRareGuestParticipationTargetScope.Order,
                4, new[] { first });
            var request = Request(second);
            var original = RuntimeRareGuestParticipationState.Snapshot;
            var preserved = original.Orders.Single(order => order.Identity == second);
            Assert(preserved.Participating && preserved.Managed && preserved.QueuePosition == 1,
                "夹具必须包含人工启用且位于队首的B。");
            AssertAllowed(Admit(Context(true, first, second)), second);

            // 复现日志中的跨帧窗口：当前上下文已经移除A，参与快照还保留{A,B}。
            // 这不是源采集失败；拒绝应等待同步且无副作用，不永久暂停、不耗重试预算。
            var remaining = Context(true, second);
            AssertWaiting(Admit(remaining), "participation-orders-not-aligned", original);
            Reconcile(remaining);
            var synchronized = RuntimeRareGuestParticipationState.Snapshot;
            Assert(synchronized.Orders.Count == 1 && synchronized.Orders[0] == preserved,
                "完整集合对齐应只移除A，完整保留B的精确身份、人工原因、绑定、观察序号及队列位置。");
            Assert(synchronized.Revision > original.Revision, "订单集合变化必须推进参与修订。");
            AssertAllowed(Admit(remaining), second);
            Reconcile(remaining);
            AssertSnapshotUnchanged(synchronized, "发布阶段再次对齐同一完整集合必须幂等。");

            // 部分采集、带错误的完整标记以及null都不能移除保留队列。
            // 连续多轮等待仍保持同一授权状态，不允许靠重试次数把临时读取问题变成永久停单。
            var incompleteContexts = new NightBusinessContext?[]
            {
                Context(false, second), Context(false),
                new() { OrdersReadComplete = true, Error = "synthetic partial read", Orders = remaining.Orders },
                null,
            };
            for (var iteration = 0; iteration < 8; iteration++)
            {
                foreach (var context in incompleteContexts)
                {
                    AssertWaiting(Admit(context), "participation-source-incomplete", synchronized);
                    Reconcile(context);
                    AssertSnapshotUnchanged(synchronized, "不完整采集的生产对齐入口不得删除旧队列。");
                }
            }
            // 完整空集与重复身份也属于集合未对齐，不能误报成原生读取失败。
            AssertWaiting(Admit(Context(true)), "participation-orders-not-aligned", synchronized);
            AssertWaiting(Admit(Context(true, second, second)), "participation-orders-not-aligned", synchronized);
            AssertAllowed(Admit(remaining), second);

            // 配置已经收到新名单、队列尚未同步时只等待；对齐名单后仍保留既有B人工授权。
            RuntimeAutomationControlState.PublishAuthority(expandedRoster.RootElement, 2, "synthetic", "synthetic");
            AssertWaiting(Admit(remaining), "participation-profile-not-aligned", synchronized);
            Reconcile(remaining);
            var rosterSynchronized = RuntimeRareGuestParticipationState.Snapshot;
            Assert(rosterSynchronized.ManagedGuestIds.SequenceEqual(new[] { 4, 5 }), "新名单未同步到参与队列。");
            Assert(rosterSynchronized.Orders.Single() == preserved, "名单扩增误改了已启用的B。");
            AssertAllowed(Admit(remaining), second);

            // 真正的人工暂停必须继续blocked；同trace但不同生命周期也不能借等待恢复取得许可。
            RuntimeRareGuestParticipationState.MutateParticipation(generation, rosterSynchronized.Revision,
                RuntimeRareGuestParticipationAction.Pause, RuntimeRareGuestParticipationTargetScope.Order,
                second.GuestId, new[] { second });
            var paused = RuntimeRareGuestParticipationState.Snapshot;
            AssertBlocked(Admit(remaining), "order-paused", paused);
            Reconcile(remaining);
            AssertSnapshotUnchanged(paused, "完整重读不得重新启用人工暂停的订单。");
            AssertBlocked(Admit(remaining, Request(second with { OrderLifecycleSequence = 999 })),
                "current-order-not-found", paused);
            AssertBlocked(Admit(remaining, Request(second with { TraceId = "invalid" })),
                "invalid-order-identity", paused);

            RuntimeAutomationControlState.PublishAuthority(disabled.RootElement, 3, "synthetic", "synthetic");
            var bypass = Admit(null);
            Assert(bypass.Permit == null && bypass.Blocked == null,
                "调度模块关闭时必须沿用原始准入流程，不能新增等待或暂停。");
            AssertSnapshotUnchanged(paused, "关闭模块的准入旁路不得改动参与队列。");
            VerifyRefreshBeforePublicationContract();
            Console.WriteLine($"PASS: production participation admission {_assertions} assertions; "
                + "transient source/roster/order waits, immediate reconciliation, exact identity, manual pause and lock release; no game execution.");
            return 0;

            (RuntimeRareGuestParticipationPermit? Permit, OrderPreparationResult? Blocked) Admit(
                NightBusinessContext? context, OrderPreparationRequest? overrideRequest = null)
            {
                object?[] arguments = { overrideRequest ?? request, generation, context, null };
                var permit = (RuntimeRareGuestParticipationPermit?)admission.Invoke(null, arguments);
                return (permit, (OrderPreparationResult?)arguments[3]);
            }

            void Reconcile(NightBusinessContext? context) => reconcile.Invoke(null, new object?[] { lifecycle, context });
        }
        finally
        {
            RuntimeRareGuestParticipationState.Reset();
            RuntimeAutomationControlState.Reset("synthetic admission completed");
        }
    }

    private static RuntimeRareGuestParticipationOrderObservation Observation(RuntimeRareGuestParticipationOrderIdentity identity)
        => new(identity, null);

    private static OrderPreparationRequest Request(RuntimeRareGuestParticipationOrderIdentity identity) => new()
    {
        TraceId = identity.TraceId, OrderLifecycleSequence = identity.OrderLifecycleSequence, GuestId = identity.GuestId,
    };

    private static NightBusinessContext Context(bool complete, params RuntimeRareGuestParticipationOrderIdentity[] identities)
        => new()
        {
            OrdersReadComplete = complete,
            Orders = identities.Select(identity => new NightBusinessOrder
            {
                TraceId = identity.TraceId, OrderLifecycleSequence = identity.OrderLifecycleSequence, GuestId = identity.GuestId,
            }).ToList(),
        };

    private static JsonDocument Profile(bool enabled, params int[] managedGuestIds) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        automationEnabled = true, autoRareOrderEnabled = true, autoNormalOrderEnabled = true,
        autoPrepCollectCooking = true, autoPrepCompleteOrder = true, autoNormalDeliverFood = true,
        autoNormalCompleteOrder = true, rareGuestParticipationModuleEnabled = enabled, managedRareGuestIds = managedGuestIds,
    }));

    private static MethodInfo RequiredProductionMethod(string name)
        => typeof(StewardOverlayController).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing production method: {name}.");

    private static void AssertAllowed((RuntimeRareGuestParticipationPermit? Permit, OrderPreparationResult? Blocked) result,
        RuntimeRareGuestParticipationOrderIdentity identity)
    {
        try
        {
            Assert(result.Permit?.Allowed == true && result.Blocked == null, "完整且对齐的已启用订单应取得生产准入许可。");
            Assert(result.Permit!.Decision.Order?.Identity == identity, "准入许可必须属于请求的精确生命周期。");
        }
        finally { result.Permit?.Dispose(); }
        AssertLockReleased();
    }

    private static void AssertWaiting((RuntimeRareGuestParticipationPermit? Permit, OrderPreparationResult? Blocked) result,
        string reason, RuntimeRareGuestParticipationSnapshot before)
    {
        // 即使断言失败也先释放意外取得的许可，避免测试本身阻塞后续清理。
        result.Permit?.Dispose();
        Assert(result.Permit == null && result.Blocked != null, "瞬态未对齐不得获得执行许可。");
        Assert(result.Blocked!.Automation.ReasonCode == reason, $"瞬态拒绝原因错误，应为{reason}。");
        Assert(result.Blocked.Automation.Outcome == "waiting" && result.Blocked.Automation.RetryAfterMs == 500,
            "无副作用的瞬态拒绝必须明确等待500ms，不得返回永久blocked或消耗失败重试。");
        Assert(result.Blocked.Automation.Stage == "participation", "等待响应应指明参与准入阶段。");
        Assert(!result.Blocked.Ok && result.Blocked.Steps.All(step => !step.Ok), "等待响应不得伪装成成功或写入已发生。");
        AssertSnapshotUnchanged(before, "拒绝准入不得删除、重排或重新授权参与队列。");
        AssertLockReleased();
    }

    private static void AssertBlocked((RuntimeRareGuestParticipationPermit? Permit, OrderPreparationResult? Blocked) result,
        string reason, RuntimeRareGuestParticipationSnapshot before)
    {
        result.Permit?.Dispose();
        Assert(result.Permit == null && result.Blocked?.Automation.ReasonCode == reason,
            $"真实暂停或错误身份必须拒绝，预期原因{reason}。");
        Assert(result.Blocked!.Automation.Outcome == "blocked" && result.Blocked.Automation.RetryAfterMs == 0,
            "真实人工暂停或身份冲突不得自动恢复重试。");
        AssertSnapshotUnchanged(before, "永久拒绝不得改动队列。");
        AssertLockReleased();
    }

    private static void AssertSnapshotUnchanged(RuntimeRareGuestParticipationSnapshot before, string message)
    {
        var after = RuntimeRareGuestParticipationState.Snapshot;
        Assert(after.Revision == before.Revision && after.IsActive == before.IsActive
            && after.BusinessGeneration == before.BusinessGeneration
            && after.ManagedGuestIds.SequenceEqual(before.ManagedGuestIds)
            && after.Orders.SequenceEqual(before.Orders), message);
    }

    private static void AssertLockReleased()
        => Assert(Task.Run(() => RuntimeRareGuestParticipationState.Snapshot.Revision).Wait(TimeSpan.FromSeconds(2)),
            "准入拒绝或许可释放后仍持有参与队列锁。");

    /// <summary>
    /// 值回归之外验证生产接线：成功替换经营上下文后即同步参与队列，
    /// 不能再次退回只在受节流的HTTP快照发布时同步。检查精确方法片段，不启动控制器。
    /// </summary>
    private static void VerifyRefreshBeforePublicationContract()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "toolchain.lock.json")))
            directory = directory.Parent;
        // 允许将隔离宿主输出到仓库外的临时目录。此时从显式仓库工作目录寻找源码，
        // 不复制源码到证据目录，也不降低下面的真实生产接线断言。
        if (directory == null)
        {
            directory = new DirectoryInfo(Environment.CurrentDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "toolchain.lock.json")))
                directory = directory.Parent;
        }
        if (directory == null) throw new InvalidOperationException("Cannot find repository for production wiring contract.");
        var source = File.ReadAllText(Path.Combine(directory.FullName, "mods/bepinex/src/Ui/StewardOverlayController.cs"));
        var method = source.IndexOf("private void RefreshBusinessContext(", StringComparison.Ordinal);
        var nextMethod = source.IndexOf("private bool CanReuseNightBusinessContext(", method, StringComparison.Ordinal);
        Assert(method >= 0 && nextMethod > method, "无法定位真实经营读取方法的边界。");
        var refresh = source[method..nextMethod];
        var assigned = refresh.IndexOf("_businessContext = Measure(\"business.rare.total\", provider.LoadContext);", StringComparison.Ordinal);
        var aligned = refresh.IndexOf("ReconcileRareGuestParticipation(", StringComparison.Ordinal);
        Assert(assigned >= 0 && aligned > assigned, "成功读取经营上下文后没有立即同步参与队列。");
        Assert(!refresh[..aligned].Contains("PublishLocalApiSnapshot(", StringComparison.Ordinal),
            "参与对齐不能依赖受节流的HTTP快照发布。");
    }

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
