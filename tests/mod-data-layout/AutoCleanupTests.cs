using System.Text;
using System.Text.Json.Nodes;
using BepInEx.Configuration;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Plugin;

/// <summary>仅使用调用方创建的合成目录，覆盖真实配置绑定与 Windows 句柄删除，不初始化 Unity。</summary>
internal static class AutoCleanupTests
{
    internal static int Run(string sandbox)
    {
        var assertions = 0;
        void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
        var counter = 0;
        (ModDataPaths Paths, StewardPluginConfig Settings, byte[] Source) Prepare(string token = "synthetic-preserved-token")
        {
            var paths = new ModDataPaths(Path.Combine(sandbox, "automatic-cleanup-" + counter++));
            Directory.CreateDirectory(Path.GetDirectoryName(paths.LegacyPluginConfigPath)!);
            var source = Encoding.UTF8.GetBytes("[LocalApi]\nToken = " + TomlTypeConverter.ConvertToString(token, typeof(string))
                + "\nPort = 32555\n[Diagnostics]\nAggregateModLogPath = synthetic-old.log\n");
            File.WriteAllBytes(paths.LegacyPluginConfigPath, source);
            Directory.CreateDirectory(paths.Root);
            File.WriteAllText(paths.FavoritesPath, "raw business bytes retained without schema conversion");
            ModDataLayoutMigration.EnsureReady(paths, StewardPluginConfig.ValidateStoredConfiguration);
            return (paths, StewardPluginConfig.Bind(new ConfigFile(paths.PluginConfigPath, false)), source);
        }
        string Backup(ModDataPaths paths) => Path.Combine(paths.MigrationBackupRoot,
            JsonNode.Parse(File.ReadAllText(paths.LayoutStatePath))!["backupDirectory"]!.GetValue<string>());
        string Status(ModDataPaths paths) => JsonNode.Parse(File.ReadAllText(paths.LegacyConfigCleanupPath))!["status"]!.GetValue<string>();
        ModLegacyConfigCleanupResult Clean((ModDataPaths Paths, StewardPluginConfig Settings, byte[] Source) item,
            Action<string>? fault = null) => ModDataLayoutMigration.TryCleanupLegacyConfiguration(item.Paths,
                item.Settings.ValidatePersistedConfiguration, fault);

        var empty = Prepare("");
        Check(LocalApiConnectionPolicy.IsUsableToken(empty.Settings.LocalApiToken.Value), "空 Token 在真实绑定阶段生成");
        var loaded = StewardPluginConfig.Bind(new ConfigFile(empty.Paths.PluginConfigPath, false));
        Check(loaded.LocalApiToken.Value == empty.Settings.LocalApiToken.Value, "生成凭据在清理前已经持久化且可重读");
        if (!OperatingSystem.IsWindows())
        {
            Check(Clean(empty).Warning != null && File.Exists(empty.Paths.LegacyPluginConfigPath), "非 Windows 保留旧 CFG");
            Console.WriteLine("SKIP: 自动 CFG 句柄删除用例仅在 Windows 执行。");
            return assertions;
        }
        var result = Clean(empty);
        Check(result.Removed && result.Warning == null, "旧 CFG 应在绑定与持久化后自动清理：" + result.Warning);
        Check(!File.Exists(empty.Paths.LegacyPluginConfigPath), "旧根文件已移除");
        Check(File.ReadAllBytes(Path.Combine(Backup(empty.Paths), "legacy-plugin.cfg")).SequenceEqual(empty.Source), "原字节备份保留");
        Check(Status(empty.Paths) == "complete", "局部清理完成记录独立于布局版本");
        var receipt = File.ReadAllBytes(empty.Paths.LegacyConfigCleanupPath);
        Check(!Clean(empty).Removed && Clean(empty).Warning == null, "完成后幂等且不重写记录");
        Check(receipt.SequenceEqual(File.ReadAllBytes(empty.Paths.LegacyConfigCleanupPath)), "完成记录原字节不变");
        File.WriteAllBytes(empty.Paths.LegacyPluginConfigPath, empty.Source);
        Check(Clean(empty).Warning != null && File.Exists(empty.Paths.LegacyPluginConfigPath), "完成后同名同内容文件重现也保留");

        foreach (var boundary in new[] { "after-cleanup-pending", "before-legacy-delete", "after-legacy-delete" })
        {
            var item = Prepare();
            var failed = Clean(item, point => { if (point == boundary) throw new IOException("synthetic interruption"); });
            Check(failed.Warning != null && Status(item.Paths) == "pending", "中断保留 pending 意图");
            Check(failed.Removed == (boundary == "after-legacy-delete"), "删除结果与失败阶段一致");
            var retried = Clean(item);
            Check(retried.Warning == null && !File.Exists(item.Paths.LegacyPluginConfigPath), "同一原件中断重试成功：" + retried.Warning);
            Check(Status(item.Paths) == "complete", "重试完成独立清理记录");
        }
        foreach (var replace in new[] { false, true })
        {
            var item = Prepare();
            Clean(item, point => { if (point == "after-cleanup-pending") throw new IOException(); });
            if (replace) { File.Delete(item.Paths.LegacyPluginConfigPath); File.WriteAllBytes(item.Paths.LegacyPluginConfigPath, item.Source); }
            else File.WriteAllText(item.Paths.LegacyPluginConfigPath, "changed after migration");
            Check(Clean(item).Warning != null && File.Exists(item.Paths.LegacyPluginConfigPath), "内容改变或同内容对象替换均拒绝重试");
        }
        var recreated = Prepare();
        Clean(recreated, point => { if (point == "after-legacy-delete") throw new IOException(); });
        File.WriteAllBytes(recreated.Paths.LegacyPluginConfigPath, recreated.Source);
        Check(Clean(recreated).Warning != null && File.Exists(recreated.Paths.LegacyPluginConfigPath), "删除后崩溃窗口重建的旧文件不能再次删除");

        foreach (var damage in new[] { "backup", "current", "receipt", "future-receipt", "future-layout", "manifest" })
        {
            var item = Prepare();
            switch (damage)
            {
                case "backup": File.WriteAllText(Path.Combine(Backup(item.Paths), "favorites.json"), "corrupt"); break;
                case "current": File.WriteAllText(item.Paths.PluginConfigPath, "broken"); break;
                case "receipt": File.WriteAllText(item.Paths.LegacyConfigCleanupPath, "{}"); break;
                case "future-receipt":
                    Clean(item, point => { if (point == "after-cleanup-pending") throw new IOException(); });
                    var entry = JsonNode.Parse(File.ReadAllText(item.Paths.LegacyConfigCleanupPath))!;
                    entry["version"] = 2; File.WriteAllText(item.Paths.LegacyConfigCleanupPath, entry.ToJsonString()); break;
                case "future-layout":
                    var state = JsonNode.Parse(File.ReadAllText(item.Paths.LayoutStatePath))!;
                    state["layoutVersion"] = 2; File.WriteAllText(item.Paths.LayoutStatePath, state.ToJsonString()); break;
                case "manifest": File.WriteAllText(Path.Combine(Backup(item.Paths), "manifest.json"), "{}"); break;
            }
            Check(Clean(item).Warning != null && File.Exists(item.Paths.LegacyPluginConfigPath), "无效证明仅阻止清理并保留源：" + damage);
        }
        var unrecorded = new ModDataPaths(Path.Combine(sandbox, "automatic-unrecorded"));
        ModDataLayoutMigration.EnsureReady(unrecorded, StewardPluginConfig.ValidateStoredConfiguration);
        var unrecordedSettings = StewardPluginConfig.Bind(new ConfigFile(unrecorded.PluginConfigPath, false));
        File.WriteAllText(unrecorded.LegacyPluginConfigPath, "not in this backup");
        Check(ModDataLayoutMigration.TryCleanupLegacyConfiguration(unrecorded, unrecordedSettings.ValidatePersistedConfiguration).Warning != null
            && File.Exists(unrecorded.LegacyPluginConfigPath), "未备份旧文件不删除");

        var occupied = Prepare();
        using (var hold = new FileStream(occupied.Paths.LegacyPluginConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(Clean(occupied).Warning != null && File.Exists(occupied.Paths.LegacyPluginConfigPath), "源独占占用不阻止新配置使用");
        Check(Clean(occupied).Removed, "占用解除后重试成功");
        var readOnly = Prepare();
        File.SetAttributes(readOnly.Paths.LegacyPluginConfigPath, FileAttributes.ReadOnly);
        try { Check(Clean(readOnly).Warning != null && File.Exists(readOnly.Paths.LegacyPluginConfigPath), "只读源保留"); }
        finally { File.SetAttributes(readOnly.Paths.LegacyPluginConfigPath, FileAttributes.Normal); }
        Check(Clean(readOnly).Removed, "只读解除后同一原件重试成功");

        var guarded = Prepare(); var blockedEdits = 0;
        var guardedResult = Clean(guarded, point =>
        {
            if (point != "before-legacy-delete") return;
            foreach (var path in new[] { guarded.Paths.LegacyPluginConfigPath, guarded.Paths.PluginConfigPath,
                Path.Combine(Backup(guarded.Paths), "legacy-plugin.cfg"), guarded.Paths.LayoutStatePath,
                guarded.Paths.LegacyConfigCleanupPath })
                try { File.WriteAllText(path, "concurrent edit"); } catch (IOException) { blockedEdits++; }
            try { File.Move(guarded.Paths.LegacyConfigCleanupPath, guarded.Paths.LegacyConfigCleanupPath + ".replaced"); }
            catch (IOException) { blockedEdits++; }
            try { Directory.Move(guarded.Paths.Root, guarded.Paths.Root + "-moved"); } catch (IOException) { blockedEdits++; }
        });
        Check(guardedResult.Removed && guardedResult.Warning == null && blockedEdits == 7, "源、证明、清理记录及父目录在删除前均保持锁定");

        var linked = Prepare(); var copied = Path.Combine(sandbox, "automatic-link-original.cfg");
        File.Move(linked.Paths.LegacyPluginConfigPath, copied);
        try
        {
            File.CreateSymbolicLink(linked.Paths.LegacyPluginConfigPath, copied);
            Check(Clean(linked).Warning != null && File.Exists(copied), "链接旧源拒绝删除且保留目标");
        }
        catch (UnauthorizedAccessException) { Console.WriteLine("SKIP: 当前系统不允许创建 CFG 链接。"); }
        finally { if (File.Exists(linked.Paths.LegacyPluginConfigPath)) File.Delete(linked.Paths.LegacyPluginConfigPath); }
        return assertions;
    }
}
