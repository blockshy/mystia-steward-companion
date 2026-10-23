using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application.Automation;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 缺料必须在厨具预约和并发计数之前退出，只暂停相关开锅；这些用例通过生产协调器的
/// 公开输入/命令/诊断验证，不访问内部预约集合，也不伪造 Unity 调用成功。
/// </summary>
internal static class AutomationIngredientAdmissionTests
{
    internal static void Run(Action<bool, string> check, Func<JsonObject> normalInput, Func<JsonObject> rareInput)
    {
        // 队首缺料、下一张普通订单和稀客均可开锅。两个物理槽位必须分给后两者，
        // 不能被前者的真实预约或普客需求预留吞掉；并发上限仍然严格生效。
        var input = MixedInput(normalInput, rareInput, includeFeasibleNormal: true);
        var engine = new AutomationCoordinator();
        var batch = engine.Advance(input, 1500);
        var commands = J.Objects(batch["commands"]).ToArray();
        check(commands.Length == 2 && commands.Any(IsFeasibleNormal) && commands.Any(IsRare),
            "缺料队首不消耗并发与预约，后续可行普客和稀客都能调度");
        check(commands.Select(command => J.Num(J.Obj(command["payload"])["cookerControllerIndex"])).Distinct().Count() == 2,
            "缺料绕行仍保持两张订单的物理预约互斥");
        var blocked = J.Objects(J.Obj(batch["resourceOverview"])["normalBlocked"]).Single();
        check(J.Str(blocked["orderKey"]) == "N1|lifecycle:1" && J.Str(blocked["reason"]).Contains("材料不足")
            && J.Str(blocked["reason"]).Contains("#1") && J.Str(blocked["reason"]).Contains("库存 0"),
            "资源概览显示精确缺料原因，不把缺料误报成厨具不足");
        var overview = J.Objects(J.Obj(batch["resourceOverview"])["cookers"]).Single();
        check(J.Num(overview["normalReserved"]) == 1 && J.Num(overview["rareReserved"]) == 1,
            "资源概览同样跳过缺料普客，只统计可行需求");
        var blockedState = J.Obj(J.Obj(batch["states"])["normal:N1|lifecycle:1"]);
        check(!J.Bool(blockedState["paused"]) && !J.Bool(blockedState["prepared"])
            && J.Num(blockedState["retryCount"]) == 0 && J.Str(blockedState["detailMessage"]).Contains("材料不足"),
            "快照缺料仅等待补充，不伪造已开锅、重试失败或人工暂停");
        VerifySchedulingDiagnostics(check, engine);

        // 调度间隔不同：2300ms 只跑普客，3000ms 只跑稀客。即使本次 PlanNormal
        // 没有执行，BuildNormalDemand 也必须排除相同缺料单，而非阻断稀客恢复。
        input = MixedInput(normalInput, rareInput, includeFeasibleNormal: false);
        engine = new AutomationCoordinator();
        var rareCommand = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
        engine.Complete((long)J.Num(rareCommand["requestId"]), CookerWaiting(), 1600);
        check(J.Arr(engine.Advance(input, 2300)["commands"]).Count == 0, "普客间隔内缺料不提交，稀客尚在等待周期");
        batch = engine.Advance(input, 3000);
        check(J.Objects(batch["commands"]).SingleOrDefault() is { } resumed && IsRare(resumed),
            "仅稀客调度周期中缺料普客不会通过需求预留饿死稀客");
        foreach (var command in J.Objects(batch["commands"])) engine.Abandon((long)J.Num(command["requestId"]));
        Quantity(input, "1", 1);
        batch = engine.Advance(input, 4500);
        check(J.Objects(batch["commands"]).Any(command => J.Str(command["action"]) == "complete-normal"
            && J.Str(J.Obj(command["payload"])["traceId"]) == "N1"), "库存补充后原缺料订单自动恢复，无需切开关或人工重试");

        // 重复材料必须逐份核算；禁止把无限库存当缺料，也禁止把未知或异常负数当无限。
        foreach (var test in new[] { (Quantity: 1, Allowed: false), (Quantity: 2, Allowed: true), (Quantity: -1, Allowed: true) })
        {
            input = normalInput();
            Recipe(input, 100)["ingredientIds"] = new JsonArray(1, 1);
            Quantity(input, "1", test.Quantity);
            batch = new AutomationCoordinator().Advance(input, 1500);
            check((J.Arr(batch["commands"]).Count == 1) == test.Allowed, "重复基础材料数量与无限库存：" + test.Quantity);
        }
        foreach (var invalid in new[] { "missing-ids", "bad-id", "quantity-missing", "negative", "fraction", "string", "duplicate-recipe", "recipe-id-missing" })
        {
            input = normalInput();
            if (invalid == "missing-ids") Recipe(input, 100).Remove("ingredientIds");
            if (invalid == "bad-id") Recipe(input, 100)["ingredientIds"] = new JsonArray(-1);
            if (invalid == "quantity-missing") Quantities(input).Remove("1");
            if (invalid == "negative") Quantity(input, "1", -2);
            if (invalid == "fraction") Quantity(input, "1", 1.5);
            if (invalid == "string") Quantity(input, "1", "10");
            if (invalid == "duplicate-recipe") J.Arr(J.Obj(input["data"])["recipes"]).Add(J.Clone(Recipe(input, 100)));
            if (invalid == "recipe-id-missing") Recipe(input, 100).Remove("recipeId");
            batch = new AutomationCoordinator().Advance(input, 1500);
            check(J.Arr(batch["commands"]).Count == 0 && J.Arr(J.Obj(batch["resourceOverview"])["cookers"]).Count == 0
                && J.Arr(J.Obj(batch["resourceOverview"])["normalBlocked"]).Count == 1,
                "未知或歧义材料证据不能预约，诊断保留具体阻塞：" + invalid);
        }
        input = normalInput();
        Recipe(input, 100)["ingredientIds"] = new JsonArray();
        J.Obj(J.Obj(input["snapshot"])["recommendationState"]).Remove("ownedIngredientQty");
        check(J.Arr(new AutomationCoordinator().Advance(input, 1500)["commands"]).Count == 1,
            "明确空基础配方不需要库存，区别于缺失配方材料字段");
        foreach (var evidence in new[] { "zero", "available", "missing-set", "invalid-set", "not-loaded", "null-quantity" })
        {
            input = normalInput();
            var runtime = J.Obj(J.Obj(input["snapshot"])["recommendationState"]);
            Quantities(input).Remove("1");
            runtime["availableIngredientIds"] = new JsonArray(2, 3);
            if (evidence == "available") runtime["availableIngredientIds"] = new JsonArray(1, 2, 3);
            if (evidence == "missing-set") runtime.Remove("availableIngredientIds");
            if (evidence == "invalid-set") runtime["availableIngredientIds"] = new JsonArray("2");
            if (evidence == "not-loaded") J.Obj(input["snapshot"])["runtimeLoaded"] = false;
            if (evidence == "null-quantity") Quantities(input)["1"] = null;
            batch = new AutomationCoordinator().Advance(input, 1500);
            var reason = J.Str(J.Objects(J.Obj(batch["resourceOverview"])["normalBlocked"]).Single()["reason"]);
            check(J.Arr(batch["commands"]).Count == 0 && reason.Contains(evidence == "zero" ? "库存 0" : "库存尚未确认"),
                "原生省略零库存时严格区分缺料和未知：" + evidence);
        }

        // 缺料不得阻断本来就可独立完成的步骤，也不得重新核算已经准入的锅次。
        input = normalInput(); Quantity(input, "1", 0);
        J.Obj(input["preferences"])["autoNormalTakeBeverage"] = true;
        J.Obj(input["preferences"])["autoNormalCompleteOrder"] = true;
        var payload = J.Obj(J.Objects(new AutomationCoordinator().Advance(input, 1500)["commands"]).Single()["payload"]);
        check(J.Bool(payload["autoTakeBeverage"]) && !J.Bool(payload["autoStartCooking"])
            && J.Num(payload["cookerControllerIndex"]) == -1, "缺料保留独立酒水步骤，但不携带任何厨具预约");
        input = normalInput(); Quantity(input, "1", 0);
        var order = J.Objects(J.Obj(J.Obj(input["snapshot"])["normalBusiness"])["orders"]).Single();
        order["hasServedFood"] = true; order["hasServedBeverage"] = true;
        J.Obj(input["preferences"])["autoNormalCompleteOrder"] = true;
        payload = J.Obj(J.Objects(new AutomationCoordinator().Advance(input, 1500)["commands"]).Single()["payload"]);
        check(J.Bool(payload["autoCompleteOrder"]) && !J.Bool(payload["autoStartCooking"]),
            "已经送达的普通订单即使基础材料耗尽仍可结算");

        VerifySpecialTarget(check, normalInput);
        VerifyCookerWaitingRecovery(check, rareInput);
    }

