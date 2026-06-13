using BepInEx.Logging;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Plugin;
using MystiaStewardCompanion.Save;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MystiaStewardCompanion.Ui;

internal sealed class StewardOverlayController
{
    private const float SpecialOrderRefreshDebounceSeconds = 0.2f;
    private const float LocalApiSnapshotPublishMinIntervalSeconds = 0.5f;
    private const float RuntimeDataFullPublishIntervalSeconds = 10f;
    private const float PendingCookingProcessIntervalSeconds = 0.25f;
    private const float NormalBusinessSnapshotCacheSeconds = 0.75f;
    private const int SceneSnapshotStartupGuardFrames = 30;
    private const float SceneSnapshotStartupGuardSeconds = 2f;
    private static readonly JsonSerializerOptions LocalApiJsonOptions = new(JsonSerializerDefaults.Web);

    private StewardPluginConfig? _config;
    private ManualLogSource? _log;
    private DataRepository? _repository;
    private RuntimeDataCatalog _runtimeDataCatalog = RuntimeDataCatalog.Empty("not loaded");
    private RecommendationState? _state;
    private RecommendationState? _businessFallbackState;
    private NightBusinessContext? _businessContext;
    private NormalBusinessContext? _normalBusinessContext;
    private LocalApiServer? _localApiServer;
    private readonly object _inventoryEditLock = new();
    private readonly Queue<PendingInventoryEdit> _pendingInventoryEdits = new();
    private readonly Queue<PendingInventoryBulkEdit> _pendingInventoryBulkEdits = new();
    private readonly object _orderPreparationLock = new();
    private readonly Queue<PendingOrderPreparation> _pendingOrderPreparations = new();
    private readonly object _rareGuestInvitationLock = new();
    private readonly Queue<PendingRareGuestInvitation> _pendingRareGuestInvitations = new();
    private bool _runtimeLoaded;
    private int _mainThreadId;
    private long _lastSpecialOrderChangeVersion;
    private string _runtimeSource = "";
    private string _activeSceneName = "";
    private string _status = "Not initialized.";
    private string _lastRuntimeErrorMessage = "";
    private string _runtimeStateSignature = "";
    private string _localApiToken = "";
    private DateTime _lastRuntimeReadUtc = DateTime.MinValue;
    private float _nextAutoRefreshAt;
    private float _nextBusinessRefreshAt;
    private float _nextLocalApiSnapshotPublishAt;
    private float _nextRuntimeDataFullPublishAt;
    private float _nextPendingCookingProcessAt;
    private float _nextNormalBusinessRefreshAt;
    private float _sceneChangedAtRealtime;
    private int _sceneChangedFrame;
    private bool _localApiSnapshotErrorLogged;
    private bool _disposed;
    private bool _controllerToggleLatched;
    private bool _specialOrderRefreshPending;
    private bool _localApiSnapshotPublishPending;
    private float _nextControllerToggleAt;
    private float _nextSpecialOrderRefreshAt;
    private string _lastPublishedRuntimeDataSignature = "";
    private readonly List<RuntimeRareCustomer> _runtimeRareCustomers = new();
    private readonly Dictionary<string, double> _performanceMs = new(StringComparer.Ordinal);

    private sealed class PendingInventoryEdit
    {
        public string ItemType { get; init; } = "";
        public int ItemId { get; init; }
        public int Quantity { get; init; }
        public ManualResetEventSlim Completion { get; } = new(false);
        public RuntimeInventoryEditResult? Result { get; set; }
        public Exception? Error { get; set; }
    }

    private sealed class PendingInventoryBulkEdit
    {
        public string ItemType { get; init; } = "";
        public IReadOnlyList<int> ItemIds { get; init; } = Array.Empty<int>();
        public int Quantity { get; init; }
        public ManualResetEventSlim Completion { get; } = new(false);
        public RuntimeInventoryBulkEditResult? Result { get; set; }
        public Exception? Error { get; set; }
    }

    private sealed class PendingOrderPreparation
    {
        public OrderPreparationRequest Request { get; init; } = new();
        public OrderActionKind Action { get; init; }
        public ManualResetEventSlim Completion { get; } = new(false);
        public OrderPreparationResult? Result { get; set; }
        public Exception? Error { get; set; }
    }

    private sealed class PendingRareGuestInvitation
    {
        public RareGuestInvitationAction Action { get; init; }
        public int GuestId { get; init; } = -1;
        public string Scope { get; init; } = "";
        public ManualResetEventSlim Completion { get; } = new(false);
        public RareGuestInvitationResult? Result { get; set; }
        public Exception? Error { get; set; }
    }

    private enum OrderActionKind
    {
        PrepareRare,
        CompleteRare,
        CompleteNormal,
    }

    private enum RareGuestInvitationAction
    {
        List,
        InviteAll,
        InviteOne,
    }

    public void Initialize(StewardPluginConfig config, ManualLogSource log)
    {
        _config = config;
        _log = log;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _activeSceneName = GetActiveSceneName();
        _sceneChangedFrame = Time.frameCount;
        _sceneChangedAtRealtime = Time.realtimeSinceStartup;
        LoadRepository();
        _localApiToken = EnsureLocalApiToken(config);
        StartLocalApi();
        RefreshBusinessContext(false);
        RefreshRuntimeState(false);
        PublishLocalApiSnapshot(force: true);
        if (_localApiServer != null)
        {
            CompanionProcessLauncher.TryAutoLaunch(config, log, _localApiToken);
        }
    }

    public void Update()
    {
        if (_disposed || _config == null) return;
        ProcessPendingInventoryEdits();
        ProcessPendingInventoryBulkEdits();
        ProcessPendingOrderPreparations();
        ProcessPendingRareGuestInvitations();
        ProcessPendingCookingCollections();
        RefreshOnSceneChange();
        RefreshBusinessContextOnSpecialOrderChange();

        if (IsTogglePressed())
        {
            if (_log != null)
            {
                CompanionProcessLauncher.TryToggleOrLaunch(_config, _log, _localApiToken);
            }
        }

        FlushPendingLocalApiSnapshot();
        if (!_config.AutoRefreshRuntime.Value || Time.realtimeSinceStartup < _nextAutoRefreshAt) return;
        _nextAutoRefreshAt = Time.realtimeSinceStartup + Math.Max(1f, _config.AutoRefreshSeconds.Value);
        RefreshBusinessContext(false);
        RefreshRuntimeState(false);
        FlushPendingLocalApiSnapshot();
    }

