using System.Text.Json;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.Save;

namespace MystiaStewardCompanion.LocalApi;

internal sealed partial class LocalApiServer
{
    /// <summary>
    /// 主线程完成本轮等待条新采样及完整队列对账后调用。只修改托管队列，绝不触发制作、送达或
    /// 高亮写入；这些行为继续经过既有经营、参与许可和原生副作用检查。HTTP 控制权转换期间
    /// 非阻塞跳过本轮，避免 Unity 主线程等待一个正在等待命令边界的后台线程。
    /// </summary>
    internal bool TryAutoResumeRareGuestParticipation(long businessGeneration, NightBusinessContext? business)
    {
        var settings = RuntimeAutomationControlState.SnapshotRareGuestAutoResumeSettings();
        if (!settings.Enabled || business is not { OrdersReadComplete: true }
            || !string.IsNullOrWhiteSpace(business.Error) || !Monitor.TryEnter(_authorityTransitionLock)) return false;
        try
        {
            // 重新读取策略，封闭 TryEnter 前发生共享配置切换的窗口。设备在线检查不伪造心跳，
            // 不依赖自动化租约：手动制作用户也可独立使用调度与游戏高亮。
            settings = RuntimeAutomationControlState.SnapshotRareGuestAutoResumeSettings();
            var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
            var gate = RuntimeNightBusinessAutomationGate.Snapshot;
            if (!settings.Enabled || !lifecycle.IsActive || lifecycle.Generation != businessGeneration
                || !gate.Allowed || gate.Generation != businessGeneration || _gameMediaReady?.Invoke() == false
                || !_deviceAuthorityStore.IsCurrentPrimaryOnline(settings.AuthorityRevision, DateTime.UtcNow)) return false;
            var before = RuntimeRareGuestParticipationState.Snapshot;
            if (!before.IsActive || before.BusinessGeneration != businessGeneration
                || !before.ManagedGuestIds.SequenceEqual(RuntimeAutomationControlState.SnapshotManagedRareGuestIds())) return false;
            var observations = business.Orders.Select(order => (
                Identity: new RuntimeRareGuestParticipationOrderIdentity(businessGeneration,
                    order.TraceId, order.OrderLifecycleSequence, order.GuestId ?? -1),
                // 主线程读取器仅在精确原生订单的等待倒计时有效时提供比例。
                // 同时保留本地防线：特殊场景禁止参与或已送齐的订单不可从展示数据获得恢复资格。
                Ratio: !order.AutomationAllowed || (order.HasServedFood && order.HasServedBeverage)
                    ? (double?)null : order.PatienceRemainingRatio)).ToArray();
            var started = RuntimeOrderPreparationService.SnapshotActiveRareAutomationQueueCandidates(
                businessGeneration, includeSuspended: true).Select(candidate => candidate.Identity).ToArray();
            var resumed = RuntimeRareGuestParticipationState.ResumeBelowPatienceThreshold(businessGeneration,
                before.Revision, true, observations, settings.ThresholdPercent, settings.ToFront, started);
            if (resumed.Count == 0) return false;
            // 当前线程尚未返回游戏动作处理循环，先撤销基于旧队列排队的命令，再发布新推荐输入。
            // 并不取消正在制作的料理；已开工任务已在状态机中作为不可抢占锚点保护。
            try
            {
                lock (_automationLeaseLock)
                {
                    _automationCommandEpoch = checked(_automationCommandEpoch + 1);
                    _advanceAutomationCommandEpoch(_automationCommandEpoch);
                }
            }
            catch (Exception ex)
            {
                // 队列已提交就不能再把发布故障冒充 CAS 冲突；仍使旧业务帧失效并返回新状态。
                // 后续原生动作继续独立验证 epoch、当前调度资格和精确订单，不复用旧执行许可。
                _log.LogWarning($"Rare participation auto-resume command invalidation failed: {ex.GetBaseException().Message}");
            }
            InvalidateBusinessInput();
            foreach (var identity in resumed)
            {
                var ratio = observations.Single(value => value.Identity == identity).Ratio;
                _log.LogInfo($"Rare participation auto-resume: order={identity.TraceId}; lifecycle={identity.OrderLifecycleSequence}; "
                    + $"generation={businessGeneration}; remaining={ratio.GetValueOrDefault():P0}; threshold={settings.ThresholdPercent}%; "
                    + $"position={(settings.ToFront ? "front" : "tail")}; revision={before.Revision}->{RuntimeRareGuestParticipationState.RevisionStamp.Revision}.");
            }
            return true;
        }
        catch (Exception ex) when (ex is RuntimeRareGuestParticipationConflictException or ArgumentException or InvalidOperationException)
        {
            // 集合或保护锚点不确定时整批跳过，不消费一次性机会；下次新采样再核对。
            // 不逐帧记录正常冲突，避免未知读取或后台配置切换造成日志洪泛。
            return false;
        }
        finally { Monitor.Exit(_authorityTransitionLock); }
    }

