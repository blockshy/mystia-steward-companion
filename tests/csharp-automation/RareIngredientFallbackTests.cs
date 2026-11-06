using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application.Automation;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// R-0019 的阶段顺序回放：等待厨具→主方案更换加料→原加料耗尽。数值均为合成夹具，
/// 不把日志中没有记录的初始库存伪装成现场事实；验证生产编排器而不提交游戏动作。
/// </summary>
internal static class RareIngredientFallbackTests
{
    internal static void Run(Action<bool, string> check, Func<JsonObject> createInput)
    {
        var input = Fixture(createInput);
        var engine = new AutomationCoordinator();
        var first = Command(engine.Advance(input, 1500));
        check(J.Numbers(J.Obj(first["payload"])["extraIngredientIds"]).SequenceEqual(new[] { 1d }), "夹具首次明确选择旧猪肉加料。");
        engine.Complete((long)J.Num(first["requestId"]), Wait("cooking-cooker-waiting"), 1600);
        PrimaryFood(input)["extraIngredients"] = new JsonArray(J.Object(("id", 3)));
        Quantity(input, "1", 0);
        var next = Command(engine.Advance(input, 3000));
        check(J.Numbers(J.Obj(next["payload"])["extraIngredientIds"]).SequenceEqual(new[] { 3d }),
            "等待厨具时锁存的普通加料必须跟随当前唯一主方案，不能继续请求已耗尽的旧猪肉。");
        check(J.Num(State(engine)["rollbackCount"]) == 0 && J.Num(State(engine)["retryCount"]) == 0,
            "未开锅的目标更新不是回滚，不消耗失败或回退预算。");
        engine.Abandon((long)J.Num(next["requestId"]));

        // 当推荐自身尚未换料时，只等待库存，不占用唯一煮锅；更后的可行订单照常调度。
        input = Fixture(createInput); Quantity(input, "1", 0);
        var snapshot = J.Obj(input["snapshot"]);
        var slots = J.Arr(J.Obj(snapshot["recommendationState"])["placedCookers"]); slots.RemoveAt(1);
        J.Obj(snapshot["recommendationState"])["placedCookerControllerCount"] = 1;
        var secondOrder = J.Obj(J.Clone(Order(input))); secondOrder["traceId"] = "R-0020"; secondOrder["orderLifecycleSequence"] = 73;
        J.Arr(J.Obj(snapshot["nightBusiness"])["orders"]).Add(secondOrder);
        var secondRow = J.Obj(J.Clone(Row(input))); secondRow["order"] = J.Clone(secondOrder);
        J.Obj(J.Objects(secondRow["executionPlans"]).Single()["food"])["extraIngredients"] = new JsonArray(J.Object(("id", 3)));
        J.Arr(J.Obj(input["recommendations"])["recommendations"]).Add(secondRow);
        engine = new AutomationCoordinator();
        var batch = engine.Advance(input, 1500); next = Command(batch);
        check(J.Str(J.Obj(next["payload"])["traceId"]) == "R-0020", "缺料队首不能占住同类唯一厨具、阻断后续足料订单。");
        check(!J.Bool(State(engine)["paused"]) && !J.Bool(State(engine)["prepared"])
            && J.Num(State(engine)["retryCount"]) == 0 && J.Str(State(engine)["detailMessage"]).Contains("材料不足"),
            "稀客缺料是明确等待，不是假开锅、失败重试或永久暂停。");
        check(J.Objects(J.Obj(batch["resourceOverview"])["cookers"]).Sum(row => J.Num(row["rareReserved"])) == 1,
            "资源概览排除缺料稀客，仅计可行订单。");
        engine.Abandon((long)J.Num(next["requestId"]));
        Quantity(input, "1", 2);
        next = Command(engine.Advance(input, 3000));
        check(J.Str(J.Obj(next["payload"])["traceId"]) == "R-0019", "库存补足后缺料订单自动恢复，无需切开关或人工重试。");

        // 精确累计基础与重复加料；未知字段和歧义配方都不应放宽成可用。
        foreach (var quantity in new[] { 1, 2, -1 })
        {
            input = Fixture(createInput);
            PrimaryFood(input)["extraIngredients"] = new JsonArray(J.Object(("id", 1)), J.Object(("id", 1)));
            Quantity(input, "1", quantity);
            check((J.Arr(new AutomationCoordinator().Advance(input, 1500)["commands"]).Count == 1) == (quantity is 2 or -1),
                "稀客重复加料逐份计数且支持无限库存：" + quantity);
        }
        foreach (var invalid in new[] { "missing-quantity", "bad-recipe", "duplicate-recipe", "missing-base", "fraction", "negative" })
        {
            input = Fixture(createInput);
            var recipes = J.Arr(J.Obj(input["data"])["recipes"]);
            if (invalid == "missing-quantity") Quantities(input).Remove("1");
            if (invalid == "bad-recipe") J.Obj(PrimaryFood(input)["recipe"])["recipeId"] = 999;
            if (invalid == "duplicate-recipe") recipes.Add(J.Clone(J.Objects(recipes).Single(recipe => J.Num(recipe["id"]) == 101)));
            if (invalid == "missing-base") J.Objects(recipes).Single(recipe => J.Num(recipe["id"]) == 101).Remove("ingredientIds");
            if (invalid == "fraction") Quantities(input)["1"] = 1.5;
            if (invalid == "negative") Quantities(input)["1"] = -2;
            check(J.Arr(new AutomationCoordinator().Advance(input, 1500)["commands"]).Count == 0,
                "稀客配方/材料证据不完整不得预约开锅：" + invalid);
        }

        // 同一命令先送酒、随后扣料前发现缺料：Prepared 仅为原生聚合成功字段，不能误认已经开锅。
        input = Fixture(createInput); J.Obj(input["preferences"])["autoPrepTakeBeverage"] = true;
        Order(input)["hasServedBeverage"] = false; Row(input)["order"] = J.Clone(Order(input));
        engine = new AutomationCoordinator(); first = Command(engine.Advance(input, 1500));
        var waiting = Wait("ingredients-unavailable-before-cooking");
        waiting["prepared"] = true; waiting["servedBeverage"] = true;
        J.Arr(waiting["steps"]).Insert(0, J.Object(("code", "beverage-delivered"), ("ok", true), ("skipped", false), ("message", "酒水已送达")));
        engine.Complete((long)J.Num(first["requestId"]), waiting, 1600);
        check(!J.Bool(State(engine)["prepared"]) && J.Bool(State(engine)["beverageHandled"])
            && !J.Bool(State(engine)["paused"]) && J.Num(State(engine)["retryCount"]) == 0,
            "扣料前缺料waiting保留成功送酒，原生Prepared=true不等于料理已开锅。");
        check(J.Arr(engine.Advance(input, 2400)["commands"]).Count == 0, "扣料前缺料尊重有界冷却，不立即续接旧payload。");
        Quantity(input, "1", 0); PrimaryFood(input)["extraIngredients"] = new JsonArray(J.Object(("id", 3)));
        next = Command(engine.Advance(input, 3000));
        check(!J.Bool(J.Obj(next["payload"])["autoTakeBeverage"])
            && J.Numbers(J.Obj(next["payload"])["extraIngredientIds"]).SequenceEqual(new[] { 3d }),
            "仅重新计划未开锅料理，不重复送酒、不继续旧缺料目标。");

        // 新推荐不是撤销原生事实的理由：已有锅次保持旧目标，未知提交保持人工处理。
        foreach (var uncertain in new[] { false, true })
        {
            input = Fixture(createInput); engine = new AutomationCoordinator(); first = Command(engine.Advance(input, 1500));
            var code = uncertain ? "cooking-start-unowned" : "cooking-started";
            engine.Complete((long)J.Num(first["requestId"]), J.Object(("ok", !uncertain),
                ("automation", J.Object(("outcome", uncertain ? "blocked" : "progressed"), ("stage", "cooking-start"),
                    ("reasonCode", code), ("jobId", uncertain ? "" : "CJ-synthetic-72"))),
                ("steps", new JsonArray(J.Object(("code", code), ("ok", !uncertain), ("skipped", false), ("message", code))))), 1600);
            Quantity(input, "1", 0); PrimaryFood(input)["extraIngredients"] = new JsonArray(J.Object(("id", 3)));
            batch = engine.Advance(input, 3000);
            check(J.Objects(batch["commands"]).All(value => !J.Bool(J.Obj(value["payload"])["autoStartCooking"]))
                && J.Numbers(J.Obj(State(engine)["recipeTarget"])["extraIngredientIds"]).SequenceEqual(new[] { 1d }),
                "已开锅或提交不明不得因缺料或新推荐换目标重复制作：" + uncertain);
            if (uncertain) check(J.Bool(State(engine)["manualResolutionRequired"]), "提交不明仍需人工处理。");
        }

        // 传输失败/权限切换丢失回执不是“确定没开锅”；任务集合缺失或重复也不能授权换目标。
        foreach (var uncertainty in new[] { "transport", "authority", "jobs-missing", "jobs-duplicate" })
        {
            input = Fixture(createInput); engine = new AutomationCoordinator(); first = Command(engine.Advance(input, 1500));
            if (uncertainty == "transport") engine.Fail((long)J.Num(first["requestId"]), "合成丢失回执", 1600);
            else if (uncertainty == "authority") J.Obj(input["authority"])["authorityRevision"] = 2;
            else engine.Complete((long)J.Num(first["requestId"]), Wait("cooking-cooker-waiting"), 1600);
            if (uncertainty == "jobs-missing") J.Obj(input["snapshot"]).Remove("automationCookingJobs");
            if (uncertainty == "jobs-duplicate")
            {
                var job = J.Object(("targetKind", "rare"), ("traceId", "R-0019"), ("orderLifecycleSequence", 72), ("jobId", "ambiguous"));
                J.Obj(input["snapshot"])["automationCookingJobs"] = new JsonArray(J.Clone(job), J.Clone(job));
            }
            Quantity(input, "1", 0); PrimaryFood(input)["extraIngredients"] = new JsonArray(J.Object(("id", 3)));
            batch = engine.Advance(input, 3000);
            check(J.Objects(batch["commands"]).All(value => !J.Bool(J.Obj(value["payload"])["autoStartCooking"]))
                && J.Numbers(J.Obj(State(engine)["recipeTarget"])["extraIngredientIds"]).SequenceEqual(new[] { 1d }),
                "未证实无副作用时不因新方案可用而换目标重做：" + uncertainty);
        }

        // 收藏限定始终由主方案准入校验；不能为了有材料偷用数组中的第二份宽松候选。
        input = Fixture(createInput); J.Obj(input["preferences"])["autoPrepRecipeFavoritesOnly"] = true;
        check(J.Arr(new AutomationCoordinator().Advance(input, 1500)["commands"]).Count == 0, "材料替换不能绕过仅用收藏限定。");
        input = Fixture(createInput); Quantity(input, "1", 0);
        var alternative = J.Obj(J.Clone(J.Objects(Row(input)["executionPlans"]).Single()));
        J.Obj(alternative["food"])["extraIngredients"] = new JsonArray(J.Object(("id", 3)));
        J.Arr(Row(input)["executionPlans"]).Add(alternative);
        check(J.Arr(new AutomationCoordinator().Advance(input, 1500)["commands"]).Count == 0,
            "主方案尚未刷新时等待，不擅自拿后位宽松方案替代当前唯一执行决策。");
    }

