using BepInEx.Configuration;
using UnityEngine;

namespace MystiaSteward.Plugin;

public sealed class StewardPluginConfig
{
    private StewardPluginConfig(
        ConfigEntry<KeyCode> toggleKey,
        ConfigEntry<KeyCode> controllerToggleKey,
        ConfigEntry<KeyCode> reloadKey,
        ConfigEntry<bool> autoRefreshRuntime,
        ConfigEntry<float> autoRefreshSeconds,
        ConfigEntry<string> nonGameplaySceneKeywords,
        ConfigEntry<string> defaultPlace,
        ConfigEntry<string> language,
        ConfigEntry<bool> enableInGameOverlay,
        ConfigEntry<float> panelOpacity,
        ConfigEntry<float> windowX,
        ConfigEntry<float> windowY,
        ConfigEntry<float> windowWidth,
        ConfigEntry<float> windowHeight,
        ConfigEntry<bool> blockGameInputOnPanel,
        ConfigEntry<bool> drawOverlayCursor,
        ConfigEntry<bool> localApiEnabled,
        ConfigEntry<string> localApiHost,
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
        ConfigEntry<bool> enableSpecialOrderLogFallback,
        ConfigEntry<int> maxNormalRows,
        ConfigEntry<int> maxRareRows,
        ConfigEntry<int> maxExtraIngredients,
        ConfigEntry<string> popularFoodTagOverride,
        ConfigEntry<string> popularHateFoodTagOverride,
        ConfigEntry<bool> famousShopOverride)
    {
        ToggleKey = toggleKey;
        ControllerToggleKey = controllerToggleKey;
        ReloadKey = reloadKey;
        AutoRefreshRuntime = autoRefreshRuntime;
        AutoRefreshSeconds = autoRefreshSeconds;
        NonGameplaySceneKeywords = nonGameplaySceneKeywords;
        DefaultPlace = defaultPlace;
        Language = language;
        EnableInGameOverlay = enableInGameOverlay;
        PanelOpacity = panelOpacity;
        WindowX = windowX;
        WindowY = windowY;
        WindowWidth = windowWidth;
        WindowHeight = windowHeight;
        BlockGameInputOnPanel = blockGameInputOnPanel;
        DrawOverlayCursor = drawOverlayCursor;
        LocalApiEnabled = localApiEnabled;
        LocalApiHost = localApiHost;
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
        EnableSpecialOrderLogFallback = enableSpecialOrderLogFallback;
        MaxNormalRows = maxNormalRows;
        MaxRareRows = maxRareRows;
        MaxExtraIngredients = maxExtraIngredients;
        PopularFoodTagOverride = popularFoodTagOverride;
        PopularHateFoodTagOverride = popularHateFoodTagOverride;
        FamousShopOverride = famousShopOverride;
    }

    public ConfigEntry<KeyCode> ToggleKey { get; }
    public ConfigEntry<KeyCode> ControllerToggleKey { get; }
    public ConfigEntry<KeyCode> ReloadKey { get; }
    public ConfigEntry<bool> AutoRefreshRuntime { get; }
    public ConfigEntry<float> AutoRefreshSeconds { get; }
    public ConfigEntry<string> NonGameplaySceneKeywords { get; }
    public ConfigEntry<string> DefaultPlace { get; }
    public ConfigEntry<string> Language { get; }
    public ConfigEntry<bool> EnableInGameOverlay { get; }
    public ConfigEntry<float> PanelOpacity { get; }
    public ConfigEntry<float> WindowX { get; }
    public ConfigEntry<float> WindowY { get; }
    public ConfigEntry<float> WindowWidth { get; }
    public ConfigEntry<float> WindowHeight { get; }
    public ConfigEntry<bool> BlockGameInputOnPanel { get; }
    public ConfigEntry<bool> DrawOverlayCursor { get; }
    public ConfigEntry<bool> LocalApiEnabled { get; }
    public ConfigEntry<string> LocalApiHost { get; }
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
    public ConfigEntry<bool> EnableSpecialOrderLogFallback { get; }
    public ConfigEntry<int> MaxNormalRows { get; }
    public ConfigEntry<int> MaxRareRows { get; }
    public ConfigEntry<int> MaxExtraIngredients { get; }
    public ConfigEntry<string> PopularFoodTagOverride { get; }
    public ConfigEntry<string> PopularHateFoodTagOverride { get; }
    public ConfigEntry<bool> FamousShopOverride { get; }

