using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using BepInEx;
using BepInEx.Logging;
using MystiaStewardCompanion.Plugin;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Updates;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>
/// 运行在游戏进程内的本地 HTTP API，向伴随窗口暴露运行时快照并接收受控操作请求。
/// </summary>
/// <remarks>
/// 服务器使用轻量 <see cref="TcpListener"/>，避免在 IL2CPP Mod 中引入额外 Web 框架依赖。
/// 始终保留回环监听，LAN 监听只能作为显式开启的附加通道。除 <c>/health</c> 外所有端点都要求 Token。
/// GET 端点只读取状态，任何会修改运行时、配置、文件或外部进程状态的操作都只接受 POST。
/// </remarks>
internal sealed class LocalApiServer : IDisposable
{
    private const int MaxRequestBytes = 32768;
    private const int DiagnosticTailMaxBytes = 2 * 1024 * 1024;
    private const int DiagnosticTailMaxLines = 2000;
    private const int AutomationDecisionDiagnosticMaxLines = 12;
    private const int AutomationDecisionDiagnosticMaxTextLength = 600;
    private const int MaxConcurrentClientHandlers = 16;
    private const string AutoLanHost = "auto";
    private const string ClientIdHeaderName = "X-Mystia-Steward-Companion-Client-Id";
    private const string ClientLabelHeaderName = "X-Mystia-Steward-Companion-Client-Label";
    private static readonly TimeSpan AutomationLeaseTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ListenerStopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClientHandlerStopTimeout = TimeSpan.FromSeconds(3);

    private readonly ManualLogSource _log;
    private readonly object _snapshotLock = new();
    private readonly object _listenerLock = new();
    private readonly object _lanSettingsLock = new();
    private readonly object _automationLeaseLock = new();
    private readonly string _pluginVersion;
    private string _token;
    private bool _lanEnabled;
    private string _lanBindHost;
    private string _lanError = "";
    private readonly Func<LocalApiLogSettings> _getLogSettings;
    private readonly Action<bool?, int?> _updateLogSettings;
    private readonly Func<bool, BepInExConsoleWindowState> _updateBepInExConsoleVisibility;
    private readonly Func<LocalApiConnectionConfigDto> _getConnectionConfig;
    private readonly Func<LocalApiConnectionConfigUpdate, LocalApiConnectionConfigDto> _updateConnectionConfig;
    private readonly Func<LocalApiConnectionConfigDto> _regenerateLocalApiToken;
    private readonly Func<string, string> _openLogFolder;
    private readonly Func<string, int, int, RuntimeInventoryEditResult> _editInventory;
    private readonly Func<string, IReadOnlyList<int>, int, RuntimeInventoryBulkEditResult> _editInventoryBulk;
    private readonly Func<OrderPreparationRequest, OrderPreparationResult> _prepareOrder;
    private readonly Func<OrderPreparationRequest, OrderPreparationResult> _completeOrder;
    private readonly Func<OrderPreparationRequest, OrderPreparationResult> _completeNormalOrder;
    private readonly Func<long, int> _advanceAutomationCommandEpoch;
    private readonly Func<long, AutomationCommandCancellationResult> _cancelAutomationJobs;
    private readonly Func<long, AutomationSafetyBarrierAckResult> _ackAutomationSafetyBarrier;
    private readonly Func<RuntimeAvailableMissionSnapshot> _readAvailableMissions;
    private readonly Func<RuntimeAvailableMissionSnapshot> _getAvailableMissionSnapshot;
    private readonly Func<string, string, RareGuestInvitationResult> _listRareGuestInvitations;
    private readonly Func<string, string, RareGuestInvitationWriteExpectation, RareGuestInvitationResult> _inviteAllRareGuests;
    private readonly Func<int, string, RareGuestInvitationWriteExpectation, RareGuestInvitationResult> _inviteRareGuest;
    private readonly UpdateService _updateService;
    private readonly FavoriteStore _favoriteStore;
    private readonly CustomRecipeStore _customRecipeStore;
    private readonly BoundedHandlerPool<TcpClient> _clientHandlers;
    private readonly List<LocalApiListenerWorker> _listeners = new();
    private readonly List<LanAddressCandidate> _activeLanCandidates = new();
    private AutomationLease? _automationLease;
    private long _automationCommandEpoch;
    private bool _running;
    private bool _lanSettingsApplied;
    private string _snapshotJson = "{\"runtimeLoaded\":false,\"status\":\"Snapshot is not ready.\"}";
    private string _snapshotSignature = "";
    private string _runtimeDataJson = "{\"isComplete\":false,\"status\":\"Runtime data is not ready.\"}";
    private string _lastSnapshotRequestDiagnosticSignature = "";
    private string _lastAutomationDecisionDiagnosticSignature = "";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class AutomationLease
    {
        public string ClientId { get; init; } = "";
        public string ClientLabel { get; init; } = "";
        public DateTime LastSeenUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
    }

    private sealed class LanAddressCandidate
    {
        public IPAddress Address { get; init; } = IPAddress.None;
        public string InterfaceName { get; init; } = "";
        public NetworkInterfaceType InterfaceType { get; init; }
        public bool HasGateway { get; init; }
        public bool LinkLocal { get; init; }
    }

    private sealed class SnapshotRequestSummary
    {
        public string CapturedAtUtc { get; init; } = "";
        public string Scene { get; init; } = "";
        public int NormalOrders { get; init; } = -1;
        public bool SpecialActive { get; init; }
        public string ChallengeType { get; init; } = "";
        public string Phase { get; init; } = "";
    }

    public LocalApiServer(
        bool lanEnabled,
        string lanBindHost,
        int port,
        string pluginVersion,
        string token,
        Func<LocalApiLogSettings> getLogSettings,
        Action<bool?, int?> updateLogSettings,
        Func<bool, BepInExConsoleWindowState> updateBepInExConsoleVisibility,
        Func<LocalApiConnectionConfigDto> getConnectionConfig,
        Func<LocalApiConnectionConfigUpdate, LocalApiConnectionConfigDto> updateConnectionConfig,
        Func<LocalApiConnectionConfigDto> regenerateLocalApiToken,
        Func<string, string> openLogFolder,
        Func<string, int, int, RuntimeInventoryEditResult> editInventory,
        Func<string, IReadOnlyList<int>, int, RuntimeInventoryBulkEditResult> editInventoryBulk,
        Func<OrderPreparationRequest, OrderPreparationResult> prepareOrder,
        Func<OrderPreparationRequest, OrderPreparationResult> completeOrder,
        Func<OrderPreparationRequest, OrderPreparationResult> completeNormalOrder,
        long automationCommandEpoch,
        Func<long, int> advanceAutomationCommandEpoch,
        Func<long, AutomationCommandCancellationResult> cancelAutomationJobs,
        Func<long, AutomationSafetyBarrierAckResult> ackAutomationSafetyBarrier,
        Func<RuntimeAvailableMissionSnapshot> readAvailableMissions,
        Func<RuntimeAvailableMissionSnapshot> getAvailableMissionSnapshot,
        Func<string, string, RareGuestInvitationResult> listRareGuestInvitations,
        Func<string, string, RareGuestInvitationWriteExpectation, RareGuestInvitationResult> inviteAllRareGuests,
        Func<int, string, RareGuestInvitationWriteExpectation, RareGuestInvitationResult> inviteRareGuest,
        UpdateService updateService,
        FavoriteStore favoriteStore,
        CustomRecipeStore customRecipeStore,
        ManualLogSource log)
    {
        BindAddress = IPAddress.Loopback;
        Port = Math.Clamp(port, 1024, 65535);
        _log = log;
        _clientHandlers = new BoundedHandlerPool<TcpClient>(
            MaxConcurrentClientHandlers,
            exception => _log.LogWarning($"Local API client handler failed: {exception.Message}"));
        _pluginVersion = pluginVersion;
        _token = token.Trim();
        _lanEnabled = lanEnabled;
        _lanBindHost = NormalizeLanBindHost(lanBindHost);
        _getLogSettings = getLogSettings;
        _updateLogSettings = updateLogSettings;
        _updateBepInExConsoleVisibility = updateBepInExConsoleVisibility;
        _getConnectionConfig = getConnectionConfig;
        _updateConnectionConfig = updateConnectionConfig;
        _regenerateLocalApiToken = regenerateLocalApiToken;
        _openLogFolder = openLogFolder;
        _editInventory = editInventory;
        _editInventoryBulk = editInventoryBulk;
        _prepareOrder = prepareOrder;
        _completeOrder = completeOrder;
        _completeNormalOrder = completeNormalOrder;
        _automationCommandEpoch = Math.Max(1, automationCommandEpoch);
        _advanceAutomationCommandEpoch = advanceAutomationCommandEpoch;
        _cancelAutomationJobs = cancelAutomationJobs;
        _ackAutomationSafetyBarrier = ackAutomationSafetyBarrier;
        _readAvailableMissions = readAvailableMissions;
        _getAvailableMissionSnapshot = getAvailableMissionSnapshot;
        _listRareGuestInvitations = listRareGuestInvitations;
        _inviteAllRareGuests = inviteAllRareGuests;
        _inviteRareGuest = inviteRareGuest;
        _updateService = updateService;
        _favoriteStore = favoriteStore;
        _customRecipeStore = customRecipeStore;
    }

    public IPAddress BindAddress { get; }
    public int Port { get; }
    public string BaseUrl => $"http://{FormatHostForUrl(BindAddress)}:{Port}";
    public bool LanRunning => GetActiveLanCandidates().Count > 0;
    public string LanError => _lanError;
    public IReadOnlyList<LocalApiLanEndpointDto> LanEndpoints => GetActiveLanCandidates()
        .Select((candidate, index) => new LocalApiLanEndpointDto
        {
            Address = candidate.Address.ToString(),
            Endpoint = $"http://{FormatHostForUrl(candidate.Address)}:{Port}",
            InterfaceName = candidate.InterfaceName,
            InterfaceType = candidate.InterfaceType.ToString(),
            HasGateway = candidate.HasGateway,
            LinkLocal = candidate.LinkLocal,
            Recommended = index == 0,
        })
        .ToArray();

