using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Application.Automation;

public sealed partial class AutomationCoordinator
{
    /// <summary>
    /// 从完整游戏快照对账订单、锅次和终态事件。缺失推荐不会删除已经准入的料理任务或人工屏障。
    /// 当前订单的强身份出现重复时整轮拒绝准入，不能依赖字典后写覆盖选择对象。
    /// </summary>
    private void IndexAndReconcileOrders(JsonObject snapshot, JsonObject preferences, long nowMs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        _currentOrderKeys.Clear();
        var jobs = J.Objects(snapshot["automationCookingJobs"]).ToArray();
        var special = snapshot["specialBusiness"];
        var specialObject = J.Obj(special);
        var rejectionScope = string.Join("|", J.Str(specialObject["challengeType"]), J.Str(specialObject["phase"]),
            string.Join(",", SpecialBusinessRules.NormalizeTags(specialObject["foodTargetTags"]).OrderBy(x => x, StringComparer.Ordinal)));
        if (_rejectionScope != rejectionScope) { _rejectionScope = rejectionScope; _rejectedRecipes.Clear(); }
        foreach (var kind in new[] { "normal", "rare" })
        {
            var collection = J.Obj(snapshot[kind == "normal" ? "normalBusiness" : "nightBusiness"])["orders"];
            foreach (var order in J.Objects(collection))
            {
                var orderKey = OrderKey(kind, order);
                if (orderKey.Length == 0) continue;
                var key = StateKey(kind, orderKey);
                if (!seen.Add(key))
                {
                    _runtimeEnabled = false;
                    _message = "当前快照中存在重复订单实例，自动化已停止本轮准入。";
                    InvalidateRequests();
                    return;
                }
                _orders[key] = J.Obj(J.Clone(order));
                _currentOrderKeys.Add(key);
                if (!_states.TryGetValue(key, out var state))
                {
                    if (_states.Count >= MaximumTrackedOrders)
                    {
                        _runtimeEnabled = false;
                        _message = "自动化订单状态达到容量上限；保留安全屏障并停止新增任务。";
                        InvalidateRequests();
                        return;
                    }
                    state = AutomationMachine.Empty(kind, orderKey, nowMs);
                    _states[key] = state;
                }
                SyncOrderFacts(state, order, preferences, nowMs);
                var job = FindJob(state, order, jobs);
                if (job is not null && !J.Bool(order["hasServedFood"])) ReconcileJob(state, job, nowMs);
                else if (J.Str(state["detailMessage"]).StartsWith("自动化阶段暂停\n", StringComparison.Ordinal))
                {
                    state["detailMessage"] = "";
                    state["detailUpdatedAtMs"] = nowMs;
                }
                var policy = SpecialBusinessRules.BuildWirePolicy(special, J.Str(order["specialBusinessRole"]), _businessGeneration);
                // 稀客目标与其推荐候选在 PlanRare 中一起更新，避免提前消耗“目标已轮换”事实而误加一次回退。
                if (kind == "normal") AutomationMachine.ReconcileTarget(state, J.Str(policy["specialTargetSignature"]), (long)J.Num(policy["specialTargetRevision"]), nowMs);
                var force = IsFullFeed(special, order);
                var prefix = kind == "normal" ? "autoNormal" : "autoPrep";
                AutomationMachine.RetireDisabledFailure(state, force || J.Bool(preferences[prefix + "TakeBeverage"]),
                    force || J.Bool(preferences[prefix + "StartCooking"]),
                    force || J.Bool(preferences[kind == "normal" ? "autoNormalDeliverFood" : "autoPrepCollectCooking"]),
                    force || J.Bool(preferences[prefix + "CompleteOrder"]), order, nowMs);
                AutomationMachine.EnforceRollbackLimit(state, MaximumRollbacks(preferences), nowMs);
            }
        }
        ReconcileEvents(snapshot, preferences, nowMs);
        foreach (var (key, state) in _states.ToArray())
        {
            var activeJob = J.Str(state["cookingJobId"]).Length > 0 && jobs.Any(x => J.Str(x["jobId"]) == J.Str(state["cookingJobId"]));
            if (!seen.Contains(key) && !activeJob && !J.Bool(state["manualResolutionRequired"]) && !_pending.Values.Any(x => x.StateKey == key))
            {
                _states.Remove(key);
                _orders.Remove(key);
            }
        }
    }