    /// <summary>特殊经营必须核对锁定目标；原点单材料有货或缺货均不能替代它的实际材料判断。</summary>
    private static void VerifySpecialTarget(Action<bool, string> check, Func<JsonObject> normalInput)
    {
        var input = normalInput();
        J.Obj(input["snapshot"])["specialBusiness"] = JsonNode.Parse("""
            {"active":true,"challengeTypeAvailable":true,"challengeType":"Story_WackyCookingCompetition","phase":"phase1","foodTargetTags":[]}
            """);
        J.Objects(J.Obj(J.Obj(input["snapshot"])["normalBusiness"])["orders"]).Single()["specialBusinessRole"] = "wacky-target-order";
        J.Obj(input["normalExecutionTargets"])["targets"] = JsonNode.Parse("""
            [{"orderKey":"N1|lifecycle:1","message":"","target":{"matchFoodId":100,"matchBeverageId":1,"foodId":101,"recipeId":201,"recipeName":"替换料理","cookerName":"煮锅","extraIngredientIds":[2,2],"beverageId":1}}]
            """);
        Quantity(input, "1", 0); Quantity(input, "2", 2);
        var engine = new AutomationCoordinator();
        var batch = engine.Advance(input, 1500);
        check(J.Arr(batch["commands"]).Count == 0 && J.Str(J.Objects(J.Obj(batch["resourceOverview"])["normalBlocked"]).Single()["reason"]).Contains("需要 3 份"),
            "特殊目标基础与相同 ID 的重复加料累计，不检查原点单配方");
        Quantity(input, "2", 3);
        var command = J.Objects(engine.Advance(input, 3000)["commands"]).Single();
        check(J.Num(J.Obj(command["payload"])["foodId"]) == 101
            && J.Numbers(J.Obj(command["payload"])["extraIngredientIds"]).SequenceEqual(new[] { 2d, 2d }),
            "特殊目标补足材料后恢复，重复加料和原精确目标未被重写");
        var otherRecipe = J.Obj(J.Clone(Recipe(input, 101)));
        otherRecipe["recipeId"] = 202;
        otherRecipe["ingredientIds"] = new JsonArray(1);
        J.Arr(J.Obj(input["data"])["recipes"]).Add(otherRecipe);
        check(J.Arr(new AutomationCoordinator().Advance(input, 1500)["commands"]).Count == 1,
            "特殊目标按 foodId 与 recipeId 唯一联结，不受同成品另一配方干扰");
        var duplicate = J.Clone(J.Objects(J.Obj(input["data"])["recipes"]).Single(recipe => J.Num(recipe["recipeId"]) == 201));
        J.Arr(J.Obj(input["data"])["recipes"]).Add(duplicate);
        check(J.Arr(new AutomationCoordinator().Advance(input, 1500)["commands"]).Count == 0,
            "双 ID 同时重复的特殊配方仍拒绝，不通过第一条结果掩盖歧义");
        J.Arr(J.Obj(input["data"])["recipes"]).Remove(duplicate);
        var target = J.Obj(J.Objects(J.Obj(input["normalExecutionTargets"])["targets"]).Single()["target"]);
        target["recipeId"] = 999;
        check(J.Arr(new AutomationCoordinator().Advance(input, 1500)["commands"]).Count == 0,
            "特殊目标 foodId 与 recipeId 不一致时不得借同名料理开锅");
    }

