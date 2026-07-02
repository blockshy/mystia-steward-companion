using BepInEx.Logging;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Plugin;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Updates;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MystiaStewardCompanion.Ui;

/// <summary>
/// Mod 运行时主控制器，负责在 Unity 主线程中刷新游戏数据、处理本地 API 请求并发布伴随窗口快照。
/// </summary>
/// <remarks>
/// 本地 API 的 HTTP 处理运行在后台线程，但 IL2CPP/Unity 对象只能安全地在 Unity 主线程访问。
/// 因此涉及库存、订单、稀客邀请和自动化的请求都会先进入队列，再由 <see cref="Update"/> 消费执行。
/// </remarks>
internal sealed class StewardOverlayController
{
    // 稀客订单 Hook 可能在同一帧连续触发多次，短暂防抖可减少重复读取夜间经营对象。
    private const float SpecialOrderRefreshDebounceSeconds = 0.2f;
    // 本地 API 快照序列化成本较高，限制最短发布时间避免每帧刷新造成卡顿。
    private const float LocalApiSnapshotPublishMinIntervalSeconds = 0.35f;
    // 结构化运行时目录体积较大，只在内容变化、强制刷新或间隔到达时完整发布。
    private const float RuntimeDataFullPublishIntervalSeconds = 10f;
    private const float RuntimeDataCatalogRetrySeconds = 5f;
    private const float PerformanceSnapshotMaxAgeSeconds = 12f;
    private const float PendingCookingProcessIntervalSeconds = 0.5f;
    private const float NormalBusinessSnapshotCacheSeconds = 1f;
    private const float StableBusinessContextRescanSeconds = 5f;
    private static readonly JsonSerializerOptions LocalApiJsonOptions = new(JsonSerializerDefaults.Web);

    private StewardPluginConfig? _config;
    private ManualLogSource? _log;
    private DataRepository? _repository;
    private RuntimeDataCatalog _runtimeDataCatalog = RuntimeDataCatalog.Empty("not loaded");
    private RuntimeMappedGuestCatalogSnapshot? _runtimeMappedGuestSnapshot;
    private RuntimeStaticDataSnapshot? _runtimeStaticDataSnapshot;
    private RecommendationState? _state;
    private RecommendationState? _businessFallbackState;
    private NightBusinessContext? _businessContext;
    private NormalBusinessContext? _normalBusinessContext;
    private LocalApiServer? _localApiServer;
    private readonly object _inventoryEditLock = new();
    // 后台 HTTP 线程提交的库存编辑请求，统一在 Unity 主线程处理。
    private readonly Queue<PendingInventoryEdit> _pendingInventoryEdits = new();
    private readonly Queue<PendingInventoryBulkEdit> _pendingInventoryBulkEdits = new();
    private readonly object _orderPreparationLock = new();
    // 订单准备会读写运行时订单、厨具和库存，必须串行回到主线程执行。
    private readonly Queue<PendingOrderPreparation> _pendingOrderPreparations = new();
    private readonly object _rareGuestInvitationLock = new();
    // 稀客邀请涉及日间场景候选和角色状态，同样不允许在 API 后台线程直接执行。
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
    private float _nextRuntimeDataCatalogRefreshAt;
    private float _nextPendingCookingProcessAt;
    private float _nextNormalBusinessRefreshAt;
    private float _nextStableBusinessContextRescanAt;
    private long _businessContextSpecialOrderVersion = long.MinValue;
    private string _businessContextSceneName = "";
    private long _normalBusinessContextCaptureVersion = long.MinValue;
    private bool _localApiSnapshotErrorLogged;
    private bool _disposed;
    private bool _controllerToggleLatched;
    private bool _specialOrderRefreshPending;
    private bool _normalOrderRefreshPending;
    private bool _localApiSnapshotPublishPending;
    private float _nextControllerToggleAt;
    private float _nextSpecialOrderRefreshAt;
    private float _nextNormalOrderCaptureRefreshAt;
    private long _lastRuntimeSceneReadinessVersion;
    private long _lastNormalOrderChangeVersion;
    private string _lastPublishedRuntimeDataSignature = "";
    private string _lastLocalApiSnapshotContentSignature = "";
    private readonly List<RuntimeRareCustomer> _runtimeRareCustomers = new();
    private readonly Dictionary<string, double> _performanceMs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _performanceUpdatedAt = new(StringComparer.Ordinal);

    /// <summary>
    /// 后台 API 线程提交的一次单项库存编辑请求。
    /// </summary>
    private sealed class PendingInventoryEdit
    {
        public string ItemType { get; init; } = "";
        public int ItemId { get; init; }
        public int Quantity { get; init; }
        public ManualResetEventSlim Completion { get; } = new(false);
        public RuntimeInventoryEditResult? Result { get; set; }
        public Exception? Error { get; set; }
    }

    /// <summary>
    /// 后台 API 线程提交的一次批量库存编辑请求。
    /// </summary>
    private sealed class PendingInventoryBulkEdit
    {
        public string ItemType { get; init; } = "";
        public IReadOnlyList<int> ItemIds { get; init; } = Array.Empty<int>();
        public int Quantity { get; init; }
        public ManualResetEventSlim Completion { get; } = new(false);
        public RuntimeInventoryBulkEditResult? Result { get; set; }
        public Exception? Error { get; set; }
    }

    /// <summary>
    /// 后台 API 线程提交的一次订单自动化请求。
    /// </summary>
    /// <remarks>
    /// Completion 用于让 HTTP 处理线程等待主线程执行完成并返回结构化结果。
    /// </remarks>
    private sealed class PendingOrderPreparation
    {
        public OrderPreparationRequest Request { get; init; } = new();
        public OrderActionKind Action { get; init; }
        public ManualResetEventSlim Completion { get; } = new(false);
        public OrderPreparationResult? Result { get; set; }
        public Exception? Error { get; set; }
    }

    /// <summary>
    /// 后台 API 线程提交的一次稀客邀请或邀请列表读取请求。
    /// </summary>
    private sealed class PendingRareGuestInvitation
    {
        public RareGuestInvitationAction Action { get; init; }
        public int GuestId { get; init; } = -1;
        public string Scope { get; init; } = "";
        public string KizunaLevels { get; init; } = "";
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