    /// <summary>快照中的已送达事实可推进阶段，但不能把人工屏障误当作普通失败清除。</summary>
    private static void SyncOrderFacts(JsonObject state, JsonObject order, JsonObject preferences, long nowMs)
    {
        var normal = J.Str(state["kind"]) == "normal";
        var food = J.Bool(order["hasServedFood"]);
        var beverage = J.Bool(order["hasServedBeverage"]);
        var ready = normal && J.Bool(order["readyToEvaluate"]);
        var completed = normal && J.Bool(order["hasEvaluated"]);
        if (!food && !beverage && !ready && !completed) return;
        var oldPrepared = J.Bool(state["prepared"]);
        var oldFood = J.Bool(state["foodDelivered"]);
        var oldBeverage = J.Bool(state["beverageHandled"]);
        var oldCompleted = J.Bool(state["completed"]);
        var oldStep = J.Str(state["step"]);
        var madeProgress = food && (!oldPrepared || J.Str(state["cookingJobId"]).Length > 0) || beverage && !oldBeverage;
        state["prepared"] = oldPrepared || food;
        state["foodDelivered"] = oldFood || food;
        state["beverageHandled"] = oldBeverage || beverage;
        state["completed"] = oldCompleted || completed;
        if (food && J.Num(state["foodDeliveredAtMs"]) <= 0) state["foodDeliveredAtMs"] = nowMs;
        if (beverage && J.Num(state["beverageHandledAtMs"]) <= 0) state["beverageHandledAtMs"] = nowMs;
        if (completed && J.Num(state["completedAtMs"]) <= 0) state["completedAtMs"] = nowMs;
        if (food && !J.Bool(state["manualResolutionRequired"])) state["cookingJobId"] = "";
        if (completed) ClearExecutionTarget(state);
        var clearsPause = J.Bool(state["paused"]) && !J.Bool(state["manualResolutionRequired"])
            && AutomationMachine.SnapshotPassesStage(J.Str(state["pausedStage"]), food || oldFood, beverage || oldBeverage, ready, completed || oldCompleted);
        var nextStep = oldStep;
        if (normal)
        {
            if (J.Bool(state["completed"])) nextStep = "done";
            else if (ready && J.Bool(preferences["autoNormalCompleteOrder"])) nextStep = "complete-order";
            else if (J.Bool(state["foodDelivered"]) && !J.Bool(state["beverageHandled"]) && J.Bool(preferences["autoNormalTakeBeverage"])) nextStep = "ensure-beverage";
            else if (J.Bool(state["beverageHandled"]) && !J.Bool(state["foodDelivered"])) nextStep = "ensure-cooking";
            else if (J.Bool(state["prepared"]) && !J.Bool(state["foodDelivered"])) nextStep = "deliver-food";
            madeProgress = J.Bool(state["prepared"]) != oldPrepared || J.Bool(state["foodDelivered"]) != oldFood
                || J.Bool(state["beverageHandled"]) != oldBeverage || J.Bool(state["completed"]) != oldCompleted || nextStep != oldStep;
        }
        else if (madeProgress) nextStep = food && beverage ? "complete-order" : food ? "ensure-beverage" : "ensure-cooking";
        if (J.Bool(state["paused"]) && !clearsPause) nextStep = "paused";
        if (madeProgress)
        {
            state["lastProgressAtMs"] = nowMs;
            if (!J.Bool(state["paused"]) || clearsPause) AutomationMachine.ClearRetries(state);
        }
        if (clearsPause) { AutomationMachine.ClearPause(state); state["lastError"] = ""; }
        AutomationMachine.SetStep(state, nextStep, nowMs);
    }

    /// <summary>仅接受精确订单实例绑定的活动料理任务；不以姓名、桌号或配方推测绑定。</summary>
    private static JsonObject? FindJob(JsonObject state, JsonObject order, IEnumerable<JsonObject> jobs)
    {
        var kind = J.Str(state["kind"]);
        var matching = jobs.Where(job => J.Str(job["targetKind"]) == kind
            && J.Num(job["orderLifecycleSequence"]) > 0 && J.Num(job["orderLifecycleSequence"]) == J.Num(order["orderLifecycleSequence"])
            && (J.Str(state["cookingJobId"]).Length > 0 ? J.Str(state["cookingJobId"]) == J.Str(job["jobId"])
                : J.Str(order["traceId"]).Length > 0 ? J.Str(job["traceId"]) == J.Str(order["traceId"])
                : kind == "normal" && J.Str(order["orderKey"]).Length > 0 && J.Str(job["orderKey"]) == J.Str(order["orderKey"]))).Take(2).ToArray();
        return matching.Length == 1 ? matching[0] : null;
    }

