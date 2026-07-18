using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using BepInEx.Logging;

namespace MystiaStewardCompanion.Plugin;

internal static class CompanionProcessLauncher
{
    private const int ControlPort = 32146;
    private const string ControlShow = "mystia-steward-companion:show";
    private const string ControlToggle = "mystia-steward-companion:toggle";
    private const string ControlExit = "mystia-steward-companion:exit";
    private static readonly TimeSpan ControlReadyTimeout = TimeSpan.FromSeconds(8);
    private static readonly object LifecycleLock = new();
    private static int _generation;
    private static bool _stopping;
    private static bool _launchPending;

    public static void BeginSession()
    {
        lock (LifecycleLock)
        {
            _generation += 1;
            _stopping = false;
            _launchPending = false;
        }
    }

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

        return new CompanionLaunchOptions(
            config.CompanionExecutablePath.Value,
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

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? "",
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add($"--api={options.ApiEndpoint}");
            startInfo.ArgumentList.Add($"--game-pid={Process.GetCurrentProcess().Id}");
            if (!string.IsNullOrWhiteSpace(options.LocalApiToken))
            {
                startInfo.ArgumentList.Add($"--token={options.LocalApiToken}");
            }

            lock (LifecycleLock)
            {
                if (_stopping || options.Generation != _generation) return false;
                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Companion process could not be started.");
            }
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

    private static bool SendControlMessage(string message, string apiEndpoint, string localApiToken)
    {
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync("127.0.0.1", ControlPort).Wait(TimeSpan.FromMilliseconds(180)))
            {
                return false;
            }

            var bytes = Encoding.UTF8.GetBytes(BuildControlMessage(message, apiEndpoint, localApiToken));
            using var stream = client.GetStream();
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildControlMessage(string message, string apiEndpoint, string localApiToken)
    {
        var builder = new StringBuilder()
            .AppendLine(message)
            .Append("--game-pid=")
            .AppendLine(Process.GetCurrentProcess().Id.ToString());

        if (!string.IsNullOrWhiteSpace(apiEndpoint))
        {
            builder
                .Append("--api=")
                .AppendLine(apiEndpoint);
        }

        if (!string.IsNullOrWhiteSpace(localApiToken))
        {
            builder.Append("--token=").AppendLine(localApiToken.Trim());
        }

        return builder.ToString();
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
                "mystia-steward-companion.exe",
                Path.Combine("companion", "mystia-steward-companion.exe"),
            }
            : new[]
            {
                "mystia-steward-companion",
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
