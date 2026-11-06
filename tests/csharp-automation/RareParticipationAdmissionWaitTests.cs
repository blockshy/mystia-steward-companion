using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application.Automation;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 复现业务快照已允许计划、主线程准入却遇到下一次集合同步的竞争。通过生产协调器的
/// Advance/Complete验证等待与恢复；不替换私有状态、不调用游戏，也不以定时重放绕过准入。
/// </summary>
internal static class RareParticipationAdmissionWaitTests
{
    private const string Trace = "R-0025";
    private const string Key = "rare:trace:R-0025|lifecycle:5";
    private static readonly string[] Reasons =
    {
        "participation-source-incomplete", "participation-profile-not-aligned", "participation-orders-not-aligned",
    };

    internal static void Run(Action<bool, string> check, Func<JsonObject> rareInput)
    {
        var count = 0;
        void Check(bool condition, string message) { count++; check(condition, message); }
        foreach (var reason in Reasons)
        foreach (var mode in new[] { "fresh-with-retry", "cooking", "food-served" })
        {
            var input = Input(rareInput);
            var order = J.Objects(input["snapshot"]?["nightBusiness"]?["orders"]).Single();
            if (mode == "cooking")
            {
                order["hasServedBeverage"] = true;
                J.Obj(input["snapshot"])["automationCookingJobs"] = J.Array(new[] { J.Object(
                    ("targetKind", "rare"), ("jobId", "CJ-existing"), ("traceId", Trace),
                    ("orderLifecycleSequence", 5), ("state", "cooking"), ("controlState", "active")) });
            }
            if (mode == "food-served") order["hasServedFood"] = true;
            MirrorOrder(input, order);
            var engine = new AutomationCoordinator();
            var command = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
            if (mode == "fresh-with-retry")
            {
                // 先经真实失败响应形成非零失败计数，避免只用零值证明“未重置预算”。
                engine.Complete((long)J.Num(command["requestId"]), Response("retryable-failure", "beverage", "synthetic-before-wait"), 1600);
                command = J.Objects(engine.Advance(input, 3100)["commands"]).Single();
                Check(J.Num(State(engine)["retryCount"]) == 1, "前置等待夹具具有既有失败预算");
            }
            var before = State(engine);
            engine.Complete((long)J.Num(command["requestId"]), Response("waiting", "participation", reason), 3200);
            var state = State(engine);
            foreach (var field in new[] { "step", "stepStartedAtMs", "prepared", "cookingJobId", "beverageHandled",
                         "beverageHandledAtMs", "foodDelivered", "foodDeliveredAtMs", "completed", "completedAtMs",
                         "retryCount", "retryStage", "rollbackCount", "recipeTarget", "beverageTarget" })
            {
                Check(state[field]?.ToJsonString() == before[field]?.ToJsonString(),
                    $"{reason}/{mode}前置等待保留{field}");
            }
            Check(!J.Bool(state["paused"]) && !J.Bool(state["manualResolutionRequired"])
                && J.Num(state["nextAttemptAtMs"]) == 3700,
                $"{reason}/{mode}只进入有界等待，不升级永久暂停");
            Check(!J.Str(state["detailMessage"]).Contains("重试暂停订单")
                && J.Str(state["detailMessage"]).Contains("未修改游戏状态"),
                "等待详情不误导用户进行人工重试");
            Check(J.Arr(engine.Advance(input, 3201)["commands"]).Count == 0,
                "前置拒绝不能立即执行旧预检携带的准备续接");

            J.Obj(input["snapshot"]?["nightBusiness"])["ordersReadComplete"] = false;
            foreach (var now in new long[] { 4000, 6000, 9000 })
            {
                Check(J.Arr(engine.Advance(input, now)["commands"]).Count == 0,
                    "等待冷却到期不等于完整集合许可，连续部分读取不发命令");
                Check(!J.Bool(State(engine)["paused"]) && J.Num(State(engine)["retryCount"]) == J.Num(before["retryCount"])
                    && J.Num(State(engine)["rollbackCount"]) == J.Num(before["rollbackCount"]),
                    "持续部分读取不消耗失败或回退预算");
            }
            J.Obj(input["snapshot"]?["nightBusiness"])["ordersReadComplete"] = true;
            var resumed = J.Objects(engine.Advance(input, 11000)["commands"]).Single();
            Check(J.Num(resumed["requestId"]) != J.Num(command["requestId"])
                && J.Str(resumed["action"]) == (mode == "fresh-with-retry" ? "prepare-rare" : "complete-rare"),
                "完整输入恢复后由规划器创建新请求，不重放旧命令");
            if (mode != "fresh-with-retry")
                Check(!J.Bool(resumed["payload"]?["autoStartCooking"]), "已开锅或已送料理不重复制作");
            Check(J.Arr(engine.Advance(input, 11001)["commands"]).Count == 0, "恢复后同单只有一个在途请求");
        }

        // 即使以后适配器返回缺失、过小或异常大的冷却，也必须维持500毫秒下限，
        // 并限制等待上限；防止一次协议偏差把安全重查变成主线程紧密循环。
        foreach (var delay in new int?[] { null, -1, 0, 250, 500, 1200, 6000 })
        {
            var input = Input(rareInput); var engine = new AutomationCoordinator();
            var command = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
            var response = Response("waiting", "participation", Reasons[0]);
            if (delay is null) J.Obj(response["automation"]).Remove("retryAfterMs");
            else J.Obj(response["automation"])["retryAfterMs"] = delay.Value;
            engine.Complete((long)J.Num(command["requestId"]), response, 1600);
            var expected = delay is > 5000 ? 5000 : delay is > 500 ? delay.Value : 500;
            Check(J.Num(State(engine)["nextAttemptAtMs"]) == 1600 + expected
                && !J.Bool(State(engine)["paused"]), "参与等待冷却限制为500至5000毫秒：" + delay);
        }

        // 用已确认入保温箱的真实运行时事件形成非零回退预算，再验证参与同步等待
        // 不把已有回退归零或重复累加；不直接修改协调器私有状态来制造测试前提。
        {
            var input = Input(rareInput);
            J.Obj(input["preferences"])["autoMaxRollbacks"] = 3;
            var engine = new AutomationCoordinator();
            var command = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
            engine.Complete((long)J.Num(command["requestId"]), J.Object(("ok", true),
                ("automation", J.Object(("outcome", "progressed"), ("stage", "cooking-start"), ("jobId", "CJ-rollback"))),
                ("steps", J.Array(new[] { J.Object(("code", "cooking-started"), ("ok", true), ("skipped", false)) }))), 1600);
            J.Obj(input["snapshot"])["automationEvents"] = J.Array(new[] { J.Object(
                ("sequence", 1), ("targetKind", "rare"), ("traceId", Trace), ("orderLifecycleSequence", 5),
                ("jobId", "CJ-rollback"), ("terminal", true), ("outcome", "interrupted"),
                ("code", "cooking-mismatch-stored"), ("reasonCode", "cooking-food-mismatch-stored"),
                ("message", "黑暗料理已确认入箱，厨具已严格复位。")) });
            Check(J.Arr(engine.Advance(input, 2500)["commands"]).Count == 0
                && J.Num(State(engine)["rollbackCount"]) == 1, "既有回退通过运行时终态事件正常记账");
            command = J.Objects(engine.Advance(input, 4100)["commands"]).Single();
            engine.Complete((long)J.Num(command["requestId"]), Response("waiting", "participation", Reasons[2]), 4200);
            Check(!J.Bool(State(engine)["paused"]) && J.Num(State(engine)["rollbackCount"]) == 1
                && J.Num(State(engine)["lastRuntimeEventSequence"]) == 1,
                "集合等待保留非零回退预算及已处理事件游标");
            command = J.Objects(engine.Advance(input, 6000)["commands"]).Single();
            Check(J.Num(State(engine)["rollbackCount"]) == 1
                && J.Str(command["action"]) == "prepare-rare", "等待恢复仍使用原预算，不重复消费已处理终态事件");
        }

        // 一批中的首单结算导致集合变更，后单准入暂等。首单成功与后单已有进度必须分别保留。
        {
            var input = Input(rareInput);
            var first = J.Objects(input["snapshot"]?["nightBusiness"]?["orders"]).Single();
            first["hasServedFood"] = true;
            first["hasServedBeverage"] = true;
            MirrorOrder(input, first);
            var second = J.Obj(J.Clone(first));
            second["traceId"] = "R-0026"; second["orderLifecycleSequence"] = 6; second["deskCode"] = 1;
            J.Arr(input["snapshot"]?["nightBusiness"]?["orders"]).Add(second);
            var secondRecommendation = J.Obj(J.Clone(J.Objects(input["recommendations"]?["recommendations"]).Single()));
            secondRecommendation["order"] = J.Clone(second);
            J.Arr(input["recommendations"]?["recommendations"]).Add(secondRecommendation);
            J.Arr(input["snapshot"]?["rareGuestParticipation"]?["entries"]).Add(J.Object(
                ("traceId", "R-0026"), ("orderLifecycleSequence", 6), ("guestId", 10), ("managed", true),
                ("participating", true), ("queuePosition", 2)));
            var engine = new AutomationCoordinator();
            var commands = J.Objects(engine.Advance(input, 1500)["commands"]).ToArray();
            Check(commands.Length == 2, "同批夹具产生两笔独立完成命令");
            var firstCommand = commands.Single(x => J.Str(x["payload"]?["traceId"]) == Trace);
            var secondCommand = commands.Single(x => J.Str(x["payload"]?["traceId"]) == "R-0026");
            var complete = Response("completed", "order", ""); complete["ok"] = true; complete["completedOrder"] = true;
            engine.Complete((long)J.Num(firstCommand["requestId"]), complete, 1600);
            engine.Complete((long)J.Num(secondCommand["requestId"]), Response("waiting", "participation", Reasons[2]), 1601);
            Check(J.Bool(State(engine)["completed"]), "后单集合等待不能撤销首单成功结算事实");
            var secondState = J.Obj(engine.Snapshot()["states"]?["rare:trace:R-0026|lifecycle:6"]);
            Check(!J.Bool(secondState["paused"]) && J.Bool(secondState["prepared"])
                && J.Bool(secondState["foodDelivered"]) && J.Bool(secondState["beverageHandled"]),
                "同批后单等待保留全部送达事实");
            J.Arr(input["snapshot"]?["nightBusiness"]?["orders"]).RemoveAt(0);
            var entries = J.Arr(input["snapshot"]?["rareGuestParticipation"]?["entries"]);
            entries.RemoveAt(0); J.Obj(entries[0])["queuePosition"] = 1;
            J.Arr(input["recommendations"]?["recommendations"]).RemoveAt(0);
            var resumed = J.Objects(engine.Advance(input, 3100)["commands"]).Single();
            Check(J.Str(resumed["payload"]?["traceId"]) == "R-0026"
                && !J.Bool(resumed["payload"]?["autoStartCooking"]) && !J.Bool(resumed["payload"]?["autoTakeBeverage"]),
                "结算后完整集合重新对齐，只完成后单，不重复送餐或开锅");
        }

        // 明确暂停、失效身份和原有blocked/fatal结果不可借相同参与阶段自动恢复。
        foreach (var reason in new[] { "order-paused", "current-order-not-found", "invalid-order-identity",
                     "business-generation-mismatch", "participation-source-incomplete" })
        foreach (var outcome in new[] { "blocked", "fatal" })
        {
            var input = Input(rareInput); var engine = new AutomationCoordinator();
            var command = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
            engine.Complete((long)J.Num(command["requestId"]), Response(outcome, "participation", reason), 1600);
            Check(J.Bool(State(engine)["paused"]) && J.Str(State(engine)["pauseReasonCode"]) == reason
                && J.Arr(engine.Advance(input, 60000)["commands"]).Count == 0,
                "非等待拒绝继续暂停：" + outcome + "/" + reason);
        }
        foreach (var manualCode in new[] { "cooking-start-unowned", "food-delivery-commit-uncertain", "order-evaluation-commit-uncertain" })
        {
            var input = Input(rareInput); var engine = new AutomationCoordinator();
            var command = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
            var response = Response("waiting", "participation", Reasons[0]);
            J.Obj(J.Arr(response["steps"])[0])["code"] = manualCode;
            engine.Complete((long)J.Num(command["requestId"]), response, 1600);
            Check(J.Bool(State(engine)["manualResolutionRequired"]) && J.Bool(State(engine)["paused"])
                && !engine.Retry("rare", "trace:R-0025|lifecycle:5", 1700),
                "携带副作用不确定证据的响应不能伪装为无动作等待：" + manualCode);
        }
        Console.WriteLine($"PASS rare participation admission wait: {count} assertions; pre-action deferral preserves progress, budgets and barriers.");
    }

