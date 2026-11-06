using BepInEx.Logging;
using MystiaStewardCompanion.Plugin;

namespace MystiaStewardCompanion.Ui;

/// <summary>在插件初始化与 Unity 行为创建之间传递配置和日志依赖；未配置时延后创建控制器。</summary>
/// <remarks>由插件及 Unity 生命周期顺序调用，本类没有并发同步或控制器单例语义。</remarks>
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
