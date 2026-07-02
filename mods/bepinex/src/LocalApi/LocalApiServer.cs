using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using BepInEx;
using BepInEx.Logging;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Updates;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>
/// 运行在游戏进程内的本地 HTTP API，向伴随窗口暴露运行时快照并接收受控操作请求。
/// </summary>
/// <remarks>
/// 服务器使用轻量 <see cref="TcpListener"/>，避免在 IL2CPP Mod 中引入额外 Web 框架依赖。
/// 始终保留回环监听，LAN 监听只能作为显式开启的附加通道。除 <c>/health</c> 外所有端点都要求 Token。GET 端点用于读取和历史兼容的轻量动作，
/// POST 端点保留给更新检查、下载、安装排程等高风险副作用操作。
/// </remarks>
internal sealed class LocalApiServer : IDisposable
{
    private const int MaxRequestBytes = 32768;
    private const string AutoLanHost = "auto";
    private const string ClientIdHeaderName = "X-Mystia-Steward-Companion-Client-Id";
    private const string ClientLabelHeaderName = "X-Mystia-Steward-Companion-Client-Label";
    private static readonly TimeSpan AutomationLeaseTtl = TimeSpan.FromSeconds(15);

    private readonly ManualLogSource _log;
    private readonly object _snapshotLock = new();
    private readonly object _listenerLock = new();
    private readonly object _automationLeaseLock = new();
    private readonly string _pluginVersion;
    private string _token;
    private bool _lanEnabled;
    private string _lanBindHost;
    private string _lanError = "";
    private readonly string _logOutputPath;
    private readonly Func<LocalApiLogSettings> _getLogSettings;
    private readonly Action<bool?, bool?, bool?, bool?> _updateLogSettings;
    private readonly Func<LocalApiConnectionConfigDto> _getConnectionConfig;
    private readonly Func<LocalApiConnectionConfigUpdate, LocalApiConnectionConfigDto> _updateConnectionConfig;
    private readonly Func<LocalApiConnectionConfigDto> _regenerateLocalApiToken;
    private readonly Func<string, string> _openLogFolder;
    private readonly Func<string, int, int, RuntimeInventoryEditResult> _editInventory;
    private readonly Func<string, IReadOnlyList<int>, int, RuntimeInventoryBulkEditResult> _editInventoryBulk;
    private readonly Func<OrderPreparationRequest, OrderPreparationResult> _prepareOrder;
    private readonly Func<OrderPreparationRequest, OrderPreparationResult> _completeOrder;
    private readonly Func<OrderPreparationRequest, OrderPreparationResult> _completeNormalOrder;
    private readonly Func<string, string, RareGuestInvitationResult> _listRareGuestInvitations;
    private readonly Func<string, string, RareGuestInvitationResult> _inviteAllRareGuests;
    private readonly Func<int, string, RareGuestInvitationResult> _inviteRareGuest;
    private readonly UpdateService _updateService;
    private readonly FavoriteStore _favoriteStore;
    private readonly CustomRecipeStore _customRecipeStore;
    private readonly List<LocalApiListener> _listeners = new();
    private readonly List<IPAddress> _activeLanAddresses = new();
    private AutomationLease? _automationLease;
    private bool _running;
    private string _snapshotJson = "{\"runtimeLoaded\":false,\"status\":\"Snapshot is not ready.\"}";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class LocalApiListener
    {
        public string Name { get; init; } = "";
        public bool IsLan { get; init; }
        public TcpListener Listener { get; init; } = null!;
    }

    private sealed class AutomationLease
    {
        public string ClientId { get; init; } = "";
        public string ClientLabel { get; init; } = "";
        public DateTime LastSeenUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
    }

