using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Support;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Application.Automation;

public sealed partial class AutomationCoordinator
{
    /// <summary>
    /// 生成与既有 OrderPreparationRequest 对应的强类型 JSON 值；数组不预先转换为 URL 文本。
    /// 鉴权修订号与命令轮次位于外层命令信封，宿主必须重新检查后才能调用游戏适配器。
    /// </summary>
    private static JsonObject BuildRarePayload(JsonObject order, JsonObject? recipe, JsonObject? beverage, JsonObject policy,
        JsonObject preferences, bool takeBeverage, bool startCooking, bool collect, bool complete, JsonObject? slot)
    {
        var payload = J.Object(("traceId", J.Str(order["traceId"])), ("orderLifecycleSequence", order["orderLifecycleSequence"]),
            ("deskCode", order["deskCode"]), ("guestId", order["guestId"]), ("guestName", order["guestName"]),
            ("runtimeGuestId", order["runtimeGuestId"]), ("specialBusinessRole", J.Str(order["specialBusinessRole"])),
            ("foodTag", order["foodTag"]), ("foodTagId", order["foodTagId"]), ("beverageTag", order["beverageTag"]), ("beverageTagId", order["beverageTagId"]),
            ("foodId", recipe?["foodId"] ?? JsonValue.Create(-1)), ("recipeId", recipe?["recipeId"] ?? JsonValue.Create(-1)),
            ("recipeName", J.Str(recipe?["recipeName"])), ("extraIngredientIds", recipe?["extraIngredientIds"] ?? new JsonArray()),
            ("predictedFoodTags", recipe?["foodTags"] ?? new JsonArray()), ("expectedFoodModifierTags", new JsonArray()),
            ("beverageId", beverage?["beverageId"] ?? JsonValue.Create(-1)), ("beverageName", J.Str(beverage?["beverageName"])),
            ("autoTakeBeverage", takeBeverage), ("autoStartCooking", startCooking), ("autoCollectCooking", collect), ("autoDeliverFood", collect),
            ("autoCompleteOrder", complete), ("recipeFavoritesOnly", J.Bool(preferences["autoPrepRecipeFavoritesOnly"])),
            ("beverageFavoritesOnly", J.Bool(preferences["autoPrepBeverageFavoritesOnly"])), ("stopOnError", J.Bool(preferences["autoPrepStopOnError"])),
            ("recipeFavorite", J.Bool(recipe?["favorite"])), ("beverageFavorite", J.Bool(beverage?["favorite"])),
            ("executionReason", "使用当前订单锁定的唯一主执行方案。"));
        CopyPolicy(payload, policy);
        AddReservation(payload, slot);
        return payload;
    }

    private static JsonObject BuildNormalPayload(JsonObject order, JsonObject? recipe, JsonObject? target, JsonObject policy,
        JsonObject preferences, bool beverage, bool cooking, bool delivery, bool completion, JsonObject? slot)
    {
        var payload = J.Object(("traceId", J.Str(order["traceId"])), ("orderKey", J.Str(order["orderKey"])),
            ("orderLifecycleSequence", order["orderLifecycleSequence"]), ("deskCode", order["deskCode"]),
            ("guestName", J.Str(order["guestName"], "普客")), ("runtimeGuestId", order["runtimeGuestId"]),
            ("specialBusinessRole", J.Str(order["specialBusinessRole"])),
            ("matchFoodId", target?["matchFoodId"] ?? order["foodId"]), ("matchBeverageId", target?["matchBeverageId"] ?? order["beverageId"]),
            ("foodId", target?["foodId"] ?? order["foodId"]), ("recipeId", target?["recipeId"] ?? recipe?["recipeId"] ?? JsonValue.Create(-1)),
            ("recipeName", Nonempty(J.Str(target?["recipeName"]), Nonempty(J.Str(order["foodName"]), J.Str(recipe?["name"])))),
            ("extraIngredientIds", target?["extraIngredientIds"] ?? new JsonArray()), ("predictedFoodTags", target?["foodTags"] ?? new JsonArray()),
            ("expectedFoodModifierTags", target?["expectedFoodModifierTags"] ?? new JsonArray()),
            ("executionMode", J.Str(target?["executionMode"])), ("allowYuumaControlledProgression", J.Bool(target?["allowYuumaControlledProgression"])),
            ("executionReason", J.Str(target?["reason"])), ("beverageId", target?["beverageId"] ?? order["beverageId"]),
            ("beverageName", Nonempty(J.Str(target?["beverageName"]), J.Str(order["beverageName"]))),
            ("autoTakeBeverage", beverage), ("autoStartCooking", cooking), ("autoCollectCooking", delivery), ("autoDeliverFood", delivery),
            ("autoCompleteOrder", completion), ("stopOnError", J.Bool(preferences["autoNormalStopOnError"])));
        CopyPolicy(payload, policy);
        AddReservation(payload, slot);
        return payload;
    }