    /// <summary>
    /// 稀客队列唯一写入口。主设备修订、经营轮次和队列修订均作 CAS；不接受客户端计算的
    /// 优先锚点。先撤销排队命令，再处理状态，即便冲突也不能让旧命令重新获得授权。
    /// </summary>
    private LocalApiRareGuestParticipationMutationDto UpdateRareGuestParticipation(string request, HttpRequestData requestData)
    {
        var (clientId, _) = ReadRequiredClientIdentity(request);
        var body = ReadJsonRequest<LocalApiRareGuestParticipationMutationRequest>(requestData,
            "expectedAuthorityRevision", "expectedBusinessGeneration", "expectedParticipationRevision", "action", "target");
        using (var json = JsonDocument.Parse(HttpRequestReader.ReadRequiredJsonBody(requestData)))
            ValidateRareGuestParticipationMutationJson(json.RootElement);
        lock (_authorityTransitionLock)
        {
            if (body.ExpectedAuthorityRevision <= 0
                || !TryAuthorizeRuntimeWriter(request, clientId, DateTime.UtcNow, out var authorityRevision, out _)
                || authorityRevision != body.ExpectedAuthorityRevision)
                throw new CompanionDeviceAuthorityException(409, "主设备或生效配置已经变化，请刷新后重试。");
            if (!RuntimeAutomationControlState.SnapshotRareGuestParticipationModuleEnabled())
                throw new CompanionDeviceAuthorityException(409, "请先启用稀客调度模块。");
            lock (_automationLeaseLock)
            {
                _automationCommandEpoch = checked(_automationCommandEpoch + 1);
                _advanceAutomationCommandEpoch(_automationCommandEpoch);
            }
            var action = body.Action switch
            {
                "pause" => RuntimeRareGuestParticipationAction.Pause,
                "enable-tail" => RuntimeRareGuestParticipationAction.EnableTail,
                "enable-front" => RuntimeRareGuestParticipationAction.EnableFront,
                _ => throw new CompanionDeviceAuthorityException(400, "稀客调度操作无效。"),
            };
            var scope = body.Target.Type == "guest" ? RuntimeRareGuestParticipationTargetScope.Guest
                : RuntimeRareGuestParticipationTargetScope.Order;
            var identities = (scope == RuntimeRareGuestParticipationTargetScope.Guest
                ? body.Target.ExpectedCurrentOrders : new[] { body.Target.Order! }.ToList())
                .Select(ToRareGuestParticipationIdentity).ToArray();
            var guestId = scope == RuntimeRareGuestParticipationTargetScope.Guest ? body.Target.GuestId : identities[0].GuestId;
            var before = RuntimeRareGuestParticipationState.Snapshot;
            try
            {
                // 多订单高亮只是展示，不代表全部都已开工。只保护已取得精确原生绑定的
                // 活动料理任务；暂停任务可能尚未刷新缓存，不得因此成为优先插入的锚点。
                var protectedOrders = new HashSet<RuntimeRareGuestParticipationOrderIdentity>();
                if (action == RuntimeRareGuestParticipationAction.EnableFront)
                {
                    if (!before.IsActive || before.BusinessGeneration != body.ExpectedBusinessGeneration
                        || before.Revision != body.ExpectedParticipationRevision)
                        throw new InvalidOperationException("队列已变化。");
                    var current = before.Orders.ToDictionary(entry => entry.Identity);
                    foreach (var candidate in RuntimeOrderPreparationService.SnapshotActiveRareAutomationQueueCandidates(body.ExpectedBusinessGeneration))
                    {
                        if (!current.TryGetValue(candidate.Identity, out var entry))
                            throw new InvalidOperationException("已开工料理不属于当前完整订单集合。");
                        if (entry.Participating) protectedOrders.Add(candidate.Identity);
                    }
                }
                RuntimeRareGuestParticipationState.MutateParticipation(body.ExpectedBusinessGeneration,
                    body.ExpectedParticipationRevision, action, scope, guestId, identities, protectedOrders);
            }
            catch (Exception ex) when (ex is RuntimeRareGuestParticipationConflictException or InvalidOperationException)
            {
                _log.LogInfo($"Rare participation conflict: {ex.Message}");
                throw new CompanionDeviceAuthorityException(409, "稀客队列或正在处理的订单已变化，请刷新后重试。");
            }
            catch (ArgumentException ex)
            {
                _log.LogInfo($"Rare participation request rejected: {ex.Message}");
                throw new CompanionDeviceAuthorityException(400, "稀客调度请求无效，请刷新订单后重试。");
            }
            var after = RuntimeRareGuestParticipationState.Snapshot;
            if (action == RuntimeRareGuestParticipationAction.Pause)
                foreach (var identity in identities)
                    RuntimeUiPinningService.RemoveRareTargetIfMatches(identity.BusinessGeneration, identity.TraceId,
                        identity.OrderLifecycleSequence, identity.GuestId, "rare order paused");
            InvalidateBusinessInput();
            _log.LogInfo($"Rare participation: action={body.Action}; guest={guestId}; count={identities.Length}; revision={before.Revision}->{after.Revision}.");
            return new LocalApiRareGuestParticipationMutationDto
            {
                Ok = true, Changed = before.Revision != after.Revision,
                Status = action == RuntimeRareGuestParticipationAction.Pause ? "已暂停所选订单。"
                    : action == RuntimeRareGuestParticipationAction.EnableTail ? "已将所选订单排到队尾。" : "已排到当前工作之后。",
                Participation = LocalApiRareGuestParticipationSnapshot.From(after),
            };
        }
    }

