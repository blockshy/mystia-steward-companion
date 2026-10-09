using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Business.Application;
using MystiaStewardCompanion.Business.Domain.GameUi;
using MystiaStewardCompanion.Business.Domain.Orders;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using MystiaStewardCompanion.Business.Domain.Support;
using MystiaStewardCompanion.Contracts;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

if (args.Contains("--serve", StringComparer.Ordinal)) { await JsonLineBridge.Run(); return; }
if (args.Contains("--load-stream", StringComparer.Ordinal)) { await LoadStreamTests.Run(Profile(), args.Contains("--client-poll", StringComparer.Ordinal) ? 750 : 10); return; }
if (args.Contains("--lease-expiry", StringComparer.Ordinal)) { await LeaseExpiryTests.Run(Profile()); return; }
if (args.Contains("--ui-failure-transition", StringComparer.Ordinal)) { await UiFailureTransitionTests.Run(Profile()); return; }
if (args.Contains("--ui-colors", StringComparer.Ordinal)) { await UiTargetColorTests.Run(Profile()); return; }

// 运行真实后台业务循环、真实设备/文件存储和真实领域代码，仅游戏副作用替换为受控委托。
// 所有临时数据均位于专属临时目录，不接触真实游戏、用户配置、TCP端口或真实客户端。
var checks = 0;
void Check(bool value, string label) { checks++; if (!value) throw new InvalidOperationException(label); }
async Task Until(Func<bool> condition, string label, int timeout = 10000)
{
    var watch = Stopwatch.StartNew();
    while (!condition()) { if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException(label); await Task.Delay(20); }
}
JsonObject Parse(string text) => JsonNode.Parse(text)!.AsObject();
var directory = Path.GetFullPath(Path.Combine("temp", "mystia-business-host-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(directory);
var log = Logger.CreateLogSource("business-host-offline");
var client = "11111111-1111-1111-1111-111111111111";
var calls = 0; var effects = 0;
using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
var block = 0;
OrderPreparationResult Adapter(OrderPreparationRequest request)
{
    Interlocked.Increment(ref calls);
    if (Volatile.Read(ref block) != 0) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("测试适配器未释放。"); }
    var current = request.IsBusinessInputCurrent?.Invoke() == true;
    if (current) Interlocked.Increment(ref effects);
    var result = new OrderPreparationResult { Ok = current, Prepared = current };
    result.Automation.Outcome = current ? "waiting" : "cancelled";
    result.Automation.Stage = "cooking-start";
    result.Automation.ReasonCode = current ? "cooking-in-progress" : "business-input-stale";
    return result;
}
var host = new LocalApiServer(directory, log, Adapter);
try
{
    var catalog = Parse(@"{""isComplete"":true,""source"":""test"",""status"":""complete"",
      ""ingredients"":[{""id"":1,""name"":""豆腐"",""tags"":[""素"",""鲜""],""price"":2}],
      ""recipes"":[{""id"":101,""recipeId"":201,""name"":""测试料理"",""ingredients"":[""豆腐""],""positiveTags"":[""素"",""鲜""],""negativeTags"":[],""cooker"":""煮锅"",""level"":3,""price"":20}],
      ""beverages"":[{""id"":21,""name"":""测试酒水"",""tags"":[""水果"",""直饮""],""level"":3,""price"":10}],
      ""rareCustomers"":[{""id"":3,""name"":""测试稀客"",""positiveTags"":[""素"",""鲜""],""negativeTags"":[],""beverageTags"":[""水果"",""直饮""],""places"":[""妖怪兽道""]}],
      ""normalCustomers"":[{""id"":3,""name"":""测试普客"",""positiveTags"":[""素"",""鲜""],""beverageTags"":[""水果""],""places"":[""妖怪兽道""]}]}" );
    var snapshot = Parse(@"{""automationSessionId"":""test-session"",""nightBusinessGeneration"":1,""nightBusinessAutomationAllowed"":true,""nightBusiness"":{""orders"":[],""activeRareGuests"":[]},""specialBusiness"":null,""automationEvents"":[],""automationCookingJobs"":[],
      ""normalBusiness"":{""orders"":[{""traceId"":""N-1"",""orderKey"":""ptr:123"",""orderLifecycleSequence"":1,""deskCode"":0,""guestId"":3,""runtimeGuestId"":3,""guestName"":""测试普客"",""foodId"":101,""beverageId"":21,""foodName"":""测试料理"",""beverageName"":""测试酒水"",""hasServedFood"":false,""hasServedBeverage"":false,""hasEvaluated"":false,""readyToEvaluate"":false,""canAutomate"":true}]},
      ""recommendationState"":{""availableRecipeIds"":[101],""availableIngredientIds"":[1],""availableBeverageIds"":[21],""ownedIngredientQty"":{""1"":20},""ownedBeverageQty"":{""21"":20},""placedCookerSnapshotComplete"":true,""placedCookerControllerCount"":1,""placedCookerEmptyControllerCount"":0,""placedCookerLockedControllerCount"":0,""placedCookerReadFailureCount"":0,""placedCookerTypeIds"":[1],""placedCookers"":[{""controllerIndex"":0,""controllerIdentity"":""0x10"",""gridPosition"":{""x"":0,""y"":0,""z"":0},""name"":""煮锅"",""typeIds"":[1],""typeNames"":[""煮锅""],""automationAvailable"":true,""couldOpen"":true,""challengeLocked"":false}]}}" );
    host.Publish(snapshot, catalog); host.Start();
    await Until(() => host.Status()["error"] != null, "无主设备必须暴露等待配置的错误。");
    Check(calls == 0 && !J.Bool(host.Status()["isCurrent"]), "未注册主设备时不能执行或把空输入发布为当前。");
    Check(!J.Bool(host.Status(999)["isCurrent"]) && !J.Bool(host.Status(999)["pending"]), "旧协议必须明确要求升级。");
    var state = host.Authority.Register(client, "test", new CompanionDeviceRegisterRequest { ProtocolVersion = 1, ProfileSchemaVersion = 1, Platform = "windows", AppVersion = "test", Profile = Profile() }, DateTime.UtcNow);
    host.Invalidate();
    await Until(() => J.Bool(host.Status()["isCurrent"]), "注册后必须恢复当前结果。");
    Check(calls == 0, "主设备已注册但没有租约时仍不能调度。");
    var seen = host.Authority.ReadBusinessState(DateTime.UtcNow).Devices.Single().LastSeenAtUtc;
    for (var index = 0; index < 4; index++) host.CheckFrameAuthorityDoesNotRenew();
    Check(seen == host.Authority.ReadBusinessState(DateTime.UtcNow).Devices.Single().LastSeenAtUtc, "后台读取不得刷新主设备在线心跳。");
    Check(!host.Authority.ReadBusinessState(DateTime.UtcNow.AddMinutes(1)).Devices.Single().Online, "后台许可检查不能伪造未来在线状态。");

    // 页面意图上限和公平轮转：旧意图被淘汰；留下的每个意图都必须得到一次当前结果。
    var copiedIntent = J.Obj(J.Clone(Parse("{\"protocolVersion\":1,\"kind\":\"rare\",\"customerId\":3,\"foodTag\":\"鲜\",\"beverageTag\":\"水果\"}")));
    Check(BusinessProtocol.ValidatePageQuery(copiedIntent)["customerId"]!.GetValue<int>() == 3,
        "解析后的JSON深复制必须保留Int32协议版本和目录ID的读取契约。");
    var query = J.Object(("protocolVersion", 1), ("kind", "normal"), ("selectedPlace", "妖怪兽道"));
    for (var index = 0; index < 17; index++) _ = host.Query("device-" + index, query);
    Check(host.QueryCount == 16 && !host.QueryKeys.Contains("device-0:normal"), "页面意图必须有界并淘汰最老意图。");
    await Until(() => Enumerable.Range(1, 16).All(index => J.Bool(host.Query("device-" + index, query)["isCurrent"])), "页面查询轮转发生饥饿。");
    Check(J.Arr(J.Obj(host.Query("device-1", query)["result"])["recipes"]).Count > 0, "页面必须使用当前运行时目录计算。");
    foreach (var invalid in new[] { J.Object(("protocolVersion", 0), ("kind", "normal")), J.Object(("protocolVersion", 1), ("kind", "normal"), ("snapshot", snapshot)), J.Object(("protocolVersion", 1), ("kind", "rare"), ("customerId", -1)), J.Object(("protocolVersion", 1), ("kind", "normal"), ("selectedPlace", new string('x', 101))) })
    {
        var rejected = false; try { _ = host.Query(client, invalid); } catch (CompanionDeviceAuthorityException exception) { rejected = exception.StatusCode == 400; }
        Check(rejected, "异常协议或伪造上下文必须在查询登记前拒绝。");
    }
    var rawInvalid = new[]
    {
        "null", "[]", "42", "\"text\"", "{", "{\"protocolVersion\":1,\"kind\":false}",
        "{\"protocolVersion\":\"1\",\"kind\":\"normal\"}", "{\"protocolVersion\":1,\"kind\":\"normal\",\"selectedPlace\":123}",
        "{\"protocolVersion\":1.5,\"kind\":\"normal\"}", "{\"protocolVersion\":999999999999999999999999999999,\"kind\":\"normal\"}",
        "{\"protocolVersion\":1,\"kind\":\"normal\",\"selectedPlace\":null}",
        "{\"protocolVersion\":1,\"kind\":\"normal\",\"kind\":\"rare\",\"customerId\":3}",
        "{\"protocolVersion\":1,\"kind\":\"normal\",\"selectedPlace\":" + new string('[', 20) + "0" + new string(']', 20) + "}",
    };
    foreach (var json in rawInvalid)
    {
        var rejected = false; try { _ = host.RawQuery(client, json); } catch (CompanionDeviceAuthorityException exception) { rejected = exception.StatusCode == 400; }
        Check(rejected, "非法原始JSON必须返回400，不能以500或空查询掩盖：" + json);
    }
    var oldVersion = host.InputVersion; host.AddFavorite();
    await Until(() => host.InputVersion > oldVersion && J.Bool(host.Status()["isCurrent"]), "外部收藏变化必须失效并重新发布。");

    // 已产生的动作模拟等待主线程，此时修改输入必须使最后一道许可失败。
    Volatile.Write(ref block, 1); host.GrantLease(client, state.AuthorityRevision, TimeSpan.FromSeconds(15));
    await Until(() => entered.IsSet, "有效主设备租约应允许产生动作。");
    Check(effects == 0, "受控适配器等待期间不得提前产生副作用。");
    host.Invalidate();
    Check(!J.Bool(host.Status()["isCurrent"]), "输入失效后旧结果立即不可用。");
    release.Set();
    await Task.Delay(50);
    Check(effects == 0, "进入适配器后输入变化仍须拒绝旧命令。");
    host.ExpireLease(); Volatile.Write(ref block, 0);
    await Until(() => J.Bool(host.Status()["isCurrent"]), "取消旧命令后业务循环必须恢复。");
    var beforeOffline = calls;
    await Task.Delay(1000);
    Check(calls == beforeOffline && !host.HasLease, "租约失效后后台不得续租或发送新命令。");

    // 不完整目录不得复用上一版页面结果；页面只返回明确的空投影。
    host.Publish(snapshot, J.Object(("isComplete", false)));
    Check(!J.Bool(host.Status()["isCurrent"]), "目录更新必须同步撤销旧状态。");
    await Until(() => J.Bool(host.Query("device-1", query)["isCurrent"]), "不可用目录的页面查询未完成。");
    Check(J.Arr(J.Obj(host.Query("device-1", query)["result"])["recipes"]).Count == 0, "不可用目录不得保留旧推荐。");
    Check(calls == beforeOffline, "缺失目录和租约时不能产生动作。");

    // 正常经营身份、特殊修订只在当前帧绑定，绑定后的同一目标必须通过UI精确校验。
    var specialSnapshot = J.Obj(J.Clone(snapshot));
    specialSnapshot["specialBusiness"] = J.Object(("active", true), ("challengeTypeAvailable", true), ("challengeType", "Story_WackyCookingCompetition"), ("phase", "Phase 1"), ("foodTargetTags", new JsonArray("鲜")));
    var normal = J.Obj(J.Arr(J.Obj(specialSnapshot["normalBusiness"])["orders"])[0]); normal["specialBusinessRole"] = "wacky-target-order";
    var row = J.Object(("orderKey", OrderIdentityAndSorting.NormalKey(normal)), ("target", J.Object(("matchFoodId", 101), ("matchBeverageId", 21), ("foodId", 101), ("recipeId", 201), ("beverageId", 21))), ("message", ""));
    var raw = J.Object(("normalExecutionTargets", J.Array(new[] { row })));
    var before = raw.ToJsonString(); var bound = BusinessQueries.BindCurrentNormalTargets(raw, specialSnapshot);
    var target = J.Obj(J.Arr(bound["normalExecutionTargets"])[0]!["target"]);
    Check(before == raw.ToJsonString(), "目标绑定不能修改原始缓存。");
    Check(GameUiTargetService.IsCurrentNormalTarget(normal, target, specialSnapshot["specialBusiness"], 1), "当前特殊目标必须同时被游戏UI认可。");
    var ui = GameUiTargetService.BuildNormal(J.Object(("orders", J.Obj(specialSnapshot["normalBusiness"])["orders"]),
        ("executionTargets", bound["normalExecutionTargets"]), ("executionTargetsCurrent", true), ("specialBusiness", specialSnapshot["specialBusiness"]),
        ("businessGeneration", 1), ("data", RuntimeDataNormalizer.Build(catalog)), ("features", new JsonObject()), ("color", "#FFFFFF")));
    Check(ui != null && J.Num(ui["recipeId"]) == 201, "绑定后的特殊普通目标必须实际生成游戏UI值。");
    foreach (var mode in new[] { "duplicate-order", "duplicate-target", "stale-food", "missing-lifecycle", "zero-generation" })
    {
        var s = J.Obj(J.Clone(specialSnapshot)); var r = J.Obj(J.Clone(raw));
        if (mode == "duplicate-order") J.Arr(J.Obj(s["normalBusiness"])["orders"]).Add(J.Clone(normal));
        if (mode == "duplicate-target") J.Arr(r["normalExecutionTargets"]).Add(J.Clone(row));
        if (mode == "stale-food") J.Obj(J.Arr(J.Obj(s["normalBusiness"])["orders"])[0])["foodId"] = 102;
        if (mode == "missing-lifecycle") J.Obj(J.Arr(J.Obj(s["normalBusiness"])["orders"])[0])["orderLifecycleSequence"] = 0;
        if (mode == "zero-generation") s["nightBusinessGeneration"] = 0;
        Check(J.Objects(BusinessQueries.BindCurrentNormalTargets(r, s)["normalExecutionTargets"]).All(item => item["target"] == null), "身份不足时拒绝绑定：" + mode);
    }
    // 缓存只跨帧复用完整相同的候选输入，结果树由调用方改动时不能污染下次命中。
    var data = RuntimeDataNormalizer.Build(catalog); var runtime = J.Obj(snapshot["recommendationState"]);
    var prefs = Parse(Profile().GetRawText()); var sets = RuntimeRecommendationSupport.BuildRuntimeSets(runtime, data)!;
    var context = RuntimeRecommendationSupport.BuildRecommendationRuntimeContext(runtime, sets, prefs, data);
    var demand = NormalTargetSelector.Demand(J.Objects(data["rareCustomers"]).Single(), "鲜", "水果");
    var cache = new OrderCandidateCache(maximumEntries: 2, maximumBytes: 1024 * 1024);
    var candidate = cache.Food(data, demand, context); var expectedCandidates = candidate.ToJsonString();
    J.Obj(candidate[0])["injectedByCaller"] = true;
    Check(cache.Food(data, demand, context).ToJsonString() == expectedCandidates && cache.Hits == 1, "命中必须返回独立候选值树。");
    var modified = J.Obj(J.Clone(context)); J.Obj(modified["ownedIngredientQty"])["1"] = 1;
    _ = cache.Food(data, demand, modified);
    Check(cache.Misses == 2, "库存变化必须重算候选。");
    _ = cache.Food(data, demand, context, J.Object(("forbiddenExtraIngredientIds", new JsonArray(1))));
    Check(cache.Misses == 3 && cache.EntryCount == 2, "特殊材料限制变化必须失效且缓存受条数上限约束。");
    _ = cache.Food(data, demand, context);
    Check(cache.Misses == 4 && cache.RetainedBytes <= 1024 * 1024, "LRU必须淘汰旧项并维持字节上限。");
    var noRetention = new OrderCandidateCache(maximumBytes: 1);
    Check(noRetention.Food(data, demand, context).Count > 0 && noRetention.EntryCount == 0, "超大单项仍计算但不突破内存上限。");
    var rareSnapshot = J.Obj(J.Clone(snapshot));
    J.Obj(rareSnapshot["nightBusiness"])["orders"] = new JsonArray(J.Object(("traceId", "R-1"), ("orderLifecycleSequence", 1), ("deskCode", 1), ("guestId", 3), ("runtimeGuestId", 3), ("guestName", "测试稀客"), ("foodTag", "鲜"), ("beverageTag", "水果"), ("firstSeenAtUtc", "old")));
    var payload = BusinessQueries.BuildOrderPayload(rareSnapshot, data, prefs, new JsonObject(), new JsonObject(), new JsonArray());
    cache = new OrderCandidateCache(); _ = OrderRecommendationService.Evaluate(payload, cache); var hitBefore = cache.Hits;
    J.Obj(J.Arr(payload["orders"])[0])["firstSeenAtUtc"] = "new"; J.Obj(J.Arr(payload["orders"])[0])["hasServedFood"] = true;
    var freshResult = OrderRecommendationService.Evaluate(payload, cache);
    Check(cache.Hits >= hitBefore + 1, "纯展示时间及送达状态变化应复用完整相同的纯计算投影。");
    Check(J.Str(J.Objects(freshResult["recommendations"]).Single()["order"]?["firstSeenAtUtc"]) == "new", "候选命中不能复用旧订单或执行许可。");
    host.Publish(snapshot, catalog); host.GrantLease(client, state.AuthorityRevision, TimeSpan.FromSeconds(15));
    entered.Reset(); release.Reset(); Volatile.Write(ref block, 1);
    await Until(() => entered.IsSet, "停止测试未进入受控适配器。");
    var timer = Stopwatch.StartNew(); host.Cancel(); timer.Stop();
    Check(timer.ElapsedMilliseconds < 1000, "停止不能等待可能依赖Unity主线程的适配委托。");
    Check(!J.Bool(host.Status()["isCurrent"]), "停止立即撤销旧结果与许可。");
    release.Set(); await host.Stop();
    Console.WriteLine($"PASS business host: {checks} assertions; real loop/stores/domain with isolated game adapter.");
}
finally
{
    release.Set(); await host.Stop(); Logger.Sources.Remove(log);
    // 专属临时目录由本测试创建，先验证绝对路径仍位于系统临时目录再清理。
    var temporaryRoot = Path.GetFullPath("temp") + Path.DirectorySeparatorChar; var ownedDirectory = Path.GetFullPath(directory);
    if (ownedDirectory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) Directory.Delete(ownedDirectory, recursive: true);
}

static JsonElement Profile()
{
    var fields = new[] { "automationEnabled", "autoRareOrderEnabled", "autoNormalOrderEnabled", "autoNormalTakeBeverage", "autoNormalStartCooking", "autoNormalDeliverFood", "autoNormalCompleteOrder", "autoNormalStopOnError", "autoPrepCompleteOrder", "autoPrepTakeBeverage", "autoPrepStartCooking", "autoPrepCollectCooking", "autoPrepRecipeFavoritesOnly", "autoPrepBeverageFavoritesOnly", "autoPrepStopOnError", "filterMissingCookers", "missionRecipePriorityEnabled", "pinFavoriteRecipeEnabled", "pinFavoriteBeverageEnabled", "rareGameUiPinningEnabled", "normalGameUiPinningEnabled", "rareRecipeVariantEnabled", "normalRecipeVariantEnabled", "rareCookerHighlightEnabled", "normalCookerHighlightEnabled", "rareSeatHighlightEnabled", "normalSeatHighlightEnabled", "rareOrderHighlightEnabled", "normalOrderHighlightEnabled" };
    var p = fields.ToDictionary(key => key, _ => (object)false, StringComparer.Ordinal);
    foreach (var key in new[] { "automationEnabled", "autoNormalOrderEnabled", "autoNormalStartCooking", "filterMissingCookers" }) p[key] = true;
    p["autoRareConcurrency"] = 2; p["autoNormalConcurrency"] = 2; p["autoMaxStepRetries"] = 3; p["autoMaxRollbacks"] = 2;
    p["rareTargetHighlightColor"] = "#FFDB2E"; p["normalTargetHighlightColor"] = "#5FACD3"; p["serviceOrderSortMode"] = "ordered";
    p["recommendationBudgetPolicy"] = "block"; p["recipeVariantLimitPerBase"] = 1;
    var keys = new[] { "foodPreference", "beveragePreference", "negativeRisk", "extraCount", "resourcePressure", "totalCost", "profit", "beverageStock", "cookerAvailable" };
    p["recommendationSortProfile"] = new { preset = "balanced", objectives = keys.Select(key => new { key, enabled = true, weight = 50, direction = key is "negativeRisk" or "extraCount" or "resourcePressure" or "totalCost" ? "asc" : "desc" }).ToArray() };
    p["recommendationExclusions"] = new { excludedIngredientIds = Array.Empty<int>(), excludedBeverageIds = Array.Empty<int>() };
    return JsonSerializer.SerializeToElement(p);
}