    private void RefreshBusinessContextOnSpecialOrderChange()
    {
        if (_config == null || !_config.AutoRefreshRuntime.Value) return;

        var version = SpecialOrderRuntimeCapture.ChangeVersion;
        if (version != _lastSpecialOrderChangeVersion)
        {
            _lastSpecialOrderChangeVersion = version;
            _specialOrderRefreshPending = true;
            _nextSpecialOrderRefreshAt = Time.realtimeSinceStartup + SpecialOrderRefreshDebounceSeconds;
        }

        if (!_specialOrderRefreshPending || Time.realtimeSinceStartup < _nextSpecialOrderRefreshAt) return;

        _specialOrderRefreshPending = false;
        RefreshBusinessContext(false, force: true);
    }

    private void RefreshOnSceneChange()
    {
        if (_config == null) return;

        var sceneName = GetActiveSceneName();
        if (string.Equals(sceneName, _activeSceneName, StringComparison.Ordinal)) return;

        _activeSceneName = sceneName;
        _sceneChangedFrame = Time.frameCount;
        _sceneChangedAtRealtime = Time.realtimeSinceStartup;
        _nextAutoRefreshAt = 0f;
        _nextBusinessRefreshAt = 0f;
        _businessFallbackState = null;
        RuntimeMissionSnapshotService.ClearCache();
        _normalBusinessContext = null;
        _nextNormalBusinessRefreshAt = 0f;

        if (IsNonGameplayScene(sceneName))
        {
            ClearLoadedRuntime(L(
                "当前游戏运行时数据不可用：当前处于非游戏内页面。",
                "Live game runtime data unavailable: this is not an in-game page."));
            PublishLocalApiSnapshot();
            return;
        }

        _status = L(
            "已切换场景，正在刷新游戏数据。",
            "Scene changed. Refreshing game data.");
        PublishLocalApiSnapshot();
    }

    private bool IsTogglePressed()
    {
        if (_config == null) return false;
        if (Input.GetKeyDown(_config.ToggleKey.Value)) return true;

        var controllerHeld = IsControllerToggleHeld(_config.ControllerToggleKey.Value);
        if (!controllerHeld)
        {
            _controllerToggleLatched = false;
            return false;
        }

        if (_controllerToggleLatched || Time.realtimeSinceStartup < _nextControllerToggleAt) return false;
        if (!IsControllerTogglePressedThisFrame(_config.ControllerToggleKey.Value)) return false;

        _controllerToggleLatched = true;
        _nextControllerToggleAt = Time.realtimeSinceStartup + 1.2f;
        return true;
    }

    private static bool IsControllerTogglePressedThisFrame(KeyCode key)
    {
        if (Input.GetKeyDown(key)) return true;
        return key == KeyCode.JoystickButton9 && IsInputSystemRightStickWasPressed();
    }

    private static bool IsControllerToggleHeld(KeyCode key)
    {
        if (Input.GetKey(key)) return true;
        return key == KeyCode.JoystickButton9 && IsInputSystemRightStickHeld();
    }