    private static JsonObject State(AutomationCoordinator engine) => J.Obj(engine.Snapshot()["states"]?[Key]);

    /// <summary>使用生产参与投影认可的完整集合，所有身份为合成值。</summary>
    private static JsonObject Input(Func<JsonObject> rareInput)
    {
        var input = rareInput();
        J.Obj(input["preferences"])["rareGuestParticipationModuleEnabled"] = true;
        J.Obj(input["preferences"])["managedRareGuestIds"] = new JsonArray(10);
        J.Obj(input["preferences"])["autoPrepStopOnError"] = true;
        var snapshot = J.Obj(input["snapshot"]);
        J.Obj(snapshot["nightBusiness"])["ordersReadComplete"] = true;
        var order = J.Objects(snapshot["nightBusiness"]?["orders"]).Single(); order["traceId"] = Trace;
        MirrorOrder(input, order);
        snapshot["rareGuestParticipation"] = J.Object(("active", true), ("businessGeneration", 1),
            ("participationRevision", 1), ("managedGuestIds", new JsonArray(10)), ("entries", J.Array(new[]
            {
                J.Object(("traceId", Trace), ("orderLifecycleSequence", 5), ("guestId", 10),
                    ("managed", true), ("participating", true), ("queuePosition", 1)),
            })));
        return input;
    }

    private static void MirrorOrder(JsonObject input, JsonObject order) =>
        J.Objects(input["recommendations"]?["recommendations"]).First()["order"] = J.Clone(order);

    private static JsonObject Response(string outcome, string stage, string reason) => J.Object(
        ("ok", false), ("prepared", false), ("servedFood", false), ("servedBeverage", false), ("completedOrder", false),
        ("error", "稀客参与检查尚未同步，未修改游戏状态。"), ("automation", J.Object(("outcome", outcome),
            ("stage", stage), ("reasonCode", reason), ("retryAfterMs", 500), ("jobId", ""))),
        ("steps", J.Array(new[] { J.Object(("code", reason), ("ok", false), ("skipped", false),
            ("message", "稀客参与检查尚未同步，未修改游戏状态。")) })));
}
