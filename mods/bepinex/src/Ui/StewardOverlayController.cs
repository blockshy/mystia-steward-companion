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
    private const float MinWindowWidth = 520f;
    private const float MinWindowHeight = 420f;
    private const float WindowMargin = 12f;
    private const float WindowHeaderHeight = 52f;
    private const float ResizeHandleSize = 24f;
    private const float SpecialOrderRefreshDebounceSeconds = 0.2f;
    private static readonly JsonSerializerOptions LocalApiJsonOptions = new(JsonSerializerDefaults.Web);

    private StewardPluginConfig? _config;
    private ManualLogSource? _log;
    private DataRepository? _repository;
    private RecommendationState? _state;
    private RecommendationState? _businessFallbackState;
    private NightBusinessContext? _businessContext;
    private RareRecommendationCache? _rareRecommendationCache;
    private LocalApiServer? _localApiServer;
    private NormalRecommendationService? _normalService;
    private RareRecommendationService? _rareService;
    private Rect _windowRect = new(48, 48, 980, 680);
    private readonly object _inventoryEditLock = new();
    private readonly Queue<PendingInventoryEdit> _pendingInventoryEdits = new();
    private readonly object _orderPreparationLock = new();
    private readonly Queue<PendingOrderPreparation> _pendingOrderPreparations = new();
    private Vector2 _scroll;
    private bool _visible;
    private bool _runtimeLoaded;
    private bool _cursorStateCaptured;
    private bool _draggingWindow;
    private bool _resizingWindow;
    private bool _previousCursorVisible;
    private bool _inputResetUnsupportedLogged;
    private CursorLockMode _previousCursorLockState;
    private Vector2 _windowDragOffset;
    private Vector2 _resizeStartMouse;
    private Rect _resizeStartRect;
    private int _tab;
    private int _placeIndex;
    private int _rareCustomerIndex;
    private int _businessOrderIndex;
    private int _recommendationCacheVersion;
    private int _mainThreadId;
    private long _lastSpecialOrderChangeVersion;
    private string _openDropdownId = "";
    private string _runtimeSource = "";
    private string _activeSceneName = "";
    private string _status = "Not initialized.";
    private string _lastRuntimeErrorMessage = "";
    private string _runtimeStateSignature = "";
    private string _requiredFoodTag = "";
    private string _requiredBeverageTag = "";
    private string _localApiToken = "";
    private int _previousRareCustomerId = -1;
    private DateTime _lastRuntimeReadUtc = DateTime.MinValue;
    private float _nextAutoRefreshAt;
    private float _nextBusinessRefreshAt;
    private bool _stylesInitialized;
    private bool _localApiSnapshotErrorLogged;
    private bool _disposed;
    private bool _controllerToggleLatched;
    private bool _specialOrderRefreshPending;
    private float _nextControllerToggleAt;
    private float _nextSpecialOrderRefreshAt;
    private GUIStyle? _titleStyle;
    private GUIStyle? _labelStyle;
    private GUIStyle? _mutedStyle;
    private GUIStyle? _sectionStyle;
    private GUIStyle? _buttonStyle;
    private GUIStyle? _buttonActiveStyle;

    private sealed class PendingInventoryEdit
    {
        public string ItemType { get; init; } = "";
        public int ItemId { get; init; }
        public int Quantity { get; init; }
        public ManualResetEventSlim Completion { get; } = new(false);
        public RuntimeInventoryEditResult? Result { get; set; }
        public Exception? Error { get; set; }
    }

    private sealed class PendingOrderPreparation
    {
        public OrderPreparationRequest Request { get; init; } = new();
        public bool CompleteOrder { get; init; }
        public ManualResetEventSlim Completion { get; } = new(false);
        public OrderPreparationResult? Result { get; set; }
        public Exception? Error { get; set; }
    }

    private sealed class RareRecommendationCache
    {
        public int Version { get; init; }
        public int CustomerId { get; init; }
        public string RequiredFoodTag { get; init; } = "";
        public string RequiredBeverageTag { get; init; } = "";
        public int MaxExtraIngredients { get; init; }
        public RecommendationState? State { get; init; }
        public List<RareRecipeResult> Recipes { get; init; } = new();
        public List<RareBeverageResult> Beverages { get; init; } = new();
    }

    public void Initialize(StewardPluginConfig config, ManualLogSource log)
    {
        _config = config;
        _log = log;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _windowRect = new Rect(
            config.WindowX.Value,
            config.WindowY.Value,
            config.WindowWidth.Value,
            config.WindowHeight.Value);
        _placeIndex = Math.Max(0, Array.IndexOf(PlaceNames.All, config.DefaultPlace.Value));
        _activeSceneName = GetActiveSceneName();
        EnsureWindowInsideScreen();
        LoadRepository();
        _localApiToken = EnsureLocalApiToken(config);
        StartLocalApi();
        RefreshBusinessContext(false);
        RefreshRuntimeState(false);
        PublishLocalApiSnapshot();
        if (_localApiServer != null)
        {
            CompanionProcessLauncher.TryAutoLaunch(config, log, _localApiToken);
        }
    }

    public void Update()
    {
        if (_disposed || _config == null) return;
        ProcessPendingInventoryEdits();
        ProcessPendingOrderPreparations();
        ProcessPendingCookingCollections();
        RefreshOnSceneChange();
        RefreshBusinessContextOnSpecialOrderChange();

        if (IsTogglePressed())
        {
            if (_config.EnableInGameOverlay.Value)
            {
                SetOverlayVisible(!_visible);
            }
            else if (_log != null)
            {
                CompanionProcessLauncher.TryToggleOrLaunch(_config, _log, _localApiToken);
            }
        }

        if (Input.GetKeyDown(_config.ReloadKey.Value))
        {
            RefreshBusinessContext(true);
            RefreshRuntimeState(true);
        }

        if (!_config.EnableInGameOverlay.Value && _visible)
        {
            SetOverlayVisible(false);
        }

        if (_visible)
        {
            EnsureOverlayCursor();
            if (_config.BlockGameInputOnPanel.Value) BlockGameInputIfPointerOverPanel();
        }

        if (!_config.AutoRefreshRuntime.Value || Time.realtimeSinceStartup < _nextAutoRefreshAt) return;
        _nextAutoRefreshAt = Time.realtimeSinceStartup + Math.Max(1f, _config.AutoRefreshSeconds.Value);
        RefreshBusinessContext(false);
        RefreshRuntimeState(false);
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
        _nextAutoRefreshAt = 0f;
        _nextBusinessRefreshAt = 0f;
        _businessFallbackState = null;

        if (IsNonGameplayScene(sceneName))
        {
            ClearLoadedRuntime(L(
                "当前游戏运行时数据不可用：当前处于非游戏内页面。",
                "Live game runtime data unavailable: this is not an in-game page."));
            PublishLocalApiSnapshot();
            return;
        }

        RefreshBusinessContext(false, force: true);
        RefreshRuntimeState(false);
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
        if (_visible && _config?.BlockGameInputOnPanel.Value == true)
        {
            BlockGameInputIfPointerOverPanel();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        CompanionProcessLauncher.TryNotifyExit();
        _localApiServer?.Dispose();
        _localApiServer = null;
        RuntimeCookerHighlightService.Clear();
        RestoreCursorState();
    }

    public void OnGUI()
    {
        if (!_visible) return;

        EnsureStyles();
        var previousDepth = GUI.depth;
        var previousLabel = GUI.skin.label;
        var previousButton = GUI.skin.button;
        var activeEvent = Event.current;
        var consumeEvent = activeEvent != null && ShouldConsumeEvent(activeEvent);

        GUI.depth = -10000;
        GUI.skin.label = _labelStyle ?? previousLabel;
        GUI.skin.button = _buttonStyle ?? previousButton;

        HandleWindowInteraction(activeEvent);
        DrawPanelChrome();
        var contentRect = new Rect(
            _windowRect.x + 18,
            _windowRect.y + 64,
            _windowRect.width - 36,
            _windowRect.height - 82);

        GUILayout.BeginArea(contentRect);
        try
        {
            DrawPanelContent();
        }
        catch (Exception ex)
        {
            _status = L($"界面错误：{ex.Message}", $"UI error: {ex.Message}");
            _log?.LogError(ex);
            GUILayout.Label(_status);
        }
        finally
        {
            GUILayout.EndArea();
            if (consumeEvent)
            {
                if (_config?.BlockGameInputOnPanel.Value == true) BlockGameInputIfPointerOverPanel();
                activeEvent?.Use();
            }

            if (_visible && _config?.DrawOverlayCursor.Value == true) DrawOverlayCursor();
            GUI.skin.label = previousLabel;
            GUI.skin.button = previousButton;
            GUI.depth = previousDepth;
        }
    }

    private void DrawPanelContent()
    {
        if (_repository == null)
        {
            GUILayout.Label(_status);
            if (GUILayout.Button(L("重新加载数据", "Reload data"))) LoadRepository();
            return;
        }

        _tab = DrawButtonRow(_tab, new[] { L("设置", "Settings"), L("普客", "Normal"), L("稀客", "Rare"), L("经营中", "Service") }, 4);
        _scroll = GUILayout.BeginScrollView(_scroll);
        try
        {
            switch (_tab)
            {
                case 0:
                    DrawSettings();
                    break;
                case 1:
                    DrawNormal();
                    break;
                case 2:
                    DrawRare();
                    break;
                case 3:
                    DrawBusiness();
                    break;
            }
        }
        finally
        {
            GUILayout.EndScrollView();
        }
    }

    private void DrawSettings()
    {
        if (_config == null || _repository == null) return;

        GUILayout.Label($"{L("状态", "Status")}: {_status}");
        GUILayout.Label($"{L("当前场景", "Active scene")}: {FormatSceneName()}");
        GUILayout.Label($"{L("数据目录", "Data")}: {_repository.DataDirectory}");
        GUILayout.Label($"{L("当前数据源", "Current source")}: {FormatCurrentSource()}");
        GUILayout.Label($"{L("键鼠开关", "Keyboard toggle")}: {_config.ToggleKey.Value}");
        GUILayout.Label($"{L("手柄开关", "Controller toggle")}: {FormatControllerKey(_config.ControllerToggleKey.Value)}");
        GUILayout.Label($"{L("刷新热键", "Refresh hotkey")}: {_config.ReloadKey.Value}");

        GUILayout.Space(8);
        GUILayout.Label($"{L("语言", "Language")}:");
        var previousLanguageIndex = IsChinese() ? 0 : 1;
        var nextLanguageIndex = DrawButtonRow(previousLanguageIndex, new[] { "中文", "English" }, 2);
        if (nextLanguageIndex != previousLanguageIndex)
        {
            _config.Language.Value = nextLanguageIndex == 0 ? "zh-CN" : "en";
            _status = L("语言已切换。", "Language changed.");
        }

        DrawOpacityControls();

        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        try
        {
            if (GUILayout.Button(L("刷新实时数据", "Refresh live data")))
            {
                RefreshRuntimeState(true);
                RefreshBusinessContext(true);
            }

            if (GUILayout.Button(L("重新加载数据", "Reload data")))
            {
                LoadRepository();
                RefreshRuntimeState(true);
                RefreshBusinessContext(true);
            }
        }
        finally
        {
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(8);
        GUILayout.Label(L(
            "数据来源：实时读取游戏运行时对象，不读取 .memory 存档文件。",
            "Data source: live game runtime objects; .memory save files are not read."));

        GUILayout.Label($"{L("非游戏页面关键词", "Non-game scene keywords")}: {_config.NonGameplaySceneKeywords.Value}");
        GUILayout.Label($"{L("窗口不透明度", "Window opacity")}: {Mathf.Clamp(_config.PanelOpacity.Value, 0.5f, 1f):0.00}");
        GUILayout.Label(L("窗口可拖动标题栏移动，拖动右下角缩放。", "Drag the title bar to move the window; drag the bottom-right handle to resize."));
        GUILayout.Label($"{L("面板输入拦截", "Panel input block")}: {(_config.BlockGameInputOnPanel.Value ? L("开启", "on") : L("关闭", "off"))}");
        GUILayout.Label($"{L("覆盖光标", "Overlay cursor")}: {(_config.DrawOverlayCursor.Value ? L("开启", "on") : L("关闭", "off"))}");
        GUILayout.Label($"{L("本地 API", "Local API")}: {FormatLocalApiStatus()}");
        DrawNightBusinessDiagnosticsSettings();

        GUILayout.Space(8);
        GUILayout.Label($"{L("当前稀客", "Active rare customers")}: {(_businessContext?.ActiveRareGuests.Count ?? 0)}");
        GUILayout.Label($"{L("经营订单", "Service orders")}: {(_businessContext?.Orders.Count ?? 0)}");
        if (!string.IsNullOrWhiteSpace(_businessContext?.Source))
        {
            GUILayout.Label($"{L("经营扫描", "Service scan")}: {_businessContext.Source}", _mutedStyle ?? GUI.skin.label);
        }

        if (!string.IsNullOrWhiteSpace(_businessContext?.Error))
        {
            GUILayout.Label($"{L("经营读取提示", "Service read note")}: {_businessContext.Error}");
        }

        if (_state == null)
        {
            DrawNoRuntimeLoaded();
            return;
        }

        GUILayout.Label($"{L("流行喜好标签", "Popular liked tag")}: {_state.PopularFoodTag ?? L("无", "none")}");
        GUILayout.Label($"{L("流行厌恶标签", "Popular hated tag")}: {_state.PopularHateFoodTag ?? L("无", "none")}");
        GUILayout.Label($"{L("明星店", "Famous shop")}: {(_state.FamousShopEnabled ? L("开启", "on") : L("关闭", "off"))}");
    }

    private void DrawNormal()
    {
        if (_repository == null || _normalService == null || _config == null) return;
        if (_state == null)
        {
            DrawNoRuntimeLoaded();
            return;
        }

        DrawPlaceSelector();
        var place = PlaceNames.All[_placeIndex];
        GUILayout.Space(8);

        var recipeRows = _normalService.ComputeRecipes(place, _state)
            .OrderByDescending(row => row.TotalCoverage)
            .ThenByDescending(row => row.IngredientCost)
            .ThenBy(row => row.Recipe.Id)
            .Take(Math.Max(1, _config.MaxNormalRows.Value));
        var beverageRows = _normalService.ComputeBeverages(place, _state)
            .OrderByDescending(row => row.TotalCoverage)
            .ThenByDescending(row => row.Beverage.Price)
            .ThenBy(row => row.Beverage.Id)
            .Take(Math.Max(1, _config.MaxNormalRows.Value));

        DrawRecommendationColumns(
            L("料理推荐", "Recipe recommendations"),
            () => DrawNormalRecipeRows(recipeRows),
            L("酒水推荐", "Beverage recommendations"),
            () => DrawNormalBeverageRows(beverageRows));
    }

    private void DrawRare()
    {
        if (_repository == null || _rareService == null || _config == null) return;
        if (_state == null)
        {
            DrawNoRuntimeLoaded();
            return;
        }

        DrawPlaceSelector();
        var place = PlaceNames.All[_placeIndex];
        var customers = _repository.GetRareCustomersByPlace(place).ToList();
        if (customers.Count == 0)
        {
            GUILayout.Label(L("该地区没有稀客。", "No rare customers in this region."));
            return;
        }

        _rareCustomerIndex = Math.Clamp(_rareCustomerIndex, 0, customers.Count - 1);
        var names = customers.Select(c => c.Name).ToList();
        _rareCustomerIndex = DrawDropdown(L("稀客", "Rare customer"), _rareCustomerIndex, names, "rare-customer");
        var customer = customers[_rareCustomerIndex];

        if (_previousRareCustomerId != customer.Id)
        {
            _previousRareCustomerId = customer.Id;
            _requiredFoodTag = "";
            _requiredBeverageTag = "";
        }

        if (string.IsNullOrWhiteSpace(_requiredFoodTag) && customer.PositiveTags.Count > 0)
        {
            _requiredFoodTag = customer.PositiveTags[0];
        }

        if (string.IsNullOrWhiteSpace(_requiredBeverageTag) && customer.BeverageTags.Count > 0)
        {
            _requiredBeverageTag = customer.BeverageTags[0];
        }

        GUILayout.Space(8);
        GUILayout.Label($"{L("稀客", "Rare customer")}: {customer.Name}");
        GUILayout.Label($"{L("料理候选 Tag", "Food tag candidates")}: {string.Join(", ", customer.PositiveTags)}");
        _requiredFoodTag = DrawStringDropdown(L("点单料理 Tag", "Required food tag"), _requiredFoodTag, customer.PositiveTags, "rare-food-tag");
        GUILayout.Label($"{L("酒水候选 Tag", "Beverage tag candidates")}: {string.Join(", ", customer.BeverageTags)}");
        _requiredBeverageTag = DrawStringDropdown(L("点单酒水 Tag", "Required beverage tag"), _requiredBeverageTag, customer.BeverageTags, "rare-beverage-tag");

        DrawRareRecommendations(customer, _requiredFoodTag, _requiredBeverageTag);
    }

    private void DrawBusiness()
    {
        if (_repository == null || _rareService == null || _config == null) return;

        GUILayout.BeginHorizontal();
        try
        {
            if (GUILayout.Button(L("刷新经营订单", "Refresh service orders")))
            {
                RefreshBusinessContext(true);
            }

            if (GUILayout.Button(L("刷新实时数据", "Refresh live data")))
            {
                RefreshRuntimeState(true);
                RefreshBusinessContext(true);
            }
        }
        finally
        {
            GUILayout.EndHorizontal();
        }

        var contextPlace = _businessContext?.Place;
        if (!string.IsNullOrWhiteSpace(contextPlace))
        {
            var placeIndex = Array.IndexOf(PlaceNames.All, contextPlace);
            if (placeIndex >= 0) _placeIndex = placeIndex;
        }

        GUILayout.Space(8);
        DrawSection(L("经营场景", "Service scene"));
        GUILayout.Label(!string.IsNullOrWhiteSpace(contextPlace)
            ? $"{L("当前经营场景", "Detected service scene")}: {contextPlace}"
            : L("未检测到经营场景。", "No service scene detected."));
        if (!string.IsNullOrWhiteSpace(_businessContext?.Source))
        {
            GUILayout.Label($"{L("扫描状态", "Scan status")}: {_businessContext.Source}", _mutedStyle ?? GUI.skin.label);
        }

        if (!string.IsNullOrWhiteSpace(_businessContext?.PlaceLabel))
        {
            GUILayout.Label($"{L("游戏地图标签", "Game map label")}: {_businessContext.PlaceLabel}", _mutedStyle ?? GUI.skin.label);
        }

        var orders = _businessContext?.Orders ?? new List<NightBusinessOrder>();
        var activeGuests = _businessContext?.ActiveRareGuests ?? new List<NightBusinessGuest>();
        GUILayout.Space(10);
        DrawSection(L("当前稀客", "Current rare customers"));
        if (activeGuests.Count == 0)
        {
            GUILayout.Label(L("尚未检测到进场、排队或已入座稀客。", "No entered, queued, or seated rare customer detected yet."));
        }
        else
        {
            foreach (var guest in activeGuests)
            {
                GUILayout.Label($"{L("桌", "Desk")} {FormatDeskCode(guest.DeskCode)}: {guest.GuestName} [{guest.Source}]");
            }
        }

        GUILayout.Space(10);
        DrawSection(L("当前稀客点单", "Current rare orders"));

        if (orders.Count == 0)
        {
            GUILayout.Label(L(
                "尚未检测到 HUD 上的稀客点单。稀客出现并点单后这里会自动刷新。",
                "No visible rare-customer order detected yet. This will refresh after a rare customer orders."));
            if (!string.IsNullOrWhiteSpace(_businessContext?.Error))
            {
                GUILayout.Label($"{L("读取提示", "Read note")}: {_businessContext.Error}", _mutedStyle ?? GUI.skin.label);
            }

            GUILayout.Space(8);
            GUILayout.Label(activeGuests.Count > 0
                ? L("已检测到稀客，等待稀客点单词条。", "Rare customer detected; waiting for required order tags.")
                : L("可以先使用“稀客”页手动选择角色和词条。", "Use the Rare tab for manual customer and tag selection for now."));
            return;
        }

        _businessOrderIndex = Math.Clamp(_businessOrderIndex, 0, orders.Count - 1);
        var orderLabels = orders
            .Select(order => $"{L("桌", "Desk")} {FormatDeskCode(order.DeskCode)}: {order.GuestName} | {order.FoodTag}/{order.BeverageTag}")
            .ToList();
        _businessOrderIndex = DrawButtonRow(_businessOrderIndex, orderLabels, 1);

        var selectedOrder = orders[_businessOrderIndex];
        GUILayout.Space(8);
        GUILayout.Label($"{L("稀客", "Rare customer")}: {selectedOrder.GuestName}");
        GUILayout.Label($"{L("来源", "Source")}: {selectedOrder.Source}");
        GUILayout.Label($"{L("点单料理 Tag", "Required food tag")}: {selectedOrder.FoodTag} ({selectedOrder.FoodTagId})");
        GUILayout.Label($"{L("点单酒水 Tag", "Required beverage tag")}: {selectedOrder.BeverageTag} ({selectedOrder.BeverageTagId})");

        var customer = ResolveRareCustomerForOrder(selectedOrder);
        if (customer == null)
        {
            GUILayout.Label(L(
                "无法把该游戏稀客映射到本地或运行时稀客数据。",
                "Cannot map this game rare customer to local or runtime rare-customer data."));
            return;
        }

        var state = GetBusinessRecommendationState();
        if (_state == null)
        {
            GUILayout.Label(L(
                "基础运行时库存暂不可用，经营中页临时按“全内容可用”计算推荐。",
                "Base runtime inventory is unavailable; Service recommendations temporarily assume all content is available."),
                _mutedStyle ?? GUI.skin.label);
        }

        DrawRareRecommendations(customer, selectedOrder.FoodTag, selectedOrder.BeverageTag, state);
    }

    private RareCustomer? ResolveRareCustomerForOrder(NightBusinessOrder order)
    {
        if (_repository == null) return null;
        if (order.GuestId.HasValue && _repository.RareCustomersById.TryGetValue(order.GuestId.Value, out var localCustomer))
        {
            return localCustomer;
        }

        return new RuntimeMappedGuestCatalog(_repository).ResolveCustomer(order.GuestId, order.GuestName);
    }

    private void DrawRareRecommendations(
        RareCustomer customer,
        string requiredFoodTag,
        string requiredBeverageTag,
        RecommendationState? recommendationState = null)
    {
        if (_rareService == null || _config == null) return;
        var state = recommendationState ?? _state;
        if (state == null) return;
        var cache = GetRareRecommendationCache(customer, requiredFoodTag, requiredBeverageTag, state);

        var recipeRows = cache.Recipes
            .Where(row => row.MeetsRequiredFood)
            .OrderByDescending(row => row.MeetsRequiredFood)
            .ThenByDescending(row => row.FoodScore)
            .ThenBy(row => row.ExtraIngredients.Count)
            .ThenBy(row => GetRareRecipeResourcePressure(row, state))
            .ThenByDescending(row => row.Recipe.Price)
            .ThenBy(row => row.ExtraCost)
            .ThenBy(row => row.Recipe.Id)
            .Take(Math.Max(1, _config.MaxRareRows.Value));

        var beverageRows = cache.Beverages
            .Where(row => row.MeetsRequiredBev)
            .OrderByDescending(row => row.MeetsRequiredBev)
            .ThenByDescending(row => row.BevScore)
            .ThenByDescending(row => row.Beverage.Price)
            .ThenBy(row => row.Beverage.Id)
            .Take(Math.Max(1, _config.MaxRareRows.Value));

        DrawRecommendationColumns(
            L("料理推荐", "Recipe recommendations"),
            () => DrawRareRecipeRows(recipeRows),
            L("酒水推荐", "Beverage recommendations"),
            () => DrawRareBeverageRows(beverageRows));
    }

    private int GetRareRecipeResourcePressure(RareRecipeResult row, RecommendationState state)
    {
        if (_repository == null) return row.BaseCost + row.ExtraCost;

        var basePressure = row.Recipe.Ingredients.Sum(name =>
            _repository.IngredientsByName.TryGetValue(name, out var ingredient)
                ? GetIngredientResourcePressure(ingredient, state)
                : 0);
        var extraPressure = row.ExtraIngredients.Sum(ingredient => GetIngredientResourcePressure(ingredient, state));

        return basePressure + extraPressure * 2;
    }

    private static int GetIngredientResourcePressure(Ingredient ingredient, RecommendationState state)
    {
        const int lowStockThreshold = 5;
        var qty = state.OwnedIngredientQty.TryGetValue(ingredient.Id, out var ownedQty)
            ? Math.Max(0, ownedQty)
            : 0;
        var stockPenalty = qty <= 0
            ? (lowStockThreshold + 1) * 100
            : Math.Max(0, lowStockThreshold + 1 - qty) * 100;

        return stockPenalty + ingredient.Price;
    }

    private void DrawRecommendationColumns(string leftTitle, Action drawLeft, string rightTitle, Action drawRight)
    {
        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        try
        {
            GUILayout.BeginVertical();
            try
            {
                DrawSection(leftTitle);
                drawLeft();
            }
            finally
            {
                GUILayout.EndVertical();
            }

            GUILayout.Space(14);

            GUILayout.BeginVertical();
            try
            {
                DrawSection(rightTitle);
                drawRight();
            }
            finally
            {
                GUILayout.EndVertical();
            }
        }
        finally
        {
            GUILayout.EndHorizontal();
        }
    }

    private void DrawNormalRecipeRows(IEnumerable<NormalRecipeResult> rows)
    {
        foreach (var row in rows)
        {
            GUILayout.Label(
                $"{row.Recipe.Name} | {L("分数", "score")} {row.TotalCoverage} | {L("成本", "cost")} {row.IngredientCost} | {L("利润", "profit")} {row.Profit}");
            DrawRecipeMetadata(row.Recipe);
            if (row.MatchedTags.Count > 0)
            {
                GUILayout.Label($"Tag: {string.Join(", ", row.MatchedTags)}", _mutedStyle ?? GUI.skin.label);
            }

            GUILayout.Space(4);
        }
    }

    private void DrawNormalBeverageRows(IEnumerable<NormalBeverageResult> rows)
    {
        foreach (var row in rows)
        {
            GUILayout.Label(
                $"{row.Beverage.Name} | {L("分数", "score")} {row.TotalCoverage} | {L("价格", "price")} {row.Beverage.Price}");
            if (row.MatchedTags.Count > 0)
            {
                GUILayout.Label($"Tag: {string.Join(", ", row.MatchedTags)}", _mutedStyle ?? GUI.skin.label);
            }

            GUILayout.Space(4);
        }
    }

    private void DrawRareRecipeRows(IEnumerable<RareRecipeResult> rows)
    {
        foreach (var row in rows)
        {
            var extras = row.ExtraIngredients.Count == 0
                ? L("不加料", "no extra")
                : "+" + string.Join(", +", row.ExtraIngredients.Select(i => i.Name));
            var totalCost = row.BaseCost + row.ExtraCost;
            GUILayout.Label(
                $"{row.Recipe.Name} ({FormatRating(row.Rating)}) | {L("分数", "score")} {row.FoodScore} | {L("成本", "cost")} {totalCost} | {extras}");
            DrawRecipeMetadata(row.Recipe);
            GUILayout.Space(4);
        }
    }

    private void DrawRareBeverageRows(IEnumerable<RareBeverageResult> rows)
    {
        foreach (var row in rows)
        {
            GUILayout.Label(
                $"{row.Beverage.Name} | {L("分数", "score")} {row.BevScore} | {L("满足点单", "required")} {(row.MeetsRequiredBev ? L("是", "yes") : L("否", "no"))} | {L("价格", "price")} {row.Beverage.Price}");
            if (row.MatchedTags.Count > 0)
            {
                GUILayout.Label($"Tag: {string.Join(", ", row.MatchedTags)}", _mutedStyle ?? GUI.skin.label);
            }

            GUILayout.Space(4);
        }
    }

    private void DrawRecipeMetadata(Recipe recipe)
    {
        GUILayout.Label($"{L("厨具", "cooker")}: {FormatCooker(recipe)}", _mutedStyle ?? GUI.skin.label);
        GUILayout.Label($"{L("基础配方", "base recipe")}: {FormatBaseRecipe(recipe)}", _mutedStyle ?? GUI.skin.label);
    }

    private string FormatCooker(Recipe recipe)
    {
        return string.IsNullOrWhiteSpace(recipe.Cooker) ? L("未知", "unknown") : recipe.Cooker;
    }

    private string FormatBaseRecipe(Recipe recipe)
    {
        return recipe.Ingredients.Count == 0 ? L("无", "none") : string.Join(", ", recipe.Ingredients);
    }

    private static string FormatControllerKey(KeyCode key)
    {
        return key == KeyCode.JoystickButton9 ? "RS Click (JoystickButton9)" : key.ToString();
    }

    private RareRecommendationCache GetRareRecommendationCache(
        RareCustomer customer,
        string requiredFoodTag,
        string requiredBeverageTag,
        RecommendationState state)
    {
        if (_rareService == null || _config == null) return new RareRecommendationCache { State = state };

        var maxExtraIngredients = Math.Clamp(_config.MaxExtraIngredients.Value, 0, 4);
        if (_rareRecommendationCache != null
            && _rareRecommendationCache.Version == _recommendationCacheVersion
            && _rareRecommendationCache.CustomerId == customer.Id
            && string.Equals(_rareRecommendationCache.RequiredFoodTag, requiredFoodTag, StringComparison.Ordinal)
            && string.Equals(_rareRecommendationCache.RequiredBeverageTag, requiredBeverageTag, StringComparison.Ordinal)
            && _rareRecommendationCache.MaxExtraIngredients == maxExtraIngredients
            && ReferenceEquals(_rareRecommendationCache.State, state))
        {
            return _rareRecommendationCache;
        }

        _rareRecommendationCache = new RareRecommendationCache
        {
            Version = _recommendationCacheVersion,
            CustomerId = customer.Id,
            RequiredFoodTag = requiredFoodTag,
            RequiredBeverageTag = requiredBeverageTag,
            MaxExtraIngredients = maxExtraIngredients,
            State = state,
            Recipes = _rareService.RankRecipes(customer, requiredFoodTag, state, null, maxExtraIngredients),
            Beverages = _rareService.RankBeverages(customer, requiredBeverageTag, state),
        };

        return _rareRecommendationCache;
    }

    private void DrawNoRuntimeLoaded()
    {
        GUILayout.Space(8);
        GUILayout.Label(L(
            "当前游戏运行时数据不可用。进入游戏并加载进度后，推荐会自动刷新。",
            "Live game runtime data is unavailable. Recommendations will refresh after entering the game and loading progress."));
        GUILayout.Label($"{L("当前场景", "Active scene")}: {FormatSceneName()}");
    }

    private void DrawPlaceSelector()
    {
        _placeIndex = Math.Clamp(_placeIndex, 0, PlaceNames.All.Length - 1);
        var previousPlaceIndex = _placeIndex;
        _placeIndex = DrawDropdown(L("地区", "Region"), _placeIndex, PlaceNames.All, "place-selector");
        if (_placeIndex != previousPlaceIndex)
        {
            _rareCustomerIndex = 0;
            _requiredFoodTag = "";
            _requiredBeverageTag = "";
            _previousRareCustomerId = -1;
        }
    }

    private int DrawDropdown(string label, int selectedIndex, IReadOnlyList<string> options, string id)
    {
        if (options.Count == 0)
        {
            GUILayout.Label($"{label}: {L("无", "none")}", _mutedStyle ?? GUI.skin.label);
            return selectedIndex;
        }

        var nextSelectedIndex = Math.Clamp(selectedIndex, 0, options.Count - 1);
        GUILayout.Label(label, _mutedStyle ?? GUI.skin.label);
        if (GUILayout.Button($"{options[nextSelectedIndex]} v", _buttonStyle ?? GUI.skin.button))
        {
            _openDropdownId = string.Equals(_openDropdownId, id, StringComparison.Ordinal) ? "" : id;
        }

        if (!string.Equals(_openDropdownId, id, StringComparison.Ordinal)) return nextSelectedIndex;

        GUILayout.BeginVertical();
        try
        {
            for (var i = 0; i < options.Count; i++)
            {
                var style = i == nextSelectedIndex
                    ? _buttonActiveStyle ?? GUI.skin.button
                    : _buttonStyle ?? GUI.skin.button;
                if (!GUILayout.Button(options[i], style)) continue;

                nextSelectedIndex = i;
                _openDropdownId = "";
            }
        }
        finally
        {
            GUILayout.EndVertical();
        }

        return nextSelectedIndex;
    }

    private string DrawStringDropdown(string label, string selectedValue, IReadOnlyList<string> options, string id)
    {
        if (options.Count == 0)
        {
            GUILayout.Label($"{label}: {L("无", "none")}", _mutedStyle ?? GUI.skin.label);
            return "";
        }

        var selectedIndex = options
            .Select((value, index) => (value, index))
            .FirstOrDefault(item => string.Equals(item.value, selectedValue, StringComparison.Ordinal))
            .index;
        if (!options.Contains(selectedValue)) selectedIndex = 0;

        var nextIndex = DrawDropdown(label, selectedIndex, options, id);
        return options[Math.Clamp(nextIndex, 0, options.Count - 1)];
    }

    private int DrawButtonRow(int selectedIndex, IReadOnlyList<string> labels, int columns)
    {
        if (labels.Count == 0) return selectedIndex;

        var safeColumns = Math.Max(1, columns);
        var nextSelectedIndex = Math.Clamp(selectedIndex, 0, labels.Count - 1);

        for (var rowStart = 0; rowStart < labels.Count; rowStart += safeColumns)
        {
            GUILayout.BeginHorizontal();
            try
            {
                var rowEnd = Math.Min(rowStart + safeColumns, labels.Count);
                for (var i = rowStart; i < rowEnd; i++)
                {
                    var style = i == nextSelectedIndex
                        ? _buttonActiveStyle ?? GUI.skin.button
                        : _buttonStyle ?? GUI.skin.button;
                    if (GUILayout.Button(labels[i], style))
                    {
                        nextSelectedIndex = i;
                    }
                }
            }
            finally
            {
                GUILayout.EndHorizontal();
            }
        }

        return nextSelectedIndex;
    }

    private void DrawOpacityControls()
    {
        if (_config == null) return;

        GUILayout.BeginHorizontal();
        try
        {
            GUILayout.Label($"{L("窗口不透明度", "Window opacity")}: {Mathf.Clamp(_config.PanelOpacity.Value, 0.5f, 1f):0.00}", _mutedStyle ?? GUI.skin.label);
            if (GUILayout.Button(L("更透明", "More transparent"))) SetPanelOpacity(_config.PanelOpacity.Value - 0.10f);
            if (GUILayout.Button(L("更不透明", "More opaque"))) SetPanelOpacity(_config.PanelOpacity.Value + 0.10f);
            if (GUILayout.Button(L("重置", "Reset"))) SetPanelOpacity(0.97f);
            if (GUILayout.Button(L("重置窗口", "Reset window"))) ResetWindowLayout();
        }
        finally
        {
            GUILayout.EndHorizontal();
        }
    }

    private void LoadRepository()
    {
        try
        {
            var dataDirectory = DataPathResolver.FindDataDirectory();
            _repository = DataRepository.Load(dataDirectory);
            _state = null;
            _businessFallbackState = null;
            _runtimeLoaded = false;
            _businessContext = null;
            _runtimeSource = "";
            _lastRuntimeErrorMessage = "";
            _runtimeStateSignature = "";
            _lastRuntimeReadUtc = DateTime.MinValue;
            _normalService = new NormalRecommendationService(_repository);
            _rareService = new RareRecommendationService(_repository);
            InvalidateRecommendationCache();
            _status = L("数据已加载，等待游戏运行时数据。", "Data loaded. Waiting for live game runtime data.");
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
                IRecommendationStateProvider runtimeProvider = new RuntimeReflectionRecommendationStateProvider(_repository, includePlacedCookers);
                var previousSource = _runtimeSource;
                var nextRuntimeState = runtimeProvider.LoadState();
                ApplyConfigOverrides(nextRuntimeState);
                var nextRuntimeSignature = BuildRecommendationStateSignature(nextRuntimeState);
                var stateChanged = _state == null
                    || !string.Equals(_runtimeStateSignature, nextRuntimeSignature, StringComparison.Ordinal);
                if (stateChanged)
                {
                    _state = nextRuntimeState;
                    _runtimeStateSignature = nextRuntimeSignature;
                    _businessFallbackState = null;
                    InvalidateRecommendationCache();
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
                InvalidateRecommendationCache();
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
                InvalidateRecommendationCache();
            }

            LogRuntimeError(ex, manual);
        }
        finally
        {
            PublishLocalApiSnapshot();
        }
    }

    private void RefreshBusinessContext(bool manual, bool force = false)
    {
        if (_repository == null || _config == null) return;
        if (!manual && !force && _businessContext != null && Time.realtimeSinceStartup < _nextBusinessRefreshAt) return;

        _nextBusinessRefreshAt = Time.realtimeSinceStartup + Math.Max(1f, _config.AutoRefreshSeconds.Value);

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
                _businessContext = new NightBusinessContext
                {
                    Source = L("当前不在夜晚经营场景。", "Not in a night business scene."),
                };
                ClearPlacedCookersFromCurrentState("not in night business scene");
                RuntimeCookerHighlightService.Clear();
                if (manual) _status = L("当前无经营场景。", "No active business scene.");
                return;
            }

            var provider = new NightBusinessReflectionProvider(
                _repository,
                CreateNightBusinessDiagnostics(),
                _activeSceneName,
                _config.EnableSpecialOrderLogFallback.Value);
            _businessContext = provider.LoadContext();
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
                PrepareOrderFromLocalApi,
                CompleteOrderFromLocalApi,
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

    private string FormatLocalApiStatus()
    {
        if (_config == null) return L("未初始化", "not initialized");
        if (!_config.LocalApiEnabled.Value) return L("已关闭", "disabled");
        return _localApiServer == null
            ? L("未启动", "not running")
            : $"{_localApiServer.BaseUrl}/snapshot";
    }

    private void PublishLocalApiSnapshot()
    {
        if (_localApiServer == null) return;

        try
        {
            var publishedState = _state ?? (_businessContext?.Orders.Count > 0 ? GetBusinessRecommendationState() : null);
            var snapshot = new LocalApiSnapshot
            {
                PluginVersion = MystiaStewardCompanionPlugin.PluginVersion,
                CapturedAtUtc = DateTime.UtcNow,
                ActiveSceneName = _activeSceneName,
                RuntimeLoaded = _runtimeLoaded,
                Status = _status,
                RuntimeSource = _runtimeSource,
                DataDirectory = _repository?.DataDirectory ?? "",
                RuntimeUiPinningStatus = RuntimeUiPinningService.Status,
                RecommendationState = publishedState == null ? null : RecommendationStateSnapshot.From(publishedState),
                NightBusiness = _businessContext,
                RuntimeRareCustomers = _repository == null
                    ? new List<RuntimeRareCustomer>()
                    : new RuntimeMappedGuestCatalog(_repository).Snapshot().RuntimeRareCustomers.ToList(),
            };
            _localApiServer.SetSnapshotJson(JsonSerializer.Serialize(snapshot, LocalApiJsonOptions));
            _localApiSnapshotErrorLogged = false;
        }
        catch (Exception ex)
        {
            if (_localApiSnapshotErrorLogged) return;
            _localApiSnapshotErrorLogged = true;
            _log?.LogWarning($"Local API snapshot publish failed: {ex.Message}");
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
        };
    }

    private void UpdateLocalApiLogSettings(bool? exposeLogs, bool? diagnostics)
    {
        if (_config == null) return;
        if (exposeLogs.HasValue) _config.ExposeLocalApiLogs.Value = exposeLogs.Value;
        if (diagnostics.HasValue) _config.EnableNightBusinessDiagnostics.Value = diagnostics.Value;
    }

    private string OpenLocalApiLogFolder(string target)
    {
        var settings = GetLocalApiLogSettings();
        var path = string.Equals(target, "diagnostics", StringComparison.OrdinalIgnoreCase)
            ? settings.NightBusinessDiagnosticsPath
            : settings.LogOutputPath;
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

    private OrderPreparationResult PrepareOrderFromLocalApi(OrderPreparationRequest request)
    {
        return RunOrderActionFromLocalApi(request, completeOrder: false);
    }

    private OrderPreparationResult CompleteOrderFromLocalApi(OrderPreparationRequest request)
    {
        return RunOrderActionFromLocalApi(request, completeOrder: true);
    }

    private OrderPreparationResult RunOrderActionFromLocalApi(OrderPreparationRequest request, bool completeOrder)
    {
        if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
        {
            var pending = new PendingOrderPreparation
            {
                Request = request,
                CompleteOrder = completeOrder,
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

        return completeOrder ? ApplyOrderCompletion(request) : ApplyOrderPreparation(request);
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
                pending.Result = pending.CompleteOrder
                    ? ApplyOrderCompletion(pending.Request)
                    : ApplyOrderPreparation(pending.Request);
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

    private void ProcessPendingCookingCollections()
    {
        foreach (var message in RuntimeOrderPreparationService.ProcessPendingCookingCollections())
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

    private void DrawNightBusinessDiagnosticsSettings()
    {
        if (_config == null) return;

        GUILayout.Space(8);
        GUILayout.Label(
            $"{L("经营诊断日志", "Night business diagnostics")}: {(_config.EnableNightBusinessDiagnostics.Value ? L("开启", "on") : L("关闭", "off"))}");
        GUILayout.Label(
            $"{L("点单日志兜底", "Order log fallback")}: {(_config.EnableSpecialOrderLogFallback.Value ? L("开启", "on") : L("关闭", "off"))}");
        GUILayout.Label(
            $"{L("伴随窗口日志读取", "Companion log access")}: {(_config.ExposeLocalApiLogs.Value ? L("开启", "on") : L("关闭", "off"))}");
        GUILayout.Label(
            $"{L("诊断日志路径", "Diagnostics path")}: {NightBusinessDiagnosticSink.ResolvePath(_config.NightBusinessDiagnosticsPath.Value)}",
            _mutedStyle ?? GUI.skin.label);
        GUILayout.Label(
            $"{L("BepInEx 日志路径", "BepInEx log path")}: {LocalApiServer.ResolveLogOutputPath()}",
            _mutedStyle ?? GUI.skin.label);
        GUILayout.Label(
            $"{L("日志读取上限", "Log read limit")}: {Math.Clamp(_config.LocalApiMaxLogLines.Value, 50, 2000)} lines / {Math.Clamp(_config.LocalApiMaxLogBytes.Value, 16 * 1024, 2 * 1024 * 1024) / 1024} KiB",
            _mutedStyle ?? GUI.skin.label);
        GUILayout.BeginHorizontal();
        try
        {
            if (GUILayout.Button(_config.ExposeLocalApiLogs.Value
                    ? L("关闭日志读取", "Disable log access")
                    : L("开启日志读取", "Enable log access")))
            {
                _config.ExposeLocalApiLogs.Value = !_config.ExposeLocalApiLogs.Value;
                _status = _config.ExposeLocalApiLogs.Value
                    ? L("伴随窗口日志读取已开启。", "Companion log access enabled.")
                    : L("伴随窗口日志读取已关闭。", "Companion log access disabled.");
            }

            if (GUILayout.Button(_config.EnableNightBusinessDiagnostics.Value
                    ? L("关闭诊断", "Disable diagnostics")
                    : L("开启诊断", "Enable diagnostics")))
            {
                _config.EnableNightBusinessDiagnostics.Value = !_config.EnableNightBusinessDiagnostics.Value;
                _status = _config.EnableNightBusinessDiagnostics.Value
                    ? L("经营诊断日志已开启。", "Night business diagnostics enabled.")
                    : L("经营诊断日志已关闭。", "Night business diagnostics disabled.");
            }

            if (GUILayout.Button(L("清空诊断日志", "Clear diagnostics log")))
            {
                NightBusinessDiagnosticSink.Clear(_config.NightBusinessDiagnosticsPath.Value);
                _status = L("经营诊断日志已清空。", "Night business diagnostics log cleared.");
            }
        }
        finally
        {
            GUILayout.EndHorizontal();
        }

        GUILayout.BeginHorizontal();
        try
        {
            if (GUILayout.Button(L("打开日志目录", "Open log folder")))
            {
                var directory = OpenLocalApiLogFolder("log");
                _status = L($"已打开日志目录：{directory}", $"Log folder opened: {directory}");
            }

            if (GUILayout.Button(L("打开诊断目录", "Open diagnostics folder")))
            {
                var directory = OpenLocalApiLogFolder("diagnostics");
                _status = L($"已打开诊断目录：{directory}", $"Diagnostics folder opened: {directory}");
            }
        }
        catch (Exception ex)
        {
            _status = L($"打开目录失败：{ex.Message}", $"Failed to open folder: {ex.Message}");
            _log?.LogWarning($"Open log folder failed: {ex.Message}");
        }
        finally
        {
            GUILayout.EndHorizontal();
        }

        GUILayout.BeginHorizontal();
        try
        {
            if (GUILayout.Button(_config.EnableSpecialOrderLogFallback.Value
                    ? L("关闭日志兜底", "Disable log fallback")
                    : L("开启日志兜底", "Enable log fallback")))
            {
                _config.EnableSpecialOrderLogFallback.Value = !_config.EnableSpecialOrderLogFallback.Value;
                _status = _config.EnableSpecialOrderLogFallback.Value
                    ? L("点单日志兜底已开启，需重启游戏后挂载日志监听。", "Order log fallback enabled; restart the game to attach the log listener.")
                    : L("点单日志兜底已关闭。", "Order log fallback disabled.");
            }
        }
        finally
        {
            GUILayout.EndHorizontal();
        }
    }

    private void ClearLoadedRuntime(string status)
    {
        _state = null;
        _runtimeLoaded = false;
        _businessContext = new NightBusinessContext
        {
            Error = status,
        };
        _runtimeSource = "";
        _runtimeStateSignature = "";
        _lastRuntimeReadUtc = DateTime.MinValue;
        RuntimeCookerHighlightService.Clear();
        InvalidateRecommendationCache();
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
        InvalidateRecommendationCache();
    }

    private static bool HasActiveNightBusinessContext(NightBusinessContext? context)
    {
        if (context == null) return false;
        if (string.IsNullOrWhiteSpace(context.Place) && string.IsNullOrWhiteSpace(context.PlaceLabel)) return false;
        if (context.Source.Contains("not in", StringComparison.OrdinalIgnoreCase)
            || context.Source.Contains("不在", StringComparison.OrdinalIgnoreCase))
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
            InvalidateRecommendationCache();
        }

        return _businessFallbackState;
    }

    private void InvalidateRecommendationCache()
    {
        _recommendationCacheVersion++;
        _rareRecommendationCache = null;
    }

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

    private void SetPanelOpacity(float value)
    {
        if (_config == null) return;
        _config.PanelOpacity.Value = Mathf.Clamp(value, 0.5f, 1f);
        _status = L(
            $"窗口不透明度已设为 {_config.PanelOpacity.Value:0.00}。",
            $"Window opacity set to {_config.PanelOpacity.Value:0.00}.");
    }

    private void SetOverlayVisible(bool visible)
    {
        if (_visible == visible) return;

        _visible = visible;
        if (_visible)
        {
            EnsureWindowInsideScreen();
            EnsureOverlayCursor();
            RefreshRuntimeState(false);
            RefreshBusinessContext(false);
        }
        else
        {
            RestoreCursorState();
        }
    }

    private void HandleWindowInteraction(Event? guiEvent)
    {
        if (guiEvent == null) return;

        var mouse = guiEvent.mousePosition;
        switch (guiEvent.type)
        {
            case EventType.MouseDown when guiEvent.button == 0 && GetResizeHandleRect().Contains(mouse):
                _resizingWindow = true;
                _draggingWindow = false;
                _resizeStartMouse = mouse;
                _resizeStartRect = _windowRect;
                guiEvent.Use();
                break;

            case EventType.MouseDown when guiEvent.button == 0 && GetHeaderDragRect().Contains(mouse):
                _draggingWindow = true;
                _resizingWindow = false;
                _windowDragOffset = new Vector2(mouse.x - _windowRect.x, mouse.y - _windowRect.y);
                guiEvent.Use();
                break;

            case EventType.MouseDrag when _draggingWindow:
                _windowRect.x = mouse.x - _windowDragOffset.x;
                _windowRect.y = mouse.y - _windowDragOffset.y;
                EnsureWindowInsideScreen();
                guiEvent.Use();
                break;

            case EventType.MouseDrag when _resizingWindow:
                _windowRect.width = _resizeStartRect.width + mouse.x - _resizeStartMouse.x;
                _windowRect.height = _resizeStartRect.height + mouse.y - _resizeStartMouse.y;
                EnsureWindowInsideScreen();
                guiEvent.Use();
                break;

            case EventType.MouseUp:
                if (_draggingWindow || _resizingWindow)
                {
                    _draggingWindow = false;
                    _resizingWindow = false;
                    EnsureWindowInsideScreen();
                    PersistWindowLayout();
                    guiEvent.Use();
                }

                break;
        }
    }

    private Rect GetHeaderDragRect()
    {
        return new Rect(_windowRect.x, _windowRect.y, Math.Max(0f, _windowRect.width - 96f), WindowHeaderHeight);
    }

    private Rect GetResizeHandleRect()
    {
        return new Rect(
            _windowRect.xMax - ResizeHandleSize,
            _windowRect.yMax - ResizeHandleSize,
            ResizeHandleSize,
            ResizeHandleSize);
    }

    private void ResetWindowLayout()
    {
        _windowRect = new Rect(48f, 48f, 980f, 680f);
        EnsureWindowInsideScreen();
        PersistWindowLayout();
        _status = L("窗口位置和大小已重置。", "Window position and size reset.");
    }

    private void PersistWindowLayout()
    {
        if (_config == null) return;
        _config.WindowX.Value = _windowRect.x;
        _config.WindowY.Value = _windowRect.y;
        _config.WindowWidth.Value = _windowRect.width;
        _config.WindowHeight.Value = _windowRect.height;
    }

    private void EnsureWindowInsideScreen()
    {
        var width = Mathf.Clamp(_windowRect.width, MinWindowWidth, Math.Max(MinWindowWidth, Screen.width - WindowMargin * 2f));
        var height = Mathf.Clamp(_windowRect.height, MinWindowHeight, Math.Max(MinWindowHeight, Screen.height - WindowMargin * 2f));
        _windowRect.width = width;
        _windowRect.height = height;
        _windowRect.x = Mathf.Clamp(_windowRect.x, WindowMargin, Math.Max(WindowMargin, Screen.width - width - WindowMargin));
        _windowRect.y = Mathf.Clamp(_windowRect.y, WindowMargin, Math.Max(WindowMargin, Screen.height - height - WindowMargin));
    }

    private void EnsureOverlayCursor()
    {
        if (!_cursorStateCaptured)
        {
            _previousCursorVisible = Cursor.visible;
            _previousCursorLockState = Cursor.lockState;
            _cursorStateCaptured = true;
        }

        if (!Cursor.visible) Cursor.visible = true;
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
    }

    private void RestoreCursorState()
    {
        if (!_cursorStateCaptured) return;

        Cursor.visible = _previousCursorVisible;
        Cursor.lockState = _previousCursorLockState;
        _cursorStateCaptured = false;
    }

    private void EnsureStyles()
    {
        if (_stylesInitialized) return;

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            normal = { textColor = new Color(0.96f, 0.97f, 0.98f) },
        };
        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            wordWrap = true,
            normal = { textColor = new Color(0.88f, 0.90f, 0.94f) },
        };
        _mutedStyle = new GUIStyle(_labelStyle ?? GUI.skin.label)
        {
            normal = { textColor = new Color(0.63f, 0.67f, 0.74f) },
        };
        _sectionStyle = new GUIStyle(_labelStyle ?? GUI.skin.label)
        {
            fontSize = 14,
            normal = { textColor = new Color(0.99f, 0.86f, 0.55f) },
        };
        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 13,
            padding = CreateOffset(10, 10, 5, 5),
            margin = CreateOffset(3, 3, 3, 3),
            normal = { textColor = new Color(0.88f, 0.91f, 0.96f) },
            hover = { textColor = Color.white },
            active = { textColor = Color.white },
        };
        _buttonActiveStyle = new GUIStyle(_buttonStyle ?? GUI.skin.button)
        {
            normal = { textColor = new Color(1f, 0.92f, 0.68f) },
            hover = { textColor = new Color(1f, 0.95f, 0.76f) },
        };
        _stylesInitialized = true;
    }

    private static RectOffset CreateOffset(int left, int right, int top, int bottom)
    {
        var offset = new RectOffset();
        offset.left = left;
        offset.right = right;
        offset.top = top;
        offset.bottom = bottom;
        return offset;
    }

    private void DrawPanelChrome()
    {
        EnsureWindowInsideScreen();
        var previousColor = GUI.color;
        try
        {
            var opacity = Mathf.Clamp(_config?.PanelOpacity.Value ?? 0.97f, 0.5f, 1f);
            GUI.color = new Color(0.025f, 0.028f, 0.036f, opacity);
            GUI.DrawTexture(_windowRect, Texture2D.whiteTexture);

            var headerRect = new Rect(_windowRect.x, _windowRect.y, _windowRect.width, WindowHeaderHeight);
            GUI.color = new Color(0.07f, 0.08f, 0.10f, opacity);
            GUI.DrawTexture(headerRect, Texture2D.whiteTexture);

            GUI.color = new Color(0.97f, 0.72f, 0.28f, 1f);
            GUI.DrawTexture(new Rect(_windowRect.x, _windowRect.y + WindowHeaderHeight - 1f, _windowRect.width, 2), Texture2D.whiteTexture);

            GUI.color = Color.white;
            GUI.Label(new Rect(_windowRect.x + 18, _windowRect.y + 9, 260, 24), "mystia-steward-companion", _titleStyle ?? GUI.skin.label);
            GUI.Label(
                new Rect(_windowRect.x + 18, _windowRect.y + 31, _windowRect.width - 150, 18),
                _status,
                _mutedStyle ?? GUI.skin.label);

            if (GUI.Button(new Rect(_windowRect.xMax - 82, _windowRect.y + 12, 28, 26), "R", _buttonStyle ?? GUI.skin.button))
            {
                ResetWindowLayout();
            }

            if (GUI.Button(new Rect(_windowRect.xMax - 46, _windowRect.y + 12, 28, 26), "X", _buttonStyle ?? GUI.skin.button))
            {
                SetOverlayVisible(false);
            }

            DrawResizeHandle();
        }
        finally
        {
            GUI.color = previousColor;
        }
    }

    private void DrawResizeHandle()
    {
        var handle = GetResizeHandleRect();
        GUI.color = new Color(0.97f, 0.72f, 0.28f, 0.9f);
        GUI.DrawTexture(new Rect(handle.xMax - 17, handle.yMax - 5, 13, 2), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(handle.xMax - 12, handle.yMax - 10, 8, 2), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(handle.xMax - 7, handle.yMax - 15, 3, 2), Texture2D.whiteTexture);
    }

    private void DrawSection(string title)
    {
        GUILayout.Space(10);
        GUILayout.Label(title, _sectionStyle ?? GUI.skin.label);
    }

    private void DrawOverlayCursor()
    {
        var mouse = Input.mousePosition;
        var position = new Vector2(mouse.x, Screen.height - mouse.y);
        var previousColor = GUI.color;
        var previousDepth = GUI.depth;
        try
        {
            GUI.depth = -10001;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(position.x + 1, position.y + 1, 14, 2), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(position.x + 1, position.y + 1, 2, 14), Texture2D.whiteTexture);
            GUI.color = new Color(1f, 0.88f, 0.40f, 1f);
            GUI.DrawTexture(new Rect(position.x, position.y, 14, 2), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(position.x, position.y, 2, 14), Texture2D.whiteTexture);
        }
        finally
        {
            GUI.depth = previousDepth;
            GUI.color = previousColor;
        }
    }

    private void BlockGameInputIfPointerOverPanel()
    {
        if (!IsPointerOverPanel()) return;

        try
        {
            Input.ResetInputAxes();
        }
        catch (Exception ex)
        {
            if (_inputResetUnsupportedLogged) return;
            _inputResetUnsupportedLogged = true;
            _log?.LogWarning($"Input.ResetInputAxes failed; overlay clicks may still reach the game: {ex.Message}");
        }
    }

    private bool ShouldConsumeEvent(Event guiEvent)
    {
        switch (guiEvent.type)
        {
            case EventType.MouseDown:
            case EventType.MouseUp:
            case EventType.MouseDrag:
            case EventType.ScrollWheel:
                return _windowRect.Contains(guiEvent.mousePosition);
            case EventType.KeyDown:
            case EventType.KeyUp:
                return true;
            default:
                return false;
        }
    }

    private bool IsPointerOverPanel()
    {
        var mouse = Input.mousePosition;
        var guiPosition = new Vector2(mouse.x, Screen.height - mouse.y);
        return _windowRect.Contains(guiPosition);
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

    private string FormatCurrentSource()
    {
        return _runtimeLoaded && !string.IsNullOrWhiteSpace(_runtimeSource)
            ? FormatSourceDescription(_runtimeSource)
            : L("未加载", "Not loaded");
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

    private string FormatSceneName()
    {
        return string.IsNullOrWhiteSpace(_activeSceneName)
            ? L("未知", "unknown")
            : _activeSceneName;
    }

    private static int FormatDeskCode(int deskCode)
    {
        return deskCode >= 0 ? deskCode + 1 : deskCode;
    }

    private string FormatRating(Rating rating)
    {
        return rating switch
        {
            Rating.ExGood => L("极好", "ExGood"),
            Rating.Good => L("好", "Good"),
            Rating.Normal => L("普通", "Normal"),
            Rating.Bad => L("差", "Bad"),
            Rating.ExBad => L("极差", "ExBad"),
            _ => rating.ToString(),
        };
    }

    private string L(string zh, string en)
    {
        return IsChinese() ? zh : en;
    }

    private bool IsChinese()
    {
        var language = _config?.Language.Value;
        return string.IsNullOrWhiteSpace(language) || language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }
}