    public static StewardPluginConfig Bind(ConfigFile config)
    {
        return new StewardPluginConfig(
            config.Bind("Hotkeys", "ToggleKey", KeyCode.F8, "Open or focus the Mystia Steward companion window. If Ui.EnableInGameOverlay is true, this toggles the in-game overlay instead."),
            config.Bind("Hotkeys", "ControllerToggleKey", KeyCode.JoystickButton9, "Open or focus the companion window with a controller. Default JoystickButton9 is commonly RS Click."),
            config.Bind("Hotkeys", "ReloadKey", KeyCode.F9, "Refresh runtime data detection."),
            config.Bind("Runtime", "AutoRefreshRuntime", true, "Refresh recommendations from live game runtime data."),
            config.Bind("Runtime", "AutoRefreshSeconds", 3f, "Seconds between live runtime-data refreshes."),
            config.Bind("Runtime", "NonGameplaySceneKeywords", "title,menu,start,select,loading,logo,opening,splash",
                "Comma-separated scene name keywords treated as pages where live runtime data is unavailable."),
            config.Bind("Ui", "DefaultPlace", "妖怪兽道", "Default region shown by the overlay."),
            config.Bind("Ui", "Language", "zh-CN", "Overlay language. Supported values: zh-CN, en."),
            config.Bind("Ui", "EnableInGameOverlay", false, "Enable the legacy in-game IMGUI overlay. Disabled by default so the companion window is the primary UI."),
            config.Bind("Ui", "PanelOpacity", 0.97f, "Overlay background opacity from 0.50 to 1.00."),
            config.Bind("Ui", "WindowX", 48f, "Overlay window X position."),
            config.Bind("Ui", "WindowY", 48f, "Overlay window Y position."),
            config.Bind("Ui", "WindowWidth", 980f, "Overlay window width."),
            config.Bind("Ui", "WindowHeight", 680f, "Overlay window height."),
            config.Bind("Ui", "BlockGameInputOnPanel", true, "Try to prevent panel mouse input from also reaching the game."),
            config.Bind("Ui", "DrawOverlayCursor", true, "Draw a visible cursor marker above the overlay while the panel is open."),
            config.Bind("LocalApi", "Enabled", true, "Expose live runtime data to an external companion window over a loopback-only local API."),
            config.Bind("LocalApi", "Host", "127.0.0.1", "Loopback bind host. Keep 127.0.0.1 to avoid proxy, localhost, and IPv6 issues."),
            config.Bind("LocalApi", "Port", 32145, "Loopback local API port for the external companion UI."),
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
            config.Bind("Diagnostics", "NightBusinessDiagnosticsPath", "", "Optional diagnostics log path. Empty uses BepInEx/config/MystiaSteward/night-business-diagnostics.log."),
            config.Bind("Diagnostics", "NightBusinessDiagnosticsIntervalSeconds", 2f, "Minimum seconds between diagnostics snapshots."),
            config.Bind("Diagnostics", "EnableSpecialOrderLogFallback", false, "Fallback to parsing game console order logs if runtime order capture does not detect orders."),
            config.Bind("Ui", "MaxNormalRows", 8, "Maximum normal recommendation rows."),
            config.Bind("Ui", "MaxRareRows", 8, "Maximum rare recommendation rows."),
            config.Bind("Rare", "MaxExtraIngredients", 4, "Maximum extra ingredients to search for rare recipes."),
            config.Bind("Overrides", "PopularFoodTag", "", "Optional popular liked food tag override. Empty uses live runtime data."),
            config.Bind("Overrides", "PopularHateFoodTag", "", "Optional popular hated food tag override. Empty uses live runtime data."),
            config.Bind("Overrides", "FamousShop", false, "Force famous shop effect on in addition to live runtime data."));
    }
}