    private static void ReconcileJob(JsonObject state, JsonObject job, long nowMs)
    {
        state["prepared"] = true;
        if (J.Bool(state["manualResolutionRequired"])) return;
        var changed = J.Str(state["cookingJobId"]) != J.Str(job["jobId"]);
        var progress = changed && (!J.Bool(state["paused"]) || J.Str(state["pausedStage"]) == "ensure-cooking");
        state["cookingJobId"] = J.Str(job["jobId"]);
        if (!J.Bool(state["paused"]) || progress)
            AutomationMachine.SetStep(state, J.Str(job["transactionStage"]) == "evaluation-receipt" || J.Str(job["controlStage"]) == "OrderEvaluation" ? "complete-order" : "deliver-food", nowMs);
        if (changed) state["lastProgressAtMs"] = nowMs;
        if (progress) { AutomationMachine.ClearPause(state); AutomationMachine.ClearRetries(state); state["lastError"] = ""; }
        if (J.Str(job["controlState"]) != "active")
            AutomationMachine.Detail(state, "自动化阶段暂停\n" + J.Str(job["controlMessage"], "当前生效配置或自动化控制权尚未就绪。"), nowMs);
    }

    private static bool IsManualEvent(JsonObject e) => J.Bool(e["terminal"]) && J.Str(e["outcome"]) == "blocked"
        && AutomationMachine.RequiresManual(J.Str(e["reasonCode"]), new[] { J.Str(e["code"]) });

    /// <summary>事件必须按序列对账；人工屏障建立后，普通迟到事件不能推进其序列或解除暂停。</summary>
    private void ReconcileEvents(JsonObject snapshot, JsonObject preferences, long nowMs)
    {
        // 缺失事件字段表示输入不完整，不能推断所有屏障已被其他设备确认。
        if (snapshot["automationEvents"] is not JsonArray) return;
        var events = J.Objects(snapshot["automationEvents"]).OrderBy(x => J.Num(x["sequence"])).ToArray();
        var unresolved = events.Where(IsManualEvent).Select(x => J.Num(x["sequence"])).ToHashSet();
        foreach (var (key, state) in _states.ToArray())
            if (J.Bool(state["manualResolutionRequired"]) && J.Num(state["lastRuntimeEventSequence"]) > 0 && !unresolved.Contains(J.Num(state["lastRuntimeEventSequence"])))
                _states[key] = ResetAfterAcknowledgement(state, nowMs, "安全栅栏已由其他控制窗口确认，等待下一轮重新判断。");

        foreach (var e in events)
        {
            var manual = IsManualEvent(e);
            var blocking = manual || J.Str(e["code"]) == "cooking-tags-unreadable-stored"
                || J.Bool(e["terminal"]) && J.Str(e["outcome"]) is "blocked" or "fatal";
            var recoverable = AutomationMachine.IsRecoverable(e);
            if (!blocking && !recoverable) continue;
            var kind = J.Str(e["targetKind"]);
            var matching = _states.Where(pair => J.Str(pair.Value["kind"]) == kind
                && _orders.TryGetValue(pair.Key, out var order) && MatchesEvent(e, order, pair.Value)).Take(2).ToArray();
            if (matching.Length != 1) continue;
            var state = matching[0].Value;
            var sequence = J.Num(e["sequence"]);
            if (sequence <= J.Num(state["lastRuntimeEventSequence"])) continue;
            if (J.Bool(state["manualResolutionRequired"]))
            {
                if (manual) state["lastRuntimeEventSequence"] = sequence;
                continue;
            }
            if (blocking)
            {
                var code = J.Str(e["code"]);
                var previousStep = J.Str(state["step"]);
                var eventStage = code == "mizuchi-contract-mismatch" || code.StartsWith("order-", StringComparison.Ordinal) ? "complete-order"
                    : code.StartsWith("beverage-", StringComparison.Ordinal) ? "ensure-beverage" : code == "cooking-start-unowned" ? "ensure-cooking" : "deliver-food";
                if (manual && (code.StartsWith("cooking-", StringComparison.Ordinal) || J.Str(e["reasonCode"]).StartsWith("cooking-", StringComparison.Ordinal))) state["prepared"] = true;
                state["cookingJobId"] = manual ? J.Str(e["jobId"], J.Str(state["cookingJobId"])) : "";
                state["paused"] = true;
                state["manualResolutionRequired"] = manual;
                state["pausedStage"] = manual ? eventStage : previousStep;
                state["pauseReasonCode"] = J.Str(e["reasonCode"], code);
                if (kind == "normal") { state["foodDelivered"] = false; state["foodDeliveredAtMs"] = 0; state["completed"] = false; state["completedAtMs"] = 0; }
                AutomationMachine.SetStep(state, "paused", nowMs);
                AutomationMachine.ClearRetries(state);
                state["lastError"] = J.Str(e["message"], "运行时无法安全确认自动化副作用，已暂停该订单。");
            }
            else
            {
                var deferred = J.Str(e["reasonCode"]) == "cooking-target-changed-stored";
                state["prepared"] = false;
                state["cookingJobId"] = "";
                state["foodDelivered"] = false;
                state["foodDeliveredAtMs"] = 0;
                state["completed"] = false;
                state["completedAtMs"] = 0;
                if (kind == "normal") ClearExecutionTarget(state);
                AutomationMachine.ClearPause(state);
                AutomationMachine.ClearRetries(state);
                state["rollbackCount"] = J.Num(state["rollbackCount"]) + (deferred ? 0 : 1);
                state["nextAttemptAtMs"] = nowMs + 500;
                state["lastError"] = J.Str(e["message"], "料理任务已中断，依据订单事实重新调度。")
                    + (deferred ? " 已保留旧目标回退预算，等待新的非空目标身份。" : "");
                AutomationMachine.SetStep(state, "ensure-cooking", nowMs);
                AutomationMachine.EnforceRollbackLimit(state, MaximumRollbacks(preferences), nowMs);
                RememberRejectedRecipe(snapshot, e);
            }
            state["lastRuntimeEventSequence"] = sequence;
            _lastRareTick = _lastNormalTick = long.MinValue;
        }
    }