    public LocalApiServer(
        bool lanEnabled,
        string lanBindHost,
        int port,
        string pluginVersion,
        string token,
        Func<LocalApiLogSettings> getLogSettings,
        Action<bool?, bool?, bool?, bool?> updateLogSettings,
        Func<LocalApiConnectionConfigDto> getConnectionConfig,
        Func<LocalApiConnectionConfigUpdate, LocalApiConnectionConfigDto> updateConnectionConfig,
        Func<LocalApiConnectionConfigDto> regenerateLocalApiToken,
        Func<string, string> openLogFolder,
        Func<string, int, int, RuntimeInventoryEditResult> editInventory,
        Func<string, IReadOnlyList<int>, int, RuntimeInventoryBulkEditResult> editInventoryBulk,
        Func<OrderPreparationRequest, OrderPreparationResult> prepareOrder,
        Func<OrderPreparationRequest, OrderPreparationResult> completeOrder,
        Func<OrderPreparationRequest, OrderPreparationResult> completeNormalOrder,
        Func<string, string, RareGuestInvitationResult> listRareGuestInvitations,
        Func<string, string, RareGuestInvitationResult> inviteAllRareGuests,
        Func<int, string, RareGuestInvitationResult> inviteRareGuest,
        UpdateService updateService,
        FavoriteStore favoriteStore,
        CustomRecipeStore customRecipeStore,
        ManualLogSource log)
    {
        BindAddress = IPAddress.Loopback;
        Port = Math.Clamp(port, 1024, 65535);
        _log = log;
        _pluginVersion = pluginVersion;
        _token = token.Trim();
        _lanEnabled = lanEnabled;
        _lanBindHost = NormalizeLanBindHost(lanBindHost);
        _getLogSettings = getLogSettings;
        _updateLogSettings = updateLogSettings;
        _getConnectionConfig = getConnectionConfig;
        _updateConnectionConfig = updateConnectionConfig;
        _regenerateLocalApiToken = regenerateLocalApiToken;
        _openLogFolder = openLogFolder;
        _editInventory = editInventory;
        _editInventoryBulk = editInventoryBulk;
        _prepareOrder = prepareOrder;
        _completeOrder = completeOrder;
        _completeNormalOrder = completeNormalOrder;
        _listRareGuestInvitations = listRareGuestInvitations;
        _inviteAllRareGuests = inviteAllRareGuests;
        _inviteRareGuest = inviteRareGuest;
        _updateService = updateService;
        _favoriteStore = favoriteStore;
        _customRecipeStore = customRecipeStore;
        _logOutputPath = ResolveLogOutputPath();
    }

    public IPAddress BindAddress { get; }
    public int Port { get; }
    public string BaseUrl => $"http://{FormatHostForUrl(BindAddress)}:{Port}";
    public string LanBindHost => _lanBindHost;
    public bool LanEnabled => _lanEnabled;
    public bool LanRunning => GetActiveLanAddresses().Count > 0;
    public string LanError => _lanError;
    public IReadOnlyList<string> LanBindAddresses => GetActiveLanAddresses().Select(static address => address.ToString()).ToArray();
    public IReadOnlyList<string> LanEndpoints => GetActiveLanAddresses().Select(address => $"http://{FormatHostForUrl(address)}:{Port}").ToArray();

    /// <summary>
    /// 启动本地 API 监听线程。
    /// </summary>
    /// <remarks>
    /// 监听线程只负责接收连接和分派请求；需要访问 Unity 或游戏运行时对象的操作会通过委托回到
    /// <c>StewardOverlayController</c>，再由主线程队列执行，避免跨线程直接触碰 IL2CPP 对象。
    /// </remarks>
    public void Start()
    {
        if (_running) return;

        _running = true;
        try
        {
            StartListener("loopback", BindAddress, isLan: false);
            ApplyLanSettings(_lanEnabled, _lanBindHost);
            _log.LogInfo($"Local API loopback listener is available at {BaseUrl}. LAN listener is an optional add-on for trusted private networks.");
        }
        catch
        {
            _running = false;
            StopAllListeners();
            throw;
        }
    }

