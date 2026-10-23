using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application.Automation;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 将真实宿主合并后的执行许可与原始现场事实同时交给生产协调器，验证未执行原因的展示优先级。
/// 每个拒绝场景仍检查实际命令为空；状态说明不能替代权限、经营代次或人工确认的校验。
/// </summary>
internal static class AutomationAvailabilityMessageTests
{
    internal static void Run(Action<bool, string> check, Func<JsonObject> normalInput)
    {
        JsonObject Input()
        {
            var input = normalInput();
            var snapshot = J.Obj(input["snapshot"]);
            snapshot["nightBusinessLifecyclePhase"] = "Active";
            snapshot["activeSceneName"] = "Night";
            return input;
        }

        // allowed=false 可能来自多个宿主条件；故意同时撤销租约，验证已知场景原因优先，
        // 而不是像旧逻辑一样统一误报为“等待控制权”。未知状态不能套用已知原因。
        var scenarios = new (string Name, Action<JsonObject> Change, string Expected)[]
        {
            ("日间", value => Scene(value, "Inactive", "Day"), "当前为日间，自动化等待夜间营业。"),
            ("主菜单", value => Scene(value, "Inactive", "Main"), "尚未开始夜间营业，自动化正在待命。"),
            ("营业结束", value => Scene(value, "Closing", "Night"), "夜间营业正在结束，自动化已停止。"),
            ("营业已销毁", value => Scene(value, "Destroyed", "Night"), "本次夜间营业已结束，等待下一次营业。"),
            ("回到日间", value => Scene(value, "Destroyed", "Day"), "当前为日间，自动化等待夜间营业。"),
            ("配置关闭优先", value => { Scene(value, "Inactive", "Day"); J.Obj(value["preferences"])["automationEnabled"] = false; }, "自动化已关闭。"),
            ("配置缺失", value => J.Obj(value["preferences"]).Remove("automationEnabled"), "等待自动化配置同步。"),
            ("配置类型不合法", value => J.Obj(value["preferences"])["automationEnabled"] = "true", "等待自动化配置同步。"),
            ("本次营业含教学", value => Block(value, "night-business-tutorial-active"), "本次夜间营业包含教学，自动化保持暂停；请手动完成本次营业。"),
            ("教学尚未确认", value => Block(value, "night-business-tutorial-state-unavailable"), "尚未确认夜间教学状态，自动化已暂停。"),
            ("生命周期尚未同步", value => Block(value, "night-business-lifecycle-unavailable"), "等待当前夜间营业状态同步。"),
            ("未知阻止原因", value => Block(value, "unknown-private-value"), "当前营业尚未满足自动化执行条件，等待现场状态确认。"),
            ("缺失生命周期", value => J.Obj(value["snapshot"]).Remove("nightBusinessLifecyclePhase"), "等待夜间营业状态确认。"),
            ("未知生命周期", value => Scene(value, "UnexpectedPhase", "Night"), "等待夜间营业状态确认。"),
            ("经营代次不一致", value => J.Obj(value["snapshot"])["nightBusinessGeneration"] = 2, "等待当前夜间营业状态同步。"),
            ("现场未读取", value => J.Obj(value["snapshot"])["runtimeLoaded"] = false, "等待当前游戏现场状态读取完成。"),
            ("其他综合执行条件未就绪", _ => { }, "等待游戏状态、主设备连接与自动化配置就绪。"),
        };
        foreach (var scenario in scenarios)
        {
            var input = Input();
            var authority = J.Obj(input["authority"]);
            authority["allowed"] = false;
            authority["leaseOwned"] = false;
            scenario.Change(input);
            var before = input.ToJsonString();
            var batch = new AutomationCoordinator().Advance(input, 1500);
            check(J.Str(batch["message"]) == scenario.Expected, "拒绝原因有精确现场依据：" + scenario.Name);
            check(!J.Bool(batch["runtimeEnabled"]) && !J.Bool(batch["leaseOwned"])
                && J.Arr(batch["commands"]).Count == 0 && J.Num(batch["inFlightCount"]) == 0
                && input.ToJsonString() == before,
                "状态说明不生成命令、不改变输入或执行权限：" + scenario.Name);
        }

        // 下述现场门禁只关闭执行，不伪造租约丢失。必须保留 leaseOwned=true 的真实授权事实，
        // 以免客户端把教学或配置关闭误认为需要重新切主设备。
        foreach (var mode in new[] { "tutorial", "disabled", "generation" })
        {
            var input = Input();
            if (mode == "tutorial") Block(input, "night-business-tutorial-active");
            if (mode == "disabled") J.Obj(input["preferences"])["automationEnabled"] = false;
            if (mode == "generation") J.Obj(input["snapshot"])["nightBusinessGeneration"] = 2;
            var batch = new AutomationCoordinator().Advance(input, 1500);
            check(J.Bool(batch["leaseOwned"]) && !J.Bool(batch["runtimeEnabled"])
                && J.Arr(batch["commands"]).Count == 0, "现场暂停保留独立授权事实：" + mode);
        }

        // 对账自身发现的订单身份冲突有更准确的失败原因，不能被通用禁用说明覆盖。
        var duplicate = Input();
        var orders = J.Arr(J.Obj(J.Obj(duplicate["snapshot"])["normalBusiness"])["orders"]);
        orders.Add(J.Clone(orders[0]));
        var conflict = new AutomationCoordinator().Advance(duplicate, 1500);
        check(J.Str(conflict["message"]) == "当前快照中存在重复订单实例，自动化已停止本轮准入。"
            && !J.Bool(conflict["runtimeEnabled"]) && J.Arr(conflict["commands"]).Count == 0,
            "精确订单身份冲突保留原失败说明和拒绝结果");

        // 真实调度跨越营业结束：旧命令已经发出，场景退出必须照旧失效它；改善说明不能
        // 让迟到的成功响应恢复 prepared，也不能让新营业仅凭状态文字获得执行资格。
        var engine = new AutomationCoordinator();
        var live = Input();
        var command = J.Objects(engine.Advance(live, 1500)["commands"]).Single();
        check(J.Num(command["requestId"]) > 0, "当前许可完整时仍实际生成受检命令");
        var day = Input();
        Scene(day, "Inactive", "Day");
        J.Obj(day["snapshot"])["nightBusinessGeneration"] = 0;
        J.Obj(day["authority"])["businessGeneration"] = 0;
        J.Obj(day["authority"])["allowed"] = false;
        J.Obj(day["authority"])["leaseOwned"] = false;
        var idle = engine.Advance(day, 1600);
        check(J.Str(idle["message"]) == "当前为日间，自动化等待夜间营业。"
            && J.Num(idle["inFlightCount"]) == 0, "回日间立即撤销原待执行命令并显示真实原因");
        var beforeLate = engine.Snapshot().ToJsonString();
        engine.Complete((long)J.Num(command["requestId"]), J.Object(("ok", true),
            ("automation", J.Object(("outcome", "progressed"), ("stage", "cooking-start"),
                ("reasonCode", ""), ("retryAfterMs", 0), ("jobId", "CJ-test"))),
            ("steps", J.Array(new[] { J.Object(("code", "cooking-started"), ("ok", true),
                ("skipped", false), ("message", "已开始")) }))), 1700);
        check(engine.Snapshot().ToJsonString() == beforeLate, "营业结束后的旧成功响应不能复活订单或改写待命状态");
        var next = Input();
        J.Obj(next["authority"])["businessGeneration"] = 2;
        J.Obj(next["snapshot"])["nightBusinessGeneration"] = 2;
        J.Obj(next["authority"])["leaseOwned"] = false;
        check(J.Arr(engine.Advance(next, 2000)["commands"]).Count == 0, "新营业缺当前租约仍不能执行");
        J.Obj(next["authority"])["leaseOwned"] = true;
        check(J.Arr(engine.Advance(next, 3000)["commands"]).Count == 1, "新营业获得完整当前许可后正常恢复调度");
    }

    /// <summary>仅构造离线事实；不从任意场景字符串推导生产权限。</summary>
    private static void Scene(JsonObject input, string phase, string scene)
    {
        var snapshot = J.Obj(input["snapshot"]);
        snapshot["nightBusinessLifecyclePhase"] = phase;
        snapshot["activeSceneName"] = scene;
    }

    /// <summary>阻止代码与允许标志同时设置，复现原生教学检测实际投影。</summary>
    private static void Block(JsonObject input, string reason)
    {
        var snapshot = J.Obj(input["snapshot"]);
        snapshot["nightBusinessAutomationAllowed"] = false;
        snapshot["nightBusinessAutomationBlockReason"] = reason;
    }
}