    private static bool MatchesEvent(JsonObject e, JsonObject order, JsonObject state)
    {
        if (J.Num(e["orderLifecycleSequence"]) <= 0 || J.Num(e["orderLifecycleSequence"]) != J.Num(order["orderLifecycleSequence"])) return false;
        if (J.Str(e["jobId"]).Length > 0 && J.Str(state["cookingJobId"]).Length > 0 && J.Str(e["jobId"]) != J.Str(state["cookingJobId"])) return false;
        if (J.Str(order["traceId"]).Length > 0) return J.Str(e["traceId"]) == J.Str(order["traceId"]);
        return J.Str(state["kind"]) == "normal" && J.Str(order["orderKey"]).Length > 0 && J.Str(e["orderKey"]) == J.Str(order["orderKey"]);
    }

    private void RememberRejectedRecipe(JsonObject snapshot, JsonObject e)
    {
        var special = J.Obj(snapshot["specialBusiness"]);
        if (!J.Bool(special["active"]) || J.Str(special["challengeType"]) != SpecialBusinessRules.WackyChallenge
            || J.Str(e["code"]) != "cooking-mismatch-stored" || J.Arr(e["targetFoodTags"]).Count == 0 || J.Arr(e["actualFoodTags"]).Count == 0
            || SpecialBusinessRules.MatchesTags(e["actualFoodTags"], e["targetFoodTags"], "any")) return;
        var key = SpecialBusinessRules.RejectedRecipeKey(e["targetFoodTags"], J.Num(e["foodId"], -1), J.Num(e["recipeId"], -1), J.Numbers(e["extraIngredientIds"]));
        if (key.Length == 0 || _rejectedRecipes.Contains(key, StringComparer.Ordinal)) return;
        _rejectedRecipes.Add(key);
        if (_rejectedRecipes.Count > MaximumRejectedRecipes) _rejectedRecipes.RemoveAt(0);
    }

