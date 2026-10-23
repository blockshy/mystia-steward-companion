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
using MystiaStewardCompanion.GameMedia;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>
/// 运行在游戏进程内的本地 HTTP API，向伴随窗口暴露运行时快照并接收受控操作请求。
/// </summary>
/// <remarks>
/// 服务器使用轻量 <see cref="TcpListener"/>，避免在 IL2CPP Mod 中引入额外 Web 框架依赖。
/// 始终保留回环监听，LAN 监听只能作为显式开启的附加通道。除 <c>/health</c> 外所有端点都要求 Token。
/// GET 端点只读取状态，任何会修改运行时、配置、文件或外部进程状态的操作都只接受 POST。
/// </remarks>
internal sealed partial class LocalApiServer : IDisposable
{
    private const int MaxRequestHeaderBytes = 32768;
    private const int MaxRequestBodyBytes = 65536;
    private const int DiagnosticTailMaxBytes = 2 * 1024 * 1024;
    private const int DiagnosticTailMaxLines = 2000;
    private const int AutomationDecisionDiagnosticMaxLines = 12;
    private const int AutomationDecisionDiagnosticMaxTextLength = 600;
    private const int MaxConcurrentClientHandlers = 16;
    private const string AutoLanHost = "auto";
    private const string ClientIdHeaderName = "X-Mystia-Steward-Companion-Client-Id";
    private const string ClientLabelHeaderName = "X-Mystia-Steward-Companion-Client-Label";
    private const string AuthorityRevisionHeaderName = "X-Mystia-Steward-Companion-Authority-Revision";
    private static readonly TimeSpan AutomationLeaseTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ListenerStopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClientHandlerStopTimeout = TimeSpan.FromSeconds(3);
    private readonly ManualLogSource _log;
    private readonly object _snapshotLock = new();
    private readonly LocalApiSnapshotPresentation _snapshotPresentation = new();
    private readonly object _listenerLock = new();
    private readonly object _lanSettingsLock = new();
    private readonly object _automationLeaseLock = new();
    private readonly object _authorityTransitionLock = new();
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
    private DiagnosticPackageStore? _diagnosticPackages;
    private readonly object _diagnosticPackagesLock = new();
    private readonly Func<string, string> _openLogFolder;
    private readonly Func<string, int, int, int?, LocalApiWritePermit, RuntimeInventoryEditResult> _editInventory;
    private readonly Func<string, IReadOnlyList<int>, int, LocalApiWritePermit, RuntimeInventoryBulkEditResult> _editInventoryBulk;
    private readonly Func<OrderPreparationRequest, OrderPreparationResult> _prepareOrder;
    private readonly Func<OrderPreparationRequest, OrderPreparationResult> _completeOrder;
    private readonly Func<OrderPreparationRequest, OrderPreparationResult> _completeNormalOrder;
    private readonly Func<long, int> _advanceAutomationCommandEpoch;
    private readonly Func<long, AutomationSafetyBarrierAckResult> _ackAutomationSafetyBarrier;
    private readonly Func<RuntimeAvailableMissionSnapshot> _readAvailableMissions;
    private readonly Func<RuntimeAvailableMissionSnapshot> _getAvailableMissionSnapshot;
    private readonly Func<string, string, RareGuestInvitationResult> _listRareGuestInvitations;
    private readonly Func<string, string, RareGuestInvitationWriteExpectation, LocalApiWritePermit, RareGuestInvitationResult> _inviteAllRareGuests;
    private readonly Func<int, string, RareGuestInvitationWriteExpectation, LocalApiWritePermit, RareGuestInvitationResult> _inviteRareGuest;
    private readonly UpdateService _updateService;
    private readonly FavoriteStore _favoriteStore;
    private readonly CustomRecipeStore _customRecipeStore;
    private readonly CompanionDeviceAuthorityStore _deviceAuthorityStore;
    private readonly GameMediaService? _gameMediaService;
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
        public long AuthorityRevision { get; init; }
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
        Func<string, string> openLogFolder,
        Func<string, int, int, int?, LocalApiWritePermit, RuntimeInventoryEditResult> editInventory,
        Func<string, IReadOnlyList<int>, int, LocalApiWritePermit, RuntimeInventoryBulkEditResult> editInventoryBulk,
        Func<OrderPreparationRequest, OrderPreparationResult> prepareOrder,
        Func<OrderPreparationRequest, OrderPreparationResult> completeOrder,
        Func<OrderPreparationRequest, OrderPreparationResult> completeNormalOrder,
        long automationCommandEpoch,
        Func<long, int> advanceAutomationCommandEpoch,
        Func<long, AutomationSafetyBarrierAckResult> ackAutomationSafetyBarrier,
        Func<RuntimeAvailableMissionSnapshot> readAvailableMissions,
        Func<RuntimeAvailableMissionSnapshot> getAvailableMissionSnapshot,
        Func<string, string, RareGuestInvitationResult> listRareGuestInvitations,
        Func<string, string, RareGuestInvitationWriteExpectation, LocalApiWritePermit, RareGuestInvitationResult> inviteAllRareGuests,
        Func<int, string, RareGuestInvitationWriteExpectation, LocalApiWritePermit, RareGuestInvitationResult> inviteRareGuest,
        UpdateService updateService,
        FavoriteStore favoriteStore,
        CustomRecipeStore customRecipeStore,
        CompanionDeviceAuthorityStore deviceAuthorityStore,
        ManualLogSource log,
        GameMediaService? gameMediaService = null,
        DiagnosticPackageStore? diagnosticPackages = null)
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
        _diagnosticPackages = diagnosticPackages;
        _openLogFolder = openLogFolder;
        _editInventory = editInventory;
        _editInventoryBulk = editInventoryBulk;
        _prepareOrder = prepareOrder;
        _completeOrder = completeOrder;
        _completeNormalOrder = completeNormalOrder;
        _automationCommandEpoch = Math.Max(1, automationCommandEpoch);
        _advanceAutomationCommandEpoch = advanceAutomationCommandEpoch;
        _ackAutomationSafetyBarrier = ackAutomationSafetyBarrier;
        _readAvailableMissions = readAvailableMissions;
        _getAvailableMissionSnapshot = getAvailableMissionSnapshot;
        _listRareGuestInvitations = listRareGuestInvitations;
        _inviteAllRareGuests = inviteAllRareGuests;
        _inviteRareGuest = inviteRareGuest;
        _updateService = updateService;
        _favoriteStore = favoriteStore;
        _customRecipeStore = customRecipeStore;
        _deviceAuthorityStore = deviceAuthorityStore;
        _gameMediaService = gameMediaService;
        if (_gameMediaService != null)
        {
            _gameMediaReady = () => _gameMediaService.IsReady;
            _gameMediaService.ReadinessChanged += OnGameMediaReadinessChanged;
        }
        RuntimeAutomationControlState.ConfigureResourceReadiness(_gameMediaReady);
        RuntimeAutomationControlState.Reset("正在等待主设备连接并完成自动化配置同步。");
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

    /// <summary>读取实际在线私网 IPv4 网卡；不以已启动的监听列表代替可选地址集合。</summary>
    public IReadOnlyList<LocalApiLanEndpointDto> AvailableLanEndpoints => GetPrivateLanIPv4Candidates()
        .Select((candidate, index) => new LocalApiLanEndpointDto
        {
            Address = candidate.Address.ToString(), Endpoint = $"http://{FormatHostForUrl(candidate.Address)}:{Port}",
            InterfaceName = candidate.InterfaceName, InterfaceType = candidate.InterfaceType.ToString(),
            HasGateway = candidate.HasGateway, LinkLocal = candidate.LinkLocal, Recommended = index == 0,
        }).ToArray();

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
                StartBusinessHost();
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
            InvalidateBusinessInput();
        }
    }

    public void SetRuntimeDataJson(string runtimeDataJson)
    {
        lock (_snapshotLock)
        {
            _runtimeDataJson = runtimeDataJson;
            InvalidateBusinessInput();
        }
    }

    public void Dispose()
    {
        if (_gameMediaService != null) _gameMediaService.ReadinessChanged -= OnGameMediaReadinessChanged;
        // 主线程停止时只撤销未来许可，不等待可能仍在等待主线程结果的后台业务线程。
        StopBusinessHost();
        RuntimeAutomationControlState.Reset("本地 API 已停止；现有自动料理任务保持暂停。");
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
        Volatile.Write(ref _token, token);
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
    /// 协议支持简单的 GET/POST；设备权威端点使用有界 JSON 请求体，其余既有端点继续使用查询参数。
    /// Flutter 通过 Dart IO 直接发送 Token 与设备身份 Header；写入权限由 Mod 校验，不依赖客户端按钮显隐。
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

            var requestData = HttpRequestReader.Read(stream, MaxRequestHeaderBytes, MaxRequestBodyBytes);
            var request = requestData.Header;
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
                var requiresMedia = RequiresGameMediaReady(path);
                if (requiresMedia) RequireGameMediaReady();
                var manualWrite = RequiresPrimaryManualWrite(path) ? CaptureManualWritePermit(request, requiresMedia) : null;
                var queuedWrite = IsQueuedManualWrite(path);
                var mutatesBusinessInput = IsBusinessInputMutation(path) && !queuedWrite;
                // 库存/邀请的 HTTP 线程会等待 Unity，不能持锁等待。它们携带许可到实际执行处复核。
                // 下载/安排安装在入口接收一次明确意图，不让长网络等待阻塞权威切换和业务循环。
                var holdsAuthority = mutatesBusinessInput || manualWrite != null && !queuedWrite
                    && path is not ("/updates/download" or "/updates/install-on-exit");
                if (holdsAuthority) Monitor.Enter(_authorityTransitionLock);
                try
                {
                // 在管理串行锁内再次鉴权，旧密码请求不能先通过入口、排队等待改密完成后再写入。
                if (holdsAuthority && !IsAuthorized(request))
                    throw new CompanionDeviceAuthorityException(401, "连接密码已更新，请使用新密码重新连接。");
                if (manualWrite != null && holdsAuthority) ValidateManualWriteAuthority(request);
                if (requiresMedia) RequireGameMediaReady();
                if (mutatesBusinessInput) InvalidateBusinessInput();
                switch (path)
                {
                    case "/game-media/reload":
                        WriteResponse(stream, 200, "OK", ToJson(new { ok = true, status = RequireGameMediaService().RequestReload() }));
                        break;
                    case "/business/page-session/open":
                        WriteResponse(stream, 200, "OK", OpenBusinessPageSession(request, requestData));
                        break;
                    case "/business/page-session/update":
                        WriteResponse(stream, 200, "OK", UpdateBusinessPageSession(request, requestData));
                        break;
                    case "/business/automation/retry":
                        WriteResponse(stream, 200, "OK", RetryBusinessAutomation(request, requestData));
                        break;
                    case "/business/automation/reset":
                        WriteResponse(stream, 200, "OK", ResetBusinessAutomation(request, requestData));
                        break;
                    case "/devices/register":
                        WriteResponse(stream, 200, "OK", ToJson(RegisterCompanionDevice(request, requestData)));
                        break;
                    case "/devices/profile":
                        WriteResponse(stream, 200, "OK", ToJson(UpdateCompanionDeviceProfile(request, requestData)));
                        break;
                    case "/devices/primary":
                        WriteResponse(stream, 200, "OK", ToJson(SetPrimaryCompanionDevice(request, requestData)));
                        break;
                    case "/devices/sync":
                        WriteResponse(stream, 200, "OK", ToJson(SyncCompanionDeviceProfile(request, requestData)));
                        break;
                    case "/devices/sync-ack":
                        WriteResponse(stream, 200, "OK", ToJson(AcknowledgeCompanionDeviceSync(request, requestData)));
                        break;
                    case "/devices/rename":
                        WriteResponse(stream, 200, "OK", ToJson(RenameCompanionDevice(request, requestData)));
                        break;
                    case "/devices/forget":
                        WriteResponse(stream, 200, "OK", ToJson(ForgetCompanionDevice(request, requestData)));
                        break;
                    case "/automation/lease/acquire":
                        WriteResponse(stream, 200, "OK", ToJson(AcquireAutomationLease(request)));
                        break;
                    case "/automation/lease/release":
                        WriteResponse(stream, 200, "OK", ToJson(ReleaseAutomationLease(request)));
                        break;
                    case "/automation/barriers/ack":
                        WriteResponse(stream, 200, "OK", ToJson(AcknowledgeAutomationSafetyBarrier(request, requestData)));
                        break;
                    case "/local-api/config":
                        if (!isLoopbackClient)
                        {
                            WriteResponse(stream, 403, "Forbidden", ToJson(new LocalApiErrorDto { Error = "local configuration is only allowed from the game PC" }));
                            break;
                        }
                        var connectionUpdate = ReadConnectionConfigUpdate(requestData);
                        LocalApiConnectionPolicy.Validate(connectionUpdate, AvailableLanEndpoints);
                        var updatedConfig = _updateConnectionConfig(connectionUpdate);
                        if (updatedConfig.Ok) SetToken(updatedConfig.Token);
                        WriteResponse(stream, 200, "OK", ToJson(updatedConfig));
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
                    case "/logs/packages/delete":
                        var deletion = ReadJsonRequest<LocalApiDiagnosticPackageDeleteRequest>(requestData, "id");
                        WriteResponse(stream, 200, "OK", DeleteDiagnosticPackageJson(deletion.Id));
                        break;
                    case "/logs/config":
                        if (HasQueryParameter(query, "aggregateLogMaxFiles")
                            && (!int.TryParse(ReadStringQuery(query, "aggregateLogMaxFiles"), out var count)
                                || count is < 1 or > AggregateModLogService.MaxFileCountLimit))
                            throw new CompanionDeviceAuthorityException(400, "日志文件数须为 1–30 之间的整数。");
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
                        WriteResponse(stream, 200, "OK", BuildInventoryEditJson(query, manualWrite!));
                        break;
                    case "/inventory/bulk-set":
                        WriteResponse(stream, 200, "OK", BuildInventoryBulkEditJson(query, manualWrite!));
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
                                ReadRareGuestInvitationWriteExpectation(query), manualWrite!)));
                        break;
                    case "/rare-guests/invite":
                        WriteResponse(
                            stream,
                            200,
                            "OK",
                            BuildRareGuestInvitationJson(() => _inviteRareGuest(
                                ReadIntQuery(query, "guestId", -1),
                                ReadStringQuery(query, "scope"),
                                ReadRareGuestInvitationWriteExpectation(query), manualWrite!)));
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
                        WriteResponse(stream, 200, "OK", _customRecipeStore.Remove(
                            ReadStringQuery(query, "mode"), ReadCustomRecipeModeRevision(query), ReadStringQuery(query, "id")));
                        break;
                    case "/custom-recipes/settings":
                        WriteResponse(stream, 200, "OK", SetCustomRecipeSettingsJson(query));
                        break;
                    case "/custom-recipes/update-flags":
                        WriteResponse(stream, 200, "OK", UpdateCustomRecipeFlagsJson(query));
                        break;
                    case "/custom-recipes/move":
                        WriteResponse(stream, 200, "OK", _customRecipeStore.Move(
                            ReadStringQuery(query, "mode"), ReadCustomRecipeModeRevision(query),
                            ReadStringQuery(query, "id"),
                            ReadStringQuery(query, "direction")));
                        break;
                    default:
                        WriteResponse(stream, 404, "Not Found", ToJson(new LocalApiErrorDto { Error = "not found" }));
                        break;
                }
                }
                finally
                {
                    if (mutatesBusinessInput) InvalidateBusinessInput();
                    if (holdsAuthority) Monitor.Exit(_authorityTransitionLock);
                }
                return;
            }

            switch (path)
            {
                case "/game-media/status":
                    WriteResponse(stream, 200, "OK", ToJson(RequireGameMediaService().Status));
                    break;
                case "/game-media/manifest":
                    WriteResponse(stream, 200, "OK", ToJson(RequireGameMediaService().Manifest()));
                    break;
                case "/game-media/image":
                    WriteGameMediaImage(stream, query);
                    break;
                case "/business/status":
                    WriteResponse(stream, 200, "OK", GetBusinessStatusJson(query));
                    break;
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
                case "/devices":
                    WriteResponse(stream, 200, "OK", ToJson(ReadCompanionDevices(request)));
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
                case "/logs/packages":
                    WriteResponse(stream, 200, "OK", ListDiagnosticPackagesJson());
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
        catch (CompanionDeviceAuthorityException ex)
        {
            TryWriteErrorResponse(stream, ex.StatusCode, HttpStatusReason(ex.StatusCode), ex.Message);
        }
        catch (GameMediaException ex)
        {
            TryWriteErrorResponse(stream, ex.StatusCode, HttpStatusReason(ex.StatusCode), ex.Message);
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
        var knownSignature = ReadStringQuery(query, "knownSignature");
        var knownPresentation = ReadStringQuery(query, "knownOrderPresentationSignature");
        string currentSignature;
        string snapshotJson;
        lock (_snapshotLock)
        {
            currentSignature = _snapshotSignature;
            snapshotJson = _snapshotJson;
        }
        // 仅 HTTP 展示使用新顺序。原始正文/签名仍留给业务配对；在独立显示锁中处理少量订单，
        // 不持有 Unity 发布锁，不等待慢方案。显示协议版本单独入令牌，不能混作执行快照签名。
        var presentationSignature = LocalApiSnapshotPresentation.Signature(currentSignature);
        var unchanged = !string.IsNullOrWhiteSpace(knownSignature)
            && !string.IsNullOrWhiteSpace(currentSignature)
            && string.Equals(knownSignature, currentSignature, StringComparison.Ordinal)
            && string.Equals(knownPresentation, presentationSignature, StringComparison.Ordinal);
        var responseJson = unchanged
            ? ToJson(new LocalApiSnapshotUnchangedDto
            {
                Unchanged = true,
                SnapshotSignature = currentSignature,
                OrderPresentationSignature = presentationSignature,
            })
            : _snapshotPresentation.Get(snapshotJson, currentSignature).Json;

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
                current.SourceRevision,
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
            ClientProtocolVersion = MystiaStewardCompanion.Contracts.ClientProtocol.Version,
            BindAddress = BindAddress.ToString(),
            Port = Port,
            AuthRequired = true,
            LocalEndpoint = BaseUrl,
            LanEnabled = _lanEnabled,
            LanRunning = LanRunning,
            LanError = string.IsNullOrWhiteSpace(_lanError) ? null : _lanError,
        });
    }

    private CompanionDeviceAuthorityStateDto RegisterCompanionDevice(string request, HttpRequestData requestData)
    {
        var (clientId, clientLabel) = ReadRequiredClientIdentity(request);
        var body = ReadJsonRequest<CompanionDeviceRegisterRequest>(
            requestData,
            "protocolVersion",
            "profileSchemaVersion",
            "platform",
            "appVersion",
            "profile");
        lock (_authorityTransitionLock)
        {
            var state = _deviceAuthorityStore.Register(clientId, clientLabel, body, DateTime.UtcNow);
            RuntimeAutomationControlState.PublishAuthority(
                state.ActiveProfile,
                state.AuthorityRevision,
                "automation-authority-unavailable",
                "主设备已注册，正在同步自动化运行状态；已开始的料理保持暂停。");
            return state;
        }
    }

    private CompanionDeviceAuthorityStateDto ReadCompanionDevices(string request)
    {
        var (clientId, _) = ReadRequiredClientIdentity(request);
        return _deviceAuthorityStore.Read(clientId, DateTime.UtcNow);
    }

    private CompanionDeviceAuthorityStateDto UpdateCompanionDeviceProfile(string request, HttpRequestData requestData)
    {
        var (clientId, _) = ReadRequiredClientIdentity(request);
        var body = ReadJsonRequest<CompanionDeviceProfileUpdateRequest>(
            requestData,
            "protocolVersion",
            "profileSchemaVersion",
            "expectedAuthorityRevision",
            "expectedProfileRevision",
            "profile");
        lock (_authorityTransitionLock)
        {
            var previousAuthorityRevision = _deviceAuthorityStore.ReadAuthorityRevision();
            var state = _deviceAuthorityStore.UpdatePrimaryProfile(clientId, body, DateTime.UtcNow);
            if (state.AuthorityRevision != previousAuthorityRevision)
            {
                InvalidateRuntimeAuthority(
                    state,
                    "automation-profile-changing",
                    "主设备的自动化配置正在更新；已开始的料理会保留在原厨具，等待新配置同步完成。",
                    "primary companion functional profile changed");
            }
            return state;
        }
    }

    private CompanionDeviceAuthorityStateDto SetPrimaryCompanionDevice(string request, HttpRequestData requestData)
    {
        var (clientId, _) = ReadRequiredClientIdentity(request);
        var body = ReadJsonRequest<CompanionDeviceSetPrimaryRequest>(
            requestData,
            "protocolVersion",
            "expectedAuthorityRevision",
            "deviceId");
        lock (_authorityTransitionLock)
        {
            var result = _deviceAuthorityStore.SetPrimary(clientId, body, DateTime.UtcNow);
            if (result.Changed)
            {
                InvalidateRuntimeAuthority(
                    result.State,
                    "automation-primary-device-changing",
                    "主设备正在切换；已开始的料理会保留在原厨具，等待新主设备完成连接与配置同步。",
                    "primary companion device changed");
            }
            return result.State;
        }
    }

    private CompanionDeviceAuthorityStateDto SyncCompanionDeviceProfile(string request, HttpRequestData requestData)
    {
        var (clientId, _) = ReadRequiredClientIdentity(request);
        var body = ReadJsonRequest<CompanionDeviceSyncRequest>(
            requestData,
            "protocolVersion",
            "expectedAuthorityRevision",
            "deviceId");
        return _deviceAuthorityStore.SyncFromPrimary(clientId, body, DateTime.UtcNow);
    }

    private CompanionDeviceAuthorityStateDto AcknowledgeCompanionDeviceSync(string request, HttpRequestData requestData)
    {
        var (clientId, _) = ReadRequiredClientIdentity(request);
        var body = ReadJsonRequest<CompanionDeviceSyncAckRequest>(
            requestData,
            "protocolVersion",
            "syncId",
            "profileRevision",
            "profileHash");
        return _deviceAuthorityStore.AcknowledgeSync(clientId, body, DateTime.UtcNow);
    }

    private CompanionDeviceAuthorityStateDto RenameCompanionDevice(string request, HttpRequestData requestData)
    {
        var (clientId, _) = ReadRequiredClientIdentity(request);
        var body = ReadJsonRequest<CompanionDeviceRenameRequest>(requestData, "protocolVersion", "label");
        return _deviceAuthorityStore.Rename(clientId, body, DateTime.UtcNow);
    }

    private CompanionDeviceAuthorityStateDto ForgetCompanionDevice(string request, HttpRequestData requestData)
    {
        var (clientId, _) = ReadRequiredClientIdentity(request);
        var body = ReadJsonRequest<CompanionDeviceForgetRequest>(
            requestData,
            "protocolVersion",
            "expectedAuthorityRevision",
            "deviceId");
        return _deviceAuthorityStore.Forget(clientId, body, DateTime.UtcNow);
    }

    private void InvalidateRuntimeAuthority(
        CompanionDeviceAuthorityStateDto state,
        string reasonCode,
        string message,
        string logReason)
    {
        lock (_automationLeaseLock)
        {
            _automationLease = null;
            RuntimeAutomationControlState.PublishAuthority(
                state.ActiveProfile,
                state.AuthorityRevision,
                reasonCode,
                message);
            _automationCommandEpoch++;
            try
            {
                _advanceAutomationCommandEpoch(_automationCommandEpoch);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Companion authority transition could not advance queued automation commands cleanly: {ex.GetBaseException().Message}");
            }
        }
        RuntimeUiPinningService.ClearTargetsForAuthorityTransition(
            RuntimeNightBusinessLifecycle.Generation,
            logReason);
        _log.LogInfo($"Companion configuration authority advanced: {logReason}.");
    }

    private static T ReadJsonRequest<T>(HttpRequestData request, params string[] expectedProperties)
    {
        var json = HttpRequestReader.ReadRequiredJsonBody(request);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new CompanionDeviceAuthorityException(400, "JSON 请求体必须是对象。");
            }
            var actual = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (actual.Length != expectedProperties.Length
                || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length
                || actual.Any(name => !expectedProperties.Contains(name, StringComparer.Ordinal)))
            {
                throw new CompanionDeviceAuthorityException(400, "JSON 请求字段与当前设备协议不一致。");
            }

            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new CompanionDeviceAuthorityException(400, "JSON 请求体不能为空。");
        }
        catch (CompanionDeviceAuthorityException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new CompanionDeviceAuthorityException(400, "JSON 请求体格式无效。");
        }
    }

    private static (string ClientId, string ClientLabel) ReadRequiredClientIdentity(string request)
    {
        var (clientId, clientLabel, error) = ReadClientIdentity(request);
        if (!string.IsNullOrWhiteSpace(error))
        {
            throw new CompanionDeviceAuthorityException(400, error);
        }
        return (clientId, clientLabel);
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

        lock (_authorityTransitionLock)
        {
            var now = DateTime.UtcNow;
            if (!TryAuthorizeRuntimeWriter(request, clientId, now, out var authorityRevision, out var authorityError))
            {
                lock (_automationLeaseLock)
                {
                    PruneExpiredAutomationLease(now);
                    return BuildAutomationLeaseDto(clientId, clientLabel, authorityRevision, authorityError);
                }
            }
            lock (_automationLeaseLock)
            {
                PruneExpiredAutomationLease(now);
                return BuildAutomationLeaseDto(clientId, clientLabel, authorityRevision, null);
            }
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

        lock (_authorityTransitionLock)
        {
            var now = DateTime.UtcNow;
            if (!TryAuthorizeRuntimeWriter(request, clientId, now, out var authorityRevision, out var authorityError))
            {
                lock (_automationLeaseLock)
                {
                    PruneExpiredAutomationLease(now);
                    return BuildAutomationLeaseDto(clientId, clientLabel, authorityRevision, authorityError);
                }
            }
            lock (_automationLeaseLock)
            {
                PruneExpiredAutomationLease(now);
                if (_automationLease != null
                    && (!string.Equals(_automationLease.ClientId, clientId, StringComparison.Ordinal)
                        || _automationLease.AuthorityRevision != authorityRevision))
                {
                    return BuildAutomationLeaseDto(
                        clientId,
                        clientLabel,
                        authorityRevision,
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
                    AuthorityRevision = authorityRevision,
                };
                RuntimeAutomationControlState.PublishLease(
                    authorityRevision,
                    _automationLease.ExpiresAtUtc);
                return BuildAutomationLeaseDto(clientId, clientLabel, authorityRevision, null);
            }
        }
    }

    private LocalApiAutomationLeaseDto ReleaseAutomationLease(string request)
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

        lock (_authorityTransitionLock)
        {
            var now = DateTime.UtcNow;
            if (!TryAuthorizeRuntimeWriter(request, clientId, now, out var authorityRevision, out var authorityError))
            {
                return new LocalApiAutomationLeaseDto
                {
                    Ok = false,
                    ClientId = clientId,
                    ClientLabel = clientLabel,
                    AuthorityRevision = authorityRevision,
                    TtlMs = (int)AutomationLeaseTtl.TotalMilliseconds,
                    Error = authorityError,
                };
            }

            lock (_automationLeaseLock)
            {
                PruneExpiredAutomationLease(now);
                if (_automationLease != null
                    && (!string.Equals(_automationLease.ClientId, clientId, StringComparison.Ordinal)
                        || _automationLease.AuthorityRevision != authorityRevision))
                {
                    return BuildAutomationLeaseDto(
                        clientId,
                        clientLabel,
                        authorityRevision,
                        $"自动化当前由 {_automationLease.ClientLabel} 控制，本设备不能代替该设备停止自动化。");
                }

                if (_automationLease != null)
                {
                    _automationLease = null;
                    RuntimeAutomationControlState.RevokeLease(
                        "automation-lease-released",
                        "自动化已暂停；已开始的料理会保留在原厨具，等待主设备连接与配置重新就绪。");
                    _automationCommandEpoch++;
                    _advanceAutomationCommandEpoch(_automationCommandEpoch);
                }

                return BuildAutomationLeaseDto(clientId, clientLabel, authorityRevision, null);
            }
        }
    }

    private AutomationSafetyBarrierAckResult AcknowledgeAutomationSafetyBarrier(string request, HttpRequestData requestData)
    {
        var intent = ReadJsonRequest<BusinessBarrierAckRequest>(requestData,
            "protocolVersion", "automationSessionId", "businessGeneration", "sequence");
        var sequence = intent.Sequence;
        if (sequence <= 0)
        {
            return new AutomationSafetyBarrierAckResult
            {
                Ok = false,
                Sequence = sequence,
                Error = "automation barrier sequence must be a positive integer",
            };
        }

        var (clientId, _, identityError) = ReadClientIdentity(request);
        lock (_authorityTransitionLock)
        {
            RequireBusinessInteractionScope(intent.ProtocolVersion, intent.AutomationSessionId, intent.BusinessGeneration);
            var now = DateTime.UtcNow;
            var authorityRevision = 0L;
            var authorityError = "";
            var authorityOk = string.IsNullOrWhiteSpace(identityError)
                && TryAuthorizeRuntimeWriter(request, clientId, now, out authorityRevision, out authorityError);
            if (!authorityOk)
            {
                return new AutomationSafetyBarrierAckResult
                {
                    Ok = false,
                    Sequence = sequence,
                    Error = !string.IsNullOrWhiteSpace(identityError) ? identityError : authorityError,
                };
            }
            lock (_automationLeaseLock)
            {
                PruneExpiredAutomationLease(now);
                if (_automationLease == null
                    || !string.Equals(_automationLease.ClientId, clientId, StringComparison.Ordinal)
                    || _automationLease.AuthorityRevision != authorityRevision)
                {
                    return new AutomationSafetyBarrierAckResult
                    {
                        Ok = false,
                        Sequence = sequence,
                        Error = _automationLease == null
                            ? "自动化控制已暂停，请等待主设备连接与配置同步完成后，再处理人工确认事项。"
                            : $"自动化当前由 {_automationLease.ClientLabel} 控制，请在该设备处理人工确认事项。",
                    };
                }

                var result = _ackAutomationSafetyBarrier(sequence);
                _businessCoordinator.AcknowledgeResult(sequence,
                    System.Text.Json.Nodes.JsonNode.Parse(ToJson(result))!.AsObject(), BusinessNow);
                return result;
            }
        }
    }

    private bool TryRequireAutomationLease(
        string request,
        out LocalApiOrderActionErrorDto error,
        out long automationEpoch)
    {
        automationEpoch = 0;
        var (clientId, clientLabel, identityError) = ReadClientIdentity(request);
        lock (_authorityTransitionLock)
        {
            var now = DateTime.UtcNow;
            var authorityRevision = 0L;
            var authorityError = "";
            var authorityOk = string.IsNullOrWhiteSpace(identityError)
                && TryAuthorizeRuntimeWriter(request, clientId, now, out authorityRevision, out authorityError);
            lock (_automationLeaseLock)
            {
                PruneExpiredAutomationLease(now);
                var status = authorityOk
                    ? BuildAutomationLeaseDto(clientId, clientLabel, authorityRevision, null)
                    : new LocalApiAutomationLeaseDto
                    {
                        Ok = false,
                        ClientId = clientId,
                        ClientLabel = clientLabel,
                        AuthorityRevision = authorityRevision,
                        Error = !string.IsNullOrWhiteSpace(identityError) ? identityError : authorityError,
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
                            ? "请确认此设备为主设备，并等待连接与自动化配置同步完成后重试。"
                            : $"自动化当前由 {status.OwnerLabel} 控制，本窗口仅查看。"),
                };
                return false;
            }
        }
    }

    /// <summary>
    /// 与 Flutter 手工写入口保持同一权限表。查看设备仍能读取、查询推荐和检查更新；
    /// 设备切主/同步等由各自 CAS 协议处理，自动化另有 lease，不能混用这三类权限。
    /// </summary>
    private static bool RequiresPrimaryManualWrite(string path) => path is
        "/favorites/add-recipe" or "/favorites/remove-recipe" or "/favorites/add-beverage" or "/favorites/remove-beverage"
        or "/custom-recipes/upsert" or "/custom-recipes/remove" or "/custom-recipes/settings" or "/custom-recipes/update-flags" or "/custom-recipes/move"
        or "/inventory/set" or "/inventory/bulk-set" or "/rare-guests/invite" or "/rare-guests/invite-all" or "/orders/rare/dismiss"
        or "/logs/config" or "/logs/console" or "/logs/open-folder" or "/logs/export-diagnostics" or "/logs/packages/delete" or "/diagnostics/automation-decision"
        or "/local-api/config" or "/updates/download" or "/updates/install-on-exit" or "/game-media/reload";

    private static bool IsQueuedManualWrite(string path) => path is
        "/inventory/set" or "/inventory/bulk-set" or "/rare-guests/invite" or "/rare-guests/invite-all";

    /// <summary>先拒绝缺失身份/旧修订；闭包只捕获本请求不可变字符串，不读取后续客户端凭据。</summary>
    private LocalApiWritePermit CaptureManualWritePermit(string request, bool requiresMedia = false)
    {
        lock (_authorityTransitionLock)
        {
            ValidateManualWriteAuthority(request);
            if (requiresMedia) RequireGameMediaReady();
            var mediaGeneration = requiresMedia ? _gameMediaService?.Manifest().Generation : null;
            return new LocalApiWritePermit(_authorityTransitionLock,
                () =>
                {
                    ValidateManualWriteAuthority(request);
                    if (!requiresMedia) return;
                    RequireGameMediaReady();
                    // 已入 Unity 队列的请求不得跨图片重读代次获得追认，即使新代现在已经 ready。
                    if (_gameMediaService != null && _gameMediaService.Manifest().Generation != mediaGeneration)
                        throw new GameMediaException(409, "game-media-generation-stale");
                }, InvalidateBusinessInput);
        }
    }

    /// <summary>调用方持有权威锁。手工写无需自动化 lease，但必须来自当前主设备和精确修订。</summary>
    private void ValidateManualWriteAuthority(string request)
    {
        // 排入 Unity 的写许可也必须复核密码，改密后旧请求不能绕过 HTTP 入口继续执行。
        if (!IsAuthorized(request)) throw new CompanionDeviceAuthorityException(401, "连接密码已更新，请重新连接后重试。");
        var (clientId, _, identityError) = ReadClientIdentity(request);
        if (!string.IsNullOrWhiteSpace(identityError))
            throw new CompanionDeviceAuthorityException(403, identityError);
        if (!TryAuthorizeRuntimeWriter(request, clientId, DateTime.UtcNow, out _, out var error))
            throw new CompanionDeviceAuthorityException(403, error);
    }

    private bool TryAuthorizeRuntimeWriter(
        string request,
        string clientId,
        DateTime nowUtc,
        out long authorityRevision,
        out string error)
    {
        authorityRevision = ReadPositiveLongHeader(request, AuthorityRevisionHeaderName);
        return _deviceAuthorityStore.TryAuthorizePrimary(
            clientId,
            authorityRevision,
            nowUtc,
            out error);
    }

    private LocalApiAutomationLeaseDto BuildAutomationLeaseDto(
        string clientId,
        string clientLabel,
        long authorityRevision,
        string? error)
    {
        var lease = _automationLease;
        return new LocalApiAutomationLeaseDto
        {
            Ok = string.IsNullOrWhiteSpace(error),
            Owned = lease != null
                && string.Equals(lease.ClientId, clientId, StringComparison.Ordinal)
                && lease.AuthorityRevision == authorityRevision,
            ClientId = clientId,
            ClientLabel = clientLabel,
            AuthorityRevision = authorityRevision,
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
        if (_automationLease != null
            && (_automationLease.ExpiresAtUtc <= now
                || _automationLease.AuthorityRevision != _deviceAuthorityStore.ReadAuthorityRevision()))
        {
            var revisionChanged = _automationLease.AuthorityRevision
                != _deviceAuthorityStore.ReadAuthorityRevision();
            _automationLease = null;
            RuntimeAutomationControlState.RevokeLease(
                revisionChanged
                    ? "automation-authority-revision-changed"
                    : "automation-lease-expired",
                revisionChanged
                    ? "主设备或共享配置已变化；已开始的料理会保留在原厨具，当前主设备完成同步后继续。"
                    : "主设备未能及时保持连接，自动化已暂停；已开始的料理会保留在原厨具，连接恢复后继续。");
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
            // 创建与返回目录必须来自同一存储实例；生产默认目录仍由工厂解析，
            // 注入的隔离存储则完全不依赖尚未初始化的 BepInEx 全局路径。
            var packages = DiagnosticPackages;
            var packageDirectory = packages.DirectoryPath;
            var added = new List<string>();

            var packagePath = packages.Create(archive =>
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
                    "snapshot/runtime-available-mission-sources.json",
                    ToJson(RuntimeAvailableMissionSourceCapture.Snapshot()),
                    added);
                AddTextEntry(
                    archive,
                    "snapshot/runtime-mission-serve-in-work-diagnostic.json",
                    ToJson(RuntimeServeInWorkMissionDiagnosticCapture.Snapshot()),
                    added);
                AddAggregateLogEntries(archive, settings.AggregateModLogPath, DiagnosticTailMaxBytes, DiagnosticTailMaxLines, added);
            });

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

    private string BuildInventoryEditJson(string query, LocalApiWritePermit permit)
    {
        var itemType = ReadStringQuery(query, "type");
        if (!int.TryParse(ReadStringQuery(query, "id"), out var itemId)
            || !int.TryParse(ReadStringQuery(query, "qty"), out var quantity))
        {
            return ToJson(new LocalApiErrorDto { Error = "invalid inventory edit parameters" });
        }

        // 可选数量前提用于单项快捷加减；显式传入空值、非法值或负数必须拒绝，
        // 不能静默退回没有比较条件的绝对设置。真正的比较在主线程实读库存时完成。
        int? expectedQuantity = null;
        if (HasQueryParameter(query, "expectedQuantity"))
        {
            if (!int.TryParse(ReadStringQuery(query, "expectedQuantity"), out var expected) || expected < 0)
                return ToJson(new LocalApiErrorDto { Error = "库存校验数量无效，本次未修改。请刷新后再调整。" });
            expectedQuantity = expected;
        }

        try
        {
            var result = _editInventory(itemType, itemId, quantity, expectedQuantity, permit);
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

    private string BuildInventoryBulkEditJson(string query, LocalApiWritePermit permit)
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
            result = _editInventoryBulk(itemType, itemIds, quantity, permit);
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
            // 批量写入可能已完成其中一部分。明确保留计数与逐项原因，不能让调用方
            // 将失败理解为全量回滚，也不能仅因某项 changed 就宣称整批成功。
            Error = result.Failed == 0 ? null
                : $"库存修改未全部成功：变更 {result.Changed} 项，未变 {result.Unchanged} 项，失败 {result.Failed} 项。"
                    + string.Join("; ", result.Errors),
        });
    }

    private string BuildOrderActionJson(
        string query,
        Func<OrderPreparationRequest, OrderPreparationResult> action,
        long automationEpoch,
        Func<bool>? isBusinessInputCurrent = null)
    {
        try
        {
            var request = new OrderPreparationRequest
            {
                AutomationEpoch = automationEpoch,
                IsBusinessInputCurrent = isBusinessInputCurrent,
                TraceId = ReadStringQuery(query, "traceId"),
                OrderKey = ReadStringQuery(query, "orderKey"),
                OrderLifecycleSequence = ReadLongQuery(query, "orderLifecycleSequence", -1),
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

    private string UpdateUiPinningTargetsJson(string request, string query)
    {
        try
        {
            var (clientId, _, identityError) = ReadClientIdentity(request);
            lock (_authorityTransitionLock)
            {
                var now = DateTime.UtcNow;
                var authorityError = "";
                if (!string.IsNullOrWhiteSpace(identityError)
                    || !TryAuthorizeRuntimeWriter(request, clientId, now, out _, out authorityError))
                {
                    return ToJson(new LocalApiStatusDto
                    {
                        Ok = false,
                        Status = "",
                        Error = !string.IsNullOrWhiteSpace(identityError) ? identityError : authorityError,
                    });
                }
                if (!HasQueryParameter(query, "targetCount")
                    || ReadStringQuery(query, "targetCount") is not ("0" or "1" or "2")
                    || !int.TryParse(ReadStringQuery(query, "targetCount"), out var targetCount))
                {
                    throw new FormatException("targetCount must be exactly 0, 1, or 2.");
                }

                var targets = new List<RuntimeUiTargetSnapshot>(targetCount);
                for (var index = 0; index < targetCount; index += 1)
                {
                    targets.Add(ReadUiPinningTarget(query, index));
                }
                ValidateUiPinningTargetParameters(query, targetCount);
                if (targets.Count == 2
                    && (targets[0].Kind != RuntimeUiTargetKind.Rare
                        || targets[1].Kind != RuntimeUiTargetKind.Normal))
                {
                    throw new FormatException("Two UI targets must be ordered rare then normal.");
                }

                var status = RuntimeUiPinningService.UpdateTargets(
                    ReadRequiredPositiveLongQuery(query, "businessGeneration"),
                    targets);
                return ToJson(new LocalApiStatusDto { Ok = true, Status = status, Error = null });
            }
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
            if ((HasQueryParameter(query, "enabled") && ReadBoolQuery(query, "enabled") == null)
                || (HasQueryParameter(query, "pinToTop") && ReadBoolQuery(query, "pinToTop") == null)
                || (HasQueryParameter(query, "sortOrder") && ReadNullableIntQuery(query, "sortOrder") == null))
                return ToJson(new LocalApiCustomRecipeMutationDto { Ok = false, Error = "invalid custom recipe flags or sort order" });
            return _customRecipeStore.Upsert(new CustomRecipeMutation
            {
                Mode = ReadStringQuery(query, "mode"),
                ExpectedModeRevision = ReadCustomRecipeModeRevision(query),
                Id = ReadStringQuery(query, "id"),
                CustomerId = customerId,
                CustomerName = ReadStringQuery(query, "customerName"),
                FoodTag = ReadStringQuery(query, "foodTag"),
                FoodId = foodId,
                RecipeId = ReadIntQuery(query, "recipeId", -1),
                RecipeName = ReadStringQuery(query, "recipeName"),
                ExtraIngredientIds = ReadExactNonNegativeIntListQuery(query, "extraIngredientIds", 5),
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

    private string SetCustomRecipeSettingsJson(string query)
    {
        var enabled = ReadBoolQuery(query, "enabled");
        if (HasQueryParameter(query, "enabled") && enabled == null)
            return ToJson(new LocalApiCustomRecipeMutationDto { Ok = false, Error = "invalid custom recipe enabled setting" });
        var mode = HasQueryParameter(query, "activeMode") ? ReadStringQuery(query, "activeMode") : null;
        return enabled == null && mode == null
            ? ToJson(new LocalApiCustomRecipeMutationDto { Ok = false, Error = "invalid custom recipe enabled setting" })
            : _customRecipeStore.SetSettings(enabled, mode, ReadCustomRecipeModeRevision(query));
    }

    // 修订由读取到的配置提供；缺失、负数或解析失败均不能使用默认修订绕过切模栅栏。
    private static long ReadCustomRecipeModeRevision(string query) =>
        long.TryParse(ReadStringQuery(query, "expectedModeRevision"), out var revision) && revision >= 0 ? revision : -1;

    private string UpdateCustomRecipeFlagsJson(string query)
    {
        var scope = ReadStringQuery(query, "scope");
        var mode = ReadStringQuery(query, "mode");
        var revision = ReadCustomRecipeModeRevision(query);
        var selection = scope switch
        {
            "all" => new CustomRecipeSelection { Kind = CustomRecipeSelectionKind.All, Mode = mode, ExpectedModeRevision = revision },
            "customer" => new CustomRecipeSelection
            {
                Kind = CustomRecipeSelectionKind.Customer,
                Mode = mode, ExpectedModeRevision = revision,
                CustomerId = ReadIntQuery(query, "customerId", -1),
            },
            "recipe" => new CustomRecipeSelection
            {
                Kind = CustomRecipeSelectionKind.Recipe,
                Mode = mode, ExpectedModeRevision = revision,
                FoodId = ReadIntQuery(query, "foodId", -1),
            },
            "entry" => new CustomRecipeSelection
            {
                Kind = CustomRecipeSelectionKind.Entry,
                Mode = mode, ExpectedModeRevision = revision,
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

    private static string HttpStatusReason(int status)
    {
        return status switch
        {
            400 => "Bad Request",
            403 => "Forbidden",
            409 => "Conflict",
            413 => "Payload Too Large",
            431 => "Request Header Fields Too Large",
            503 => "Service Unavailable",
            _ => "Internal Server Error",
        };
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
        var token = Volatile.Read(ref _token);
        if (string.IsNullOrWhiteSpace(token)) return false;
        return string.Equals(ReadHeader(request, "X-Mystia-Steward-Companion-Token"), token, StringComparison.Ordinal);
    }

    private static (string ClientId, string ClientLabel, string? Error) ReadClientIdentity(string request)
    {
        var clientId = (ReadHeader(request, ClientIdHeaderName) ?? "").Trim();
        if (!IsValidClientId(clientId))
        {
            return ("", "伴随窗口", "请求缺少有效的伴随设备 ID。");
        }

        // 新客户端在 ASCII 请求头中发送 URI 编码后的 UTF-8 名称。先解码再校验字符数，
        // 避免中文被 HTTP 解析器替换为问号；控制字符不能经百分号编码绕过头校验。
        var label = Uri.UnescapeDataString((ReadHeader(request, ClientLabelHeaderName) ?? "").Trim());
        if (label.Any(char.IsControl)) return ("", "伴随窗口", "设备名称不能包含控制字符。");
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

    private static long ReadPositiveLongHeader(string request, string headerName)
    {
        var raw = ReadHeader(request, headerName);
        return long.TryParse(raw, out var value) && value > 0 ? value : 0;
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
