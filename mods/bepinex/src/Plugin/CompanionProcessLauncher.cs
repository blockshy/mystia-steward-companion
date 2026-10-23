using System.Diagnostics;
using BepInEx.Logging;

namespace MystiaStewardCompanion.Plugin;

internal static class CompanionProcessLauncher
{
    private static string _expectedExecutablePath = "";
    private const string ControlShow = "show";
    private const string ControlToggle = "toggle";
    private const string ControlExit = "exit";
    private static readonly TimeSpan ControlReadyTimeout = TimeSpan.FromSeconds(8);
    private static readonly object LifecycleLock = new();
    private static readonly CompanionConnectionToken ConnectionToken = new();
    private static int _generation;
    private static bool _stopping;
    private static bool _launchPending;

    public static void BeginSession()
    {
        ConnectionToken.Reset();
        lock (LifecycleLock)
        {
            _generation += 1;
            _stopping = false;
            _launchPending = false;
        }
    }

    /// <summary>只更新后续启动/激活所用凭据，不额外弹出窗口或重放 show/toggle 操作。</summary>
    public static void UpdateConnectionToken(string token) => ConnectionToken.Set(token);

    public static void TryAutoLaunch(StewardPluginConfig config, ManualLogSource log, string localApiToken)
    {
        if (!config.CompanionAutoLaunch.Value) return;
        var options = CaptureLaunchOptions(config, log, localApiToken);
        if (options != null) QueueControlOrLaunch(ControlShow, options);
    }

    public static void TryToggleOrLaunch(StewardPluginConfig config, ManualLogSource log, string localApiToken)
    {
        var options = CaptureLaunchOptions(config, log, localApiToken);
        if (options != null) QueueControlOrLaunch(ControlToggle, options);
    }

    public static void TryNotifyExit(ManualLogSource? log)
    {
        int generation;
        lock (LifecycleLock)
        {
            if (_stopping) return;
            _stopping = true;
            generation = _generation;
        }

        if (!ThreadPool.QueueUserWorkItem(_ =>
            {
                if (IsStoppingGeneration(generation))
                {
                    SendControlMessage(ControlExit, apiEndpoint: "", localApiToken: "");
                }
            }))
        {
            log?.LogWarning("Companion exit notification could not be queued.");
        }
    }

    private static void QueueControlOrLaunch(string message, CompanionLaunchOptions options)
    {
        if (ThreadPool.QueueUserWorkItem(_ =>
        {
            if (!IsActiveGeneration(options.Generation)) return;
            if (SendControlMessage(message, options.ApiEndpoint, options.LocalApiToken))
            {
                if (!IsActiveGeneration(options.Generation))
                {
                    SendControlMessage(ControlExit, apiEndpoint: "", localApiToken: "");
                }
                return;
            }
            if (!TryBeginLaunch(options.Generation)) return;
            try
            {
                if (SendControlMessage(message, options.ApiEndpoint, options.LocalApiToken)) return;
                if (TryLaunch(options)) WaitForControlServer(options);
            }
            finally
            {
                CompleteLaunch(options.Generation);
            }
        }))
        {
            return;
        }

        options.Log.LogWarning("Companion control request could not be queued.");
    }

    private static CompanionLaunchOptions? CaptureLaunchOptions(
        StewardPluginConfig config,
        ManualLogSource log,
        string localApiToken)
    {
        int generation;
        lock (LifecycleLock)
        {
            if (_stopping) return null;
            generation = _generation;
        }

        var executablePath = ResolveExecutablePath(config.CompanionExecutablePath.Value);
        lock (LifecycleLock) _expectedExecutablePath = executablePath;
        return new CompanionLaunchOptions(
            executablePath,
            BuildLocalApiEndpoint(config.LocalApiPort.Value),
            localApiToken.Trim(),
            log,
            generation);
    }