    private static bool IsInputSystemRightStickWasPressed()
    {
        try
        {
            var gamepadType = Type.GetType("UnityEngine.InputSystem.Gamepad, Unity.InputSystem");
            var current = gamepadType?.GetProperty("current")?.GetValue(null);
            var rightStickButton = current?.GetType().GetProperty("rightStickButton")?.GetValue(current);
            var pressed = rightStickButton?.GetType().GetProperty("wasPressedThisFrame")?.GetValue(rightStickButton);
            return pressed is bool isPressed && isPressed;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInputSystemRightStickHeld()
    {
        try
        {
            var gamepadType = Type.GetType("UnityEngine.InputSystem.Gamepad, Unity.InputSystem");
            var current = gamepadType?.GetProperty("current")?.GetValue(null);
            var rightStickButton = current?.GetType().GetProperty("rightStickButton")?.GetValue(current);
            var pressed = rightStickButton?.GetType().GetProperty("isPressed")?.GetValue(rightStickButton);
            return pressed is bool isPressed && isPressed;
        }
        catch
        {
            return false;
        }
    }

    public void LateUpdate()
    {
        RuntimeCookerHighlightService.Tick();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        CompanionProcessLauncher.TryNotifyExit();
        _localApiServer?.Dispose();
        _localApiServer = null;
        RuntimeCookerHighlightService.Clear();
    }

    private void LoadRepository()
    {
        try
        {
            _repository = DataRepository.Empty();
            _runtimeDataCatalog = RuntimeDataCatalog.Empty("waiting for live game runtime data");
            _state = null;
            _businessFallbackState = null;
            _runtimeLoaded = false;
            _businessContext = null;
            _normalBusinessContext = null;
            _runtimeSource = "";
            _lastRuntimeErrorMessage = "";
            _runtimeStateSignature = "";
            _lastRuntimeReadUtc = DateTime.MinValue;
            _runtimeRareCustomers.Clear();
            _status = L(
                "等待游戏运行时数据；不再读取外部兜底数据。",
                "Waiting for live game runtime data; external fallback data is no longer loaded.");
        }
        catch (Exception ex)
        {
            _status = L($"数据加载失败：{ex.Message}", $"Failed to load data: {ex.Message}");
            _log?.LogError(ex);
        }
    }

    private void RefreshRuntimeState(bool manual)
    {
        if (_repository == null || _config == null) return;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            _activeSceneName = GetActiveSceneName();
            if (IsNonGameplayScene(_activeSceneName))
            {
                ClearLoadedRuntime(L(
                    "当前游戏运行时数据不可用：当前处于非游戏内页面。",
                    "Live game runtime data unavailable: this is not an in-game page."));
                return;
            }

            if (RuntimeReflectionRecommendationStateProvider.CanReadRuntimeState(out var runtimeReason))
            {
                var includePlacedCookers = HasActiveNightBusinessContext(_businessContext);
                var includeDaySceneState = ShouldReadDaySceneRuntimeState();
                var runtimeProvider = new RuntimeReflectionRecommendationStateProvider(
                    _repository,
                    includePlacedCookers,
                    includeDaySceneState);
                var previousSource = _runtimeSource;
                var nextRuntimeState = Measure("runtime.loadState", runtimeProvider.LoadState);
                RecordPerformanceEntries("runtime.", runtimeProvider.PerformanceMs);
                ApplyConfigOverrides(nextRuntimeState);
                var nextRuntimeSignature = BuildRecommendationStateSignature(nextRuntimeState);
                var stateChanged = _state == null
                    || !string.Equals(_runtimeStateSignature, nextRuntimeSignature, StringComparison.Ordinal);
                if (stateChanged)
                {
                    _state = nextRuntimeState;
                    _runtimeStateSignature = nextRuntimeSignature;
                    _businessFallbackState = null;
                }

                _runtimeLoaded = true;
                _runtimeSource = runtimeProvider.Description;
                _lastRuntimeErrorMessage = "";
                _lastRuntimeReadUtc = DateTime.UtcNow;
                TryRefreshRuntimeDataCatalog();
                var sourceChanged = !string.Equals(previousSource, runtimeProvider.Description, StringComparison.OrdinalIgnoreCase);
                var sourceLabel = FormatSourceDescription(runtimeProvider.Description);
                _status = manual || sourceChanged
                    ? L($"已读取游戏实时数据：{sourceLabel}", $"Live game data loaded: {sourceLabel}")
                    : L($"游戏实时数据已刷新：{sourceLabel}", $"Live game data refreshed: {sourceLabel}");
                return;
            }

            ClearLoadedRuntime(manual
                ? L($"无法读取游戏实时数据：{RuntimeReasonZh(runtimeReason)}", $"Cannot read live game data: {runtimeReason}")
                : L("当前游戏运行时数据不可用。进入游戏后会自动读取实时数据。", "Live game runtime data is unavailable. It will be detected after entering the game."));
        }
        catch (InvalidOperationException ex) when (string.Equals(
                   ex.Message,
                   "Game runtime data is empty; game progress may not be loaded.",
                   StringComparison.Ordinal))
        {
            _status = L(
                "基础运行时数据暂不可用；经营中页会继续读取稀客和点单。",
                "Base runtime data is temporarily unavailable; Service will keep reading rare customers and orders.");
            if (!_runtimeLoaded) _state = null;
            _runtimeSource = "";
            if (!_runtimeLoaded)
            {
                _runtimeStateSignature = "";
            }

            _lastRuntimeReadUtc = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            _status = L($"读取游戏实时数据失败：{RuntimeReasonZh(ex.Message)}", $"Failed to load live game data: {ex.Message}");
            if (!_runtimeLoaded)
            {
                _state = null;
                _runtimeStateSignature = "";
            }

            LogRuntimeError(ex, manual);
        }
        finally
        {
            RecordPerformance("refresh.runtime", stopwatch.Elapsed);
            PublishLocalApiSnapshot();
        }
    }

    private void RefreshBusinessContext(bool manual, bool force = false)
    {
        if (_repository == null || _config == null) return;
        if (!manual && !force && _businessContext != null && Time.realtimeSinceStartup < _nextBusinessRefreshAt) return;

        _nextBusinessRefreshAt = Time.realtimeSinceStartup + Math.Max(1f, _config.AutoRefreshSeconds.Value);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            _activeSceneName = GetActiveSceneName();
            if (IsNonGameplayScene(_activeSceneName))
            {
                _businessContext = new NightBusinessContext
                {
                    Error = L("当前存档未加载：处于非游戏内页面。", "Current save is not loaded: this is not an in-game page."),
                };
                return;
            }

            if (!IsNightBusinessScene(_activeSceneName))
            {
                SpecialOrderRuntimeCapture.ClearOrders("left night business scene");
                _businessContext = new NightBusinessContext
                {
                    Source = L("当前不在夜晚经营场景。", "Not in a night business scene."),
                };
                _normalBusinessContext = null;
                _nextNormalBusinessRefreshAt = 0f;
                ClearPlacedCookersFromCurrentState("not in night business scene");
                RuntimeCookerHighlightService.Clear();
                if (manual) _status = L("当前无经营场景。", "No active business scene.");
                return;
            }

            TryRefreshRuntimeDataCatalog();
            var provider = new NightBusinessReflectionProvider(
                _repository,
                CreateNightBusinessDiagnostics(),
                _activeSceneName);
            _businessContext = Measure("business.rare.total", provider.LoadContext);
            RecordPerformanceEntries("business.rare.", provider.PerformanceMs);
            RefreshNormalBusinessContext(force: true);
            if (manual)
            {
                _status = _businessContext.Orders.Count > 0
                    ? L($"已读取经营订单：{_businessContext.Orders.Count} 条。", $"Service orders loaded: {_businessContext.Orders.Count}.")
                    : L("尚未检测到稀客点单。", "No rare-customer order detected yet.");
            }
        }
        catch (Exception ex)
        {
            _businessContext = new NightBusinessContext
            {
                Error = ex.Message,
            };
            if (manual) _status = L($"读取经营订单失败：{ex.Message}", $"Failed to read service orders: {ex.Message}");
            _log?.LogError(ex);
        }
        finally
        {
            RecordPerformance("refresh.business", stopwatch.Elapsed);
            PublishLocalApiSnapshot();
        }
    }

    private void StartLocalApi()
    {
        if (_config == null || _log == null || !_config.LocalApiEnabled.Value) return;

        try
        {
            _localApiServer = new LocalApiServer(
                _config.LocalApiHost.Value,
                _config.LocalApiPort.Value,
                MystiaStewardCompanionPlugin.PluginVersion,
                _localApiToken,
                GetLocalApiLogSettings,
                UpdateLocalApiLogSettings,
                OpenLocalApiLogFolder,
                EditInventoryFromLocalApi,
                EditInventoryBulkFromLocalApi,
                PrepareOrderFromLocalApi,
                CompleteOrderFromLocalApi,
                CompleteNormalOrderFromLocalApi,
                ListRareGuestInvitationsFromLocalApi,
                InviteAllRareGuestsFromLocalApi,
                InviteRareGuestFromLocalApi,
                new FavoriteStore(FavoriteStore.ResolvePath(), _log),
                _log);
            _localApiServer.Start();
        }
        catch (Exception ex)
        {
            _localApiServer = null;
            _status = L(
                $"本地 API 启动失败：{ex.Message}",
                $"Local API failed to start: {ex.Message}");
            _log.LogWarning($"Local API failed to start: {ex}");
        }
    }

