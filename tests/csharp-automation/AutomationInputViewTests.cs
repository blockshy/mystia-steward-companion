using System.Diagnostics;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application.Automation;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 使用生产协调器验证冻结组件入口：公开 JSON 复制契约、跨回调所有权、旧许可失效和大结果命中分配。
/// 夹具全部为独立托管值，不加载游戏、不调用网络、不触发强制 GC，也不把分配量解释为实机帧率。
/// </summary>
internal static class AutomationInputViewTests
{
    internal static void Run(Action<bool, string> check, Func<JsonObject> normalInput, Func<JsonObject> rareInput)
    {
        // 公开入口仍复制全部输入。调用方在返回后改配置、目录、订单和推荐，不能改变已经接受的状态。
        var mutable = rareInput();
        var publicEngine = new AutomationCoordinator();
        publicEngine.Advance(mutable, 1500);
        var ownedStatus = publicEngine.Snapshot().ToJsonString();
        J.Obj(mutable["authority"])["leaseOwned"] = false;
        J.Obj(mutable["preferences"])["automationEnabled"] = false;
        J.Obj(mutable["data"])["recipes"] = new JsonArray();
        J.Obj(mutable["snapshot"])["nightBusiness"] = new JsonObject();
        J.Obj(mutable["recommendations"])["recommendations"] = new JsonArray();
        check(publicEngine.Snapshot().ToJsonString() == ownedStatus, "公开 Advance 保留深复制所有权，外部后续修改不渗入状态");

        // 同一永久只读输入同时供两个独立协调器消费。比较完整输出，而不是只比较命令数量。
        foreach (var input in new[] { normalInput(), rareInput() })
        {
            var before = input.ToJsonString();
            var copied = new AutomationCoordinator();
            var frozen = new AutomationCoordinator();
            var view = View(input);
            var copiedBatch = copied.Advance(input, 1500);
            var frozenBatch = frozen.AdvanceFrozen(view, 1500);
            check(copiedBatch.ToJsonString() == frozenBatch.ToJsonString(), "冻结入口的首次完整状态和命令与复制入口相等");
            var command = J.Objects(frozenBatch["commands"]).Single();
            var id = (long)J.Num(command["requestId"]);

            // 对外命令仍是副本；修改返回的料理和加料不能污染锁定目标或 PendingCommand。
            var stableStatus = frozen.Snapshot().ToJsonString();
            J.Obj(command["payload"])["recipeId"] = 999999;
            J.Obj(command["payload"])["extraIngredientIds"] = new JsonArray(999999);
            check(frozen.Snapshot().ToJsonString() == stableStatus, "冻结输入不改变对外命令的独立副本契约");

            // 模拟主线程响应从另一 CLR 线程返回；输入视图在 Advance 返回后仍可被安全读取。
            var response = Response("progressed", "beverage", "beverage-delivered");
            Task.Run(() => frozen.Complete(id, response, 1600)).GetAwaiter().GetResult();
            copied.Complete(id, response, 1600);
            check(copied.Snapshot().ToJsonString() == frozen.Snapshot().ToJsonString(), "跨线程 Complete 使用保留视图，结果与复制入口一致");
            foreach (var now in new long[] { 1601, 2401, 3201 })
                check(copied.Advance(input, now).ToJsonString() == frozen.AdvanceFrozen(view, now).ToJsonString(),
                    "缓存命中仍推进真实调度时钟和续接，完整输出不变");
            check(input.ToJsonString() == before, "调度、诊断和回调都不写入冻结组件及其后代");
            var rejectedClock = false;
            try { frozen.AdvanceFrozen(view, 3100); }
            catch (ArgumentOutOfRangeException) { rejectedClock = true; }
            check(rejectedClock, "冻结入口同样拒绝倒退时间");
        }

        // 新的输入只能新建后整体替换。测试明确不在发布后的对象上修改 authority，避免错误演示借用规则。
        foreach (var change in new[] { "epoch", "generation", "lease", "identity" })
        {
            var first = rareInput();
            var next = J.Obj(J.Clone(first));
            if (change == "epoch") J.Obj(next["authority"])["automationEpoch"] = 2;
            if (change == "generation")
            {
                J.Obj(next["authority"])["businessGeneration"] = 2;
                J.Obj(next["snapshot"])["nightBusinessGeneration"] = 2;
            }
            if (change == "lease") J.Obj(next["authority"])["leaseOwned"] = false;
            if (change == "identity")
            {
                var orders = J.Arr(J.Obj(J.Obj(next["snapshot"])["nightBusiness"])["orders"]);
                orders.Add(J.Clone(orders[0]));
            }
            var copied = new AutomationCoordinator();
            var frozen = new AutomationCoordinator();
            var old = J.Objects(copied.Advance(first, 1500)["commands"]).Single();
            frozen.AdvanceFrozen(View(first), 1500);
            check(copied.Advance(next, 1600).ToJsonString() == frozen.AdvanceFrozen(View(next), 1600).ToJsonString(),
                "冻结入口完整保留新输入门禁：" + change);
            var priorStatus = frozen.Snapshot().ToJsonString();
            var oldId = (long)J.Num(old["requestId"]);
            var late = Response("progressed", "cooking-start", "cooking-started");
            copied.Complete(oldId, late, 1650);
            frozen.Complete(oldId, late, 1650);
            check(copied.Snapshot().ToJsonString() == frozen.Snapshot().ToJsonString(), "迟到旧命令处理保持原有作用域规则：" + change);
            if (change != "identity")
                check(frozen.Snapshot().ToJsonString() == priorStatus, "旧许可的迟到回包不能修改新轮次：" + change);
        }

        // 状态标量不因移除 JSON 中间壳而丢失。缺失/过期/错误都不能凭有推荐数组取得执行资格。
        foreach (var unavailable in new[] { "stale", "pending", "error", "missing" })
        {
            var input = rareInput();
            var recommendations = J.Obj(input["recommendations"]);
            if (unavailable == "stale") recommendations["isCurrent"] = false;
            if (unavailable == "pending") recommendations["pending"] = true;
            if (unavailable == "error") recommendations["error"] = "离线夹具错误";
            if (unavailable == "missing") input.Remove("recommendations");
            var copied = new AutomationCoordinator().Advance(input, 1500);
            var frozen = new AutomationCoordinator().AdvanceFrozen(View(input), 1500);
            check(copied.ToJsonString() == frozen.ToJsonString() && J.Arr(frozen["commands"]).Count == 0,
                "冻结结果状态不绕过推荐门禁：" + unavailable);
        }

        // 普通订单的特殊经营目标有独立的就绪状态，不能错误地沿用稀客推荐的状态或空目标回退。
        foreach (var availability in new[] { "ready", "stale", "pending", "error", "missing", "duplicate" })
        {
            var input = normalInput();
            J.Obj(input["snapshot"])["specialBusiness"] = JsonNode.Parse("""
                {"active":true,"challengeTypeAvailable":true,"challengeType":"Story_WackyCookingCompetition","phase":"phase1","foodTargetTags":[]}
                """);
            J.Objects(J.Obj(J.Obj(input["snapshot"])["normalBusiness"])["orders"]).Single()["specialBusinessRole"] = "wacky-target-order";
            var targets = J.Obj(input["normalExecutionTargets"]);
            targets["targets"] = JsonNode.Parse("""
                [{"orderKey":"N1|lifecycle:1","message":"","target":{"matchFoodId":100,"matchBeverageId":1,"foodId":101,"recipeId":201,"recipeName":"替换料理","cookerName":"煮锅","extraIngredientIds":[3,3],"beverageId":2,"beverageName":"替换酒水"}}]
                """);
            if (availability == "stale") targets["isCurrent"] = false;
            if (availability == "pending") targets["pending"] = true;
            if (availability == "error") targets["error"] = "离线夹具目标错误";
            if (availability == "missing") input.Remove("normalExecutionTargets");
            if (availability == "duplicate") J.Arr(targets["targets"]).Add(J.Clone(J.Arr(targets["targets"])[0]));
            var copied = new AutomationCoordinator().Advance(input, 1500);
            var frozen = new AutomationCoordinator().AdvanceFrozen(View(input), 1500);
            check(copied.ToJsonString() == frozen.ToJsonString(), "普通特殊目标完整状态对照：" + availability);
            check(J.Arr(frozen["commands"]).Count == (availability == "ready" ? 1 : 0),
                "普通特殊目标独立执行门禁：" + availability);
        }

        MeasureRepeatedHits(check, rareInput);
    }

