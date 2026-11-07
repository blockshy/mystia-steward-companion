using BepInEx.Configuration;
using System.Security.Cryptography;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
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
    private ConfigFile _configFile = null!;

    /// <summary>
    /// 连接设置作为一份表单统一保存，避免逐字段自动 Save 产生部分内存生效。
    /// 文件系统保存失败时恢复旧内存值；不声称已经回滚可能完成的磁盘写入，调用方必须明确报错。
    /// Local API 的管理写锁串行保护调用，故暂时关闭自动 Save 不与其它管理表单交错。
    /// </summary>
    internal void SaveLocalApiConnection(bool enabled, string host, string token)
    {
        var previousEnabled = LocalApiLanEnabled.Value;
        var previousHost = LocalApiLanHost.Value;
        var previousToken = LocalApiToken.Value;
        var autoSave = _configFile.SaveOnConfigSet;
        _configFile.SaveOnConfigSet = false;
        try
        {
            LocalApiLanEnabled.Value = enabled;
            LocalApiLanHost.Value = host;
            LocalApiToken.Value = token;
            _configFile.Save();
        }
        catch
        {
            LocalApiLanEnabled.Value = previousEnabled;
            LocalApiLanHost.Value = previousHost;
            LocalApiToken.Value = previousToken;
            throw;
        }
        finally { _configFile.SaveOnConfigSet = autoSave; }
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
    /// <param name="config">由调用方使用统一数据根构造、关闭构造时保存的配置文件对象。</param>
    /// <returns>包含所有配置 Entry 的强类型访问对象。</returns>
    /// <remarks>
    /// 配置分组名称也是用户可见的 INI 分节名，修改时需要同步 README 和故障排查说明。
    /// 本地 API 始终保留回环监听，并通过 Token 鉴权；LAN 监听只能作为显式开启的附加通道。
    /// </remarks>
    public static StewardPluginConfig Bind(ConfigFile config)
    {
        // 即使调用方绕过生产迁移入口，仍先拒绝损坏原件。真实转换器的预校验必须发生在
        // 第一个 Bind 之前，否则框架可能用默认值替代非法值，并在随后保存时掩盖错误。
        if (File.Exists(config.ConfigFilePath))
            ModDataLayoutMigration.ValidateConfiguration(File.ReadAllBytes(config.ConfigFilePath), ValidateStoredConfiguration);
        var autoSave = config.SaveOnConfigSet;
        config.SaveOnConfigSet = false;
        try
        {
            var settings = new StewardPluginConfig
            {
                _configFile = config,
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
                LocalApiToken = config.Bind("LocalApi", "Token", "", "Local API password: 8–128 visible ASCII characters, without whitespace. Empty lets the plugin generate a random token on next launch."),
                CompanionAutoLaunch = config.Bind("Companion", "AutoLaunch", true, "Launch the external companion window when the plugin loads if the executable exists."),
                CompanionExecutablePath = config.Bind("Companion", "ExecutablePath", "", "Optional Flutter companion executable path. Empty selects companion/mystia-steward-companion.exe inside the complete plugin package."),
                SetConsoleUtf8 = config.Bind("Ui", "SetConsoleUtf8", true, "Set the Windows console code page and .NET console encoding to UTF-8 after the plugin loads."),
                ShowBepInExConsoleOnStartup = config.Bind("Diagnostics", "ShowBepInExConsoleOnStartup", false, "Show the BepInEx console window when the plugin loads for local troubleshooting. Disabled by default."),
                EnableAggregateModLog = config.Bind("Diagnostics", "EnableAggregateModLog", false, "Write a troubleshooting aggregate log that captures all BepInEx log sources while enabled."),
                AggregateModLogPath = config.Bind("Diagnostics", "AggregateModLogPath", "", "Aggregate log location is managed under BepInEx/config/MystiaStewardCompanion; legacy custom paths are reset without deleting old files."),
                AggregateModLogMaxFileCount = config.Bind("Diagnostics", "AggregateModLogMaxFileCount", AggregateModLogService.DefaultMaxFileCount, "Aggregate log files to keep: 1–30, including the active file. Default 30 keeps about 300 MB because each file rotates at 10 MB."),
                UpdatesEnabled = config.Bind("Updates", "Enabled", true, "Allow the plugin to check GitHub Releases for mystia-steward-companion updates."),
                UpdatesAutoCheck = config.Bind("Updates", "AutoCheck", true, "Check for updates automatically when the local API starts."),
                UpdatesCheckIntervalHours = config.Bind("Updates", "CheckIntervalHours", 24, "Minimum hours between automatic update checks."),
                UpdatesIncludePrerelease = config.Bind("Updates", "IncludePrerelease", false, "Include GitHub prerelease versions when checking for updates."),
                PopularFoodTagOverride = config.Bind("Overrides", "PopularFoodTag", "", "Optional popular liked food tag override. Empty uses live runtime data."),
                PopularHateFoodTagOverride = config.Bind("Overrides", "PopularHateFoodTag", "", "Optional popular hated food tag override. Empty uses live runtime data."),
                FamousShopOverride = config.Bind("Overrides", "FamousShop", false, "Force famous shop effect on in addition to live runtime data."),
            };
            // 初次迁移原件已经备份；完成迁移后的手工旧路径也只归一为空值。运行期日志根由
            // ModDataPaths 决定，不再允许配置把本插件的新日志写回旧外部位置。
            settings.AggregateModLogPath.Value = "";
            // 首次 Token 在启动配置阶段生成，不能延后到 Unity 控制器初始化后才写入。
            // 非空旧 Token 已经通过前置校验；这里只处理用户明确留空或首次安装的情况。
            if (settings.LocalApiToken.Value.Length == 0)
                settings.LocalApiToken.Value = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            config.Save();
            // Save 成功后刷新实际文件并回读所有已绑定项，随后才允许移除旧根 CFG。
            // 此收尾只证明主配置已采用新位置，不宣称业务 JSON 或 Flutter 已完成初始化。
            using (var persisted = new FileStream(config.ConfigFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                persisted.Flush(flushToDisk: true);
                if (persisted.Length > 1024 * 1024) throw new ModDataValidationException("主配置大小无效。");
                using var buffer = new MemoryStream();
                persisted.CopyTo(buffer);
                settings.ValidatePersistedConfiguration(buffer.ToArray());
            }
            return settings;
        }
        finally { config.SaveOnConfigSet = autoSave; }
    }

    /// <summary>
    /// 比较磁盘上每个已绑定项与当前实例实际采用的值。使用真实 BepInEx 转换器而非序列化文本
    /// 等值，允许合法格式差异；缺项、类型错误或 Token 未持久化均不能授权旧 CFG 清理。
    /// </summary>
    internal void ValidatePersistedConfiguration(byte[] bytes)
    {
        ModDataLayoutMigration.ValidateConfiguration(bytes, entries =>
        {
            ValidateStoredConfiguration(entries);
            var stored = entries.ToDictionary(entry => (entry.Section, entry.Key), entry => entry.Value);
            foreach (var (definition, entry) in _configFile)
            {
                if (!stored.TryGetValue((definition.Section, definition.Key), out var value)
                    || !Equals(TomlTypeConverter.ConvertToValue(value, entry.SettingType), entry.BoxedValue))
                    throw new ModDataValidationException("主配置回读结果与已绑定设置不一致，旧配置未清理。");
            }
            if (!LocalApiConnectionPolicy.IsUsableToken(LocalApiToken.Value))
                throw new ModDataValidationException("主配置凭据尚未完整持久化，旧配置未清理。");
        });
    }

    /// <summary>
    /// 只读、有界识别公开 v1.3.1 的 Windows Tauri 程序；文件名、目录名或版本资源不能代替
    /// 精确内容身份。未知/无法读取的自定义程序保持原值，提示中不得输出配置值或私有位置。
    /// </summary>
    internal static bool IsKnownLegacyCompanion(string encodedPath, Action<string>? warning = null)
    {
        var configured = (string)TomlTypeConverter.ConvertToValue(encodedPath, typeof(string));
        if (string.IsNullOrWhiteSpace(configured)) return false;
        try
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
            for (string? current = path; current != null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked executable is not identified.");
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            const long expectedLength = 10388992;
            if (source.Length == expectedLength)
            {
                using var digest = SHA256.Create();
                var actual = Convert.ToHexString(digest.ComputeHash(source)).ToLowerInvariant();
                if (actual == "fa7a5ef5e6a7f5f4a11e9a9b9516fa051f9828aa0a1bb3d0aff740cbf0c4b9d0")
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        warning?.Invoke("旧配置中的自定义客户端未识别为公开 v1.3.1，已保留原设置；请核对它是否指向新版 Flutter 客户端。");
        return false;
    }

    /// <summary>
    /// 使用实际 BepInEx TOML 转换规则预检所有现用项；未知旧项保留，不假定可绑定为当前项。
    /// 错误只报告固定项名，不能透传含配置值的转换异常。非空旧 Token 不兼容时停止启动，
    /// 不把“需要用户修正的凭据”折叠为空值交给自动生成逻辑。
    /// </summary>
    internal static void ValidateStoredConfiguration(IReadOnlyList<ModConfigEntry> entries)
    {
        foreach (var entry in entries)
        {
            var identity = entry.Section + "/" + entry.Key;
            var type = identity switch
            {
                "Hotkeys/ToggleKey" or "Hotkeys/ControllerToggleKey" => typeof(KeyCode),
                "Runtime/AutoRefreshRuntime" or "LocalApi/Enabled" or "LocalApi/AllowLanConnections"
                    or "Companion/AutoLaunch" or "Ui/SetConsoleUtf8" or "Diagnostics/ShowBepInExConsoleOnStartup"
                    or "Diagnostics/EnableAggregateModLog" or "Updates/Enabled" or "Updates/AutoCheck"
                    or "Updates/IncludePrerelease" or "Overrides/FamousShop" => typeof(bool),
                "LocalApi/Port" or "Diagnostics/AggregateModLogMaxFileCount" or "Updates/CheckIntervalHours" => typeof(int),
                "Runtime/AutoRefreshSeconds" => typeof(float),
                "Runtime/NonGameplaySceneKeywords" or "LocalApi/LanHost" or "LocalApi/Token"
                    or "Companion/ExecutablePath" or "Diagnostics/AggregateModLogPath"
                    or "Overrides/PopularFoodTag" or "Overrides/PopularHateFoodTag" => typeof(string),
                _ => null,
            };
            if (type == null) continue;
            try
            {
                var value = TomlTypeConverter.ConvertToValue(entry.Value, type);
                if (type == typeof(float) && !float.IsFinite((float)value)
                    || type == typeof(KeyCode) && !Enum.IsDefined(typeof(KeyCode), value))
                    throw new FormatException();
                if (identity == "LocalApi/Token" && value is string token && token.Length != 0
                    && !LocalApiConnectionPolicy.IsUsableToken(token)) throw new FormatException();
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or InvalidCastException)
            { throw new ModDataValidationException($"主配置项 {identity} 无效；原值已保留，请修正后重试。"); }
        }
    }
}