    private void PublishLocalApiSnapshot(bool force = false)
    {
        if (_localApiServer == null) return;
        if (!force && Time.realtimeSinceStartup < _nextLocalApiSnapshotPublishAt)
        {
            _localApiSnapshotPublishPending = true;
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            _localApiSnapshotPublishPending = false;
            _nextLocalApiSnapshotPublishAt = Time.realtimeSinceStartup + LocalApiSnapshotPublishMinIntervalSeconds;
            TryRefreshRuntimeDataCatalog();
            var publishedState = _state ?? (_businessContext?.Orders.Count > 0 ? GetBusinessRecommendationState() : null);
            var dayMap = ReadActiveDayMapForSnapshot();
            var snapshot = new LocalApiSnapshot
            {
                PluginVersion = MystiaStewardCompanionPlugin.PluginVersion,
                CapturedAtUtc = DateTime.UtcNow,
                ActiveSceneName = _activeSceneName,
                ActiveDayMapLabel = dayMap.Label,
                ActiveDayMapName = dayMap.Name,
                RuntimeLoaded = _runtimeLoaded,
                Status = _status,
                RuntimeSource = _runtimeSource,
                RuntimeUiPinningStatus = RuntimeUiPinningService.Status,
                RecommendationState = Measure(
                    "snapshot.recommendationState",
                    () => publishedState == null ? null : RecommendationStateSnapshot.From(publishedState)),
                NightBusiness = _businessContext,
                RuntimeMissions = Measure("snapshot.missions", ReadRuntimeMissionsForSnapshot),
                NormalBusiness = Measure("snapshot.normalBusiness", ReadNormalBusinessForSnapshot),
                RuntimeRareCustomers = _runtimeRareCustomers.ToList(),
                RuntimeData = BuildRuntimeDataForSnapshot(force),
                PerformanceMs = BuildPerformanceSnapshot(),
            };
            _localApiServer.SetSnapshotJson(Measure("snapshot.serialize", () => JsonSerializer.Serialize(snapshot, LocalApiJsonOptions)));
            _localApiSnapshotErrorLogged = false;
        }
        catch (Exception ex)
        {
            if (_localApiSnapshotErrorLogged) return;
            _localApiSnapshotErrorLogged = true;
            _log?.LogWarning($"Local API snapshot publish failed: {ex.Message}");
        }
        finally
        {
            RecordPerformance("snapshot.publish", stopwatch.Elapsed);
        }
    }

    private (string Label, string Name) ReadActiveDayMapForSnapshot()
    {
        if (!_runtimeLoaded) return ("", "");
        if (IsNonGameplayScene(_activeSceneName)) return ("", "");
        if (IsNightBusinessScene(_activeSceneName)) return ("", "");
        if (IsSceneSnapshotStartupGuardActive()) return ("", "");

        try
        {
            var map = RuntimeRareGuestInvitationService.ReadCurrentDaySceneMapInfo();
            return (map.Label, map.Name);
        }
        catch
        {
            return ("", "");
        }
    }

    private void FlushPendingLocalApiSnapshot()
    {
        if (!_localApiSnapshotPublishPending) return;
        if (Time.realtimeSinceStartup < _nextLocalApiSnapshotPublishAt) return;

        PublishLocalApiSnapshot();
    }

    private RuntimeDataCatalog? BuildRuntimeDataForSnapshot(bool force)
    {
        if (!_runtimeDataCatalog.IsComplete) return _runtimeDataCatalog;

        var signature = BuildRuntimeDataSignature(_runtimeDataCatalog);
        if (force
            || Time.realtimeSinceStartup >= _nextRuntimeDataFullPublishAt
            || !string.Equals(signature, _lastPublishedRuntimeDataSignature, StringComparison.Ordinal))
        {
            _lastPublishedRuntimeDataSignature = signature;
            _nextRuntimeDataFullPublishAt = Time.realtimeSinceStartup + RuntimeDataFullPublishIntervalSeconds;
            return _runtimeDataCatalog;
        }

        return null;
    }

    private static string BuildRuntimeDataSignature(RuntimeDataCatalog catalog)
    {
        return string.Join(
            "|",
            catalog.IsComplete ? "1" : "0",
            catalog.Source,
            catalog.Status,
            catalog.Recipes.Count,
            catalog.Ingredients.Count,
            catalog.Beverages.Count,
            catalog.NormalCustomers.Count,
            catalog.RareCustomers.Count,
            catalog.FoodTagIdMap.Count);
    }

