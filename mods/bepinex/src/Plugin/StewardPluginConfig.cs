using BepInEx.Configuration;
using UnityEngine;

namespace MystiaStewardCompanion.Plugin;

/// <summary>
/// Mod 的 BepInEx 配置封装，集中暴露热键、运行时读取、本地 API、伴随窗口、诊断和自动更新设置。
/// </summary>
/// <remarks>
/// 该类型只负责绑定和保存 <see cref="ConfigEntry{T}"/>，不直接读取游戏运行时对象。调用方应在需要时读取
/// Entry 的当前值，这样用户手动修改配置后可以在后续刷新中逐步生效。
/// </remarks>
public sealed class StewardPluginConfig
{
    private StewardPluginConfig(
        ConfigEntry<KeyCode> toggleKey,
        ConfigEntry<KeyCode> controllerToggleKey,
        ConfigEntry<bool> autoRefreshRuntime,
        ConfigEntry<float> autoRefreshSeconds,
        ConfigEntry<string> nonGameplaySceneKeywords,
        ConfigEntry<bool> localApiEnabled,
        ConfigEntry<bool> localApiLanEnabled,
        ConfigEntry<string> localApiLanHost,
        ConfigEntry<int> localApiPort,
        ConfigEntry<string> localApiToken,
        ConfigEntry<bool> exposeLocalApiLogs,
        ConfigEntry<int> localApiMaxLogLines,
        ConfigEntry<int> localApiMaxLogBytes,
        ConfigEntry<bool> companionAutoLaunch,
        ConfigEntry<string> companionExecutablePath,
        ConfigEntry<bool> setConsoleUtf8,
        ConfigEntry<bool> disableBepInExConsoleLog,
        ConfigEntry<bool> hideBepInExConsoleWindow,
        ConfigEntry<bool> enableNightBusinessDiagnostics,
        ConfigEntry<string> nightBusinessDiagnosticsPath,
        ConfigEntry<float> nightBusinessDiagnosticsIntervalSeconds,
        ConfigEntry<bool> enableAggregateModLog,
        ConfigEntry<string> aggregateModLogPath,
        ConfigEntry<bool> updatesEnabled,
        ConfigEntry<bool> updatesAutoCheck,
        ConfigEntry<int> updatesCheckIntervalHours,
        ConfigEntry<bool> updatesIncludePrerelease,
        ConfigEntry<int> maxExtraIngredients,
        ConfigEntry<string> popularFoodTagOverride,
        ConfigEntry<string> popularHateFoodTagOverride,
        ConfigEntry<bool> famousShopOverride)
    {
        ToggleKey = toggleKey;
        ControllerToggleKey = controllerToggleKey;
        AutoRefreshRuntime = autoRefreshRuntime;
        AutoRefreshSeconds = autoRefreshSeconds;
        NonGameplaySceneKeywords = nonGameplaySceneKeywords;
        LocalApiEnabled = localApiEnabled;
        LocalApiLanEnabled = localApiLanEnabled;
        LocalApiLanHost = localApiLanHost;
        LocalApiPort = localApiPort;
        LocalApiToken = localApiToken;
        ExposeLocalApiLogs = exposeLocalApiLogs;
        LocalApiMaxLogLines = localApiMaxLogLines;
        LocalApiMaxLogBytes = localApiMaxLogBytes;
        CompanionAutoLaunch = companionAutoLaunch;
        CompanionExecutablePath = companionExecutablePath;
        SetConsoleUtf8 = setConsoleUtf8;
        DisableBepInExConsoleLog = disableBepInExConsoleLog;
        HideBepInExConsoleWindow = hideBepInExConsoleWindow;
        EnableNightBusinessDiagnostics = enableNightBusinessDiagnostics;
        NightBusinessDiagnosticsPath = nightBusinessDiagnosticsPath;
        NightBusinessDiagnosticsIntervalSeconds = nightBusinessDiagnosticsIntervalSeconds;
        EnableAggregateModLog = enableAggregateModLog;
        AggregateModLogPath = aggregateModLogPath;
        UpdatesEnabled = updatesEnabled;
        UpdatesAutoCheck = updatesAutoCheck;
        UpdatesCheckIntervalHours = updatesCheckIntervalHours;
        UpdatesIncludePrerelease = updatesIncludePrerelease;
        MaxExtraIngredients = maxExtraIngredients;
        PopularFoodTagOverride = popularFoodTagOverride;
        PopularHateFoodTagOverride = popularHateFoodTagOverride;
        FamousShopOverride = famousShopOverride;
    }