    /// <summary>
    /// 启动本地 API 监听线程。
    /// </summary>
    /// <remarks>
    /// 监听线程只负责接收连接和分派请求；需要访问 Unity 或游戏运行时对象的操作会通过委托回到
    /// <c>StewardOverlayController</c>，再由主线程队列执行，避免跨线程直接触碰 IL2CPP 对象。
    /// </remarks>
    public void Start()
    {
        lock (_lanSettingsLock)
        {
            if (_running) return;

            _running = true;
            _lanSettingsApplied = false;
            try
            {
                _clientHandlers.StartAccepting();
                StartListener("loopback", BindAddress, lanCandidate: null);
                ApplyLanSettingsCore(_lanEnabled, _lanBindHost);
                _updateService.StartAutoCheckScheduler();
                _log.LogInfo($"Local API loopback listener is available at {BaseUrl}. LAN listener is an optional add-on for trusted private networks.");
            }
            catch
            {
                _running = false;
                _lanSettingsApplied = false;
                _clientHandlers.StopAccepting();
                StopAllListeners();
                throw;
            }
        }
    }

    /// <summary>
    /// 更新伴随窗口读取的最新快照 JSON。
    /// </summary>
    /// <param name="snapshotJson">已经序列化好的运行时快照。</param>
    /// <remarks>
    /// 快照在 Unity 主线程构建并一次性替换，API 线程只读取字符串副本，避免每个 HTTP 请求重复反射读取游戏对象。
    /// </remarks>
    public void SetSnapshotJson(string snapshotJson, string snapshotSignature)
    {
        lock (_snapshotLock)
        {
            _snapshotJson = snapshotJson;
            _snapshotSignature = snapshotSignature;
        }
    }

    public void SetRuntimeDataJson(string runtimeDataJson)
    {
        lock (_snapshotLock)
        {
            _runtimeDataJson = runtimeDataJson;
        }
    }

    public void Dispose()
    {
        lock (_lanSettingsLock)
        {
            _running = false;
            _lanSettingsApplied = false;
            _clientHandlers.StopAccepting();
            _updateService.BeginShutdown();
            StopAllListeners();
        }

        if (!_clientHandlers.WaitForIdle(ClientHandlerStopTimeout))
        {
            _log.LogWarning("Local API client handlers did not stop within three seconds.");
        }

        _updateService.Dispose();
    }

    public void ApplyLanSettings(bool lanEnabled, string lanBindHost)
    {
        lock (_lanSettingsLock)
        {
            ApplyLanSettingsCore(lanEnabled, lanBindHost);
        }
    }

    private void ApplyLanSettingsCore(bool lanEnabled, string lanBindHost)
    {
        var normalizedHost = NormalizeLanBindHost(lanBindHost);
        if (!_running)
        {
            _lanEnabled = lanEnabled;
            _lanBindHost = normalizedHost;
            _lanSettingsApplied = false;
            _lanError = "";
            return;
        }

        var resolution = lanEnabled
            ? ResolveLanBindCandidates(normalizedHost)
            : (Candidates: (IReadOnlyList<LanAddressCandidate>)Array.Empty<LanAddressCandidate>(), Error: "");
        var bindCandidates = resolution.Candidates;
        if (_lanSettingsApplied
            && _lanEnabled == lanEnabled
            && string.Equals(_lanBindHost, normalizedHost, StringComparison.OrdinalIgnoreCase)
            && TryRefreshActiveLanCandidates(
                bindCandidates,
                lanEnabled && bindCandidates.Count == 0 ? resolution.Error : ""))
        {
            return;
        }

        _lanEnabled = lanEnabled;
        _lanBindHost = normalizedHost;
        _lanSettingsApplied = true;
        StopLanListeners();
        _lanError = "";

        if (!lanEnabled)
        {
            _log.LogInfo("Local API LAN listener is disabled. Loopback listener remains available.");
            return;
        }

        if (bindCandidates.Count == 0)
        {
            _lanError = resolution.Error;
            _log.LogWarning($"Local API LAN listener was not started: {_lanError}");
            return;
        }

        foreach (var candidate in bindCandidates)
        {
            try
            {
                StartListener("LAN", candidate.Address, candidate);
            }
            catch (Exception ex)
            {
                _lanError = ex.Message;
                _log.LogWarning($"Local API LAN listener failed on {candidate.Address}:{Port}: {ex.Message}");
            }
        }

        var started = GetActiveLanCandidates();
        if (started.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(_lanError)) _lanError = "LAN listener failed on all private IPv4 addresses.";
            _log.LogWarning($"Local API LAN listener was not started: {_lanError}");
            return;
        }

        _lanError = "";
        _log.LogInfo($"Local API LAN listener available at {string.Join(", ", started.Select(candidate => $"http://{candidate.Address}:{Port}"))}.");
    }

    public void SetToken(string token)
    {
        _token = token.Trim();
    }

    private void StartListener(string name, IPAddress bindAddress, LanAddressCandidate? lanCandidate)
    {
        var isLan = lanCandidate != null;
        var listener = new LocalApiListenerWorker(
            name,
            isLan,
            bindAddress,
            Port,
            isLan
                ? $"mystia-steward-companion Local API LAN {bindAddress}"
                : "mystia-steward-companion Local API loopback",
            client => _clientHandlers.TryDispatch(client, HandleClient),
            HandleListenerFailure);
        lock (_listenerLock)
        {
            _listeners.Add(listener);
            if (lanCandidate != null) _activeLanCandidates.Add(lanCandidate);
        }

        try
        {
            listener.Start();
        }
        catch
        {
            lock (_listenerLock)
            {
                _listeners.Remove(listener);
                if (lanCandidate != null) _activeLanCandidates.Remove(lanCandidate);
            }
            listener.Stop(ListenerStopTimeout);
            throw;
        }
    }

    private void StopLanListeners()
    {
        List<LocalApiListenerWorker> lanListeners;
        lock (_listenerLock)
        {
            lanListeners = _listeners.Where(static listener => listener.IsLan).ToList();
            _listeners.RemoveAll(static listener => listener.IsLan);
            _activeLanCandidates.Clear();
        }

        foreach (var listener in lanListeners)
        {
            StopListener(listener);
        }
    }

    private void StopAllListeners()
    {
        List<LocalApiListenerWorker> listeners;
        lock (_listenerLock)
        {
            listeners = _listeners.ToList();
            _listeners.Clear();
            _activeLanCandidates.Clear();
        }

        foreach (var listener in listeners)
        {
            StopListener(listener);
        }
    }

    private void StopListener(LocalApiListenerWorker listener)
    {
        if (!listener.Stop(ListenerStopTimeout))
        {
            _log.LogWarning($"Local API {listener.Name} listener thread did not stop within {ListenerStopTimeout.TotalSeconds:0.#} seconds.");
        }
    }

    private IReadOnlyList<LanAddressCandidate> GetActiveLanCandidates()
    {
        lock (_listenerLock)
        {
            return _activeLanCandidates.ToArray();
        }
    }

    private bool TryRefreshActiveLanCandidates(
        IReadOnlyList<LanAddressCandidate> candidates,
        string lanError)
    {
        lock (_listenerLock)
        {
            if (!HaveSameAddresses(_activeLanCandidates, candidates)) return false;

            _activeLanCandidates.Clear();
            _activeLanCandidates.AddRange(candidates);
            _lanError = lanError;
            return true;
        }
    }

    private void HandleListenerFailure(LocalApiListenerWorker listener, Exception exception)
    {
        lock (_listenerLock)
        {
            if (!_listeners.Remove(listener)) return;
            if (listener.IsLan)
            {
                _activeLanCandidates.RemoveAll(candidate => candidate.Address.Equals(listener.BindAddress));
                _lanError = exception.Message;
            }
        }

        if (listener.IsLan)
        {
            _log.LogWarning($"Local API {listener.Name} listener terminated after an unexpected failure: {exception.Message}");
            return;
        }

        lock (_lanSettingsLock)
        {
            _running = false;
            _lanSettingsApplied = false;
            StopLanListeners();
        }
        _log.LogError($"Local API loopback listener terminated after an unexpected failure: {exception.Message}. Restart the game to restore the local API.");
    }

