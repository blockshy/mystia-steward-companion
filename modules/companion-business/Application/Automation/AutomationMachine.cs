using System.Text.Json.Nodes;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Application.Automation;

/// <summary>
/// 自动化订单的纯状态转换规则。所有时间都由宿主传入；本类型不访问时钟、网络或游戏对象。
/// 与原客户端 automation-machine / automation-state 的结构化结果语义保持一致，中文消息只用于展示。
/// </summary>
public static class AutomationMachine
{
    private static readonly HashSet<string> ManualCodes = new(StringComparer.Ordinal)
    {
        "beverage-delivery-commit-uncertain", "food-delivery-commit-uncertain", "cooking-start-unowned",
        "cooking-progress-stalled", "cooking-progress-regressed", "cooking-result-unreadable",
        "cooking-tags-unreadable-stored", "cooking-delivery-blocked", "cooking-delivery-commit-uncertain",
        "cooking-warmer-commit-uncertain", "cooking-warmer-reset-blocked", "cooking-delivery-timeout",
        "cooking-warmer-reset-failed", "cooking-job-exception", "cooking-manual-handoff-unreadable",
        "cooking-delivery-cleanup-blocked", "cooking-delivery-cleanup-failed", "order-evaluation-commit-uncertain",
        "order-evaluation-target-mismatch", "order-evaluation-closeout-unresolved", "mizuchi-contract-mismatch",
    };
    private static readonly HashSet<string> RecoverableCodes = new(StringComparer.Ordinal)
    {
        "cooking-ownership-lost", "cooking-controller-reused", "cooking-mismatch-stored", "cooking-target-unavailable-stored",
    };
    private static readonly HashSet<string> RequestStages = new(StringComparer.Ordinal)
    {
        "match-order", "ensure-beverage", "ensure-cooking", "deliver-food", "complete-order",
    };

    /// <summary>创建一笔订单的完整状态，不使用缺省字段代表未初始化的安全状态。</summary>
    public static JsonObject Empty(string kind, string orderKey, long nowMs) => J.Object(
        ("kind", kind), ("orderKey", orderKey), ("recipeTarget", null), ("recipeTargetSignature", ""),
        ("recipeTargetRevision", 0), ("beverageTarget", null), ("executionTarget", null),
        ("executionTargetBusinessGeneration", 0), ("prepared", false), ("cookingJobId", ""),
        ("beverageHandled", false), ("beverageHandledAtMs", 0), ("foodDelivered", false),
        ("foodDeliveredAtMs", 0), ("completed", false), ("completedAtMs", 0),
        ("step", kind == "normal" ? "match-order" : "idle"), ("stepStartedAtMs", nowMs),
        ("lastProgressAtMs", nowMs), ("retryCount", 0), ("retryStage", ""), ("rollbackCount", 0),
        ("rollbackTargetSignature", ""), ("rollbackTargetRevision", 0), ("nextAttemptAtMs", 0),
        ("lastError", ""), ("detailMessage", ""), ("detailUpdatedAtMs", 0), ("paused", false),
        ("manualResolutionRequired", false), ("pausedStage", ""), ("pauseReasonCode", ""),
        ("lastRuntimeEventSequence", 0));

    /// <summary>只有明确的副作用不确定代码才要求人工确认，不能解析本地化消息猜测。</summary>
    public static bool RequiresManual(string reasonCode, IEnumerable<string>? stepCodes = null) =>
        ManualCodes.Contains(reasonCode) || (stepCodes?.Any(ManualCodes.Contains) ?? false);

    /// <summary>运行时明确标记为可恢复的中断才消耗回退额度；普通等待不算失败。</summary>
    public static bool IsRecoverable(JsonObject e) => J.Bool(e["terminal"]) && J.Str(e["outcome"]) == "interrupted"
        && (RecoverableCodes.Contains(J.Str(e["code"])) || RecoverableCodes.Contains(J.Str(e["reasonCode"])));

