using System.Text.Json.Nodes;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Application.Automation;

public sealed partial class AutomationCoordinator
{
    // 传输失败或许可切换时失去回执的订单不能靠“清空状态”消除不确定性。
    // 本集合只限制人工重置，不改变既有调度、回执对账和人工确认策略；订单退役或换场才清除。
    private readonly HashSet<string> _resetUncertainOrders = new(StringComparer.Ordinal);

    /// <summary>
    /// 安全重建一笔订单的规划状态。宿主必须传入刚核验仍当前的冻结快照引用；若后台已推进
    /// 到另一帧则拒绝，让用户重新查看。这里不取消料理任务、不调用游戏，也不直接生成命令。
    /// </summary>
    public JsonObject Reset(string kind, string orderKey, string sessionId, long generation,
        JsonObject expectedSnapshot, long nowMs)
    {
        lock (_gate)
        {
            if (nowMs < _lastNow) return J.Object(("ok", false), ("error", "重置请求时间已过期，请重新确认。"));
            if (sessionId != _session || generation != _businessGeneration
                || !ReferenceEquals(expectedSnapshot, _input.Snapshot))
                return J.Object(("ok", false), ("error", "游戏或订单状态已更新，请刷新后重新确认。"));
            var key = StateKey(kind, orderKey);
            var reason = ResetBlockReason(key);
            if (reason.Length > 0) return J.Object(("ok", false), ("error", reason));
            var previous = _states[key];
            var next = AutomationMachine.Empty(kind, orderKey, nowMs);
            // 已消费事件不能因重置重复扣恢复预算；现场送达事实立即同步，避免下轮再次送达。
            next["lastRuntimeEventSequence"] = J.Clone(previous["lastRuntimeEventSequence"]);
            SyncOrderFacts(next, _orders[key], _input.Preferences, nowMs);
            AutomationMachine.Detail(next, "已重置规划状态；下一轮按当前订单重新判断。", nowMs);
            _states[key] = next;
            _lastNow = nowMs;
            _lastRareTick = _lastNormalTick = long.MinValue;
            return J.Object(("ok", true), ("status", "已重置此订单的规划状态。"));
        }
    }

    /// <summary>
    /// 与按钮可用状态及执行入口共用同一拒绝规则。未知集合、重复锅次或未证实的送达都拒绝；
    /// 不能把无回执解释为无副作用，也不能用重置绕过人工确认。调用者必须持协调器锁。
    /// </summary>
    private string ResetBlockReason(string key)
    {
        if (!_authorized || !_runtimeEnabled) return "当前经营状态不允许重置。";
        if (!_currentOrderKeys.Contains(key) || !_states.TryGetValue(key, out var state)
            || !_orders.TryGetValue(key, out var order)) return "该订单已离开当前经营列表。";
        if (J.Bool(state["manualResolutionRequired"])) return "请先处理此订单的人工确认事项。";
        if (_pending.Values.Any(request => request.StateKey == key)
            || _continuations.Any(request => request.StateKey == key)) return "此订单仍有操作正在处理。";
        if (_resetUncertainOrders.Contains(key)) return "此订单曾有未确认回执，不能通过重置清除。";
        if (_input.Snapshot["automationCookingJobs"] is not JsonArray
            || _input.Snapshot["automationEvents"] is not JsonArray) return "料理任务或事件状态尚未完整读取。";
        var kind = J.Str(state["kind"]);
        if (J.Objects(_input.Snapshot["automationCookingJobs"]).Any(job =>
            J.Str(job["targetKind"]) == kind
            && J.Num(job["orderLifecycleSequence"]) == J.Num(order["orderLifecycleSequence"])
            && (J.Str(job["traceId"]) == J.Str(order["traceId"])
                || kind == "normal" && J.Str(job["orderKey"]) == J.Str(order["orderKey"])))
            || J.Str(state["cookingJobId"]).Length > 0)
            return "此订单仍有料理任务，请等待完成或处理人工交接。";
        if (J.Objects(_input.Snapshot["automationEvents"]).Any(e => IsManualEvent(e) && MatchesEvent(e, order, state)))
            return "请先处理此订单的人工确认事项。";
        if (!KnownBoolean(order["hasServedFood"]) || !KnownBoolean(order["hasServedBeverage"])
            || kind == "normal" && !KnownBoolean(order["hasEvaluated"]))
            return "订单送达或评价状态尚未确认。";
        if (J.Bool(state["completed"]) || J.Bool(order["hasEvaluated"])) return "订单已完成，无需重置。";
        if ((J.Bool(state["prepared"]) || J.Bool(state["foodDelivered"])) && !J.Bool(order["hasServedFood"])
            || J.Bool(state["beverageHandled"]) && !J.Bool(order["hasServedBeverage"]))
            return "已有制作或送达记录尚未被现场确认，不能重置。";
        return "";
    }

    private static bool KnownBoolean(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out _);
}