    private static bool HaveSameAddresses(
        IReadOnlyList<LanAddressCandidate> left,
        IReadOnlyList<LanAddressCandidate> right)
    {
        if (left.Count != right.Count) return false;
        return left.Select(static candidate => candidate.Address.ToString())
            .OrderBy(static address => address, StringComparer.Ordinal)
            .SequenceEqual(
                right.Select(static candidate => candidate.Address.ToString())
                    .OrderBy(static address => address, StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// 解析单个 HTTP 请求并路由到对应端点。
    /// </summary>
    /// <param name="client">由监听线程接收到的 TCP 客户端。</param>
    /// <remarks>
    /// 当前协议只支持简单的 GET/POST，无请求体。Tauri 伴随窗口通过 Header 传入 Token；
    /// 浏览器开发模式同样走回环地址和 Token，避免把游戏运行时操作暴露给任意网页。
    /// </remarks>
    private void HandleClient(TcpClient client)
    {
        NetworkStream? stream = null;
        try
        {
            client.ReceiveTimeout = 2500;
            client.SendTimeout = 2500;
            stream = client.GetStream();
            var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
            if (!IsClientAddressAllowed(remoteEndPoint))
            {
                WriteResponse(stream, 403, "Forbidden", ToJson(new LocalApiErrorDto { Error = "forbidden client address" }));
                return;
            }

            var request = HttpRequestReader.ReadHeader(stream, MaxRequestBytes);
            var firstLine = request.Split('\n').FirstOrDefault()?.TrimEnd('\r') ?? "";
            var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                WriteResponse(stream, 400, "Bad Request", ToJson(new LocalApiErrorDto { Error = "bad request" }));
                return;
            }

            var method = parts[0];
            var (path, query) = SplitRequestTarget(parts[1]);
            if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                WriteResponse(stream, 204, "No Content", "");
                return;
            }

            var isGet = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
            var isPost = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase);
            if (!isGet && !isPost)
            {
                WriteResponse(stream, 405, "Method Not Allowed", ToJson(new LocalApiErrorDto { Error = "method not allowed" }));
                return;
            }

            if (RequiresAuthorization(path) && !IsAuthorized(request))
            {
                WriteResponse(stream, 401, "Unauthorized", ToJson(new LocalApiErrorDto { Error = "unauthorized" }));
                return;
            }

            var isLoopbackClient = IsLoopbackClient(remoteEndPoint);

            if (isPost)
            {
                switch (path)
                {
                    case "/automation/lease/acquire":
                        WriteResponse(stream, 200, "OK", ToJson(AcquireAutomationLease(request)));
                        break;
                    case "/automation/jobs/cancel":
                        WriteResponse(stream, 200, "OK", ToJson(CancelAutomationAndReleaseLease(request)));
                        break;
                    case "/automation/barriers/ack":
                        WriteResponse(stream, 200, "OK", ToJson(AcknowledgeAutomationSafetyBarrier(request, query)));
                        break;
                    case "/local-api/config":
                        if (!isLoopbackClient)
                        {
                            WriteResponse(stream, 403, "Forbidden", ToJson(new LocalApiErrorDto { Error = "local configuration is only allowed from the game PC" }));
                            break;
                        }
                        var updatedConfig = _updateConnectionConfig(new LocalApiConnectionConfigUpdate
                        {
                            LanEnabled = ReadBoolQuery(query, "lanEnabled"),
                            LanBindHost = ReadStringQuery(query, "lanHost"),
                        });
                        WriteResponse(stream, 200, "OK", ToJson(updatedConfig));
                        break;
                    case "/local-api/token/regenerate":
                        if (!isLoopbackClient)
                        {
                            WriteResponse(stream, 403, "Forbidden", ToJson(new LocalApiErrorDto { Error = "token regeneration is only allowed from the game PC" }));
                            break;
                        }
                        var regeneratedConfig = _regenerateLocalApiToken();
                        SetToken(regeneratedConfig.Token);
                        WriteResponse(stream, 200, "OK", ToJson(regeneratedConfig));
                        break;
                    case "/updates/status":
                        WriteResponse(stream, 200, "OK", ToJson(_updateService.GetStatus()));
                        break;
                    case "/updates/check":
                        WriteResponse(stream, 200, "OK", ToJson(_updateService.CheckForUpdates()));
                        break;
                    case "/updates/download":
                        WriteResponse(stream, 200, "OK", ToJson(_updateService.DownloadUpdate()));
                        break;
                    case "/updates/install-on-exit":
                        WriteResponse(stream, 200, "OK", ToJson(_updateService.InstallOnExit()));
                        break;
                    case "/diagnostics/automation-decision":
                        WriteResponse(stream, 200, "OK", BuildAutomationDecisionDiagnosticJson(query, request));
                        break;
                    case "/logs/export-diagnostics":
                        WriteResponse(stream, 200, "OK", BuildDiagnosticPackageJson(ReadBoolQuery(query, "open") ?? false));
                        break;
                    case "/logs/config":
                        _updateLogSettings(
                            ReadBoolQuery(query, "aggregateLog"),
                            ReadNullableIntQuery(query, "aggregateLogMaxFiles"));
                        WriteResponse(stream, 200, "OK", BuildLogSettingsJson());
                        break;
                    case "/logs/console":
                        if (!isLoopbackClient)
                        {
                            WriteResponse(stream, 403, "Forbidden", ToJson(new LocalApiErrorDto { Error = "BepInEx console control is only allowed from the game PC" }));
                            break;
                        }
                        WriteResponse(stream, 200, "OK", BuildBepInExConsoleActionJson(query));
                        break;
                    case "/logs/open-folder":
                        WriteResponse(stream, 200, "OK", OpenLogFolderJson(ReadStringQuery(query, "target")));
                        break;
                    case "/inventory/set":
                        WriteResponse(stream, 200, "OK", BuildInventoryEditJson(query));
                        break;
                    case "/inventory/bulk-set":
                        WriteResponse(stream, 200, "OK", BuildInventoryBulkEditJson(query));
                        break;
                    case "/orders/prepare-next":
                        if (!TryRequireAutomationLease(request, out var prepareLeaseError, out var prepareEpoch))
                        {
                            WriteResponse(stream, 200, "OK", ToJson(prepareLeaseError));
                            break;
                        }
                        WriteResponse(stream, 200, "OK", BuildOrderActionJson(query, _prepareOrder, prepareEpoch));
                        break;
                    case "/orders/complete-first":
                        if (!TryRequireAutomationLease(request, out var completeLeaseError, out var completeEpoch))
                        {
                            WriteResponse(stream, 200, "OK", ToJson(completeLeaseError));
                            break;
                        }
                        WriteResponse(stream, 200, "OK", BuildOrderActionJson(query, _completeOrder, completeEpoch));
                        break;
                    case "/orders/normal/complete-first":
                        if (!TryRequireAutomationLease(request, out var normalLeaseError, out var normalEpoch))
                        {
                            WriteResponse(stream, 200, "OK", ToJson(normalLeaseError));
                            break;
                        }
                        WriteResponse(stream, 200, "OK", BuildOrderActionJson(query, _completeNormalOrder, normalEpoch));
                        break;
                    case "/orders/rare/dismiss":
                        WriteResponse(stream, 200, "OK", BuildRareOrderDismissJson(query));
                        break;
                    case "/rare-guests/invite-all":
                        WriteResponse(
                            stream,
                            200,
                            "OK",
                            BuildRareGuestInvitationJson(() => _inviteAllRareGuests(
                                ReadStringQuery(query, "scope"),
                                ReadStringQuery(query, "levels"),
                                ReadRareGuestInvitationWriteExpectation(query))));
                        break;
                    case "/rare-guests/invite":
                        WriteResponse(
                            stream,
                            200,
                            "OK",
                            BuildRareGuestInvitationJson(() => _inviteRareGuest(
                                ReadIntQuery(query, "guestId", -1),
                                ReadStringQuery(query, "scope"),
                                ReadRareGuestInvitationWriteExpectation(query))));
                        break;
                    case "/ui-pinning/target":
                        WriteResponse(stream, 200, "OK", UpdateUiPinningTargetJson(query));
                        break;
                    case "/favorites/add-recipe":
                        WriteResponse(stream, 200, "OK", AddRecipeFavoriteJson(query));
                        break;
                    case "/favorites/remove-recipe":
                        WriteResponse(stream, 200, "OK", _favoriteStore.RemoveRecipe(ReadStringQuery(query, "id")));
                        break;
                    case "/favorites/add-beverage":
                        WriteResponse(stream, 200, "OK", AddBeverageFavoriteJson(query));
                        break;
                    case "/favorites/remove-beverage":
                        WriteResponse(stream, 200, "OK", _favoriteStore.RemoveBeverage(ReadStringQuery(query, "id")));
                        break;
                    case "/custom-recipes/upsert":
                        WriteResponse(stream, 200, "OK", UpsertCustomRecipeJson(query));
                        break;
                    case "/custom-recipes/remove":
                        WriteResponse(stream, 200, "OK", _customRecipeStore.Remove(ReadStringQuery(query, "id")));
                        break;
                    case "/custom-recipes/settings":
                        WriteResponse(stream, 200, "OK", SetCustomRecipesEnabledJson(query));
                        break;
                    case "/custom-recipes/update-flags":
                        WriteResponse(stream, 200, "OK", UpdateCustomRecipeFlagsJson(query));
                        break;
                    case "/custom-recipes/move":
                        WriteResponse(stream, 200, "OK", _customRecipeStore.Move(
                            ReadStringQuery(query, "id"),
                            ReadStringQuery(query, "direction")));
                        break;
                    default:
                        WriteResponse(stream, 404, "Not Found", ToJson(new LocalApiErrorDto { Error = "not found" }));
                        break;
                }
                return;
            }

            switch (path)
            {
                case "/health":
                    WriteResponse(stream, 200, "OK", BuildHealthJson());
                    break;
                case "/local-api/config":
                    if (!isLoopbackClient)
                    {
                        WriteResponse(stream, 403, "Forbidden", ToJson(new LocalApiErrorDto { Error = "local configuration is only available on the game PC" }));
                        break;
                    }
                    WriteResponse(stream, 200, "OK", ToJson(_getConnectionConfig()));
                    break;
                case "/snapshot":
                    WriteResponse(stream, 200, "OK", GetSnapshotJson(query, request));
                    break;
                case "/runtime-data":
                    WriteResponse(stream, 200, "OK", GetRuntimeDataJson());
                    break;
                case "/missions/tracked":
                    WriteResponse(stream, 200, "OK", GetTrackedMissionsJson(query));
                    break;
                case "/missions/available":
                    WriteResponse(stream, 200, "OK", GetAvailableMissionsJson(query));
                    break;
                case "/automation/lease":
                    WriteResponse(stream, 200, "OK", ToJson(ReadAutomationLease(request)));
                    break;
                case "/logs/settings":
                    WriteResponse(stream, 200, "OK", BuildLogSettingsJson());
                    break;
                case "/favorites":
                    WriteResponse(stream, 200, "OK", _favoriteStore.GetJson());
                    break;
                case "/custom-recipes":
                    WriteResponse(stream, 200, "OK", _customRecipeStore.GetJson());
                    break;
                case "/rare-guests/invitations":
                    WriteResponse(
                        stream,
                        200,
                        "OK",
                        BuildRareGuestInvitationJson(
                            () => _listRareGuestInvitations(
                                ReadStringQuery(query, "scope"),
                                ReadStringQuery(query, "levels"))));
                    break;
                default:
                    WriteResponse(stream, 404, "Not Found", ToJson(new LocalApiErrorDto { Error = "not found" }));
                    break;
            }
        }
        catch (HttpRequestReadException ex)
        {
            TryWriteErrorResponse(stream, ex.StatusCode, ex.Reason, ex.Error);
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Local API request failed: {ex.Message}");
            TryWriteErrorResponse(stream, 500, "Internal Server Error", "internal server error");
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private string GetSnapshotJson(string query = "", string request = "", bool logRequest = true)
    {
        string knownSignature;
        string currentSignature;
        string snapshotJson;
        string responseJson;
        bool unchanged;
        lock (_snapshotLock)
        {
            knownSignature = ReadStringQuery(query, "knownSignature");
            currentSignature = _snapshotSignature;
            snapshotJson = _snapshotJson;
            unchanged = !string.IsNullOrWhiteSpace(knownSignature)
                && !string.IsNullOrWhiteSpace(_snapshotSignature)
                && string.Equals(knownSignature, _snapshotSignature, StringComparison.Ordinal);
            responseJson = unchanged
                ? ToJson(new LocalApiSnapshotUnchangedDto
                {
                    Unchanged = true,
                    SnapshotSignature = _snapshotSignature,
                })
                : _snapshotJson;
        }

        if (logRequest)
        {
            AppendSnapshotRequestDiagnostic(
                request,
                knownSignature,
                currentSignature,
                snapshotJson,
                responseJson.Length,
                unchanged);
        }

        return responseJson;
    }

    private static string GetTrackedMissionsJson(string query)
    {
        return LocalApiTrackedMissionsPayload.BuildJson(
            RuntimeMissionDiagnosticCapture.ReadTrackedMissions(),
            ReadStringQuery(query, "knownSignature"),
            JsonOptions);
    }

    private string GetAvailableMissionsJson(string query)
    {
        RuntimeAvailableMissionSnapshot snapshot;
        try
        {
            snapshot = _readAvailableMissions();
        }
        catch (Exception ex)
        {
            var current = _getAvailableMissionSnapshot();
            snapshot = RuntimeAvailableMissionSnapshot.Unavailable(
                current.MissionGeneration,
                current.DaySceneGeneration,
                RuntimeAvailableMissionSnapshot.MissionDataIncompleteStatus,
                $"available-mission-command-failed:{ex.GetType().Name}:{ex.GetBaseException().Message}");
        }

        return LocalApiAvailableMissionsPayload.BuildJson(
            snapshot,
            ReadStringQuery(query, "knownSignature"),
            JsonOptions);
    }

    private void AppendSnapshotRequestDiagnostic(
        string request,
        string knownSignature,
        string currentSignature,
        string snapshotJson,
        int responseLength,
        bool unchanged)
    {
        if (!AggregateModLogService.Enabled) return;

        try
        {
            var summary = ReadSnapshotRequestSummary(snapshotJson);
            var clientId = (ReadHeader(request, ClientIdHeaderName) ?? "").Trim();
            var clientLabel = (ReadHeader(request, ClientLabelHeaderName) ?? "").Trim();
            if (string.IsNullOrWhiteSpace(clientLabel)) clientLabel = "unknown";
            var diagnosticSignature = string.Join(
                "|",
                clientId,
                clientLabel,
                FormatShortSignature(knownSignature),
                FormatShortSignature(currentSignature),
                unchanged ? "unchanged" : "full",
                responseLength,
                summary.Scene,
                summary.NormalOrders,
                summary.SpecialActive,
                summary.ChallengeType,
                summary.Phase);
            if (string.Equals(diagnosticSignature, _lastSnapshotRequestDiagnosticSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastSnapshotRequestDiagnosticSignature = diagnosticSignature;
            AggregateModLogService.AppendSection(
                "snapshot.request",
                "Local API Snapshot Request",
                string.Join(
                    Environment.NewLine,
                    $"client: {clientLabel} / {FormatShortSignature(clientId)}",
                    $"knownSignature: {FormatShortSignature(knownSignature)}",
                    $"currentSignature: {FormatShortSignature(currentSignature)}",
                    $"response: {(unchanged ? "unchanged" : "full")}",
                    $"responseBytes: {responseLength}",
                    $"scene: {summary.Scene}",
                    $"normalOrders: {summary.NormalOrders}",
                    $"specialActive: {summary.SpecialActive}",
                    $"special: {summary.ChallengeType} {summary.Phase}",
                    $"capturedAtUtc: {summary.CapturedAtUtc}"));
        }
        catch
        {
            // Request diagnostics must never affect local API responses.
        }
    }

    private static SnapshotRequestSummary ReadSnapshotRequestSummary(string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        var root = document.RootElement;
        var normalOrders = -1;
        if (TryGetJsonProperty(root, "normalBusiness", out var normalBusiness)
            && normalBusiness.ValueKind == JsonValueKind.Object
            && TryGetJsonProperty(normalBusiness, "orders", out var normalOrdersElement)
            && normalOrdersElement.ValueKind == JsonValueKind.Array)
        {
            normalOrders = normalOrdersElement.GetArrayLength();
        }

        var specialActive = false;
        var challengeType = "";
        var phase = "";
        if (TryGetJsonProperty(root, "specialBusiness", out var specialBusiness)
            && specialBusiness.ValueKind == JsonValueKind.Object)
        {
            specialActive = ReadJsonBoolean(specialBusiness, "active");
            challengeType = ReadJsonString(specialBusiness, "challengeType");
            phase = ReadJsonString(specialBusiness, "phase");
        }

        return new SnapshotRequestSummary
        {
            CapturedAtUtc = ReadJsonString(root, "capturedAtUtc"),
            Scene = ReadJsonString(root, "activeSceneName"),
            NormalOrders = normalOrders,
            SpecialActive = specialActive,
            ChallengeType = challengeType,
            Phase = phase,
        };
    }

    private static bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    private static string ReadJsonString(JsonElement element, string name)
    {
        return TryGetJsonProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static bool ReadJsonBoolean(JsonElement element, string name)
    {
        return TryGetJsonProperty(element, name, out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    private static string FormatShortSignature(string value)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Length == 0) return "<empty>";
        return normalized.Length <= 12 ? normalized : normalized[..12];
    }

    private string GetRuntimeDataJson()
    {
        lock (_snapshotLock)
        {
            return _runtimeDataJson;
        }
    }

    private string BuildHealthJson()
    {
        return ToJson(new LocalApiHealthDto
        {
            Ok = true,
            PluginVersion = _pluginVersion,
            BindAddress = BindAddress.ToString(),
            Port = Port,
            AuthRequired = true,
            LocalEndpoint = BaseUrl,
            LanEnabled = _lanEnabled,
            LanRunning = LanRunning,
            LanError = string.IsNullOrWhiteSpace(_lanError) ? null : _lanError,
        });
    }

    private LocalApiAutomationLeaseDto ReadAutomationLease(string request)
    {
        var (clientId, clientLabel, error) = ReadClientIdentity(request);
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new LocalApiAutomationLeaseDto
            {
                Ok = false,
                ClientId = clientId,
                ClientLabel = clientLabel,
                TtlMs = (int)AutomationLeaseTtl.TotalMilliseconds,
                Error = error,
            };
        }

        lock (_automationLeaseLock)
        {
            PruneExpiredAutomationLease(DateTime.UtcNow);
            return BuildAutomationLeaseDto(clientId, clientLabel, null);
        }
    }

    private LocalApiAutomationLeaseDto AcquireAutomationLease(string request)
    {
        var (clientId, clientLabel, error) = ReadClientIdentity(request);
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new LocalApiAutomationLeaseDto
            {
                Ok = false,
                ClientId = clientId,
                ClientLabel = clientLabel,
                TtlMs = (int)AutomationLeaseTtl.TotalMilliseconds,
                Error = error,
            };
        }

        lock (_automationLeaseLock)
        {
            var now = DateTime.UtcNow;
            PruneExpiredAutomationLease(now);
            if (_automationLease != null
                && !string.Equals(_automationLease.ClientId, clientId, StringComparison.Ordinal))
            {
                return BuildAutomationLeaseDto(
                    clientId,
                    clientLabel,
                    $"自动化当前由 {_automationLease.ClientLabel} 控制，本窗口仅查看。");
            }

            if (_automationLease == null)
            {
                _automationCommandEpoch++;
                _advanceAutomationCommandEpoch(_automationCommandEpoch);
            }

            _automationLease = new AutomationLease
            {
                ClientId = clientId,
                ClientLabel = clientLabel,
                LastSeenUtc = now,
                ExpiresAtUtc = now + AutomationLeaseTtl,
            };
            return BuildAutomationLeaseDto(clientId, clientLabel, null);
        }
    }

    private LocalApiAutomationCancellationDto CancelAutomationAndReleaseLease(string request)
    {
        var (clientId, clientLabel, error) = ReadClientIdentity(request);
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new LocalApiAutomationCancellationDto
            {
                Ok = false,
                Error = error,
            };
        }

        lock (_automationLeaseLock)
        {
            PruneExpiredAutomationLease(DateTime.UtcNow);
            if (_automationLease == null
                || !string.Equals(_automationLease.ClientId, clientId, StringComparison.Ordinal))
            {
                return new LocalApiAutomationCancellationDto
                {
                    Ok = false,
                    Error = _automationLease == null
                        ? "自动化控制权已失效，无法确认取消屏障。"
                        : $"自动化当前由 {_automationLease.ClientLabel} 控制，本窗口不能取消其任务。",
                    CommandEpoch = _automationCommandEpoch,
                };
            }

            var cancellationEpoch = ++_automationCommandEpoch;
            try
            {
                var result = _cancelAutomationJobs(cancellationEpoch);
                _automationLease = null;
                return new LocalApiAutomationCancellationDto
                {
                    Ok = true,
                    Status = $"自动化已取消：job {result.CancelledJobs} 个，排队命令 {result.CancelledCommands} 个。",
                    CommandEpoch = result.CommandEpoch,
                    CancelledJobs = result.CancelledJobs,
                    CancelledCommands = result.CancelledCommands,
                    LeaseReleased = true,
                };
            }
            catch (Exception ex)
            {
                return new LocalApiAutomationCancellationDto
                {
                    Ok = false,
                    Error = ex.GetBaseException().Message,
                    CommandEpoch = cancellationEpoch,
                    LeaseReleased = false,
                };
            }
        }
    }

    private AutomationSafetyBarrierAckResult AcknowledgeAutomationSafetyBarrier(string request, string query)
    {
        if (!long.TryParse(ReadStringQuery(query, "sequence"), out var sequence) || sequence <= 0)
        {
            return new AutomationSafetyBarrierAckResult
            {
                Ok = false,
                Sequence = sequence,
                Error = "automation barrier sequence must be a positive integer",
            };
        }

        var (clientId, clientLabel, identityError) = ReadClientIdentity(request);
        lock (_automationLeaseLock)
        {
            PruneExpiredAutomationLease(DateTime.UtcNow);
            if (!string.IsNullOrWhiteSpace(identityError)
                || _automationLease == null
                || !string.Equals(_automationLease.ClientId, clientId, StringComparison.Ordinal))
            {
                return new AutomationSafetyBarrierAckResult
                {
                    Ok = false,
                    Sequence = sequence,
                    Error = !string.IsNullOrWhiteSpace(identityError)
                        ? identityError
                        : _automationLease == null
                            ? "自动化控制权已失效，不能确认安全栅栏。"
                            : $"自动化当前由 {_automationLease.ClientLabel} 控制，本窗口不能确认其安全栅栏。",
                };
            }

            return _ackAutomationSafetyBarrier(sequence);
        }
    }

    private bool TryRequireAutomationLease(
        string request,
        out LocalApiOrderActionErrorDto error,
        out long automationEpoch)
    {
        automationEpoch = 0;
        var (clientId, clientLabel, identityError) = ReadClientIdentity(request);
        lock (_automationLeaseLock)
        {
            PruneExpiredAutomationLease(DateTime.UtcNow);
            var status = string.IsNullOrWhiteSpace(identityError)
                ? BuildAutomationLeaseDto(clientId, clientLabel, null)
                : new LocalApiAutomationLeaseDto
                {
                    Ok = false,
                    ClientId = clientId,
                    ClientLabel = clientLabel,
                    Error = identityError,
                };
            if (status.Ok && status.Owned)
            {
                automationEpoch = _automationCommandEpoch;
                error = new LocalApiOrderActionErrorDto();
                return true;
            }

            error = new LocalApiOrderActionErrorDto
            {
                Ok = false,
                Prepared = false,
                Error = status.Error
                    ?? (string.IsNullOrWhiteSpace(status.OwnerClientId)
                        ? "自动化控制权不可用，请先在本窗口开启自动化。"
                        : $"自动化当前由 {status.OwnerLabel} 控制，本窗口仅查看。"),
            };
            return false;
        }
    }

    private LocalApiAutomationLeaseDto BuildAutomationLeaseDto(string clientId, string clientLabel, string? error)
    {
        var lease = _automationLease;
        return new LocalApiAutomationLeaseDto
        {
            Ok = string.IsNullOrWhiteSpace(error),
            Owned = lease != null && string.Equals(lease.ClientId, clientId, StringComparison.Ordinal),
            ClientId = clientId,
            ClientLabel = clientLabel,
            OwnerClientId = lease?.ClientId ?? "",
            OwnerLabel = lease?.ClientLabel ?? "",
            OwnerLastSeenUtc = lease == null ? "" : lease.LastSeenUtc.ToString("O"),
            ExpiresAtUtc = lease == null ? "" : lease.ExpiresAtUtc.ToString("O"),
            TtlMs = (int)AutomationLeaseTtl.TotalMilliseconds,
            Error = error,
        };
    }

    private string BuildAutomationDecisionDiagnosticJson(string query, string request)
    {
        try
        {
            AppendAutomationDecisionDiagnostic(query, request);
            return ToJson(new LocalApiStatusDto
            {
                Ok = true,
                Status = "automation decision diagnostic appended",
                Error = null,
            });
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiStatusDto
            {
                Ok = false,
                Status = "",
                Error = ex.Message,
            });
        }
    }

    private void AppendAutomationDecisionDiagnostic(string query, string request)
    {
        if (!AggregateModLogService.Enabled) return;

        try
        {
            var clientId = (ReadHeader(request, ClientIdHeaderName) ?? "").Trim();
            var clientLabel = (ReadHeader(request, ClientLabelHeaderName) ?? "").Trim();
            if (string.IsNullOrWhiteSpace(clientLabel)) clientLabel = "unknown";

            var signature = LimitDiagnosticText(ReadStringQuery(query, "signature"), 160);
            var diagnosticSignature = string.Join("|", clientId, signature);
            if (!string.IsNullOrWhiteSpace(signature)
                && string.Equals(diagnosticSignature, _lastAutomationDecisionDiagnosticSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastAutomationDecisionDiagnosticSignature = diagnosticSignature;

            var challengeType = LimitDiagnosticText(ReadStringQuery(query, "challengeType"), 80);
            var phase = LimitDiagnosticText(ReadStringQuery(query, "phase"), 40);
            var channel = challengeType.Contains("Yuyuko", StringComparison.OrdinalIgnoreCase)
                || challengeType.Contains("幽幽子", StringComparison.Ordinal)
                    ? "special-business.yuyuko"
                    : "automation.frontend";
            var title = channel == "special-business.yuyuko"
                ? "Yuyuko Challenge Frontend Automation Decision"
                : "Frontend Automation Decision";

            var lines = new List<string>
            {
                $"client: {clientLabel} / {FormatShortSignature(clientId)}",
                $"event: {LimitDiagnosticText(ReadStringQuery(query, "eventName"), 80)}",
                $"message: {LimitDiagnosticText(ReadStringQuery(query, "message"))}",
                $"scene: {LimitDiagnosticText(ReadStringQuery(query, "scene"), 60)}",
                $"special: {challengeType} {phase}",
                $"specialRole: {LimitDiagnosticText(ReadStringQuery(query, "specialBusinessRole"), 80)}",
                $"counts: orders={ReadIntQuery(query, "orderCount", 0)}, selections={ReadIntQuery(query, "selectionCount", 0)}, skips={ReadIntQuery(query, "skipCount", 0)}",
                string.Join(
                    "; ",
                    "automation=" + (ReadBoolQuery(query, "automationEnabled") ?? false),
                    "leaseOwned=" + (ReadBoolQuery(query, "leaseOwned") ?? false),
                    "complete=" + (ReadBoolQuery(query, "autoCompleteOrder") ?? false),
                    "takeBeverage=" + (ReadBoolQuery(query, "autoTakeBeverage") ?? false),
                    "startCooking=" + (ReadBoolQuery(query, "autoStartCooking") ?? false),
                    "collectCooking=" + (ReadBoolQuery(query, "autoCollectCooking") ?? false),
                    "recipeFavoritesOnly=" + (ReadBoolQuery(query, "recipeFavoritesOnly") ?? false),
                    "beverageFavoritesOnly=" + (ReadBoolQuery(query, "beverageFavoritesOnly") ?? false),
                    "rareConcurrency=" + ReadIntQuery(query, "rareConcurrency", 0)),
            };

            var leaseMessage = LimitDiagnosticText(ReadStringQuery(query, "leaseMessage"));
            if (!string.IsNullOrWhiteSpace(leaseMessage)) lines.Add($"leaseMessage: {leaseMessage}");

            AppendPrefixedDiagnosticLines(lines, "order", ReadDiagnosticLines(query, "orderLines"));
            AppendPrefixedDiagnosticLines(lines, "selection", ReadDiagnosticLines(query, "selectionLines"));
            AppendPrefixedDiagnosticLines(lines, "skip", ReadDiagnosticLines(query, "skipLines"));

            AggregateModLogService.AppendSection(channel, title, string.Join(Environment.NewLine, lines));
        }
        catch
        {
            // Frontend diagnostics must never affect local API responses.
        }
    }

    private void PruneExpiredAutomationLease(DateTime now)
    {
        if (_automationLease != null && _automationLease.ExpiresAtUtc <= now)
        {
            _automationLease = null;
        }
    }

    private string BuildLogSettingsJson()
    {
        var settings = _getLogSettings();
        return ToJson(new LocalApiLogSettingsDto
        {
            AggregateModLogEnabled = settings.AggregateModLogEnabled,
            AggregateModLogPath = settings.AggregateModLogPath,
            AggregateModLogDirectory = GetDirectory(settings.AggregateModLogPath),
            AggregateModLogMaxFileBytes = settings.AggregateModLogMaxFileBytes,
            AggregateModLogMaxFileCount = settings.AggregateModLogMaxFileCount,
            AggregateModLogMaxTotalBytes = settings.AggregateModLogMaxTotalBytes,
            BepInExConsoleSupported = settings.BepInExConsoleSupported,
            BepInExConsoleConfiguredVisible = settings.BepInExConsoleConfiguredVisible,
            BepInExConsoleActive = settings.BepInExConsoleActive,
            BepInExConsoleVisible = settings.BepInExConsoleVisible,
            BepInExConsoleStatus = settings.BepInExConsoleStatus,
        });
    }

    private string BuildBepInExConsoleActionJson(string query)
    {
        var visible = ReadBoolQuery(query, "visible");
        if (!visible.HasValue)
        {
            var current = _getLogSettings();
            return ToJson(new LocalApiBepInExConsoleActionDto
            {
                Ok = false,
                Supported = current.BepInExConsoleSupported,
                ConfiguredVisible = current.BepInExConsoleConfiguredVisible,
                Active = current.BepInExConsoleActive,
                Visible = current.BepInExConsoleVisible,
                Status = current.BepInExConsoleStatus,
                Error = "invalid BepInEx console visibility",
            });
        }

        var state = _updateBepInExConsoleVisibility(visible.Value);
        return ToJson(new LocalApiBepInExConsoleActionDto
        {
            Ok = state.Ok,
            Supported = state.Supported,
            ConfiguredVisible = state.ConfiguredVisible,
            Active = state.Active,
            Visible = state.Visible,
            Status = state.Status,
            Error = state.Error,
        });
    }

    private string OpenLogFolderJson(string target)
    {
        try
        {
            var directory = _openLogFolder(target);
            return ToJson(new LocalApiDirectoryActionDto { Ok = true, Directory = directory, Error = null });
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiDirectoryActionDto { Ok = false, Directory = "", Error = ex.Message });
        }
    }

    private string BuildDiagnosticPackageJson(bool openFolder)
    {
        try
        {
            var settings = _getLogSettings();
            var packageDirectory = ResolveDiagnosticPackageDirectory();
            Directory.CreateDirectory(packageDirectory);
            var packagePath = Path.Combine(
                packageDirectory,
                "mystia-steward-companion-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
            var added = new List<string>();

            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                AddTextEntry(archive, "manifest.json", BuildDiagnosticManifestJson(settings), added);
                AddTextEntry(archive, "snapshot/current-snapshot.json", GetSnapshotJson(logRequest: false), added);
                AddTextEntry(archive, "snapshot/runtime-data.json", GetRuntimeDataJson(), added);
                AddTextEntry(
                    archive,
                    "snapshot/runtime-mission-diagnostic.json",
                    ToJson(RuntimeMissionDiagnosticCapture.Report()),
                    added);
                AddTextEntry(
                    archive,
                    "snapshot/runtime-scheduled-event-diagnostic.json",
                    ToJson(RuntimeScheduledEventDiagnosticCapture.Report()),
                    added);
                AddTextEntry(
                    archive,
                    "snapshot/runtime-available-missions.json",
                    ToJson(_getAvailableMissionSnapshot()),
                    added);
                AddTextEntry(
                    archive,
                    "snapshot/runtime-mission-serve-in-work-diagnostic.json",
                    ToJson(RuntimeServeInWorkMissionDiagnosticCapture.Snapshot()),
                    added);
                AddAggregateLogEntries(archive, settings.AggregateModLogPath, DiagnosticTailMaxBytes, DiagnosticTailMaxLines, added);
            }

            if (openFolder)
            {
                try
                {
                    _openLogFolder("packages");
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"Open diagnostic package folder failed: {ex.Message}");
                }
            }

            return ToJson(new LocalApiDiagnosticPackageDto
            {
                Ok = true,
                Path = packagePath,
                Directory = packageDirectory,
                Files = added,
                Error = null,
            });
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiDiagnosticPackageDto
            {
                Ok = false,
                Path = "",
                Directory = "",
                Files = Array.Empty<string>(),
                Error = ex.Message,
            });
        }
    }