    /// <summary>
    /// 初始化主控制器、运行时数据源和本地 API。
    /// </summary>
    /// <remarks>
    /// 该方法在插件启动时由 Unity 主线程调用。它只加载本地数据仓库的空壳，实际静态目录和存档状态随后从游戏运行时读取。
    /// </remarks>
    public void Initialize(StewardPluginConfig config, ManualLogSource log)
    {
        _config = config;
        _log = log;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _activeSceneName = GetActiveSceneName();
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

    /// <summary>
    /// Unity 每帧更新入口。
    /// </summary>
    /// <remarks>
    /// 执行顺序很重要：先处理场景变化和待办队列，再刷新订单/运行时快照，最后按节流策略发布到本地 API。
    /// </remarks>
    public void Update()
    {
        if (_disposed || _config == null) return;
        RefreshOnSceneChange();
        RefreshOnRuntimeSceneReadinessChange();
        ProcessPendingInventoryEdits();
        ProcessPendingInventoryBulkEdits();
        ProcessPendingOrderPreparations();
        ProcessPendingRareGuestInvitations();
        ProcessPendingCookingCollections();
        RefreshBusinessContextOnOrderCaptureChange();

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

    /// <summary>
    /// 在订单 Hook 版本变化后防抖刷新对应经营上下文。
    /// </summary>
    private void RefreshBusinessContextOnOrderCaptureChange()
    {
        if (_config == null || !_config.AutoRefreshRuntime.Value) return;

        var specialVersion = SpecialOrderRuntimeCapture.ChangeVersion;
        if (specialVersion != _lastSpecialOrderChangeVersion)
        {
            _lastSpecialOrderChangeVersion = specialVersion;
            _specialOrderRefreshPending = true;
            _nextSpecialOrderRefreshAt = Time.realtimeSinceStartup + SpecialOrderRefreshDebounceSeconds;
        }

        var normalVersion = NormalOrderRuntimeCapture.ChangeVersion;
        if (normalVersion != _lastNormalOrderChangeVersion)
        {
            _lastNormalOrderChangeVersion = normalVersion;
            _normalOrderRefreshPending = true;
            _nextNormalOrderCaptureRefreshAt = Time.realtimeSinceStartup + SpecialOrderRefreshDebounceSeconds;
        }

        var now = Time.realtimeSinceStartup;
        if (_specialOrderRefreshPending && now >= _nextSpecialOrderRefreshAt)
        {
            _specialOrderRefreshPending = false;
            RefreshBusinessContext(false, force: true);
            return;
        }

        if (_normalOrderRefreshPending && now >= _nextNormalOrderCaptureRefreshAt)
        {
            _normalOrderRefreshPending = false;
            RefreshNormalBusinessContext(force: true);
            PublishLocalApiSnapshot();
        }
    }

    /// <summary>
    /// 处理游戏场景切换导致的运行时状态失效。
    /// </summary>
    /// <remarks>
    /// 不同场景可读取的数据范围不同。离开经营或进入准备界面时需要清理夜间对象，避免伴随窗口继续使用过期引用。
    /// </remarks>
    private void RefreshOnSceneChange()
    {
        if (_config == null) return;

        var sceneName = GetActiveSceneName();
        if (string.Equals(sceneName, _activeSceneName, StringComparison.Ordinal)) return;

        _activeSceneName = sceneName;
        if (IsNonGameplayScene(sceneName) || IsNightBusinessScene(sceneName))
        {
            RuntimeSceneReadinessCapture.ClearForSceneChange(sceneName);
        }

        _nextAutoRefreshAt = 0f;
        _nextBusinessRefreshAt = 0f;
        ResetRuntimeRetryDelays();
        _lastPublishedRuntimeDataSignature = "";
        _businessFallbackState = null;
        RuntimeMissionSnapshotService.ClearCache();
        _normalBusinessContext = null;
        _nextNormalBusinessRefreshAt = 0f;
        ResetNightBusinessRefreshCache();
        ResetNormalBusinessRefreshCache();

        if (IsIzakayaPrepActive(sceneName))
        {
            ClearNightBusinessRuntime(L(
                "经营准备界面正在初始化；暂不读取夜间经营对象。",
                "Izakaya prep is initializing; night-business objects are not read yet."));
            PublishLocalApiSnapshot();
            return;
        }

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

    /// <summary>
    /// 响应运行时场景就绪探针变化，强制下一轮重新读取目录和快照。
    /// </summary>
    private void RefreshOnRuntimeSceneReadinessChange()
    {
        var version = RuntimeSceneReadinessCapture.ChangeVersion;
        if (version == _lastRuntimeSceneReadinessVersion) return;

        _lastRuntimeSceneReadinessVersion = version;
        _nextAutoRefreshAt = 0f;
        ResetRuntimeRetryDelays();
        _lastPublishedRuntimeDataSignature = "";
        RuntimeMissionSnapshotService.ClearCache();
        _localApiSnapshotPublishPending = true;
    }

    private void ResetRuntimeRetryDelays()
    {
        _nextRuntimeDataCatalogRefreshAt = 0f;
        RuntimeMappedGuestCatalog.ResetRetryDelay();
        RuntimeStaticDataCatalog.ResetRetryDelay();
        SpecialOrderRuntimeCapture.ResetAttachRetryDelay();
        NormalOrderRuntimeCapture.ResetAttachRetryDelay();
    }

    /// <summary>
    /// 检测键盘或手柄快捷键是否触发伴随窗口显隐。
    /// </summary>
    /// <remarks>
    /// 手柄右摇杆按下在新旧输入系统中的表现不同，因此额外通过 InputSystem 反射读取 pressed/held 状态。
    /// </remarks>
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

    /// <summary>
    /// Unity LateUpdate 入口，用于厨具高亮等需要等待普通 Update 后再执行的视觉效果。
    /// </summary>
    public void LateUpdate()
    {
        RuntimeCookerHighlightService.Tick();
    }

    /// <summary>
    /// 停止本地 API、通知伴随窗口退出并清理运行时视觉状态。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        CompanionProcessLauncher.TryNotifyExit();
        _localApiServer?.Dispose();
        _localApiServer = null;
        RuntimeCookerHighlightService.Clear();
    }

    /// <summary>
    /// 初始化运行时数据仓库状态。
    /// </summary>
    /// <remarks>
    /// 当前版本静态数据优先来自游戏运行时目录，因此这里不会立即读取仓库内 JSON 数据，
    /// 而是进入“等待实时数据”的状态。
    /// </remarks>
    private void LoadRepository()
    {
        try
        {
            _repository = DataRepository.Empty();
            _runtimeDataCatalog = RuntimeDataCatalog.Empty("waiting for live game runtime data");
            _runtimeMappedGuestSnapshot = null;
            _runtimeStaticDataSnapshot = null;
            _state = null;
            _businessFallbackState = null;
            _runtimeLoaded = false;
            _businessContext = null;
            _normalBusinessContext = null;
            ResetNightBusinessRefreshCache();
            ResetNormalBusinessRefreshCache();
            _runtimeSource = "";
            _lastRuntimeErrorMessage = "";
            _runtimeStateSignature = "";
            _lastRuntimeReadUtc = DateTime.MinValue;
            _runtimeRareCustomers.Clear();
            _status = L(
                "等待游戏运行时数据；当前页面需要运行时数据就绪。",
                "Waiting for live game runtime data; this page requires runtime data to be ready.");
        }
        catch (Exception ex)
        {
            _status = L($"数据加载失败：{ex.Message}", $"Failed to load data: {ex.Message}");
            _log?.LogError(ex);
        }
    }

    /// <summary>
    /// 刷新推荐系统所需的游戏运行时基础状态。
    /// </summary>
    /// <param name="manual">是否由用户手动触发刷新，用于决定状态提示和日志强度。</param>
    /// <remarks>
    /// 本方法读取库存、解锁料理、流行 Tag、厨具和日间状态，并将配置覆盖项应用到推荐状态。
    /// 读取失败时不会清空已加载状态，除非当前场景明确不再允许使用旧运行时数据。
    /// </remarks>
    private void RefreshRuntimeState(bool manual)
    {
        if (_repository == null || _config == null) return;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            _activeSceneName = GetActiveSceneName();
            if (!CanReadRuntimeStateInCurrentScene(_activeSceneName, out var sceneWaitReason))
            {
                if (IsNonGameplayScene(_activeSceneName))
                {
                    ClearLoadedRuntime(sceneWaitReason);
                }
                else if (IsIzakayaPrepActive(_activeSceneName) || IsNightBusinessScene(_activeSceneName))
                {
                    ClearNightBusinessRuntime(sceneWaitReason);
                }
                else
                {
                    _status = sceneWaitReason;
                }

                return;
            }

            if (RuntimeReflectionRecommendationStateProvider.CanReadRuntimeState(out var runtimeReason))
            {
                TryRefreshRuntimeDataCatalog();

                var includePlacedCookers = !IsIzakayaPrepActive(_activeSceneName) && HasActiveNightBusinessContext(_businessContext);
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

    /// <summary>
    /// 刷新夜晚经营上下文，包括稀客订单和普客订单。
    /// </summary>
    /// <param name="manual">是否由用户手动触发。</param>
    /// <param name="force">是否跳过缓存时间立即读取。</param>
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

            if (IsIzakayaPrepActive(_activeSceneName))
            {
                ClearNightBusinessRuntime(L(
                    "经营准备界面正在初始化；当前无经营场景。",
                    "Izakaya prep is initializing; no active business scene."));
                return;
            }

            if (!IsNightBusinessScene(_activeSceneName))
            {
                ClearNightBusinessRuntime(L("当前不在夜晚经营场景。", "Not in a night business scene."));
                if (manual) _status = L("当前无经营场景。", "No active business scene.");
                return;
            }

            var diagnostics = CreateNightBusinessDiagnostics();
            var specialOrderVersion = SpecialOrderRuntimeCapture.ChangeVersion;
            if (CanReuseNightBusinessContext(manual, force, diagnostics != null, specialOrderVersion))
            {
                return;
            }

            var provider = new NightBusinessReflectionProvider(
                _repository,
                diagnostics,
                _activeSceneName,
                _runtimeMappedGuestSnapshot,
                _runtimeStaticDataSnapshot);
            _businessContext = Measure("business.rare.total", provider.LoadContext);
            _businessContextSpecialOrderVersion = specialOrderVersion;
            _businessContextSceneName = _activeSceneName;
            _nextStableBusinessContextRescanAt = Time.realtimeSinceStartup + StableBusinessContextRescanSeconds;
            RecordPerformanceEntries("business.rare.", provider.PerformanceMs);
            RefreshNormalBusinessContext(force: manual);
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

    private bool CanReuseNightBusinessContext(
        bool manual,
        bool force,
        bool diagnosticsEnabled,
        long specialOrderVersion)
    {
        if (manual || force || diagnosticsEnabled) return false;
        if (_businessContext == null || !HasActiveNightBusinessContext(_businessContext)) return false;
        if (!string.Equals(_businessContextSceneName, _activeSceneName, StringComparison.Ordinal)) return false;
        if (_businessContextSpecialOrderVersion != specialOrderVersion) return false;
        return Time.realtimeSinceStartup < _nextStableBusinessContextRescanAt;
    }

    /// <summary>
    /// 启动本地 API，并把需要回到主线程执行的动作注入服务器。
    /// </summary>
    private void StartLocalApi()
    {
        if (_config == null || _log == null || !_config.LocalApiEnabled.Value) return;

        try
        {
            var updateService = new UpdateService(
                new UpdateServiceSettings
                {
                    Enabled = _config.UpdatesEnabled.Value,
                    AutoCheck = _config.UpdatesAutoCheck.Value,
                    CheckIntervalHours = _config.UpdatesCheckIntervalHours.Value,
                    IncludePrerelease = _config.UpdatesIncludePrerelease.Value,
                },
                _log);
            var favoriteStore = new FavoriteStore(FavoriteStore.ResolvePath(), _log);
            _localApiServer = new LocalApiServer(
                _config.LocalApiLanEnabled.Value,
                _config.LocalApiLanHost.Value,
                _config.LocalApiPort.Value,
                MystiaStewardCompanionPlugin.PluginVersion,
                _localApiToken,
                GetLocalApiLogSettings,
                UpdateLocalApiLogSettings,
                GetLocalApiConnectionConfig,
                UpdateLocalApiConnectionConfig,
                RegenerateLocalApiToken,
                OpenLocalApiLogFolder,
                EditInventoryFromLocalApi,
                EditInventoryBulkFromLocalApi,
                PrepareOrderFromLocalApi,
                CompleteOrderFromLocalApi,
                CompleteNormalOrderFromLocalApi,
                ListRareGuestInvitationsFromLocalApi,
                InviteAllRareGuestsFromLocalApi,
                InviteRareGuestFromLocalApi,
                updateService,
                favoriteStore,
                new CustomRecipeStore(CustomRecipeStore.ResolvePath(), favoriteStore, _log),
                _log);
            _localApiServer.Start();
            updateService.StartAutoCheck();
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

    /// <summary>
    /// 构建并发布本地 API 快照。
    /// </summary>
    /// <param name="force">是否忽略节流和内容签名，强制写入最新快照。</param>
    /// <remarks>
    /// 快照是伴随窗口的主要数据来源。为降低经营中帧耗，本方法会做节流、内容签名去重和大体积运行时目录的间隔发布。
    /// </remarks>
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
            var runtimeBasicsLoaded = HasRuntimeBasicsLoaded();
            var publishedState = CanPublishRecommendationState()
                ? _state ?? (_businessContext?.Orders.Count > 0 ? GetBusinessRecommendationState() : null)
                : null;
            var dayMap = ReadActiveDayMapForSnapshot();
            var snapshot = new LocalApiSnapshot
            {
                PluginVersion = MystiaStewardCompanionPlugin.PluginVersion,
                CapturedAtUtc = DateTime.UtcNow,
                ActiveSceneName = _activeSceneName,
                ActiveDayMapLabel = dayMap.Label,
                ActiveDayMapName = dayMap.Name,
                RuntimeLoaded = runtimeBasicsLoaded,
                Status = _status,
                RuntimeSource = _runtimeSource,
                RuntimeSceneReadinessStatus = RuntimeSceneReadinessCapture.Status,
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

            var contentSignature = BuildLocalApiSnapshotContentSignature(snapshot);
            if (!force && string.Equals(contentSignature, _lastLocalApiSnapshotContentSignature, StringComparison.Ordinal))
            {
                _localApiSnapshotErrorLogged = false;
                return;
            }

            _localApiServer.SetSnapshotJson(Measure("snapshot.serialize", () => JsonSerializer.Serialize(snapshot, LocalApiJsonOptions)));
            _lastLocalApiSnapshotContentSignature = contentSignature;
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

    /// <summary>
    /// 在快照中读取当前日间地图信息。
    /// </summary>
    /// <remarks>
    /// 日间 UI 尚未就绪时不读取运行时对象，避免进入地图切换或准备面板期间读到半初始化状态。
    /// </remarks>
    private (string Label, string Name) ReadActiveDayMapForSnapshot()
    {
        if (!HasRuntimeBasicsLoaded()) return ("", "");
        if (!IsDaySceneRuntimeScene(_activeSceneName)) return ("", "");
        if (ShouldBlockDaySceneRuntimeReads(_activeSceneName)) return ("", "");
        if (!IsDayScenePanelReady()) return ("", "");

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

    /// <summary>
    /// 根据运行时目录体积和签名决定本轮是否完整发布目录。
    /// </summary>
    /// <returns>需要随快照发布的目录；本轮可复用前端缓存时返回 <c>null</c>。</returns>
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

    /// <summary>
    /// 生成本地 API 快照内容签名。
    /// </summary>
    /// <remarks>
    /// 签名只包含会影响 UI 展示的字段，不包含捕获时间和性能指标，从而避免无意义的快照重复写入。
    /// </remarks>
    private static string BuildLocalApiSnapshotContentSignature(LocalApiSnapshot snapshot)
    {
        var builder = new StringBuilder(2048);
        AppendValue(builder, snapshot.PluginVersion);
        AppendValue(builder, snapshot.ActiveSceneName);
        AppendValue(builder, snapshot.ActiveDayMapLabel);
        AppendValue(builder, snapshot.ActiveDayMapName);
        AppendValue(builder, snapshot.RuntimeLoaded);
        AppendValue(builder, snapshot.Status);
        AppendValue(builder, snapshot.RuntimeSource);
        AppendValue(builder, snapshot.RuntimeSceneReadinessStatus);
        AppendValue(builder, snapshot.RuntimeUiPinningStatus);
        AppendRecommendationSnapshot(builder, snapshot.RecommendationState);
        AppendNightBusiness(builder, snapshot.NightBusiness);
        AppendRuntimeMissions(builder, snapshot.RuntimeMissions);
        AppendNormalBusiness(builder, snapshot.NormalBusiness);
        AppendRuntimeRareCustomers(builder, snapshot.RuntimeRareCustomers);
        AppendValue(builder, snapshot.RuntimeData == null ? "<runtime-data:null>" : BuildRuntimeDataSignature(snapshot.RuntimeData));
        return builder.ToString();
    }

    private static void AppendRecommendationSnapshot(StringBuilder builder, RecommendationStateSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            AppendValue(builder, "<recommendation:null>");
            return;
        }

        AppendInts(builder, snapshot.AvailableRecipeIds);
        AppendInts(builder, snapshot.AvailableBeverageIds);
        AppendInts(builder, snapshot.AvailableIngredientIds);
        AppendIntDictionary(builder, snapshot.OwnedIngredientQty);
        AppendIntDictionary(builder, snapshot.OwnedBeverageQty);
        AppendInts(builder, snapshot.PlacedCookerTypeIds);
        foreach (var cooker in snapshot.PlacedCookers.OrderBy(cooker => cooker.ControllerIndex))
        {
            AppendValue(builder, cooker.ControllerIndex);
            AppendInts(builder, cooker.TypeIds);
            AppendStrings(builder, cooker.TypeNames);
            AppendValue(builder, cooker.Name);
            AppendValue(builder, cooker.IsOpen);
            AppendValue(builder, cooker.Source);
        }

        AppendValue(builder, snapshot.PlacedCookerStatus);
        AppendValue(builder, snapshot.PopularFoodTag);
        AppendValue(builder, snapshot.PopularHateFoodTag);
        AppendValue(builder, snapshot.FamousShopEnabled);
    }

    private static void AppendNightBusiness(StringBuilder builder, NightBusinessContext? context)
    {
        if (context == null)
        {
            AppendValue(builder, "<night-business:null>");
            return;
        }

        AppendValue(builder, context.Place);
        AppendValue(builder, context.PlaceLabel);
        AppendValue(builder, context.Source);
        AppendValue(builder, context.Error);
        foreach (var guest in context.ActiveRareGuests
                     .OrderBy(guest => guest.DeskCode)
                     .ThenBy(guest => guest.GuestId ?? -1)
                     .ThenBy(guest => guest.GuestName, StringComparer.Ordinal))
        {
            AppendValue(builder, guest.DeskCode);
            AppendValue(builder, guest.GuestId);
            AppendValue(builder, guest.GuestName);
            AppendValue(builder, guest.Source);
            AppendValue(builder, guest.Fund);
            AppendValue(builder, guest.BaseFundCarry);
            AppendValue(builder, guest.MaxFundCarry);
            AppendValue(builder, guest.ExtraFundByBuff);
            AppendValue(builder, guest.WillPayMoney);
        }

        foreach (var order in context.Orders
                     .OrderBy(order => order.DeskCode)
                     .ThenBy(order => order.GuestId ?? -1)
                     .ThenBy(order => order.FoodTagId)
                     .ThenBy(order => order.BeverageTagId)
                     .ThenBy(order => order.Source, StringComparer.Ordinal))
        {
            AppendValue(builder, order.DeskCode);
            AppendValue(builder, order.GuestId);
            AppendValue(builder, order.GuestName);
            AppendValue(builder, order.FoodTagId);
            AppendValue(builder, order.FoodTag);
            AppendValue(builder, order.BeverageTagId);
            AppendValue(builder, order.BeverageTag);
            AppendValue(builder, order.Source);
            AppendValue(builder, order.IsFreeOrder);
            AppendValue(builder, order.HasServedFood);
            AppendValue(builder, order.HasServedBeverage);
        }
    }

    private static void AppendRuntimeMissions(StringBuilder builder, RuntimeMissionContext? context)
    {
        if (context == null)
        {
            AppendValue(builder, "<missions:null>");
            return;
        }

        AppendValue(builder, context.Source);
        AppendValue(builder, context.Error);
        foreach (var mission in context.AvailableMissions
                     .OrderBy(mission => mission.Label, StringComparer.Ordinal)
                     .ThenBy(mission => mission.Title, StringComparer.Ordinal))
        {
            AppendValue(builder, mission.Label);
            AppendValue(builder, mission.Title);
            AppendValue(builder, mission.CharacterLabel);
            AppendValue(builder, mission.CharacterName);
            AppendStrings(builder, mission.Places);
            AppendValue(builder, mission.Source);
            AppendValue(builder, mission.Status);
            AppendValue(builder, mission.Started);
            AppendValue(builder, mission.Finished);
            AppendValue(builder, mission.TargetRecipeId);
            AppendValue(builder, mission.TargetRecipeName);
        }

        foreach (var target in context.ServeTargets
                     .OrderBy(target => target.GuestId)
                     .ThenBy(target => target.MissionLabel, StringComparer.Ordinal)
                     .ThenBy(target => target.RecipeId))
        {
            AppendValue(builder, target.GuestId);
            AppendValue(builder, target.GuestName);
            AppendValue(builder, target.GuestLabel);
            AppendValue(builder, target.MissionLabel);
            AppendValue(builder, target.MissionTitle);
            AppendValue(builder, target.RecipeId);
            AppendValue(builder, target.RecipeName);
            AppendValue(builder, target.Status);
            AppendValue(builder, target.Source);
        }
    }

    private static void AppendNormalBusiness(StringBuilder builder, NormalBusinessContext? context)
    {
        if (context == null)
        {
            AppendValue(builder, "<normal-business:null>");
            return;
        }

        AppendValue(builder, context.Source);
        AppendValue(builder, context.Error);
        foreach (var order in context.Orders
                     .OrderBy(order => order.OrderKey, StringComparer.Ordinal)
                     .ThenBy(order => order.DeskCode)
                     .ThenBy(order => order.GuestName, StringComparer.Ordinal))
        {
            AppendValue(builder, order.OrderKey);
            AppendValue(builder, order.DeskCode);
            AppendValue(builder, order.GuestName);
            AppendValue(builder, order.FoodId);
            AppendValue(builder, order.FoodName);
            AppendValue(builder, order.BeverageId);
            AppendValue(builder, order.BeverageName);
            AppendValue(builder, order.HasServedFood);
            AppendValue(builder, order.HasServedBeverage);
            AppendValue(builder, order.ReadyToEvaluate);
            AppendValue(builder, order.HasEvaluated);
            AppendValue(builder, order.Source);
        }
    }

    private static void AppendRuntimeRareCustomers(StringBuilder builder, IEnumerable<RuntimeRareCustomer> customers)
    {
        foreach (var customer in customers.OrderBy(customer => customer.Id).ThenBy(customer => customer.Name, StringComparer.Ordinal))
        {
            AppendValue(builder, customer.Id);
            AppendValue(builder, customer.RuntimeStringId);
            AppendValue(builder, customer.Name);
            AppendStrings(builder, customer.Places);
            AppendStrings(builder, customer.PositiveTags);
            AppendStrings(builder, customer.NegativeTags);
            AppendStrings(builder, customer.BeverageTags);
            AppendValue(builder, customer.Source);
        }
    }

    private static void AppendIntDictionary(StringBuilder builder, IReadOnlyDictionary<int, int> values)
    {
        foreach (var item in values.OrderBy(item => item.Key))
        {
            AppendValue(builder, item.Key);
            AppendValue(builder, item.Value);
        }
    }

    private static void AppendInts(StringBuilder builder, IEnumerable<int> values)
    {
        foreach (var value in values.OrderBy(value => value))
        {
            AppendValue(builder, value);
        }
    }

    private static void AppendStrings(StringBuilder builder, IEnumerable<string> values)
    {
        foreach (var value in values.OrderBy(value => value, StringComparer.Ordinal))
        {
            AppendValue(builder, value);
        }
    }

    private static void AppendValue(StringBuilder builder, string? value)
    {
        if (value == null)
        {
            builder.Append("<null>|");
            return;
        }

        builder.Append(value.Length).Append(':').Append(value).Append('|');
    }

    private static void AppendValue(StringBuilder builder, bool value)
    {
        builder.Append(value ? "1|" : "0|");
    }

    private static void AppendValue(StringBuilder builder, bool? value)
    {
        builder.Append(value.HasValue ? (value.Value ? "1|" : "0|") : "<null>|");
    }

    private static void AppendValue(StringBuilder builder, int value)
    {
        builder.Append(value).Append('|');
    }

    private static void AppendValue(StringBuilder builder, int? value)
    {
        builder.Append(value?.ToString() ?? "<null>").Append('|');
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
        _performanceUpdatedAt[key] = Time.realtimeSinceStartup;
    }

    private Dictionary<string, double> BuildPerformanceSnapshot()
    {
        var minUpdatedAt = Time.realtimeSinceStartup - PerformanceSnapshotMaxAgeSeconds;
        return _performanceMs
            .Where(item => _performanceUpdatedAt.TryGetValue(item.Key, out var updatedAt) && updatedAt >= minUpdatedAt)
            .OrderByDescending(item => item.Value)
            .Take(12)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

    private void RecordPerformanceEntries(string prefix, IReadOnlyDictionary<string, double> entries)
    {
        var updatedAt = Time.realtimeSinceStartup;
        foreach (var entry in entries)
        {
            var key = $"{prefix}{entry.Key}";
            _performanceMs[key] = entry.Value;
            _performanceUpdatedAt[key] = updatedAt;
        }
    }

    /// <summary>
    /// 尝试刷新从游戏运行时导出的结构化静态目录。
    /// </summary>
    /// <remarks>
    /// 推荐引擎的料理、酒水、材料和稀客目录最终以游戏运行时为准。读取成功后替换当前仓库数据源；
    /// 失败时保留空目录状态并按重试间隔再次尝试。
    /// </remarks>
    private void TryRefreshRuntimeDataCatalog()
    {
        if (_repository == null) return;
        if (_runtimeDataCatalog.IsComplete) return;
        if (Time.realtimeSinceStartup < _nextRuntimeDataCatalogRefreshAt) return;
        _nextRuntimeDataCatalogRefreshAt = Time.realtimeSinceStartup + RuntimeDataCatalogRetrySeconds;

        try
        {
            var mappedGuestSnapshot = Measure("runtimeData.mappedGuests", () => new RuntimeMappedGuestCatalog(_repository).Snapshot());
            _runtimeMappedGuestSnapshot = mappedGuestSnapshot;
            _runtimeRareCustomers.Clear();
            _runtimeRareCustomers.AddRange(mappedGuestSnapshot.RuntimeRareCustomers);
            var staticDataSnapshot = Measure("runtimeData.staticData", () => new RuntimeStaticDataCatalog(_repository).Snapshot(mappedGuestSnapshot));
            _runtimeStaticDataSnapshot = staticDataSnapshot;
            _runtimeDataCatalog = staticDataSnapshot.DataCatalog;
            if (!_runtimeDataCatalog.IsComplete) return;

            _repository = DataRepository.FromRuntime(_runtimeDataCatalog);
            _businessFallbackState = null;
            _nextRuntimeDataCatalogRefreshAt = 0f;
            _runtimeSource = string.IsNullOrWhiteSpace(_runtimeSource)
                ? "game-runtime-static-data"
                : $"{_runtimeSource}; runtime-static-data";
        }
        catch (Exception ex)
        {
            _runtimeDataCatalog = RuntimeDataCatalog.Empty($"runtime data unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// 读取任务面板快照，并补充经营订单相关的上菜目标。
    /// </summary>
    /// <remarks>
    /// 任务和稀客邀请只在日间场景读取；夜间经营时仍会把当前订单目标合并到任务上下文，便于 UI 展示。
    /// </remarks>
    private RuntimeMissionContext? ReadRuntimeMissionsForSnapshot()
    {
        if (!HasRuntimeBasicsLoaded()) return null;

        if (IsNonGameplayScene(_activeSceneName))
        {
            return new RuntimeMissionContext
            {
                Source = "任务数据等待存档加载完成。",
            };
        }

        if (ShouldBlockDaySceneRuntimeReads(_activeSceneName))
        {
            return new RuntimeMissionContext
            {
                Source = "任务数据等待经营准备界面初始化完成。",
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

        if (!IsDaySceneRuntimeScene(_activeSceneName))
        {
            return new RuntimeMissionContext
            {
                Source = "任务数据只在日间场景读取。",
            };
        }

        if (!CanReadDaySceneTaskRuntime(out var daySceneWaitReason))
        {
            return new RuntimeMissionContext
            {
                Source = daySceneWaitReason,
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

    private bool ShouldReadDaySceneRuntimeState()
    {
        if (!IsDaySceneRuntimeScene(_activeSceneName)) return false;
        if (ShouldBlockDaySceneRuntimeReads(_activeSceneName)) return false;
        return IsDayScenePanelReady();
    }

    /// <summary>
    /// 判断当前是否允许读取日间任务与可邀请稀客。
    /// </summary>
    /// <param name="reason">不可读取时返回面向 UI 的等待或错误原因。</param>
    private bool CanReadDaySceneTaskRuntime(out string reason)
    {
        reason = "";
        if (!HasRuntimeBasicsLoaded())
        {
            reason = "游戏运行时数据尚未读取完成，请稍后再试。";
            return false;
        }

        if (IsNonGameplayScene(_activeSceneName))
        {
            reason = "当前未进入存档，无法读取日间任务和可邀请稀客。";
            return false;
        }

        if (ShouldBlockDaySceneRuntimeReads(_activeSceneName))
        {
            reason = "经营准备界面正在初始化，暂不读取日间任务和可邀请稀客。";
            return false;
        }

        if (IsNightBusinessScene(_activeSceneName))
        {
            reason = "当前处于夜间经营，日间任务和稀客邀请只支持日间场景。";
            return false;
        }

        if (!IsDaySceneRuntimeScene(_activeSceneName))
        {
            reason = "当前不在日间场景，无法读取日间任务和可邀请稀客。";
            return false;
        }

        if (!IsDayScenePanelReady())
        {
            reason = "日间场景 UI 正在初始化，暂不读取日间任务和可邀请稀客。";
            return false;
        }

        return true;
    }

    private bool HasRuntimeBasicsLoaded()
    {
        if (IsNonGameplayScene(_activeSceneName)) return false;
        return _runtimeLoaded || _state != null || _runtimeDataCatalog.IsComplete;
    }

    private bool CanPublishRecommendationState()
    {
        if (IsNonGameplayScene(_activeSceneName)) return false;
        if (_state != null) return true;
        return HasActiveNightBusinessContext(_businessContext);
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

    /// <summary>
    /// 读取或刷新普客订单快照。
    /// </summary>
    /// <remarks>
    /// 普客订单只在已确认夜间经营上下文有效时读取，并使用短缓存降低每帧反射扫描成本。
    /// </remarks>
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

        var captureVersion = NormalOrderRuntimeCapture.ChangeVersion;
        if (!force
            && NormalOrderRuntimeCapture.IsAttached
            && _normalBusinessContext != null
            && _normalBusinessContext.Orders.Count == 0
            && _normalBusinessContextCaptureVersion == captureVersion)
        {
            _nextNormalBusinessRefreshAt = Time.realtimeSinceStartup + NormalBusinessSnapshotCacheSeconds;
            return _normalBusinessContext;
        }

        try
        {
            var service = new RuntimeNormalOrderSnapshotService(_repository);
            _normalBusinessContext = Measure("business.normal.total", service.Load);
            RecordPerformanceEntries("business.normal.", service.PerformanceMs);
            _normalBusinessContextCaptureVersion = captureVersion;
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
            _normalBusinessContextCaptureVersion = captureVersion;
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

    private LocalApiConnectionConfigDto GetLocalApiConnectionConfig()
    {
        var port = Math.Clamp(_config?.LocalApiPort.Value ?? 32145, 1024, 65535);
        return new LocalApiConnectionConfigDto
        {
            Ok = true,
            LocalEndpoint = _localApiServer?.BaseUrl ?? $"http://127.0.0.1:{port}",
            LanEnabled = _config?.LocalApiLanEnabled.Value ?? false,
            LanRunning = _localApiServer?.LanRunning ?? false,
            LanBindHost = _config?.LocalApiLanHost.Value ?? "auto",
            Port = port,
            Token = _localApiToken,
            LanBindAddresses = _localApiServer?.LanBindAddresses ?? Array.Empty<string>(),
            LanEndpoints = _localApiServer?.LanEndpoints ?? Array.Empty<string>(),
            LanError = string.IsNullOrWhiteSpace(_localApiServer?.LanError) ? null : _localApiServer?.LanError,
        };
    }

    private LocalApiConnectionConfigDto UpdateLocalApiConnectionConfig(LocalApiConnectionConfigUpdate update)
    {
        if (_config == null)
        {
            return new LocalApiConnectionConfigDto
            {
                Ok = false,
                Error = "configuration is not available",
            };
        }

        if (update.LanEnabled.HasValue) _config.LocalApiLanEnabled.Value = update.LanEnabled.Value;
        if (update.LanBindHost != null) _config.LocalApiLanHost.Value = NormalizeLocalApiLanHost(update.LanBindHost);
        _localApiServer?.ApplyLanSettings(_config.LocalApiLanEnabled.Value, _config.LocalApiLanHost.Value);
        return GetLocalApiConnectionConfig();
    }

    private LocalApiConnectionConfigDto RegenerateLocalApiToken()
    {
        if (_config == null)
        {
            return new LocalApiConnectionConfigDto
            {
                Ok = false,
                Error = "configuration is not available",
            };
        }

        _localApiToken = GenerateLocalApiToken();
        _config.LocalApiToken.Value = _localApiToken;
        _localApiServer?.SetToken(_localApiToken);
        return GetLocalApiConnectionConfig();
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
                AggregateModLogPath = AggregateModLogService.ResolvePath(""),
                AggregateModLogMaxFileBytes = AggregateModLogService.MaxFileBytes,
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
            AggregateModLogEnabled = _config.EnableAggregateModLog.Value,
            AggregateModLogPath = AggregateModLogService.ResolvePath(_config.AggregateModLogPath.Value),
            AggregateModLogMaxFileBytes = AggregateModLogService.MaxFileBytes,
            NativeBepInExConsoleEnabled = !_config.DisableBepInExConsoleLog.Value && !_config.HideBepInExConsoleWindow.Value,
            NativeBepInExConsoleVisible = BepInExConsoleHelper.IsCurrentConsoleWindowVisible(),
        };
    }

    private void UpdateLocalApiLogSettings(bool? exposeLogs, bool? diagnostics, bool? nativeConsole, bool? aggregateLog)
    {
        if (_config == null) return;
        if (exposeLogs.HasValue) _config.ExposeLocalApiLogs.Value = exposeLogs.Value;
        if (diagnostics.HasValue) _config.EnableNightBusinessDiagnostics.Value = diagnostics.Value;
        if (aggregateLog.HasValue)
        {
            _config.EnableAggregateModLog.Value = aggregateLog.Value;
            AggregateModLogService.Configure(_config.EnableAggregateModLog.Value, _config.AggregateModLogPath.Value);
        }

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
            "aggregate" => settings.AggregateModLogPath,
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

    /// <summary>
    /// 处理本地 API 发起的单项库存编辑请求。
    /// </summary>
    /// <remarks>
    /// 若当前线程不是 Unity 主线程，会将请求入队并阻塞等待主线程在 <see cref="Update"/> 中执行。
    /// 超时会返回给 HTTP 调用方，避免后台请求无限挂起。
    /// </remarks>
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

    /// <summary>
    /// 处理本地 API 发起的批量库存编辑请求。
    /// </summary>
    /// <remarks>
    /// 批量操作可能触发多次运行时库存写入，因此等待时间比单项编辑略长。
    /// </remarks>
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

    private RareGuestInvitationResult InviteAllRareGuestsFromLocalApi(string scope, string kizunaLevels)
    {
        return RunRareGuestInvitationFromLocalApi(RareGuestInvitationAction.InviteAll, scope: scope, kizunaLevels: kizunaLevels);
    }

    private RareGuestInvitationResult ListRareGuestInvitationsFromLocalApi(string scope, string kizunaLevels)
    {
        return RunRareGuestInvitationFromLocalApi(RareGuestInvitationAction.List, scope: scope, kizunaLevels: kizunaLevels);
    }

    private RareGuestInvitationResult InviteRareGuestFromLocalApi(int guestId, string scope)
    {
        return RunRareGuestInvitationFromLocalApi(RareGuestInvitationAction.InviteOne, guestId, scope);
    }

    /// <summary>
    /// 处理本地 API 发起的稀客邀请读取或邀请请求。
    /// </summary>
    /// <remarks>
    /// 稀客邀请读取依赖日间场景运行时对象。后台线程调用时会入队并等待主线程返回结果。
    /// </remarks>
    private RareGuestInvitationResult RunRareGuestInvitationFromLocalApi(RareGuestInvitationAction action, int guestId = -1, string scope = "", string kizunaLevels = "")
    {
        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
        {
            return ApplyRareGuestInvitation(action, guestId, scope, kizunaLevels);
        }

        var pending = new PendingRareGuestInvitation
        {
            Action = action,
            GuestId = guestId,
            Scope = scope,
            KizunaLevels = kizunaLevels,
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

    /// <summary>
    /// 处理本地 API 发起的订单自动化请求。
    /// </summary>
    /// <remarks>
    /// 订单自动化会读写订单对象、厨具、桌面显示和库存，必须统一在主线程串行执行。
    /// </remarks>
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

    /// <summary>
    /// 执行稀客订单准备动作并刷新快照。
    /// </summary>
    private OrderPreparationResult ApplyOrderPreparation(OrderPreparationRequest request)
    {
        if (!CanRunNightBusinessOrderAction(out var reason)) return BuildUnavailableOrderResult(request, reason);

        var result = RuntimeOrderPreparationService.Prepare(request);
        _status = result.Ok
            ? L("已准备下一笔稀客订单。", "Next rare-customer order prepared.")
            : L($"准备下一笔稀客订单未完成：{result.Error}", $"Preparing next rare-customer order did not finish: {result.Error}");
        PublishLocalApiSnapshot();
        return result;
    }

    /// <summary>
    /// 检查是否允许执行夜间经营订单自动化。
    /// </summary>
    private bool CanRunNightBusinessOrderAction(out string reason)
    {
        reason = "";
        _activeSceneName = GetActiveSceneName();
        if (IsIzakayaPrepActive(_activeSceneName))
        {
            reason = "经营准备界面正在初始化，暂不执行订单自动化。";
            return false;
        }

        if (!IsNightBusinessScene(_activeSceneName))
        {
            reason = "当前不在夜晚经营场景，暂不执行订单自动化。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 在当前场景不允许执行订单自动化时构造统一失败结果。
    /// </summary>
    private static OrderPreparationResult BuildUnavailableOrderResult(OrderPreparationRequest request, string reason)
    {
        var result = new OrderPreparationResult
        {
            Ok = false,
            Error = reason,
            Order = new OrderPreparationOrder
            {
                DeskCode = request.DeskCode,
                GuestId = request.GuestId,
                GuestName = request.GuestName,
                FoodTag = request.FoodTag,
                BeverageTag = request.BeverageTag,
            },
            RecipeId = request.RecipeId,
            RecipeName = request.RecipeName,
            BeverageId = request.BeverageId,
            BeverageName = request.BeverageName,
        };
        result.Steps.Add(new OrderPreparationStep
        {
            Name = "场景检查",
            Ok = false,
            Message = reason,
        });
        return result;
    }

    private OrderPreparationResult ApplyOrderCompletion(OrderPreparationRequest request)
    {
        if (!CanRunNightBusinessOrderAction(out var reason)) return BuildUnavailableOrderResult(request, reason);

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
        if (!CanRunNightBusinessOrderAction(out var reason)) return BuildUnavailableOrderResult(request, reason);

        var result = RuntimeOrderPreparationService.CompleteNormalFirst(request);
        _status = result.Ok
            ? L("已处理当前第一笔普客订单。", "First normal-customer order handled.")
            : L($"处理当前第一笔普客订单未完成：{result.Error}", $"Handling first normal-customer order did not finish: {result.Error}");
        RefreshNormalBusinessContext(force: true);
        PublishLocalApiSnapshot();
        return result;
    }

    /// <summary>
    /// 消费后台线程提交的订单自动化队列。
    /// </summary>
    /// <remarks>
    /// 每个 pending 都会在 finally 中释放等待线程，确保业务异常不会导致 HTTP 线程永久阻塞。
    /// </remarks>
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

    /// <summary>
    /// 消费后台线程提交的稀客邀请队列。
    /// </summary>
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
                pending.Result = ApplyRareGuestInvitation(pending.Action, pending.GuestId, pending.Scope, pending.KizunaLevels);
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

    /// <summary>
    /// 执行稀客邀请列表读取或邀请动作。
    /// </summary>
    private RareGuestInvitationResult ApplyRareGuestInvitation(RareGuestInvitationAction action, int guestId, string scope, string kizunaLevels = "")
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
            RareGuestInvitationAction.List => RuntimeRareGuestInvitationService.ListAvailable(_repository, _log, scope, kizunaLevels),
            RareGuestInvitationAction.InviteOne => RuntimeRareGuestInvitationService.InviteOne(_repository, guestId, _log, scope),
            _ => RuntimeRareGuestInvitationService.InviteAllAvailable(_repository, _log, scope, kizunaLevels),
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
        return CanReadDaySceneTaskRuntime(out reason);
    }

    /// <summary>
    /// 轮询出锅直送队列并发布产生的自动化消息。
    /// </summary>
    /// <remarks>
    /// 离开夜间经营场景时立即清理待直送队列，避免下一次经营复用旧厨具对象引用。
    /// </remarks>
    private void ProcessPendingCookingCollections()
    {
        if (!RuntimeOrderPreparationService.HasPendingCookingCollections) return;

        if (!IsNightBusinessScene(_activeSceneName))
        {
            RuntimeOrderPreparationService.ClearPendingCookingCollections();
            return;
        }

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

    /// <summary>
    /// 消费单项库存编辑队列。
    /// </summary>
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

    /// <summary>
    /// 消费批量库存编辑队列。
    /// </summary>
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

    /// <summary>
    /// 在主线程执行库存数量写入，并刷新依赖库存的运行时快照。
    /// </summary>
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

    /// <summary>
    /// 在主线程执行批量库存写入，并汇总成功、未变化和失败数量。
    /// </summary>
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

    /// <summary>
    /// 清除全部已加载运行时状态。
    /// </summary>
    /// <remarks>
    /// 用于主菜单、未加载存档等场景。会同时清空订单捕获、出锅直送队列和厨具高亮，避免跨存档残留。
    /// </remarks>
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
        ResetNightBusinessRefreshCache();
        ResetNormalBusinessRefreshCache();
        _runtimeMappedGuestSnapshot = null;
        _runtimeStaticDataSnapshot = null;
        _runtimeSource = "";
        _runtimeStateSignature = "";
        _lastRuntimeReadUtc = DateTime.MinValue;
        SpecialOrderRuntimeCapture.ClearOrders("runtime cleared");
        NormalOrderRuntimeCapture.ClearOrders("runtime cleared");
        RuntimeOrderPreparationService.ClearPendingCookingCollections();
        RuntimeCookerHighlightService.Suspend(status);
        _status = status;
    }

    /// <summary>
    /// 清除夜间经营专属状态，但保留基础推荐运行时数据。
    /// </summary>
    private void ClearNightBusinessRuntime(string status)
    {
        SpecialOrderRuntimeCapture.ClearOrders("left night business scene");
        NormalOrderRuntimeCapture.ClearOrders("left night business scene");
        RuntimeOrderPreparationService.ClearPendingCookingCollections();
        _businessContext = new NightBusinessContext
        {
            Source = status,
        };
        _normalBusinessContext = null;
        _nextNormalBusinessRefreshAt = 0f;
        ResetNightBusinessRefreshCache();
        ResetNormalBusinessRefreshCache();
        ClearPlacedCookersFromCurrentState("not in night business scene");
        RuntimeCookerHighlightService.Suspend(status);
        _status = status;
    }

    private void ResetNightBusinessRefreshCache()
    {
        _businessContextSpecialOrderVersion = long.MinValue;
        _businessContextSceneName = "";
        _nextStableBusinessContextRescanAt = 0f;
    }

    private void ResetNormalBusinessRefreshCache()
    {
        _normalBusinessContextCaptureVersion = long.MinValue;
    }

    /// <summary>
    /// 从当前推荐状态中移除摆放厨具快照。
    /// </summary>
    /// <remarks>
    /// 不在夜间经营场景时厨具对象引用会失效，但库存和解锁料理仍可继续用于推荐。
    /// </remarks>
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

    /// <summary>
    /// 判断夜间经营上下文是否可用于推荐和订单自动化。
    /// </summary>
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

    /// <summary>
    /// 取得经营页可用的推荐状态。
    /// </summary>
    /// <remarks>
    /// 若基础运行时状态暂不可读但夜间订单存在，则构造一次全可用兜底状态，保证订单推荐仍可显示候选。
    /// </remarks>
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

    /// <summary>
    /// 生成推荐状态签名，用于判断运行时状态是否发生会影响推荐的变化。
    /// </summary>
    private static string BuildRecommendationStateSignature(RecommendationState state)
    {
        unchecked
        {
            var hash = 17;
            hash = HashIds(hash, state.AvailableRecipeIds);
            hash = HashIds(hash, state.AvailableBeverageIds);
            hash = HashIds(hash, state.AvailableIngredientIds);
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

    /// <summary>
    /// 获取或生成本地 API Token。
    /// </summary>
    /// <remarks>
    /// Token 写入 BepInEx 配置，用于伴随窗口和 Mod 本地 API 之间的请求鉴权。
    /// </remarks>
    private static string EnsureLocalApiToken(StewardPluginConfig config)
    {
        var token = config.LocalApiToken.Value.Trim();
        if (IsUsableLocalApiToken(token)) return token;

        token = GenerateLocalApiToken();
        config.LocalApiToken.Value = token;
        return token;
    }

    private static string GenerateLocalApiToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    private static bool IsUsableLocalApiToken(string token)
    {
        return token.Length >= 32 && token.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));
    }

    private static string NormalizeLocalApiLanHost(string value)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || string.Equals(normalized, "0.0.0.0", StringComparison.Ordinal)
            || string.Equals(normalized, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        return normalized;
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
        if (string.IsNullOrWhiteSpace(sceneName)) return true;
        var normalized = sceneName.Trim();
        var compact = normalized.Replace(" ", "", StringComparison.Ordinal);
        if (IsMainMenuPanelActive()
            || normalized.Contains("entry", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("title", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("menu", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("loading", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Main", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Main Scene", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("MainScene", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("MainMenu", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_config == null) return false;
        return ContainsConfiguredKeyword(sceneName, _config.NonGameplaySceneKeywords.Value);
    }

    /// <summary>
    /// 判断当前场景是否允许读取基础运行时推荐状态。
    /// </summary>
    /// <param name="sceneName">Unity 当前激活场景名。</param>
    /// <param name="reason">不可读取时返回面向 UI 的原因。</param>
    private bool CanReadRuntimeStateInCurrentScene(string sceneName, out string reason)
    {
        reason = "";
        if (IsNonGameplayScene(sceneName))
        {
            reason = L(
                "当前游戏运行时数据不可用：当前处于非游戏内页面。",
                "Live game runtime data unavailable: this is not an in-game page.");
            return false;
        }

        if (IsIzakayaPrepActive(sceneName))
        {
            if (IsIzakayaPrepPanelReady()) return true;

            reason = L(
                "经营准备界面正在初始化；等待游戏准备面板完成打开后读取运行态。",
                "Izakaya prep is initializing; waiting for the game prep panel to finish opening before reading live runtime state.");
            return false;
        }

        if (IsNightBusinessScene(sceneName)) return true;

        if (!IsDaySceneRuntimeScene(sceneName))
        {
            reason = L(
                "等待日间场景运行态初始化完成；暂不读取游戏运行态。",
                "Waiting for day-scene runtime data before reading live runtime state.");
            return false;
        }

        if (!IsDayScenePanelReady())
        {
            reason = L(
                "日间场景正在初始化；暂不读取游戏运行态。",
                "Day scene is initializing; live runtime state is not read yet.");
            return false;
        }

        return true;
    }

    private static bool IsDayScenePanelReady()
    {
        return RuntimeSceneReadinessCapture.DaySceneReady && IsDayScenePanelActive();
    }

    private static bool IsIzakayaPrepPanelReady()
    {
        return RuntimeSceneReadinessCapture.IzakayaPrepReady && IsIzakayaPrepPanelActiveInWorkPrepRoot();
    }

    private static bool IsNightBusinessScene(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName)) return false;

        var normalized = sceneName.Trim();
        return string.Equals(normalized, "Work", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "WorkScene", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("WorkScene", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIzakayaPrepScene(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName)) return false;

        var normalized = sceneName.Trim();
        if (normalized.Contains("WorkPrep", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("IzakayaConfig", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("IzakayaPrep", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized.Contains("Prep", StringComparison.OrdinalIgnoreCase)
            && (normalized.Contains("Work", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("Izakaya", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 判断当前是否是可读取日间运行时对象的场景。
    /// </summary>
    private bool IsDaySceneRuntimeScene(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName)) return false;
        if (IsNonGameplayScene(sceneName)) return false;
        if (IsNightBusinessScene(sceneName)) return false;
        if (IsIzakayaPrepScene(sceneName)) return false;
        return IsDayScenePanelActive();
    }

    private static bool IsIzakayaPrepActive(string sceneName)
    {
        if (IsIzakayaPrepScene(sceneName)) return true;
        if (IsNightBusinessScene(sceneName)) return false;
        return IsIzakayaPrepPanelActiveInWorkPrepRoot();
    }

    /// <summary>
    /// 判断日间运行时读取是否应被经营准备面板阻塞。
    /// </summary>
    /// <remarks>
    /// 部分场景名仍像日间场景，但经营准备面板已经激活，此时读取日间任务或稀客候选容易拿到过期对象。
    /// </remarks>
    private static bool ShouldBlockDaySceneRuntimeReads(string sceneName)
    {
        if (IsIzakayaPrepScene(sceneName)) return true;
        if (IsNightBusinessScene(sceneName)) return false;
        return IsIzakayaPrepPanelActiveInWorkPrepRoot();
    }

    private static bool IsIzakayaPrepPanelActiveInWorkPrepRoot()
    {
        return FindActiveIzakayaPrepPanelInWorkPrepRoot() != null;
    }

    private static bool IsMainMenuPanelActive()
    {
        return FindActiveGameObject("MainMenuPannel") != null
            || FindActiveGameObject("MainMenuPannel(Clone)") != null
            || FindActiveGameObject("EventMainMenuPannel") != null
            || FindActiveGameObject("EventMainMenuPannel(Clone)") != null;
    }

    private static bool IsDayScenePanelActive()
    {
        return FindActiveGameObject("DaySceneSustainedPannel") != null
            || FindActiveGameObject("DaySceneSustainedPannel(Clone)") != null;
    }

    private static GameObject? FindActiveGameObject(string name)
    {
        try
        {
            var gameObject = GameObject.Find(name);
            return gameObject != null && gameObject.activeInHierarchy ? gameObject : null;
        }
        catch
        {
            return null;
        }
    }

    private static GameObject? FindActiveIzakayaPrepPanelInWorkPrepRoot()
    {
        try
        {
            var panel = GameObject.Find("IzakayaConfigPannelNew(Clone)")
                ?? GameObject.Find("IzakayaConfigPannelNew");
            if (panel == null || !panel.activeInHierarchy) return null;
            return HasWorkPrepAncestor(panel.transform) ? panel : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasWorkPrepAncestor(Transform? transform)
    {
        for (var current = transform; current != null; current = current.parent)
        {
            var name = current.name ?? "";
            if (name.Contains("WorkPrep", StringComparison.OrdinalIgnoreCase)
                || name.Contains("WorkPrepScenePannelRoot", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