    private static void AddReservation(JsonObject payload, JsonObject? slot)
    {
        if (slot is null) { ClearReservation(payload); return; }
        payload["cookerControllerIndex"] = J.Clone(slot["controllerIndex"]);
        payload["cookerControllerIdentity"] = J.Clone(slot["controllerIdentity"]);
        var position = J.Obj(slot["gridPosition"]);
        payload["cookerGridX"] = J.Clone(position["x"]);
        payload["cookerGridY"] = J.Clone(position["y"]);
        payload["cookerGridZ"] = J.Clone(position["z"]);
    }

    private static void ClearReservation(JsonObject payload)
    {
        payload["cookerControllerIndex"] = -1;
        payload["cookerControllerIdentity"] = "";
        payload["cookerGridX"] = null;
        payload["cookerGridY"] = null;
        payload["cookerGridZ"] = null;
    }

    /// <summary>保持原人工确认 HTTP 路由时，宿主可在完成同一租约校验后用本入口回填已验证 ACK。</summary>
    public bool AcknowledgeResult(long sequence, JsonObject response, long nowMs)
    {
        lock (_gate)
        {
            if (!_authorized || sequence <= 0) return false;
            var request = new PendingCommand(0, _scopeVersion, "", "ack-barrier", "", sequence, new JsonObject(), new JsonObject(), true, 1, 1);
            // 没有当前订单状态的孤立屏障也必须完整校验回执，不能因为“没有残留暂停”而误报成功。
            return CompleteAcknowledgement(request, response, nowMs);
        }
    }

