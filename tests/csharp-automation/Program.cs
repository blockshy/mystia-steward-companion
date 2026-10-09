using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application.Automation;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

// 差分入口只接受离线测试数据；它不加载 Mod、Unity 或网络适配器。
if (args.Contains("--oracle", StringComparer.Ordinal))
{
    // Windows 控制台默认代码页可能不是 UTF-8，管道必须与 Node JSON 编码显式一致。
    Console.InputEncoding = System.Text.Encoding.UTF8;
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    var results = new JsonArray();
    foreach (var item in J.Objects(JsonNode.Parse(Console.In.ReadToEnd())))
    {
        var candidate = J.Obj(J.Clone(item["state"]));
        var now = (long)J.Num(item["now"]);
        switch (J.Str(item["operation"]))
        {
            case "outcome":
                AutomationMachine.ApplyOutcome(candidate, J.Obj(item["response"]), J.Str(item["stage"]), now, J.Bool(item["stopOnError"]), (int)J.Num(item["maxRetries"]));
                break;
            case "retry": AutomationMachine.Retry(candidate, now); break;
            case "target": AutomationMachine.ReconcileTarget(candidate, J.Str(item["signature"]), (long)J.Num(item["revision"]), now); break;
            default: throw new InvalidOperationException("未定义的离线差分操作。");
        }
        results.Add(candidate);
    }
    Console.WriteLine(results.ToJsonString());
    return;
}

// 该测试入口不模拟 Unity 调用，只验证业务层对既有结构化命令结果的处理和命令生成边界。
var checks = 0;
void Check(bool condition, string label)
{
    checks++;
    if (!condition) throw new InvalidOperationException($"断言失败：{label}");
}
JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
JsonObject Response(string outcome, string stage = "cooking-start", string reason = "", string code = "") =>
    J.Object(("ok", outcome is "progressed" or "completed"), ("automation", J.Object(("outcome", outcome), ("stage", stage), ("reasonCode", reason), ("retryAfterMs", 0), ("jobId", ""))),
        ("steps", code.Length == 0 ? new JsonArray() : J.Array(new[] { J.Object(("code", code), ("ok", true), ("skipped", false), ("message", code)) })));

// 同一阶段等待和中断不会清空历史失败；阶段变化才使用新阶段预算。
var state = AutomationMachine.Empty("normal", "N1|lifecycle:1", 100);
AutomationMachine.ApplyOutcome(state, Response("retryable-failure"), "ensure-cooking", 200, true, 3);
AutomationMachine.ApplyOutcome(state, Response("waiting"), "ensure-cooking", 300, true, 3);
Check(J.Num(state["retryCount"]) == 1, "waiting 保留失败计数");
AutomationMachine.ApplyOutcome(state, Response("interrupted"), "ensure-cooking", 400, true, 3);
Check(J.Num(state["retryCount"]) == 1, "interrupted 保留失败计数");
AutomationMachine.ApplyOutcome(state, Response("retryable-failure", "beverage"), "ensure-cooking", 500, true, 3);
Check(J.Num(state["retryCount"]) == 1 && J.Str(state["retryStage"]) == "ensure-beverage", "运行时阶段优先且分阶段预算独立");
AutomationMachine.ApplyOutcome(state, Response("retryable-failure", "beverage"), "ensure-beverage", 600, true, 2);
Check(J.Bool(state["paused"]), "达到重试上限暂停");
Check(AutomationMachine.Retry(state, 700) && !J.Bool(state["paused"]), "显式普通重试恢复");
AutomationMachine.ApplyOutcome(state, Response("blocked", "cooking-delivery", "cooking-delivery-commit-uncertain"), "ensure-cooking", 800, true, 3);
Check(J.Bool(state["manualResolutionRequired"]) && J.Bool(state["prepared"]), "不确定提交建立人工屏障并保留锅次事实");
Check(!AutomationMachine.Retry(state, 900), "普通重试不能解除人工屏障");
AutomationMachine.RetireDisabledFailure(state, false, false, false, false, new JsonObject(), 1000);
Check(J.Bool(state["manualResolutionRequired"]) && J.Bool(state["paused"]), "关闭阶段不会清除人工屏障");
state["rollbackCount"] = 3;
AutomationMachine.ReconcileTarget(state, "old", 1, 1000);
AutomationMachine.ReconcileTarget(state, "new", 2, 1100);
Check(J.Num(state["rollbackCount"]) == 0 && J.Bool(state["paused"]), "目标轮换重开预算但保留人工屏障");

