namespace MystiaStewardCompanion.Plugin;

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
