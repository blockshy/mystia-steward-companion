using System.Text;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;

namespace MystiaStewardCompanion.Ui;

internal sealed partial class StewardOverlayController
{
    private (bool Active, long Generation, long Revision) _lastParticipationStamp;
    private string _lastRareGuestParticipationDiagnosticSignature = "";

    /// <summary>
    /// 仅接受本次发布前在 Unity 主线程刷新过的等待条上下文。调用顺序固定为新采样→完整订单
    /// 对账→阈值策略→快照构造；不得从跨帧缓存或 HTTP 请求触发，未知比例保持 null。
    /// </summary>
    private RuntimeRareGuestParticipationSnapshot TryAutoResumeRareGuestParticipationFromFreshSnapshot(
        NightBusinessLifecycleSnapshot lifecycle, NightBusinessContext? freshBusiness,
        RuntimeRareGuestParticipationSnapshot participation)
    {
        if (Thread.CurrentThread.ManagedThreadId != _mainThreadId || !lifecycle.IsActive
            || _localApiServer == null) return participation;
        return _localApiServer.TryAutoResumeRareGuestParticipation(lifecycle.Generation, freshBusiness)
            ? RuntimeRareGuestParticipationState.Snapshot : participation;
    }

    /// <summary>调度变更从后台提交；主线程只检测版本并发布快照，不扫描游戏对象或重算推荐。</summary>
    private void ProcessRareGuestParticipationStateChange()
    {
        var stamp = RuntimeRareGuestParticipationState.RevisionStamp;
        if (stamp == _lastParticipationStamp) return;
        _lastParticipationStamp = stamp;
        MarkLocalApiSnapshotDirty(LocalApiSnapshotDirtyDomain.RareBusiness | LocalApiSnapshotDirtyDomain.Automation,
            "rare guest participation changed", force: true);
    }

    /// <summary>经营轮次与原生生命周期共用；不得用场景名或桌位推断新经营。</summary>
    private static void EnsureRareGuestParticipationBusiness(NightBusinessLifecycleSnapshot lifecycle)
    {
        var stamp = RuntimeRareGuestParticipationState.RevisionStamp;
        if (stamp.Active && stamp.Generation == lifecycle.Generation) return;
        RuntimeRareGuestParticipationState.BeginBusiness(lifecycle.Generation,
            RuntimeAutomationControlState.SnapshotManagedRareGuestIds());
    }

    /// <summary>只有完整采集可结束消失订单；读取失败和 CAS 冲突保留已确认状态，并由执行许可拒绝歧义写入。</summary>
    private RuntimeRareGuestParticipationSnapshot ReconcileRareGuestParticipation(
        NightBusinessLifecycleSnapshot lifecycle,
        NightBusinessContext? nightBusiness)
    {
        if (!lifecycle.IsActive || lifecycle.Generation <= 0)
        {
            return RuntimeRareGuestParticipationState.Snapshot;
        }

        try
        {
            ReconcileRareGuestParticipationOrders(lifecycle, nightBusiness);
            _lastRareGuestParticipationDiagnosticSignature = "";
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or OverflowException
                                   or RuntimeRareGuestParticipationConflictException)
        {
            var diagnosticSignature = $"{lifecycle.Generation}:{ex.GetType().Name}:{ex.Message}";
            if (!string.Equals(
                    diagnosticSignature,
                    _lastRareGuestParticipationDiagnosticSignature,
                    StringComparison.Ordinal))
            {
                _lastRareGuestParticipationDiagnosticSignature = diagnosticSignature;
                _log?.LogWarning(
                    $"Rare-guest participation projection retained its previous complete state: {ex.Message}");
            }
        }

        return RuntimeRareGuestParticipationState.Snapshot;
    }

