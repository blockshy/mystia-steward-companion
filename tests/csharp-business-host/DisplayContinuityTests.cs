using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.Business.Application;
using MystiaStewardCompanion.LocalApi;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 真实宿主的只读展示连续性回归。阻塞游戏替身期间持续发布新快照，明确区分
/// “允许显示上次成功结果”与“允许执行当前命令”，并验证切换作用域会立即清空旧展示。
/// </summary>
internal static class DisplayContinuityTests
{
    internal static async Task Run(JsonElement profile)
    {
        var directory = Path.GetFullPath(Path.Combine("temp", "mystia-business-display-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var log = Logger.CreateLogSource("business-display-offline");
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var effects = 0;
        var host = new LocalApiServer(directory, log, request =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("离线动作替身未释放。");
            var allowed = request.IsBusinessInputCurrent?.Invoke() == true;
            if (allowed) Interlocked.Increment(ref effects);
            var result = new OrderPreparationResult { Ok = false, Prepared = false };
            result.Automation.Outcome = "cancelled";
            result.Automation.Stage = "lease";
            return result;
        });
        try
        {
            const string client = "11111111-1111-1111-1111-111111111111";
            var authority = host.Authority.Register(client, "display regression", new CompanionDeviceRegisterRequest
            { ProtocolVersion = 1, ProfileSchemaVersion = 1, Platform = "windows", AppVersion = "offline", Profile = profile }, DateTime.UtcNow);
            var snapshot = LeaseExpiryTests.Snapshot();
            snapshot["automationSessionId"] = "display-session";
            snapshot["nightBusinessLifecyclePhase"] = "active";
            snapshot["activeSceneName"] = "offline-night";
            snapshot["runtimeLoaded"] = true;
            snapshot["runtimeDaySceneGeneration"] = 1;
            snapshot["runtimeDaySceneReady"] = true;
            snapshot["runtimeDataSignature"] = "catalog-one";
            snapshot["snapshotSignature"] = "display-initial";
            snapshot["specialBusiness"] = J.Object(("active", false), ("challengeTypeAvailable", true),
                ("challengeType", ""), ("source", "observed-0"));
            var normalOrder = J.Obj(J.Arr(snapshot["normalBusiness"]?["orders"])[0]);
            normalOrder["traceId"] = "N-1"; normalOrder["orderKey"] = "ptr:123";
            J.Obj(snapshot["nightBusiness"])["orders"] = new JsonArray(J.Object(("traceId", "R-1"),
                ("orderLifecycleSequence", 1), ("deskCode", 1), ("guestId", 3), ("runtimeGuestId", 3),
                ("guestName", "测试稀客"), ("foodTag", "素"), ("beverageTag", "水果")));
            var catalog = LeaseExpiryTests.Catalog();
            var normalIntent = J.Object(("protocolVersion", 1), ("kind", "normal"), ("selectedPlace", "妖怪兽道"));
            var rareIntent = J.Object(("protocolVersion", 1), ("kind", "rare"), ("customerId", 3), ("foodTag", "素"), ("beverageTag", "水果"));
            host.Publish(snapshot, catalog);
            host.Query(client, normalIntent); host.Query(client, rareIntent);
            host.Start();
            await Until(() => J.Bool(host.Query(client, normalIntent)["isCurrent"])
                && J.Bool(host.Query(client, rareIntent)["isCurrent"]), "静止输入时两类页面未完成。");
            Check(J.Arr(host.Status()["recommendations"]?["recommendations"]).Count == 1, "经营稀客必须有真实计算结果。");

            // 正常页面是最早处理的旧槽位。下一轮获得执行权并停在真实宿主的动作调用位置时，
            // 该页面必须已经刷新，不能等动作完成并触发快照失效后才尝试处理。
            host.GrantLease(client, authority.AuthorityRevision, TimeSpan.FromSeconds(10));
            snapshot["snapshotSignature"] = "before-blocking-action";
            host.Publish(snapshot, catalog);
            await Until(() => entered.IsSet, "没有进入受控动作替身。");
            var before = host.Query(client, normalIntent);
            Check(J.Bool(before["isCurrent"]), "页面被阻塞动作饿死：动作调用前必须先发布页面。");
            var originalSignature = J.Str(before["sourceSnapshotSignature"]);
            Check(originalSignature == "before-blocking-action", "页面来源签名必须对应实际计算帧。");

            // 六次独立250ms发布只改变观察信息；每次动作许可都过期，但旧展示仍应明确可读。
            for (var index = 0; index < 6; index++)
            {
                snapshot["snapshotSignature"] = "observation-" + index;
                J.Obj(J.Arr(snapshot["nightBusiness"]?["orders"])[0])["lastSeenAtUtc"] = "observed-" + index;
                var special = J.Obj(snapshot["specialBusiness"]);
                special["source"] = "diagnostic-" + index;
                special["lastTargetUpdatedUtc"] = "time-" + index;
                special["targetTimeProgress"] = index / 10d;
                special["targetTagTimeProgress"] = index / 10d;
                special["currentAnger"] = index; special["currentSpellCount"] = index;
                host.Publish(snapshot, catalog);
                var page = host.Query(client, normalIntent);
                var state = host.Status();
                Check(!J.Bool(page["isCurrent"]) && J.Bool(page["pending"]) && J.Arr(page["result"]?["recipes"]).Count > 0,
                    "250ms观察更新应保留非当前页面展示，不能清空为永久计算中。");
                Check(J.Str(page["sourceSnapshotSignature"]) == originalSignature, "保留结果不得重新盖上新快照签名。");
                Check(!J.Bool(state["isCurrent"]) && J.Arr(state["recommendations"]?["recommendations"]).Count == 1,
                    "同作用域的经营稀客结果应保留为非当前展示。");
                await Task.Delay(250);
            }

            // 场景、会话、生命周期、目录和特殊目标的变化必须在后台仍被阻塞时立即清空旧结果。
            foreach (var field in new[] { "automationSessionId", "nightBusinessGeneration", "nightBusinessLifecyclePhase",
                "activeSceneName", "runtimeLoaded", "runtimeDaySceneGeneration", "runtimeDaySceneReady", "runtimeDataSignature" })
            {
                var changed = J.Obj(J.Clone(snapshot));
                changed[field] = field is "nightBusinessGeneration" or "runtimeDaySceneGeneration" ? JsonValue.Create(2)
                    : field is "runtimeLoaded" or "runtimeDaySceneReady" ? JsonValue.Create(false) : JsonValue.Create("different");
                host.Publish(changed, catalog);
                Check(host.Query(client, normalIntent)["result"] == null && host.Status()["recommendations"] == null,
                    "来源切换未清除旧展示：" + field);
            }
            foreach (var field in new[] { "active", "challengeTypeAvailable", "challengeType", "phase", "foodTargetTags",
                "beverageTargetTags", "requiredExtraIngredientIds", "yuumaFoodTargetRevision", "wackyKoishiShieldBroken",
                "wackyKoishiFoodPreferenceTags", "wackyKoishiFoodHateTags", "wackyKoishiBeveragePreferenceTags" })
            {
                var changed = J.Obj(J.Clone(snapshot));
                var special = J.Obj(changed["specialBusiness"]);
                special[field] = field.EndsWith("Tags", StringComparison.Ordinal) ? new JsonArray("different")
                    : field == "requiredExtraIngredientIds" ? new JsonArray(99)
                    : field == "yuumaFoodTargetRevision" ? JsonValue.Create(2)
                    : field == "challengeTypeAvailable" ? JsonValue.Create(false)
                    : field is "active" or "wackyKoishiShieldBroken" ? JsonValue.Create(true) : JsonValue.Create("different");
                host.Publish(changed, catalog);
                Check(host.Query(client, normalIntent)["result"] == null, "特殊经营语义变化未清除旧展示：" + field);
            }
            var changedCatalog = J.Obj(J.Clone(catalog)); changedCatalog["status"] = "different-catalog";
            host.Publish(snapshot, changedCatalog);
            Check(host.Query(client, normalIntent)["result"] == null && host.Status()["recommendations"] == null,
                "目录先于快照签名变化时也必须清空旧展示。");
            host.Publish(snapshot, catalog);
            Check(host.Query(client, normalIntent)["result"] != null, "恢复原作用域应可继续查看上次成功结果。");

            var changedIntent = J.Obj(J.Clone(normalIntent)); changedIntent["selectedPlace"] = "人间之里";
            Check(host.Query(client, changedIntent)["result"] == null, "更换页面选择不能继承上次意图的结果。");
            var sourceContext = J.Obj(before["sourceContext"]);
            foreach (var changedAuthority in new[]
            {
                BusinessDisplayContext.Build(snapshot, "different-registry", authority.AuthorityRevision, authority.ActiveProfileRevision, authority.ActiveProfileHash),
                BusinessDisplayContext.Build(snapshot, authority.RegistryId, authority.AuthorityRevision + 1, authority.ActiveProfileRevision, authority.ActiveProfileHash),
                BusinessDisplayContext.Build(snapshot, authority.RegistryId, authority.AuthorityRevision, authority.ActiveProfileRevision + 1, authority.ActiveProfileHash),
                BusinessDisplayContext.Build(snapshot, authority.RegistryId, authority.AuthorityRevision, authority.ActiveProfileRevision, "different-profile"),
            }) Check(!BusinessDisplayContext.Matches(sourceContext, changedAuthority), "权威或配置变更不能复用展示作用域。");

            var updatedProfile = J.Obj(JsonNode.Parse(profile.GetRawText())); updatedProfile["automationEnabled"] = false;
            host.Import(J.Object(("snapshot", snapshot), ("catalog", catalog), ("profile", updatedProfile)), client);
            Check(host.Query(client, rareIntent)["result"] == null && host.Status()["recommendations"] == null,
                "真实设备配置更新必须立即清除另一查询槽位及经营旧展示。");
            release.Set();
            await host.Stop();
            Check(Volatile.Read(ref effects) == 0, "保留旧展示错误地重新授予了旧命令执行许可。");
            Console.WriteLine($"PASS display continuity: {checks} assertions; retained display never becomes current or grants execution.");
        }
        finally
        {
            release.Set(); await host.Stop(); Logger.Sources.Remove(log);
            var temporaryRoot = Path.GetFullPath("temp") + Path.DirectorySeparatorChar;
            if (directory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = Environment.TickCount64 + 8000;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException(message);
            await Task.Delay(20);
        }
    }
}
