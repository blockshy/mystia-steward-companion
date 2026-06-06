using System.Diagnostics;
using BepInEx.Logging;

namespace MystiaSteward.Plugin;

internal static class CompanionProcessLauncher
{
    public static void TryAutoLaunch(StewardPluginConfig config, ManualLogSource log)
    {
        if (!config.CompanionAutoLaunch.Value) return;
        TryLaunchOrFocus(config, log);
    }

    public static void TryLaunchOrFocus(StewardPluginConfig config, ManualLogSource log)
    {
        try
        {
            var executablePath = ResolveExecutablePath(config.CompanionExecutablePath.Value);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                log.LogInfo("Companion launch skipped: companion executable was not found.");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"--api=http://127.0.0.1:{Math.Clamp(config.LocalApiPort.Value, 1024, 65535)}",
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? "",
                UseShellExecute = false,
            };

            Process.Start(startInfo);
            log.LogInfo($"Companion launch/focus requested: {executablePath}");
        }
        catch (Exception ex)
        {
            log.LogWarning($"Companion launch failed: {ex.Message}");
        }
    }

    private static string ResolveExecutablePath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (File.Exists(expanded)) return Path.GetFullPath(expanded);
        }

        var pluginDirectory = Path.GetDirectoryName(typeof(MystiaStewardPlugin).Assembly.Location);
        if (string.IsNullOrWhiteSpace(pluginDirectory)) return "";

        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                "MystiaSteward.Companion.exe",
                "mystia-steward-companion.exe",
                "Mystia Steward Companion.exe",
                Path.Combine("companion", "MystiaSteward.Companion.exe"),
                Path.Combine("companion", "mystia-steward-companion.exe"),
                Path.Combine("companion", "Mystia Steward Companion.exe"),
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