    /// <summary>输出纯展示状态；所有 JsonNode 均复制，HTTP 线程不能修改编排器内部状态。</summary>
    private JsonObject Status(IEnumerable<JsonObject> commands)
    {
        var states = new JsonObject();
        var rare = new JsonArray();
        var normal = new JsonArray();
        foreach (var (key, state) in _states)
        {
            states[key] = J.Clone(state);
            if (!_orders.TryGetValue(key, out var order)) continue;
            var kind = J.Str(state["kind"]);
            if (kind == "normal" && J.Bool(order["hasEvaluated"]) && !J.Bool(state["manualResolutionRequired"])) continue;
            var target = kind == "normal" ? J.Obj(state["executionTarget"]) : J.Obj(state["recipeTarget"]);
            var beverage = J.Obj(state["beverageTarget"]);
            var diagnostic = J.Object(("orderKey", state["orderKey"]), ("traceId", order["traceId"]),
                ("title", kind == "normal" ? $"桌 {DisplayDesk(order["deskCode"])} · {J.Str(order["foodName"])}" : $"{J.Str(order["guestName"], "稀客")} · 桌 {DisplayDesk(order["deskCode"])}"),
                ("foodTag", J.Str(order["foodTag"])), ("beverageTag", J.Str(order["beverageTag"])),
                ("recipeName", J.Str(target["recipeName"])), ("foodName", J.Str(order["foodName"])),
                ("beverageName", kind == "normal" ? J.Str(order["beverageName"]) : J.Str(beverage["beverageName"])),
                ("source", J.Str(order["source"])), ("stepLabel", StepLabel(J.Str(state["step"]))),
                ("stepSeconds", J.Num(state["stepStartedAtMs"]) > 0 ? Math.Max(0, Math.Floor((_lastNow - J.Num(state["stepStartedAtMs"])) / 1000)) : 0),
                ("nextAction", NextAction(state)), ("retryCount", state["retryCount"]), ("rollbackCount", state["rollbackCount"]),
                ("lastError", state["lastError"]), ("detailMessage", state["detailMessage"]), ("detailUpdatedAtMs", state["detailUpdatedAtMs"]),
                ("prepared", J.Bool(state["prepared"]) || J.Bool(order["hasServedFood"])),
                ("beverageDeliveryRequested", J.Bool(state["beverageHandled"]) || J.Bool(order["hasServedBeverage"])),
                ("foodDeliveryRequested", J.Bool(state["foodDelivered"]) || J.Bool(order["hasServedFood"])),
                ("completed", J.Bool(state["completed"]) || J.Bool(order["hasEvaluated"])),
                ("hasServedFood", J.Bool(order["hasServedFood"])), ("hasServedBeverage", J.Bool(order["hasServedBeverage"])),
                ("readyToEvaluate", J.Bool(order["readyToEvaluate"])), ("hasEvaluated", J.Bool(order["hasEvaluated"])),
                ("paused", state["paused"]), ("manualResolutionRequired", state["manualResolutionRequired"]),
                ("lastRuntimeEventSequence", state["lastRuntimeEventSequence"]),
                ("controllerAvailable", order["controllerAvailable"]), ("canAutomate", order["canAutomate"]), ("actionBlockReason", J.Str(order["actionBlockReason"])));
            (kind == "normal" ? normal : rare).Add(diagnostic);
        }
        var barriers = J.Objects(J.Obj(_input["snapshot"])["automationEvents"]).Where(IsManualEvent)
            .Where(e => J.Num(e["orderLifecycleSequence"]) > 0 && J.Str(e["orderRuntimeKind"]).Length > 0 && J.Str(e["orderId"]).Length > 0 && J.Str(e["orderControllerId"]).Length > 0)
            .GroupBy(e => string.Join(":", J.Str(e["targetKind"]), J.Str(e["orderRuntimeKind"]), J.Str(e["orderId"]), J.Str(e["orderControllerId"]), J.Key(J.Num(e["orderLifecycleSequence"]))), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(e => J.Num(e["sequence"])).First()).OrderByDescending(e => J.Num(e["sequence"]))
            .Select(e => J.Object(("sequence", e["sequence"]), ("targetKind", e["targetKind"]),
                ("title", $"{(J.Str(e["targetKind"]) == "normal" ? "普客" : "稀客")} · {J.Str(e["guestName"], "未知客人")} · 桌 {DisplayDesk(e["deskCode"])}"),
                ("code", Nonempty(J.Str(e["reasonCode"]), J.Str(e["code"]))), ("message", J.Str(e["message"], "请检查游戏现场后确认。")), ("error", "")));
        return J.Object(("scopeVersion", _scopeVersion), ("runtimeEnabled", _runtimeEnabled), ("leaseOwned", _authorized),
            ("message", _message), ("commands", J.Array(commands)), ("states", states), ("rareDiagnostics", rare), ("normalDiagnostics", normal),
            ("safetyBarriers", J.Array(barriers)), ("rejectedRecipeKeys", J.Array(_rejectedRecipes)),
            ("rareBusy", _pending.Values.Any(request => request.Action is "prepare-rare" or "complete-rare")),
            ("normalBusy", _pending.Values.Any(request => request.Action == "complete-normal")),
            ("inFlightCount", _pending.Count), ("resourceOverview", BuildResourceOverview()));
    }

