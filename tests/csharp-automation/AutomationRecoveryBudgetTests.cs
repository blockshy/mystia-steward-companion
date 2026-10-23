using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application.Automation;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 通过生产协调器复现两次独立锅次的成品不符事件。只提供已经确认入箱、释放锅次的
/// 结构化事实，不伪造 Unity 调用；恢复必须同时满足精确事件身份、预算与当前执行许可。
/// </summary>
internal static class AutomationRecoveryBudgetTests
{
    private const string OrderKey = "N-0043|lifecycle:53";
    private const string StateKey = "normal:" + OrderKey;

    internal static void Run(Action<bool, string> check, Func<JsonObject> normalInput, Func<JsonObject> rareInput)
    {
        var input = Input(normalInput);
        var engine = new AutomationCoordinator();
        var first = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
        CompleteStart(engine, first, "CJ-000053", 1600);
        check(J.Bool(State(engine)["prepared"]) && J.Str(State(engine)["detailMessage"]).Contains("制作中"),
            "恢复夹具先具有真实已开锅响应及旧制作进度");

        Events(input).Add(StoredEvent(1, "CJ-000053"));
        var batch = engine.Advance(input, 2500);
        var state = State(engine);
        check(J.Num(state["rollbackCount"]) == 1 && !J.Bool(state["paused"])
            && !J.Bool(state["prepared"]) && J.Str(state["cookingJobId"]).Length == 0,
            "第一次已确认安全入箱释放精确锅次，消耗一次预算但不暂停");
        check(J.Arr(batch["commands"]).Count == 0 && J.Num(state["nextAttemptAtMs"]) == 3000,
            "安全回退仍遵守原有调度冷却，不在事件回填时直接重复开锅");
        check(J.Str(state["detailMessage"]).Contains("已放入保温箱")
            && J.Str(state["detailMessage"]).Contains("重新调度")
            && !J.Str(state["detailMessage"]).Contains("制作中"),
            "终态事件立即取代上一锅制作中详情");
        engine.Advance(input, 2800);
        check(J.Num(State(engine)["rollbackCount"]) == 1, "重复快照不会重复消耗同一终态事件的预算");

        var second = J.Objects(engine.Advance(input, 3300)["commands"]).Single();
        var payload = J.Obj(second["payload"]);
        check(J.Str(payload["traceId"]) == "N-0043" && J.Num(payload["orderLifecycleSequence"]) == 53
            && J.Num(payload["foodId"]) == 11 && J.Num(payload["recipeId"]) == 10
            && J.Num(payload["cookerControllerIndex"], -1) >= 0,
            "首次回退后以同一精确订单和配方重新受检预约，不猜目标或厨具");
        CompleteStart(engine, second, "CJ-000057", 3400);
        Events(input).Add(StoredEvent(2, "CJ-000057"));
        batch = engine.Advance(input, 4000);
        state = State(engine);
        check(J.Num(state["rollbackCount"]) == 2 && J.Bool(state["paused"])
            && !J.Bool(state["manualResolutionRequired"]) && !J.Bool(state["prepared"])
            && J.Str(state["pauseReasonCode"]) == "rollback-limit-reached",
            "第二个独立锅次安全回退触及既有二次上限，进入可手工重试的暂停");
        check(J.Str(state["detailMessage"]).Contains("2/2")
            && J.Str(state["detailMessage"]).Contains("不会继续自动重试")
            && J.Str(state["detailMessage"]).Contains("重试暂停订单")
            && !J.Str(state["detailMessage"]).Contains("制作中"),
            "预算暂停详情同时显示真实原因与明确恢复入口，不残留等待完成");
        var diagnostic = J.Objects(batch["normalDiagnostics"]).Single();
        check(J.Str(diagnostic["nextAction"]).Contains("预算已用尽")
            && J.Str(diagnostic["nextAction"]).Contains("重试暂停订单"),
            "状态投影明确需要用户重开预算，不暗示空闲厨具会自动恢复");
        check(J.Str(batch["message"]).Contains("均已暂停")
            && !J.Str(batch["message"]).Contains("等待订单、厨具"),
            "全体未完成订单暂停时不再显示普通调度等待摘要");
        var scheduling = J.Objects(engine.SchedulingDiagnostics()["orders"]).Single();
        check(J.Num(scheduling["rollbackCount"]) == 2 && J.Num(scheduling["retryCount"]) == 0
            && J.Str(scheduling["pauseReasonCode"]) == "rollback-limit-reached",
            "有界调度诊断保留独立预算及暂停代码，原因不依赖长错误文本尾部");

        var detailTime = J.Num(state["detailUpdatedAtMs"]);
        foreach (var now in new long[] { 4500, 6000, 60000, 300000 })
        {
            batch = engine.Advance(input, now);
            check(J.Arr(batch["commands"]).Count == 0 && J.Num(State(engine)["rollbackCount"]) == 2
                && J.Num(State(engine)["detailUpdatedAtMs"]) == detailTime,
                $"暂停后的重复快照与空闲时间 {now} 不自动重开预算，也不反复刷新说明时间");
        }

        check(!engine.Retry("normal", "N-0043|lifecycle:52", 300001), "人工重试不能作用于旧生命周期");
        check(engine.Retry("normal", OrderKey, 300002), "当前精确订单允许显式重试重新开放普通回退预算");
        state = State(engine);
        check(!J.Bool(state["paused"]) && J.Num(state["rollbackCount"]) == 0
            && J.Str(state["detailMessage"]).Contains("已手动重试"),
            "显式重试同步替换暂停说明但不伪造已开锅");
        batch = engine.Advance(input, 300003);
        check(J.Objects(batch["commands"]).SingleOrDefault() is { } retry
            && J.Num(J.Obj(retry["payload"])["foodId"]) == 11
            && J.Num(J.Obj(retry["payload"])["orderLifecycleSequence"]) == 53,
            "人工重试后仍由下一轮完整准入生成精确目标命令");

        // 与当前锅次不匹配的事件不会破坏正在制作的目标；相同 trace 并不足以授权回退。
        foreach (var boundary in new[] { "lifecycle", "job", "kind", "trace" })
        {
            input = Input(normalInput);
            engine = new AutomationCoordinator();
            first = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
            CompleteStart(engine, first, "CJ-000053", 1600);
            var foreign = StoredEvent(1, "CJ-000053");
            if (boundary == "lifecycle") foreign["orderLifecycleSequence"] = 52;
            if (boundary == "job") foreign["jobId"] = "CJ-000052";
            if (boundary == "kind") foreign["targetKind"] = "rare";
            if (boundary == "trace") foreign["traceId"] = "N-0042";
            Events(input).Add(foreign);
            engine.Advance(input, 2500);
            check(J.Num(State(engine)["rollbackCount"]) == 0 && J.Bool(State(engine)["prepared"])
                && J.Str(State(engine)["cookingJobId"]) == "CJ-000053",
                "不同精确事件身份不能清理或消耗当前锅次预算：" + boundary);
        }

        // 入箱或复位未确认属于人工安全屏障，不是可以用普通重试循环恢复的失败。
        foreach (var code in new[] { "cooking-warmer-commit-uncertain", "cooking-warmer-reset-blocked" })
        {
            input = Input(normalInput);
            engine = new AutomationCoordinator();
            first = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
            CompleteStart(engine, first, "CJ-000053", 1600);
            var uncertain = StoredEvent(1, "CJ-000053");
            uncertain["outcome"] = "blocked";
            uncertain["code"] = code;
            uncertain["reasonCode"] = code;
            uncertain["message"] = "入箱或复位结果尚未确认。";
            Events(input).Add(uncertain);
            engine.Advance(input, 2500);
            check(J.Bool(State(engine)["paused"]) && J.Bool(State(engine)["manualResolutionRequired"])
                && !engine.Retry("normal", OrderKey, 2600), "不确定副作用拒绝普通重试：" + code);
            Events(input).Add(StoredEvent(2, "CJ-000053"));
            batch = engine.Advance(input, 60000);
            check(J.Arr(batch["commands"]).Count == 0 && J.Bool(State(engine)["manualResolutionRequired"])
                && J.Num(State(engine)["rollbackCount"]) == 0
                && J.Str(State(engine)["detailMessage"]).Contains("普通重试不能代替人工确认"),
                "迟到可恢复事件不能清除人工屏障或伪造安全入箱：" + code);
            check(J.Str(batch["message"]).Contains("需要人工确认")
                && J.Str(J.Objects(batch["normalDiagnostics"]).Single()["nextAction"]).Contains("确认已处理"),
                "人工屏障摘要与下一步不误指普通重试：" + code);
        }

        // 特殊稀客目标对账可以独立消耗预算，并非所有暂停都来自运行时终态事件。
        // 该分支保留原动作拒绝语义，只要求“目标不可用”不能盖掉已经达到上限的事实。
        input = rareInput();
        J.Obj(input["preferences"])["autoMaxRollbacks"] = 1;
        J.Obj(input["snapshot"])["specialBusiness"] = J.Object(("active", true),
            ("challengeTypeAvailable", true), ("challengeType", "Story_WackyCookingCompetition"),
            ("phase", "phase1"), ("foodTargetTags", new JsonArray("肉")));
        engine = new AutomationCoordinator();
        batch = engine.Advance(input, 1500);
        var rareState = J.Obj(J.Obj(batch["states"])["rare:trace:R1|lifecycle:5"]);
        check(J.Bool(rareState["paused"]) && J.Num(rareState["rollbackCount"]) == 1,
            "特殊稀客目标无法继续时仍按原目标对账规则触及预算上限");
        check(J.Str(rareState["detailMessage"]).Contains("1/1")
            && J.Str(rareState["detailMessage"]).Contains("重试暂停订单"),
            "稀客目标错误不覆盖已暂停原因及恢复入口");
        check(J.Arr(batch["commands"]).Count == 0, "补充稀客暂停说明不改变原动作准入拒绝");
    }

