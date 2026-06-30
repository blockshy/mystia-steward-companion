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
    private static readonly object RequestLock = new();
    private static DateTime _lastRequestUtc = DateTime.MinValue;

    public static void TryAutoLaunch(StewardPluginConfig config, ManualLogSource log, string localApiToken)
    {
        if (!config.CompanionAutoLaunch.Value) return;
        TryShowOrLaunch(config, log, localApiToken);
    }

    public static void TryLaunchOrFocus(StewardPluginConfig config, ManualLogSource log, string localApiToken)
    {
        TryShowOrLaunch(config, log, localApiToken);
    }

    public static void TryShowOrLaunch(StewardPluginConfig config, ManualLogSource log, string localApiToken)
    {
        if (SendControlMessage(ControlShow, config, localApiToken)) return;
        TryLaunch(config, log, localApiToken);
    }

    public static void TryToggleOrLaunch(StewardPluginConfig config, ManualLogSource log, string localApiToken)
    {
        if (IsRequestThrottled()) return;
        if (SendControlMessage(ControlToggle, config, localApiToken)) return;
        TryLaunch(config, log, localApiToken);
    }

    public static void TryNotifyExit()
    {
        SendControlMessage(ControlExit);
    }

    private static void TryLaunch(StewardPluginConfig config, ManualLogSource log, string localApiToken)
    {
        try
        {
            RecordRequestTime();
            var executablePath = ResolveExecutablePath(config.CompanionExecutablePath.Value);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                log.LogInfo("Companion launch skipped: companion executable was not found.");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? "",
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add($"--api=http://127.0.0.1:{Math.Clamp(config.LocalApiPort.Value, 1024, 65535)}");
            startInfo.ArgumentList.Add($"--game-pid={Process.GetCurrentProcess().Id}");
            if (!string.IsNullOrWhiteSpace(localApiToken))
            {
                startInfo.ArgumentList.Add($"--token={localApiToken}");
            }

            Process.Start(startInfo);
            log.LogInfo($"Companion launch/focus requested: {executablePath}");
        }
        catch (Exception ex)
        {
            log.LogWarning($"Companion launch failed: {ex.Message}");
        }
    }

    private static bool SendControlMessage(string message, StewardPluginConfig? config = null, string localApiToken = "")
    {
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync("127.0.0.1", ControlPort).Wait(TimeSpan.FromMilliseconds(180)))
            {
                return false;
            }

            var bytes = Encoding.UTF8.GetBytes(BuildControlMessage(message, config, localApiToken));
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

    private static string BuildControlMessage(string message, StewardPluginConfig? config, string localApiToken)
    {
        var builder = new StringBuilder()
            .AppendLine(message)
            .Append("--game-pid=")
            .AppendLine(Process.GetCurrentProcess().Id.ToString());

        if (config != null)
        {
            builder
                .Append("--api=http://127.0.0.1:")
                .AppendLine(Math.Clamp(config.LocalApiPort.Value, 1024, 65535).ToString());
        }

        if (!string.IsNullOrWhiteSpace(localApiToken))
        {
            builder.Append("--token=").AppendLine(localApiToken.Trim());
        }

        return builder.ToString();
    }

    private static bool IsRequestThrottled()
    {
        lock (RequestLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastRequestUtc < TimeSpan.FromMilliseconds(800))
            {
                return true;
            }

            _lastRequestUtc = now;
            return false;
        }
    }

    private static void RecordRequestTime()
    {
        lock (RequestLock)
        {
            _lastRequestUtc = DateTime.UtcNow;
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

}