    private string BuildInventoryEditJson(string query)
    {
        var itemType = ReadStringQuery(query, "type");
        if (!int.TryParse(ReadStringQuery(query, "id"), out var itemId)
            || !int.TryParse(ReadStringQuery(query, "qty"), out var quantity))
        {
            return ToJson(new LocalApiErrorDto { Error = "invalid inventory edit parameters" });
        }

        try
        {
            var result = _editInventory(itemType, itemId, quantity);
            var ok = string.IsNullOrWhiteSpace(result.Error);
            return ToJson(new LocalApiInventoryEditDto
            {
                Ok = ok,
                Type = result.ItemType,
                Id = result.ItemId,
                RequestedQuantity = result.RequestedQuantity,
                PreviousQuantity = result.PreviousQuantity,
                Quantity = result.Quantity,
                Changed = result.Changed,
                Error = ok ? null : result.Error,
            });
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiErrorDto { Error = ex.Message });
        }
    }

    private string BuildInventoryBulkEditJson(string query)
    {
        var itemType = ReadStringQuery(query, "type");
        var itemIds = ReadIntListQuery(query, "ids");
        if (!int.TryParse(ReadStringQuery(query, "qty"), out var quantity) || itemIds.Count == 0)
        {
            return ToJson(new LocalApiErrorDto { Error = "invalid inventory bulk edit parameters" });
        }

        RuntimeInventoryBulkEditResult result;
        try
        {
            result = _editInventoryBulk(itemType, itemIds, quantity);
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiErrorDto { Error = ex.Message });
        }

        return ToJson(new LocalApiInventoryBulkEditDto
        {
            Ok = result.Failed == 0,
            Type = result.ItemType,
            RequestedQuantity = result.RequestedQuantity,
            Total = result.Total,
            Changed = result.Changed,
            Unchanged = result.Unchanged,
            Failed = result.Failed,
            Errors = result.Errors,
            Error = result.Failed == 0 ? null : string.Join("; ", result.Errors),
        });
    }

    private string BuildOrderActionJson(
        string query,
        Func<OrderPreparationRequest, OrderPreparationResult> action,
        long automationEpoch)
    {
        try
        {
            var request = new OrderPreparationRequest
            {
                AutomationEpoch = automationEpoch,
                TraceId = ReadStringQuery(query, "traceId"),
                OrderKey = ReadStringQuery(query, "orderKey"),
                DeskCode = ReadIntQuery(query, "deskCode", -1),
                GuestId = ReadNullableIntQuery(query, "guestId"),
                RuntimeGuestId = ReadNullableIntQuery(query, "runtimeGuestId"),
                GuestName = ReadStringQuery(query, "guestName"),
                SpecialBusinessRole = ReadStringQuery(query, "specialBusinessRole"),
                FoodTagId = ReadNullableIntQuery(query, "foodTagId"),
                FoodTag = ReadStringQuery(query, "foodTag"),
                BeverageTagId = ReadNullableIntQuery(query, "beverageTagId"),
                BeverageTag = ReadStringQuery(query, "beverageTag"),
                MatchFoodId = ReadIntQuery(query, "matchFoodId", -1),
                MatchBeverageId = ReadIntQuery(query, "matchBeverageId", -1),
                FoodId = ReadIntQuery(query, "foodId", -1),
                RecipeId = ReadIntQuery(query, "recipeId", -1),
                RecipeName = ReadStringQuery(query, "recipeName"),
                ExtraIngredientIds = ReadIntListQuery(query, "extraIngredientIds"),
                PredictedFoodTags = ReadStringListQuery(query, "predictedFoodTags"),
                PredictedFoodTagsProvided = HasQueryParameter(query, "predictedFoodTags"),
                ExpectedFoodModifierTags = ReadStringListQuery(query, "expectedFoodModifierTags"),
                SpecialTargetChallenge = ReadStringQuery(query, "specialTargetChallenge"),
                SpecialTargetOwner = ReadStringQuery(query, "specialTargetOwner"),
                SpecialTargetGeneration = ReadLongQuery(query, "specialTargetGeneration", 0),
                SpecialTargetRevision = ReadLongQuery(query, "specialTargetRevision", 0),
                SpecialTargetFoodTags = ReadStringListQuery(query, "specialTargetFoodTags"),
                SpecialTargetMatchMode = ReadStringQuery(query, "specialTargetMatchMode"),
                SpecialTargetSignature = ReadStringQuery(query, "specialTargetSignature"),
                AllowYuumaControlledProgression = ReadBoolQuery(query, "allowYuumaControlledProgression") ?? false,
                ExecutionMode = ReadStringQuery(query, "executionMode"),
                ExecutionReason = ReadStringQuery(query, "executionReason"),
                BeverageId = ReadIntQuery(query, "beverageId", -1),
                BeverageName = ReadStringQuery(query, "beverageName"),
                AutoTakeBeverage = ReadBoolQuery(query, "autoTakeBeverage") ?? false,
                AutoStartCooking = ReadBoolQuery(query, "autoStartCooking") ?? false,
                CookerControllerIndex = ReadIntQuery(query, "cookerControllerIndex", -1),
                CookerControllerIdentity = ReadStringQuery(query, "cookerControllerIdentity"),
                CookerGridX = ReadNullableIntQuery(query, "cookerGridX"),
                CookerGridY = ReadNullableIntQuery(query, "cookerGridY"),
                CookerGridZ = ReadNullableIntQuery(query, "cookerGridZ"),
                AutoCollectCooking = ReadBoolQuery(query, "autoCollectCooking") ?? false,
                AutoDeliverFood = ReadBoolQuery(query, "autoDeliverFood") ?? false,
                AutoCompleteOrder = ReadBoolQuery(query, "autoCompleteOrder") ?? false,
                RecipeFavoritesOnly = ReadBoolQuery(query, "recipeFavoritesOnly") ?? false,
                BeverageFavoritesOnly = ReadBoolQuery(query, "beverageFavoritesOnly") ?? false,
                StopOnError = ReadBoolQuery(query, "stopOnError") ?? true,
                RecipeFavorite = ReadBoolQuery(query, "recipeFavorite") ?? false,
                BeverageFavorite = ReadBoolQuery(query, "beverageFavorite") ?? false,
            };

            var result = action(request);
            return ToJson(result);
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiOrderActionErrorDto
            {
                Ok = false,
                Prepared = false,
                Error = ex.Message,
            });
        }
    }

    private static string BuildRareGuestInvitationJson(Func<RareGuestInvitationResult> action)
    {
        try
        {
            var result = action();
            return ToJson(result);
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiRareGuestInvitationErrorDto
            {
                Ok = false,
                RuntimeAvailable = false,
                Status = "稀客邀请失败。",
                Error = ex.Message,
            });
        }
    }

    private static string BuildRareOrderDismissJson(string query)
    {
        try
        {
            var removed = SpecialOrderRuntimeCapture.DismissOrder(
                ReadIntQuery(query, "deskCode", -1),
                ReadNullableIntQuery(query, "runtimeGuestId"),
                ReadNullableIntQuery(query, "foodTagId"),
                ReadNullableIntQuery(query, "beverageTagId"));
            var status = removed > 0
                ? $"已删除 {removed} 条稀客订单缓存。"
                : "未找到匹配的稀客订单缓存。";
            return ToJson(new LocalApiRareOrderDismissDto { Ok = true, Removed = removed, Status = status, Error = null });
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiRareOrderDismissDto { Ok = false, Removed = 0, Status = "", Error = ex.Message });
        }
    }

    private static string UpdateUiPinningTargetJson(string query)
    {
        try
        {
            var enabled = ReadBoolQuery(query, "enabled") ?? false;
            var highlightEnabled = ReadBoolQuery(query, "highlightEnabled") ?? false;
            var status = RuntimeUiPinningService.UpdateTarget(
                ReadLongQuery(query, "businessGeneration", 0),
                enabled,
                highlightEnabled,
                ReadIntQuery(query, "recipeId", -1),
                ReadIntQuery(query, "beverageId", -1),
                ReadIntListQuery(query, "ingredientIds"),
                ReadStringQuery(query, "recipeName"),
                ReadStringQuery(query, "beverageName"),
                ReadIntQuery(query, "cookerTypeId", -1),
                ReadStringQuery(query, "cookerName"));
            return ToJson(new LocalApiStatusDto { Ok = true, Status = status, Error = null });
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiStatusDto { Ok = false, Status = "", Error = ex.Message });
        }
    }

    private string AddRecipeFavoriteJson(string query)
    {
        if (!int.TryParse(ReadStringQuery(query, "customerId"), out var customerId)
            || !int.TryParse(ReadStringQuery(query, "recipeId"), out var recipeId))
        {
            return "{\"ok\":false,\"favorites\":{\"version\":1,\"recipes\":[],\"beverages\":[]},\"error\":\"invalid favorite recipe parameters\"}";
        }

        try
        {
            return _favoriteStore.AddRecipe(
                customerId,
                ReadStringQuery(query, "customerName"),
                ReadStringQuery(query, "foodTag"),
                recipeId,
                ReadIntListQuery(query, "extraIngredientIds"));
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiFavoriteMutationDto { Ok = false, Error = ex.Message });
        }
    }

    private string AddBeverageFavoriteJson(string query)
    {
        if (!int.TryParse(ReadStringQuery(query, "customerId"), out var customerId)
            || !int.TryParse(ReadStringQuery(query, "beverageId"), out var beverageId))
        {
            return "{\"ok\":false,\"favorites\":{\"version\":1,\"recipes\":[],\"beverages\":[]},\"error\":\"invalid favorite beverage parameters\"}";
        }

        try
        {
            return _favoriteStore.AddBeverage(
                customerId,
                ReadStringQuery(query, "customerName"),
                ReadStringQuery(query, "beverageTag"),
                beverageId);
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiFavoriteMutationDto { Ok = false, Error = ex.Message });
        }
    }

    private string UpsertCustomRecipeJson(string query)
    {
        if (!int.TryParse(ReadStringQuery(query, "customerId"), out var customerId)
            || !int.TryParse(ReadStringQuery(query, "foodId"), out var foodId))
        {
            return ToJson(new LocalApiCustomRecipeMutationDto { Ok = false, Error = "invalid custom recipe parameters" });
        }

        try
        {
            return _customRecipeStore.Upsert(new CustomRecipeMutation
            {
                Id = ReadStringQuery(query, "id"),
                CustomerId = customerId,
                CustomerName = ReadStringQuery(query, "customerName"),
                FoodTag = ReadStringQuery(query, "foodTag"),
                FoodId = foodId,
                RecipeId = ReadIntQuery(query, "recipeId", -1),
                RecipeName = ReadStringQuery(query, "recipeName"),
                ExtraIngredientIds = ReadIntListQuery(query, "extraIngredientIds"),
                Enabled = ReadBoolQuery(query, "enabled"),
                PinToTop = ReadBoolQuery(query, "pinToTop"),
                SortOrder = ReadNullableIntQuery(query, "sortOrder"),
            });
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiCustomRecipeMutationDto { Ok = false, Error = ex.Message });
        }
    }

    private string SetCustomRecipesEnabledJson(string query)
    {
        var enabled = ReadBoolQuery(query, "enabled");
        return enabled == null
            ? ToJson(new LocalApiCustomRecipeMutationDto { Ok = false, Error = "invalid custom recipe enabled setting" })
            : _customRecipeStore.SetEnabled(enabled.Value);
    }

    private string UpdateCustomRecipeFlagsJson(string query)
    {
        var scope = ReadStringQuery(query, "scope");
        var selection = scope switch
        {
            "all" => new CustomRecipeSelection { Kind = CustomRecipeSelectionKind.All },
            "customer" => new CustomRecipeSelection
            {
                Kind = CustomRecipeSelectionKind.Customer,
                CustomerId = ReadIntQuery(query, "customerId", -1),
            },
            "recipe" => new CustomRecipeSelection
            {
                Kind = CustomRecipeSelectionKind.Recipe,
                FoodId = ReadIntQuery(query, "foodId", -1),
            },
            "entry" => new CustomRecipeSelection
            {
                Kind = CustomRecipeSelectionKind.Entry,
                Id = ReadStringQuery(query, "id"),
            },
            _ => null,
        };
        if (selection == null
            || (selection.Kind == CustomRecipeSelectionKind.Customer && selection.CustomerId < 0)
            || (selection.Kind == CustomRecipeSelectionKind.Recipe && selection.FoodId < 0)
            || (selection.Kind == CustomRecipeSelectionKind.Entry && string.IsNullOrWhiteSpace(selection.Id)))
        {
            return ToJson(new LocalApiCustomRecipeMutationDto { Ok = false, Error = "invalid custom recipe selection" });
        }

        var enabled = ReadBoolQuery(query, "enabled");
        var pinToTop = ReadBoolQuery(query, "pinToTop");
        if ((HasQueryParameter(query, "enabled") && enabled == null)
            || (HasQueryParameter(query, "pinToTop") && pinToTop == null))
        {
            return ToJson(new LocalApiCustomRecipeMutationDto { Ok = false, Error = "invalid custom recipe flags" });
        }

        return _customRecipeStore.UpdateFlags(
            selection,
            enabled,
            pinToTop);
    }

    private static List<string> ReadLogTail(string path, int maxBytes, int maxLines)
    {
        var info = new FileInfo(path);
        var start = Math.Max(0, info.Length - maxBytes);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        if (start > 0) reader.ReadLine();

        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
            if (lines.Count > maxLines) lines.RemoveAt(0);
        }

        return lines;
    }

    public static string ResolveDiagnosticPackageDirectory()
    {
        return Path.Combine(Paths.ConfigPath, "MystiaStewardCompanion", "diagnostic-packages");
    }

    private static void AddAggregateLogEntries(ZipArchive archive, string primaryPath, int maxBytes, int maxLines, List<string> added)
    {
        foreach (var path in AggregateModLogService.EnumerateFiles(primaryPath))
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) continue;
            AddLogTailEntry(archive, path, "aggregate/" + name.Replace(".log", ".tail.log", StringComparison.Ordinal), maxBytes, maxLines, added);
        }
    }

    private static void AddLogTailEntry(
        ZipArchive archive,
        string path,
        string entryName,
        int maxBytes,
        int maxLines,
        List<string> added)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var content = string.Join(Environment.NewLine, ReadLogTail(path, maxBytes, maxLines));
        AddTextEntry(archive, entryName, content, added);
    }

    private static void AddTextEntry(ZipArchive archive, string entryName, string content, List<string> added)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
        added.Add(entryName);
    }

    private string BuildDiagnosticManifestJson(LocalApiLogSettings settings)
    {
        return ToJson(new LocalApiDiagnosticManifestDto
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            BaseUrl = BaseUrl,
            AggregateModLogPath = settings.AggregateModLogPath,
            AggregateModLogMaxFileBytes = settings.AggregateModLogMaxFileBytes,
            AggregateModLogMaxFileCount = settings.AggregateModLogMaxFileCount,
            AggregateModLogMaxTotalBytes = settings.AggregateModLogMaxTotalBytes,
        });
    }

    private static void WriteResponse(NetworkStream stream, int status, string reason, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = new StringBuilder();
        headers.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        headers.Append("Content-Type: application/json; charset=utf-8\r\n");
        headers.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        headers.Append("Cache-Control: no-store\r\n");
        headers.Append("Access-Control-Allow-Origin: *\r\n");
        headers.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        headers.Append("Access-Control-Allow-Headers: Content-Type, X-Mystia-Steward-Companion-Token, X-Mystia-Steward-Companion-Client-Id, X-Mystia-Steward-Companion-Client-Label\r\n");
        headers.Append("Access-Control-Max-Age: 86400\r\n");
        headers.Append("Connection: close\r\n");
        headers.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (bodyBytes.Length > 0)
        {
            stream.Write(bodyBytes, 0, bodyBytes.Length);
        }
    }

    private static void TryWriteErrorResponse(NetworkStream? stream, int status, string reason, string error)
    {
        if (stream == null) return;
        try
        {
            WriteResponse(stream, status, reason, ToJson(new LocalApiErrorDto { Error = error }));
        }
        catch
        {
            // The connection may already be closed or have a partial response; handler shutdown still proceeds.
        }
    }

    private static string NormalizeLanBindHost(string configuredHost)
    {
        var host = (configuredHost ?? "").Trim();
        if (string.IsNullOrWhiteSpace(host)) return AutoLanHost;
        return string.Equals(host, AutoLanHost, StringComparison.OrdinalIgnoreCase) ? AutoLanHost : host;
    }

    private static (IReadOnlyList<LanAddressCandidate> Candidates, string Error) ResolveLanBindCandidates(
        string configuredHost)
    {
        var host = NormalizeLanBindHost(configuredHost);
        var discovered = GetPrivateLanIPv4Candidates();
        if (string.Equals(host, AutoLanHost, StringComparison.OrdinalIgnoreCase))
        {
            return discovered.Count > 0
                ? (discovered, "")
                : (discovered, "No active private LAN IPv4 address is available for binding.");
        }

        if (!IPAddress.TryParse(host, out var parsed))
        {
            return (
                Array.Empty<LanAddressCandidate>(),
                $"LAN host '{configuredHost}' must be a private IPv4 address or 'auto'.");
        }

        var address = NormalizeIPv4Address(parsed);
        if (address == null || !IsPrivateLanAddress(address))
        {
            return (
                Array.Empty<LanAddressCandidate>(),
                $"LAN host '{configuredHost}' is not a private IPv4 address.");
        }

        var matched = discovered.FirstOrDefault(candidate => candidate.Address.Equals(address));
        return matched != null
            ? (new[] { matched }, "")
            : (
                Array.Empty<LanAddressCandidate>(),
                $"LAN host '{configuredHost}' is not assigned to an active network interface.");
    }

    private static IReadOnlyList<LanAddressCandidate> GetPrivateLanIPv4Candidates()
    {
        var candidates = new List<LanAddressCandidate>();
        NetworkInterface[] networkInterfaces;

        try
        {
            networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return Array.Empty<LanAddressCandidate>();
        }

        foreach (var networkInterface in networkInterfaces)
        {
            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            try
            {
                IPInterfaceProperties properties;
                try
                {
                    properties = networkInterface.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                var hasGateway = properties.GatewayAddresses.Any(gateway =>
                {
                    var gatewayAddress = NormalizeIPv4Address(gateway.Address);
                    return gatewayAddress != null && !gatewayAddress.Equals(IPAddress.Any);
                });
                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = NormalizeIPv4Address(unicast.Address);
                    if (address != null && IsPrivateLanAddress(address))
                    {
                        candidates.Add(new LanAddressCandidate
                        {
                            Address = address,
                            InterfaceName = networkInterface.Name,
                            InterfaceType = networkInterface.NetworkInterfaceType,
                            HasGateway = hasGateway,
                            LinkLocal = IsLinkLocalAddress(address),
                        });
                    }
                }
            }
            catch
            {
                // A changing or virtual adapter must not discard candidates found on other adapters.
            }
        }

        return candidates
            .GroupBy(static candidate => candidate.Address)
            .Select(static group => OrderLanCandidates(group).First())
            .OrderByDescending(static candidate => candidate.HasGateway)
            .ThenBy(static candidate => candidate.LinkLocal)
            .ThenBy(static candidate => GetInterfacePreference(candidate.InterfaceType))
            .ThenBy(static candidate => candidate.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => GetIPv4SortKey(candidate.Address))
            .ToArray();
    }

    private static IOrderedEnumerable<LanAddressCandidate> OrderLanCandidates(
        IEnumerable<LanAddressCandidate> candidates)
    {
        return candidates
            .OrderByDescending(static candidate => candidate.HasGateway)
            .ThenBy(static candidate => candidate.LinkLocal)
            .ThenBy(static candidate => GetInterfacePreference(candidate.InterfaceType))
            .ThenBy(static candidate => candidate.InterfaceName, StringComparer.OrdinalIgnoreCase);
    }

    private static int GetInterfacePreference(NetworkInterfaceType interfaceType)
    {
        return interfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => 0,
            NetworkInterfaceType.Ethernet => 1,
            NetworkInterfaceType.FastEthernetT => 1,
            NetworkInterfaceType.FastEthernetFx => 1,
            NetworkInterfaceType.GigabitEthernet => 1,
            _ => 2,
        };
    }

    private static uint GetIPv4SortKey(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24)
            | ((uint)bytes[1] << 16)
            | ((uint)bytes[2] << 8)
            | bytes[3];
    }

    private static bool IsLinkLocalAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private bool IsClientAddressAllowed(IPEndPoint? remoteEndPoint)
    {
        var remoteAddress = remoteEndPoint?.Address;
        if (remoteAddress == null) return false;
        if (IPAddress.IsLoopback(remoteAddress)) return true;
        if (!_lanEnabled) return false;

        var ipv4 = NormalizeIPv4Address(remoteAddress);
        return ipv4 != null && IsPrivateLanAddress(ipv4);
    }

    private static bool IsLoopbackClient(IPEndPoint? remoteEndPoint)
    {
        var remoteAddress = remoteEndPoint?.Address;
        return remoteAddress != null && IPAddress.IsLoopback(remoteAddress);
    }

    private static IPAddress? NormalizeIPv4Address(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork) return address;
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        return null;
    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            && (bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254));
    }

    private static string ToJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static string FormatHostForUrl(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
    }

    /// <summary>
    /// 校验请求头中的本地 API Token。
    /// </summary>
    /// <param name="request">完整请求头文本。</param>
    /// <returns>Token 与当前插件生成或配置的值完全一致时返回 <c>true</c>。</returns>
    private bool IsAuthorized(string request)
    {
        if (string.IsNullOrWhiteSpace(_token)) return false;
        return string.Equals(ReadHeader(request, "X-Mystia-Steward-Companion-Token"), _token, StringComparison.Ordinal);
    }

    private static (string ClientId, string ClientLabel, string? Error) ReadClientIdentity(string request)
    {
        var clientId = (ReadHeader(request, ClientIdHeaderName) ?? "").Trim();
        if (!IsValidClientId(clientId))
        {
            return ("", "伴随窗口", "自动化请求缺少有效客户端 ID。");
        }

        var label = (ReadHeader(request, ClientLabelHeaderName) ?? "").Trim();
        if (string.IsNullOrWhiteSpace(label)) label = "伴随窗口";
        if (label.Length > 48) label = label[..48];
        return (clientId, label, null);
    }

    private static bool IsValidClientId(string value)
    {
        if (value.Length < 16 || value.Length > 64) return false;
        return value.All(static character =>
            (character >= 'a' && character <= 'z')
            || (character >= 'A' && character <= 'Z')
            || (character >= '0' && character <= '9')
            || character == '-');
    }

    /// <summary>
    /// 判断端点是否需要 Token 鉴权。
    /// </summary>
    /// <remarks>
    /// <c>/health</c> 保持无鉴权，供伴随窗口启动探测和进程存活判断使用；其他端点可能暴露存档状态、
    /// 日志路径或运行时修改能力，必须鉴权。
    /// </remarks>
    private static bool RequiresAuthorization(string path)
    {
        return !string.Equals(path, "/health", StringComparison.Ordinal);
    }

    private static string? ReadHeader(string request, string headerName)
    {
        foreach (var line in request.Split('\n').Skip(1))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) break;
            var separator = trimmed.IndexOf(':');
            if (separator <= 0) continue;
            var name = trimmed[..separator].Trim();
            if (!string.Equals(name, headerName, StringComparison.OrdinalIgnoreCase)) continue;
            return trimmed[(separator + 1)..].Trim();
        }

        return null;
    }

    private static (string Path, string Query) SplitRequestTarget(string target)
    {
        if (target.IndexOf('\r') >= 0 || target.IndexOf('\n') >= 0)
        {
            return ("/", "");
        }

        var queryStart = target.IndexOf('?');
        return queryStart < 0
            ? (target, "")
            : (target[..queryStart], target[(queryStart + 1)..]);
    }

    private static bool? ReadBoolQuery(string query, string key)
    {
        var value = ReadStringQuery(query, key);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1") return true;
        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0") return false;
        return null;
    }

    private static int ReadIntQuery(string query, string key, int fallback)
    {
        return int.TryParse(ReadStringQuery(query, key), out var value) ? value : fallback;
    }

    private static long ReadLongQuery(string query, string key, long fallback)
    {
        return long.TryParse(ReadStringQuery(query, key), out var value) ? value : fallback;
    }

    private static RareGuestInvitationWriteExpectation ReadRareGuestInvitationWriteExpectation(
        string query)
    {
        return new RareGuestInvitationWriteExpectation(
            ReadLongQuery(query, "expectedDaySceneGeneration", 0),
            ReadStringQuery(query, "expectedMapLabel"));
    }

    private static int? ReadNullableIntQuery(string query, string key)
    {
        return int.TryParse(ReadStringQuery(query, key), out var value) ? value : null;
    }

    private static string ReadStringQuery(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 0) continue;
            var name = Uri.UnescapeDataString(parts[0].Replace("+", " ", StringComparison.Ordinal));
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) continue;
            return parts.Length == 1
                ? ""
                : Uri.UnescapeDataString(parts[1].Replace("+", " ", StringComparison.Ordinal));
        }

        return "";
    }

    private static bool HasQueryParameter(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries).Any(pair =>
        {
            var separator = pair.IndexOf('=');
            var encodedName = separator < 0 ? pair : pair[..separator];
            var name = Uri.UnescapeDataString(encodedName.Replace("+", " ", StringComparison.Ordinal));
            return string.Equals(name, key, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static IReadOnlyList<string> ReadDiagnosticLines(string query, string key)
    {
        var value = ReadStringQuery(query, key);
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => LimitDiagnosticText(line))
            .Take(AutomationDecisionDiagnosticMaxLines)
            .ToArray();
    }

    private static void AppendPrefixedDiagnosticLines(List<string> lines, string prefix, IReadOnlyList<string> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            lines.Add($"{prefix}[{index + 1}]: {values[index]}");
        }
    }

    private static string LimitDiagnosticText(string value, int maxLength = AutomationDecisionDiagnosticMaxTextLength)
    {
        if (maxLength <= 0) return "";
        var normalized = (value ?? "").Trim();
        if (normalized.Length <= maxLength) return normalized;
        return maxLength <= 3
            ? normalized[..maxLength]
            : normalized[..(maxLength - 3)] + "...";
    }

    private static List<int> ReadIntListQuery(string query, string key)
    {
        var value = ReadStringQuery(query, key);
        if (string.IsNullOrWhiteSpace(value)) return new List<int>();

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var id) ? id : -1)
            .Where(id => id >= 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    private static List<string> ReadStringListQuery(string query, string key)
    {
        var value = ReadStringQuery(query, key);
        if (string.IsNullOrWhiteSpace(value)) return new List<string>();

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(part => part, StringComparer.Ordinal)
            .ToList();
    }

    private static string GetDirectory(string path)
    {
        return Path.GetDirectoryName(path) ?? "";
    }
}

internal sealed class LocalApiLogSettings
{
    public bool AggregateModLogEnabled { get; init; }
    public string AggregateModLogPath { get; init; } = "";
    public long AggregateModLogMaxFileBytes { get; init; } = AggregateModLogService.MaxFileBytes;
    public int AggregateModLogMaxFileCount { get; init; } = AggregateModLogService.DefaultMaxFileCount;
    public long AggregateModLogMaxTotalBytes { get; init; } = AggregateModLogService.GetMaxTotalBytes(AggregateModLogService.DefaultMaxFileCount);
    public bool BepInExConsoleSupported { get; init; }
    public bool BepInExConsoleConfiguredVisible { get; init; }
    public bool BepInExConsoleActive { get; init; }
    public bool BepInExConsoleVisible { get; init; }
    public string BepInExConsoleStatus { get; init; } = "";
}
