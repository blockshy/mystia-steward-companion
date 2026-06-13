using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using BepInEx;
using BepInEx.Logging;
using MystiaStewardCompanion.Save;

namespace MystiaStewardCompanion.LocalApi;

internal sealed class LocalApiServer : IDisposable
{
    private const int MaxRequestBytes = 32768;

    private readonly ManualLogSource _log;
    private readonly object _snapshotLock = new();
    private readonly string _token;
    private readonly string _healthJson;
    private readonly string _logOutputPath;
    private readonly Func<LocalApiLogSettings> _getLogSettings;
    private readonly Action<bool?, bool?, bool?> _updateLogSettings;
    private readonly Func<string, string> _openLogFolder;
    private readonly Func<string, int, int, RuntimeInventoryEditResult> _editInventory;
    private readonly Func<string, IReadOnlyList<int>, int, RuntimeInventoryBulkEditResult> _editInventoryBulk;
    private readonly Func<OrderPreparationRequest, OrderPreparationResult> _prepareOrder;
    private readonly Func<OrderPreparationRequest, OrderPreparationResult> _completeOrder;
    private readonly Func<OrderPreparationRequest, OrderPreparationResult> _completeNormalOrder;
    private readonly Func<string, RareGuestInvitationResult> _listRareGuestInvitations;
    private readonly Func<string, RareGuestInvitationResult> _inviteAllRareGuests;
    private readonly Func<int, string, RareGuestInvitationResult> _inviteRareGuest;
    private readonly FavoriteStore _favoriteStore;
    private TcpListener? _listener;
    private Thread? _thread;
    private bool _running;
    private string _snapshotJson = "{\"runtimeLoaded\":false,\"status\":\"Snapshot is not ready.\"}";

    public LocalApiServer(
        string configuredHost,
        int port,
        string pluginVersion,
        string token,
        Func<LocalApiLogSettings> getLogSettings,
        Action<bool?, bool?, bool?> updateLogSettings,
        Func<string, string> openLogFolder,
        Func<string, int, int, RuntimeInventoryEditResult> editInventory,
        Func<string, IReadOnlyList<int>, int, RuntimeInventoryBulkEditResult> editInventoryBulk,
        Func<OrderPreparationRequest, OrderPreparationResult> prepareOrder,
        Func<OrderPreparationRequest, OrderPreparationResult> completeOrder,
        Func<OrderPreparationRequest, OrderPreparationResult> completeNormalOrder,
        Func<string, RareGuestInvitationResult> listRareGuestInvitations,
        Func<string, RareGuestInvitationResult> inviteAllRareGuests,
        Func<int, string, RareGuestInvitationResult> inviteRareGuest,
        FavoriteStore favoriteStore,
        ManualLogSource log)
    {
        BindAddress = ResolveLoopbackAddress(configuredHost, log);
        Port = Math.Clamp(port, 1024, 65535);
        _log = log;
        _token = token.Trim();
        _getLogSettings = getLogSettings;
        _updateLogSettings = updateLogSettings;
        _openLogFolder = openLogFolder;
        _editInventory = editInventory;
        _editInventoryBulk = editInventoryBulk;
        _prepareOrder = prepareOrder;
        _completeOrder = completeOrder;
        _completeNormalOrder = completeNormalOrder;
        _listRareGuestInvitations = listRareGuestInvitations;
        _inviteAllRareGuests = inviteAllRareGuests;
        _inviteRareGuest = inviteRareGuest;
        _favoriteStore = favoriteStore;
        _logOutputPath = ResolveLogOutputPath();
        _healthJson = $"{{\"ok\":true,\"pluginVersion\":\"{EscapeJson(pluginVersion)}\",\"bindAddress\":\"{BindAddress}\",\"port\":{Port},\"authRequired\":true}}";
    }

    public IPAddress BindAddress { get; }
    public int Port { get; }
    public string BaseUrl => $"http://{FormatHostForUrl(BindAddress)}:{Port}";

