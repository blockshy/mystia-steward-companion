using System.Text;
using BepInEx.Configuration;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.Plugin;

/// <summary>
/// 迁移决策与文件身份识别分开验证：合成识别器只检查首次旧配置归一化边界；
/// 生产识别器使用不存在或错误内容的合成程序验证“未知即保留”，不伪造公开版本的正向摘要证据。
/// </summary>
internal static class LegacyCompanionTests
{
    internal static int Run(string sandbox)
    {
        var assertions = 0;
        void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
        ModDataPaths Prepare(string name)
        {
            var paths = new ModDataPaths(Path.Combine(sandbox, name));
            Directory.CreateDirectory(Path.GetDirectoryName(paths.LegacyPluginConfigPath)!);
            File.WriteAllText(paths.LegacyPluginConfigPath,
                "[LocalApi]\nToken = synthetic-old-client-token\n[Companion]\nExecutablePath = synthetic-old-client.exe\n", new UTF8Encoding(false));
            return paths;
        }
        var known = Prepare("known-legacy-client");
        var original = File.ReadAllBytes(known.LegacyPluginConfigPath);
        var probes = 0;
        ModDataLayoutMigration.EnsureReady(known, StewardPluginConfig.ValidateStoredConfiguration,
            isLegacyCompanion: value => { probes++; return value == "synthetic-old-client.exe"; });
        var knownSettings = StewardPluginConfig.Bind(new ConfigFile(known.PluginConfigPath, false));
        Check(probes == 1 && knownSettings.CompanionExecutablePath.Value == "", "仅精确识别的旧客户端归一到完整包默认入口");
        Check(File.ReadAllBytes(known.LegacyPluginConfigPath).SequenceEqual(original), "客户端路径规范化不修改旧原件");
        knownSettings.CompanionExecutablePath.Value = "synthetic-custom-flutter.exe";
        ModDataLayoutMigration.EnsureReady(known, StewardPluginConfig.ValidateStoredConfiguration,
            isLegacyCompanion: _ => throw new Exception("完成布局不得重新识别"));
        Check(StewardPluginConfig.Bind(new ConfigFile(known.PluginConfigPath, false)).CompanionExecutablePath.Value
            == "synthetic-custom-flutter.exe", "完成布局保留后续自定义 Flutter 设置");

        var unknown = Prepare("unknown-legacy-client");
        ModDataLayoutMigration.EnsureReady(unknown, StewardPluginConfig.ValidateStoredConfiguration, isLegacyCompanion: _ => false);
        Check(StewardPluginConfig.Bind(new ConfigFile(unknown.PluginConfigPath, false)).CompanionExecutablePath.Value
            == "synthetic-old-client.exe", "未知自定义程序不得推断为旧版");

        var interrupted = Prepare("interrupted-legacy-client");
        try
        {
            ModDataLayoutMigration.EnsureReady(interrupted, StewardPluginConfig.ValidateStoredConfiguration,
                point => { if (point == "after-config-publish") throw new IOException("synthetic interruption"); }, _ => true);
            throw new Exception("应到达发布后故障点。");
        }
        catch (IOException) { assertions++; }
        ModDataLayoutMigration.EnsureReady(interrupted, StewardPluginConfig.ValidateStoredConfiguration,
            isLegacyCompanion: _ => throw new Exception("prepared 恢复不得重新探测"));
        Check(StewardPluginConfig.Bind(new ConfigFile(interrupted.PluginConfigPath, false)).CompanionExecutablePath.Value == "",
            "发布后恢复使用已校验的 prepared 内容");

        foreach (var exists in new[] { false, true })
        {
            var path = Path.Combine(sandbox, exists ? "synthetic-unknown.exe" : "synthetic-missing.exe");
            if (exists) File.WriteAllText(path, "synthetic wrong executable");
            string? warning = null;
            Check(!StewardPluginConfig.IsKnownLegacyCompanion(TomlTypeConverter.ConvertToString(path, typeof(string)), value => warning = value),
                "未知及不可读程序均不归一化");
            Check(warning != null && !warning.Contains(path, StringComparison.OrdinalIgnoreCase)
                && !warning.Contains(sandbox, StringComparison.OrdinalIgnoreCase), "识别提示不包含私有路径或配置值");
        }
        return assertions;
    }
}
