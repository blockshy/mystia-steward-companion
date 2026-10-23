using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Support;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Application.Automation;

public sealed partial class AutomationCoordinator
{
    /// <summary>
    /// 为低频聚合日志提供有界调度事实。最多记录 16 个当前订单与 32 个可用厨具槽，
    /// 不复制目录、候选、配方或完整状态，不访问 Unity，也不运行规划/改变重试时钟。
    /// 宿主仅在已启用日志的五秒窗口末调用；本结果不是执行许可，不能送回业务输入。
    /// </summary>
    public JsonObject SchedulingDiagnostics()
    {
        lock (_gate)
        {
            var runtime = _input.Snapshot["recommendationState"];
            var pool = Cookers.BuildAutomationCookerPool(runtime);
            // 保留“看起来有锅但无法准入”的原始标量，区分严格空闲、关闭、锁定或未读全；
            // 不记录原生指针，不在诊断时再次读取控制器。类型集合也限制长度。
            var observed = J.Objects(runtime?["placedCookers"]).Take(32).Select(cooker => J.Object(
                ("index", cooker["controllerIndex"]), ("typeIds", J.Array(J.Numbers(cooker["typeIds"]).Take(8))),
                ("available", J.Bool(cooker["automationAvailable"])), ("couldOpen", J.Bool(cooker["couldOpen"])),
                ("locked", J.Bool(cooker["challengeLocked"])),
                ("availability", DiagnosticText(cooker["automationAvailability"], 48))));
            var slots = J.Objects(pool["slots"]).Take(32).Select(slot => J.Object(
                ("index", slot["controllerIndex"]), ("types", slot["supportedKeys"]),
                ("reservedThisPeriod", _usedCookers.Contains((int)J.Num(slot["controllerIndex"], -1)))));
            var orders = _currentOrderKeys.OrderBy(key => key, StringComparer.Ordinal).Take(16)
                .Where(key => _states.ContainsKey(key) && _orders.ContainsKey(key))
                .Select(key =>
                {
                    var state = _states[key];
                    var order = _orders[key];
                    return J.Object(("kind", state["kind"]), ("traceId", DiagnosticText(order["traceId"], 64)),
                        ("table", order["deskCode"]), ("foodId", order["foodId"]),
                        ("recipeId", state["recipeTarget"]?["recipeId"] ?? state["executionTarget"]?["recipeId"]),
                        ("step", DiagnosticText(state["step"], 48)), ("prepared", J.Bool(state["prepared"])),
                        ("foodServed", J.Bool(order["hasServedFood"])), ("beverageServed", J.Bool(order["hasServedBeverage"])),
                        ("beverageHandled", J.Bool(state["beverageHandled"])), ("completed", J.Bool(state["completed"])),
                        ("paused", J.Bool(state["paused"])), ("manualRequired", J.Bool(state["manualResolutionRequired"])),
                        // 使用结构化预算字段，避免有界错误文本截断后看不到真正的暂停原因。
                        ("rollbackCount", J.Num(state["rollbackCount"])), ("retryCount", J.Num(state["retryCount"])),
                        ("pauseReasonCode", DiagnosticText(state["pauseReasonCode"], 64)),
                        ("jobId", DiagnosticText(state["cookingJobId"], 64)),
                        ("pending", _pending.Values.Any(pending => pending.StateKey == key)),
                        ("retryInMs", Math.Max(0, J.Num(state["nextAttemptAtMs"]) - _lastNow)),
                        ("detail", DiagnosticText(state["detailMessage"], 180)),
                        ("lastError", DiagnosticText(state["lastError"], 180)));
                });
            return J.Object(("authorized", _authorized), ("runtimeEnabled", _runtimeEnabled),
                ("stopped", _stopped), ("currentOrders", _currentOrderKeys.Count),
                ("pendingCommands", _pending.Count), ("continuations", _continuations.Count),
                ("recommendationsCurrent", _input.Recommendations.IsCurrent),
                ("recommendationsPending", _input.Recommendations.Pending),
                ("cookerSnapshotComplete", J.Bool(runtime?["placedCookerSnapshotComplete"])),
                ("cookerReadFailures", J.Num(runtime?["placedCookerReadFailureCount"])),
                ("cookerControllerCount", J.Num(runtime?["placedCookerControllerCount"])),
                ("observedCookers", J.Array(observed)), ("availableSlots", J.Array(slots)), ("orders", J.Array(orders)),
                ("omittedOrders", Math.Max(0, _currentOrderKeys.Count - 16)));
        }
    }

    /// <summary>限制诊断文本长度；去除换行，避免错误消息破坏聚合日志的行边界。</summary>
    private static string DiagnosticText(JsonNode? value, int limit)
    {
        var text = J.Str(value).Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= limit ? text : text[..limit];
    }
}
