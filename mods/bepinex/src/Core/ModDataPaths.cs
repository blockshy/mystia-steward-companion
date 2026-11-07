namespace MystiaStewardCompanion.Core;

/// <summary>
/// Mod 自有数据的唯一目录布局。构造只计算路径，不创建目录，也不读取 BepInEx 的全局环境，
/// 因而隔离工具可显式传入自己的配置根；安装载荷和游戏存档不属于本布局。
/// </summary>
internal sealed class ModDataPaths
{
    internal const string PluginConfigFileName = "com.tyukki.mystia-steward-companion.cfg";

    internal ModDataPaths(string configRoot)
    {
        if (string.IsNullOrWhiteSpace(configRoot)) throw new ArgumentException("配置根目录不能为空。", nameof(configRoot));
        var root = Path.GetFullPath(configRoot);
        Root = Path.Combine(root, "MystiaStewardCompanion");
        LegacyPluginConfigPath = Path.Combine(root, PluginConfigFileName);
    }

    internal string Root { get; }
    internal string PluginConfigPath => Path.Combine(Root, PluginConfigFileName);
    internal string LegacyPluginConfigPath { get; }
    internal string GameMediaRoot => Path.Combine(Root, "cache", "game-media");
    internal string FavoritesPath => Path.Combine(Root, "favorites.json");
    internal string CustomRecipesPath => Path.Combine(Root, "custom-recipes.json");
    internal string DeviceAuthorityPath => Path.Combine(Root, "companion-devices.json");
    internal string GameFocusOverlayPath => Path.Combine(Root, "game-focus-overlay.json");
    internal string AggregateLogPath => Path.Combine(Root, "aggregate-mod.log");
    internal string DiagnosticPackagesDirectory => Path.Combine(Root, "diagnostic-packages");
    internal string UpdatesRoot => Path.Combine(Root, "updates");
    internal string LayoutStatePath => Path.Combine(Root, "layout-state.json");
    internal string MigrationBackupRoot => Path.Combine(Root, "migration-backups");
    internal string LegacyConfigCleanupPath => Path.Combine(Root, "legacy-cfg-cleanup.json");
}
