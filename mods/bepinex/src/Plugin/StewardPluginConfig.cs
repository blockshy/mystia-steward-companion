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
            ToggleKey = config.Bind("Hotkeys", "ToggleKey", KeyCode.F8, "在游戏和伴随窗口之间切换焦点。"),
            ControllerToggleKey = config.Bind("Hotkeys", "ControllerToggleKey", KeyCode.JoystickButton9, "使用手柄在游戏和伴随窗口之间切换焦点。默认的 JoystickButton9 通常对应右摇杆按下。"),
            AutoRefreshRuntime = config.Bind("Runtime", "AutoRefreshRuntime", true, "根据游戏实时数据自动刷新推荐。"),
            AutoRefreshSeconds = config.Bind("Runtime", "AutoRefreshSeconds", 3f, "游戏实时数据的刷新间隔，单位为秒。"),
            NonGameplaySceneKeywords = config.Bind("Runtime", "NonGameplaySceneKeywords", "title,menu,start,select,loading,logo,opening,splash",
                "以英文逗号分隔的场景名称关键词；匹配这些关键词时不读取游戏实时数据。"),
            LocalApiEnabled = config.Bind("LocalApi", "Enabled", true, "通过受 Token 保护的本地 API 向伴随窗口提供游戏实时数据。"),
            LocalApiLanEnabled = config.Bind("LocalApi", "AllowLanConnections", false, "允许受信任的局域网设备连接。本机连接始终保持开启。"),
            LocalApiLanHost = config.Bind("LocalApi", "LanHost", "auto", "局域网监听地址。auto 会监听检测到的私有 IPv4 地址，也可填写一个具体的私有 IPv4 地址。"),
            LocalApiPort = config.Bind("LocalApi", "Port", 32145, "伴随窗口使用的本地 API 端口。"),
            LocalApiToken = config.Bind("LocalApi", "Token", "", "本地 API 的内部 Token。留空时，插件会在下次启动时自动生成。"),
            CompanionAutoLaunch = config.Bind("Companion", "AutoLaunch", true, "插件加载后，如果找到伴随程序，则自动启动伴随窗口。"),
            CompanionExecutablePath = config.Bind("Companion", "ExecutablePath", "", "伴随程序的可选路径。留空时在插件 DLL 所在目录旁查找。"),
            SetConsoleUtf8 = config.Bind("Ui", "SetConsoleUtf8", true, "插件加载后，将 Windows 控制台代码页和 .NET 控制台编码设为 UTF-8。"),
            ShowBepInExConsoleOnStartup = config.Bind("Diagnostics", "ShowBepInExConsoleOnStartup", false, "插件加载时显示 BepInEx 控制台，供本机排查问题。默认关闭。"),
            EnableAggregateModLog = config.Bind("Diagnostics", "EnableAggregateModLog", false, "开启后写入汇总诊断日志，其中包含全部 BepInEx 日志来源。"),
            AggregateModLogPath = config.Bind("Diagnostics", "AggregateModLogPath", "", "汇总日志的可选路径。留空时使用 BepInEx/config/MystiaStewardCompanion/aggregate-mod.log。"),
            AggregateModLogMaxFileCount = config.Bind("Diagnostics", "AggregateModLogMaxFileCount", AggregateModLogService.DefaultMaxFileCount, "汇总日志最多保留的文件数，包含当前文件。默认保留 30 个；每个文件达到 10 MB 时轮换，因此约占 300 MB。"),
            UpdatesEnabled = config.Bind("Updates", "Enabled", true, "允许插件从 GitHub Releases 检查 mystia-steward-companion 更新。"),
            UpdatesAutoCheck = config.Bind("Updates", "AutoCheck", true, "本地 API 启动后自动检查更新。"),
            UpdatesCheckIntervalHours = config.Bind("Updates", "CheckIntervalHours", 24, "两次自动检查更新之间的最短间隔，单位为小时。"),
            UpdatesIncludePrerelease = config.Bind("Updates", "IncludePrerelease", false, "检查更新时包含 GitHub 预发布版本。"),
            PopularFoodTagOverride = config.Bind("Overrides", "PopularFoodTag", "", "可选的流行喜爱料理标签。留空时使用游戏实时数据。"),
            PopularHateFoodTagOverride = config.Bind("Overrides", "PopularHateFoodTag", "", "可选的流行厌恶料理标签。留空时使用游戏实时数据。"),
            FamousShopOverride = config.Bind("Overrides", "FamousShop", false, "在游戏实时状态之外，强制启用名店效果。"),
        };
    }
}
