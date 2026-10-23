using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Updates;
using MystiaStewardCompanion.Business.Domain.Orders;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 当前订单显示独立于慢方案的隔离回归：先覆盖纯显示缓存，再通过真实生产 HTTP 路由
/// 验证固定时间顺序、跨通道位置与已删除配置的拒绝。全部身份、订单和目录为内存合成；
/// 任何游戏动作/更新委托均显式拒绝。
/// </summary>
internal static class SnapshotPresentationTests
{
    internal static int Run(JsonElement profile)
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        string[] Ids(JsonElement value, string business) => value.GetProperty(business).GetProperty("orders")
            .EnumerateArray().Select(order => order.GetProperty("traceId").GetString()!).ToArray();
        JsonElement Parse(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
        JsonObject Order(string trace, int guest, int second, int desk) => J.Object(("traceId", trace),
            ("guestId", guest), ("guestName", "合成客人" + guest), ("deskCode", desk),
            ("orderLifecycleSequence", 1), ("firstSeenAtUtc", $"2026-10-12T00:00:{second:D2}Z"),
            ("foodName", "合成料理"), ("beverageName", "合成酒水"), ("foodTag", "素"), ("beverageTag", "水果"));
        var snapshot = J.Object(("snapshotSignature", "source-one"), ("automationSessionId", "synthetic-session"),
            ("runtimeLoaded", false), ("runtimeDataComplete", false), ("nightBusinessGeneration", 0),
            ("nightBusinessLifecyclePhase", "Inactive"), ("nightBusinessAutomationAllowed", false),
            ("nightBusiness", J.Object(("orders", new JsonArray(Order("R-3", 1, 3, 0), Order("R-2", 2, 2, 1), Order("R-1", 1, 1, 0))),
                ("status", "合成读取状态"), ("activeRareGuests", new JsonArray()))),
            ("normalBusiness", J.Object(("orders", new JsonArray(Order("N-3", 3, 2, 2), Order("N-2", 3, 2, 1), Order("N-1", 3, 1, 2))))),
            ("specialBusiness", null), ("recommendationState", J.Object(("unrecognizedField", new JsonArray(1, 2, "原样保留")))),
            ("automationEvents", new JsonArray()), ("automationCookingJobs", new JsonArray()));
        var raw = snapshot.ToJsonString();
        var projector = new LocalApiSnapshotPresentation();
        var ordered = projector.Get(raw, "source-one");
        var orderedJson = Parse(ordered.Json);
        Check(Ids(orderedJson, "nightBusiness").SequenceEqual(new[] { "R-1", "R-2", "R-3" }), "到单模式排序错误。");
        Check(Ids(orderedJson, "normalBusiness").SequenceEqual(new[] { "N-1", "N-2", "N-3" }), "普客时间/桌号排序错误。");
        Check(orderedJson.GetProperty("snapshotSignature").GetString() == "source-one", "显示层改写了业务快照签名。");
        Check(orderedJson.GetProperty("recommendationState").GetProperty("unrecognizedField")[2].GetString() == "原样保留",
            "显示层丢弃了未知业务字段。");
        Check(ReferenceEquals(ordered, projector.Get(new string(raw.ToCharArray()), "source-one")),
            "同内容不同字符串实例未复用单槽缓存。");
        var parallel = Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => projector.Get(raw, "source-one"))))
            .GetAwaiter().GetResult();
        Check(parallel.All(result => ReferenceEquals(result, ordered)), "并发读取重复建立显示投影。");
        var merged = new[] { "nightBusiness", "normalBusiness" }.SelectMany(name => orderedJson.GetProperty(name)
            .GetProperty("orders").EnumerateArray()).OrderBy(order => order.GetProperty("serviceOrderPosition").GetInt32()).ToArray();
        Check(merged.Select(order => order.GetProperty("traceId").GetString()).SequenceEqual(new[] { "N-1", "R-1", "N-2", "N-3", "R-2", "R-3" }),
            "普/稀客合并未按共同点单时间和稳定身份排列。");
        Check(merged.Select(order => order.GetProperty("serviceOrderPosition").GetInt32()).SequenceEqual(Enumerable.Range(0, 6)),
            "跨通道显示位置不是唯一连续整数。");
        Check(snapshot.ToJsonString() == raw, "排序污染了原始输入。");

        var special = J.Obj(J.Clone(snapshot));
        special["specialBusiness"] = J.Object(("active", true), ("challengeTypeAvailable", true), ("challengeType", "Story_Mizuchi"));
        var rare = J.Arr(special["nightBusiness"]?["orders"]);
        J.Obj(rare[0])["specialBusinessRole"] = "mizuchi-story-possessed-order";
        J.Obj(rare[1])["specialBusinessRole"] = "mizuchi-story-ordinary-order";
        J.Obj(rare[2])["specialBusinessRole"] = "mizuchi-story-unverified-order";
        Check(Ids(Parse(projector.Get(special.ToJsonString(), "special").Json), "nightBusiness")
            .SequenceEqual(new[] { "R-1", "R-2", "R-3" }), "特殊角色不得抢占经营显示中的先到订单。");
        Check(OrderIdentityAndSorting.SortRareForExecution(J.Objects(rare), special["specialBusiness"])
            .Select(order => J.Str(order["traceId"])).SequenceEqual(new[] { "R-3", "R-2", "R-1" }),
            "固定显示顺序意外改变了特殊经营执行优先级。");
        var sameTime = new[] { Order("R-b", 1, 1, 0), Order("R-a", 2, 1, 0) };
        Check(OrderIdentityAndSorting.SortRare(sameTime).Select(order => J.Str(order["traceId"]))
            .SequenceEqual(OrderIdentityAndSorting.SortRare(sameTime.Reverse()).Select(order => J.Str(order["traceId"]))),
            "同刻订单顺序依赖输入集合顺序，未使用稳定身份。");
        var unknownTime = Order("R-unknown", 1, 1, 0);
        unknownTime["firstSeenAtUtc"] = "invalid"; unknownTime["lastSeenAtUtc"] = "2020-01-01T00:00:00Z";
        Check(ReferenceEquals(OrderIdentityAndSorting.SortRare(new[] { unknownTime, sameTime[0] }).Last(), unknownTime),
            "未知首次时间不能借最近观察时间冒充早到订单。");

        // 错误读取携带的旧订单不能通过显示排序被重新包装成正常列表；未知/非法形状也原样保留。
        special["specialBusiness"] = null;
        J.Obj(special["nightBusiness"])["error"] = "合成读取失败";
        var failed = Parse(projector.Get(special.ToJsonString(), "failed").Json);
        Check(Ids(failed, "nightBusiness").SequenceEqual(new[] { "R-3", "R-2", "R-1" })
            && failed.GetProperty("nightBusiness").GetProperty("error").GetString() == "合成读取失败", "错误快照被重新排序或清除了错误。");
        special["nightBusiness"] = null;
        J.Obj(special["normalBusiness"])["orders"] = new JsonArray("非法条目", null);
        var malformed = Parse(projector.Get(special.ToJsonString(), "malformed").Json);
        Check(malformed.GetProperty("nightBusiness").ValueKind == JsonValueKind.Null
            && malformed.GetProperty("normalBusiness").GetProperty("orders")[0].GetString() == "非法条目", "未知读取状态被伪装成空列表。");

        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetFullPath(Path.Combine(tempRoot, "mystia-snapshot-presentation-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var log = Logger.CreateLogSource("snapshot-presentation-isolated");
        var effects = 0;
        InvalidOperationException Forbidden() { Interlocked.Increment(ref effects); return new("隔离排序测试不允许访问游戏或更新。"); }
        try
        {
            var settings = J.Obj(JsonNode.Parse(profile.GetRawText()));
            settings["automationEnabled"] = false;
            var store = new CompanionDeviceAuthorityStore(Path.Combine(root, "devices.json"), log);
            var authority = store.Register("snapshot-primary-0001", "Synthetic", new CompanionDeviceRegisterRequest
            { ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                Platform = "windows", AppVersion = "test", Profile = JsonSerializer.SerializeToElement(settings) }, DateTime.UtcNow);
            using var update = new UpdateService(new UpdateServiceSettings { Enabled = false, AutoCheck = false }, log,
                Path.Combine(root, "updates"), () => DateTime.UtcNow, _ => throw Forbidden());
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            using var server = new LocalApiServer(false, "auto", port, "snapshot-test", "synthetic-snapshot-token",
                () => throw Forbidden(), (_, _) => throw Forbidden(), _ => throw Forbidden(),
                () => throw Forbidden(), _ => throw Forbidden(), _ => throw Forbidden(),
                (_, _, _, _, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(),
                _ => throw Forbidden(), _ => throw Forbidden(), _ => throw Forbidden(), 1, _ => 0, _ => throw Forbidden(),
                () => throw Forbidden(), () => RuntimeAvailableMissionSnapshot.Unavailable(0, 0, RuntimeAvailableMissionSnapshot.NotAttachedStatus, "synthetic"),
                (_, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(),
                update, new FavoriteStore(Path.Combine(root, "favorites.json"), log),
                new CustomRecipeStore(Path.Combine(root, "custom-recipes.json"), log), store, log);
            server.SetSnapshotJson(raw, "source-one"); server.Start();
            using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
            JsonElement Send(string path, object? body = null, HttpStatusCode expectedStatus = HttpStatusCode.OK)
            {
                using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, path);
                request.Headers.Add("X-Mystia-Steward-Companion-Token", "synthetic-snapshot-token");
                request.Headers.Add("X-Mystia-Steward-Companion-Client-Id", "snapshot-primary-0001");
                request.Headers.Add("X-Mystia-Steward-Companion-Client-Label", "Synthetic");
                request.Headers.Add("X-Mystia-Steward-Companion-Authority-Revision", authority.AuthorityRevision.ToString());
                if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var response = client.SendAsync(request).GetAwaiter().GetResult();
                if (response.StatusCode != expectedStatus) throw new InvalidOperationException(path + " HTTP " + response.StatusCode);
                return Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
            string Known(string token) => "/snapshot?knownSignature=source-one&knownOrderPresentationSignature=" + Uri.EscapeDataString(token);
            using (var unauthenticated = client.GetAsync("/snapshot").GetAwaiter().GetResult())
                Check(unauthenticated.StatusCode == HttpStatusCode.Unauthorized, "实时排序绕过了原有Token认证。");
            var first = Send("/snapshot"); var firstToken = first.GetProperty("orderPresentationSignature").GetString()!;
            Check(Ids(first, "nightBusiness").SequenceEqual(new[] { "R-1", "R-2", "R-3" }), "真实HTTP未使用C#显示排序。");
            Check(!Send("/snapshot?knownSignature=source-one").TryGetProperty("unchanged", out _), "只带业务签名错误复用了未知显示顺序。");
            Check(Send(Known(firstToken)).GetProperty("unchanged").GetBoolean(), "双签名相同不能使用unchanged。");
            var obsolete = J.Obj(J.Clone(settings)); obsolete["serviceOrderSortMode"] = "guest";
            var rejected = Send("/devices/profile", new { protocolVersion = 1, profileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                expectedAuthorityRevision = authority.AuthorityRevision, expectedProfileRevision = authority.CurrentDeviceProfileRevision,
                profile = obsolete }, HttpStatusCode.BadRequest);
            Check(!rejected.GetProperty("ok").GetBoolean(), "当前HTTP仍接受已删除的排序字段。");
            Check(!Send(Known("order-v1:guest:source-one")).TryGetProperty("unchanged", out _),
                "旧排序展示令牌不能复用成当前固定显示投影。");
            settings["recommendationBudgetPolicy"] = "warn";
            var saved = Send("/devices/profile", new { protocolVersion = 1, profileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                expectedAuthorityRevision = authority.AuthorityRevision, expectedProfileRevision = authority.CurrentDeviceProfileRevision,
                profile = settings });
            Check(saved.GetProperty("ok").GetBoolean(), "正常共享配置变更被错误拒绝。");
            Check(Send(Known(firstToken)).GetProperty("unchanged").GetBoolean(),
                "其它配置变化不应影响固定订单顺序或显示缓存。");
            var rawField = typeof(LocalApiServer).GetField("_snapshotJson", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var signatureField = typeof(LocalApiServer).GetField("_snapshotSignature", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Check((string?)rawField.GetValue(server) == raw && (string?)signatureField.GetValue(server) == "source-one",
                "HTTP显示排序污染了后台计算所用原文/签名。");
            Check(effects == 0, "只读排序触发了游戏或更新动作。");
            Console.WriteLine($"PASS snapshot-presentation: {checks} checks; exact sorting, independent token, unchanged, errors and raw business ownership.");
            return 0;
        }
        finally
        {
            Logger.Sources.Remove(log);
            // 只清理本方法创建的唯一临时目录，不触碰仓库、游戏或用户配置。
            if (root.StartsWith(tempRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, recursive: true);
        }
    }
}