// 输入夹具使用完整控制器身份和明确经营轮次，不启动真实游戏或依赖真实时间。
JsonObject Input() => Parse("""
{
 "authority":{"sessionId":"game-1","businessGeneration":1,"authorityRevision":1,"automationEpoch":1,"leaseOwned":true,"allowed":true},
 "preferences":{"automationEnabled":true,"autoNormalOrderEnabled":true,"autoRareOrderEnabled":false,"autoNormalTakeBeverage":false,"autoNormalStartCooking":true,"autoNormalDeliverFood":false,"autoNormalCompleteOrder":false,"autoNormalStopOnError":true,"autoNormalConcurrency":2,"autoRareConcurrency":2,"autoMaxStepRetries":3,"autoMaxRollbacks":2},
 "snapshot":{"nightBusinessGeneration":1,"nightBusinessAutomationAllowed":true,"automationEvents":[],"automationCookingJobs":[],"specialBusiness":null,
  "normalBusiness":{"orders":[{"traceId":"N1","orderKey":"N1","orderLifecycleSequence":1,"deskCode":0,"guestName":"普客","foodId":100,"beverageId":1,"foodName":"测试料理","beverageName":"测试酒水","hasServedFood":false,"hasServedBeverage":false,"hasEvaluated":false,"readyToEvaluate":false,"canAutomate":true}]},
  "nightBusiness":{"orders":[]},
  "recommendationState":{"placedCookerSnapshotComplete":true,"placedCookerControllerCount":2,"placedCookerEmptyControllerCount":0,"placedCookerLockedControllerCount":0,"placedCookerReadFailureCount":0,"placedCookerTypeIds":[1,2],"placedCookers":[
    {"controllerIndex":0,"controllerIdentity":"0x10","gridPosition":{"x":0,"y":0,"z":0},"typeIds":[1,2],"automationAvailable":true,"couldOpen":true,"challengeLocked":false},
    {"controllerIndex":1,"controllerIdentity":"0x20","gridPosition":{"x":1,"y":0,"z":0},"typeIds":[1],"automationAvailable":true,"couldOpen":true,"challengeLocked":false}]}},
 "data":{"recipes":[{"id":100,"recipeId":200,"name":"测试料理","cooker":"煮锅"}],"beverages":[{"id":1,"name":"测试酒水"}]},
 "favorites":{"recipes":[],"beverages":[]},"recommendations":{"isCurrent":true,"pending":false,"error":null,"recommendations":[]},
 "normalExecutionTargets":{"isCurrent":true,"pending":false,"error":null,"targets":[]}
}
""");
var engine = new AutomationCoordinator();
var input = Input();
var batch = engine.Advance(input, 1500);
var command = J.Objects(batch["commands"]).Single();
Check(J.Num(J.Obj(command["payload"])["cookerControllerIndex"]) == 1, "优先预约能力较少的物理控制器");
Check(J.Str(command["action"]) == "complete-normal", "普客使用规范组合动作");
var requestId = (long)J.Num(command["requestId"]);
Check(J.Arr(engine.Advance(input, 3000)["commands"]).Count == 0, "同一订单未返回期间不重复发送");
engine.Abandon(requestId);
var resubmitted = J.Objects(engine.Advance(input, 3001)["commands"]).Single();
Check(J.Num(resubmitted["requestId"]) != requestId && J.Num(J.Obj(J.Obj(engine.Snapshot()["states"])["normal:N1|lifecycle:1"])["retryCount"]) == 0,
    "未进入适配器的命令放弃后可重新准入且不消耗失败预算");
requestId = (long)J.Num(resubmitted["requestId"]);
J.Obj(input["authority"])["authorityRevision"] = 2;
engine.Advance(input, 3100);
engine.Complete(requestId, Response("progressed", "cooking-start", "", "cooking-started"), 3200);
Check(!J.Bool(J.Obj(J.Obj(engine.Snapshot()["states"])["normal:N1|lifecycle:1"])["prepared"]), "旧配置响应不得修改新作用域");

