using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using MystiaStewardCompanion.Core;
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
    public const string PluginGuid = "com.tyukki.mystia-steward-companion";
    public const string PluginName = "mystia-steward-companion";
    public const string PluginVersion = "1.3.1";

    /// <summary>
    /// BepInEx 调用的插件加载入口。
    /// </summary>
    /// <remarks>
    /// 这里会按固定顺序完成配置读取、Harmony 捕获注册、IL2CPP 类型注册和常驻
    /// GameObject 创建。顺序不能随意调整：运行时捕获需要尽早挂接，Overlay 行为必须先注册到
    /// IL2CPP 运行时再添加到 Unity 对象上。
    /// </remarks>
    public override void Load()
    {
        StewardPluginConfig settings;
        ModDataPaths paths;
        try
        {
            paths = new ModDataPaths(Paths.ConfigPath);
            // 框架仍构造指向旧根的只读 Config 属性；关闭其自动保存，且本插件不再 Bind/Save 它。
            // 第三方若显式写回该路径，已完成的清理记录会保留新出现文件，不再次自动删除。
            Config.SaveOnConfigSet = false;
            // 不绑定 BasePlugin.Config：它对应旧根 CFG。先完成备份/布局恢复，再构造新根的
            // ConfigFile，避免 Bind 默认值或后续 JSON schema 升级先行覆盖可恢复的旧数据。
            ModDataLayoutMigration.EnsureReady(paths, StewardPluginConfig.ValidateStoredConfiguration,
                isLegacyCompanion: encoded => StewardPluginConfig.IsKnownLegacyCompanion(encoded, message => Log.LogWarning(message)));
            settings = StewardPluginConfig.Bind(new ConfigFile(paths.PluginConfigPath, saveOnInit: false));
        }
        catch (Exception ex)
        {
            // 不把框架转换异常或配置正文写入日志；其中可能包含手工 Token 或私有路径。
            // 初始化失败后停止后续运行初始化；Bind 可能已保存必要配置，但不得继续注册 Hook、启动 API 或打开数据日志。
            var reason = ex is ModDataValidationException ? ex.Message : $"{ex.GetType().Name}；请检查文件访问权限与占用情况";
            Log.LogError($"Mod 数据目录或主配置初始化失败：{reason} 请检查主配置与 layout-state.json，原数据未自动重置。");
            return;
        }
        // 仅清理已经完成真实绑定、Token 保存和回读的旧 CFG；失败保留源并提示，不阻止正常启动。
        // 此处在启动时调用一次，不进入逐帧循环，也不以局部清理结果冒充全部业务数据迁移成功。
        var cleanup = ModDataLayoutMigration.TryCleanupLegacyConfiguration(paths, settings.ValidatePersistedConfiguration);
        if (cleanup.Warning != null) Log.LogWarning(cleanup.Warning);
        else if (cleanup.Removed) Log.LogInfo("旧根主配置已自动清理，原字节备份保留在统一数据目录中。");
        BepInExConsoleWindow.Instance.ConfigurePreference(
            () => settings.ShowBepInExConsoleOnStartup.Value,
            visible => settings.ShowBepInExConsoleOnStartup.Value = visible);
        if (settings.ShowBepInExConsoleOnStartup.Value)
        {
            var consoleState = BepInExConsoleWindow.Instance.ApplyVisibility(visible: true);
            if (!consoleState.Ok)
            {
                Log.LogWarning($"Failed to show the BepInEx console on startup: {consoleState.Error}");
            }
        }

        AggregateModLogService.Configure(
            settings.EnableAggregateModLog.Value,
            settings.AggregateModLogPath.Value,
            settings.AggregateModLogMaxFileCount.Value);
        if (settings.SetConsoleUtf8.Value)
        {
            ConsoleEncodingHelper.TryUseUtf8(Log);
        }

        RuntimeNightBusinessLifecycle.Attach(Log);
        SpecialOrderRuntimeCapture.Attach(Log);
        NormalOrderRuntimeCapture.Attach(Log);
        RuntimeSceneReadinessCapture.Attach(Log);
        RuntimeSpecialBusinessContextService.Attach(Log);
        YuumaCookerTopologyObserver.Attach(Log);
        RuntimeScheduledEventDiagnosticCapture.Attach(Log);
        RuntimeAvailableMissionSourceCapture.Attach(Log);
        RuntimeMissionDiagnosticCapture.Attach(Log);
        RuntimeServeInWorkMissionDiagnosticCapture.Attach(Log);
        YuyukoChallengeEvaluationTracker.Attach(Log);
        RuntimeOrderHighlightService.Attach(Log);
        RuntimeThrowDeliverOrderHighlightService.Attach(Log);
        RuntimeSeatHighlightService.Attach(Log);
        RuntimeUiPinningService.Attach(Log);
        RuntimePinnedListHighlightService.Attach(Log);
        RuntimeCookingGenerationTracker.Attach(Log);

        StewardOverlayRuntimeContext.Configure(settings, Log);
        ClassInjector.RegisterTypeInIl2Cpp<StewardOverlayBehaviour>();

        var gameObject = new GameObject("mystia-steward-companion Overlay");
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        gameObject.hideFlags = HideFlags.HideAndDontSave;

        gameObject.AddComponent<StewardOverlayBehaviour>();

        Log.LogInfo($"{PluginName} {PluginVersion} loaded. Press {settings.ToggleKey.Value} to open or focus the companion window.");
    }
}
