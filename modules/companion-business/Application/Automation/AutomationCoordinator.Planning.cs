using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using MystiaStewardCompanion.Business.Domain.Support;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Application.Automation;

public sealed partial class AutomationCoordinator
{
    /// <summary>普客优先处理可完成订单，再按精确控制器容量准入；厨具等待不会消耗并发名额。</summary>
    private void PlanNormal(List<JsonObject> commands, long nowMs)
    {
        if (_pending.Values.Any(p => p.Action == "complete-normal")) return;
        var snapshot = J.Obj(_input["snapshot"]);
        var preferences = J.Obj(_input["preferences"]);
        if (!HasActions(preferences, "normal")) return;
        var pool = Cookers.BuildAutomationCookerPool(snapshot["recommendationState"]);
        var candidates = SortedOrders("normal").OrderByDescending(pair => CompletionReady(pair.Value, _states[pair.Key]));
        var count = 0;
        foreach (var (key, order) in candidates)
        {
            var state = _states[key];
            if (!CanPlan(key, state, nowMs) || J.Bool(order["hasEvaluated"]) || !J.Bool(order["canAutomate"], true)) continue;
            var special = snapshot["specialBusiness"];
            var force = IsFullFeed(special, order);
            var beverage = (J.Bool(preferences["autoNormalTakeBeverage"]) || force) && !J.Bool(order["hasServedBeverage"])
                && !J.Bool(state["beverageHandled"]) && (force || J.Num(order["beverageId"], -1) >= 0);
            var cooking = (J.Bool(preferences["autoNormalStartCooking"]) || force) && !J.Bool(order["hasServedFood"])
                && !J.Bool(state["prepared"]) && !J.Bool(state["foodDelivered"]) && (force || J.Num(order["foodId"], -1) >= 0);
            var completionReady = (J.Bool(preferences["autoNormalCompleteOrder"]) || force) && !J.Bool(state["completed"]) && CompletionReady(order, state);
            var delivery = (J.Bool(preferences["autoNormalDeliverFood"]) || force) && J.Bool(state["prepared"]) && !J.Bool(order["hasServedFood"]);
            var requiresTarget = cooking || delivery || J.Str(state["cookingJobId"]).Length > 0;
            var policy = SpecialBusinessRules.BuildWirePolicy(special, J.Str(order["specialBusinessRole"]), _businessGeneration);
            var target = ResolveNormalTarget(order, state, policy, requiresTarget, out var targetError);
            if (targetError.Length > 0) { AutomationMachine.Waiting(state, "ensure-cooking", targetError, nowMs); continue; }
            if (HasExpiredHandoff(state, order)) { AutomationMachine.Detail(state, "旧目标成品等待人工处理；同一订单不会重复开锅。", nowMs); continue; }
            if (!beverage && !cooking && !completionReady && !delivery) continue;
            var cookerName = target is not null ? J.Str(target["cookerName"]) : "";
            var recipe = FindRecipe(order);
            if (cookerName.Length == 0) cookerName = J.Str(recipe?["cooker"]);
            JsonObject? slot = null;
            if (cooking)
            {
                slot = Reserve(pool, cookerName);
                if (slot is null) { AutomationMachine.Waiting(state, "ensure-cooking", "等待完整厨具快照及可用的精确控制器预约。", nowMs); continue; }
            }
            var completion = force || J.Bool(preferences["autoNormalCompleteOrder"])
                && (completionReady || beverage || J.Bool(preferences["autoNormalDeliverFood"]));
            if ((beverage || delivery) && !completion) { AutomationMachine.Waiting(state, "match-order", "自动送达必须同时启用自动完成订单。", nowMs); continue; }
            var payload = BuildNormalPayload(order, recipe, target, policy, preferences, beverage, cooking,
                J.Bool(preferences["autoNormalDeliverFood"]) || force, completion, slot);
            if (requiresTarget && target is not null)
            {
                state["executionTarget"] = J.Clone(target);
                state["executionTargetBusinessGeneration"] = _businessGeneration;
            }
            AddCommand(commands, key, "complete-normal", payload, nowMs);
            if (++count >= Math.Clamp((int)J.Num(preferences["autoNormalConcurrency"], 2), 1, 6)) break;
        }
    }