    public ConfigEntry<KeyCode> ToggleKey { get; }
    public ConfigEntry<KeyCode> ControllerToggleKey { get; }
    public ConfigEntry<bool> AutoRefreshRuntime { get; }
    public ConfigEntry<float> AutoRefreshSeconds { get; }
    public ConfigEntry<string> NonGameplaySceneKeywords { get; }
    public ConfigEntry<bool> LocalApiEnabled { get; }
    public ConfigEntry<bool> LocalApiLanEnabled { get; }
    public ConfigEntry<string> LocalApiLanHost { get; }
    public ConfigEntry<int> LocalApiPort { get; }
    public ConfigEntry<string> LocalApiToken { get; }
    public ConfigEntry<bool> ExposeLocalApiLogs { get; }
    public ConfigEntry<int> LocalApiMaxLogLines { get; }
    public ConfigEntry<int> LocalApiMaxLogBytes { get; }
    public ConfigEntry<bool> CompanionAutoLaunch { get; }
    public ConfigEntry<string> CompanionExecutablePath { get; }
    public ConfigEntry<bool> SetConsoleUtf8 { get; }
    public ConfigEntry<bool> DisableBepInExConsoleLog { get; }
    public ConfigEntry<bool> HideBepInExConsoleWindow { get; }
    public ConfigEntry<bool> EnableNightBusinessDiagnostics { get; }
    public ConfigEntry<string> NightBusinessDiagnosticsPath { get; }
    public ConfigEntry<float> NightBusinessDiagnosticsIntervalSeconds { get; }
    public ConfigEntry<bool> EnableAggregateModLog { get; }
    public ConfigEntry<string> AggregateModLogPath { get; }
    public ConfigEntry<bool> UpdatesEnabled { get; }
    public ConfigEntry<bool> UpdatesAutoCheck { get; }
    public ConfigEntry<int> UpdatesCheckIntervalHours { get; }
    public ConfigEntry<bool> UpdatesIncludePrerelease { get; }
    public ConfigEntry<int> MaxExtraIngredients { get; }
    public ConfigEntry<string> PopularFoodTagOverride { get; }
    public ConfigEntry<string> PopularHateFoodTagOverride { get; }
    public ConfigEntry<bool> FamousShopOverride { get; }