    public void Start()
    {
        if (_running) return;

        _listener = new TcpListener(BindAddress, Port);
        _listener.Start();
        _running = true;
        _thread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "mystia-steward-companion Local API",
        };
        _thread.Start();
        _log.LogInfo($"Local API listening at {BaseUrl}. Use 127.0.0.1 to avoid proxy and localhost resolution issues.");
    }

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

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Stopping the listener during shutdown should not surface as a plugin error.
        }

        _listener = null;
        _thread = null;
    }

    private void ListenLoop()
    {
        while (_running)
        {
            try
            {
                var client = _listener?.AcceptTcpClient();
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
                _log.LogWarning($"Local API accept failed: {ex.Message}");
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 2500;
                client.SendTimeout = 2500;
                using var stream = client.GetStream();
                var request = ReadRequest(stream);
                var firstLine = request.Split('\n').FirstOrDefault()?.TrimEnd('\r') ?? "";
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    WriteResponse(stream, 400, "Bad Request", "{\"error\":\"bad request\"}");
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

                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    WriteResponse(stream, 405, "Method Not Allowed", "{\"error\":\"method not allowed\"}");
                    return;
                }

                if (RequiresAuthorization(path) && !IsAuthorized(request))
                {
                    WriteResponse(stream, 401, "Unauthorized", "{\"error\":\"unauthorized\"}");
                    return;
                }

                switch (path)
                {
                    case "/health":
                        WriteResponse(stream, 200, "OK", _healthJson);
                        break;
                    case "/snapshot":
                        WriteResponse(stream, 200, "OK", GetSnapshotJson());
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
                            ReadBoolQuery(query, "nativeConsole"));
                        WriteResponse(stream, 200, "OK", BuildLogSettingsJson());
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
                        WriteResponse(stream, 200, "OK", BuildOrderActionJson(query, _prepareOrder));
                        break;
                    case "/orders/complete-first":
                        WriteResponse(stream, 200, "OK", BuildOrderActionJson(query, _completeOrder));
                        break;
                    case "/orders/normal/complete-first":
                        WriteResponse(stream, 200, "OK", BuildOrderActionJson(query, _completeNormalOrder));
                        break;
                    case "/orders/rare/dismiss":
                        WriteResponse(stream, 200, "OK", BuildRareOrderDismissJson(query));
                        break;
                    case "/rare-guests/invitations":
                        WriteResponse(stream, 200, "OK", BuildRareGuestInvitationJson(() => _listRareGuestInvitations(ReadStringQuery(query, "scope"))));
                        break;
                    case "/rare-guests/invite-all":
                        WriteResponse(stream, 200, "OK", BuildRareGuestInvitationJson(() => _inviteAllRareGuests(ReadStringQuery(query, "scope"))));
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
                    default:
                        WriteResponse(stream, 404, "Not Found", "{\"error\":\"not found\"}");
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
            return "{\"capturedAtUtc\":\""
                + DateTime.UtcNow.ToString("O")
                + "\",\"path\":\""
                + EscapeJson(logPath)
                + "\",\"exists\":false,\"enabled\":false,\"maxLines\":"
                + maxLogLines
                + ",\"maxBytes\":"
                + maxLogBytes
                + ",\"lines\":[],\"error\":\"log access is disabled\"}";
        }

        try
        {
            var exists = File.Exists(logPath);
            var lines = exists ? ReadLogTail(logPath, maxLogBytes, maxLogLines) : new List<string>();
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"capturedAtUtc\":\"").Append(DateTime.UtcNow.ToString("O")).Append("\",");
            builder.Append("\"path\":\"").Append(EscapeJson(logPath)).Append("\",");
            builder.Append("\"exists\":").Append(exists ? "true" : "false").Append(',');
            builder.Append("\"enabled\":true,");
            builder.Append("\"maxLines\":").Append(maxLogLines).Append(',');
            builder.Append("\"maxBytes\":").Append(maxLogBytes).Append(',');
            builder.Append("\"lines\":[");
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append('"').Append(EscapeJson(lines[i])).Append('"');
            }
            builder.Append("],\"error\":null}");
            return builder.ToString();
        }
        catch (Exception ex)
        {
            return "{\"capturedAtUtc\":\""
                + DateTime.UtcNow.ToString("O")
                + "\",\"path\":\""
                + EscapeJson(logPath)
                + "\",\"exists\":false,\"enabled\":true,\"lines\":[],\"error\":\""
                + EscapeJson(ex.Message)
                + "\"}";
        }
    }

    private string BuildLogSettingsJson()
    {
        var settings = _getLogSettings();
        return new StringBuilder()
            .Append('{')
            .Append("\"logAccessEnabled\":").Append(settings.LogAccessEnabled ? "true" : "false").Append(',')
            .Append("\"logOutputPath\":\"").Append(EscapeJson(settings.LogOutputPath)).Append("\",")
            .Append("\"logOutputDirectory\":\"").Append(EscapeJson(GetDirectory(settings.LogOutputPath))).Append("\",")
            .Append("\"maxLogLines\":").Append(Math.Clamp(settings.MaxLogLines, 50, 2000)).Append(',')
            .Append("\"maxLogBytes\":").Append(Math.Clamp(settings.MaxLogBytes, 16 * 1024, 2 * 1024 * 1024)).Append(',')
            .Append("\"nightBusinessDiagnosticsEnabled\":").Append(settings.NightBusinessDiagnosticsEnabled ? "true" : "false").Append(',')
            .Append("\"nightBusinessDiagnosticsPath\":\"").Append(EscapeJson(settings.NightBusinessDiagnosticsPath)).Append("\",")
            .Append("\"nightBusinessDiagnosticsDirectory\":\"").Append(EscapeJson(GetDirectory(settings.NightBusinessDiagnosticsPath))).Append("\",")
            .Append("\"nativeBepInExConsoleEnabled\":").Append(settings.NativeBepInExConsoleEnabled ? "true" : "false").Append(',')
            .Append("\"nativeBepInExConsoleVisible\":").Append(settings.NativeBepInExConsoleVisible ? "true" : "false")
            .Append('}')
            .ToString();
    }

    private string OpenLogFolderJson(string target)
    {
        try
        {
            var directory = _openLogFolder(target);
            return "{\"ok\":true,\"directory\":\"" + EscapeJson(directory) + "\",\"error\":null}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"directory\":\"\",\"error\":\"" + EscapeJson(ex.Message) + "\"}";
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

            return new StringBuilder()
                .Append('{')
                .Append("\"ok\":true,")
                .Append("\"path\":\"").Append(EscapeJson(packagePath)).Append("\",")
                .Append("\"directory\":\"").Append(EscapeJson(packageDirectory)).Append("\",")
                .Append("\"files\":").Append(BuildJsonStringArray(added)).Append(',')
                .Append("\"error\":null")
                .Append('}')
                .ToString();
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"path\":\"\",\"directory\":\"\",\"files\":[],\"error\":\"" + EscapeJson(ex.Message) + "\"}";
        }
    }

    private string BuildInventoryEditJson(string query)
    {
        var itemType = ReadStringQuery(query, "type");
        if (!int.TryParse(ReadStringQuery(query, "id"), out var itemId)
            || !int.TryParse(ReadStringQuery(query, "qty"), out var quantity))
        {
            return "{\"ok\":false,\"error\":\"invalid inventory edit parameters\"}";
        }

        try
        {
            var result = _editInventory(itemType, itemId, quantity);
            var ok = string.IsNullOrWhiteSpace(result.Error);
            return new StringBuilder()
                .Append('{')
                .Append("\"ok\":").Append(ok ? "true" : "false").Append(',')
                .Append("\"type\":\"").Append(EscapeJson(result.ItemType)).Append("\",")
                .Append("\"id\":").Append(result.ItemId).Append(',')
                .Append("\"requestedQuantity\":").Append(result.RequestedQuantity).Append(',')
                .Append("\"previousQuantity\":").Append(result.PreviousQuantity).Append(',')
                .Append("\"quantity\":").Append(result.Quantity).Append(',')
                .Append("\"changed\":").Append(result.Changed ? "true" : "false").Append(',')
                .Append("\"error\":").Append(ok ? "null" : $"\"{EscapeJson(result.Error ?? "")}\"")
                .Append('}')
                .ToString();
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"error\":\"" + EscapeJson(ex.Message) + "\"}";
        }
    }

    private string BuildInventoryBulkEditJson(string query)
    {
        var itemType = ReadStringQuery(query, "type");
        var itemIds = ReadIntListQuery(query, "ids");
        if (!int.TryParse(ReadStringQuery(query, "qty"), out var quantity) || itemIds.Count == 0)
        {
            return "{\"ok\":false,\"error\":\"invalid inventory bulk edit parameters\"}";
        }

        RuntimeInventoryBulkEditResult result;
        try
        {
            result = _editInventoryBulk(itemType, itemIds, quantity);
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"error\":\"" + EscapeJson(ex.Message) + "\"}";
        }

        var builder = new StringBuilder()
            .Append('{')
            .Append("\"ok\":").Append(result.Failed == 0 ? "true" : "false").Append(',')
            .Append("\"type\":\"").Append(EscapeJson(result.ItemType)).Append("\",")
            .Append("\"requestedQuantity\":").Append(result.RequestedQuantity).Append(',')
            .Append("\"total\":").Append(result.Total).Append(',')
            .Append("\"changed\":").Append(result.Changed).Append(',')
            .Append("\"unchanged\":").Append(result.Unchanged).Append(',')
            .Append("\"failed\":").Append(result.Failed).Append(',')
            .Append("\"errors\":[");

        for (var i = 0; i < result.Errors.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append('"').Append(EscapeJson(result.Errors[i])).Append('"');
        }

        builder.Append("],\"error\":");
        builder.Append(result.Failed == 0 ? "null" : $"\"{EscapeJson(string.Join("; ", result.Errors))}\"");
        builder.Append('}');
        return builder.ToString();
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
                AcceptableFoodIds = ReadIntListQuery(query, "acceptableFoodIds"),
                TrayBacklogMinSeconds = Math.Max(0, ReadIntQuery(query, "trayBacklogMinSeconds", 0)),
                BeverageId = ReadIntQuery(query, "beverageId", -1),
                BeverageName = ReadStringQuery(query, "beverageName"),
                AutoTakeBeverage = ReadBoolQuery(query, "autoTakeBeverage") ?? false,
                AutoStartCooking = ReadBoolQuery(query, "autoStartCooking") ?? false,
                AutoCollectCooking = ReadBoolQuery(query, "autoCollectCooking") ?? false,
                FavoritesOnly = ReadBoolQuery(query, "favoritesOnly") ?? false,
                StopOnError = ReadBoolQuery(query, "stopOnError") ?? true,
                RecipeFavorite = ReadBoolQuery(query, "recipeFavorite") ?? false,
                BeverageFavorite = ReadBoolQuery(query, "beverageFavorite") ?? false,
            };

            var result = action(request);
            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"prepared\":false,\"error\":\"" + EscapeJson(ex.Message) + "\",\"order\":{\"deskCode\":-1,\"guestId\":null,\"guestName\":\"\",\"foodTag\":\"\",\"beverageTag\":\"\"},\"recipeId\":-1,\"recipeName\":\"\",\"beverageId\":-1,\"beverageName\":\"\",\"steps\":[]}";
        }
    }

    private static string BuildRareGuestInvitationJson(Func<RareGuestInvitationResult> action)
    {
        try
        {
            var result = action();
            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"runtimeAvailable\":false,\"status\":\"稀客邀请失败。\",\"error\":\""
                + EscapeJson(ex.Message)
                + "\",\"candidateCount\":0,\"usableCount\":0,\"existingSlotCount\":0,\"existingControlledCount\":0,\"scheduledSlotCount\":0,\"invitedCount\":0,\"skippedCount\":0,\"scope\":\"current\",\"currentMapLabel\":\"\",\"currentMapName\":\"\",\"candidates\":[],\"available\":[],\"invited\":[],\"skipped\":[]}";
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
            return "{\"ok\":true,\"removed\":" + removed + ",\"status\":\"" + EscapeJson(status) + "\",\"error\":null}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"removed\":0,\"status\":\"\",\"error\":\"" + EscapeJson(ex.Message) + "\"}";
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
            return "{\"ok\":true,\"status\":\"" + EscapeJson(status) + "\",\"error\":null}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"status\":\"\",\"error\":\"" + EscapeJson(ex.Message) + "\"}";
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
            return "{\"ok\":false,\"favorites\":{\"version\":1,\"recipes\":[],\"beverages\":[]},\"error\":\"" + EscapeJson(ex.Message) + "\"}";
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
            return "{\"ok\":false,\"favorites\":{\"version\":1,\"recipes\":[],\"beverages\":[]},\"error\":\"" + EscapeJson(ex.Message) + "\"}";
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
        return new StringBuilder()
            .Append('{')
            .Append("\"generatedAtUtc\":\"").Append(DateTime.UtcNow.ToString("O")).Append("\",")
            .Append("\"baseUrl\":\"").Append(EscapeJson(BaseUrl)).Append("\",")
            .Append("\"logOutputPath\":\"").Append(EscapeJson(string.IsNullOrWhiteSpace(settings.LogOutputPath) ? _logOutputPath : settings.LogOutputPath)).Append("\",")
            .Append("\"automationLogPath\":\"").Append(EscapeJson(RuntimeOrderPreparationService.ResolveAutomationLogPath())).Append("\",")
            .Append("\"nightBusinessDiagnosticsPath\":\"").Append(EscapeJson(settings.NightBusinessDiagnosticsPath)).Append("\",")
            .Append("\"maxLogLines\":").Append(Math.Clamp(settings.MaxLogLines, 50, 2000)).Append(',')
            .Append("\"maxLogBytes\":").Append(Math.Clamp(settings.MaxLogBytes, 16 * 1024, 2 * 1024 * 1024))
            .Append('}')
            .ToString();
    }

    private static string BuildJsonStringArray(IEnumerable<string> values)
    {
        var builder = new StringBuilder();
        builder.Append('[');
        var first = true;
        foreach (var value in values)
        {
            if (!first) builder.Append(',');
            builder.Append('"').Append(EscapeJson(value)).Append('"');
            first = false;
        }
        builder.Append(']');
        return builder.ToString();
    }

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
        headers.Append("Connection: close\r\n");
        headers.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (bodyBytes.Length > 0)
        {
            stream.Write(bodyBytes, 0, bodyBytes.Length);
        }
    }

    private static IPAddress ResolveLoopbackAddress(string configuredHost, ManualLogSource log)
    {
        if (IPAddress.TryParse(configuredHost, out var parsed) && IPAddress.IsLoopback(parsed))
        {
            return parsed.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        }

        if (!string.IsNullOrWhiteSpace(configuredHost)
            && !string.Equals(configuredHost, "127.0.0.1", StringComparison.Ordinal)
            && !string.Equals(configuredHost, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning($"Local API host '{configuredHost}' is not loopback. Falling back to 127.0.0.1.");
        }

        return IPAddress.Loopback;
    }

    private static string EscapeJson(string value)
    {
        return (value ?? "")
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
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

    private bool IsAuthorized(string request)
    {
        if (string.IsNullOrWhiteSpace(_token)) return false;
        return string.Equals(ReadHeader(request, "X-Mystia-Steward-Companion-Token"), _token, StringComparison.Ordinal);
    }

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
    public bool NativeBepInExConsoleEnabled { get; init; }
    public bool NativeBepInExConsoleVisible { get; init; }
}
