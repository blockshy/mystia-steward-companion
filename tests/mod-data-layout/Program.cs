using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Configuration;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.Plugin;

// 显式整包夹具入口只接受仓库 temp 下带固定哨兵的合成树；不提供任意真实配置迁移命令。
if (args.Length != 0)
{
    ManualUpgradeFixture.Run(args);
    return 0;
}

// 全部目录和凭据均为本进程生成的合成夹具；不读取 BepInEx 全局配置路径或真实游戏目录。
var sandbox = Path.Combine(Path.GetTempPath(), "mystia-mod-data-layout-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
var assertions = 0;
void Check(bool condition, string reason) { assertions++; if (!condition) throw new Exception(reason); }
ModDataPaths Area(string name) => new(Path.Combine(sandbox, name));
void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
string Cfg(string token = "synthetic-token-1234567890", string log = "") => "[LocalApi]\nToken = "
    + TomlTypeConverter.ConvertToString(token, typeof(string)) + "\n[Diagnostics]\nAggregateModLogPath = " + log
    + "\n[Runtime]\nAutoRefreshSeconds = 3\n";
void Ready(ModDataPaths p, Action<string>? fault = null) => ModDataLayoutMigration.EnsureReady(p, StewardPluginConfig.ValidateStoredConfiguration, fault);
Exception Reject(Action action, string reason)
{
    try { action(); } catch (Exception error) { assertions++; return error; }
    throw new Exception("预期拒绝：" + reason);
}
JsonObject State(ModDataPaths p) => JsonNode.Parse(File.ReadAllText(p.LayoutStatePath))!.AsObject();
string Backup(ModDataPaths p) => Path.Combine(p.MigrationBackupRoot, State(p)["backupDirectory"]!.GetValue<string>());
StewardPluginConfig Bind(ModDataPaths p) => StewardPluginConfig.Bind(new ConfigFile(p.PluginConfigPath, false));
try
{
    var fresh = Area("fresh");
    Check(!Directory.Exists(fresh.Root), "路径构造不得创建目录");
    Ready(fresh);
    Check(State(fresh)["status"]!.GetValue<string>() == "complete", "首次安装完成布局");
    Check(State(fresh)["files"]!.AsArray().Count == 0, "首次安装没有虚构原件");
    var settings = Bind(fresh);
    Check(settings.ToggleKey.Value == UnityEngine.KeyCode.F8, "真实 BepInEx 绑定默认枚举");
    Check(!File.Exists(fresh.LegacyPluginConfigPath), "绑定新配置不得生成旧根 CFG");
    settings.LocalApiToken.Value = "synthetic-saved-token";
    Check(Bind(fresh).LocalApiToken.Value == "synthetic-saved-token", "真实 ConfigEntry 保存及重新加载");

    var legacy = Area("legacy");
    var token = "synthetic-\\-quote\"-equals=-hash#!";
    var legacyText = Cfg(token, "synthetic-external.log");
    Put(legacy.LegacyPluginConfigPath, legacyText);
    Put(legacy.FavoritesPath, "{raw preserved, deliberately not valid JSON}");
    Put(legacy.CustomRecipesPath, "{\"version\":1,\"enabled\":true,\"recipes\":[]}");
    Put(legacy.CustomRecipesPath + ".v1.backup", "original v1 bytes");
    Put(legacy.DeviceAuthorityPath, "{\"schemaVersion\":1}");
    Put(legacy.GameFocusOverlayPath, "{\"version\":1}");
    Put(legacy.GameFocusOverlayPath + ".v1.bak", "original overlay bytes");
    Put(legacy.AggregateLogPath, "not a migration source");
    Put(Path.Combine(legacy.UpdatesRoot, "update-state.json"), "not a migration source");
    Ready(legacy);
    Check(File.ReadAllText(legacy.LegacyPluginConfigPath) == legacyText, "旧 CFG 原件未修改");
    Check(File.ReadAllText(legacy.FavoritesPath) == "{raw preserved, deliberately not valid JSON}", "布局不解释或升级业务 JSON");
    Check(State(legacy)["files"]!.AsArray().Count == 7, "只备份固定白名单的现有文件");
    Check(!File.Exists(Path.Combine(Backup(legacy), "aggregate-mod.log")), "不收集聚合日志");
    foreach (var item in State(legacy)["files"]!.AsArray())
    {
        var bytes = File.ReadAllBytes(Path.Combine(Backup(legacy), item!["key"]!.GetValue<string>()));
        Check(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == item["sha256"]!.GetValue<string>(), "每个备份可独立验证 SHA256");
    }
    Check(Bind(legacy).LocalApiToken.Value == token, "真实框架转义 Token 在路径迁移后保持相同凭据");
    Check(Bind(legacy).AggregateModLogPath.Value == "", "旧外部日志位置归一为统一根默认值");
    var marker = File.ReadAllBytes(legacy.LayoutStatePath);
    Ready(legacy);
    Check(File.ReadAllBytes(legacy.LayoutStatePath).SequenceEqual(marker), "完成后重入不重写布局记录");
    Check(Directory.EnumerateDirectories(legacy.MigrationBackupRoot).Count() == 1, "完成后不重复创建备份");
    var edited = Bind(legacy); edited.LocalApiToken.Value = "synthetic-new-authoritative-token";
    edited.AggregateModLogPath.Value = "another-external.log";
    Ready(legacy);
    Check(Bind(legacy).LocalApiToken.Value == "synthetic-new-authoritative-token", "完成后新配置权威不被旧 CFG 覆盖");
    Check(Bind(legacy).AggregateModLogPath.Value == "", "完成后手工外部日志路径也被收敛");

    // 覆盖发布前与发布后的四个持久化边界；自定义日志使新旧 CFG 字节确实不同。
    foreach (var boundary in new[] { "after-prepared", "before-config-publish", "after-config-publish", "before-complete" })
    {
        var p = Area(boundary); Put(p.LegacyPluginConfigPath, Cfg(log: "old-external.log"));
        Reject(() => Ready(p, point => { if (point == boundary) throw new IOException("synthetic interruption"); }), boundary);
        Check(State(p)["status"]!.GetValue<string>() == "prepared", "中断留有 prepared 恢复记录");
        Ready(p);
        Check(State(p)["status"]!.GetValue<string>() == "complete", "中断恢复完成");
        Check(Bind(p).LocalApiToken.Value == "synthetic-token-1234567890", "重试不重新生成 Token");
        Check(Directory.EnumerateDirectories(p.MigrationBackupRoot).Count() == 1, "恢复使用同一备份");
    }

    var existing = Area("existing-target"); Put(existing.PluginConfigPath, Cfg("synthetic-target-token")); Ready(existing);
    Check(Bind(existing).LocalApiToken.Value == "synthetic-target-token", "只有目标文件时保留目标");
    var identical = Area("identical"); Put(identical.PluginConfigPath, Cfg()); Put(identical.LegacyPluginConfigPath, Cfg()); Ready(identical);
    Check(State(identical)["files"]!.AsArray().Count == 2, "同内容的新旧 CFG 均备份");
    var conflict = Area("conflict"); Put(conflict.PluginConfigPath, Cfg("synthetic-target-token")); Put(conflict.LegacyPluginConfigPath, Cfg());
    Reject(() => Ready(conflict), "新旧配置冲突");
    Check(!File.Exists(conflict.LayoutStatePath), "冲突不发布迁移状态");
    Check(File.ReadAllText(conflict.PluginConfigPath) == Cfg("synthetic-target-token"), "冲突不覆盖目标");

    var invalid = new[] { "", "# empty\n", "broken syntax", "[LocalApi]\nToken = one\nToken = two\n",
        "[LocalApi]\nPort = abc\n", "[Runtime]\nAutoRefreshSeconds = NaN\n", "[Hotkeys]\nToggleKey = invalid-key\n",
        Cfg("synthetic-不兼容-token"), Cfg(new string('s', 129)), Cfg("synthetic token with spaces") };
    for (var index = 0; index < invalid.Length; index++)
    {
        var p = Area("invalid-" + index); Put(p.LegacyPluginConfigPath, invalid[index]);
        var error = Reject(() => Ready(p), "非法 CFG 必须先于绑定和任何迁移写入拒绝");
        Check(error is ModDataValidationException, "已净化配置错误可供宿主显示具体原因");
        Check(!Directory.Exists(p.Root), "非法源配置不产生插件数据写入");
        Check(File.ReadAllText(p.LegacyPluginConfigPath) == invalid[index], "非法原件仍保留");
        Check(!error.Message.Contains("不兼容-token", StringComparison.Ordinal), "错误不回显 Token");
    }
    var badEncoding = Area("bad-encoding"); Directory.CreateDirectory(Path.GetDirectoryName(badEncoding.LegacyPluginConfigPath)!);
    File.WriteAllBytes(badEncoding.LegacyPluginConfigPath, new byte[] { 0xff, 0xfe, 0x61 });
    Reject(() => Ready(badEncoding), "无效 UTF-8");

    foreach (var content in new[] { "{broken", "{\"layoutVersion\":2,\"status\":\"complete\",\"backupDirectory\":\"unused\",\"configurationSha256\":\"\",\"files\":[]}" })
    {
        var p = Area("bad-marker-" + Guid.NewGuid().ToString("N")); Put(p.LayoutStatePath, content); Put(p.PluginConfigPath, Cfg());
        Reject(() => Ready(p), "损坏或未来版本记录");
        Check(File.ReadAllText(p.LayoutStatePath) == content, "不覆盖未知布局记录");
    }
    var lost = Area("lost-completed-config"); Ready(lost); File.Delete(lost.PluginConfigPath);
    Reject(() => Ready(lost), "完成后目标配置丢失不能重新导入旧源或默认值");
    Check(!File.Exists(lost.PluginConfigPath), "丢失目标不自动重建");
    var damaged = Area("damaged-completed-config"); Ready(damaged); Put(damaged.PluginConfigPath, "broken");
    Reject(() => Ready(damaged), "完成后损坏配置阻止继续绑定");
    Reject(() => Bind(damaged), "直接调用 Bind 同样不回退默认值");
    Check(File.ReadAllText(damaged.PluginConfigPath) == "broken", "真实框架未被允许覆盖损坏正文");

    foreach (var change in new[] { "source", "target", "backup", "new-json", "manifest" })
    {
        var p = Area("change-" + change); Put(p.LegacyPluginConfigPath, Cfg());
        Reject(() => Ready(p, point => { if (point == "after-prepared") throw new IOException(); }), "准备阶段中断");
        switch (change)
        {
            case "source": Put(p.LegacyPluginConfigPath, Cfg("synthetic-changed-token")); break;
            case "target": Put(p.PluginConfigPath, Cfg("synthetic-conflicting-token")); break;
            case "backup": Put(Path.Combine(Backup(p), "legacy-plugin.cfg"), "damaged"); break;
            case "new-json": Put(p.FavoritesPath, "new external file"); break;
            case "manifest": Put(Path.Combine(Backup(p), "manifest.json"), "{}"); break;
        }
        Reject(() => Ready(p), "恢复拒绝外部变化或坏备份：" + change);
        Check(State(p)["status"]!.GetValue<string>() == "prepared", "拒绝恢复不发布 complete");
    }

    // 故障观察点也可模拟外部进程编辑 marker；不得以第二次读到的摘要授权覆盖新记录。
    foreach (var boundary in new[] { "after-prepared", "before-complete" })
    {
        var p = Area("marker-changed-" + boundary); Put(p.LegacyPluginConfigPath, Cfg());
        const string externalMarker = "{\"layoutVersion\":999,\"synthetic\":\"external modification\"}";
        var error = Reject(() => Ready(p, point => { if (point == boundary) Put(p.LayoutStatePath, externalMarker); }), "拒绝覆盖发布期间改变的 marker");
        Check(error is ModDataValidationException, "marker 冲突报告净化错误");
        Check(File.ReadAllText(p.LayoutStatePath) == externalMarker, "保留外部 marker 修改原件");
    }

    var held = Area("locked-source"); Put(held.LegacyPluginConfigPath, Cfg());
    using (var lease = new FileStream(held.LegacyPluginConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Reject(() => Ready(held), "读访问被拒绝不得当作文件不存在");
    Check(!Directory.Exists(held.Root), "拒绝读取时没有迁移写入");
    Ready(held);
    if (OperatingSystem.IsWindows())
    {
        var readOnly = Area("readonly-target"); Put(readOnly.PluginConfigPath, Cfg(log: "old-external.log"));
        File.SetAttributes(readOnly.PluginConfigPath, FileAttributes.ReadOnly);
        try { Reject(() => Ready(readOnly), "只读目标禁止替换"); }
        finally { File.SetAttributes(readOnly.PluginConfigPath, FileAttributes.Normal); }
        Check(File.ReadAllText(readOnly.PluginConfigPath) == Cfg(log: "old-external.log"), "只读失败保留原件");
        Ready(readOnly);
        Check(State(readOnly)["status"]!.GetValue<string>() == "complete", "访问恢复后可重试");
    }

    // 符号链接需系统授权；若环境不允许创建，明确报告未覆盖，不能伪称通过。
    var link = Area("reparse"); var outside = Path.Combine(sandbox, "outside"); Directory.CreateDirectory(outside);
    Directory.CreateDirectory(Path.GetDirectoryName(link.Root)!);
    try
    {
        Directory.CreateSymbolicLink(link.Root, outside);
        try { Reject(() => Ready(link), "拒绝数据根重解析点"); Check(!Directory.EnumerateFileSystemEntries(outside).Any(), "不向链接目标写数据"); }
        finally { Directory.Delete(link.Root); }
    }
    catch (UnauthorizedAccessException) { Console.WriteLine("SKIP: 当前系统不允许创建符号链接；重解析点场景未执行。"); }
    assertions += AutoCleanupTests.Run(sandbox);
    assertions += LegacyCompanionTests.Run(sandbox);
    Console.WriteLine($"Mod data layout smoke: {assertions} assertions passed; real BepInEx ConfigFile/TOML binding exercised.");
    return 0;
}
finally
{
    // 仅清理本进程创建的随机夹具根；重解析点用例在自身 finally 中先移除链接。
    if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
}
