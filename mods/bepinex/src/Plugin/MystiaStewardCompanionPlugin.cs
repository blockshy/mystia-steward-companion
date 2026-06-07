using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Ui;
using UnityEngine;

namespace MystiaStewardCompanion.Plugin;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class MystiaStewardCompanionPlugin : BasePlugin
{
    private const string LegacyPluginGuid = "com.tyukki.mystia-steward";

    public const string PluginGuid = "com.tyukki.mystia-steward-companion";
    public const string PluginName = "mystia-steward-companion";
    public const string PluginVersion = "1.0.1";

    public override void Load()
    {
        if (TryMigrateLegacyConfig(Log))
        {
            Config.Reload();
        }

        var settings = StewardPluginConfig.Bind(Config);
        if (settings.SetConsoleUtf8.Value)
        {
            ConsoleEncodingHelper.TryUseUtf8(Log);
        }

        BepInExConsoleHelper.Apply(
            settings.DisableBepInExConsoleLog.Value,
            settings.HideBepInExConsoleWindow.Value,
            Log);

        SpecialOrderRuntimeCapture.Attach(Log);
        if (settings.EnableSpecialOrderLogFallback.Value)
        {
            SpecialOrderLogCapture.Attach(Log);
        }

        StewardOverlayRuntimeContext.Configure(settings, Log);
        ClassInjector.RegisterTypeInIl2Cpp<StewardOverlayBehaviour>();

        var gameObject = new GameObject("mystia-steward-companion Overlay");
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        gameObject.hideFlags = HideFlags.HideAndDontSave;

        gameObject.AddComponent<StewardOverlayBehaviour>();

        Log.LogInfo($"{PluginName} {PluginVersion} loaded. Press {settings.ToggleKey.Value} to open or focus the companion window.");
    }

    private static bool TryMigrateLegacyConfig(ManualLogSource log)
    {
        try
        {
            var oldPath = Path.Combine(Paths.ConfigPath, $"{LegacyPluginGuid}.cfg");
            var newPath = Path.Combine(Paths.ConfigPath, $"{PluginGuid}.cfg");
            if (!File.Exists(oldPath) || File.Exists(newPath)) return false;

            File.Copy(oldPath, newPath);
            log.LogInfo($"Migrated legacy config to {PluginGuid}.cfg.");
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning($"Legacy config migration failed: {ex.Message}");
            return false;
        }
    }
}
