using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application.Automation;
using MystiaStewardCompanion.Business.Domain.Orders;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>离线验证完整调度投影与真实 C# 编排器连接，不模拟或驱动原生游戏。</summary>
internal static class RareParticipationTests
{
    internal static void Run(Action<bool, string> check, Func<JsonObject> rareInput)
    {
        JsonObject Prepare()
        {
            var input = rareInput();
            var profile = J.Obj(input["preferences"]);
            profile["rareGuestParticipationModuleEnabled"] = true;
            profile["managedRareGuestIds"] = new JsonArray(10);
            var snapshot = J.Obj(input["snapshot"]);
            J.Obj(snapshot["nightBusiness"])["ordersReadComplete"] = true;
            J.Objects(snapshot["nightBusiness"]?["orders"]).Single()["traceId"] = "R-0001";
            J.Obj(J.Objects(input["recommendations"]?["recommendations"]).Single()["order"])["traceId"] = "R-0001";
            snapshot["rareGuestParticipation"] = JsonNode.Parse("""
                {"active":true,"businessGeneration":1,"participationRevision":1,"managedGuestIds":[10],
                 "entries":[{"traceId":"R-0001","orderLifecycleSequence":5,"guestId":10,"managed":true,"participating":false,"queuePosition":null}]}
                """);
            return input;
        }
        var paused = Prepare();
        var snapshot = J.Obj(paused["snapshot"]);
        var profile = J.Obj(paused["preferences"]);
        check(RareOrderParticipationProjection.TryReadPositions(snapshot, profile, out var positions)
            && positions is { Count: 0 }, "完整默认暂停队列合法，但无参与订单");
        check(J.Arr(new AutomationCoordinator().Advance(paused, 1500)["commands"]).Count == 0,
            "暂停订单保留完整推荐也不能生成自动化命令");
        check(J.Objects(paused["recommendations"]?["recommendations"]).Count() == 1,
            "调度投影不删除完整推荐");
        var active = Prepare();
        var entry = J.Objects(active["snapshot"]?["rareGuestParticipation"]?["entries"]).Single();
        entry["participating"] = true; entry["queuePosition"] = 1;
        check(J.Arr(new AutomationCoordinator().Advance(active, 1500)["commands"]).Count == 1,
            "精确启用后同一订单恢复自动化计划");
        foreach (var defect in new[] { "missing-entry", "duplicate-entry", "old-generation", "roster-mismatch", "identity-reuse", "queue-gap", "capture-error", "capture-incomplete", "capture-completeness-missing" })
        {
            var malformed = J.Obj(J.Clone(active));
            var snap = J.Obj(malformed["snapshot"]);
            var state = J.Obj(snap["rareGuestParticipation"]);
            var entries = J.Arr(state["entries"]);
            switch (defect)
            {
                case "missing-entry": entries.Clear(); break;
                case "duplicate-entry": entries.Add(J.Clone(entries[0])); break;
                case "old-generation": state["businessGeneration"] = 2; break;
                case "roster-mismatch": state["managedGuestIds"] = new JsonArray(11); break;
                case "identity-reuse": J.Obj(entries[0])["orderLifecycleSequence"] = 6; break;
                case "queue-gap": J.Obj(entries[0])["queuePosition"] = 2; break;
                case "capture-error": J.Obj(snap["nightBusiness"])["error"] = "partial"; break;
                case "capture-incomplete": J.Obj(snap["nightBusiness"])["ordersReadComplete"] = false; break;
                case "capture-completeness-missing": J.Obj(snap["nightBusiness"]).Remove("ordersReadComplete"); break;
            }
            check(!RareOrderParticipationProjection.TryReadPositions(snap, J.Obj(malformed["preferences"]), out _),
                "损坏完整集合拒绝：" + defect);
            check(J.Arr(new AutomationCoordinator().Advance(malformed, 1500)["commands"]).Count == 0,
                "损坏完整集合不生成命令：" + defect);
        }
        // 不能只测首轮计划：预检返回后存在立即续接队列，而采集完整性变化不一定改变 epoch。
        // 通过真实 complete-rare → prepare-rare 路径验证暂缓/恢复，不用私有队列注入代替行为。
        foreach (var defect in new[] { "capture-incomplete", "missing-entry", "paused-entry", "identity-reuse" })
        {
            var input = J.Obj(J.Clone(active));
            var snap = J.Obj(input["snapshot"]);
            var order = J.Objects(snap["nightBusiness"]?["orders"]).Single();
            order["hasServedFood"] = true;
            J.Obj(J.Objects(input["recommendations"]?["recommendations"]).Single()["order"])["hasServedFood"] = true;
            var coordinator = new AutomationCoordinator();
            var preflight = J.Objects(coordinator.Advance(input, 1500)["commands"]).Single();
            check(J.Str(preflight["action"]) == "complete-rare"
                && !J.Bool(J.Obj(preflight["payload"])["autoStartCooking"]),
                "已送料理订单先执行完成预检：" + defect);
            coordinator.Complete((long)J.Num(preflight["requestId"]), J.Object(("ok", true),
                ("automation", J.Object(("outcome", "waiting"), ("stage", "order"), ("retryAfterMs", 0))),
                ("steps", new JsonArray())), 1600);
            var originalParticipation = J.Clone(snap["rareGuestParticipation"]);
            var entries = J.Arr(snap["rareGuestParticipation"]?["entries"]);
            switch (defect)
            {
                case "capture-incomplete": J.Obj(snap["nightBusiness"])["ordersReadComplete"] = false; break;
                case "missing-entry": entries.Clear(); break;
                case "paused-entry": J.Obj(entries[0])["participating"] = false; J.Obj(entries[0])["queuePosition"] = null; break;
                case "identity-reuse": J.Obj(entries[0])["orderLifecycleSequence"] = 6; break;
            }
            var waiting = coordinator.Advance(input, 1601);
            var waitingState = J.Obj(J.Obj(waiting["states"])["rare:trace:R-0001|lifecycle:5"]);
            check(J.Arr(waiting["commands"]).Count == 0 && J.Bool(waitingState["prepared"])
                && J.Bool(waitingState["foodDelivered"]) && !J.Bool(waitingState["paused"])
                && J.Num(waitingState["retryCount"]) == 0 && J.Num(waitingState["rollbackCount"]) == 0,
                "参与投影不可用时续接不发动作、不丢进度或消耗预算：" + defect);
            check(J.Arr(coordinator.Advance(input, 1650)["commands"]).Count == 0,
                "连续不可用帧不重放被暂缓续接：" + defect);
            J.Obj(snap["nightBusiness"])["ordersReadComplete"] = true;
            snap["rareGuestParticipation"] = originalParticipation;
            // 1700 尚未达到下一次常规稀客调度周期，只能由保留下来的续接恢复。
            var resumed = J.Objects(coordinator.Advance(input, 1700)["commands"]).Single();
            check(J.Str(resumed["action"]) == "prepare-rare" && J.Bool(J.Obj(resumed["payload"])["autoTakeBeverage"])
                && !J.Bool(J.Obj(resumed["payload"])["autoStartCooking"]),
                "完整身份恢复后只继续未完成步骤，不重复制作已送料理：" + defect);
            check(J.Arr(coordinator.Advance(input, 1701)["commands"]).Count == 0,
                "恢复续接只提交一次：" + defect);
        }
        foreach (var disabled in new[] { "module-off", "empty-roster" })
        {
            var input = Prepare(); var preferences = J.Obj(input["preferences"]);
            if (disabled == "module-off") preferences["rareGuestParticipationModuleEnabled"] = false;
            else preferences["managedRareGuestIds"] = new JsonArray();
            J.Obj(input["snapshot"]).Remove("rareGuestParticipation");
            check(J.Arr(new AutomationCoordinator().Advance(input, 1500)["commands"]).Count == 1,
                "关闭模块或空名单完整沿用既有行为：" + disabled);
        }
    }
}