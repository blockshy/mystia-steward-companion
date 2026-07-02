using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Ui;
using UnityEngine;

namespace MystiaStewardCompanion.Plugin;

/// <summary>
/// BepInEx IL2CPP 插件入口，负责在游戏进程内完成 Mod 的一次性启动工作。
/// </summary>
/// <remarks>
/// 该类只做启动编排：读取配置、挂接运行时捕获、注册 Unity 行为组件，并创建常驻
/// <see cref="StewardOverlayBehaviour"/>。后续轮询、快照发布、本地 API 和自动化动作均由
/// Overlay Controller 在 Unity 主线程中处理，避免在 BepInEx 加载阶段直接访问尚未就绪的游戏对象。
/// </remarks>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class MystiaStewardCompanionPlugin : BasePlugin
{
    private const string LegacyPluginGuid = "com.tyukki.mystia-steward";

    public const string PluginGuid = "com.tyukki.mystia-steward-companion";
    public const string PluginName = "mystia-steward-companion";
    public const string PluginVersion = "1.1.0";

    /// <summary>
    /// BepInEx 调用的插件加载入口。
    /// </summary>
    /// <remarks>
    /// 这里会按固定顺序完成配置迁移、控制台设置、Harmony 捕获注册、IL2CPP 类型注册和常驻
    /// GameObject 创建。顺序不能随意调整：运行时捕获需要尽早挂接，Overlay 行为必须先注册到
    /// IL2CPP 运行时再添加到 Unity 对象上。
    /// </remarks>
    public override void Load()
    {
        if (TryMigrateLegacyConfig(Log))
        {
            Config.Reload();
        }

        var settings = StewardPluginConfig.Bind(Config);
        AggregateModLogService.Configure(settings.EnableAggregateModLog.Value, settings.AggregateModLogPath.Value);
        if (settings.SetConsoleUtf8.Value)
        {
            ConsoleEncodingHelper.TryUseUtf8(Log);
        }

        BepInExConsoleHelper.Apply(Log);

        SpecialOrderRuntimeCapture.Attach(Log);
        NormalOrderRuntimeCapture.Attach(Log);
        RuntimeSceneReadinessCapture.Attach(Log);
        RuntimeUiPinningService.Attach(Log);

        StewardOverlayRuntimeContext.Configure(settings, Log);
        ClassInjector.RegisterTypeInIl2Cpp<StewardOverlayBehaviour>();

        var gameObject = new GameObject("mystia-steward-companion Overlay");
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        gameObject.hideFlags = HideFlags.HideAndDontSave;

        gameObject.AddComponent<StewardOverlayBehaviour>();

        Log.LogInfo($"{PluginName} {PluginVersion} loaded. Press {settings.ToggleKey.Value} to open or focus the companion window.");
    }

    /// <summary>
    /// 将旧插件 GUID 对应的配置文件复制到当前 GUID，保留历史用户配置。
    /// </summary>
    /// <param name="log">用于记录迁移结果的 BepInEx 日志源。</param>
    /// <returns>成功复制旧配置时返回 <c>true</c>，否则返回 <c>false</c>。</returns>
    /// <remarks>
    /// 只复制、不移动旧文件，避免用户回退旧版本时丢失配置。新配置已存在时不会覆盖，防止把新版本配置还原成旧状态。
    /// </remarks>
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