// 两个订单必须获得不同物理控制器；相同原生身份的重复行使整轮停止。
input = Input();
var order2 = J.Obj(J.Clone(J.Obj(input["snapshot"])["normalBusiness"]!["orders"]![0]));
order2["traceId"] = "N2"; order2["orderKey"] = "N2"; order2["orderLifecycleSequence"] = 2;
J.Arr(J.Obj(J.Obj(input["snapshot"])["normalBusiness"])["orders"]).Add(order2);
engine = new AutomationCoordinator();
batch = engine.Advance(input, 1500);
Check(J.Objects(batch["commands"]).Select(x => J.Num(J.Obj(x["payload"])["cookerControllerIndex"])).Distinct().Count() == 2, "并发订单不得复用同一物理槽位");
input = Input();
J.Arr(J.Obj(J.Obj(input["snapshot"])["normalBusiness"])["orders"]).Add(J.Clone(J.Arr(J.Obj(J.Obj(input["snapshot"])["normalBusiness"])["orders"])[0]));
engine = new AutomationCoordinator();
Check(J.Arr(engine.Advance(input, 1500)["commands"]).Count == 0, "重复精确订单身份拒绝准入");

// 在途命令与新运行时阻塞事件交错，迟到成功不能抹掉人工屏障；精确 ACK 才能解除。
input = Input(); engine = new AutomationCoordinator();
command = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
var barrier = Parse("""{"sequence":7,"targetKind":"normal","traceId":"N1","orderKey":"N1","orderLifecycleSequence":1,"terminal":true,"outcome":"blocked","code":"cooking-delivery-commit-uncertain","reasonCode":"cooking-delivery-commit-uncertain","orderRuntimeKind":"Normal","orderId":"0x100","orderControllerId":"0x200","message":"待确认"}""");
J.Arr(J.Obj(input["snapshot"])["automationEvents"]).Add(barrier);
engine.Advance(input, 1600);
engine.Complete((long)J.Num(command["requestId"]), Response("progressed", "cooking-start", "", "cooking-started"), 1700);
var pendingState = J.Obj(J.Obj(engine.Snapshot()["states"])["normal:N1|lifecycle:1"]);
Check(J.Bool(pendingState["manualResolutionRequired"]) && J.Num(pendingState["lastRuntimeEventSequence"]) == 7, "迟到成功不能覆盖更高序号屏障");
var ack = engine.BeginAcknowledge(7, 1800);
Check(J.Bool(ack["ok"]), "当前屏障可以发起精确确认");
engine.CompleteAck((long)J.Num(J.Obj(ack["command"])["requestId"]), Parse("""{"ok":true,"sequence":7,"acknowledgedCount":2,"acknowledgedSequences":[7,7]}"""), 1900);
Check(J.Bool(J.Obj(J.Obj(engine.Snapshot()["states"])["normal:N1|lifecycle:1"])["manualResolutionRequired"]), "重复 ACK 序号集合被拒绝");
ack = engine.BeginAcknowledge(7, 2000);
engine.CompleteAck((long)J.Num(J.Obj(ack["command"])["requestId"]), Parse("""{"ok":true,"sequence":7,"acknowledgedCount":1,"acknowledgedSequences":[7]}"""), 2100);
Check(!J.Bool(J.Obj(J.Obj(engine.Snapshot()["states"])["normal:N1|lifecycle:1"])["manualResolutionRequired"]), "有效精确 ACK 清除人工屏障");

// 无完整厨具证据时可以显示状态，但不产生开锅请求。
input = Input(); J.Obj(J.Obj(input["snapshot"])["recommendationState"])["placedCookerSnapshotComplete"] = false;
engine = new AutomationCoordinator();
Check(J.Arr(engine.Advance(input, 1500)["commands"]).Count == 0, "不完整厨具快照不能开锅");
input = Input(); J.Obj(input["authority"])["leaseOwned"] = false;
engine = new AutomationCoordinator();
Check(J.Arr(engine.Advance(input, 1500)["commands"]).Count == 0, "断线无租约时不生成命令");
engine.Stop(); J.Obj(input["authority"])["leaseOwned"] = true;
Check(J.Arr(engine.Advance(input, 1600)["commands"]).Count == 0, "停止后的宿主不会恢复调度");

