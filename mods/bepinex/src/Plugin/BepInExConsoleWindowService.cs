namespace MystiaStewardCompanion.Plugin;

/// <summary>控制台窗口的最小平台能力接口；实现负责实际窗口操作及状态读取。</summary>
internal interface IBepInExConsoleRuntime
{
    bool IsSupported { get; }
    bool IsActive { get; }
    nint WindowHandle { get; }
    bool IsVisible { get; }

    void Create();
    void DisableCloseCommand();
    void EnsureLogListener();
    void ShowWithoutActivation();
    void Hide();
}

/// <summary>一次操作的结果快照；配置可见性与实际窗口可见性分别返回，避免混淆保存意图和执行结果。</summary>
internal sealed class BepInExConsoleWindowState
{
    public bool Ok { get; init; }
    public bool Supported { get; init; }
    public bool ConfiguredVisible { get; init; }
    public bool Active { get; init; }
    public bool Visible { get; init; }
    public string Status { get; init; } = "";
    public string? Error { get; init; }
}

/// <summary>串行协调控制台窗口状态和持久化偏好，并在失败时尽力恢复操作前的状态。</summary>
internal sealed class BepInExConsoleWindowService
{
    private readonly object _gate = new();
    private readonly IBepInExConsoleRuntime _runtime;
    private Func<bool>? _readConfiguredVisibility;
    private Action<bool>? _writeConfiguredVisibility;
    private nint _pendingCloseProtectionWindow;

