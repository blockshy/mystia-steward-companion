using System.Text.Json;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application.Automation;
using MystiaStewardCompanion.Business.Domain.GameUi;
using MystiaStewardCompanion.Business.Domain.Orders;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 2026-11-06经营日志的去敏回放：真实trace/桌号/同一客人ID与点单时间保留，
/// 日志没有公开原生生命周期值，因此明确使用独立合成生命周期和经营代次，不猜原生指针。
/// 连接真实托管参与状态机、公开DTO、HTTP显示排序、自动化编排及游戏高亮目标算法；
/// 只观察生成的命令/目标，不提交游戏动作、不访问网络或用户配置。
/// </summary>
internal static class RareOrderDisplayReplayTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const long Generation = 91;
    private static readonly string[] Traces = { "R-0005", "R-0022", "R-0024", "R-0025" };
    private static readonly int[] Desks = { 7, 1, 2, 5 };
    private static readonly long[] Lifecycles = { 1005, 1022, 1024, 1025 };
    private static readonly string[] ObservedTimes = {
        "2026-11-06T21:12:13.721+08:00", "2026-11-06T21:14:53.346+08:00",
        "2026-11-06T21:15:54.329+08:00", "2026-11-06T21:15:56.292+08:00",
    };

    internal static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        var identities = Traces.Select((trace, index) => new RuntimeRareGuestParticipationOrderIdentity(
            Generation, trace, Lifecycles[index], 30)).ToArray();
        try
        {
            RuntimeRareGuestParticipationState.Reset();
            var revision = RuntimeRareGuestParticipationState.BeginBusiness(Generation, new[] { 30, 36 });
            revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(Generation, revision, true,
                identities.Select(identity => new RuntimeRareGuestParticipationOrderObservation(identity, null)));
            var input = Input();
            void Synchronize(bool complete = true)
            {
                var dto = LocalApiRareGuestParticipationSnapshot.From(RuntimeRareGuestParticipationState.Snapshot, complete);
                input["snapshot"]!["rareGuestParticipation"] = JsonSerializer.SerializeToNode(dto, JsonOptions);
                input["snapshot"]!["nightBusiness"]!["ordersReadComplete"] = complete;
            }
            void CheckCounts(int active, string stage)
            {
                Synchronize();
                var state = RuntimeRareGuestParticipationState.Snapshot;
                var dto = LocalApiRareGuestParticipationSnapshot.From(state);
                Check(dto.Ready && dto.DisplayOrderKeys?.Count == 4 && dto.Entries.Count == 4,
                    stage + "：暂停/恢复不应改变四笔订单的可见集合。");
                Check(dto.DisplayOrderKeys!.Select(key => (key.TraceId, key.OrderLifecycleSequence)).Distinct().Count() == 4,
                    stage + "：同名同guestId的不同生命周期被合并。");
                Check(dto.Entries.Count(entry => entry.Participating) == active, stage + "：展示变动意外改变参与资格。");
                var snapshot = J.Obj(input["snapshot"]); var profile = J.Obj(input["preferences"]);
                Check(RareOrderParticipationProjection.TryReadPositions(snapshot, profile, out var positions)
                    && positions?.Count == active, stage + "：执行投影没有保持独立参与集合。");
                var commands = J.Objects(new AutomationCoordinator().Advance(input, 1500)["commands"]).ToArray();
                Check(commands.Length == Math.Min(active, 4), stage + "：自动化命令数不符合参与集合。");
                var activeTraces = dto.Entries.Where(entry => entry.Participating).Select(entry => entry.TraceId).ToHashSet();
                Check(commands.All(command => activeTraces.Contains(J.Str(command["payload"]?["traceId"]))),
                    stage + "：暂停订单从可见集合越过自动化资格。");
                var targets = Targets(input);
                Check(targets.Count == active && targets.All(target => activeTraces.Contains(J.Str(target["traceId"]))),
                    stage + "：原生高亮目标包含暂停订单。");
                foreach (var identity in identities)
                {
                    using var permit = RuntimeRareGuestParticipationState.AcquireAdmissionPermit(identity);
                    Check(permit.Allowed == activeTraces.Contains(identity.TraceId), stage + "：真实参与许可被展示集合扩大。");
                }
            }
            CheckCounts(0, "新经营默认暂停");
            var initial = LocalApiRareGuestParticipationSnapshot.From(RuntimeRareGuestParticipationState.Snapshot);
            Check(initial.Entries.All(entry => entry.Managed && !entry.Participating
                && entry.ReasonCode == RuntimeRareGuestParticipationState.DefaultPausedReason),
                "保留的手动名单应令每个新生命周期默认暂停，而非自动放行。");
            Check(initial.DisplayOrderKeys!.Select(key => key.TraceId).SequenceEqual(Traces), "全部暂停时未按首次点单顺序显示。");

            void Mutate(int index, RuntimeRareGuestParticipationAction action)
            {
                revision = RuntimeRareGuestParticipationState.MutateParticipation(Generation, revision, action,
                    RuntimeRareGuestParticipationTargetScope.Order, 30, new[] { identities[index] });
            }
            Mutate(2, RuntimeRareGuestParticipationAction.EnableTail);
            CheckCounts(1, "启用桌3");
            Check(LocalApiRareGuestParticipationSnapshot.From(RuntimeRareGuestParticipationState.Snapshot).DisplayOrderKeys!
                .Select(key => key.TraceId).SequenceEqual(new[] { "R-0024", "R-0005", "R-0022", "R-0025" }),
                "参与队列没有排在暂停单之前或暂停单相对顺序改变。");
            Mutate(0, RuntimeRareGuestParticipationAction.EnableTail);
            CheckCounts(2, "恢复桌8到队尾");
            Mutate(2, RuntimeRareGuestParticipationAction.Pause);
            CheckCounts(1, "再次暂停桌3");
            Check(LocalApiRareGuestParticipationSnapshot.From(RuntimeRareGuestParticipationState.Snapshot).DisplayOrderKeys!
                .Select(key => key.TraceId).SequenceEqual(Traces), "暂停单没有回到其首次点单的相对次序。");
            Mutate(2, RuntimeRareGuestParticipationAction.EnableFront);
            CheckCounts(2, "重新优先桌3");

            // 无论displayOrderKeys展示多少条，都不能挽救部分采集、旧代次、重复/变更身份。
            foreach (var defect in new[] { "incomplete", "old-generation", "changed-lifecycle", "duplicate-entry", "missing-entry" })
            {
                var broken = J.Obj(J.Clone(input)); var snapshot = J.Obj(broken["snapshot"]);
                var participation = J.Obj(snapshot["rareGuestParticipation"]); var entries = J.Arr(participation["entries"]);
                switch (defect)
                {
                    case "incomplete": J.Obj(snapshot["nightBusiness"])["ordersReadComplete"] = false; break;
                    case "old-generation": participation["businessGeneration"] = Generation - 1; break;
                    case "changed-lifecycle": J.Obj(entries[0])["orderLifecycleSequence"] = 99999; break;
                    case "duplicate-entry": entries.Add(J.Clone(entries[0])); break;
                    case "missing-entry": entries.RemoveAt(0); break;
                }
                Check(!RareOrderParticipationProjection.TryReadPositions(snapshot, J.Obj(broken["preferences"]), out _),
                    "不完整/旧身份被误判为可执行：" + defect);
                Check(J.Arr(new AutomationCoordinator().Advance(broken, 1500)["commands"]).Count == 0,
                    "不完整/旧身份产生自动化动作：" + defect);
                Check(Targets(broken).Count == 0, "不完整/旧身份产生原生高亮目标：" + defect);
            }
            foreach (var stale in new[] { identities[0] with { BusinessGeneration = Generation - 1 }, identities[0] with { OrderLifecycleSequence = 99999 } })
            {
                using var permit = RuntimeRareGuestParticipationState.AcquireAdmissionPermit(stale);
                Check(!permit.Allowed, "显示中相同trace不能让过期生命周期获得许可。");
            }
            var partial = LocalApiRareGuestParticipationSnapshot.From(RuntimeRareGuestParticipationState.Snapshot, sourceComplete: false);
            Check(!partial.Ready && partial.DisplayOrderKeys is { Count: 0 } && partial.Entries.Count == 4,
                "部分采集不能伪造已确认顺序或丢弃已有状态证据。");

            VerifyMixedSlots(input, Check);
            Console.WriteLine($"PASS: rare-order display replay; {checks} assertions; real traces with synthetic lifecycles; four visible, paused execution/highlights remain zero; no game execution.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine("FAIL: rare-order display replay — " + exception); return 1; }
        finally { RuntimeRareGuestParticipationState.Reset(); }
    }

    /// <summary>
    /// HTTP的serviceOrderPosition一直包含所有真实订单；这里只验证边界，不把参与排序写入原始
    /// 快照。两端可把完整稀客展示序列填回这些稀客槽位，普客位置及相对先后保持稳定。
    /// </summary>
    private static void VerifyMixedSlots(JsonObject input, Action<bool, string> check)
    {
        var snapshot = J.Obj(J.Clone(input["snapshot"]));
        snapshot["normalBusiness"] = new JsonObject { ["ordersReadComplete"] = true, ["orders"] = new JsonArray(
            new JsonObject { ["traceId"] = "N-9001", ["orderLifecycleSequence"] = 9001, ["orderKey"] = "synthetic-1",
                ["firstSeenAtUtc"] = "2026-11-06T21:13:00+08:00" },
            new JsonObject { ["traceId"] = "N-9002", ["orderLifecycleSequence"] = 9002, ["orderKey"] = "synthetic-2",
                ["firstSeenAtUtc"] = "2026-11-06T21:15:00+08:00" }) };
        var original = snapshot.ToJsonString();
        var presentation = new LocalApiSnapshotPresentation().Get(original, "replay-mixed");
        var shown = JsonNode.Parse(presentation.Json)!.AsObject();
        var rare = J.Objects(shown["nightBusiness"]?["orders"]).ToArray();
        var normal = J.Objects(shown["normalBusiness"]?["orders"]).ToArray();
        check(rare.Length == 4 && normal.Length == 2, "HTTP显示投影过滤了暂停稀客。");
        check(normal.Select(order => (int)J.Num(order["serviceOrderPosition"])).SequenceEqual(new[] { 1, 3 }),
            "稀客参与队列改变了普客原始时间槽位。");
        var keys = J.Objects(snapshot["rareGuestParticipation"]?["displayOrderKeys"]).Select(key => J.Str(key["traceId"])).ToArray();
        var slots = rare.Select(order => (int)J.Num(order["serviceOrderPosition"])).OrderBy(position => position).ToArray();
        var reordered = keys.Select((trace, index) => (Trace: trace, Slot: slots[index]))
            .Concat(normal.Select(order => (Trace: J.Str(order["traceId"]), Slot: (int)J.Num(order["serviceOrderPosition"]))))
            .OrderBy(item => item.Slot).Select(item => item.Trace).ToArray();
        check(reordered.SequenceEqual(new[] { "R-0024", "N-9001", "R-0005", "N-9002", "R-0022", "R-0025" }),
            "完整稀客顺序不能填回原槽位或暂停单遗失。");
        check(snapshot.ToJsonString() == original, "只读显示排序污染了业务原始快照。");
    }

    private static IReadOnlyList<JsonObject> Targets(JsonObject input) => GameUiTargetService.EvaluateAll(new FrozenGameUiTargetInput(
        "rare", J.Obj(input["data"]), Recommendations: input["recommendations"]?["recommendations"],
        Features: J.Object(("listPinningEnabled", true), ("cookerHighlightEnabled", true), ("seatHighlightEnabled", true), ("orderHighlightEnabled", true)),
        BusinessGeneration: Generation, Snapshot: J.Obj(input["snapshot"]), Preferences: J.Obj(input["preferences"])));

    /// <summary>仅构造可执行的明确主方案，正向启用用例须产生真实编排命令，避免无效夹具造成“零动作”假通过。</summary>
    private static JsonObject Input()
    {
        var orders = new JsonArray(Traces.Select((trace, index) => (JsonNode)new JsonObject {
            ["traceId"] = trace, ["orderLifecycleSequence"] = Lifecycles[index], ["deskCode"] = Desks[index],
            ["guestId"] = 30, ["runtimeGuestId"] = 30, ["guestName"] = "萌澄果", ["firstSeenAtUtc"] = ObservedTimes[index],
            ["foodTagId"] = 1, ["foodTag"] = "素", ["beverageTagId"] = 2, ["beverageTag"] = "水果", ["automationAllowed"] = true,
            ["hasServedFood"] = false, ["hasServedBeverage"] = false, ["hasEvaluated"] = false,
        }).ToArray());
        var input = JsonNode.Parse(@"{
            ""authority"":{""sessionId"":""isolated-replay"",""businessGeneration"":91,""authorityRevision"":1,""automationEpoch"":1,""leaseOwned"":true,""allowed"":true},
            ""preferences"":{""automationEnabled"":true,""autoRareOrderEnabled"":true,""autoNormalOrderEnabled"":false,
                ""rareGuestParticipationModuleEnabled"":true,""managedRareGuestIds"":[30,36],""autoRareConcurrency"":4,
                ""autoNormalConcurrency"":2,""autoMaxStepRetries"":3,""autoMaxRollbacks"":2,
                ""autoPrepStartCooking"":true,""autoPrepTakeBeverage"":true,""autoPrepCompleteOrder"":true,""autoPrepCollectCooking"":true},
            ""snapshot"":{""runtimeLoaded"":true,""nightBusinessGeneration"":91,""nightBusinessLifecyclePhase"":""Active"",
                ""nightBusinessAutomationAllowed"":true,""automationEvents"":[],""automationCookingJobs"":[],""specialBusiness"":null,
                ""normalBusiness"":{""ordersReadComplete"":true,""orders"":[]},""nightBusiness"":{""ordersReadComplete"":true},
                ""recommendationState"":{""availableIngredientIds"":[1],""ownedIngredientQty"":{""1"":100},
                    ""placedCookerSnapshotComplete"":true,""placedCookerControllerCount"":4,""placedCookerReadFailureCount"":0}},
            ""data"":{""recipes"":[{""id"":100,""recipeId"":200,""name"":""测试料理"",""cooker"":""煮锅"",""ingredientIds"":[1]}],
                ""ingredients"":[{""id"":1,""name"":""基础食材""}],""beverages"":[{""id"":1,""name"":""测试酒水""}]},
            ""favorites"":{""recipes"":[],""beverages"":[]},""recommendations"":{""isCurrent"":true,""pending"":false,""error"":null},
            ""normalExecutionTargets"":{""isCurrent"":true,""pending"":false,""error"":null,""targets"":[]}}
            ")!.AsObject();
        input["snapshot"]!["nightBusiness"]!["orders"] = orders;
        input["snapshot"]!["recommendationState"]!["placedCookers"] = new JsonArray(Enumerable.Range(0, 4).Select(index =>
            (JsonNode)new JsonObject { ["controllerIndex"] = index, ["controllerIdentity"] = "0x" + (4096 + index * 16).ToString("X"),
                ["gridPosition"] = new JsonObject { ["x"] = index, ["y"] = 0, ["z"] = 0 }, ["typeIds"] = new JsonArray(1),
                ["automationAvailable"] = true, ["couldOpen"] = true, ["challengeLocked"] = false }).ToArray());
        input["recommendations"]!["recommendations"] = new JsonArray(orders.OfType<JsonObject>().Select(order => (JsonNode)new JsonObject {
            ["order"] = J.Clone(order), ["customer"] = new JsonObject { ["id"] = 30 },
            ["executionPlans"] = JsonNode.Parse(@"[{""food"":{""recipe"":{""id"":100,""recipeId"":200,""name"":""测试料理"",""cooker"":""煮锅"",""ingredients"":[""基础食材""]},""extraIngredients"":[],""activeTags"":[""素""],""meetsRequiredFood"":true},""beverage"":{""beverage"":{""id"":1,""name"":""测试酒水""}}}]")
        }).ToArray());
        return input;
    }
}
