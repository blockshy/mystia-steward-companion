using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Updates;

// 跨语言宿主的控制输出固定 UTF-8，避免 Windows 控制台代码页破坏中文诊断。
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
if (Environment.Version.Major != 6) throw new InvalidOperationException("契约宿主必须使用真实 .NET 6 运行时。");
if (args.Length == 2 && args[0] == "--management")
{
    using var managementProfile = JsonDocument.Parse(File.ReadAllText(args[1]));
    return ManagementHttpTests.Run(managementProfile.RootElement.Clone());
}
if (args.Length == 2 && args[0] == "--game-media")
{
    using var mediaProfile = JsonDocument.Parse(File.ReadAllText(args[1]));
    return GameMediaHttpTests.Run(mediaProfile.RootElement.Clone());
}
// 真实 HTTP 显示排序独立于后台求解，使用合成订单、临时设备配置及空闲回环端口验证。
if (args.Length == 2 && args[0] == "--snapshot-presentation")
{
    using var presentationProfile = JsonDocument.Parse(File.ReadAllText(args[1]));
    return SnapshotPresentationTests.Run(presentationProfile.RootElement.Clone());
}
// 仅使用合成身份与独占临时目录。游戏/更新委托默认拒绝；指定 fixture 仅更新内存计数，
// 用于验证排队许可的正反向路径，禁止读取用户凭据、存档或启动游戏。
var withGameMedia = args.Length == 3 && args[2] == "--with-game-media";
if ((!withGameMedia && args.Length != 2) || args[0] is not ("recovery" or "full" or "corrupt" or "future")) return 2;
var scenario = args[0];
using var profileFile = JsonDocument.Parse(File.ReadAllText(args[1]));
var profile = profileFile.RootElement.Clone();
var root = Path.Combine(Path.GetTempPath(), "mystia-flutter-contracts-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var log = Logger.CreateLogSource("identity-migration-p0");
log.LogEvent += (_, entry) => Console.Error.WriteLine(entry.Data);
var path = Path.Combine(root, "companion-devices.json");
try
{
    var initial = new CompanionDeviceAuthorityStore(path, log);
    var count = scenario == "full" ? 32 : 1;
    for (var index = 1; index <= count; ++index)
        initial.Register($"p0-old-device-{index:D8}", "Synthetic old device", new CompanionDeviceRegisterRequest
        {
            ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion, Platform = "windows", AppVersion = "1.3.1",
            Profile = profile,
        }, DateTime.UtcNow.AddMinutes(-5));
    if (scenario == "corrupt") File.WriteAllText(path, "{broken-preserved");
    if (scenario == "future") File.WriteAllText(path, "{\"version\":999,\"future\":true}");
    // 重新加载清除在线心跳，验证历史主设备离线时仍能恢复持久化配置。
    var store = new CompanionDeviceAuthorityStore(path, log);
    // 仅显式请求的 UI 联调启用完整生产资源服务；普通身份契约仍保留可选依赖为 null 的范围。
    // 托管专属驱动线程模拟 Unity Tick，避免异步 stdin 恢复线程变化触碰主线程检查。
    using var gameMedia = withGameMedia ? SyntheticGameMediaHost.Start(Path.Combine(root, "game-media")) : null;
    using var queuedWriteRelease = new ManualResetEventSlim(false);
    var manualEffects = 0;
    TResult ExecuteSyntheticWrite<TResult>(LocalApiWritePermit permit, bool queue, Func<TResult> action)
    {
        if (!queue) return permit.Execute<TResult>(() => throw Forbidden());
        // 模拟 HTTP 已接受、Unity 尚未消费的队列，不调用真实游戏对象。
        queuedWriteRelease.Reset();
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "manual-write-queued" }));
        Console.Out.Flush();
        if (!queuedWriteRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic queue was not released.");
        return permit.Execute(() =>
        {
            Interlocked.Increment(ref manualEffects);
            return action();
        });
    }
    using var update = new UpdateService(new UpdateServiceSettings { Enabled = false, AutoCheck = false }, log,
        Path.Combine(root, "updates"), () => DateTime.UtcNow, _ => throw Forbidden());
    var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
    var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
    // 合成连接设置只在临时宿主内存中修改；用于真实客户端改密回执后接续，不开启任何 LAN 监听。
    LocalApiServer? activeServer = null;
    var fixtureToken = "flutter-contracts-synthetic-token";
    var fixtureLanHost = "auto";
    var fixtureLanEnabled = false;
    LocalApiConnectionConfigDto FixtureConnection() => new()
    {
        Token = fixtureToken, Port = port, LocalEndpoint = $"http://127.0.0.1:{port}",
        LanBindHost = fixtureLanHost, LanEnabled = fixtureLanEnabled,
        AvailableLanEndpoints = activeServer?.AvailableLanEndpoints ?? Array.Empty<LocalApiLanEndpointDto>(),
    };
    using var server = new LocalApiServer(false, "auto", port, "flutter-contracts", "flutter-contracts-synthetic-token",
        // 仅返回本次隔离目录中的日志元数据，不启动聚合日志服务，也不读取或修改真实用户日志。
        // 客户端诊断页同时读取设置与导出包列表；两者都必须能在真实 HTTP 夹具中安全读取。
        () => new LocalApiLogSettings
        {
            AggregateModLogEnabled = false,
            AggregateModLogPath = Path.Combine(root, "logs", "aggregate-mod.log"),
            BepInExConsoleSupported = false,
            BepInExConsoleStatus = "隔离宿主不控制本机控制台。",
        }, (_, _) => throw Forbidden(), _ => throw Forbidden(),
        FixtureConnection, change =>
        {
            fixtureToken = change.Token ?? fixtureToken;
            fixtureLanHost = change.LanBindHost ?? fixtureLanHost;
            fixtureLanEnabled = change.LanEnabled ?? fixtureLanEnabled;
            return FixtureConnection();
        }, _ => throw Forbidden(),
        (type, id, quantity, expectedQuantity, permit) => withGameMedia && SyntheticInventoryFixture.Contains(id)
            ? permit.Execute(() => SyntheticInventoryFixture.Set(type, id, quantity, expectedQuantity))
            : ExecuteSyntheticWrite(permit, id == 9901,
                () => new RuntimeInventoryEditResult { ItemType = type, ItemId = id, RequestedQuantity = quantity, Quantity = quantity, Changed = true }),
        (type, ids, quantity, permit) => withGameMedia && ids.All(SyntheticInventoryFixture.Contains)
            ? permit.Execute(() => SyntheticInventoryFixture.SetAll(type, ids, quantity))
            : ExecuteSyntheticWrite(permit, ids.Contains(9901),
                () => new RuntimeInventoryBulkEditResult { ItemType = type, RequestedQuantity = quantity, Total = ids.Count, Changed = ids.Count }),
        _ => throw Forbidden(), _ => throw Forbidden(), _ => throw Forbidden(),
        1, _ => 0, _ => throw Forbidden(), () => throw Forbidden(),
        () => RuntimeAvailableMissionSnapshot.Unavailable(0, 0, RuntimeAvailableMissionSnapshot.NotAttachedStatus, "synthetic-game-not-attached"),
        (_, _) => throw Forbidden(),
        (scope, _, _, permit) => ExecuteSyntheticWrite(permit, scope == "fixture-queued", () => new RareGuestInvitationResult { Ok = true }),
        (_, scope, _, permit) => ExecuteSyntheticWrite(permit, scope == "fixture-queued", () => new RareGuestInvitationResult { Ok = true }),
        update, new FavoriteStore(Path.Combine(root, "favorites.json"), log),
        new CustomRecipeStore(Path.Combine(root, "custom-recipes.json"), log), store, log, gameMedia?.Service,
        new DiagnosticPackageStore(Path.Combine(root, "diagnostic-packages")));
    activeServer = server;
    // 白天的合成快照不包含任何 Unity 对象；用于新客户端握手和当前业务 JSON 信封验证。
    server.SetSnapshotJson(@"{""snapshotSignature"":""fixture-1"",""automationSessionId"":""fixture-session"",""nightBusinessGeneration"":0,""nightBusinessLifecyclePhase"":""Inactive"",""runtimeLoaded"":false,""runtimeDataComplete"":false,""runtimeDataSignature"":"""",""automationEvents"":[],""automationCookingJobs"":[],""nightBusiness"":{""orders"":[],""activeRareGuests"":[]},""normalBusiness"":{""orders"":[]},""specialBusiness"":null,""recommendationState"":{}}", "fixture-1");
    VerifyInteractionScope();
    if (withGameMedia) SyntheticInventoryFixture.VerifySnapshotSignature();
    server.Start();
    // 由生产业务程序集直接导出预设，不在测试宿主再次手写权重。
    // Dart 只持有表单默认值，契约测试对比完整目标列表、方向和权重。
    var sortPresets = new[] { "balanced", "resources", "profit", "simple" }.ToDictionary(
        preset => preset,
        MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationEngine.BuildDefaultRecommendationSortProfile);
    var orderPresentation = OrderPresentationFixture.Build(profile);
    Console.WriteLine(JsonSerializer.Serialize(new { kind = "flutter-contracts-real-local-api", port, root, scenario, sortPresets, orderPresentation }));
    Console.Out.Flush();
    // 只通过宿主 stdin 推进合成确认边界；不增加生产 HTTP 控制接口，不调用游戏生命周期 Hook。
    // 两个快照都故意重用 sequence=1，验证旧确认不能因当前会话变化而被重新绑定。
    while (await Console.In.ReadLineAsync() is { Length: > 0 } command)
    {
        if (command == "release-manual-write")
        {
            queuedWriteRelease.Set();
            continue;
        }
        if (command == "manual-write-effects")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "manual-write-effects", count = Volatile.Read(ref manualEffects) }));
            Console.Out.Flush();
            continue;
        }
        var scope = command switch
        {
            "confirmation-before" => (Session: "confirmation-process-before", Generation: 1L),
            "confirmation-after" => (Session: "confirmation-process-after", Generation: 2L),
            _ => throw new InvalidOperationException("Unknown isolated host control command."),
        };
        server.SetSnapshotJson(JsonSerializer.Serialize(new
        {
            snapshotSignature = scope.Session, automationSessionId = scope.Session,
            nightBusinessGeneration = scope.Generation, nightBusinessLifecyclePhase = "Inactive",
            runtimeLoaded = false, runtimeDataComplete = false, runtimeDataSignature = "",
            automationEvents = new[] { new { sequence = 1, message = scope.Session } },
            automationCookingJobs = Array.Empty<object>(), recommendationState = new { },
            nightBusiness = new { orders = Array.Empty<object>(), activeRareGuests = Array.Empty<object>() },
            normalBusiness = new { orders = Array.Empty<object>() }, specialBusiness = (object?)null,
        }), scope.Session);
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "confirmation-scope", session = scope.Session, generation = scope.Generation }));
        Console.Out.Flush();
    }
    // stdin 结束只清理本进程创建的目录；不开放远程停止入口，不输出 Token。
    server.Dispose();
    Console.WriteLine("STOPPED");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("flutter-contracts-host-failed: " + exception.GetType().Name + ": " + exception.Message);
    return 1;
}
finally
{
    Logger.Sources.Remove(log);
    Directory.Delete(root, true);
}

static Exception Forbidden() => new InvalidOperationException("Game/update operation is outside the identity P0 fixture.");

// 纯值契约验证包含进程重启后 sequence 可重用的前提：会话不同即拒绝，不依赖事件序号。
static void VerifyInteractionScope()
{
    var snapshot = System.Text.Json.Nodes.JsonNode.Parse("{\"automationSessionId\":\"new-session\",\"nightBusinessGeneration\":2,\"nightBusinessLifecyclePhase\":\"Active\"}")!.AsObject();
    MystiaStewardCompanion.Contracts.ClientProtocol.ValidateInteractionScope(2, "new-session", 2, snapshot);
    foreach (var request in new[] { (1, "new-session", 2L), (2, "old-session", 2L), (2, "new-session", 1L) })
    {
        var rejected = false;
        try { MystiaStewardCompanion.Contracts.ClientProtocol.ValidateInteractionScope(request.Item1, request.Item2, request.Item3, snapshot); }
        catch (ArgumentException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("旧客户端/会话/经营代次未被拒绝。");
    }
}
