using MystiaStewardCompanion.Plugin;

try
{
    VerifyUnsupportedPlatformFailsClosed();
    VerifyCreateShowAndListenerSequence();
    VerifyExistingConsoleIsReused();
    VerifyInactiveExistingWindowIsNotReclassified();
    VerifyHideNeverDetaches();
    VerifyTransitionFailuresAreReported();
    VerifyCloseProtectionFailureBlocksShowUntilRetry();
    VerifyPreferenceFailureRestoresBothDirections();
    VerifyUncapturedStateDoesNotRewritePreference();
    VerifyConcurrentOppositeRequestsStayConsistent();
    VerifyProductionSourceContract();
    Console.WriteLine(
        "PASS: BepInEx console control is opt-in, Windows-only, close-safe, "
        + "transactional, listener-safe, and hide-only.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyUnsupportedPlatformFailsClosed()
{
    var runtime = new FakeConsoleRuntime { IsSupported = false };
    var service = CreateService(runtime);

    var hidden = service.ApplyVisibility(visible: false);
    AssertEqual(true, hidden.Ok, "Keeping an unsupported console hidden should be a successful no-op.");
    AssertEqual("unsupported-platform", hidden.Status, "Unsupported status was not preserved.");

    var visible = service.ApplyVisibility(visible: true);
    AssertEqual(false, visible.Ok, "An unsupported platform accepted a show request.");
    AssertContains(visible.Error ?? "", "only available on Windows", "Unsupported error was not explicit.");
    AssertEqual(0, runtime.CreateCalls, "Unsupported runtime attempted to create a console.");
    AssertEqual(0, runtime.ShowCalls, "Unsupported runtime attempted to show a console.");
}

static void VerifyCreateShowAndListenerSequence()
{
    var runtime = new FakeConsoleRuntime();
    var service = CreateService(runtime);

    var result = service.ApplyVisibility(visible: true);

    AssertEqual(true, result.Ok, "Console show request failed.");
    AssertEqual(true, result.Active, "Created console was not active.");
    AssertEqual(true, result.Visible, "Created console was not visible.");
    AssertEqual(1, runtime.CreateCalls, "Console was not created exactly once.");
    AssertEqual(1, runtime.DisableCloseCalls, "A Mod-created console kept its process-closing command.");
    AssertEqual(1, runtime.EnsureListenerCalls, "Console listener was not ensured exactly once.");
    AssertEqual(1, runtime.ShowCalls, "Console was not shown exactly once.");
    AssertSequenceEqual(
        new[] { "create", "disable-close", "listener", "show" },
        runtime.Events,
        "Create, close protection, listener registration, and show order changed.");
    AssertEqual(true, result.ConfiguredVisible, "A successful show was not persisted atomically.");
}

static void VerifyExistingConsoleIsReused()
{
    var runtime = new FakeConsoleRuntime
    {
        IsActive = true,
        IsVisible = true,
    };
    var service = CreateService(runtime);

    var first = service.ApplyVisibility(visible: true);
    var second = service.ApplyVisibility(visible: true);

    AssertEqual(true, first.Ok && second.Ok, "Idempotent show request failed.");
    AssertEqual(0, runtime.CreateCalls, "An existing console was recreated.");
    AssertEqual(0, runtime.DisableCloseCalls, "The Mod changed an existing BepInEx console menu.");
    AssertEqual(0, runtime.ShowCalls, "An already-visible console was shown again.");
    AssertEqual(2, runtime.EnsureListenerCalls, "Each show request must verify that a listener still exists.");
}

static void VerifyInactiveExistingWindowIsNotReclassified()
{
    var runtime = new FakeConsoleRuntime
    {
        WindowHandle = 1,
    };
    var service = CreateService(runtime);

    var result = service.ApplyVisibility(visible: true);

    AssertEqual(true, result.Ok, "An inactive existing console window could not be attached.");
    AssertEqual(1, runtime.CreateCalls, "The inactive BepInEx driver was not initialized.");
    AssertEqual(0, runtime.DisableCloseCalls, "An existing console window lost its close command.");
    AssertEqual(true, result.Visible, "The existing console window was not shown.");
}

static void VerifyHideNeverDetaches()
{
    var runtime = new FakeConsoleRuntime
    {
        IsActive = true,
        IsVisible = true,
    };
    var service = CreateService(runtime);

    var first = service.ApplyVisibility(visible: false);
    var second = service.ApplyVisibility(visible: false);

    AssertEqual(true, first.Ok && second.Ok, "Hide request failed.");
    AssertEqual(true, first.Active, "Hiding unexpectedly deactivated the BepInEx console.");
    AssertEqual(false, first.Visible, "Console remained visible after hide.");
    AssertEqual(1, runtime.HideCalls, "Hide was not idempotent.");
    AssertEqual(0, runtime.CreateCalls, "Hide created a console.");
    AssertEqual(0, runtime.DisableCloseCalls, "Hide changed the console close command.");
    AssertEqual(0, runtime.EnsureListenerCalls, "Hide changed the console listener.");
    AssertEqual(false, second.ConfiguredVisible, "A successful hide was not persisted atomically.");
}

static void VerifyTransitionFailuresAreReported()
{
    var createFailure = new FakeConsoleRuntime { CreateException = new InvalidOperationException("create failed") };
    var createResult = CreateService(createFailure).ApplyVisibility(visible: true);
    AssertEqual(false, createResult.Ok, "Create failure was reported as success.");
    AssertContains(createResult.Error ?? "", "create failed", "Create failure detail was lost.");

    var inactiveAfterCreate = new FakeConsoleRuntime
    {
        ActivateOnCreate = false,
        BecomeVisibleOnCreate = true,
    };
    var inactiveResult = CreateService(inactiveAfterCreate).ApplyVisibility(visible: true);
    AssertEqual(false, inactiveResult.Ok, "Inactive create result was accepted.");
    AssertContains(inactiveResult.Error ?? "", "did not activate", "Inactive create diagnostic was lost.");
    AssertEqual(1, inactiveAfterCreate.HideCalls, "A visible partial create was not rolled back.");
    AssertEqual(false, inactiveAfterCreate.IsVisible, "A failed partial create left a visible console window.");
    AssertEqual(false, inactiveResult.Visible, "The failed response misreported the rolled-back window.");

    var throwAfterWindow = new FakeConsoleRuntime
    {
        ActivateOnCreate = false,
        BecomeVisibleOnCreate = true,
        CreateExceptionAfterWindow = new InvalidOperationException("stream initialization failed"),
    };
    var throwAfterWindowResult = CreateService(throwAfterWindow).ApplyVisibility(visible: true);
    AssertEqual(false, throwAfterWindowResult.Ok, "A partial create exception was reported as success.");
    AssertContains(
        throwAfterWindowResult.Error ?? "",
        "stream initialization failed",
        "The partial create exception was lost.");
    AssertEqual(1, throwAfterWindow.DisableCloseCalls, "A partially created Mod window kept SC_CLOSE.");
    AssertEqual(1, throwAfterWindow.HideCalls, "A partially created visible window was not hidden.");
    AssertEqual(false, throwAfterWindow.IsVisible, "A partial create exception left its window visible.");

    var showFailure = new FakeConsoleRuntime
    {
        IsActive = true,
        BecomeVisibleOnShow = false,
    };
    var showResult = CreateService(showFailure).ApplyVisibility(visible: true);
    AssertEqual(false, showResult.Ok, "Invisible show result was accepted.");
    AssertContains(showResult.Error ?? "", "did not become visible", "Show verification diagnostic was lost.");

    var hideFailure = new FakeConsoleRuntime
    {
        IsActive = true,
        IsVisible = true,
        BecomeHiddenOnHide = false,
    };
    var hideResult = CreateService(hideFailure).ApplyVisibility(visible: false);
    AssertEqual(false, hideResult.Ok, "Visible hide result was accepted.");
    AssertContains(hideResult.Error ?? "", "did not become hidden", "Hide verification diagnostic was lost.");
}

static void VerifyCloseProtectionFailureBlocksShowUntilRetry()
{
    var runtime = new FakeConsoleRuntime
    {
        DisableCloseException = new InvalidOperationException("menu protection failed"),
    };
    var service = CreateService(runtime);

    var failed = service.ApplyVisibility(visible: true);
    AssertEqual(false, failed.Ok, "A close-protection failure still showed the console.");
    AssertContains(failed.Error ?? "", "menu protection failed", "Close-protection failure detail was lost.");
    AssertEqual(false, runtime.IsVisible, "An unprotected Mod-created window became visible.");
    AssertEqual(1, runtime.DisableCloseCalls, "Close protection was not attempted.");

    runtime.DisableCloseException = null;
    var retried = service.ApplyVisibility(visible: true);
    AssertEqual(true, retried.Ok, "Close protection could not be retried.");
    AssertEqual(true, retried.Visible, "The console was not shown after close protection succeeded.");
    AssertEqual(2, runtime.DisableCloseCalls, "Pending close protection was not retried before show.");
}

static void VerifyPreferenceFailureRestoresBothDirections()
{
    var showRuntime = new FakeConsoleRuntime();
    var hiddenPreference = new FakeConsolePreference { ThrowAfterEveryMutation = true };
    var showService = CreateService(showRuntime, hiddenPreference);

    var showResult = showService.ApplyVisibility(visible: true);

    AssertEqual(false, showResult.Ok, "A failed show preference commit was reported as success.");
    AssertContains(showResult.Error ?? "", "preference write failed", "Show preference failure detail was lost.");
    AssertEqual(false, hiddenPreference.Visible, "Show rollback did not restore the startup setting.");
    AssertEqual(false, showRuntime.IsVisible, "Show rollback did not restore the window visibility.");
    AssertEqual(false, showResult.ConfiguredVisible, "Show rollback returned a stale configured state.");

    var hideRuntime = new FakeConsoleRuntime { IsActive = true, IsVisible = true };
    var visiblePreference = new FakeConsolePreference
    {
        Visible = true,
        ThrowAfterEveryMutation = true,
    };
    var hideService = CreateService(hideRuntime, visiblePreference);

    var hideResult = hideService.ApplyVisibility(visible: false);

    AssertEqual(false, hideResult.Ok, "A failed hide preference commit was reported as success.");
    AssertContains(hideResult.Error ?? "", "preference write failed", "Hide preference failure detail was lost.");
    AssertEqual(true, visiblePreference.Visible, "Hide rollback did not restore the startup setting.");
    AssertEqual(true, hideRuntime.IsVisible, "Hide rollback did not restore the window visibility.");
    AssertEqual(true, hideResult.ConfiguredVisible, "Hide rollback returned a stale configured state.");
}

static void VerifyConcurrentOppositeRequestsStayConsistent()
{
    var runtime = new FakeConsoleRuntime
    {
        ShowEntered = new ManualResetEventSlim(false),
        ContinueShow = new ManualResetEventSlim(false),
    };
    var preference = new FakeConsolePreference();
    var service = CreateService(runtime, preference);

    var showTask = Task.Run(() => service.ApplyVisibility(visible: true));
    AssertEqual(true, runtime.ShowEntered.Wait(TimeSpan.FromSeconds(2)), "Show request did not enter the runtime.");
    var hideStarted = new ManualResetEventSlim(false);
    var hideTask = Task.Run(() =>
    {
        hideStarted.Set();
        return service.ApplyVisibility(visible: false);
    });
    AssertEqual(true, hideStarted.Wait(TimeSpan.FromSeconds(2)), "Hide request was not issued concurrently.");
    runtime.ContinueShow.Set();

    var shown = showTask.GetAwaiter().GetResult();
    var hidden = hideTask.GetAwaiter().GetResult();

    AssertEqual(true, shown.Ok, "Concurrent show request failed.");
    AssertEqual(true, shown.Visible, "Show response did not contain its committed window state.");
    AssertEqual(true, shown.ConfiguredVisible, "Show response mixed in a later preference.");
    AssertEqual(true, hidden.Ok, "Queued hide request failed.");
    AssertEqual(false, hidden.Visible, "Queued hide response did not contain its committed window state.");
    AssertEqual(false, hidden.ConfiguredVisible, "Queued hide did not commit its preference.");
    AssertEqual(false, runtime.IsVisible, "Final window state did not match the last request.");
    AssertEqual(false, preference.Visible, "Final startup preference did not match the last request.");
}

static void VerifyUncapturedStateDoesNotRewritePreference()
{
    var runtime = new FakeConsoleRuntime { ThrowWhenReadingActive = true };
    var preference = new FakeConsolePreference { Visible = true };
    var service = CreateService(runtime, preference);

    var result = service.ApplyVisibility(visible: false);

    AssertEqual(false, result.Ok, "A runtime state read failure was reported as success.");
    AssertEqual(true, preference.Visible, "An uncaptured runtime state cleared the startup preference.");
    AssertEqual(0, preference.SetCalls, "An operation that never changed state attempted preference rollback.");
}

static void VerifyProductionSourceContract()
{
    var runtimeSource = File.ReadAllText(FindSource("mods", "bepinex", "src", "Plugin", "BepInExConsoleRuntime.cs"));
    var serviceSource = File.ReadAllText(FindSource("mods", "bepinex", "src", "Plugin", "BepInExConsoleWindowService.cs"));
    var configSource = File.ReadAllText(FindSource("mods", "bepinex", "src", "Plugin", "StewardPluginConfig.cs"));
    var pluginSource = File.ReadAllText(FindSource("mods", "bepinex", "src", "Plugin", "MystiaStewardCompanionPlugin.cs"));

    AssertContains(runtimeSource, "ConsoleManager.CreateConsole();", "Production runtime does not use the BepInEx 783 create API.");
    AssertContains(runtimeSource, "listener is ConsoleLogListener", "Production runtime does not detect an existing console listener.");
    AssertContains(runtimeSource, "Logger.Listeners.Add(new ConsoleLogListener());", "Production runtime does not install the missing console listener.");
    AssertContains(runtimeSource, "ShowWindowHide = 0", "Production runtime does not define hide-only window control.");
    AssertContains(runtimeSource, "ShowWindowWithoutActivation = 4", "Production runtime may steal focus while showing the console.");
    AssertContains(runtimeSource, "SystemCommandClose = 0xf060", "Production runtime does not identify SC_CLOSE.");
    AssertContains(runtimeSource, "DeleteMenu(menu, SystemCommandClose", "Production runtime leaves the close command enabled.");
    AssertContains(runtimeSource, "public nint WindowHandle", "Production runtime cannot identify the Win32 window independently of BepInEx driver state.");
    AssertAbsent(runtimeSource, "SetConsoleCtrlHandler", "A control handler cannot make the console close button safe.");
    AssertAbsent(runtimeSource, "DetachConsole", "Production runtime must never detach the shared BepInEx console.");
    AssertAbsent(runtimeSource, "ConfigConsoleEnabled.Value", "Production runtime must not rewrite global BepInEx console configuration.");
    AssertAbsent(runtimeSource, "BepInEx.cfg", "Production runtime must not write the global BepInEx configuration file.");
    AssertContains(
        serviceSource,
        "var protectionException = TryProtectNewWindow(previousWindow);",
        "Production service does not protect a window created before CreateConsole throws.");
    AssertContains(
        serviceSource,
        "if (previousWindow != 0 || currentWindow == 0)",
        "Production service may change the close command on a pre-existing console window.");
    AssertAbsent(
        serviceSource,
        "previousActive && _runtime.IsVisible",
        "Production service still hides actual visibility behind BepInEx driver state.");

    AssertContains(
        configSource,
        "config.Bind(\"Diagnostics\", \"ShowBepInExConsoleOnStartup\", false",
        "The Mod console startup setting is not default-off.");
    AssertContains(
        pluginSource,
        "if (settings.ShowBepInExConsoleOnStartup.Value)",
        "Plugin startup does not guard console creation behind the explicit opt-in setting.");
    AssertContains(
        pluginSource,
        "ApplyVisibility(visible: true)",
        "Plugin startup does not limit its action to an explicit show request.");
    AssertAbsent(
        pluginSource,
        "ApplyVisibility(settings.ShowBepInExConsoleOnStartup.Value)",
        "Default-off startup would hide a console enabled by the user's global BepInEx configuration.");
}

static BepInExConsoleWindowService CreateService(
    FakeConsoleRuntime runtime,
    FakeConsolePreference? preference = null)
{
    preference ??= new FakeConsolePreference();
    var service = new BepInExConsoleWindowService(runtime);
    service.ConfigurePreference(
        () => preference.Visible,
        visible => preference.Set(visible));
    return service;
}

static string FindSource(params string[] parts)
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
    {
        var candidateParts = new[] { directory.FullName }.Concat(parts).ToArray();
        var candidate = Path.Combine(candidateParts);
        if (File.Exists(candidate)) return candidate;
    }

    throw new FileNotFoundException($"Could not locate source file: {string.Join('/', parts)}.");
}

static void AssertContains(string source, string value, string message)
{
    if (!source.Contains(value, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertAbsent(string source, string value, string message)
{
    if (source.Contains(value, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}

static void AssertSequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected=[{string.Join(", ", expected)}], actual=[{string.Join(", ", actual)}].");
    }
}

internal sealed class FakeConsoleRuntime : IBepInExConsoleRuntime
{
    private bool _isActive;
    private bool _isVisible;

    public bool IsSupported { get; set; } = true;
    public bool IsActive
    {
        get
        {
            if (ThrowWhenReadingActive)
            {
                throw new InvalidOperationException("active state read failed");
            }

            return _isActive;
        }
        set => _isActive = value;
    }
    public nint WindowHandle { get; set; }
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            _isVisible = value;
            if (value && WindowHandle == 0) WindowHandle = 1;
        }
    }
    public bool ThrowWhenReadingActive { get; init; }
    public bool ActivateOnCreate { get; set; } = true;
    public bool CreateWindowOnCreate { get; set; } = true;
    public bool BecomeVisibleOnCreate { get; set; }
    public bool BecomeVisibleOnShow { get; set; } = true;
    public bool BecomeHiddenOnHide { get; set; } = true;
    public Exception? CreateException { get; set; }
    public Exception? CreateExceptionAfterWindow { get; set; }
    public Exception? DisableCloseException { get; set; }
    public int CreateCalls { get; private set; }
    public int DisableCloseCalls { get; private set; }
    public int EnsureListenerCalls { get; private set; }
    public int ShowCalls { get; private set; }
    public int HideCalls { get; private set; }
    public List<string> Events { get; } = new();
    public ManualResetEventSlim? ShowEntered { get; init; }
    public ManualResetEventSlim? ContinueShow { get; init; }

    public void Create()
    {
        CreateCalls += 1;
        Events.Add("create");
        if (CreateException != null) throw CreateException;
        if (CreateWindowOnCreate && WindowHandle == 0) WindowHandle = 1;
        IsActive = ActivateOnCreate;
        if (BecomeVisibleOnCreate) IsVisible = true;
        if (CreateExceptionAfterWindow != null) throw CreateExceptionAfterWindow;
    }

    public void DisableCloseCommand()
    {
        DisableCloseCalls += 1;
        Events.Add("disable-close");
        if (DisableCloseException != null) throw DisableCloseException;
    }

    public void EnsureLogListener()
    {
        EnsureListenerCalls += 1;
        Events.Add("listener");
    }

    public void ShowWithoutActivation()
    {
        ShowCalls += 1;
        Events.Add("show");
        ShowEntered?.Set();
        ContinueShow?.Wait(TimeSpan.FromSeconds(2));
        if (BecomeVisibleOnShow) IsVisible = true;
    }

    public void Hide()
    {
        HideCalls += 1;
        Events.Add("hide");
        if (BecomeHiddenOnHide) IsVisible = false;
    }
}

internal sealed class FakeConsolePreference
{
    public bool Visible { get; set; }
    public bool ThrowAfterEveryMutation { get; init; }
    public int SetCalls { get; private set; }

    public void Set(bool visible)
    {
        SetCalls += 1;
        Visible = visible;
        if (ThrowAfterEveryMutation)
        {
            throw new InvalidOperationException("preference write failed");
        }
    }
}