    /// <summary>
    /// 将已读取的纯托管订单集合与调度状态同步，不额外访问Unity或触发任何订单动作。
    /// 读取成功后立即调用，快照发布时再幂等复核；不能让HTTP发布节流延迟执行准入的身份集合。
    /// 部分读取保留旧队列，完整结果仍经过精确身份、代次和CAS验证；异常交由实例入口限频记录。
    /// </summary>
    private static RuntimeRareGuestParticipationSnapshot ReconcileRareGuestParticipationOrders(
        NightBusinessLifecycleSnapshot lifecycle, NightBusinessContext? nightBusiness)
    {
        if (!lifecycle.IsActive || lifecycle.Generation <= 0)
            return RuntimeRareGuestParticipationState.Snapshot;
        EnsureRareGuestParticipationBusiness(lifecycle);
        var state = RuntimeRareGuestParticipationState.Snapshot;
        var managedGuestIds = RuntimeAutomationControlState.SnapshotManagedRareGuestIds();
        if (!state.ManagedGuestIds.SequenceEqual(managedGuestIds))
        {
            RuntimeRareGuestParticipationState.ReplaceManagedGuestIds(lifecycle.Generation, state.Revision, managedGuestIds);
            state = RuntimeRareGuestParticipationState.Snapshot;
        }
        var complete = nightBusiness is { OrdersReadComplete: true } && string.IsNullOrWhiteSpace(nightBusiness.Error);
        var observations = complete ? nightBusiness!.Orders.Select(order =>
            new RuntimeRareGuestParticipationOrderObservation(new RuntimeRareGuestParticipationOrderIdentity(
                lifecycle.Generation, order.TraceId, order.OrderLifecycleSequence, order.GuestId ?? -1), Binding: null)) : null;
        RuntimeRareGuestParticipationState.ReconcileCurrentOrders(lifecycle.Generation, state.Revision, complete, observations);
        return RuntimeRareGuestParticipationState.Snapshot;
    }


    /// <summary>持有精确订单许可覆盖同步游戏动作；暂停等待动作结束，不退款或中断正在制作的料理。</summary>
    private static RuntimeRareGuestParticipationPermit? AcquireRareOrderParticipationAdmission(
        OrderPreparationRequest request,
        long businessGeneration,
        NightBusinessContext? currentBusiness,
        out OrderPreparationResult? blocked)
    {
        blocked = null;
        var managedGuestIds = RuntimeAutomationControlState.SnapshotManagedRareGuestIds();
        if (managedGuestIds.Count == 0) return null;

        var identity = new RuntimeRareGuestParticipationOrderIdentity(
            businessGeneration,
            request.TraceId,
            request.OrderLifecycleSequence,
            request.GuestId ?? -1);
        var permit = RuntimeRareGuestParticipationState.AcquireAdmissionPermit(identity);
        var participation = RuntimeRareGuestParticipationState.Snapshot;
        var configuredAsManaged = request.GuestId.HasValue
            && managedGuestIds.Contains(request.GuestId.Value);
        var rostersAligned = participation.IsActive
            && participation.BusinessGeneration == businessGeneration
            && participation.ManagedGuestIds.Count == managedGuestIds.Count
            && managedGuestIds.All(participation.ManagedGuestIds.Contains);
        var configurationAligned = rostersAligned
            && permit.Decision.Order != null
            && permit.Decision.Order.Managed == configuredAsManaged;
        // 队列在部分采集时故意保留，保留身份不等于当前仍可准入。此时已持有调度许可锁，
        // 主线程使用当前采集集合复核完整性；HTTP手动请求与迟到命令也不能绕过业务投影。
        var collectionIssue = ReadParticipationCollectionBlockReason(businessGeneration, currentBusiness, participation);
        if (permit.Allowed && configurationAligned && collectionIssue.Length == 0) return permit;

        var reasonCode = permit.Allowed
            ? collectionIssue.Length > 0 ? collectionIssue : "participation-profile-not-aligned"
            : permit.Decision.ReasonCode;
        // 只有此处在调用Prepare/Complete之前明确拒绝的三种同步问题可自动等待。
        // 不能把身份冲突、手动暂停或运行时已经可能发生副作用的blocked结果改成重试。
        var transient = permit.Allowed && reasonCode is ("participation-source-incomplete"
            or "participation-profile-not-aligned" or "participation-orders-not-aligned");
        var message = reasonCode switch
        {
            "participation-source-incomplete" => "稀客订单读取暂未完成，等待同步后自动继续；本次未修改游戏状态。",
            "participation-orders-not-aligned" => "稀客订单与调度队列正在同步，稍后自动继续；本次未修改游戏状态。",
            "participation-profile-not-aligned" => "稀客调度名单正在同步，稍后自动继续；本次未修改游戏状态。",
            "order-paused" => "该稀客订单当前已暂停，不参与高亮、自动化或资源预约。",
            _ => $"暂时无法确认该稀客订单是否已启用，未修改游戏状态：{permit.Decision.Message}",
        };
        permit.Dispose();
        blocked = BuildUnavailableOrderResult(request, message, reasonCode);
        blocked.Automation.Outcome = transient ? "waiting" : "blocked";
        blocked.Automation.Stage = "participation";
        blocked.Automation.ReasonCode = reasonCode;
        blocked.Automation.RetryAfterMs = transient ? 500 : 0;
        if (blocked.Steps.Count > 0)
        {
            var step = blocked.Steps[0];
            blocked.Steps[0] = new OrderPreparationStep
            {
                Code = step.Code,
                Name = "稀客参与检查",
                Ok = step.Ok,
                Skipped = step.Skipped,
                Message = step.Message,
            };
        }
        return null;
    }