    /// <summary>
    /// 更新伴随窗口读取的最新快照 JSON。
    /// </summary>
    /// <param name="snapshotJson">已经序列化好的运行时快照。</param>
    /// <remarks>
    /// 快照在 Unity 主线程构建并一次性替换，API 线程只读取字符串副本，避免每个 HTTP 请求重复反射读取游戏对象。
    /// </remarks>
    public void SetSnapshotJson(string snapshotJson)
    {
        lock (_snapshotLock)
        {
            _snapshotJson = snapshotJson;
        }
    }

    public void Dispose()
    {
        _running = false;
        StopAllListeners();
    }

    public void ApplyLanSettings(bool lanEnabled, string lanBindHost)
    {
        _lanEnabled = lanEnabled;
        _lanBindHost = NormalizeLanBindHost(lanBindHost);
        StopLanListeners();
        _lanError = "";

        if (!_running || !lanEnabled)
        {
            if (_running) _log.LogInfo("Local API LAN listener is disabled. Loopback listener remains available.");
            return;
        }

        var bindAddresses = ResolveLanBindAddresses(_lanBindHost, _log);
        if (bindAddresses.Count == 0)
        {
            _lanError = "No private LAN IPv4 address is available for binding.";
            _log.LogWarning($"Local API LAN listener was not started: {_lanError}");
            return;
        }

        var started = new List<IPAddress>();
        foreach (var address in bindAddresses)
        {
            try
            {
                StartListener("LAN", address, isLan: true);
                started.Add(address);
            }
            catch (Exception ex)
            {
                _lanError = ex.Message;
                _log.LogWarning($"Local API LAN listener failed on {address}:{Port}: {ex.Message}");
            }
        }

        lock (_listenerLock)
        {
            _activeLanAddresses.Clear();
            _activeLanAddresses.AddRange(started);
        }

        if (started.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(_lanError)) _lanError = "LAN listener failed on all private IPv4 addresses.";
            _log.LogWarning($"Local API LAN listener was not started: {_lanError}");
            return;
        }

        _lanError = "";
        _log.LogInfo($"Local API LAN listener available at {string.Join(", ", started.Select(address => $"http://{address}:{Port}"))}.");
    }

    public void SetToken(string token)
    {
        _token = token.Trim();
    }

    private void StartListener(string name, IPAddress bindAddress, bool isLan)
    {
        var tcpListener = new TcpListener(bindAddress, Port);
        tcpListener.Start();
        var listener = new LocalApiListener
        {
            Name = name,
            IsLan = isLan,
            Listener = tcpListener,
        };
        var thread = new Thread(() => ListenLoop(listener))
        {
            IsBackground = true,
            Name = isLan
                ? $"mystia-steward-companion Local API LAN {bindAddress}"
                : "mystia-steward-companion Local API loopback",
        };
        lock (_listenerLock)
        {
            _listeners.Add(listener);
        }
        thread.Start();
    }

    private void StopLanListeners()
    {
        List<LocalApiListener> lanListeners;
        lock (_listenerLock)
        {
            lanListeners = _listeners.Where(static listener => listener.IsLan).ToList();
            _listeners.RemoveAll(static listener => listener.IsLan);
            _activeLanAddresses.Clear();
        }

        foreach (var listener in lanListeners)
        {
            StopListener(listener);
        }
    }

    private void StopAllListeners()
    {
        List<LocalApiListener> listeners;
        lock (_listenerLock)
        {
            listeners = _listeners.ToList();
            _listeners.Clear();
            _activeLanAddresses.Clear();
        }

        foreach (var listener in listeners)
        {
            StopListener(listener);
        }
    }

    private static void StopListener(LocalApiListener listener)
    {
        try
        {
            listener.Listener.Stop();
        }
        catch
        {
            // Stopping listeners during shutdown should not surface as a plugin error.
        }
    }

    private IReadOnlyList<IPAddress> GetActiveLanAddresses()
    {
        lock (_listenerLock)
        {
            return _activeLanAddresses.ToArray();
        }
    }