    public BepInExConsoleWindowService(IBepInExConsoleRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>绑定一次偏好读写委托，防止运行中更换存储来源导致回滚写入不同配置。</summary>
    public void ConfigurePreference(
        Func<bool> readConfiguredVisibility,
        Action<bool> writeConfiguredVisibility)
    {
        ArgumentNullException.ThrowIfNull(readConfiguredVisibility);
        ArgumentNullException.ThrowIfNull(writeConfiguredVisibility);

        lock (_gate)
        {
            if (_readConfiguredVisibility != null || _writeConfiguredVisibility != null)
            {
                throw new InvalidOperationException(
                    "BepInEx console visibility preference is already configured.");
            }

            _readConfiguredVisibility = readConfiguredVisibility;
            _writeConfiguredVisibility = writeConfiguredVisibility;
        }
    }

    public BepInExConsoleWindowState ReadState()
    {
        lock (_gate)
        {
            return ReadStateCore(ok: true, error: null);
        }
    }

    /// <summary>先操作并读回窗口状态，再保存偏好；任一步失败均返回失败及可观测的回滚错误。</summary>
    /// <remarks>回滚只恢复可见性及偏好，不销毁本次新建的控制台，也不移除日志监听器。</remarks>
    public BepInExConsoleWindowState ApplyVisibility(bool visible)
    {
        lock (_gate)
        {
            if (!_runtime.IsSupported)
            {
                return visible
                    ? ReadStateCore(ok: false, error: "BepInEx console window control is only available on Windows.")
                    : ReadStateCore(ok: true, error: null);
            }

            var previousActive = false;
            var previousWindow = (nint)0;
            var previousVisible = false;
            var previousConfiguredVisible = false;
            var configuredVisibilityCaptured = false;
            var runtimeStateCaptured = false;
            var configuredVisibilityMutationAttempted = false;
            var runtimeStateMutationAttempted = false;
            try
            {
                previousConfiguredVisible = ReadConfiguredVisibilityCore();
                configuredVisibilityCaptured = true;
                previousActive = _runtime.IsActive;
                previousWindow = _runtime.WindowHandle;
                previousVisible = _runtime.IsVisible;
                runtimeStateCaptured = true;

                if (visible)
                {
                    EnsurePendingCloseProtection();
                    if (!previousActive)
                    {
                        runtimeStateMutationAttempted = true;
                        try
                        {
                            _runtime.Create();
                        }
                        catch (Exception createException)
                        {
                            var protectionException = TryProtectNewWindow(previousWindow);
                            if (protectionException != null)
                            {
                                throw new InvalidOperationException(
                                    $"{createException.Message}; close protection failed: "
                                    + protectionException.Message,
                                    new AggregateException(createException, protectionException));
                            }

                            throw;
                        }

                        var protectionFailure = TryProtectNewWindow(previousWindow);
                        if (protectionFailure != null)
                        {
                            throw new InvalidOperationException(
                                $"Could not protect the Mod-created BepInEx console close command: "
                                + protectionFailure.Message,
                                protectionFailure);
                        }
                    }

                    if (!_runtime.IsActive)
                    {
                        throw new InvalidOperationException("BepInEx did not activate a console after the create request.");
                    }

                    _runtime.EnsureLogListener();
                    if (!_runtime.IsVisible)
                    {
                        runtimeStateMutationAttempted = true;
                        _runtime.ShowWithoutActivation();
                    }

                    if (!_runtime.IsVisible)
                    {
                        throw new InvalidOperationException("The BepInEx console window did not become visible.");
                    }
                }
                else if (_runtime.IsVisible)
                {
                    runtimeStateMutationAttempted = true;
                    _runtime.Hide();
                    if (_runtime.IsVisible)
                    {
                        throw new InvalidOperationException("The BepInEx console window did not become hidden.");
                    }
                }

                configuredVisibilityMutationAttempted =
                    _writeConfiguredVisibility != null;
                _writeConfiguredVisibility?.Invoke(visible);
                return ReadStateCore(ok: true, error: null);
            }
            catch (Exception ex)
            {
                var rollbackError = TryRestorePreviousState(
                    previousActive,
                    previousVisible,
                    previousConfiguredVisible,
                    restoreRuntimeState: runtimeStateCaptured && runtimeStateMutationAttempted,
                    restoreConfiguredVisibility:
                        configuredVisibilityCaptured && configuredVisibilityMutationAttempted);
                return ReadStateCore(
                    ok: false,
                    error: rollbackError == null
                        ? ex.Message
                        : $"{ex.Message}; rollback failed: {rollbackError}");
            }
        }
    }

    private string? TryRestorePreviousState(
        bool previousActive,
        bool previousVisible,
        bool previousConfiguredVisible,
        bool restoreRuntimeState,
        bool restoreConfiguredVisibility)
    {
        var failures = new List<string>();
        if (restoreConfiguredVisibility)
        {
            try
            {
                _writeConfiguredVisibility?.Invoke(previousConfiguredVisible);
            }
            catch (Exception ex)
            {
                failures.Add($"preference: {ex.Message}");
            }
        }

        if (restoreRuntimeState)
        {
            try
            {
                if (previousVisible)
                {
                    if (!_runtime.IsVisible)
                    {
                        _runtime.ShowWithoutActivation();
                    }
                }
                else if (_runtime.IsVisible)
                {
                    _runtime.Hide();
                }

                if (previousActive && !_runtime.IsActive)
                {
                    throw new InvalidOperationException(
                        "The previous active console could not be restored.");
                }

                if (_runtime.IsVisible != previousVisible)
                {
                    throw new InvalidOperationException(
                        "The previous console visibility could not be restored.");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"window: {ex.Message}");
            }
        }

        return failures.Count == 0
            ? null
            : string.Join("; ", failures);
    }

    private void EnsurePendingCloseProtection()
    {
        if (_pendingCloseProtectionWindow == 0) return;

        var currentWindow = _runtime.WindowHandle;
        if (currentWindow != _pendingCloseProtectionWindow)
        {
            _pendingCloseProtectionWindow = 0;
            return;
        }

        _runtime.DisableCloseCommand();
        _pendingCloseProtectionWindow = 0;
    }

    // 仅保护本服务从“无句柄”状态创建的窗口，不改动调用前已存在控制台的关闭策略。
    // 保护失败保留精确句柄；后续显示请求仅在该窗口仍存在时重试。
    private Exception? TryProtectNewWindow(nint previousWindow)
    {
        try
        {
            var currentWindow = _runtime.WindowHandle;
            if (previousWindow != 0 || currentWindow == 0)
            {
                return null;
            }

            _pendingCloseProtectionWindow = currentWindow;
            _runtime.DisableCloseCommand();
            _pendingCloseProtectionWindow = 0;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private BepInExConsoleWindowState ReadStateCore(bool ok, string? error)
    {
        try
        {
            var supported = _runtime.IsSupported;
            var active = supported && _runtime.IsActive;
            var visible = supported && _runtime.IsVisible;
            var configuredVisible = ReadConfiguredVisibilityCore();
            return new BepInExConsoleWindowState
            {
                Ok = ok,
                Supported = supported,
                ConfiguredVisible = configuredVisible,
                Active = active,
                Visible = visible,
                Status = !supported
                    ? "unsupported-platform"
                    : visible
                        ? "visible"
                        : active
                            ? "hidden"
                            : "inactive",
                Error = error,
            };
        }
        catch (Exception ex)
        {
            return new BepInExConsoleWindowState
            {
                Ok = false,
                Supported = false,
                ConfiguredVisible = false,
                Active = false,
                Visible = false,
                Status = "state-read-failed",
                Error = error ?? ex.Message,
            };
        }
    }

    private bool ReadConfiguredVisibilityCore()
    {
        return _readConfiguredVisibility?.Invoke() ?? false;
    }
}
