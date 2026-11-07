using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BepInEx.Configuration;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Plugin;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Updates;

/// <summary>
/// 连接与诊断管理的生产 HTTP/文件边界回归。仅使用新建临时目录、合成密码与空闲回环端口；
/// 不读取用户配置、不调用游戏委托、不启动客户端，也不打开真正 LAN 监听。
/// </summary>
internal static class ManagementHttpTests
{
    internal static int Run(JsonElement profile)
    {
        var root = Path.Combine(Path.GetTempPath(), "mystia-management-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = Logger.CreateLogSource("management-isolated");
        var checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new InvalidOperationException(label); }
        void Reject(Action action, string label)
        {
            var rejected = false;
            try { action(); } catch (Exception) { rejected = true; }
            Check(rejected, label);
        }
        try
        {
            foreach (var valid in new[] { "pass1234", new string('x', 128), "a!@#$%^&*()[]{}:;\"'=+?\\/" })
                Check(LocalApiConnectionPolicy.IsUsableToken(valid), "合法自定义密码被拒绝。");
            foreach (var invalid in new[] { "", "short", new string('x', 129), "abc defg", "password\n", "密码123456", " abcdefgh" })
                Check(!LocalApiConnectionPolicy.IsUsableToken(invalid), "非法密码未拒绝。");
            var available = new[] { new LocalApiLanEndpointDto { Address = "192.168.8.2" } };
            LocalApiConnectionPolicy.Validate(new() { LanBindHost = "192.168.8.2", Token = "pass1234" }, available);
            LocalApiConnectionPolicy.Validate(new() { LanBindHost = "auto" }, Array.Empty<LocalApiLanEndpointDto>());
            foreach (var invalid in new[] { "", "0.0.0.0", "127.0.0.1", "192.168.8.3", "example.com", "192.168.8.2 " })
                Reject(() => LocalApiConnectionPolicy.Validate(new() { LanBindHost = invalid }, available), "非实时网卡地址未拒绝。");

            VerifyConfigurationSave(root, Check, Reject);
            VerifyQueuedCredentials(Check);
            var packages = new DiagnosticPackageStore(Path.Combine(root, "packages"));
            VerifyPackages(root, packages, Check, Reject);
            VerifyHttp(root, packages, profile, log, Check);
            Console.WriteLine($"PASS: management {checks} assertions (connection/password handoff, HTTP authority and update origin, diagnostic ZIP scope and exact deletion).");
            return 0;
        }
        catch (Exception exception)
        {
            // 所有输入都是合成值；不输出请求/Token 或文件正文。
            Console.Error.WriteLine("management-failed: " + exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
        finally { Logger.Sources.Remove(log); Directory.Delete(root, true); }
    }

    private static void VerifyConfigurationSave(string root, Action<bool, string> check, Action<Action, string> reject)
    {
        var path = Path.Combine(root, "synthetic.cfg");
        var file = new ConfigFile(path, false);
        var config = StewardPluginConfig.Bind(file);
        config.SaveLocalApiConnection(false, "auto", "custom123");
        check(config.LocalApiToken.Value == "custom123", "自定义密码未保存到配置内存。");
        var reloaded = StewardPluginConfig.Bind(new ConfigFile(path, false));
        check(reloaded.LocalApiToken.Value == "custom123" && LocalApiConnectionPolicy.IsUsableToken(reloaded.LocalApiToken.Value),
            "再次启动不能保留合法短自定义密码。");
        // 用目录占据文件目标精确制造保存失败，无需更改 ACL 或访问用户配置。
        File.Delete(path); Directory.CreateDirectory(path);
        reject(() => config.SaveLocalApiConnection(true, "192.168.8.2", "changed1"), "保存失败没有传播。");
        check(config.LocalApiToken.Value == "custom123" && !config.LocalApiLanEnabled.Value && config.LocalApiLanHost.Value == "auto",
            "保存失败后内存留下部分新设置。");
        check(file.SaveOnConfigSet, "保存失败后自动保存开关未恢复。");
    }

    private static void VerifyQueuedCredentials(Action<bool, string> check)
    {
        var credentials = new CompanionConnectionToken();
        credentials.Set("original1");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var changing = new ManualResetEventSlim();
        var old = Task.Run(() => credentials.Use("captured0", token =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("合成发送未释放。");
            return token;
        }));
        check(entered.Wait(TimeSpan.FromSeconds(5)), "合成发送未开始。");
        var update = Task.Run(() => { changing.Set(); credentials.Set("updated12"); });
        check(changing.Wait(TimeSpan.FromSeconds(5)), "合成更新未开始。");
        check(!update.IsCompleted, "旧发送未完成就返回改密成功。");
        release.Set();
        check(Task.WaitAll(new Task[] { old, update }, TimeSpan.FromSeconds(5)), "发送与凭据更新死锁。");
        check(old.Result == "original1", "已经发送的请求在执行中改变了凭据。");
        check(credentials.Use("captured0", token => token) == "updated12", "排队旧 options 重新发送了旧密码。");
        credentials.Reset();
        check(credentials.Use("nextgame", token => token) == "nextgame", "新游戏会话未清除旧凭据。");
    }

    private static void WritePackage(ZipArchive zip, string detail = "{}")
    {
        void Entry(string name, string content)
        { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8); writer.Write(content); }
        Entry("manifest.json", "{\"generatedAtUtc\":\"2026-10-21T00:00:00Z\",\"baseUrl\":\"http://127.0.0.1\",\"aggregateModLogMaxFileCount\":30}");
        Entry("snapshot/current-snapshot.json", detail);
        Entry("snapshot/runtime-data.json", "{}");
    }

