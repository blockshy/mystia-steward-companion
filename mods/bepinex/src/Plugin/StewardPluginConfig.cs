using BepInEx.Configuration;
using MystiaStewardCompanion.Save;
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
    private StewardPluginConfig()
    {
    }

    public ConfigEntry<KeyCode> ToggleKey { get; private init; } = null!;
    public ConfigEntry<KeyCode> ControllerToggleKey { get; private init; } = null!;
    public ConfigEntry<bool> AutoRefreshRuntime { get; private init; } = null!;
    public ConfigEntry<float> AutoRefreshSeconds { get; private init; } = null!;
    public ConfigEntry<string> NonGameplaySceneKeywords { get; private init; } = null!;
    public ConfigEntry<bool> LocalApiEnabled { get; private init; } = null!;
    public ConfigEntry<bool> LocalApiLanEnabled { get; private init; } = null!;
    public ConfigEntry<string> LocalApiLanHost { get; private init; } = null!;
    public ConfigEntry<int> LocalApiPort { get; private init; } = null!;
    public ConfigEntry<string> LocalApiToken { get; private init; } = null!;
    public ConfigEntry<bool> CompanionAutoLaunch { get; private init; } = null!;
    public ConfigEntry<string> CompanionExecutablePath { get; private init; } = null!;
    public ConfigEntry<bool> SetConsoleUtf8 { get; private init; } = null!;
    public ConfigEntry<bool> ShowBepInExConsoleOnStartup { get; private init; } = null!;
    public ConfigEntry<bool> EnableAggregateModLog { get; private init; } = null!;
    public ConfigEntry<string> AggregateModLogPath { get; private init; } = null!;
    public ConfigEntry<int> AggregateModLogMaxFileCount { get; private init; } = null!;
    public ConfigEntry<bool> UpdatesEnabled { get; private init; } = null!;
    public ConfigEntry<bool> UpdatesAutoCheck { get; private init; } = null!;
    public ConfigEntry<int> UpdatesCheckIntervalHours { get; private init; } = null!;
    public ConfigEntry<bool> UpdatesIncludePrerelease { get; private init; } = null!;
    public ConfigEntry<string> PopularFoodTagOverride { get; private init; } = null!;
    public ConfigEntry<string> PopularHateFoodTagOverride { get; private init; } = null!;
    public ConfigEntry<bool> FamousShopOverride { get; private init; } = null!;

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
        return new StewardPluginConfig
        {
            ToggleKey = config.Bind("Hotkeys", "ToggleKey", KeyCode.F8, "Switch focus between the game and the mystia-steward-companion companion window."),
            ControllerToggleKey = config.Bind("Hotkeys", "ControllerToggleKey", KeyCode.JoystickButton9, "Switch focus between the game and companion window with a controller. Default JoystickButton9 is commonly RS Click."),
            AutoRefreshRuntime = config.Bind("Runtime", "AutoRefreshRuntime", true, "Refresh recommendations from live game runtime data."),
            AutoRefreshSeconds = config.Bind("Runtime", "AutoRefreshSeconds", 3f, "Seconds between live runtime-data refreshes."),
            NonGameplaySceneKeywords = config.Bind("Runtime", "NonGameplaySceneKeywords", "title,menu,start,select,loading,logo,opening,splash",
                "Comma-separated scene name keywords treated as pages where live runtime data is unavailable."),
            LocalApiEnabled = config.Bind("LocalApi", "Enabled", true, "Expose live runtime data to an external companion window over the token-protected local API."),
            LocalApiLanEnabled = config.Bind("LocalApi", "AllowLanConnections", false, "Allow trusted private-network devices to connect. The loopback listener always remains enabled."),
            LocalApiLanHost = config.Bind("LocalApi", "LanHost", "auto", "LAN bind host. Use auto to listen on detected private IPv4 addresses, or set a specific private IPv4 address."),
            LocalApiPort = config.Bind("LocalApi", "Port", 32145, "Local API port for the external companion UI."),
            LocalApiToken = config.Bind("LocalApi", "Token", "", "Internal local API token. Empty lets the plugin generate one on next launch."),
            CompanionAutoLaunch = config.Bind("Companion", "AutoLaunch", true, "Launch the external companion window when the plugin loads if the executable exists."),
            CompanionExecutablePath = config.Bind("Companion", "ExecutablePath", "", "Optional companion executable path. Empty searches beside the plugin DLL."),
            SetConsoleUtf8 = config.Bind("Ui", "SetConsoleUtf8", true, "Set the Windows console code page and .NET console encoding to UTF-8 after the plugin loads."),
            ShowBepInExConsoleOnStartup = config.Bind("Diagnostics", "ShowBepInExConsoleOnStartup", false, "Show the BepInEx console window when the plugin loads for local troubleshooting. Disabled by default."),
            EnableAggregateModLog = config.Bind("Diagnostics", "EnableAggregateModLog", false, "Write a troubleshooting aggregate log that captures all BepInEx log sources while enabled."),
            AggregateModLogPath = config.Bind("Diagnostics", "AggregateModLogPath", "", "Optional aggregate log path. Empty uses BepInEx/config/MystiaStewardCompanion/aggregate-mod.log."),
            AggregateModLogMaxFileCount = config.Bind("Diagnostics", "AggregateModLogMaxFileCount", AggregateModLogService.DefaultMaxFileCount, "Maximum aggregate log files to keep, including the active file. Default 30 keeps about 300 MB because each file rotates at 10 MB."),
            UpdatesEnabled = config.Bind("Updates", "Enabled", true, "Allow the plugin to check GitHub Releases for mystia-steward-companion updates."),
            UpdatesAutoCheck = config.Bind("Updates", "AutoCheck", true, "Check for updates automatically when the local API starts."),
            UpdatesCheckIntervalHours = config.Bind("Updates", "CheckIntervalHours", 24, "Minimum hours between automatic update checks."),
            UpdatesIncludePrerelease = config.Bind("Updates", "IncludePrerelease", false, "Include GitHub prerelease versions when checking for updates."),
            PopularFoodTagOverride = config.Bind("Overrides", "PopularFoodTag", "", "Optional popular liked food tag override. Empty uses live runtime data."),
            PopularHateFoodTagOverride = config.Bind("Overrides", "PopularHateFoodTag", "", "Optional popular hated food tag override. Empty uses live runtime data."),
            FamousShopOverride = config.Bind("Overrides", "FamousShop", false, "Force famous shop effect on in addition to live runtime data."),
        };
    }
}