    /// <summary>嵌套目标字段严格校验，拒绝多余字段、缺失身份及不完整的客人订单集合。</summary>
    private static void ValidateRareGuestParticipationMutationJson(JsonElement request)
    {
        var action = request.GetProperty("action");
        if (action.ValueKind != JsonValueKind.String
            || action.GetString() is not ("pause" or "enable-tail" or "enable-front"))
        {
            throw new CompanionDeviceAuthorityException(
                400,
                "action 必须严格为 pause、enable-tail 或 enable-front。");
        }

        var target = request.GetProperty("target");
        if (target.ValueKind != JsonValueKind.Object)
        {
            throw new CompanionDeviceAuthorityException(400, "target 必须是 JSON 对象。");
        }
        var targetType = target.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
        JsonElement expectedOrders;
        if (string.Equals(targetType, "guest", StringComparison.Ordinal))
        {
            ValidateExactJsonProperties(target, "type", "guestId", "expectedCurrentOrders");
            expectedOrders = target.GetProperty("expectedCurrentOrders");
            if (expectedOrders.ValueKind != JsonValueKind.Array
                || expectedOrders.GetArrayLength() == 0
                || expectedOrders.GetArrayLength() > RuntimeRareGuestParticipationState.MaximumCurrentOrders)
            {
                throw new CompanionDeviceAuthorityException(
                    400,
                    "guest 类型 target 的 expectedCurrentOrders 必须包含 1 至 512 个当前明确订单标识。");
            }
        }
        else if (string.Equals(targetType, "order", StringComparison.Ordinal))
        {
            ValidateExactJsonProperties(target, "type", "order");
            var order = target.GetProperty("order");
            if (order.ValueKind != JsonValueKind.Object)
            {
                throw new CompanionDeviceAuthorityException(400, "order 类型 target 必须包含一个明确订单标识对象。");
            }
            using var singleOrder = JsonDocument.Parse($"[{order.GetRawText()}]");
            expectedOrders = singleOrder.RootElement.Clone();
        }
        else
        {
            throw new CompanionDeviceAuthorityException(400, "target.type 必须严格为 guest 或 order。");
        }

        var expectedIdentityProperties = new HashSet<string>(
            new[] { "businessGeneration", "traceId", "orderLifecycleSequence", "guestId" },
            StringComparer.Ordinal);
        foreach (var order in expectedOrders.EnumerateArray())
        {
            if (order.ValueKind != JsonValueKind.Object)
            {
                throw new CompanionDeviceAuthorityException(400, "订单标识必须是 JSON 对象。");
            }
            var actual = order.EnumerateObject().Select(property => property.Name).ToArray();
            if (actual.Length != expectedIdentityProperties.Count
                || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length
                || actual.Any(name => !expectedIdentityProperties.Contains(name)))
            {
                throw new CompanionDeviceAuthorityException(400, "订单标识字段与稀客队列请求格式不一致。");
            }
        }
    }

    private static void ValidateExactJsonProperties(JsonElement value, params string[] expectedProperties)
    {
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expectedProperties.Length
            || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length
            || actual.Any(name => !expectedProperties.Contains(name, StringComparer.Ordinal)))
        {
            throw new CompanionDeviceAuthorityException(400, "稀客参与目标字段与当前协议不一致。");
        }
    }

    private static RuntimeRareGuestParticipationOrderIdentity ToRareGuestParticipationIdentity(
        LocalApiRareGuestParticipationIdentityDto order)
    {
        return new RuntimeRareGuestParticipationOrderIdentity(
            order.BusinessGeneration,
            order.TraceId,
            order.OrderLifecycleSequence,
            order.GuestId);
    }

}