    private void ListenLoop(LocalApiListener listener)
    {
        while (_running)
        {
            try
            {
                var client = listener.Listener.AcceptTcpClient();
                if (client == null) continue;
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
            catch (SocketException) when (!_running)
            {
                return;
            }
            catch (ObjectDisposedException) when (!_running)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Local API {listener.Name} accept failed: {ex.Message}");
            }
        }
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
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 2500;
                client.SendTimeout = 2500;
                using var stream = client.GetStream();
                var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                if (!IsClientAddressAllowed(remoteEndPoint))
                {
                    WriteResponse(stream, 403, "Forbidden", ToJson(new LocalApiErrorDto { Error = "forbidden client address" }));
                    return;
                }

                var request = ReadRequest(stream);
                var firstLine = request.Split('\n').FirstOrDefault()?.TrimEnd('\r') ?? "";
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    WriteResponse(stream, 400, "Bad Request", ToJson(new LocalApiErrorDto { Error = "bad request" }));
                    return;
                }

                var method = parts[0];
                var (path, query) = SplitRequestTarget(parts[1]);
                path = NormalizeApiPath(path);
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
                    // 更新安装会下载文件、写入状态并启动独立进程，因此只接受 POST，避免被预取、刷新或普通链接误触发。
                    switch (path)
                    {
                        case "/automation/lease/acquire":
                            WriteResponse(stream, 200, "OK", ToJson(AcquireAutomationLease(request)));
                            break;
                        case "/automation/lease/release":
                            WriteResponse(stream, 200, "OK", ToJson(ReleaseAutomationLease(request)));
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
                        case "/updates/check":
                            WriteResponse(stream, 200, "OK", ToJson(_updateService.CheckForUpdates(force: true)));
                            break;
                        case "/updates/download":
                            WriteResponse(stream, 200, "OK", ToJson(_updateService.DownloadUpdate()));
                            break;
                        case "/updates/install-on-exit":
                            WriteResponse(stream, 200, "OK", ToJson(_updateService.InstallOnExit()));
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
                        WriteResponse(stream, 200, "OK", GetSnapshotJson());
                        break;
                    case "/automation/lease":
                        WriteResponse(stream, 200, "OK", ToJson(ReadAutomationLease(request)));
                        break;
                    case "/logs":
                        WriteResponse(stream, 200, "OK", BuildLogsJson());
                        break;
                    case "/logs/automation":
                        WriteResponse(stream, 200, "OK", BuildAutomationLogsJson());
                        break;
                    case "/logs/export-diagnostics":
                        WriteResponse(stream, 200, "OK", BuildDiagnosticPackageJson(ReadBoolQuery(query, "open") ?? false));
                        break;
                    case "/logs/settings":
                        WriteResponse(stream, 200, "OK", BuildLogSettingsJson());
                        break;
                    case "/logs/config":
                        _updateLogSettings(
                            ReadBoolQuery(query, "logAccess"),
                            ReadBoolQuery(query, "diagnostics"),
                            ReadBoolQuery(query, "nativeConsole"),
                            ReadBoolQuery(query, "aggregateLog"));
                        WriteResponse(stream, 200, "OK", BuildLogSettingsJson());
                        break;
                    case "/updates/status":
                        WriteResponse(stream, 200, "OK", ToJson(_updateService.GetStatus()));
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
                        if (!TryRequireAutomationLease(request, out var prepareLeaseError))
                        {
                            WriteResponse(stream, 200, "OK", ToJson(prepareLeaseError));
                            break;
                        }
                        WriteResponse(stream, 200, "OK", BuildOrderActionJson(query, _prepareOrder));
                        break;
                    case "/orders/complete-first":
                        if (!TryRequireAutomationLease(request, out var completeLeaseError))
                        {
                            WriteResponse(stream, 200, "OK", ToJson(completeLeaseError));
                            break;
                        }
                        WriteResponse(stream, 200, "OK", BuildOrderActionJson(query, _completeOrder));
                        break;
                    case "/orders/normal/complete-first":
                        if (!TryRequireAutomationLease(request, out var normalLeaseError))
                        {
                            WriteResponse(stream, 200, "OK", ToJson(normalLeaseError));
                            break;
                        }
                        WriteResponse(stream, 200, "OK", BuildOrderActionJson(query, _completeNormalOrder));
                        break;
                    case "/orders/rare/dismiss":
                        WriteResponse(stream, 200, "OK", BuildRareOrderDismissJson(query));
                        break;
                    case "/rare-guests/invitations":
                        WriteResponse(stream, 200, "OK", BuildRareGuestInvitationJson(() => _listRareGuestInvitations(ReadStringQuery(query, "scope"), ReadStringQuery(query, "levels"))));
                        break;
                    case "/rare-guests/invite-all":
                        WriteResponse(stream, 200, "OK", BuildRareGuestInvitationJson(() => _inviteAllRareGuests(ReadStringQuery(query, "scope"), ReadStringQuery(query, "levels"))));
                        break;
                    case "/rare-guests/invite":
                        WriteResponse(stream, 200, "OK", BuildRareGuestInvitationJson(() => _inviteRareGuest(ReadIntQuery(query, "guestId", -1), ReadStringQuery(query, "scope"))));
                        break;
                    case "/ui-pinning/target":
                        WriteResponse(stream, 200, "OK", UpdateUiPinningTargetJson(query));
                        break;
                    case "/favorites":
                        WriteResponse(stream, 200, "OK", _favoriteStore.GetJson());
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
                    case "/custom-recipes":
                        WriteResponse(stream, 200, "OK", _customRecipeStore.GetJson());
                        break;
                    case "/custom-recipes/upsert":
                        WriteResponse(stream, 200, "OK", UpsertCustomRecipeJson(query));
                        break;
                    case "/custom-recipes/remove":
                        WriteResponse(stream, 200, "OK", _customRecipeStore.Remove(ReadStringQuery(query, "id")));
                        break;
                    case "/custom-recipes/toggle":
                        WriteResponse(stream, 200, "OK", _customRecipeStore.Toggle(
                            ReadStringQuery(query, "id"),
                            ReadBoolQuery(query, "enabled") ?? true));
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
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Local API request failed: {ex.Message}");
            }
        }
    }

    private string GetSnapshotJson()
    {
        lock (_snapshotLock)
        {
            return _snapshotJson;
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
            LanBindAddresses = LanBindAddresses,
            LanEndpoints = LanEndpoints,
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

        lock (_automationLeaseLock)
        {
            PruneExpiredAutomationLease(DateTime.UtcNow);
            if (_automationLease != null
                && string.Equals(_automationLease.ClientId, clientId, StringComparison.Ordinal))
            {
                _automationLease = null;
            }

            return BuildAutomationLeaseDto(clientId, clientLabel, null);
        }
    }

    private bool TryRequireAutomationLease(string request, out LocalApiOrderActionErrorDto error)
    {
        var status = ReadAutomationLease(request);
        if (status.Ok && status.Owned)
        {
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

    private void PruneExpiredAutomationLease(DateTime now)
    {
        if (_automationLease != null && _automationLease.ExpiresAtUtc <= now)
        {
            _automationLease = null;
        }
    }

    private string BuildLogsJson()
    {
        var settings = _getLogSettings();
        var logPath = string.IsNullOrWhiteSpace(settings.LogOutputPath) ? _logOutputPath : settings.LogOutputPath;
        return BuildLogFileJson(logPath, settings);
    }

    private string BuildAutomationLogsJson()
    {
        var settings = _getLogSettings();
        return BuildLogFileJson(RuntimeOrderPreparationService.ResolveAutomationLogPath(), settings);
    }

    private static string BuildLogFileJson(string logPath, LocalApiLogSettings settings)
    {
        var maxLogBytes = Math.Clamp(settings.MaxLogBytes, 16 * 1024, 2 * 1024 * 1024);
        var maxLogLines = Math.Clamp(settings.MaxLogLines, 50, 2000);
        if (!settings.LogAccessEnabled)
        {
            return ToJson(new LocalApiLogFileDto
            {
                CapturedAtUtc = DateTime.UtcNow.ToString("O"),
                Path = logPath,
                Exists = false,
                Enabled = false,
                MaxLines = maxLogLines,
                MaxBytes = maxLogBytes,
                Lines = Array.Empty<string>(),
                Error = "log access is disabled",
            });
        }

        try
        {
            var exists = File.Exists(logPath);
            var lines = exists ? ReadLogTail(logPath, maxLogBytes, maxLogLines) : new List<string>();
            return ToJson(new LocalApiLogFileDto
            {
                CapturedAtUtc = DateTime.UtcNow.ToString("O"),
                Path = logPath,
                Exists = exists,
                Enabled = true,
                MaxLines = maxLogLines,
                MaxBytes = maxLogBytes,
                Lines = lines,
                Error = null,
            });
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiLogFileDto
            {
                CapturedAtUtc = DateTime.UtcNow.ToString("O"),
                Path = logPath,
                Exists = false,
                Enabled = true,
                MaxLines = maxLogLines,
                MaxBytes = maxLogBytes,
                Lines = Array.Empty<string>(),
                Error = ex.Message,
            });
        }
    }

    private string BuildLogSettingsJson()
    {
        var settings = _getLogSettings();
        return ToJson(new LocalApiLogSettingsDto
        {
            LogAccessEnabled = settings.LogAccessEnabled,
            LogOutputPath = settings.LogOutputPath,
            LogOutputDirectory = GetDirectory(settings.LogOutputPath),
            MaxLogLines = Math.Clamp(settings.MaxLogLines, 50, 2000),
            MaxLogBytes = Math.Clamp(settings.MaxLogBytes, 16 * 1024, 2 * 1024 * 1024),
            NightBusinessDiagnosticsEnabled = settings.NightBusinessDiagnosticsEnabled,
            NightBusinessDiagnosticsPath = settings.NightBusinessDiagnosticsPath,
            NightBusinessDiagnosticsDirectory = GetDirectory(settings.NightBusinessDiagnosticsPath),
            AggregateModLogEnabled = settings.AggregateModLogEnabled,
            AggregateModLogPath = settings.AggregateModLogPath,
            AggregateModLogDirectory = GetDirectory(settings.AggregateModLogPath),
            AggregateModLogMaxFileBytes = settings.AggregateModLogMaxFileBytes,
            NativeBepInExConsoleEnabled = settings.NativeBepInExConsoleEnabled,
            NativeBepInExConsoleVisible = settings.NativeBepInExConsoleVisible,
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
            var maxLogBytes = Math.Clamp(settings.MaxLogBytes, 16 * 1024, 2 * 1024 * 1024);
            var maxLogLines = Math.Clamp(settings.MaxLogLines, 50, 2000);

            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                AddTextEntry(archive, "manifest.json", BuildDiagnosticManifestJson(settings), added);
                AddTextEntry(archive, "snapshot/current-snapshot.json", GetSnapshotJson(), added);
                AddLogTailEntry(
                    archive,
                    string.IsNullOrWhiteSpace(settings.LogOutputPath) ? _logOutputPath : settings.LogOutputPath,
                    "logs/LogOutput.tail.log",
                    maxLogBytes,
                    maxLogLines,
                    added);
                AddLogTailEntry(
                    archive,
                    RuntimeOrderPreparationService.ResolveAutomationLogPath(),
                    "logs/automation-jobs.tail.log",
                    maxLogBytes,
                    maxLogLines,
                    added);
                AddLogTailEntry(
                    archive,
                    RuntimeOrderPreparationService.ResolveAutomationLogPath() + ".1",
                    "logs/automation-jobs.1.tail.log",
                    maxLogBytes,
                    maxLogLines,
                    added);
                AddDiagnosticLogEntries(archive, settings.NightBusinessDiagnosticsPath, maxLogBytes, maxLogLines, added);
                AddAggregateLogEntries(archive, settings.AggregateModLogPath, maxLogBytes, maxLogLines, added);
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

    private string BuildOrderActionJson(string query, Func<OrderPreparationRequest, OrderPreparationResult> action)
    {
        try
        {
            var request = new OrderPreparationRequest
            {
                OrderKey = ReadStringQuery(query, "orderKey"),
                DeskCode = ReadIntQuery(query, "deskCode", -1),
                GuestId = ReadNullableIntQuery(query, "guestId"),
                GuestName = ReadStringQuery(query, "guestName"),
                FoodTag = ReadStringQuery(query, "foodTag"),
                BeverageTag = ReadStringQuery(query, "beverageTag"),
                FoodId = ReadIntQuery(query, "foodId", -1),
                RecipeId = ReadIntQuery(query, "recipeId", -1),
                RecipeName = ReadStringQuery(query, "recipeName"),
                ExtraIngredientIds = ReadIntListQuery(query, "extraIngredientIds"),
                BeverageId = ReadIntQuery(query, "beverageId", -1),
                BeverageName = ReadStringQuery(query, "beverageName"),
                AutoTakeBeverage = ReadBoolQuery(query, "autoTakeBeverage") ?? false,
                AutoStartCooking = ReadBoolQuery(query, "autoStartCooking") ?? false,
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
                ReadNullableIntQuery(query, "guestId"),
                ReadStringQuery(query, "guestName"),
                ReadIntQuery(query, "foodTagId", int.MinValue),
                ReadIntQuery(query, "beverageTagId", int.MinValue));
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
            return ToJson(new LocalApiFavoriteErrorDto { Ok = false, Error = ex.Message });
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
            return ToJson(new LocalApiFavoriteErrorDto { Ok = false, Error = ex.Message });
        }
    }

    private string UpsertCustomRecipeJson(string query)
    {
        if (!int.TryParse(ReadStringQuery(query, "customerId"), out var customerId)
            || !int.TryParse(ReadStringQuery(query, "foodId"), out var foodId))
        {
            return ToJson(new LocalApiCustomRecipeErrorDto { Ok = false, Error = "invalid custom recipe parameters" });
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
                Enabled = ReadBoolQuery(query, "enabled") ?? true,
                PinToTop = ReadBoolQuery(query, "pinToTop") ?? true,
                SortOrder = ReadNullableIntQuery(query, "sortOrder"),
            });
        }
        catch (Exception ex)
        {
            return ToJson(new LocalApiCustomRecipeErrorDto { Ok = false, Error = ex.Message });
        }
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

    private static void AddDiagnosticLogEntries(ZipArchive archive, string primaryPath, int maxBytes, int maxLines, List<string> added)
    {
        var directory = Path.GetDirectoryName(primaryPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        foreach (var path in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName))
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) continue;
            AddLogTailEntry(archive, path, "diagnostics/" + name.Replace(".log", ".tail.log", StringComparison.Ordinal), maxBytes, maxLines, added);
        }
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
            LogOutputPath = string.IsNullOrWhiteSpace(settings.LogOutputPath) ? _logOutputPath : settings.LogOutputPath,
            AutomationLogPath = RuntimeOrderPreparationService.ResolveAutomationLogPath(),
            NightBusinessDiagnosticsPath = settings.NightBusinessDiagnosticsPath,
            AggregateModLogPath = settings.AggregateModLogPath,
            AggregateModLogMaxFileBytes = settings.AggregateModLogMaxFileBytes,
            MaxLogLines = Math.Clamp(settings.MaxLogLines, 50, 2000),
            MaxLogBytes = Math.Clamp(settings.MaxLogBytes, 16 * 1024, 2 * 1024 * 1024),
        });
    }

    /// <summary>
    /// 从 TCP 流中读取 HTTP 请求头。
    /// </summary>
    /// <param name="stream">客户端网络流。</param>
    /// <returns>ASCII 解码后的请求头文本。</returns>
    /// <remarks>
    /// 本地 API 不接收请求体，因此读到头部结束标记后立即返回。最大读取长度用于限制异常客户端占用内存。
    /// </remarks>
    private static string ReadRequest(NetworkStream stream)
    {
        var buffer = new byte[MaxRequestBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var count = stream.Read(buffer, total, buffer.Length - total);
            if (count <= 0) break;
            total += count;
            if (total >= 4
                && buffer[total - 4] == '\r'
                && buffer[total - 3] == '\n'
                && buffer[total - 2] == '\r'
                && buffer[total - 1] == '\n')
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, total);
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

    private static string NormalizeLanBindHost(string configuredHost)
    {
        var host = (configuredHost ?? "").Trim();
        if (string.IsNullOrWhiteSpace(host)
            || string.Equals(host, "0.0.0.0", StringComparison.Ordinal)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return AutoLanHost;
        }

        return host;
    }

    private static IReadOnlyList<IPAddress> ResolveLanBindAddresses(string configuredHost, ManualLogSource log)
    {
        var host = NormalizeLanBindHost(configuredHost);
        if (string.Equals(host, AutoLanHost, StringComparison.OrdinalIgnoreCase))
        {
            return GetPrivateLanIPv4Addresses();
        }

        if (IPAddress.TryParse(host, out var parsed))
        {
            var address = NormalizeIPv4Address(parsed);
            if (address != null && IsPrivateLanAddress(address))
            {
                return new[] { address };
            }
        }

        log.LogWarning($"Local API LAN host '{configuredHost}' is not a private IPv4 bind address. LAN listener will remain disabled.");
        return Array.Empty<IPAddress>();
    }

    private static IReadOnlyList<IPAddress> GetPrivateLanIPv4Addresses()
    {
        var addresses = new List<IPAddress>();

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    var address = NormalizeIPv4Address(unicast.Address);
                    if (address != null && IsPrivateLanAddress(address))
                    {
                        addresses.Add(address);
                    }
                }
            }
        }
        catch
        {
            // Fall back to DNS hostname resolution below.
        }

        if (addresses.Count == 0)
        {
            try
            {
                foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    var ipv4 = NormalizeIPv4Address(address);
                    if (ipv4 != null && IsPrivateLanAddress(ipv4))
                    {
                        addresses.Add(ipv4);
                    }
                }
            }
            catch
            {
                // No LAN address can be advertised.
            }
        }

        return addresses
            .Distinct()
            .OrderBy(static address => address.ToString(), StringComparer.Ordinal)
            .ToArray();
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

    public static string ResolveLogOutputPath()
    {
        try
        {
            return Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "BepInEx", "LogOutput.log");
        }
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

    private static string NormalizeApiPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return "/snapshot";
        if (path.StartsWith("/api/", StringComparison.Ordinal)) return path[4..];
        return path;
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

    private static string GetDirectory(string path)
    {
        return Path.GetDirectoryName(path) ?? "";
    }
}

internal sealed class LocalApiLogSettings
{
    public bool LogAccessEnabled { get; init; }
    public string LogOutputPath { get; init; } = "";
    public int MaxLogLines { get; init; } = 300;
    public int MaxLogBytes { get; init; } = 256 * 1024;
    public bool NightBusinessDiagnosticsEnabled { get; init; }
    public string NightBusinessDiagnosticsPath { get; init; } = "";
    public bool AggregateModLogEnabled { get; init; }
    public string AggregateModLogPath { get; init; } = "";
    public long AggregateModLogMaxFileBytes { get; init; } = AggregateModLogService.MaxFileBytes;
    public bool NativeBepInExConsoleEnabled { get; init; }
    public bool NativeBepInExConsoleVisible { get; init; }
}
