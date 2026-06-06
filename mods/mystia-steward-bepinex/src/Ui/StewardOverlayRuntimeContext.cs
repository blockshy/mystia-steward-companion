using BepInEx.Logging;
using MystiaSteward.Plugin;

namespace MystiaSteward.Ui;

internal static class StewardOverlayRuntimeContext
{
    private static StewardPluginConfig? _config;
    private static ManualLogSource? _log;

    public static void Configure(StewardPluginConfig config, ManualLogSource log)
    {
        _config = config;
        _log = log;
    }

    public static StewardOverlayController? CreateController()
    {
        if (_config == null || _log == null) return null;

        var controller = new StewardOverlayController();
        controller.Initialize(_config, _log);
        return controller;
    }
}