    private T Measure<T>(string key, Func<T> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            RecordPerformance(key, stopwatch.Elapsed);
        }
    }

    private void RecordPerformance(string key, TimeSpan elapsed)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        var milliseconds = Math.Round(elapsed.TotalMilliseconds, 2);
        _performanceMs[key] = milliseconds;
    }

    private Dictionary<string, double> BuildPerformanceSnapshot()
    {
        return _performanceMs
            .OrderByDescending(item => item.Value)
            .Take(12)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

    private void RecordPerformanceEntries(string prefix, IReadOnlyDictionary<string, double> entries)
    {
        foreach (var entry in entries)
        {
            _performanceMs[$"{prefix}{entry.Key}"] = entry.Value;
        }
    }

    private void TryRefreshRuntimeDataCatalog()
    {
        if (_repository == null) return;
        if (_runtimeDataCatalog.IsComplete) return;

        try
        {
            var mappedGuestSnapshot = Measure("runtimeData.mappedGuests", () => new RuntimeMappedGuestCatalog(_repository).Snapshot());
            _runtimeRareCustomers.Clear();
            _runtimeRareCustomers.AddRange(mappedGuestSnapshot.RuntimeRareCustomers);
            var staticDataSnapshot = Measure("runtimeData.staticData", () => new RuntimeStaticDataCatalog(_repository).Snapshot(mappedGuestSnapshot));
            _runtimeDataCatalog = staticDataSnapshot.DataCatalog;
            if (!_runtimeDataCatalog.IsComplete) return;

            _repository = DataRepository.FromRuntime(_runtimeDataCatalog);
            _businessFallbackState = null;
            _runtimeSource = string.IsNullOrWhiteSpace(_runtimeSource)
                ? "game-runtime-static-data"
                : $"{_runtimeSource}; runtime-static-data";
        }
        catch (Exception ex)
        {
            _runtimeDataCatalog = RuntimeDataCatalog.Empty($"runtime data unavailable: {ex.Message}");
        }
    }

    private RuntimeMissionContext? ReadRuntimeMissionsForSnapshot()
    {
        if (!_runtimeLoaded) return null;

        if (IsNonGameplayScene(_activeSceneName))
        {
            return new RuntimeMissionContext
            {
                Source = "任务数据等待存档加载完成。",
            };
        }

        if (IsNightBusinessScene(_activeSceneName))
        {
            var context = new RuntimeMissionContext
            {
                Source = "任务数据只在日间场景读取；当前处于夜间经营。",
            };
            return Measure("mission.serveTargets", () => RuntimeMissionSnapshotService.WithServeTargets(
                context,
                _repository,
                _businessContext?.ActiveRareGuests ?? Enumerable.Empty<NightBusinessGuest>(),
                _businessContext?.Orders ?? Enumerable.Empty<NightBusinessOrder>()));
        }

        if (IsSceneSnapshotStartupGuardActive())
        {
            return new RuntimeMissionContext
            {
                Source = "任务数据等待日间 UI 初始化完成。",
            };
        }

        try
        {
            var missions = Measure("mission.load", RuntimeMissionSnapshotService.Load);
            return Measure("mission.serveTargets", () => RuntimeMissionSnapshotService.WithServeTargets(
                missions,
                _repository,
                _businessContext?.ActiveRareGuests ?? Enumerable.Empty<NightBusinessGuest>(),
                _businessContext?.Orders ?? Enumerable.Empty<NightBusinessOrder>()));
        }
        catch (Exception ex)
        {
            return new RuntimeMissionContext
            {
                Source = "error",
                Error = ex.Message,
            };
        }
    }

    private bool IsSceneSnapshotStartupGuardActive()
    {
        return Time.frameCount - _sceneChangedFrame < SceneSnapshotStartupGuardFrames
            || Time.realtimeSinceStartup - _sceneChangedAtRealtime < SceneSnapshotStartupGuardSeconds;
    }

    private bool ShouldReadDaySceneRuntimeState()
    {
        if (!_runtimeLoaded && IsSceneSnapshotStartupGuardActive()) return false;
        if (IsNonGameplayScene(_activeSceneName)) return false;
        if (IsNightBusinessScene(_activeSceneName)) return true;
        return !IsSceneSnapshotStartupGuardActive();
    }

    private NormalBusinessContext? ReadNormalBusinessForSnapshot()
    {
        if (_repository == null || !HasActiveNightBusinessContext(_businessContext))
        {
            _normalBusinessContext = null;
            _nextNormalBusinessRefreshAt = 0f;
            return null;
        }

        return RefreshNormalBusinessContext(force: false);
    }

    private NormalBusinessContext? RefreshNormalBusinessContext(bool force)
    {
        if (_repository == null || !HasActiveNightBusinessContext(_businessContext))
        {
            _normalBusinessContext = null;
            return null;
        }

        if (!force
            && _normalBusinessContext != null
            && Time.realtimeSinceStartup < _nextNormalBusinessRefreshAt)
        {
            return _normalBusinessContext;
        }

        try
        {
            var service = new RuntimeNormalOrderSnapshotService(_repository);
            _normalBusinessContext = Measure("business.normal.total", service.Load);
            RecordPerformanceEntries("business.normal.", service.PerformanceMs);
            _nextNormalBusinessRefreshAt = Time.realtimeSinceStartup + NormalBusinessSnapshotCacheSeconds;
            return _normalBusinessContext;
        }
        catch (Exception ex)
        {
            _normalBusinessContext = new NormalBusinessContext
            {
                Source = "error",
                Error = ex.Message,
            };
            _nextNormalBusinessRefreshAt = Time.realtimeSinceStartup + NormalBusinessSnapshotCacheSeconds;
            return _normalBusinessContext;
        }
    }

    private NightBusinessDiagnosticSink? CreateNightBusinessDiagnostics()
    {
        if (_config == null || !_config.EnableNightBusinessDiagnostics.Value) return null;

        return new NightBusinessDiagnosticSink(
            _config.NightBusinessDiagnosticsPath.Value,
            TimeSpan.FromSeconds(Math.Max(1f, _config.NightBusinessDiagnosticsIntervalSeconds.Value)));
    }

    private LocalApiLogSettings GetLocalApiLogSettings()
    {
        if (_config == null)
        {
            return new LocalApiLogSettings
            {
                LogOutputPath = LocalApiServer.ResolveLogOutputPath(),
                MaxLogLines = 300,
                MaxLogBytes = 256 * 1024,
                NightBusinessDiagnosticsPath = NightBusinessDiagnosticSink.ResolvePath(""),
                NativeBepInExConsoleVisible = BepInExConsoleHelper.IsCurrentConsoleWindowVisible(),
            };
        }

        return new LocalApiLogSettings
        {
            LogAccessEnabled = _config.ExposeLocalApiLogs.Value,
            LogOutputPath = LocalApiServer.ResolveLogOutputPath(),
            MaxLogLines = _config.LocalApiMaxLogLines.Value,
            MaxLogBytes = _config.LocalApiMaxLogBytes.Value,
            NightBusinessDiagnosticsEnabled = _config.EnableNightBusinessDiagnostics.Value,
            NightBusinessDiagnosticsPath = NightBusinessDiagnosticSink.ResolvePath(_config.NightBusinessDiagnosticsPath.Value),
            NativeBepInExConsoleEnabled = !_config.DisableBepInExConsoleLog.Value && !_config.HideBepInExConsoleWindow.Value,
            NativeBepInExConsoleVisible = BepInExConsoleHelper.IsCurrentConsoleWindowVisible(),
        };
    }

    private void UpdateLocalApiLogSettings(bool? exposeLogs, bool? diagnostics, bool? nativeConsole)
    {
        if (_config == null) return;
        if (exposeLogs.HasValue) _config.ExposeLocalApiLogs.Value = exposeLogs.Value;
        if (diagnostics.HasValue) _config.EnableNightBusinessDiagnostics.Value = diagnostics.Value;
        if (nativeConsole.HasValue && _log != null)
        {
            _config.DisableBepInExConsoleLog.Value = !nativeConsole.Value;
            _config.HideBepInExConsoleWindow.Value = !nativeConsole.Value;
            BepInExConsoleHelper.SetNativeConsoleEnabled(nativeConsole.Value, _log);
        }
    }

    private string OpenLocalApiLogFolder(string target)
    {
        var settings = GetLocalApiLogSettings();
        var path = target.ToLowerInvariant() switch
        {
            "diagnostics" => settings.NightBusinessDiagnosticsPath,
            "automation" => RuntimeOrderPreparationService.ResolveAutomationLogPath(),
            "packages" => Path.Combine(LocalApiServer.ResolveDiagnosticPackageDirectory(), "diagnostics.zip"),
            _ => settings.LogOutputPath,
        };
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Log directory is not available.");
        }

        Directory.CreateDirectory(directory);
        OpenDirectory(directory);
        return directory;
    }

    private RuntimeInventoryEditResult EditInventoryFromLocalApi(string itemType, int itemId, int quantity)
    {
        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
        {
            return ApplyInventoryEdit(itemType, itemId, quantity);
        }

        var pending = new PendingInventoryEdit
        {
            ItemType = itemType,
            ItemId = itemId,
            Quantity = quantity,
        };
        lock (_inventoryEditLock)
        {
            _pendingInventoryEdits.Enqueue(pending);
        }

        if (!pending.Completion.Wait(TimeSpan.FromSeconds(2.5)))
        {
            throw new TimeoutException("Inventory edit timed out waiting for Unity main thread.");
        }

        if (pending.Error != null) throw pending.Error;
        return pending.Result ?? throw new InvalidOperationException("Inventory edit did not produce a result.");
    }

    private RuntimeInventoryBulkEditResult EditInventoryBulkFromLocalApi(string itemType, IReadOnlyList<int> itemIds, int quantity)
    {
        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
        {
            return ApplyInventoryBulkEdit(itemType, itemIds, quantity);
        }

        var pending = new PendingInventoryBulkEdit
        {
            ItemType = itemType,
            ItemIds = itemIds.ToArray(),
            Quantity = quantity,
        };
        lock (_inventoryEditLock)
        {
            _pendingInventoryBulkEdits.Enqueue(pending);
        }

        if (!pending.Completion.Wait(TimeSpan.FromSeconds(6)))
        {
            throw new TimeoutException("Inventory bulk edit timed out waiting for Unity main thread.");
        }

        if (pending.Error != null) throw pending.Error;
        return pending.Result ?? throw new InvalidOperationException("Inventory bulk edit did not produce a result.");
    }

    private OrderPreparationResult PrepareOrderFromLocalApi(OrderPreparationRequest request)
    {
        return RunOrderActionFromLocalApi(request, OrderActionKind.PrepareRare);
    }

    private OrderPreparationResult CompleteOrderFromLocalApi(OrderPreparationRequest request)
    {
        return RunOrderActionFromLocalApi(request, OrderActionKind.CompleteRare);
    }

    private OrderPreparationResult CompleteNormalOrderFromLocalApi(OrderPreparationRequest request)
    {
        return RunOrderActionFromLocalApi(request, OrderActionKind.CompleteNormal);
    }

    private RareGuestInvitationResult InviteAllRareGuestsFromLocalApi(string scope)
    {
        return RunRareGuestInvitationFromLocalApi(RareGuestInvitationAction.InviteAll, scope: scope);
    }

    private RareGuestInvitationResult ListRareGuestInvitationsFromLocalApi(string scope)
    {
        return RunRareGuestInvitationFromLocalApi(RareGuestInvitationAction.List, scope: scope);
    }

    private RareGuestInvitationResult InviteRareGuestFromLocalApi(int guestId, string scope)
    {
        return RunRareGuestInvitationFromLocalApi(RareGuestInvitationAction.InviteOne, guestId, scope);
    }

    private RareGuestInvitationResult RunRareGuestInvitationFromLocalApi(RareGuestInvitationAction action, int guestId = -1, string scope = "")
    {
        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
        {
            return ApplyRareGuestInvitation(action, guestId, scope);
        }

        var pending = new PendingRareGuestInvitation
        {
            Action = action,
            GuestId = guestId,
            Scope = scope,
        };
        lock (_rareGuestInvitationLock)
        {
            _pendingRareGuestInvitations.Enqueue(pending);
        }

        if (!pending.Completion.Wait(TimeSpan.FromSeconds(3.5)))
        {
            throw new TimeoutException("Rare guest invitation timed out waiting for Unity main thread.");
        }

        if (pending.Error != null) throw pending.Error;
        return pending.Result ?? throw new InvalidOperationException("Rare guest invitation did not produce a result.");
    }

    private OrderPreparationResult RunOrderActionFromLocalApi(OrderPreparationRequest request, OrderActionKind action)
    {
        if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
        {
            var pending = new PendingOrderPreparation
            {
                Request = request,
                Action = action,
            };
            lock (_orderPreparationLock)
            {
                _pendingOrderPreparations.Enqueue(pending);
            }

            if (!pending.Completion.Wait(TimeSpan.FromSeconds(3.5)))
            {
                throw new TimeoutException("Order preparation timed out waiting for Unity main thread.");
            }

            if (pending.Error != null) throw pending.Error;
            return pending.Result ?? throw new InvalidOperationException("Order preparation did not produce a result.");
        }

        return action switch
        {
            OrderActionKind.PrepareRare => ApplyOrderPreparation(request),
            OrderActionKind.CompleteRare => ApplyOrderCompletion(request),
            OrderActionKind.CompleteNormal => ApplyNormalOrderCompletion(request),
            _ => ApplyOrderPreparation(request),
        };
    }

    private OrderPreparationResult ApplyOrderPreparation(OrderPreparationRequest request)
    {
        var result = RuntimeOrderPreparationService.Prepare(request);
        _status = result.Ok
            ? L("已准备下一笔稀客订单。", "Next rare-customer order prepared.")
            : L($"准备下一笔稀客订单未完成：{result.Error}", $"Preparing next rare-customer order did not finish: {result.Error}");
        PublishLocalApiSnapshot();
        return result;
    }

    private OrderPreparationResult ApplyOrderCompletion(OrderPreparationRequest request)
    {
        var result = RuntimeOrderPreparationService.CompleteFirst(request);
        _status = result.Ok
            ? L("已完成当前第一笔稀客订单。", "First rare-customer order completed.")
            : L($"完成当前第一笔稀客订单失败：{result.Error}", $"Completing first rare-customer order failed: {result.Error}");
        RefreshBusinessContext(false, force: true);
        PublishLocalApiSnapshot();
        return result;
    }

    private OrderPreparationResult ApplyNormalOrderCompletion(OrderPreparationRequest request)
    {
        var result = RuntimeOrderPreparationService.CompleteNormalFirst(request);
        _status = result.Ok
            ? L("已处理当前第一笔普客订单。", "First normal-customer order handled.")
            : L($"处理当前第一笔普客订单未完成：{result.Error}", $"Handling first normal-customer order did not finish: {result.Error}");
        RefreshBusinessContext(false, force: true);
        PublishLocalApiSnapshot();
        return result;
    }

    private void ProcessPendingOrderPreparations()
    {
        while (true)
        {
            PendingOrderPreparation? pending;
            lock (_orderPreparationLock)
            {
                pending = _pendingOrderPreparations.Count == 0 ? null : _pendingOrderPreparations.Dequeue();
            }

            if (pending == null) return;

            try
            {
                pending.Result = pending.Action switch
                {
                    OrderActionKind.PrepareRare => ApplyOrderPreparation(pending.Request),
                    OrderActionKind.CompleteRare => ApplyOrderCompletion(pending.Request),
                    OrderActionKind.CompleteNormal => ApplyNormalOrderCompletion(pending.Request),
                    _ => ApplyOrderPreparation(pending.Request),
                };
            }
            catch (Exception ex)
            {
                pending.Error = ex;
            }
            finally
            {
                pending.Completion.Set();
            }
        }
    }

    private void ProcessPendingRareGuestInvitations()
    {
        while (true)
        {
            PendingRareGuestInvitation? pending;
            lock (_rareGuestInvitationLock)
            {
                pending = _pendingRareGuestInvitations.Count == 0 ? null : _pendingRareGuestInvitations.Dequeue();
            }

            if (pending == null) return;

            try
            {
                pending.Result = ApplyRareGuestInvitation(pending.Action, pending.GuestId, pending.Scope);
            }
            catch (Exception ex)
            {
                pending.Error = ex;
            }
            finally
            {
                pending.Completion.Set();
            }
        }
    }

    private RareGuestInvitationResult ApplyRareGuestInvitation(RareGuestInvitationAction action, int guestId, string scope)
    {
        if (!CanRunRareGuestInvitationAction(out var waitReason))
        {
            return new RareGuestInvitationResult
            {
                Ok = false,
                RuntimeAvailable = false,
                Status = waitReason,
                Error = waitReason,
                Scope = string.IsNullOrWhiteSpace(scope) ? "current" : scope.Trim(),
            };
        }

        var result = action switch
        {
            RareGuestInvitationAction.List => RuntimeRareGuestInvitationService.ListAvailable(_repository, _log, scope),
            RareGuestInvitationAction.InviteOne => RuntimeRareGuestInvitationService.InviteOne(_repository, guestId, _log, scope),
            _ => RuntimeRareGuestInvitationService.InviteAllAvailable(_repository, _log, scope),
        };
        if (action != RareGuestInvitationAction.List)
        {
            _status = result.Ok
                ? result.Status
                : L($"邀请稀客失败：{result.Error ?? result.Status}", $"Rare guest invitation failed: {result.Error ?? result.Status}");
            PublishLocalApiSnapshot();
        }
        return result;
    }

    private bool CanRunRareGuestInvitationAction(out string reason)
    {
        reason = "";
        if (!_runtimeLoaded)
        {
            reason = "游戏运行时数据尚未读取完成，请稍后再试。";
            return false;
        }

        if (IsNonGameplayScene(_activeSceneName))
        {
            reason = "当前未进入存档，无法读取可邀请稀客。";
            return false;
        }

        if (IsNightBusinessScene(_activeSceneName))
        {
            reason = "当前处于夜间经营，稀客邀请只支持日间场景。";
            return false;
        }

        if (IsSceneSnapshotStartupGuardActive())
        {
            reason = "日间场景正在初始化，请稍后再试。";
            return false;
        }

        return true;
    }

    private void ProcessPendingCookingCollections()
    {
        if (Time.realtimeSinceStartup < _nextPendingCookingProcessAt) return;
        _nextPendingCookingProcessAt = Time.realtimeSinceStartup + PendingCookingProcessIntervalSeconds;

        var messages = Measure(
            "automation.collect",
            RuntimeOrderPreparationService.ProcessPendingCookingCollections);
        foreach (var message in messages)
        {
            _status = message;
            _log?.LogInfo(message);
            PublishLocalApiSnapshot();
        }
    }

    private void ProcessPendingInventoryEdits()
    {
        while (true)
        {
            PendingInventoryEdit? pending;
            lock (_inventoryEditLock)
            {
                pending = _pendingInventoryEdits.Count == 0 ? null : _pendingInventoryEdits.Dequeue();
            }

            if (pending == null) return;

            try
            {
                pending.Result = ApplyInventoryEdit(pending.ItemType, pending.ItemId, pending.Quantity);
            }
            catch (Exception ex)
            {
                pending.Error = ex;
            }
            finally
            {
                pending.Completion.Set();
            }
        }
    }

    private void ProcessPendingInventoryBulkEdits()
    {
        while (true)
        {
            PendingInventoryBulkEdit? pending;
            lock (_inventoryEditLock)
            {
                pending = _pendingInventoryBulkEdits.Count == 0 ? null : _pendingInventoryBulkEdits.Dequeue();
            }

            if (pending == null) return;

            try
            {
                pending.Result = ApplyInventoryBulkEdit(pending.ItemType, pending.ItemIds, pending.Quantity);
            }
            catch (Exception ex)
            {
                pending.Error = ex;
            }
            finally
            {
                pending.Completion.Set();
            }
        }
    }

    private RuntimeInventoryEditResult ApplyInventoryEdit(string itemType, int itemId, int quantity)
    {
        var result = RuntimeInventoryEditor.SetQuantity(itemType, itemId, quantity);
        _status = result.Error == null
            ? L(
                $"库存已修改：{result.ItemType} #{result.ItemId} {result.PreviousQuantity} -> {result.Quantity}",
                $"Inventory changed: {result.ItemType} #{result.ItemId} {result.PreviousQuantity} -> {result.Quantity}")
            : L($"库存修改失败：{result.Error}", $"Inventory edit failed: {result.Error}");
        RefreshRuntimeState(false);
        RefreshBusinessContext(false);
        PublishLocalApiSnapshot();
        return result;
    }

    private RuntimeInventoryBulkEditResult ApplyInventoryBulkEdit(string itemType, IReadOnlyList<int> itemIds, int quantity)
    {
        var normalizedType = itemType;
        var changed = 0;
        var unchanged = 0;
        var failed = 0;
        var errors = new List<string>();

        foreach (var itemId in itemIds.Where(id => id >= 0).Distinct().OrderBy(id => id))
        {
            try
            {
                var result = RuntimeInventoryEditor.SetQuantity(itemType, itemId, quantity);
                normalizedType = result.ItemType;
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    failed++;
                    if (errors.Count < 8) errors.Add($"#{itemId}: {result.Error}");
                    continue;
                }

                if (result.Changed) changed++;
                else unchanged++;
            }
            catch (Exception ex)
            {
                failed++;
                if (errors.Count < 8) errors.Add($"#{itemId}: {ex.Message}");
            }
        }

        var total = changed + unchanged + failed;
        _status = L(
            $"批量库存修改：{normalizedType} 共 {total} 项，变更 {changed}，未变 {unchanged}，失败 {failed}",
            $"Inventory bulk edit: {normalizedType} total {total}, changed {changed}, unchanged {unchanged}, failed {failed}");
        RefreshRuntimeState(false);
        RefreshBusinessContext(false);
        PublishLocalApiSnapshot();

        return new RuntimeInventoryBulkEditResult
        {
            ItemType = normalizedType,
            RequestedQuantity = Math.Clamp(quantity, 0, 9999),
            Total = total,
            Changed = changed,
            Unchanged = unchanged,
            Failed = failed,
            Errors = errors,
        };
    }

    private void ClearLoadedRuntime(string status)
    {
        _state = null;
        _runtimeLoaded = false;
        _businessContext = new NightBusinessContext
        {
            Error = status,
        };
        _normalBusinessContext = null;
        _nextNormalBusinessRefreshAt = 0f;
        _runtimeSource = "";
        _runtimeStateSignature = "";
        _lastRuntimeReadUtc = DateTime.MinValue;
        SpecialOrderRuntimeCapture.ClearOrders("runtime cleared");
        RuntimeCookerHighlightService.Clear();
        _status = status;
    }

    private void ClearPlacedCookersFromCurrentState(string status)
    {
        if (_state == null) return;
        if (_state.PlacedCookers.Count == 0
            && _state.PlacedCookerTypeIds.Count == 0
            && string.Equals(_state.PlacedCookerStatus, status, StringComparison.Ordinal))
        {
            return;
        }

        _state.PlacedCookers.Clear();
        _state.PlacedCookerTypeIds.Clear();
        _state.PlacedCookerStatus = status;
        _runtimeStateSignature = BuildRecommendationStateSignature(_state);
        _businessFallbackState = null;
    }

    private static bool HasActiveNightBusinessContext(NightBusinessContext? context)
    {
        if (context == null) return false;
        var status = $"{context.Source}; {context.Error}";
        if (string.IsNullOrWhiteSpace(context.Source) && !string.IsNullOrWhiteSpace(context.Error)) return false;
        if (status.Contains("not in", StringComparison.OrdinalIgnoreCase)
            || status.Contains("not loaded", StringComparison.OrdinalIgnoreCase)
            || status.Contains("non-game", StringComparison.OrdinalIgnoreCase)
            || status.Contains("No active business scene", StringComparison.OrdinalIgnoreCase)
            || status.Contains("不在", StringComparison.OrdinalIgnoreCase)
            || status.Contains("未加载", StringComparison.OrdinalIgnoreCase)
            || status.Contains("当前无经营场景", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private RecommendationState GetBusinessRecommendationState()
    {
        if (_state != null) return _state;
        if (_repository == null) return new RecommendationState();

        if (_businessFallbackState == null)
        {
            _businessFallbackState = RecommendationState.AllAvailable(_repository);
            ApplyConfigOverrides(_businessFallbackState);
        }

        return _businessFallbackState;
    }

    private static string BuildRecommendationStateSignature(RecommendationState state)
    {
        unchecked
        {
            var hash = 17;
            hash = HashIds(hash, state.AvailableRecipeIds);
            hash = HashIds(hash, state.AvailableBeverageIds);
            hash = HashIds(hash, state.AvailableIngredientIds);
            hash = HashIds(hash, state.AvailableRareCustomerIds);
            foreach (var item in state.OwnedIngredientQty.OrderBy(item => item.Key))
            {
                hash = (hash * 31) + item.Key;
                hash = (hash * 31) + item.Value;
            }
            foreach (var item in state.OwnedBeverageQty.OrderBy(item => item.Key))
            {
                hash = (hash * 31) + item.Key;
                hash = (hash * 31) + item.Value;
            }

            hash = HashIds(hash, state.PlacedCookerTypeIds);
            foreach (var cooker in state.PlacedCookers.OrderBy(cooker => cooker.ControllerIndex))
            {
                hash = (hash * 31) + cooker.ControllerIndex;
                hash = HashIds(hash, cooker.TypeIds);
                hash = (hash * 31) + cooker.Source.GetHashCode();
            }

            hash = (hash * 31) + state.PlacedCookerStatus.GetHashCode();
            hash = (hash * 31) + (state.PopularFoodTag?.GetHashCode() ?? 0);
            hash = (hash * 31) + (state.PopularHateFoodTag?.GetHashCode() ?? 0);
            hash = (hash * 31) + (state.FamousShopEnabled ? 1 : 0);
            return hash.ToString("X8");
        }
    }

    private static int HashIds(int seed, IEnumerable<int> values)
    {
        unchecked
        {
            var hash = seed;
            foreach (var value in values.OrderBy(value => value))
            {
                hash = (hash * 31) + value;
            }

            return hash;
        }
    }

    private void LogRuntimeError(Exception ex, bool manual)
    {
        var message = ex.Message;
        if (!manual && string.Equals(_lastRuntimeErrorMessage, message, StringComparison.Ordinal)) return;

        _lastRuntimeErrorMessage = message;
        _log?.LogError(ex);
    }

    private void ApplyConfigOverrides(RecommendationState state)
    {
        if (_config == null) return;
        if (!string.IsNullOrWhiteSpace(_config.PopularFoodTagOverride.Value))
        {
            state.PopularFoodTag = _config.PopularFoodTagOverride.Value.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_config.PopularHateFoodTagOverride.Value))
        {
            state.PopularHateFoodTag = _config.PopularHateFoodTagOverride.Value.Trim();
        }

        if (_config.FamousShopOverride.Value)
        {
            state.FamousShopEnabled = true;
        }
    }

    private static string EnsureLocalApiToken(StewardPluginConfig config)
    {
        var token = config.LocalApiToken.Value.Trim();
        if (IsUsableLocalApiToken(token)) return token;

        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        config.LocalApiToken.Value = token;
        return token;
    }

    private static bool IsUsableLocalApiToken(string token)
    {
        return token.Length >= 32 && token.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));
    }

    private static void OpenDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
            return;
        }

        var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        Process.Start(new ProcessStartInfo
        {
            FileName = opener,
            ArgumentList = { directory },
            UseShellExecute = false,
        });
    }

    private bool IsNonGameplayScene(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName)) return false;
        if (sceneName.Contains("entry", StringComparison.OrdinalIgnoreCase)
            || sceneName.Contains("title", StringComparison.OrdinalIgnoreCase)
            || sceneName.Contains("menu", StringComparison.OrdinalIgnoreCase)
            || sceneName.Contains("loading", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_config == null) return false;
        return ContainsConfiguredKeyword(sceneName, _config.NonGameplaySceneKeywords.Value);
    }

    private static bool IsNightBusinessScene(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName)) return false;

        var normalized = sceneName.Trim();
        return string.Equals(normalized, "Work", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "WorkScene", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("WorkScene", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetActiveSceneName()
    {
        try
        {
            return SceneManager.GetActiveScene().name ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool ContainsConfiguredKeyword(string value, string keywords)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(keywords)) return false;

        foreach (var keyword in keywords.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = keyword.Trim();
            if (normalized.Length == 0) continue;
            if (value.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }

        return false;
    }

    private string FormatSourceDescription(string source)
    {
        return source == "Game runtime live data"
            ? L("游戏实时运行时数据", "Game runtime live data")
            : source;
    }

    private static string RuntimeReasonZh(string reason)
    {
        return reason switch
        {
            "RunTimeStorage type is not loaded." => "RunTimeStorage 类型尚未加载。",
            "RunTimePlayerData type is not loaded." => "RunTimePlayerData 类型尚未加载。",
            "RunTimeStorage live-data methods are not available." => "RunTimeStorage 实时数据方法不可用。",
            "RunTimePlayerData live-data methods are not available." => "RunTimePlayerData 实时数据方法不可用。",
            "Game runtime data is empty; game progress may not be loaded." => "游戏运行时基础数据为空，可能处于夜间经营或进度尚未完全加载。",
            _ => reason,
        };
    }

    private static string L(string zh, string en)
    {
        return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? zh : en;
    }
}