    /// <summary>响应中的提交事实和阶段结果分开归并，避免把 HTTP 成功或 waiting 当作已开锅。</summary>
    private static void ApplyOrderResponse(JsonObject state, PendingCommand request, JsonObject response, long nowMs)
    {
        var automation = J.Obj(response["automation"]);
        var outcome = J.Str(automation["outcome"]);
        var stage = AutomationMachine.ResponseStage(J.Str(automation["stage"]), request.Stage);
        var mismatch = J.Objects(response["steps"]).Any(x => J.Str(x["code"]) == "cooking-mismatch-stored");
        var pending = J.Str(automation["stage"]) == "cooking-delivery" && outcome == "waiting";
        var started = DidStep(response, "cooking-started", "");
        var beverage = DidStep(response, "beverage-delivered", "servedBeverage");
        var food = DidStep(response, "food-delivered", "servedFood");
        var completed = DidStep(response, "order-completed", "completedOrder");
        var normal = J.Str(state["kind"]) == "normal";
        var interrupted = outcome is "interrupted" or "blocked" or "fatal";
        var manualCooking = AutomationMachine.RequiresManual(J.Str(automation["reasonCode"]), J.Objects(response["steps"]).Select(x => J.Str(x["code"])))
            && stage is "ensure-cooking" or "deliver-food";
        if (beverage && !J.Bool(state["beverageHandled"])) state["beverageHandledAtMs"] = nowMs;
        if (food && !J.Bool(state["foodDelivered"])) state["foodDeliveredAtMs"] = nowMs;
        if (completed && !J.Bool(state["completed"])) state["completedAtMs"] = nowMs;
        state["beverageHandled"] = J.Bool(state["beverageHandled"]) || J.Bool(request.Order["hasServedBeverage"]) || beverage;
        state["foodDelivered"] = !mismatch && (J.Bool(state["foodDelivered"]) || J.Bool(request.Order["hasServedFood"]) || food);
        state["completed"] = !mismatch && (J.Bool(state["completed"]) || J.Bool(request.Order["hasEvaluated"]) || completed);
        state["prepared"] = manualCooking || !mismatch && !interrupted && (J.Bool(state["prepared"]) || started || pending
            || normal && stage == "ensure-cooking" && outcome == "progressed" || food || J.Bool(request.Order["hasServedFood"]));
        state["cookingJobId"] = J.Bool(state["prepared"]) ? J.Str(automation["jobId"]).Length > 0 ? J.Str(automation["jobId"]) : J.Str(state["cookingJobId"]) : "";
        if (mismatch) { state["foodDeliveredAtMs"] = 0; state["completedAtMs"] = 0; if (normal) ClearExecutionTarget(state); }
        var nextStep = J.Bool(state["completed"]) ? "done"
            : J.Bool(request.Payload["autoTakeBeverage"]) && !J.Bool(state["beverageHandled"]) ? "ensure-beverage"
            : J.Bool(state["foodDelivered"]) || J.Bool(request.Order["readyToEvaluate"]) ? "complete-order"
            : J.Bool(state["prepared"]) ? "deliver-food" : "ensure-cooking";
        AutomationMachine.ApplyOutcome(state, response, outcome is "retryable-failure" or "interrupted" or "blocked" or "fatal" ? request.Stage : nextStep,
            nowMs, request.StopOnError, request.MaximumRetries);
        if (J.Bool(state["completed"]) && !J.Bool(state["manualResolutionRequired"]))
        {
            AutomationMachine.ClearPause(state);
            AutomationMachine.ClearRetries(state);
            AutomationMachine.SetStep(state, "done", nowMs);
            ClearExecutionTarget(state);
        }
        AutomationMachine.EnforceRollbackLimit(state, request.MaximumRollbacks, nowMs);
        AutomationMachine.Detail(state, string.Join("\n", J.Objects(response["steps"]).Select(x => J.Str(x["message"])).Where(x => x.Length > 0)), nowMs);
    }

    private static bool DidStep(JsonObject response, string code, string fact) => fact.Length > 0 && J.Bool(response[fact])
        || J.Objects(response["steps"]).Any(x => J.Str(x["code"]) == code && J.Bool(x["ok"]) && !J.Bool(x["skipped"]));
    private static void ClearExecutionTarget(JsonObject state) { state["executionTarget"] = null; state["executionTargetBusinessGeneration"] = 0; }
    private static int MaximumRetries(JsonObject preferences) => Math.Clamp((int)J.Num(preferences["autoMaxStepRetries"], 3), 1, 10);
    private static int MaximumRollbacks(JsonObject preferences) => Math.Clamp((int)J.Num(preferences["autoMaxRollbacks"], 2), 0, 10);
    private static bool IsFullFeed(JsonNode? special, JsonObject order) => J.Bool(J.Obj(special)["active"])
        && J.Str(J.Obj(special)["challengeType"]) == SpecialBusinessRules.WackyChallenge
        && J.Bool(J.Obj(special)["wackyKoishiShieldBroken"]) && J.Str(order["specialBusinessRole"]).Trim() == SpecialBusinessRules.KoishiRole;
}