    /// <summary>
    /// 构造八订单、每单八十方案的完整保留结果，模拟缓存已经命中的宿主。只测 Advance 本身，
    /// 不把夹具构造、输入序列化或强制收集混入分配统计；两入口完整结果必须保持相同。
    /// </summary>
    private static void MeasureRepeatedHits(Action<bool, string> check, Func<JsonObject> rareInput)
    {
        var input = rareInput();
        var rows = J.Arr(J.Obj(input["recommendations"])["recommendations"]);
        var template = J.Obj(rows[0]);
        var plan = J.Clone(J.Arr(template["executionPlans"])[0]);
        var recommendations = new JsonArray();
        var orders = new JsonArray();
        for (var index = 0; index < 8; index++)
        {
            var row = J.Obj(J.Clone(template));
            var order = J.Obj(row["order"]);
            order["traceId"] = "R-" + (index + 1);
            order["deskCode"] = index;
            var plans = new JsonArray();
            for (var candidate = 0; candidate < 80; candidate++) plans.Add(J.Clone(plan));
            row["executionPlans"] = plans;
            recommendations.Add(row);
            orders.Add(J.Clone(order));
        }
        J.Obj(input["recommendations"])["recommendations"] = recommendations;
        J.Obj(J.Obj(input["snapshot"])["nightBusiness"])["orders"] = orders;
        var before = input.ToJsonString();
        var copied = new AutomationCoordinator();
        var frozen = new AutomationCoordinator();
        var view = View(input);
        for (var index = 0; index < 4; index++)
            check(copied.Advance(input, 1500 + index).ToJsonString() == frozen.AdvanceFrozen(view, 1500 + index).ToJsonString(),
                "大结果冻结入口预热完整对照");

        const int iterations = 24;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < iterations; index++) copied.Advance(input, 2000 + index * 200);
        watch.Stop();
        var copiedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var copiedMs = watch.Elapsed.TotalMilliseconds;
        allocated = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (var index = 0; index < iterations; index++) frozen.AdvanceFrozen(view, 2000 + index * 200);
        watch.Stop();
        var frozenBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check(copied.Snapshot().ToJsonString() == frozen.Snapshot().ToJsonString(), "二十四轮命中后完整协调器状态一致");
        check(input.ToJsonString() == before, "大结果命中循环未修改原输入");
        // 只断言数量级收益，避免将硬件耗时或特定 CLR 小对象大小锁进测试；两者都保留所有诊断输出。
        check(frozenBytes * 4 < copiedBytes, "冻结组件入口移除大方案树复制，累计分配至少下降四倍");
        Console.WriteLine(J.Object(("probe", "automation-frozen-input"), ("orders", 8), ("plansPerOrder", 80),
            ("iterations", iterations), ("copiedBytes", copiedBytes), ("frozenBytes", frozenBytes),
            ("copiedMs", copiedMs), ("frozenMs", watch.Elapsed.TotalMilliseconds)).ToJsonString());
    }

    /// <summary>此方法只在离线测试中对构造完成后永久只读的输入建立视图，不在生产中暴露 JSON 借用捷径。</summary>
    private static AutomationInputView View(JsonObject input)
    {
        // 夹具的 JSON 同样可能仍是 .NET 6 的懒展开节点。独占时递归枚举一次，遵守生产冻结契约；
        // 分配测量在创建视图之后开始，不能把首次物化反复算进缓存命中轮次。
        Materialize(input);
        var recommendations = J.Obj(input["recommendations"]);
        var targets = J.Obj(input["normalExecutionTargets"]);
        return AutomationInputView.FromFrozenComponents(J.Obj(input["snapshot"]), J.Obj(input["preferences"]),
            J.Obj(input["data"]), J.Obj(input["favorites"]), J.Obj(input["authority"]),
            new(recommendations["recommendations"] as JsonArray, J.Bool(recommendations["isCurrent"]),
                J.Bool(recommendations["pending"]), J.Str(recommendations["error"])),
            new(targets["targets"] as JsonArray, J.Bool(targets["isCurrent"]), J.Bool(targets["pending"]), J.Str(targets["error"])));
    }

    private static void Materialize(JsonNode? node)
    {
        if (node is JsonObject value)
            foreach (var field in value) Materialize(field.Value);
        else if (node is JsonArray values)
            foreach (var item in values) Materialize(item);
    }

    private static JsonObject Response(string outcome, string stage, string code) => J.Object(
        ("ok", true), ("automation", J.Object(("outcome", outcome), ("stage", stage), ("reasonCode", ""),
            ("retryAfterMs", 0), ("jobId", ""))),
        ("steps", J.Array(new[] { J.Object(("code", code), ("ok", true), ("skipped", false), ("message", code)) })));
}