    private static bool TryLaunch(CompanionLaunchOptions options)
    {
        try
        {
            var executablePath = ResolveExecutablePath(options.ConfiguredExecutablePath);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                options.Log.LogInfo("Companion launch skipped: companion executable was not found.");
                return false;
            }

            // 游戏由 Steam 启动时会携带其环境标记；管家是独立工具，只调整子进程环境副本。
            // 参数继续通过 ArgumentList 传递，不经 shell，也不把连接凭据写入日志或环境。
            var startInfo = CompanionProcessEnvironment.CreateStartInfo(
                executablePath, Path.GetDirectoryName(executablePath) ?? "");
            startInfo.ArgumentList.Add($"--api={options.ApiEndpoint}");
            startInfo.ArgumentList.Add($"--game-pid={Process.GetCurrentProcess().Id}");
            var started = ConnectionToken.Use(options.LocalApiToken, token =>
            {
                lock (LifecycleLock)
                {
                    if (_stopping || options.Generation != _generation) return false;
                    if (!string.IsNullOrWhiteSpace(token)) startInfo.ArgumentList.Add($"--token={token}");
                    using var process = Process.Start(startInfo)
                        ?? throw new InvalidOperationException("Companion process could not be started.");
                    return true;
                }
            });
            if (!started) return false;
            options.Log.LogInfo($"Companion launch/focus requested: {executablePath}");
            return true;
        }
        catch (Exception ex)
        {
            options.Log.LogWarning($"Companion launch failed: {ex.Message}");
            return false;
        }
    }

    private static void WaitForControlServer(CompanionLaunchOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        while (IsActiveGeneration(options.Generation) && stopwatch.Elapsed < ControlReadyTimeout)
        {
            if (SendControlMessage(ControlShow, options.ApiEndpoint, options.LocalApiToken)) return;
            Thread.Sleep(50);
        }

        if (IsActiveGeneration(options.Generation))
        {
            options.Log.LogWarning("Companion process started but did not claim the control port in time.");
        }
    }

    /// <summary>新版控制客户端先验证目标进程，再发送凭据；退出须等到同一进程真正终止。</summary>
    private static bool SendControlMessage(string message, string apiEndpoint, string localApiToken)
    {
        string executablePath;
        lock (LifecycleLock) executablePath = _expectedExecutablePath;
        // 已排队的激活不能在改密回执之后再次发送捕获的旧凭据；退出消息没有连接参数。
        if (message == ControlExit)
            return CompanionControlClient.TrySend(message, "", "", Process.GetCurrentProcess().Id, executablePath);
        return ConnectionToken.Use(localApiToken, token => CompanionControlClient.TrySend(message, apiEndpoint, token,
            Process.GetCurrentProcess().Id, executablePath));
    }
    private static string BuildLocalApiEndpoint(int configuredPort)
    {
        var port = Math.Clamp(configuredPort, 1024, 65535);
        return $"http://127.0.0.1:{port}";
    }

    private static bool IsActiveGeneration(int generation)
    {
        lock (LifecycleLock)
        {
            return !_stopping && generation == _generation;
        }
    }

    private static bool IsStoppingGeneration(int generation)
    {
        lock (LifecycleLock)
        {
            return _stopping && generation == _generation;
        }
    }

    private static bool TryBeginLaunch(int generation)
    {
        lock (LifecycleLock)
        {
            if (_stopping || generation != _generation || _launchPending) return false;
            _launchPending = true;
            return true;
        }
    }

    private static void CompleteLaunch(int generation)
    {
        lock (LifecycleLock)
        {
            if (generation == _generation) _launchPending = false;
        }
    }

    private static string ResolveExecutablePath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (File.Exists(expanded)) return Path.GetFullPath(expanded);
        }

        var pluginDirectory = Path.GetDirectoryName(typeof(MystiaStewardCompanionPlugin).Assembly.Location);
        if (string.IsNullOrWhiteSpace(pluginDirectory)) return "";

        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine("companion", "mystia-steward-companion.exe"),
            }
            : new[]
            {
                Path.Combine("companion", "mystia-steward-companion"),
            };

        return candidates
            .Select(candidate => Path.Combine(pluginDirectory, candidate))
            .FirstOrDefault(File.Exists) ?? "";
    }

    private sealed record CompanionLaunchOptions(
        string ConfiguredExecutablePath,
        string ApiEndpoint,
        string LocalApiToken,
        ManualLogSource Log,
        int Generation);

}
