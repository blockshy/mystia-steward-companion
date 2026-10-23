using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application.Automation;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>只调用真实纯托管协调器，验证重置不会取消锅次、解除屏障或重新提交未知动作。</summary>
internal static class AutomationResetSafetyTests
{
    internal static void Run(Action<bool, string> check, Func<JsonObject> factory)
    {
        foreach (var kind in new[] { "normal", "rare" })
        {
            var input = factory();
            var preferences = J.Obj(input["preferences"]);
            preferences["autoNormalStartCooking"] = false;
            preferences["autoRareOrderEnabled"] = kind == "rare";
            var snapshot = J.Obj(input["snapshot"]);
            var order = J.Objects(snapshot["normalBusiness"]?["orders"]).Single();
            if (kind == "rare")
            {
                J.Obj(snapshot["nightBusiness"])["orders"] = new JsonArray(J.Clone(order));
                J.Obj(snapshot["normalBusiness"])["orders"] = new JsonArray();
                order = J.Objects(snapshot["nightBusiness"]?["orders"]).Single();
            }
            var key = AutomationCoordinator.OrderKey(kind, order);
            var engine = new AutomationCoordinator();
            long now = 100;
            engine.AdvanceFrozen(View(input), now);
            void NewFrame()
            {
                input = J.Obj(J.Clone(input));
                snapshot = J.Obj(input["snapshot"]);
                order = J.Objects(snapshot[kind == "normal" ? "normalBusiness" : "nightBusiness"]?["orders"]).Single();
            }
            JsonObject Reset(string session = "game-1", long generation = 1, JsonObject? expected = null) =>
                engine.Reset(kind, key, session, generation, expected ?? snapshot, now + 1);
            check(J.Bool(Reset()["ok"]), kind + " 未开始动作的当前订单可安全重置");
            check(!J.Bool(Reset("other")["ok"]) && !J.Bool(Reset(generation: 2)["ok"]), kind + " 拒绝跨会话或经营代次");
            check(!J.Bool(Reset(expected: J.Obj(J.Clone(snapshot)))["ok"]), kind + " 相同文字的新旧快照不能冒充当前冻结输入");
            check(!J.Bool(engine.Reset(kind, key + "old", "game-1", 1, snapshot, 101)["ok"]), kind + " 拒绝旧订单身份");
            NewFrame(); order["hasServedFood"] = true;
            engine.AdvanceFrozen(View(input), now += 2);
            check(J.Bool(Reset()["ok"]) && J.Bool(J.Obj(engine.Snapshot()["states"])[kind + ":" + key]?["foodDelivered"]),
                kind + " 重置保留现场已确认的料理送达");
            NewFrame(); order["hasServedFood"] = "false";
            engine.AdvanceFrozen(View(input), now += 2);
            check(!J.Bool(Reset()["ok"]), kind + " 非布尔送达值拒绝重置");
            NewFrame(); order["hasServedFood"] = false;
            J.Arr(snapshot["automationCookingJobs"]).Add(J.Object(("targetKind", kind), ("traceId", order["traceId"]),
                ("orderKey", order["orderKey"]), ("orderLifecycleSequence", 1), ("jobId", "job-active")));
            engine.AdvanceFrozen(View(input), now += 2);
            var blockedJob = Reset();
            check(!J.Bool(blockedJob["ok"]) && J.Str(blockedJob["error"]).Contains("料理任务"),
                kind + " 活动锅次不得通过重置遗忘");
        }

        foreach (var outcome in new[] { "pending", "abandoned", "unknown", "manual" })
        {
            var input = factory(); var snapshot = J.Obj(input["snapshot"]);
            var engine = new AutomationCoordinator();
            var command = J.Objects(engine.AdvanceFrozen(View(input), 100)["commands"]).Single();
            var id = (long)J.Num(command["requestId"]);
            if (outcome == "abandoned") engine.Abandon(id);
            if (outcome == "unknown") engine.Fail(id, "模拟丢失回执", 101);
            if (outcome == "manual") engine.Complete(id, J.Object(("ok", false),
                ("automation", J.Object(("outcome", "blocked"), ("stage", "cooking-start"),
                    ("reasonCode", "cooking-start-unowned"))), ("steps", new JsonArray())), 101);
            var result = engine.Reset("normal", "N1|lifecycle:1", "game-1", 1, snapshot, 102);
            check(J.Bool(result["ok"]) == (outcome == "abandoned"), outcome + " 只有明确未进入游戏适配器的命令可被安全遗忘");
            check(J.Arr(result["commands"]).Count == 0, "重置结果本身不携带新的游戏执行命令");
        }

        foreach (var blocked in new[] { "completed", "authority", "jobs-missing", "events-missing", "partial-delivery" })
        {
            var input = factory(); var snapshot = J.Obj(input["snapshot"]);
            J.Obj(input["preferences"])["autoNormalStartCooking"] = false;
            var order = J.Objects(snapshot["normalBusiness"]?["orders"]).Single();
            if (blocked == "completed") order["hasEvaluated"] = true;
            if (blocked == "authority") J.Obj(input["authority"])["allowed"] = false;
            if (blocked == "jobs-missing") snapshot.Remove("automationCookingJobs");
            if (blocked == "events-missing") snapshot.Remove("automationEvents");
            var engine = new AutomationCoordinator();
            if (blocked == "partial-delivery")
            {
                // 构造真实步骤成功回执，但现场尚未证实送达；重置不能遗忘这次已知写入。
                J.Obj(input["preferences"])["autoNormalStartCooking"] = true;
                var command = J.Objects(engine.AdvanceFrozen(View(input), 100)["commands"]).Single();
                engine.Complete((long)J.Num(command["requestId"]), J.Object(("ok", true),
                    ("automation", J.Object(("outcome", "progressed"), ("stage", "cooking-start"))),
                    ("steps", new JsonArray(J.Object(("code", "cooking-started"), ("ok", true), ("skipped", false))))), 101);
            }
            else engine.AdvanceFrozen(View(input), 100);
            check(!J.Bool(engine.Reset("normal", "N1|lifecycle:1", "game-1", 1, snapshot, 102)["ok"]),
                blocked + " 不能用重置绕过完成、权限或未确定现场事实");
        }
    }

    /// <summary>测试取得输入独占所有权后提交冻结组件；后续变化先生成新树再 Advance。</summary>
    private static AutomationInputView View(JsonObject input) => AutomationInputView.FromFrozenComponents(
        J.Obj(input["snapshot"]), J.Obj(input["preferences"]), J.Obj(input["data"]), J.Obj(input["favorites"]), J.Obj(input["authority"]),
        new(J.Arr(input["recommendations"]?["recommendations"]), true, false, null),
        new(J.Arr(input["normalExecutionTargets"]?["targets"]), true, false, null));
}
