using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using MystiaSteward.Save;
using MystiaSteward.Ui;
using UnityEngine;

namespace MystiaSteward.Plugin;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class MystiaStewardPlugin : BasePlugin
{
    public const string PluginGuid = "com.tyukki.mystia-steward";
    public const string PluginName = "Mystia Steward";
    public const string PluginVersion = "0.1.25";

    public override void Load()
    {
        var settings = StewardPluginConfig.Bind(Config);
        if (settings.SetConsoleUtf8.Value)
        {
            ConsoleEncodingHelper.TryUseUtf8(Log);
        }

        SpecialOrderRuntimeCapture.Attach(Log);
        if (settings.EnableSpecialOrderLogFallback.Value)
        {
            SpecialOrderLogCapture.Attach(Log);
        }

        StewardOverlayRuntimeContext.Configure(settings, Log);
        ClassInjector.RegisterTypeInIl2Cpp<StewardOverlayBehaviour>();

        var gameObject = new GameObject("Mystia Steward Overlay");
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        gameObject.hideFlags = HideFlags.HideAndDontSave;

        gameObject.AddComponent<StewardOverlayBehaviour>();

        CompanionProcessLauncher.TryAutoLaunch(settings, Log);

        Log.LogInfo($"{PluginName} {PluginVersion} loaded. Press {settings.ToggleKey.Value} to open or focus the companion window.");
    }
}
