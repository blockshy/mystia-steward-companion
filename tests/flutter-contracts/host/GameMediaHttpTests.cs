using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BepInEx.Logging;
using MystiaStewardCompanion.GameMedia;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Updates;

/// <summary>
/// 真实生产 HTTP 服务的图片契约。只使用合成 PNG 和独占临时缓存，所有游戏委托拒绝；
/// 唯一受控副作用是内存计数，专门验证已经进入队列的许可不能跨资源重读执行。
/// </summary>
internal static class GameMediaHttpTests
{
    internal static int Run(JsonElement profile)
    {
        var root = Path.Combine(Path.GetTempPath(), "mystia-media-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = Logger.CreateLogSource("game-media-http-isolated");
        var checks = 0; var effects = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new InvalidOperationException(label); }
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            using var media = new GameMediaService(new GameMediaStore(Path.Combine(root, "media")));
            var extractor = new Extractor(); media.Configure(extractor);
            using var update = new UpdateService(new UpdateServiceSettings { Enabled = false, AutoCheck = false }, log,
                Path.Combine(root, "updates"), () => DateTime.UtcNow, _ => throw Forbidden());
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            using var server = new LocalApiServer(false, "auto", port, "media-test", "synthetic-media-token",
                () => throw Forbidden(), (_, _) => throw Forbidden(), _ => throw Forbidden(),
                () => throw Forbidden(), _ => throw Forbidden(), _ => throw Forbidden(),
                (type, id, quantity, _, permit) =>
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("合成写入队列未释放");
                    return permit.Execute(() => { effects++; return new RuntimeInventoryEditResult {
                        ItemType = type, ItemId = id, RequestedQuantity = quantity, Quantity = quantity, Changed = true }; });
                },
                (_, _, _, _) => throw Forbidden(), _ => throw Forbidden(), _ => throw Forbidden(), _ => throw Forbidden(),
                1, _ => 0, _ => throw Forbidden(), () => throw Forbidden(),
                () => RuntimeAvailableMissionSnapshot.Unavailable(0, 0, RuntimeAvailableMissionSnapshot.NotAttachedStatus, "synthetic"),
                (_, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(),
                update, new FavoriteStore(Path.Combine(root, "favorites.json"), log),
                new CustomRecipeStore(Path.Combine(root, "custom-recipes.json"), log),
                new CompanionDeviceAuthorityStore(Path.Combine(root, "devices.json"), log), log, media);
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
            HttpResponseMessage Send(string path, bool post = false, string device = "media-primary-0001", long revision = 1,
                object? body = null, bool authenticated = true)
            {
                using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, path);
                if (authenticated) request.Headers.Add("X-Mystia-Steward-Companion-Token", "synthetic-media-token");
                request.Headers.Add("X-Mystia-Steward-Companion-Client-Id", device);
                request.Headers.Add("X-Mystia-Steward-Companion-Client-Label", "Synthetic");
                request.Headers.Add("X-Mystia-Steward-Companion-Authority-Revision", revision.ToString());
                if (post) request.Content = new StringContent(body == null ? "" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                return client.SendAsync(request).GetAwaiter().GetResult();
            }
            JsonElement Json(HttpResponseMessage response)
            {
                using (response) using (var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()))
                    return document.RootElement.Clone();
            }
            void Status(string path, HttpStatusCode expected, bool post = false, string device = "media-primary-0001", long revision = 1)
            { using var response = Send(path, post, device, revision); Check(response.StatusCode == expected, path + " HTTP " + response.StatusCode); }
            void Ready()
            {
                var watch = Stopwatch.StartNew();
                while (!media.IsReady)
                {
                    if (watch.ElapsedMilliseconds > 10000 || media.Status.Phase == "failed") throw new InvalidOperationException("合成资源未就绪");
                    media.Tick(); Thread.Sleep(1);
                }
            }
            using (var response = Send("/game-media/status", authenticated: false)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "状态需要认证");
            Check(Json(Send("/game-media/status")).GetProperty("phase").GetString() == "waiting", "初始waiting");
            Status("/game-media/manifest", HttpStatusCode.Conflict);
            var registration = new { protocolVersion = 1, profileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion, platform = "windows", appVersion = "1.3.1", profile };
            var authority = Json(Send("/devices/register", true, body: registration));
            Check(authority.GetProperty("authorityRevision").GetInt64() == 1, "未就绪仍可注册主设备");
            Json(Send("/devices/register", true, device: "media-observer-0002", body: registration));
            Status("/automation/lease/acquire", HttpStatusCode.Conflict, true);
            Status("/inventory/set?type=ingredient&id=1&qty=2", HttpStatusCode.Conflict, true);
            Status("/automation/lease/release", HttpStatusCode.OK, true);
            Status("/game-media/reload", HttpStatusCode.Forbidden, true, "media-observer-0002");
            Status("/game-media/reload", HttpStatusCode.Forbidden, true, revision: 2);
            Status("/game-media/reload", HttpStatusCode.OK, true);
            Ready();
            var manifest = Json(Send("/game-media/manifest"));
            var first = manifest.GetProperty("generation").GetString()!;
            Check(manifest.GetProperty("items").GetArrayLength() == 1, "真实清单整代返回");
            using (var response = Send("/game-media/image?generation=" + first + "&key=food%3A1"))
            {
                var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "image/png", "PNG响应类型");
                Check(response.Content.Headers.ContentLength == bytes.Length && bytes.SequenceEqual(Extractor.Png), "显式长度与实际PNG一致");
            }
            Status("/game-media/image?generation=" + first + "&key=..%2Fcurrent.json", HttpStatusCode.NotFound);
            Status("/game-media/image?generation=" + new string('0', 32) + "&key=food%3A1", HttpStatusCode.Conflict);
            Status("/automation/lease/acquire", HttpStatusCode.OK, true);
            // 不阻塞驱动主线程：后台只发 HTTP，主线程先触发重读并完成新代，再允许旧队列继续。
            var queued = Task.Run(() => Send("/inventory/set?type=ingredient&id=1&qty=2", true));
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "旧写入已进入队列");
            Status("/game-media/reload", HttpStatusCode.OK, true);
            Check(!media.IsReady, "重读立即撤销ready");
            Status("/game-media/reload", HttpStatusCode.OK, true);
            Status("/automation/lease/acquire", HttpStatusCode.Conflict, true);
            Status("/automation/lease/release", HttpStatusCode.OK, true);
            Ready();
            Check(extractor.Calls == 2 && media.Manifest().Generation != first, "HTTP重复重读合并单次新代");
            release.Set();
            Check(Json(queued.GetAwaiter().GetResult()).GetProperty("error").GetString() == "game-media-generation-stale"
                && effects == 0, "旧队列跨代拒绝且无副作用");
            entered.Reset();
            Check(Json(Send("/inventory/set?type=ingredient&id=1&qty=2", true)).GetProperty("ok").GetBoolean()
                && effects == 1, "当前代合法许可确实执行一次");
            Console.WriteLine($"PASS game-media-real-http: {checks} checks");
            return 0;
        }
        finally
        {
            release.Set(); RuntimeAutomationControlState.ConfigureResourceReadiness(null);
            Logger.Sources.Remove(log); Directory.Delete(root, recursive: true);
        }
    }
    private static InvalidOperationException Forbidden() => new("隔离图片测试不允许访问游戏或更新委托");
    private sealed class Extractor : IRuntimeGameMediaExtractor
    {
        // 固定1x1透明PNG，身份与目录完全合成；不读取纳管图片或真实游戏文件。
        internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        internal int Calls;
        public bool TryCreatePlan(out GameMediaPlan? plan, out string error)
        { plan = new(new string('c', 64), new[] { new GameMediaKey("food", 1, "合成料理") }, Array.Empty<GameMediaRecipe>()); error = ""; return true; }
        public GameMediaExtractState Extract(GameMediaKey item, out GameMediaImage? image, out string message)
        { Calls++; image = new(Png.ToArray(), 1, 1); message = ""; return GameMediaExtractState.Ready; }
    }
}