    /// <summary>将游戏阶段映射到业务阶段，显式运行时阶段优先于请求前的推断。</summary>
    public static string ResponseStage(string stage, string fallback) => stage.Trim().ToLowerInvariant() switch
    {
        "beverage" => "ensure-beverage", "cooking-start" => "ensure-cooking", "cooking-delivery" => "deliver-food",
        "order" => "complete-order", "validation" => "match-order", _ => fallback,
    };

    /// <summary>按原顺序选择合并请求的首个处理阶段。</summary>
    public static string RequestStage(bool beverage, bool cooking, bool delivery, bool completion, string fallback = "match-order") =>
        beverage ? "ensure-beverage" : cooking ? "ensure-cooking" : delivery ? "deliver-food" : completion ? "complete-order" : fallback;

    /// <summary>保留同阶段开始时间，阶段改变时只使用宿主传入的时间。</summary>
    public static void SetStep(JsonObject state, string step, long nowMs)
    {
        if (J.Str(state["step"]) != step || J.Num(state["stepStartedAtMs"]) <= 0) state["stepStartedAtMs"] = nowMs;
        state["step"] = step;
    }

    /// <summary>记录等待原因而不重置失败计数、回退预算或人工屏障。</summary>
    public static void Waiting(JsonObject state, string step, string message, long nowMs)
    {
        if (J.Bool(state["paused"])) return;
        SetStep(state, step, nowMs);
        state["lastError"] = message;
        Detail(state, message, nowMs);
    }

    /// <summary>只在展示详情变化时推进详情时间，避免每次轮询导致界面无意义刷新。</summary>
    public static void Detail(JsonObject state, string message, long nowMs)
    {
        if (message.Length == 0 || J.Str(state["detailMessage"]) == message) return;
        state["detailMessage"] = message;
        state["detailUpdatedAtMs"] = nowMs;
    }

    /// <summary>消费一份经过请求轮次校验的结构化响应；调用方负责保证旧响应不能进入本函数。</summary>
    public static void ApplyOutcome(JsonObject state, JsonObject response, string requestedStage, long nowMs, bool stopOnError, int maxRetries)
    {
        var automation = J.Obj(response["automation"]);
        var outcome = J.Str(automation["outcome"]);
        var stage = ResponseStage(J.Str(automation["stage"]), RequestStages.Contains(requestedStage) ? requestedStage : "match-order");
        var manual = J.Bool(state["manualResolutionRequired"]) || RequiresManual(J.Str(automation["reasonCode"]), J.Objects(response["steps"]).Select(x => J.Str(x["code"])));
        if (manual)
        {
            if (!J.Bool(state["manualResolutionRequired"]))
            {
                state["pausedStage"] = stage;
                state["pauseReasonCode"] = J.Str(automation["reasonCode"]);
                state["stepStartedAtMs"] = nowMs;
            }
            state["manualResolutionRequired"] = true;
            state["prepared"] = J.Bool(state["prepared"]) || stage is "ensure-cooking" or "deliver-food";
            if (J.Str(automation["jobId"]).Length > 0) state["cookingJobId"] = J.Str(automation["jobId"]);
            state["step"] = "paused";
            state["paused"] = true;
            ClearRetries(state);
            var message = FailureMessage(response);
            if (message != "未知状态") state["lastError"] = message;
            return;
        }

        var stageChanged = J.Str(state["retryStage"]).Length > 0 && J.Str(state["retryStage"]) != stage;
        var retries = stageChanged ? 0 : (int)J.Num(state["retryCount"]);
        var progressed = outcome is "progressed" or "completed";
        if (outcome == "retryable-failure") retries++;
        else if (progressed) retries = 0;
        var pause = outcome is "blocked" or "fatal" || outcome == "retryable-failure" && stopOnError && retries >= maxRetries;
        state["retryCount"] = retries;
        state["retryStage"] = progressed ? "" : outcome is "retryable-failure" or "interrupted" ? stage : stageChanged ? "" : J.Str(state["retryStage"]);
        var nextAttempt = stageChanged ? 0 : J.Num(state["nextAttemptAtMs"]);
        if (outcome is "retryable-failure" or "interrupted" || outcome == "waiting" && J.Num(automation["retryAfterMs"]) > 0)
            nextAttempt = nowMs + Math.Max(250, J.Num(automation["retryAfterMs"]));
        else if (progressed) nextAttempt = 0;
        state["nextAttemptAtMs"] = nextAttempt;
        if (progressed) { state["lastProgressAtMs"] = nowMs; state["lastError"] = ""; }
        if (outcome is "retryable-failure" or "blocked" or "fatal") state["lastError"] = FailureMessage(response);
        if (pause)
        {
            state["paused"] = true;
            state["pausedStage"] = stage;
            state["pauseReasonCode"] = J.Str(automation["reasonCode"]);
        }
        SetStep(state, J.Bool(state["paused"]) ? "paused" : stage, nowMs);
    }