    private static void VerifyPackages(string root, DiagnosticPackageStore store, Action<bool, string> check, Action<Action, string> reject)
    {
        check(store.List().Count == 0, "尚无导出目录时列表不是空。");
        var package = store.Create(zip => WritePackage(zip));
        var directory = Path.GetDirectoryName(package)!;
        var userFile = Path.Combine(directory, "user-backup.zip");
        File.WriteAllText(userFile, "user archive, must remain");
        var disguised = Path.Combine(directory, "mystia-steward-companion-diagnostics-20261021-000000.zip");
        using (var empty = ZipFile.Open(disguised, ZipArchiveMode.Create)) empty.CreateEntry("personal.txt");
        var item = store.List().Single();
        check(item.FileName == Path.GetFileName(package) && item.ByteLength == new FileInfo(package).Length && item.Id.Length == 64,
            "诊断包列举字段错误或误纳入用户文件。");
        foreach (var id in new[] { "../user-backup.zip", package, item.FileName, "", new string('0', 64) })
            reject(() => store.Delete(id), "任意路径或未列举标识可删除文件。");
        File.SetLastWriteTimeUtc(package, File.GetLastWriteTimeUtc(package).AddSeconds(1));
        reject(() => store.Delete(item.Id), "旧列表能删除被替换的版本。");
        check(File.Exists(package), "过期删除修改了原文件。");
        var current = store.List().Single();
        check(current.Id != item.Id, "文件版本变化后 ID 未变化。");
        store.Delete(current.Id);
        check(!File.Exists(package) && File.Exists(userFile) && File.Exists(disguised), "精确删除影响了其它用户文件。");
        reject(() => store.Delete(current.Id), "重复删除被报告成成功。");

        // 所有链接目标也在本测试临时根内；这里只证明链接不能扩大管理范围。
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        var outsideStore = new DiagnosticPackageStore(outside);
        var outsidePackage = outsideStore.Create(zip => WritePackage(zip));
        var link = Path.Combine(root, "package-link");
        Directory.CreateSymbolicLink(link, outside);
        reject(() => new DiagnosticPackageStore(link).List(), "目录重解析点未拒绝。");
        reject(() => new DiagnosticPackageStore(Path.Combine(link, "child")).Create(zip => WritePackage(zip)), "祖先重解析点未拒绝。");
        var fileLink = Path.Combine(directory, Path.GetFileName(outsidePackage));
        File.CreateSymbolicLink(fileLink, outsidePackage);
        reject(() => store.List(), "诊断文件重解析点未拒绝。");
        check(File.Exists(outsidePackage), "链接目标被修改。");
        File.Delete(fileLink); Directory.Delete(link);
        check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "导出留下临时文件。");
    }

    private static void VerifyHttp(string root, DiagnosticPackageStore packages, JsonElement profile, ManualLogSource log,
        Action<bool, string> check)
    {
        var store = new CompanionDeviceAuthorityStore(Path.Combine(root, "devices.json"), log);
        using var update = new UpdateService(new UpdateServiceSettings { Enabled = false, AutoCheck = false }, log,
            Path.Combine(root, "updates"), () => DateTime.UtcNow, _ => throw Forbidden());
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var currentToken = "original-password"; var changes = 0; var logChanges = 0; var maxFiles = 30;
        LocalApiServer? active = null;
        LocalApiConnectionConfigDto Configuration() => new()
        {
            LocalEndpoint = $"http://127.0.0.1:{port}", Port = port, Token = currentToken, LanBindHost = "auto",
            AvailableLanEndpoints = active?.AvailableLanEndpoints ?? Array.Empty<LocalApiLanEndpointDto>(),
        };
        LocalApiServer CreateServer(bool lanEnabled) => new(lanEnabled, "auto", port, "management-test", currentToken,
            () => new LocalApiLogSettings
            {
                AggregateModLogMaxFileCount = maxFiles,
                AggregateModLogPath = Path.Combine(root, "logs", "aggregate-mod.log"),
            }, (_, count) => { logChanges++; maxFiles = count ?? maxFiles; },
            _ => throw Forbidden(), Configuration, change => { changes++; currentToken = change.Token ?? currentToken; return Configuration(); },
            _ => throw Forbidden(), (_, _, _, _, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(),
            _ => throw Forbidden(), _ => throw Forbidden(), _ => throw Forbidden(), 1, _ => 0, _ => throw Forbidden(),
            () => throw Forbidden(), () => RuntimeAvailableMissionSnapshot.Unavailable(0, 0, RuntimeAvailableMissionSnapshot.NotAttachedStatus, "synthetic"),
            (_, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(), update,
            new FavoriteStore(Path.Combine(root, "favorites.json"), log), new CustomRecipeStore(Path.Combine(root, "custom.json"), log),
            store, log, diagnosticPackages: packages);
        using var server = CreateServer(false);
        active = server; server.Start();
        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
        JsonElement Send(string path, object? body = null, string? token = null, string device = "management-primary-0001",
            long revision = 1, HttpStatusCode expected = HttpStatusCode.OK, string? raw = null)
        {
            using var request = new HttpRequestMessage(body == null && raw == null ? HttpMethod.Get : HttpMethod.Post, path);
            request.Headers.Add("X-Mystia-Steward-Companion-Token", token ?? currentToken);
            request.Headers.Add("X-Mystia-Steward-Companion-Client-Id", device);
            request.Headers.Add("X-Mystia-Steward-Companion-Client-Label", "Synthetic");
            request.Headers.Add("X-Mystia-Steward-Companion-Authority-Revision", revision.ToString());
            if (body != null || raw != null) request.Content = new StringContent(raw ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            check(response.StatusCode == expected, path + " HTTP 状态不符。");
            using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return document.RootElement.Clone();
        }
        var registration = new { protocolVersion = 1, profileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
            platform = "windows", appVersion = "1.3.1", profile };
        Send("/devices/register", registration);
        Send("/devices/register", registration, device: "management-observer-0002");
        // 第二实例只构造策略，不调用 Start，因此不会开放真实 LAN 监听。
        // 合成来源复用已注册权威及关闭的更新服务，既验证 HTTP 路由又禁止启动更新进程。
        using var originServer = CreateServer(true);
        VerifyUpdateRequestOrigin(originServer, Path.Combine(root, "updates", "update-state.json"), currentToken, check);
        var config = Send("/local-api/config");
        check(config.GetProperty("availableLanEndpoints").ValueKind == JsonValueKind.Array
            && config.GetProperty("lanEndpoints").GetArrayLength() == 0, "LAN 关闭时缺少独立可选地址列表。");
        Send("/local-api/config", new { token = "updated-password" }, device: "management-observer-0002", expected: HttpStatusCode.Forbidden);
        Send("/local-api/config", new { token = "updated-password" }, revision: 2, expected: HttpStatusCode.Forbidden);
        foreach (var invalid in new[] { "{\"token\":\"short\",\"lanEnabled\":true}", "{\"token\":null}", "{\"lanHost\":\"0.0.0.0\",\"token\":\"valid123\"}",
            "{\"token\":\"password1\",\"token\":\"password2\"}", "{\"lanEnabled\":\"true\"}", "{}", "{\"path\":\"anything\"}" })
            Send("/local-api/config", expected: HttpStatusCode.BadRequest, raw: invalid);
        check(changes == 0 && currentToken == "original-password", "无效/未授权表单产生部分配置变更。");
        var changed = Send("/local-api/config", new { lanEnabled = false, lanHost = "auto", token = "newpass8" });
        check(changed.GetProperty("token").GetString() == "newpass8" && changes == 1, "改密回执未携带真正新密码。");
        Send("/local-api/config", token: "original-password", expected: HttpStatusCode.Unauthorized);
        Send("/local-api/config", new { token = "should-not-apply" }, token: "original-password", expected: HttpStatusCode.Unauthorized);
        check(Send("/local-api/config").GetProperty("token").GetString() == "newpass8" && changes == 1,
            "旧密码请求被重放，或新密码不能立即续接。");
        Send("/local-api/config", new { lanEnabled = false });
        check(currentToken == "newpass8", "省略 token 清除了当前密码。");
        Send("/local-api/token/regenerate", new { }, expected: HttpStatusCode.NotFound);
        foreach (var value in new[] { "0", "31", "9999", "invalid", "" })
            Send("/logs/config?aggregateLogMaxFiles=" + value, new { }, expected: HttpStatusCode.BadRequest);
        check(logChanges == 0, "非法日志上限调用了配置写入。");
        Send("/logs/config?aggregateLogMaxFiles=1", new { });
        check(maxFiles == 1, "日志数量 1 未应用。");
        Send("/logs/config?aggregateLogMaxFiles=30", new { });
        check(maxFiles == 30, "日志数量 30 未应用。");
        // 直接经过生产 HTTP 导出链，证明无需初始化全局 Paths.ConfigPath，
        // 返回目录、实际 ZIP 与后续列举删除始终属于同一个临时存储实例。
        // 统一数据目录后，配置和迁移备份与诊断包共享父目录。用合成敏感内容验证
        // 导出仍按条目白名单构建，不能通过递归压缩父目录意外收集配置或历史原件。
        const string privateMarker = "synthetic-config-must-not-enter-diagnostics";
        File.WriteAllText(Path.Combine(root, "com.tyukki.mystia-steward-companion.cfg"),
            "[LocalApi]\nToken = " + privateMarker, Encoding.UTF8);
        var migrationBackup = Path.Combine(root, "migration-backups", "synthetic-batch");
        Directory.CreateDirectory(migrationBackup);
        File.WriteAllText(Path.Combine(migrationBackup, "original.cfg"), privateMarker, Encoding.UTF8);
        var exported = Send("/logs/export-diagnostics?open=false", new { });
        check(exported.GetProperty("ok").GetBoolean(), "注入目录中的真实诊断导出失败。");
        var path = exported.GetProperty("path").GetString()!;
        check(exported.GetProperty("directory").GetString() == packages.DirectoryPath
            && Path.GetDirectoryName(path) == packages.DirectoryPath && File.Exists(path),
            "导出回执目录与实际注入存储不一致。");
        using (var archive = ZipFile.OpenRead(path))
        {
            check(archive.GetEntry("snapshot/current-snapshot.json") != null,
                "诊断导出缺少快照，不能用空归档通过隔离检查。");
            foreach (var entry in archive.Entries)
            {
                check(!entry.FullName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)
                    && !entry.FullName.StartsWith("migration-backups/", StringComparison.OrdinalIgnoreCase),
                    "诊断导出包含同目录配置或迁移备份。");
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                check(!reader.ReadToEnd().Contains(privateMarker, StringComparison.Ordinal),
                    "诊断导出收集了配置或备份中的合成敏感内容。");
            }
        }
        Send("/logs/packages", token: "wrong-password", expected: HttpStatusCode.Unauthorized);
        var listed = Send("/logs/packages", device: "management-observer-0002");
        var id = listed.GetProperty("items")[0].GetProperty("id").GetString()!;
        Send("/logs/packages/delete", new { id }, device: "management-observer-0002", expected: HttpStatusCode.Forbidden);
        Send("/logs/packages/delete", new { id }, revision: 2, expected: HttpStatusCode.Forbidden);
        check(File.Exists(path), "无权限删除修改了文件。");
        var bad = Send("/logs/packages/delete", new { id = "../outside.zip" });
        check(!bad.GetProperty("ok").GetBoolean() && File.Exists(path), "任意路径删除未拒绝。");
        check(Send("/logs/packages/delete", new { id }).GetProperty("ok").GetBoolean() && !File.Exists(path), "主设备精确删除未成功。");
        check(!Send("/logs/packages/delete", new { id }).GetProperty("ok").GetBoolean(), "重复删除伪报成功。");
    }

    /// <summary>
    /// 真实回环 socket 承载 HTTP 请求，只有传输层对端由合成值替代；生产入口始终读取 Socket.RemoteEndPoint。
    /// 不使用请求 Header 伪造测试来源，也不打开真正 LAN 监听。关闭的更新服务会在被调用时写状态，
    /// 因而可证明被拒绝的安装请求没有进入服务；本测试不证明真实安装或网络防火墙行为。
    /// </summary>
    private static void VerifyUpdateRequestOrigin(LocalApiServer server, string statePath, string token,
        Action<bool, string> check)
    {
        var handler = typeof(LocalApiServer).GetMethod("HandleClientCore", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(TcpClient), typeof(IPEndPoint), typeof(bool) }, null)
            ?? throw new InvalidOperationException("缺少 HTTP 传输处理入口。");
        var productionHandler = typeof(LocalApiServer).GetMethod("HandleClient", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(TcpClient) }, null)
            ?? throw new InvalidOperationException("缺少 HTTP socket 处理入口。");
        using (var closed = new TcpClient())
        {
            // 已关闭连接模拟关闭过程中的确定失败边界，生产入口不能在 try 之外读取 socket 而逃逸。
            closed.Dispose();
            productionHandler.Invoke(server, new object[] { closed });
            check(true, "关闭连接的异常没有被生产请求边界吸收。");
        }
        (HttpStatusCode Code, JsonElement Body) Send(IPAddress origin, string route, string forwardedFor,
            string device = "management-primary-0001")
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using var accepted = listener.AcceptTcpClient();
                listener.Stop();
                var handling = Task.Run(() => handler.Invoke(server, new object[] { accepted, new IPEndPoint(origin, 43210), true }));
                client.ReceiveTimeout = 5000;
                using var stream = client.GetStream();
                var request = $"POST {route} HTTP/1.1\r\nHost: localhost\r\n"
                    + $"X-Mystia-Steward-Companion-Token: {token}\r\n"
                    + $"X-Mystia-Steward-Companion-Client-Id: {device}\r\n"
                    + "X-Mystia-Steward-Companion-Client-Label: Synthetic\r\n"
                    + "X-Mystia-Steward-Companion-Authority-Revision: 1\r\n"
                    + $"X-Forwarded-For: {forwardedFor}\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{{}}";
                stream.Write(Encoding.UTF8.GetBytes(request));
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var response = reader.ReadToEnd();
                handling.GetAwaiter().GetResult();
                var status = (HttpStatusCode)int.Parse(response.Split(' ', 3)[1]);
                var bodyOffset = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                using var body = JsonDocument.Parse(response[(bodyOffset + 4)..]);
                return (status, body.RootElement.Clone());
            }
            finally { listener.Stop(); }
        }

        var lan = IPAddress.Parse("192.168.8.25");
        var denied = Send(lan, "/updates/install-on-exit", "127.0.0.1");
        check(denied.Code == HttpStatusCode.Forbidden, "LAN 主设备借自报回环 Header 绕过安装来源检查。");
        check(!File.Exists(statePath), "拒绝 LAN 安装后仍调用更新服务并写入状态。");
        var download = Send(lan, "/updates/download", "127.0.0.1");
        check(download.Code == HttpStatusCode.OK && !download.Body.GetProperty("ok").GetBoolean()
            && download.Body.GetProperty("error").GetString() == "自动更新已关闭。",
            "LAN 主设备下载未到达既有更新服务门禁。");
        check(File.Exists(statePath), "下载用例未真正进入关闭的更新服务。");
        var previous = File.ReadAllBytes(statePath);
        check(Send(lan, "/updates/install-on-exit", "127.0.0.1").Code == HttpStatusCode.Forbidden
            && previous.SequenceEqual(File.ReadAllBytes(statePath)), "拒绝 LAN 安装修改了既有更新状态。");
        var local = Send(IPAddress.Loopback, "/updates/install-on-exit", "192.168.8.25");
        check(local.Code == HttpStatusCode.OK && local.Body.GetProperty("error").GetString() == "自动更新已关闭。",
            "本机安装被请求 Header 误判为 LAN 或绕过服务门禁。");
        check(Send(lan, "/updates/download", "127.0.0.1", "management-observer-0002").Code == HttpStatusCode.Forbidden,
            "LAN 查看设备绕过主设备下载权限。");
    }

    private static Exception Forbidden() => new InvalidOperationException("隔离管理测试禁止游戏、更新或外部进程操作。");
}