    /// <summary>完整采集失败时仍保留管理信息，但不给服务页发布可参与的展示身份。</summary>
    private static bool IsParticipationProjectionComplete(NightBusinessLifecycleSnapshot lifecycle,
        NightBusinessContext? business, RuntimeRareGuestParticipationSnapshot state)
    {
        return lifecycle.IsActive && IsParticipationCollectionComplete(lifecycle.Generation, business, state);
    }

    /// <summary>
    /// 展示投影和主线程准入共用完整集合规则。未知读取只保留旧队列供恢复，不让已保留身份
    /// 自动成为本轮执行资格；在准入路径中，调用方持有调度锁直到动作结束或拒绝。
    /// </summary>
    private static bool IsParticipationCollectionComplete(long businessGeneration,
        NightBusinessContext? business, RuntimeRareGuestParticipationSnapshot state)
        => ReadParticipationCollectionBlockReason(businessGeneration, business, state).Length == 0;

    /// <summary>
    /// 分开标识源读取缺失、配置同步和完整集合版本错位。三者均拒绝写入，但诊断不能把
    /// “上一单完成后等待队列对账”误报为原生读取故障；完整身份集合要求仍保持不变。
    /// </summary>
    private static string ReadParticipationCollectionBlockReason(long businessGeneration,
        NightBusinessContext? business, RuntimeRareGuestParticipationSnapshot state)
    {
        if (business is not { OrdersReadComplete: true } || !string.IsNullOrWhiteSpace(business.Error))
            return "participation-source-incomplete";
        if (!state.IsActive || state.BusinessGeneration != businessGeneration
            || !state.ManagedGuestIds.SequenceEqual(RuntimeAutomationControlState.SnapshotManagedRareGuestIds()))
            return "participation-profile-not-aligned";
        var identities = business.Orders.Select(order => new RuntimeRareGuestParticipationOrderIdentity(
            businessGeneration, order.TraceId, order.OrderLifecycleSequence, order.GuestId ?? -1)).ToArray();
        return identities.Length == state.Orders.Count && identities.Distinct().Count() == identities.Length
            && identities.ToHashSet().SetEquals(state.Orders.Select(order => order.Identity))
            ? "" : "participation-orders-not-aligned";
    }
    private static void AppendRareGuestParticipation(
        StringBuilder builder,
        LocalApiRareGuestParticipationSnapshot participation)
    {
        AppendValue(builder, participation.Active);
        AppendValue(builder, participation.Ready);
        AppendValue(builder, participation.BusinessGeneration);
        AppendValue(builder, participation.ParticipationRevision);
        foreach (var guestId in participation.ManagedGuestIds) AppendValue(builder, guestId);
        foreach (var entry in participation.Entries)
        {
            AppendValue(builder, entry.TraceId);
            AppendValue(builder, entry.OrderLifecycleSequence);
            AppendValue(builder, entry.GuestId);
            AppendValue(builder, entry.Managed);
            AppendValue(builder, entry.Participating);
            AppendValue(builder, entry.ReasonCode);
            AppendValue(builder, entry.QueuePosition);
        }
    }

}