    /// <summary>传输错误只推进当前阶段的有限重试；不得自动解除已有人工屏障。</summary>
    public static void TransportFailure(JsonObject state, string stage, string message, long nowMs, bool stopOnError, int maxRetries)
    {
        if (J.Bool(state["manualResolutionRequired"])) return;
        var count = (J.Str(state["retryStage"]) == stage ? J.Num(state["retryCount"]) : 0) + 1;
        state["retryCount"] = count;
        state["retryStage"] = stage;
        state["nextAttemptAtMs"] = nowMs + 1000;
        state["lastError"] = message;
        if (stopOnError && count >= maxRetries)
        {
            state["paused"] = true;
            state["pausedStage"] = stage;
            state["pauseReasonCode"] = "transport-failure";
        }
        SetStep(state, J.Bool(state["paused"]) ? "paused" : stage, nowMs);
    }

    /// <summary>回退达到上限时暂停；0 次回退不会被错误解释为达到 0 上限。</summary>
    public static void EnforceRollbackLimit(JsonObject state, int maximum, long nowMs)
    {
        var count = J.Num(state["rollbackCount"]);
        if (J.Bool(state["paused"]) || count <= 0 || count < maximum) return;
        state["paused"] = true;
        state["pausedStage"] = J.Str(state["step"]);
        state["pauseReasonCode"] = "rollback-limit-reached";
        SetStep(state, "paused", nowMs);
        state["lastError"] = $"{J.Str(state["lastError"])} 自动回退已达到上限 {count}/{maximum}，已暂停该订单。".Trim();
    }

    /// <summary>非空特殊目标身份真正轮换才重开回退额度；人工确认暂停不因目标轮换被解除。</summary>
    public static bool ReconcileTarget(JsonObject state, string signature, long revision, long nowMs)
    {
        if (signature.Length == 0) return false;
        var previous = J.Str(state["rollbackTargetSignature"]);
        var rotated = previous.Length > 0 && (previous != signature || J.Num(state["rollbackTargetRevision"]) != revision);
        state["rollbackTargetSignature"] = signature;
        state["rollbackTargetRevision"] = revision;
        if (!rotated) return false;
        state["rollbackCount"] = 0;
        if (J.Bool(state["paused"]) && !J.Bool(state["manualResolutionRequired"]) && J.Str(state["pauseReasonCode"]) == "rollback-limit-reached")
        {
            ClearPause(state);
            ClearRetries(state);
            state["lastError"] = "";
            SetStep(state, "ensure-cooking", nowMs);
        }
        return true;
    }

