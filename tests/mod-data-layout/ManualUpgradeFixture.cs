using System.Text.Json.Nodes;
using BepInEx.Configuration;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.Plugin;

/// <summary>
/// 供隔离整包手动升级脚本调用生产迁移、绑定与收尾；仅验证合成设置，不启动 Unity 或读取真实用户配置。
/// 目录边界、哨兵和固定字段同时成立才进入写入路径，夹具生命周期由外层脚本管理。
/// </summary>
internal static class ManualUpgradeFixture
{
    internal static void Run(string[] args)
    {
        if (args.Length != 2 || args[0] != "--manual-upgrade-fixture" || !Path.IsPathFullyQualified(args[1]))
            throw new ArgumentException("只允许显式合成升级夹具参数。");
        var configRoot = Path.GetFullPath(args[1]);
        var temporaryRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "temp")) + Path.DirectorySeparatorChar;
        if (!configRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("合成配置根必须位于当前仓库 temp 内。");
        for (string? path = configRoot; path != null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("合成配置夹具不能经过重解析点。");
        var sentinel = Path.Combine(configRoot, ".synthetic-mod-config-fixture");
        if ((File.GetAttributes(sentinel) & FileAttributes.ReparsePoint) != 0
            || new FileInfo(sentinel).Length > 128
            || File.ReadAllText(sentinel) != "mystia-synthetic-config-v1")
            throw new IOException("合成配置夹具哨兵无效。");

        var paths = new ModDataPaths(configRoot);
        if ((File.GetAttributes(paths.LegacyPluginConfigPath) & FileAttributes.ReparsePoint) != 0
            || new FileInfo(paths.LegacyPluginConfigPath).Length > 1024 * 1024)
            throw new IOException("合成旧配置类型或大小无效。");
        var original = File.ReadAllBytes(paths.LegacyPluginConfigPath);
        ModDataLayoutMigration.ValidateConfiguration(original, entries =>
        {
            StewardPluginConfig.ValidateStoredConfiguration(entries);
            Check(entries.Single(entry => entry.Section == "LocalApi" && entry.Key == "Token").Value == "synthetic-manual-upgrade-token",
                "夹具凭据必须为固定合成值。");
            Check(entries.Single(entry => entry.Section == "Runtime" && entry.Key == "AutoRefreshSeconds").Value == "3",
                "夹具刷新周期必须为固定合成值。");
        });
        var business = new[] { paths.FavoritesPath, paths.CustomRecipesPath, paths.DeviceAuthorityPath }
            .ToDictionary(path => path, File.ReadAllBytes);
        ModDataLayoutMigration.EnsureReady(paths, StewardPluginConfig.ValidateStoredConfiguration,
            isLegacyCompanion: value => StewardPluginConfig.IsKnownLegacyCompanion(value));
        var settings = StewardPluginConfig.Bind(new ConfigFile(paths.PluginConfigPath, false));
        Check(settings.LocalApiToken.Value == "synthetic-manual-upgrade-token" && settings.AutoRefreshSeconds.Value == 3f,
            "实际绑定必须保留合成凭据及设置。");
        Check(settings.CompanionExecutablePath.Value == "", "公开旧外置程序必须经精确识别后归一为完整包默认入口。");
        var result = ModDataLayoutMigration.TryCleanupLegacyConfiguration(paths, settings.ValidatePersistedConfiguration);
        Check(result.Removed && result.Warning == null && !File.Exists(paths.LegacyPluginConfigPath), "首次启动必须完成旧根配置清理。");
        var state = JsonNode.Parse(File.ReadAllText(paths.LayoutStatePath))!;
        var backup = Path.Combine(paths.MigrationBackupRoot, state["backupDirectory"]!.GetValue<string>(), "legacy-plugin.cfg");
        Check(File.ReadAllBytes(backup).SequenceEqual(original), "必须保留迁移前原字节备份。");
        var receipt = File.ReadAllBytes(paths.LegacyConfigCleanupPath);

        // 模拟下次启动重新绑定；不复用上一实例来替代磁盘持久化验证。
        ModDataLayoutMigration.EnsureReady(paths, StewardPluginConfig.ValidateStoredConfiguration);
        var secondSettings = StewardPluginConfig.Bind(new ConfigFile(paths.PluginConfigPath, false));
        var second = ModDataLayoutMigration.TryCleanupLegacyConfiguration(paths, secondSettings.ValidatePersistedConfiguration);
        Check(secondSettings.LocalApiToken.Value == settings.LocalApiToken.Value && !second.Removed && second.Warning == null
            && File.ReadAllBytes(paths.LegacyConfigCleanupPath).SequenceEqual(receipt), "第二次启动必须幂等且保留凭据。");
        foreach (var item in business) Check(File.ReadAllBytes(item.Key).SequenceEqual(item.Value), "仅迁移 CFG 不得改写业务 JSON。");
        Console.WriteLine("Manual v1.3.1 upgrade fixture passed: real binding, automatic CFG cleanup, original backup and second-start idempotence.");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