    /// <summary>采用日志中的精确数值身份，其他输入均来自隔离夹具；不读取用户配置或游戏状态。</summary>
    private static JsonObject Input(Func<JsonObject> normalInput)
    {
        var input = normalInput();
        var order = J.Objects(input["snapshot"]?["normalBusiness"]?["orders"]).Single();
        order["traceId"] = "N-0043";
        order["orderKey"] = "N-0043";
        order["orderLifecycleSequence"] = 53;
        order["foodId"] = 11;
        order["foodName"] = "合成目标料理";
        order["hasServedBeverage"] = true;
        var recipe = J.Objects(input["data"]?["recipes"]).First();
        recipe["id"] = 11;
        recipe["recipeId"] = 10;
        recipe["name"] = "合成目标料理";
        return input;
    }

    private static JsonObject State(AutomationCoordinator engine) => J.Obj(engine.Snapshot()["states"]?[StateKey]);
    private static JsonArray Events(JsonObject input) => J.Arr(input["snapshot"]?["automationEvents"]);

    private static void CompleteStart(AutomationCoordinator engine, JsonObject command, string job, long now) =>
        engine.Complete((long)J.Num(command["requestId"]), J.Object(("ok", true),
            ("automation", J.Object(("outcome", "progressed"), ("stage", "cooking-start"), ("jobId", job))),
            ("steps", J.Array(new[] { J.Object(("code", "cooking-started"), ("ok", true),
                ("skipped", false), ("message", "目标料理已在制作中，等待完成。")) }))), now);

    private static JsonObject StoredEvent(long sequence, string job) => J.Object(
        ("sequence", sequence), ("targetKind", "normal"), ("traceId", "N-0043"),
        ("orderKey", "N-0043"), ("orderLifecycleSequence", 53), ("jobId", job),
        ("terminal", true), ("outcome", "interrupted"), ("code", "cooking-mismatch-stored"),
        ("reasonCode", "cooking-food-mismatch-stored"), ("foodId", 11), ("recipeId", 10), ("actualFoodId", -1),
        ("message", "黑暗料理（料理 #-1）已放入保温箱并确认提交；厨具已严格复位。"));
}