    /// <summary>
    /// 生成诊断页面的本轮资源需求预览；与真实调度共享物理控制器选择规则，但使用独立预约集合，
    /// 因而读取诊断不会改变实际准入。普客先预约、稀客后预约，多功能厨具在预览中也只计一次。
    /// </summary>
    private JsonObject BuildResourceOverview()
    {
        var preferences = J.Obj(_input["preferences"]);
        var blocked = new JsonArray();
        var rows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (!J.Bool(preferences["automationEnabled"])) return J.Object(("cookers", new JsonArray()), ("normalBlocked", blocked));
        var snapshot = J.Obj(_input["snapshot"]);
        var pool = Cookers.BuildAutomationCookerPool(snapshot["recommendationState"]);
        var used = new HashSet<int>();

        bool ReserveOverview(string cookerName, string kind, string label)
        {
            var cooker = Cookers.NormalizeCookerName(cookerName);
            if (cooker.Length == 0) return false;
            var slot = Cookers.FindAvailableAutomationCookerSlot(pool, cooker, used);
            if (slot is null) return false;
            used.Add((int)J.Num(slot["controllerIndex"]));
            if (!rows.TryGetValue(cooker, out var row))
            {
                row = J.Object(("key", cooker), ("label", cooker),
                    ("capacity", J.Objects(pool["slots"]).Count(item => J.Strings(item["supportedKeys"]).Contains(cooker, StringComparer.Ordinal))),
                    ("normalReserved", 0), ("rareReserved", 0), ("labels", new JsonArray()));
                rows.Add(cooker, row);
            }
            row[kind + "Reserved"] = J.Num(row[kind + "Reserved"]) + 1;
            J.Arr(row["labels"]).Add(label);
            return true;
        }

        if (J.Bool(preferences["autoNormalOrderEnabled"]) && J.Bool(preferences["autoNormalStartCooking"]))
        {
            var count = 0;
            foreach (var (key, order) in SortedOrders("normal"))
            {
                var state = _states[key];
                if (J.Bool(order["hasEvaluated"]) || J.Bool(order["hasServedFood"]) || J.Bool(state["prepared"])
                    || J.Bool(state["foodDelivered"]) || J.Bool(state["paused"])) continue;
                var policy = Domain.SpecialBusiness.SpecialBusinessRules.BuildWirePolicy(snapshot["specialBusiness"], J.Str(order["specialBusinessRole"]), _businessGeneration);
                var target = ResolveNormalTarget(order, state, policy, true, out var error);
                var recipe = FindRecipe(order);
                var label = $"普客 {Nonempty(J.Str(order["foodName"]), "未知料理")} · 桌 {DisplayDesk(order["deskCode"])}";
                if (error.Length == 0 && target is null && recipe is null) error = "当前订单未匹配到料理目录，无法确认开锅目标。";
                if (error.Length > 0)
                {
                    blocked.Add(J.Object(("orderKey", state["orderKey"]), ("label", label), ("reason", error)));
                    continue;
                }
                var cooker = target is null ? J.Str(recipe?["cooker"]) : J.Str(target["cookerName"]);
                if (ReserveOverview(cooker, "normal", label) && ++count >= Math.Clamp((int)J.Num(preferences["autoNormalConcurrency"], 2), 1, 6)) break;
            }
        }

        if (J.Bool(preferences["autoRareOrderEnabled"]) && J.Bool(preferences["autoPrepStartCooking"]))
        {
            var byKey = J.Objects(J.Obj(_input["recommendations"])["recommendations"])
                .GroupBy(item => OrderKey("rare", J.Obj(item["order"])), StringComparer.Ordinal)
                .Where(group => group.Key.Length > 0 && group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var count = 0;
            foreach (var (key, order) in SortedOrders("rare"))
            {
                var state = _states[key];
                if (J.Bool(state["prepared"]) || J.Bool(order["hasServedFood"]) || J.Bool(state["paused"])
                    || !byKey.TryGetValue(J.Str(state["orderKey"]), out var item)) continue;
                var (recipe, _) = PickRareTargets(item, preferences);
                if (recipe is null) continue;
                var label = $"稀客 {Nonempty(J.Str(order["guestName"]), "未知")} · 桌 {DisplayDesk(order["deskCode"])}";
                if (ReserveOverview(J.Str(recipe["cookerName"]), "rare", label)
                    && ++count >= Math.Clamp((int)J.Num(preferences["autoRareConcurrency"], 2), 1, 6)) break;
            }
        }
        return J.Object(("cookers", J.Array(rows.Values.OrderBy(row => J.Str(row["label"]), StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), false)))),
            ("normalBlocked", blocked));
    }

    private static string StepLabel(string step) => step switch
    {
        "match-order" => "匹配订单", "ensure-beverage" => "确认酒水", "ensure-cooking" => "确认料理",
        "deliver-food" => "送达料理", "complete-order" => "完成订单", "done" => "完成", "paused" => "暂停", _ => "待命",
    };
    private static string NextAction(JsonObject state) => J.Bool(state["manualResolutionRequired"]) ? "确认游戏状态后点击“确认已处理”"
        : J.Bool(state["paused"]) ? "等待手动重试或订单变化" : J.Str(state["step"]) switch
        {
            "complete-order" => "下一轮尝试完成订单", "deliver-food" => "等待 Mod 料理任务送达", "ensure-beverage" => "下一轮校验酒水送达",
            "ensure-cooking" => "下一轮校验厨具/开锅", "match-order" => "下一轮匹配订单", "done" => "等待订单从列表移除", _ => "下一轮刷新",
        };
    private static string Nonempty(string value, string fallback) => value.Length > 0 ? value : fallback;
    private static string DisplayDesk(JsonNode? value) => J.Key(J.Num(value) >= 0 ? J.Num(value) + 1 : J.Num(value));
}