    /// <summary>
    /// 从 BepInEx 配置文件中绑定全部配置项，并为首次运行写入默认值。
    /// </summary>
    /// <param name="config">BepInEx 为当前插件提供的配置文件对象。</param>
    /// <returns>包含所有配置 Entry 的强类型访问对象。</returns>
    /// <remarks>
    /// 配置分组名称也是用户可见的 INI 分节名，修改时需要同步 README 和故障排查说明。
    /// 本地 API 始终保留回环监听，并通过 Token 鉴权；LAN 监听只能作为显式开启的附加通道。
    /// </remarks>
    public static StewardPluginConfig Bind(ConfigFile config)
    {
        return new StewardPluginConfig(
            config.Bind("Hotkeys", "ToggleKey", KeyCode.F8, "Switch focus between the game and the mystia-steward-companion companion window."),
            config.Bind("Hotkeys", "ControllerToggleKey", KeyCode.JoystickButton9, "Switch focus between the game and companion window with a controller. Default JoystickButton9 is commonly RS Click."),
            config.Bind("Runtime", "AutoRefreshRuntime", true, "Refresh recommendations from live game runtime data."),
            config.Bind("Runtime", "AutoRefreshSeconds", 3f, "Seconds between live runtime-data refreshes."),
            config.Bind("Runtime", "NonGameplaySceneKeywords", "title,menu,start,select,loading,logo,opening,splash",
                "Comma-separated scene name keywords treated as pages where live runtime data is unavailable."),
            config.Bind("LocalApi", "Enabled", true, "Expose live runtime data to an external companion window over the token-protected local API."),
            config.Bind("LocalApi", "AllowLanConnections", false, "Allow trusted private-network devices to connect. The loopback listener always remains enabled."),
            config.Bind("LocalApi", "LanHost", "auto", "LAN bind host. Use auto to listen on detected private IPv4 addresses, or set a specific private IPv4 address."),
            config.Bind("LocalApi", "Port", 32145, "Local API port for the external companion UI."),
            config.Bind("LocalApi", "Token", "", "Internal local API token. Empty lets the plugin generate one on next launch."),
            config.Bind("LocalApi", "ExposeLogs", true, "Allow the companion window to read BepInEx/LogOutput.log through the token-protected local API."),
            config.Bind("LocalApi", "MaxLogLines", 300, "Maximum LogOutput.log lines returned to the companion window."),
            config.Bind("LocalApi", "MaxLogBytes", 262144, "Maximum LogOutput.log bytes scanned from the end of the file."),
            config.Bind("Companion", "AutoLaunch", true, "Launch the external companion window when the plugin loads if the executable exists."),
            config.Bind("Companion", "ExecutablePath", "", "Optional companion executable path. Empty searches beside the plugin DLL."),
            config.Bind("Ui", "SetConsoleUtf8", true, "Set the Windows console code page and .NET console encoding to UTF-8 after the plugin loads."),
            config.Bind("BepInEx", "DisableConsoleLogWindow", true, "Set BepInEx Logging.Console.Enabled=false for the next game launch."),
            config.Bind("BepInEx", "HideConsoleWindow", true, "Hide the current Windows console window after the plugin loads."),
            config.Bind("Diagnostics", "EnableNightBusinessDiagnostics", false, "Write night-business detection snapshots to an external file for debugging."),
            config.Bind("Diagnostics", "NightBusinessDiagnosticsPath", "", "Optional diagnostics log path. Empty uses BepInEx/config/mystia-steward-companion/night-business-diagnostics.log."),
            config.Bind("Diagnostics", "NightBusinessDiagnosticsIntervalSeconds", 2f, "Minimum seconds between diagnostics snapshots."),
            config.Bind("Diagnostics", "EnableAggregateModLog", false, "Write a troubleshooting aggregate log that captures all BepInEx log sources while enabled."),
            config.Bind("Diagnostics", "AggregateModLogPath", "", "Optional aggregate log path. Empty uses BepInEx/config/MystiaStewardCompanion/aggregate-mod.log. The file rotates every 10 MB without a total cap."),
            config.Bind("Updates", "Enabled", true, "Allow the plugin to check GitHub Releases for mystia-steward-companion updates."),
            config.Bind("Updates", "AutoCheck", true, "Check for updates automatically when the local API starts."),
            config.Bind("Updates", "CheckIntervalHours", 24, "Minimum hours between automatic update checks."),
            config.Bind("Updates", "IncludePrerelease", false, "Include GitHub prerelease versions when checking for updates."),
            config.Bind("Rare", "MaxExtraIngredients", 4, "Maximum extra ingredients to search for rare recipes."),
            config.Bind("Overrides", "PopularFoodTag", "", "Optional popular liked food tag override. Empty uses live runtime data."),
            config.Bind("Overrides", "PopularHateFoodTag", "", "Optional popular hated food tag override. Empty uses live runtime data."),
            config.Bind("Overrides", "FamousShop", false, "Force famous shop effect on in addition to live runtime data."));
    }
}