    /// <summary>普通重试永远不能确认不确定副作用；只有达到回退上限的暂停才重开回退预算。</summary>
    public static bool Retry(JsonObject state, long nowMs)
    {
        if (!J.Bool(state["paused"]) || J.Bool(state["manualResolutionRequired"])) return false;
        var reset = J.Str(state["pauseReasonCode"]) == "rollback-limit-reached";
        var pausedStage = J.Str(state["pausedStage"]);
        var step = RequestStages.Contains(pausedStage) ? pausedStage : J.Bool(state["prepared"]) || J.Bool(state["beverageHandled"]) ? "complete-order" : "match-order";
        if (reset) state["rollbackCount"] = 0;
        ClearPause(state);
        ClearRetries(state);
        state["lastError"] = reset ? "已手动重试，自动回退计数已重新开放。" : "已手动重试，等待下一轮自动化继续。";
        SetStep(state, step, nowMs);
        return true;
    }

    /// <summary>已关闭阶段的普通失败可以退役；人工屏障始终保留。</summary>
    public static void RetireDisabledFailure(JsonObject state, bool beverage, bool cooking, bool delivery, bool completion, JsonObject order, long nowMs)
    {
        if (J.Bool(state["manualResolutionRequired"])) return;
        var enabled = new HashSet<string>(StringComparer.Ordinal) { "idle", "match-order", "done" };
        if (beverage) enabled.Add("ensure-beverage");
        if (cooking) enabled.Add("ensure-cooking");
        if (delivery) enabled.Add("deliver-food");
        if (completion) enabled.Add("complete-order");
        var clearRetry = J.Str(state["retryStage"]).Length > 0 && !enabled.Contains(J.Str(state["retryStage"]));
        var clearPause = J.Bool(state["paused"]) && J.Str(state["pausedStage"]).Length > 0 && !enabled.Contains(J.Str(state["pausedStage"]));
        if (!clearRetry && !clearPause) return;
        ClearRetries(state);
        state["lastError"] = "";
        if (!clearPause) return;
        ClearPause(state);
        SetStep(state, RequestStage(beverage && !J.Bool(state["beverageHandled"]) && !J.Bool(order["hasServedBeverage"]),
            cooking && !J.Bool(state["prepared"]) && !J.Bool(order["hasServedFood"]),
            delivery && J.Bool(state["prepared"]) && !J.Bool(order["hasServedFood"]),
            completion && (J.Str(state["kind"]) == "rare" || J.Bool(order["readyToEvaluate"]) ||
                (J.Bool(state["foodDelivered"]) || J.Bool(order["hasServedFood"])) && (J.Bool(state["beverageHandled"]) || J.Bool(order["hasServedBeverage"]))), "idle"), nowMs);
    }

    /// <summary>快照证明已跨过暂停阶段时，仅解除普通失败；人工确认状态必须等待 ACK。</summary>
    public static bool SnapshotPassesStage(string stage, bool food, bool beverage, bool ready, bool completed) => completed || stage switch
    {
        "ensure-beverage" => beverage || ready,
        "ensure-cooking" or "deliver-food" => food || ready,
        "complete-order" => completed,
        "match-order" or "idle" => food || beverage || ready,
        _ => false,
    };

    /// <summary>清除可恢复暂停标记，不修改人工屏障字段。</summary>
    internal static void ClearPause(JsonObject state)
    {
        state["paused"] = false;
        state["pausedStage"] = "";
        state["pauseReasonCode"] = "";
    }

    /// <summary>只清理阶段重试预算；回退额度具有独立生命周期。</summary>
    internal static void ClearRetries(JsonObject state)
    {
        state["retryCount"] = 0;
        state["retryStage"] = "";
        state["nextAttemptAtMs"] = 0;
    }

    /// <summary>从结构化步骤生成显示错误；此消息不参与任何决策。</summary>
    internal static string FailureMessage(JsonObject response)
    {
        var failed = J.Objects(response["steps"]).FirstOrDefault(x => !J.Bool(x["ok"]) && !J.Bool(x["skipped"]));
        return failed is null ? J.Str(response["error"], "未知状态") : $"{J.Str(failed["name"])}: {J.Str(failed["message"])}";
    }
}