    private static JsonObject Fixture(Func<JsonObject> createInput)
    {
        var input = createInput();
        J.Obj(input["preferences"])["autoPrepTakeBeverage"] = false;
        var order = Order(input); order["traceId"] = "R-0019"; order["orderLifecycleSequence"] = 72;
        order["deskCode"] = 4; order["guestId"] = 30; order["guestName"] = "二岩猯藏"; order["hasServedBeverage"] = true;
        Row(input)["order"] = J.Clone(order);
        PrimaryFood(input)["recipe"] = J.Clone(J.Objects(J.Obj(input["data"])["recipes"]).Single(recipe => J.Num(recipe["id"]) == 101));
        PrimaryFood(input)["extraIngredients"] = new JsonArray(J.Object(("id", 1)));
        return input;
    }
    private static JsonObject Wait(string reason) => J.Object(("ok", true),
        ("automation", J.Object(("outcome", "waiting"), ("stage", "cooking-start"), ("reasonCode", reason), ("retryAfterMs", 1000), ("jobId", ""))),
        ("steps", new JsonArray(J.Object(("code", reason), ("ok", true), ("skipped", true), ("message", "等待新库存及当前主方案")))));
    private static JsonObject Command(JsonObject batch) => J.Objects(batch["commands"]).Single();
    private static JsonObject State(AutomationCoordinator engine) => J.Obj(J.Obj(engine.Snapshot()["states"])["rare:trace:R-0019|lifecycle:72"]);
    private static JsonObject Order(JsonObject input) => J.Objects(J.Obj(J.Obj(input["snapshot"])["nightBusiness"])["orders"]).First();
    private static JsonObject Row(JsonObject input) => J.Objects(J.Obj(input["recommendations"])["recommendations"]).First();
    private static JsonObject PrimaryFood(JsonObject input) => J.Obj(J.Objects(Row(input)["executionPlans"]).First()["food"]);
    private static JsonObject Quantities(JsonObject input) => J.Obj(J.Obj(J.Obj(input["snapshot"])["recommendationState"])["ownedIngredientQty"]);
    private static void Quantity(JsonObject input, string id, int quantity) => Quantities(input)[id] = quantity;
}