    /// <summary>
    /// 重现原生日志 start-waiting 的真实结构化响应，随后只改变时间和当前可用槽位。
    /// 确认它不等于 cooking-delivery/pending，不会置 prepared 或需要人工恢复。
    /// </summary>
    private static void VerifyCookerWaitingRecovery(Action<bool, string> check, Func<JsonObject> rareInput)
    {
        var input = rareInput();
        J.Obj(input["preferences"])["autoPrepTakeBeverage"] = false;
        var engine = new AutomationCoordinator();
        var command = J.Objects(engine.Advance(input, 1500)["commands"]).Single();
        engine.Complete((long)J.Num(command["requestId"]), CookerWaiting(), 1600);
        var state = J.Obj(J.Obj(engine.Snapshot()["states"])["rare:trace:R1|lifecycle:5"]);
        check(!J.Bool(state["prepared"]) && !J.Bool(state["paused"]) && J.Num(state["nextAttemptAtMs"]) == 2600,
            "原生厨具等待仅有界退避，不误标已经开锅或永久暂停");
        check(J.Arr(engine.Advance(input, 2400)["commands"]).Count == 0, "厨具等待的最短重试时间仍受尊重");
        var resumed = J.Objects(engine.Advance(input, 3000)["commands"]).Single();
        check(IsRare(resumed) && J.Bool(J.Obj(resumed["payload"])["autoStartCooking"]),
            "厨具释放且没有其它需求占位后稀客恢复精确开锅请求");
        engine.Complete((long)J.Num(resumed["requestId"]), CookerWaiting(), 3100);
        J.Obj(input["authority"])["leaseOwned"] = false;
        check(J.Arr(engine.Advance(input, 4500)["commands"]).Count == 0, "缺料和厨具等待恢复都不能绕过租约撤销");
    }

