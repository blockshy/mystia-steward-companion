using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace MystiaStewardCompanion.Plugin;

internal static class BepInExConsoleHelper
{
    private const int HideWindow = 0;

    public static void Apply(bool disableConsoleLogWindow, bool hideConsoleWindow, ManualLogSource log)
    {
        if (disableConsoleLogWindow)
        {
            DisableConsoleLogForNextLaunch(log);
        }

        if (hideConsoleWindow)
        {
            HideCurrentConsoleWindow(log);
        }
    }

    private static void DisableConsoleLogForNextLaunch(ManualLogSource log)
    {
        try
        {
            var configPath = Path.Combine(Paths.ConfigPath, "BepInEx.cfg");
            SetIniBoolean(configPath, "Logging.Console", "Enabled", false);
        }
        catch (Exception ex)
        {
            log.LogWarning($"Failed to disable BepInEx console logging for next launch: {ex.Message}");
        }
    }

    private static void HideCurrentConsoleWindow(ManualLogSource log)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            var consoleWindow = GetConsoleWindow();
            if (consoleWindow == IntPtr.Zero) return;
            ShowWindow(consoleWindow, HideWindow);
        }
        catch (Exception ex)
        {
            log.LogWarning($"Failed to hide BepInEx console window: {ex.Message}");
        }
    }

    private static void SetIniBoolean(string path, string sectionName, string keyName, bool value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var replacement = $"{keyName} = {value.ToString().ToLowerInvariant()}";
        var lines = File.Exists(path)
            ? File.ReadAllLines(path, Encoding.UTF8).ToList()
            : new List<string>();

        var sectionStart = -1;
        var sectionEnd = lines.Count;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith("[", StringComparison.Ordinal) || !trimmed.EndsWith("]", StringComparison.Ordinal)) continue;

            if (sectionStart >= 0)
            {
                sectionEnd = i;
                break;
            }

            if (string.Equals(trimmed, $"[{sectionName}]", StringComparison.OrdinalIgnoreCase))
            {
                sectionStart = i;
            }
        }

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add($"[{sectionName}]");
            lines.Add(replacement);
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            return;
        }

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith($"{keyName} ", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith($"{keyName}=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var indentation = lines[i][..(lines[i].Length - trimmed.Length)];
            lines[i] = indentation + replacement;
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            return;
        }

        lines.Insert(sectionStart + 1, replacement);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