    /// <summary>稀客只消费唯一主方案；收藏限定、特殊目标和物理容量均在准入前核验。</summary>
    private void PlanRare(List<JsonObject> commands, long nowMs)
    {
        if (_pending.Values.Any(p => p.Action is "prepare-rare" or "complete-rare")) return;
        var inputRecommendations = J.Obj(_input["recommendations"]);
        if (!J.Bool(inputRecommendations["isCurrent"]) || J.Bool(inputRecommendations["pending"]) || J.Str(inputRecommendations["error"]).Length > 0) return;
        var snapshot = J.Obj(_input["snapshot"]);
        var preferences = J.Obj(_input["preferences"]);
        if (!HasActions(preferences, "rare")) return;
        var pool = Cookers.BuildAutomationCookerPool(snapshot["recommendationState"]);
        var byKey = J.Objects(inputRecommendations["recommendations"]).GroupBy(item => OrderKey("rare", J.Obj(item["order"])), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0 && group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var normalDemand = BuildNormalDemand(pool, nowMs);
        var count = 0;
        foreach (var (key, order) in SortedOrders("rare"))
        {
            var state = _states[key];
            if (!CanPlan(key, state, nowMs) || !byKey.TryGetValue(J.Str(state["orderKey"]), out var item)) continue;
            if (!J.Bool(order["automationAllowed"], true) || J.Num(order["deskCode"], -1) < 0 || order["runtimeGuestId"] is null
                || order["foodTagId"] is null || order["beverageTagId"] is null) continue;
            var special = snapshot["specialBusiness"];
            var force = IsFullFeed(special, order);
            var targets = PickRareTargets(item, preferences);
            var needsRecipe = J.Bool(preferences["autoPrepStartCooking"]) && !J.Bool(state["prepared"]) && !J.Bool(order["hasServedFood"]);
            var needsBeverage = J.Bool(preferences["autoPrepTakeBeverage"]) && !J.Bool(state["beverageHandled"]) && !J.Bool(order["hasServedBeverage"]);
            // 与原候选处理一致：新开锅不能用另一份宽松候选补齐当前主方案缺少的必要部分。
            if (targets.Recipe is null && needsRecipe || targets.Beverage is null && needsBeverage)
            {
                AutomationMachine.Waiting(state, "match-order", "当前唯一主方案没有满足推荐与收藏限定的完整执行目标。", nowMs);
                continue;
            }
            state["recipeTarget"] ??= J.Clone(targets.Recipe);
            state["beverageTarget"] ??= J.Clone(targets.Beverage);
            var policy = SpecialBusinessRules.BuildWirePolicy(special, J.Str(order["specialBusinessRole"]), _businessGeneration);
            var requiresRecipe = !J.Bool(order["hasServedFood"]) && (J.Str(state["cookingJobId"]).Length > 0
                || (J.Bool(preferences["autoPrepStartCooking"]) || force) && !J.Bool(state["prepared"])
                || (J.Bool(preferences["autoPrepCollectCooking"]) || force) && J.Bool(state["prepared"]));
            var targetError = ReconcileRareTarget(state, order, targets.Recipe, policy, requiresRecipe, nowMs);
            var activeJob = FindJob(state, order, J.Objects(snapshot["automationCookingJobs"]));
            if (activeJob is not null && !J.Bool(order["hasServedFood"])) ReconcileJob(state, activeJob, nowMs);
            AutomationMachine.EnforceRollbackLimit(state, MaximumRollbacks(preferences), nowMs);
            if (targetError.Length > 0) { AutomationMachine.Detail(state, targetError, nowMs); continue; }
            if (!CanPlan(key, state, nowMs) || HasExpiredHandoff(state, order)) continue;
            var recipeTarget = state["recipeTarget"] as JsonObject;
            var beverageTarget = state["beverageTarget"] as JsonObject;
            var cooking = (J.Bool(preferences["autoPrepStartCooking"]) || force) && !J.Bool(state["prepared"]) && !J.Bool(order["hasServedFood"]);
            var beverage = (J.Bool(preferences["autoPrepTakeBeverage"]) || force) && !J.Bool(state["beverageHandled"]) && !J.Bool(order["hasServedBeverage"]);
            var cookingError = CookingDeferral(order, recipeTarget);
            if (recipeTarget is null || cookingError.Length > 0) cooking = false;
            if (beverageTarget is null) beverage = false;
            var completion = J.Bool(preferences["autoPrepCompleteOrder"]) || force;
            var collection = J.Bool(preferences["autoPrepCollectCooking"]) || force;
            if ((beverage || collection) && !completion) { AutomationMachine.Waiting(state, "match-order", "自动送达必须同时启用自动完成订单。", nowMs); continue; }
            var preflight = completion && (J.Bool(state["prepared"]) || activeJob is not null || J.Bool(order["hasServedFood"]));
            JsonObject? slot = null;
            if (cooking)
            {
                slot = Reserve(pool, J.Str(recipeTarget?["cookerName"]), normalDemand);
                if (slot is null) cooking = false;
            }
            if (!preflight && !cooking && !beverage)
            {
                AutomationMachine.Waiting(state, slot is null && recipeTarget is not null ? "ensure-cooking" : completion ? "complete-order" : "idle",
                    cookingError.Length > 0 ? cookingError : "等待料理、订单或可用厨具状态更新。", nowMs);
                continue;
            }
            var payload = BuildRarePayload(order, recipeTarget, beverageTarget, policy, preferences, beverage, cooking, collection, completion, slot);
            if (preflight)
            {
                var prepare = cooking || beverage ? J.Obj(J.Clone(payload)) : null;
                payload["autoStartCooking"] = false;
                ClearReservation(payload);
                AddCommand(commands, key, "complete-rare", payload, nowMs, prepare, "prepare-rare");
            }
            else AddCommand(commands, key, "prepare-rare", payload, nowMs);
            if (++count >= Math.Clamp((int)J.Num(preferences["autoRareConcurrency"], 2), 1, 6)) break;
        }
    }

    /// <summary>跨普客调度间隔预留其本轮开锅需求，稀客不会抢走同一周期的单个物理槽位。</summary>
    private HashSet<int> BuildNormalDemand(JsonObject pool, long nowMs)
    {
        var preferences = J.Obj(_input["preferences"]);
        var reserved = new HashSet<int>();
        if (!J.Bool(preferences["autoNormalOrderEnabled"]) || !J.Bool(preferences["autoNormalStartCooking"])) return reserved;
        var remaining = Math.Clamp((int)J.Num(preferences["autoNormalConcurrency"], 2), 1, 6)
            - _pending.Values.Count(p => p.Action == "complete-normal" && J.Bool(p.Payload["autoStartCooking"]));
        if (remaining <= 0) return reserved;
        var snapshot = J.Obj(_input["snapshot"]);
        foreach (var (key, order) in SortedOrders("normal"))
        {
            var state = _states[key];
            if (!CanPlan(key, state, nowMs) || J.Bool(order["hasEvaluated"]) || J.Bool(order["hasServedFood"]) || J.Bool(state["prepared"])
                || J.Num(order["foodId"], -1) < 0 || !J.Bool(order["canAutomate"], true)) continue;
            var policy = SpecialBusinessRules.BuildWirePolicy(snapshot["specialBusiness"], J.Str(order["specialBusinessRole"]), _businessGeneration);
            var target = ResolveNormalTarget(order, state, policy, true, out var error);
            if (error.Length > 0) continue;
            var cooker = target is null ? J.Str(FindRecipe(order)?["cooker"]) : J.Str(target["cookerName"]);
            var unavailable = new HashSet<int>(_usedCookers);
            unavailable.UnionWith(reserved);
            var slot = Cookers.FindAvailableAutomationCookerSlot(pool, Cookers.NormalizeCookerName(cooker), unavailable);
            if (slot is null) continue;
            reserved.Add((int)J.Num(slot["controllerIndex"]));
            if (reserved.Count >= remaining) break;
        }
        return reserved;
    }

    /// <summary>续接命令重新检查最新作用域、阶段、目标与厨具；不会直接重放上一次响应保存的旧请求。</summary>
    private void DrainContinuations(List<JsonObject> commands, long nowMs)
    {
        var count = _continuations.Count;
        while (count-- > 0 && _continuations.TryDequeue(out var continuation))
        {
            if (continuation.Scope != _scopeVersion || !_currentOrderKeys.Contains(continuation.StateKey) || !_states.TryGetValue(continuation.StateKey, out var state)
                || !_orders.TryGetValue(continuation.StateKey, out var order) || !CanPlan(continuation.StateKey, state, nowMs)) continue;
            var policy = SpecialBusinessRules.BuildWirePolicy(J.Obj(_input["snapshot"])["specialBusiness"], J.Str(order["specialBusinessRole"]), _businessGeneration);
            var payload = J.Obj(J.Clone(continuation.Payload));
            if (J.Str(payload["specialTargetSignature"]) != J.Str(policy["specialTargetSignature"])
                || J.Num(payload["specialTargetRevision"]) != J.Num(policy["specialTargetRevision"])) continue;
            payload["autoTakeBeverage"] = J.Bool(payload["autoTakeBeverage"]) && !J.Bool(state["beverageHandled"]) && !J.Bool(order["hasServedBeverage"]);
            payload["autoStartCooking"] = J.Bool(payload["autoStartCooking"]) && !J.Bool(state["prepared"]) && !J.Bool(order["hasServedFood"]);
            if (J.Bool(payload["autoStartCooking"]))
            {
                var runtime = J.Obj(_input["snapshot"])["recommendationState"];
                var pool = Cookers.BuildAutomationCookerPool(runtime);
                var oldIndex = (int)J.Num(payload["cookerControllerIndex"], -1);
                var slot = J.Objects(pool["slots"]).FirstOrDefault(x => J.Num(x["controllerIndex"]) == oldIndex
                    && J.Str(x["controllerIdentity"]) == J.Str(payload["cookerControllerIdentity"])
                    && J.Num(J.Obj(x["gridPosition"])["x"]) == J.Num(payload["cookerGridX"])
                    && J.Num(J.Obj(x["gridPosition"])["y"]) == J.Num(payload["cookerGridY"])
                    && J.Num(J.Obj(x["gridPosition"])["z"]) == J.Num(payload["cookerGridZ"]));
                var reservedByAnotherRequest = _pending.Values.Any(p =>
                    J.Bool(p.Payload["autoStartCooking"]) && J.Num(p.Payload["cookerControllerIndex"], -1) == oldIndex
                    || p.ContinuationPayload is JsonObject reservedPayload && J.Bool(reservedPayload["autoStartCooking"]) && J.Num(reservedPayload["cookerControllerIndex"], -1) == oldIndex);
                if (slot is null || reservedByAnotherRequest) payload["autoStartCooking"] = false;
                else _usedCookers.Add(oldIndex);
            }
            if (!J.Bool(payload["autoStartCooking"])) ClearReservation(payload);
            if (continuation.Action == "prepare-rare" && !J.Bool(payload["autoStartCooking"]) && !J.Bool(payload["autoTakeBeverage"])) continue;
            AddCommand(commands, continuation.StateKey, continuation.Action, payload, nowMs);
        }
    }

    private bool CanPlan(string key, JsonObject state, long nowMs) => !(RareParticipationBlocked && key.StartsWith("rare:", StringComparison.Ordinal))
        && !J.Bool(state["paused"]) && !J.Bool(state["manualResolutionRequired"])
        && !J.Bool(state["completed"]) && J.Num(state["nextAttemptAtMs"]) <= nowMs && !_pending.Values.Any(x => x.StateKey == key);

    private JsonObject? Reserve(JsonObject pool, string cookerName, ISet<int>? reserved = null)
    {
        if (!J.Bool(pool["snapshotComplete"]) || cookerName.Length == 0) return null;
        var unavailable = new HashSet<int>(_usedCookers);
        if (reserved is not null) unavailable.UnionWith(reserved);
        var slot = Cookers.FindAvailableAutomationCookerSlot(pool, Cookers.NormalizeCookerName(cookerName), unavailable);
        if (slot is not null) _usedCookers.Add((int)J.Num(slot["controllerIndex"]));
        return slot;
    }

    private void AddCommand(List<JsonObject> commands, string key, string action, JsonObject payload, long nowMs,
        JsonObject? continuation = null, string continuationAction = "")
    {
        var state = _states[key];
        var preferences = J.Obj(_input["preferences"]);
        var stage = AutomationMachine.RequestStage(J.Bool(payload["autoTakeBeverage"]), J.Bool(payload["autoStartCooking"]),
            J.Bool(payload["autoDeliverFood"]) && J.Bool(state["prepared"]) && !J.Bool(_orders[key]["hasServedFood"]), J.Bool(payload["autoCompleteOrder"]));
        var id = ++_nextRequestId;
        var pending = new PendingCommand(id, _scopeVersion, key, action, stage, (long)J.Num(state["lastRuntimeEventSequence"]),
            J.Obj(J.Clone(_orders[key])), J.Obj(J.Clone(payload)), J.Bool(payload["stopOnError"]), MaximumRetries(preferences), MaximumRollbacks(preferences),
            continuation is null ? null : J.Obj(J.Clone(continuation)), continuationAction);
        _pending[id] = pending;
        commands.Add(CommandNode(pending));
        AutomationMachine.SetStep(state, stage, nowMs);
    }

    private JsonObject? ResolveNormalTarget(JsonObject order, JsonObject state, JsonObject policy, bool requiresRecipe, out string error)
    {
        var special = J.Obj(_input["snapshot"])["specialBusiness"];
        var role = J.Str(order["specialBusinessRole"]);
        var rule = SpecialBusinessRules.BuildOrderRule(special, role);
        error = J.Str(rule["blockingReason"]);
        if (error.Length > 0) return null;
        if (!SpecialBusinessRules.RequiresNormalTarget(special, role)) return null;
        if (J.Str(J.Obj(rule["foodTarget"])["enforcement"]) == "require" && J.Arr(J.Obj(rule["foodTarget"])["tags"]).Count > 0 && J.Str(policy["specialTargetSignature"]).Length == 0)
        { error = "特殊经营料理目标缺少有效经营代际或目标身份。"; return null; }
        if (state["executionTarget"] is JsonObject locked && J.Num(state["executionTargetBusinessGeneration"]) == _businessGeneration
            && J.Str(locked["specialTargetSignature"]) == J.Str(policy["specialTargetSignature"])
            && J.Num(locked["specialTargetRevision"]) == J.Num(policy["specialTargetRevision"])) return J.Obj(J.Clone(locked));
        if (!requiresRecipe) { error = "特殊经营料理执行目标未在执行前锁存。"; return null; }
        var targets = J.Obj(_input["normalExecutionTargets"]);
        if (!J.Bool(targets["isCurrent"]) || J.Bool(targets["pending"]) || J.Str(targets["error"]).Length > 0)
        { error = "特殊经营执行目标尚未取得当前输入的有效结果。"; return null; }
        var selections = J.Objects(targets["targets"]).Where(x => J.Str(x["orderKey"]) == J.Str(state["orderKey"])).Take(2).ToArray();
        if (selections.Length != 1 || selections[0]["target"] is not JsonObject target)
        { error = selections.Length == 1 ? J.Str(selections[0]["message"], "当前特殊经营没有可执行目标。") : "特殊经营执行目标缺失或不唯一。"; return null; }
        error = J.Str(selections[0]["message"]);
        if (error.Length > 0) return null;
        var next = J.Obj(J.Clone(target));
        CopyPolicy(next, policy);
        return next;
    }

    private string ReconcileRareTarget(JsonObject state, JsonObject order, JsonObject? recommended, JsonObject policy, bool required, long nowMs)
    {
        var special = J.Obj(_input["snapshot"])["specialBusiness"];
        var rule = SpecialBusinessRules.BuildOrderRule(special, J.Str(order["specialBusinessRole"]));
        if (J.Str(rule["blockingReason"]).Length > 0) return J.Str(rule["blockingReason"]);
        var targetRule = J.Obj(rule["foodTarget"]);
        var signature = J.Str(policy["specialTargetSignature"]);
        var revision = (long)J.Num(policy["specialTargetRevision"]);
        var rotated = AutomationMachine.ReconcileTarget(state, signature, revision, nowMs);
        if (J.Str(targetRule["enforcement"]) != "require" || J.Arr(targetRule["tags"]).Count == 0)
        {
            if (state["recipeTarget"] is JsonObject ordinary) CopyPolicy(ordinary, SpecialBusinessRules.EmptyWirePolicy());
            state["recipeTargetSignature"] = ""; state["recipeTargetRevision"] = 0;
            return "";
        }
        if (signature.Length == 0) return "特殊经营料理目标缺少有效经营代际或目标身份。";
        if (!required) { state["recipeTarget"] = null; state["recipeTargetSignature"] = ""; state["recipeTargetRevision"] = 0; return ""; }
        var current = state["recipeTarget"] as JsonObject;
        bool Valid(JsonObject? value) => value is not null && SpecialBusinessRules.MatchesFoodTarget(value["foodTags"], targetRule)
            && !IsRejected(value, targetRule["tags"]);
        if (Valid(current))
        {
            CopyPolicy(current!, policy);
            state["recipeTargetSignature"] = signature; state["recipeTargetRevision"] = revision;
            return "";
        }
        if (Valid(recommended))
        {
            var changed = current is null || !SameRecipe(current, recommended!);
            var next = J.Obj(J.Clone(recommended));
            CopyPolicy(next, policy);
            state["recipeTarget"] = next;
            state["recipeTargetSignature"] = signature; state["recipeTargetRevision"] = revision;
            state["prepared"] = false; state["cookingJobId"] = "";
            AutomationMachine.SetStep(state, "ensure-cooking", nowMs);
            state["lastProgressAtMs"] = nowMs;
            AutomationMachine.ClearRetries(state);
            state["rollbackCount"] = rotated ? 0 : J.Num(state["rollbackCount"]) + (changed ? 1 : 0);
            return "";
        }
        if (current is not null)
        {
            state["recipeTarget"] = null;
            state["prepared"] = false; state["cookingJobId"] = "";
            state["rollbackCount"] = rotated ? 0 : J.Num(state["rollbackCount"]) + 1;
            AutomationMachine.ClearRetries(state);
            AutomationMachine.SetStep(state, "ensure-cooking", nowMs);
        }
        state["recipeTargetSignature"] = signature; state["recipeTargetRevision"] = revision;
        return "当前特殊经营目标没有可执行且未被实际拒绝的料理方案。";
    }

    private bool IsRejected(JsonObject target, JsonNode? tags) => J.Str(J.Obj(J.Obj(_input["snapshot"])["specialBusiness"])["challengeType"]) == SpecialBusinessRules.WackyChallenge
        && _rejectedRecipes.Contains(SpecialBusinessRules.RejectedRecipeKey(tags, J.Num(target["foodId"], -1), J.Num(target["recipeId"], -1), J.Numbers(target["extraIngredientIds"])), StringComparer.Ordinal);

    private string CookingDeferral(JsonObject order, JsonObject? target)
    {
        if (target is null) return "当前没有可执行的料理目标。";
        var special = J.Obj(_input["snapshot"])["specialBusiness"];
        var rule = SpecialBusinessRules.BuildOrderRule(special, J.Str(order["specialBusinessRole"]));
        var foodTarget = J.Obj(rule["foodTarget"]);
        if (J.Str(J.Obj(special)["challengeType"]) != SpecialBusinessRules.WackyChallenge || J.Str(foodTarget["enforcement"]) != "require" || J.Arr(foodTarget["tags"]).Count == 0) return "";
        if (IsRejected(target, foodTarget["tags"])) return "该料理加料组合已被当前特殊目标的实际评价拒绝。";
        if (!SpecialBusinessRules.MatchesFoodTarget(target["foodTags"], foodTarget)) return "当前料理目标不满足特殊经营目标标签。";
        return SpecialBusinessRules.CountdownDeferral(special);
    }

    private (JsonObject? Recipe, JsonObject? Beverage) PickRareTargets(JsonObject item, JsonObject preferences)
    {
        var needRecipe = J.Bool(preferences["autoPrepStartCooking"]);
        var needBeverage = J.Bool(preferences["autoPrepTakeBeverage"]);
        if (!needRecipe && !needBeverage) return (null, null);
        var plan = J.Objects(item["executionPlans"]).FirstOrDefault();
        if (plan is null) return (null, null);
        var food = plan["food"] as JsonObject;
        var beverageCandidate = plan["beverage"] as JsonObject;
        var recipe = food is null ? null : J.Obj(food["recipe"]);
        var drink = beverageCandidate is null ? null : J.Obj(beverageCandidate["beverage"]);
        var extras = food is null ? Array.Empty<double>() : J.Objects(food["extraIngredients"]).Select(x => J.Num(x["id"])).ToArray();
        var order = J.Obj(item["order"]);
        var customerId = J.Num(J.Obj(item["customer"])["id"], -1);
        var favorites = J.Obj(_input["favorites"]);
        var recipeFavorite = recipe is not null && J.Objects(favorites["recipes"]).Any(x => J.Num(x["customerId"], -1) == customerId
            && J.Str(x["foodTag"]) == J.Str(order["foodTag"]) && J.Num(x["recipeId"], -1) == J.Num(recipe["id"], -2)
            && NormalizedIds(J.Numbers(x["extraIngredientIds"])).SequenceEqual(NormalizedIds(extras)));
        var beverageFavorite = drink is not null && J.Objects(favorites["beverages"]).Any(x => J.Num(x["customerId"], -1) == customerId
            && J.Str(x["beverageTag"]) == J.Str(order["beverageTag"]) && J.Num(x["beverageId"], -1) == J.Num(drink["id"], -2));
        if (needRecipe && (recipe is null || J.Bool(preferences["autoPrepRecipeFavoritesOnly"]) && !recipeFavorite)
            || needBeverage && (drink is null || J.Bool(preferences["autoPrepBeverageFavoritesOnly"]) && !beverageFavorite)) return (null, null);
        var target = needRecipe && recipe is not null ? J.Object(("recipeId", recipe["recipeId"]), ("foodId", recipe["id"]),
            ("recipeName", recipe["name"]), ("cookerName", recipe["cooker"]), ("extraIngredientIds", J.Array(extras)),
            ("foodTags", food!["activeTags"]), ("favorite", recipeFavorite), ("preferenceFallback", !J.Bool(food["meetsRequiredFood"]))) : null;
        if (target is not null) CopyPolicy(target, SpecialBusinessRules.EmptyWirePolicy());
        return (target, needBeverage && drink is not null ? J.Object(("beverageId", drink["id"]), ("beverageName", drink["name"]), ("favorite", beverageFavorite)) : null);
    }

    /// <summary>显示和自动化复用同一订单排序规则，特殊角色优先级不得由客户端再次推导。</summary>
    private IEnumerable<KeyValuePair<string, JsonObject>> SortedOrders(string kind)
    {
        var orders = _orders.Where(pair => _currentOrderKeys.Contains(pair.Key)
            && _states.TryGetValue(pair.Key, out var state) && J.Str(state["kind"]) == kind).Select(pair => pair.Value);
        var sorted = kind == "normal"
            ? Domain.Orders.OrderIdentityAndSorting.SortNormal(orders)
            : Domain.Orders.OrderIdentityAndSorting.SortRare(orders, J.Str(J.Obj(_input["preferences"])["serviceOrderSortMode"]),
                J.Obj(_input["snapshot"])["specialBusiness"]);
        return sorted.Select(order => new KeyValuePair<string, JsonObject>(StateKey(kind, OrderKey(kind, order)), order));
    }

    private JsonObject? FindRecipe(JsonObject order) => J.Objects(J.Obj(_input["data"])["recipes"]).FirstOrDefault(x => J.Num(x["id"], -2) == J.Num(order["foodId"], -1));
    private bool HasExpiredHandoff(JsonObject state, JsonObject order) => !J.Bool(order["hasServedFood"])
        && J.Str(FindJob(state, order, J.Objects(J.Obj(_input["snapshot"])["automationCookingJobs"]))?["state"]) == "manual-handoff-expired";
    private static bool CompletionReady(JsonObject order, JsonObject state) => J.Bool(order["readyToEvaluate"])
        || (J.Bool(order["hasServedFood"]) || J.Bool(state["foodDelivered"])) && (J.Bool(order["hasServedBeverage"]) || J.Bool(state["beverageHandled"]));
    private static bool HasActions(JsonObject preferences, string kind) => kind == "normal"
        ? J.Bool(preferences["autoNormalTakeBeverage"]) || J.Bool(preferences["autoNormalStartCooking"]) || J.Bool(preferences["autoNormalDeliverFood"]) || J.Bool(preferences["autoNormalCompleteOrder"])
        : J.Bool(preferences["autoPrepTakeBeverage"]) || J.Bool(preferences["autoPrepStartCooking"]) || J.Bool(preferences["autoPrepCollectCooking"]) || J.Bool(preferences["autoPrepCompleteOrder"]);
    private static IEnumerable<double> NormalizedIds(IEnumerable<double> values) => values.Where(x => double.IsFinite(x) && x >= 0).Select(Math.Truncate).Distinct().OrderBy(x => x);
    private static bool SameRecipe(JsonObject left, JsonObject right) => J.Num(left["foodId"]) == J.Num(right["foodId"]) && J.Num(left["recipeId"]) == J.Num(right["recipeId"])
        && NormalizedIds(J.Numbers(left["extraIngredientIds"])).SequenceEqual(NormalizedIds(J.Numbers(right["extraIngredientIds"])));
    private static void CopyPolicy(JsonObject target, JsonObject policy) { foreach (var (key, value) in policy) target[key] = J.Clone(value); }
}