    /// <summary>低频日志投影是只读旁路：读两次不能推进状态，调用方也不能反向修改预约或详情。</summary>
    private static void VerifySchedulingDiagnostics(Action<bool, string> check, AutomationCoordinator engine)
    {
        var before = engine.Snapshot().ToJsonString();
        var first = engine.SchedulingDiagnostics();
        var second = engine.SchedulingDiagnostics();
        check(before == engine.Snapshot().ToJsonString() && first.ToJsonString() == second.ToJsonString(),
            "调度诊断读取不运行规划、不推进时钟，也不改变完整 Snapshot");
        check(J.Objects(first["availableSlots"]).All(slot => J.Strings(slot["types"]).Contains("煮锅"))
            && J.Objects(first["orders"]).Any(order => J.Str(order["traceId"]) == "N1" && J.Str(order["detail"]).Contains("材料不足")),
            "调度诊断保留真实 supportedKeys 与缺料 detailMessage");
        first["authorized"] = false;
        J.Objects(first["availableSlots"]).First()["types"] = new JsonArray("伪造类型");
        J.Objects(first["orders"]).First()["detail"] = "外部修改";
        check(before == engine.Snapshot().ToJsonString() && second.ToJsonString() == engine.SchedulingDiagnostics().ToJsonString(),
            "返回调度诊断树的外部修改不能污染内部状态或后续日志投影");
    }

    private static JsonObject MixedInput(Func<JsonObject> normalInput, Func<JsonObject> rareInput, bool includeFeasibleNormal)
    {
        var input = rareInput();
        var preferences = J.Obj(input["preferences"]);
        preferences["autoNormalOrderEnabled"] = true;
        preferences["autoPrepTakeBeverage"] = false;
        Quantity(input, "1", 0);
        var row = J.Objects(J.Obj(input["recommendations"])["recommendations"]).Single();
        var food = J.Obj(J.Objects(row["executionPlans"]).Single()["food"]);
        food["recipe"] = J.Clone(Recipe(input, 101));
        if (includeFeasibleNormal)
        {
            var order = J.Obj(J.Clone(J.Objects(J.Obj(J.Obj(normalInput()["snapshot"])["normalBusiness"])["orders"]).Single()));
            order["traceId"] = "N2"; order["orderKey"] = "N2"; order["orderLifecycleSequence"] = 2;
            order["foodId"] = 101; order["foodName"] = "替换料理";
            J.Arr(J.Obj(J.Obj(input["snapshot"])["normalBusiness"])["orders"]).Add(order);
        }
        return input;
    }

    private static JsonObject CookerWaiting() => J.Object(("ok", true),
        ("automation", J.Object(("outcome", "waiting"), ("stage", "cooking-start"), ("reasonCode", "cooking-cooker-waiting"), ("retryAfterMs", 1000), ("jobId", ""))),
        ("steps", J.Array(new[] { J.Object(("code", "cooking-cooker-waiting"), ("ok", true), ("skipped", true), ("message", "等待精确预约厨具释放。")) })));
    private static bool IsFeasibleNormal(JsonObject command) => J.Str(command["action"]) == "complete-normal" && J.Str(J.Obj(command["payload"])["traceId"]) == "N2";
    private static bool IsRare(JsonObject command) => J.Str(command["action"]) == "prepare-rare";
    private static JsonObject Recipe(JsonObject input, int foodId) => J.Objects(J.Obj(input["data"])["recipes"]).Single(item => J.Num(item["id"]) == foodId);
    private static JsonObject Quantities(JsonObject input) => J.Obj(J.Obj(J.Obj(input["snapshot"])["recommendationState"])["ownedIngredientQty"]);
    private static void Quantity(JsonObject input, string id, int quantity) => Quantities(input)[id] = quantity;
    private static void Quantity(JsonObject input, string id, double quantity) => Quantities(input)[id] = quantity;
    private static void Quantity(JsonObject input, string id, string quantity) => Quantities(input)[id] = quantity;
}