// 稀客固定使用 executionPlans[0]，不会因为界面行顺序不同而另选候选。
JsonObject RareInput()
{
    var result = Input();
    var preferences = J.Obj(result["preferences"]);
    preferences["autoNormalOrderEnabled"] = false;
    preferences["autoRareOrderEnabled"] = true;
    preferences["autoPrepStartCooking"] = true;
    preferences["autoPrepTakeBeverage"] = true;
    preferences["autoPrepCompleteOrder"] = true;
    preferences["autoPrepCollectCooking"] = true;
    var order = Parse("""{"traceId":"R1","orderLifecycleSequence":5,"deskCode":0,"guestId":10,"runtimeGuestId":12,"guestName":"稀客","foodTagId":1,"foodTag":"甜","beverageTagId":2,"beverageTag":"低酒精","automationAllowed":true,"hasServedFood":false,"hasServedBeverage":false}""");
    J.Obj(J.Obj(result["snapshot"])["nightBusiness"])["orders"] = J.Array(new[] { order });
    var item = J.Object(("order", order), ("customer", J.Object(("id", 10))),
        ("executionPlans", Parse("""{"plans":[{"food":{"recipe":{"id":100,"recipeId":200,"name":"主方案料理","cooker":"煮锅"},"extraIngredients":[{"id":3}],"activeTags":["甜"],"meetsRequiredFood":true},"beverage":{"beverage":{"id":1,"name":"主方案酒水"}}}]}""")["plans"]));
    J.Obj(result["recommendations"])["recommendations"] = J.Array(new[] { item });
    return result;
}
input = RareInput(); engine = new AutomationCoordinator();
command = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
Check(J.Str(command["action"]) == "prepare-rare" && J.Num(J.Obj(command["payload"])["recipeId"]) == 200, "稀客锁定唯一主方案");
var delivered = Response("progressed", "beverage", "", "beverage-delivered");
engine.Complete((long)J.Num(command["requestId"]), delivered, 1600);
var followup = J.Objects(engine.Advance(input, 1601)["commands"]).Single();
Check(J.Str(followup["action"]) == "complete-rare" && !J.Bool(J.Obj(followup["payload"])["autoStartCooking"]), "送酒后立即完成续接不重复开锅");
engine.Complete((long)J.Num(followup["requestId"]), Response("waiting", "order"), 1650);
J.Obj(input["recommendations"])["isCurrent"] = false;
Check(J.Arr(engine.Advance(input, 3100)["commands"]).Count == 0, "过期推荐不能生成新的稀客动作");
input = RareInput(); J.Obj(input["preferences"])["autoPrepRecipeFavoritesOnly"] = true;
engine = new AutomationCoordinator();
Check(J.Arr(engine.Advance(input, 1500)["commands"]).Count == 0, "收藏限定不能借普通推荐绕过");
input = RareInput();
var strictSnapshot = J.Obj(input["snapshot"]);
strictSnapshot["specialBusiness"] = Parse("""{"active":true,"challengeTypeAvailable":true,"challengeType":"Story_WackyCookingCompetition","phase":"phase1","foodTargetTags":["甜"],"targetTagTimeProgress":0.1}""");
engine = new AutomationCoordinator();
command = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
Check(!J.Bool(J.Obj(command["payload"])["autoStartCooking"]) && J.Bool(J.Obj(command["payload"])["autoTakeBeverage"]), "特殊目标倒计时阻止开锅但保留合法酒水步骤");

// 已准入锅次在推荐暂时失败时仍保持控制状态，不能清空后重开一锅。
input = Input(); engine = new AutomationCoordinator();
command = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
var cookingResponse = Response("progressed", "cooking-start", "", "cooking-started");
J.Obj(cookingResponse["automation"])["jobId"] = "job-1";
engine.Complete((long)J.Num(command["requestId"]), cookingResponse, 1600);
J.Obj(input["snapshot"])["automationCookingJobs"] = Parse("""{"jobs":[{"targetKind":"normal","jobId":"job-1","traceId":"N1","orderLifecycleSequence":1,"state":"cooking","controlState":"suspended-authority","controlMessage":"租约暂停"}]}""")["jobs"] is JsonNode jobsNode ? J.Clone(jobsNode) : null;
J.Obj(input["normalExecutionTargets"])["isCurrent"] = false;
batch = engine.Advance(input, 2400);
Check(J.Arr(batch["commands"]).Count == 0 && J.Bool(J.Obj(J.Obj(batch["states"])["normal:N1|lifecycle:1"])["prepared"]), "已准入锅次保留且不重复开锅");
J.Obj(J.Obj(input["snapshot"])["normalBusiness"])["orders"] = new JsonArray();
J.Obj(input["preferences"])["autoNormalDeliverFood"] = true;
J.Obj(input["preferences"])["autoNormalCompleteOrder"] = true;
batch = engine.Advance(input, 4000);
Check(J.Arr(batch["commands"]).Count == 0 && J.Obj(batch["states"]).Count == 1, "消失订单的锅次仅保留诊断，不从旧订单快照继续发动作");
Check(!engine.AcknowledgeResult(999, Parse("""{"ok":true,"sequence":999,"acknowledgedCount":2,"acknowledgedSequences":[999,999]}"""), 4100),
    "没有本地订单的孤立屏障回执仍拒绝重复序号集合");
Console.WriteLine($"PASS csharp-automation: {checks} assertions (pure managed; no game execution).");
