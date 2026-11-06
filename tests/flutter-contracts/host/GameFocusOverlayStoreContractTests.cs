using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Ui;

/// <summary>
/// 专注层版本2设置专项回归。全部输入为合成值，独占临时目录中通过真实文件占用检验原子
/// 替换失败；与全生产HTTP宿主共享断言，不读取用户配置、启动游戏或构建Unity对象。
/// </summary>
internal static class GameFocusOverlayStoreContractTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static int _count;
    internal static int Run()
    {
        _count = 0;
        var root = Path.Combine(Path.GetTempPath(), "mystia-focus-store-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = new List<string>();
        var scenarios = new (string Name, Action Action)[]
        {
            ("默认值与持久化", () => Persistence(root)), ("严格更新", StrictUpdates), ("子对象完整性", NestedUpdates),
            ("修订CAS", RevisionConflicts), ("原子写失败", () => FailedWrite(root)),
            ("版本1精确迁移", () => Migration(root)), ("迁移失败保留", () => FailedMigration(root)),
            ("持久化严格读取与恢复", () => StrictFiles(root)),
        };
        try
        {
            foreach (var (name, action) in scenarios)
                try { action(); Console.WriteLine("PASS: " + name); }
                catch (Exception error) { failures.Add(name + " — " + error); }
            foreach (var error in failures) Console.Error.WriteLine("FAIL: " + error);
            Console.WriteLine($"{(failures.Count == 0 ? "PASS" : "FAIL")}: game focus store v2; {scenarios.Length} scenarios, {_count} assertions, {failures.Count} failures; no game execution.");
            return failures.Count == 0 ? 0 : 1;
        }
        finally { Directory.Delete(root, recursive: true); } // 仅清理本次唯一生成的已知绝对路径。
    }

    private static void Persistence(string root)
    {
        var path = Path.Combine(root, "persistent.json");
        var store = new GameFocusOverlayStore(path);
        Check(store.Snapshot.Ready && !store.Snapshot.CanReset && !store.Snapshot.Settings.Enabled && store.Snapshot.Revision == 1, "默认状态错误。");
        Check(store.Snapshot.Settings.Theme == "light" && store.Snapshot.Settings.ScrollPercent == 0, "默认主题/滚动错误。");
        Check(store.Snapshot.Settings.Content == new GameFocusOverlayContent() && !File.Exists(path), "默认内容或首次读取写盘异常。");
        var content = new GameFocusOverlayContent { ShowNormal = false, RareFirst = true, ShowExtras = false };
        var changes = new JsonObject
        {
            ["enabled"] = true, ["backgroundOpacity"] = 51, ["contentOpacity"] = 83, ["borderWidth"] = 3,
            ["fontSize"] = 20, ["theme"] = "dark", ["scrollPercent"] = 64,
            ["layout"] = JsonSerializer.SerializeToNode(new GameFocusOverlayLayout(.1, .2, .4, .5), Options),
            ["content"] = JsonSerializer.SerializeToNode(content, Options),
        };
        var saved = Update(store, changes.ToJsonString());
        Check(saved.Revision == 2 && saved.Settings.Content == content && saved.Settings.Theme == "dark", "保存未整体发布。");
        Check(new GameFocusOverlayStore(path).Snapshot.Settings == saved.Settings, "持久化往返丢失字段。");
        using var disk = JsonDocument.Parse(File.ReadAllBytes(path));
        Check(disk.RootElement.GetProperty("version").GetInt32() == 2, "未写入版本2。");
        var wire = JsonSerializer.SerializeToNode(saved, Options)!.AsObject();
        Check(!wire.ContainsKey("editing") && !wire["settings"]!.AsObject().ContainsKey("showRare"), "新响应暴露旧字段。");
        Check(typeof(GameFocusOverlayStore).GetMethod("TrySaveLayout") == null && typeof(GameFocusOverlayStore).GetMethod("EndEditing") == null, "旧游戏内编辑入口未删除。");
        var fixedTime = DateTime.UnixEpoch.AddDays(10);
        File.SetLastWriteTimeUtc(path, fixedTime);
        Check(ReferenceEquals(saved, Update(store, changes.ToJsonString())) && File.GetLastWriteTimeUtc(path) == fixedTime, "相同设置不幂等。");
        var reset = Update(store, "{\"resetLayout\":true}");
        Check(reset.Settings.Layout == GameFocusOverlayLayout.Default && reset.Settings.ScrollPercent == 0, "布局重置未回到顶部。");
        Check(reset.Settings.Content == content && reset.Settings.Theme == "dark" && reset.Settings.BackgroundOpacity == 51, "布局重置清除了其它偏好。");
        Check(!File.Exists(path + ".v1.bak"), "版本2新文件产生了伪旧备份。");
    }

    private static void StrictUpdates()
    {
        var store = new GameFocusOverlayStore();
        var state = store.Snapshot;
        foreach (var json in new[]
        {
            "{}", "[]", "null", "{\"enabled\":true,\"enabled\":false}", "{\"unknown\":1}",
            "{\"enabled\":1}", "{\"editing\":true}", "{\"showRare\":true}", "{\"showNormal\":false}", "{\"rareFirst\":true}",
            "{\"resetLayout\":1}", "{\"resetLayout\":true,\"enabled\":true}",
            "{\"fontSize\":18.5}", "{\"fontSize\":\"18\"}", "{\"fontSize\":2147483648}",
            "{\"backgroundOpacity\":-1}", "{\"backgroundOpacity\":101}", "{\"contentOpacity\":19}", "{\"contentOpacity\":101}",
            "{\"borderWidth\":-1}", "{\"borderWidth\":7}", "{\"fontSize\":11}", "{\"fontSize\":25}",
            "{\"theme\":\"Light\"}", "{\"theme\":\"system\"}", "{\"theme\":1}", "{\"theme\":null}",
            "{\"scrollPercent\":-1}", "{\"scrollPercent\":101}", "{\"scrollPercent\":1.5}", "{\"scrollPercent\":\"50\"}",
            "{\"layout\":null}", "{\"layout\":{\"x\":0}}", "{\"content\":null}", "{\"content\":{}}", "{\"revision\":100}",
        })
        {
            Throws<ArgumentException>(() => Update(store, json), "非法更新未拒绝：" + json);
            Check(ReferenceEquals(state, store.Snapshot), "非法更新发布了部分设置。");
        }
        foreach (var json in new[]
        {
            "{\"backgroundOpacity\":0,\"contentOpacity\":20,\"borderWidth\":0,\"fontSize\":12,\"scrollPercent\":0,\"theme\":\"light\"}",
            "{\"backgroundOpacity\":100,\"contentOpacity\":100,\"borderWidth\":6,\"fontSize\":24,\"scrollPercent\":100,\"theme\":\"dark\"}",
        }) Check(Update(store, json).Ready, "合法边界值被拒绝。");
    }

    private static void NestedUpdates()
    {
        var store = new GameFocusOverlayStore();
        var state = store.Snapshot;
        foreach (var kind in new[] { "layout", "content" })
        {
            var value = kind == "layout" ? JsonSerializer.SerializeToNode(GameFocusOverlayLayout.Default, Options)!.AsObject()
                : JsonSerializer.SerializeToNode(new GameFocusOverlayContent(), Options)!.AsObject();
            foreach (var key in value.Select(pair => pair.Key).ToArray())
            {
                var missing = Clone(value); missing.Remove(key); Reject(missing);
                var wrong = Clone(value); wrong[key] = "wrong-type"; Reject(wrong);
                var duplicate = value.ToJsonString().Insert(1, JsonSerializer.Serialize(key) + ":" + value[key]!.ToJsonString() + ",");
                Throws<ArgumentException>(() => Update(store, "{\"" + kind + "\":" + duplicate + "}"), "嵌套重复字段未拒绝。");
                Check(ReferenceEquals(state, store.Snapshot), "嵌套重复字段改变了状态。");
            }
            var unknown = Clone(value); unknown["future"] = true; Reject(unknown);
            void Reject(JsonObject nested)
            {
                Throws<ArgumentException>(() => Update(store, new JsonObject { [kind] = nested }.ToJsonString()), "子对象缺失/未知/错误类型未拒绝。");
                Check(ReferenceEquals(state, store.Snapshot), "子对象错误改变了状态。");
            }
        }
        foreach (var layout in new[]
        {
            "{\"x\":-0.1,\"y\":0,\"width\":0.5,\"height\":0.5}", "{\"x\":0,\"y\":0,\"width\":0,\"height\":0.5}",
            "{\"x\":0.8,\"y\":0,\"width\":0.5,\"height\":0.5}", "{\"x\":0,\"y\":0.8,\"width\":0.5,\"height\":0.5}",
            "{\"x\":0,\"y\":0,\"width\":1e999,\"height\":0.5}",
        })
        {
            Throws<ArgumentException>(() => Update(store, "{\"layout\":" + layout + "}"), "越界/非有限布局未拒绝。");
            Check(ReferenceEquals(state, store.Snapshot), "非法布局改变状态。");
        }
        var flags = new GameFocusOverlayContent { ShowRare = false, ShowNormal = false, RareFirst = true, ShowOrderTags = false,
            ShowFood = false, ShowIngredients = false, ShowExtras = false, ShowCooker = false, ShowBeverage = false };
        Check(Update(store, new JsonObject { ["content"] = JsonSerializer.SerializeToNode(flags, Options) }.ToJsonString()).Settings.Content == flags, "九字段内容未完整保存。");
        Check(Update(store, "{\"layout\":{\"x\":0,\"y\":0,\"width\":1,\"height\":1}}").Settings.Layout.Width == 1, "全屏比例被拒绝。");
    }

    private static void RevisionConflicts()
    {
        var store = new GameFocusOverlayStore();
        var old = store.Snapshot.Revision;
        var remote = Update(store, "{\"theme\":\"dark\"}");
        using (var layout = JsonDocument.Parse("{\"layout\":{\"x\":0.1,\"y\":0.1,\"width\":0.4,\"height\":0.5}}"))
            Throws<InvalidOperationException>(() => store.Update(old, layout.RootElement), "旧布局覆盖了远端新修订。");
        Check(ReferenceEquals(remote, store.Snapshot), "冲突布局改变状态。");
        using var gate = new ManualResetEventSlim(false);
        var outcomes = new System.Collections.Concurrent.ConcurrentBag<string>();
        var jobs = new[] { 18, 20 }.Select(size => Task.Run(() =>
        {
            using var request = JsonDocument.Parse("{\"fontSize\":" + size + "}");
            gate.Wait();
            try { store.Update(remote.Revision, request.RootElement); outcomes.Add("accepted"); }
            catch (InvalidOperationException) { outcomes.Add("conflict"); }
        })).ToArray();
        gate.Set(); Task.WaitAll(jobs);
        Check(outcomes.Count(item => item == "accepted") == 1 && outcomes.Count(item => item == "conflict") == 1, "同修订并发提交未只成功一次。");
        Check(store.Snapshot.Revision == remote.Revision + 1, "冲突请求推进了额外修订。");
    }

    private static void FailedWrite(string root)
    {
        var path = Path.Combine(root, "write-failure.json");
        var store = new GameFocusOverlayStore(path);
        var state = Update(store, "{\"enabled\":true}");
        var original = File.ReadAllBytes(path);
        // 不共享Delete权限，使真实File.Move覆盖失败，测试内存、原文件和临时文件三者边界。
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            FileWriteFails(() => Update(store, "{\"theme\":\"dark\"}"));
            FileWriteFails(() => Update(store, "{\"layout\":{\"x\":0.1,\"y\":0.1,\"width\":0.4,\"height\":0.5}}"));
            Check(ReferenceEquals(state, store.Snapshot) && File.ReadAllBytes(path).SequenceEqual(original), "落盘失败却发布内存或破坏原文件。");
        }
        Check(!Directory.EnumerateFiles(root, ".write-failure.json.*.tmp").Any(), "落盘失败遗留临时文件。");
        Check(Update(store, "{\"theme\":\"dark\"}").Settings.Theme == "dark", "解除占用后无法保存。");
        // 修订溢出也必须在落盘前拒绝，不能产生“磁盘已成功，内存仍旧”的半提交。
        typeof(GameFocusOverlayStore).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, store.Snapshot with { Revision = long.MaxValue });
        var disk = File.ReadAllBytes(path);
        Throws<OverflowException>(() => Update(store, "{\"fontSize\":24}"), "修订溢出未拒绝。");
        Check(File.ReadAllBytes(path).SequenceEqual(disk), "修订溢出前已改写磁盘。");
    }

    private static void Migration(string root)
    {
        var path = Path.Combine(root, "migration.json");
        var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(" \n" + VersionOne() + "\r\n")).ToArray();
        File.WriteAllBytes(path, original);
        var migrated = new GameFocusOverlayStore(path);
        var settings = migrated.Snapshot.Settings;
        Check(migrated.Snapshot.Ready && settings.Enabled, "合法版本1迁移失败。");
        Check(settings.BackgroundOpacity == 51 && settings.ContentOpacity == 83 && settings.FontSize == 20 && settings.BorderWidth == 3, "旧外观未保留。");
        Check(settings.Layout == new GameFocusOverlayLayout(.1, .2, .4, .5), "旧布局未保留。");
        Check(!settings.Content.ShowRare && settings.Content.ShowNormal && settings.Content.RareFirst, "旧内容三字段未迁入。");
        Check(settings.Content.ShowOrderTags && settings.Content.ShowFood && settings.Content.ShowIngredients && settings.Content.ShowExtras
            && settings.Content.ShowCooker && settings.Content.ShowBeverage && settings.Theme == "light" && settings.ScrollPercent == 0, "新增字段默认值错误。");
        Check(File.ReadAllBytes(path + ".v1.bak").SequenceEqual(original), "备份不是原字节（含BOM空白）。");
        using (var document = JsonDocument.Parse(File.ReadAllBytes(path))) Check(document.RootElement.GetProperty("version").GetInt32() == 2, "未落盘版本2。");
        var disk = File.ReadAllBytes(path);
        Check(new GameFocusOverlayStore(path).Snapshot.Settings == settings, "二次读取设置变化。");
        Check(File.ReadAllBytes(path).SequenceEqual(disk) && File.ReadAllBytes(path + ".v1.bak").SequenceEqual(original), "二次启动重复改写。");
    }

    private static void FailedMigration(string root)
    {
        var original = Encoding.UTF8.GetBytes(VersionOne());
        var conflict = Path.Combine(root, "migration-conflict.json");
        File.WriteAllBytes(conflict, original); File.WriteAllText(conflict + ".v1.bak", "different-backup");
        Check(!new GameFocusOverlayStore(conflict).Snapshot.Ready, "已有不同备份未阻止覆盖。");
        Check(File.ReadAllBytes(conflict).SequenceEqual(original) && File.ReadAllText(conflict + ".v1.bak") == "different-backup", "冲突损坏原始证据。");
        var blocked = Path.Combine(root, "migration-backup-blocked.json");
        File.WriteAllBytes(blocked, original); Directory.CreateDirectory(blocked + ".v1.bak");
        Check(!new GameFocusOverlayStore(blocked).Snapshot.Ready && File.ReadAllBytes(blocked).SequenceEqual(original), "备份失败仍继续迁移。");
        Check(!Directory.EnumerateFiles(root, "migration-backup-blocked.json.v1.bak.*.tmp").Any(), "备份失败遗留临时文件。");
        var locked = Path.Combine(root, "migration-source-locked.json");
        File.WriteAllBytes(locked, original);
        using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = new GameFocusOverlayStore(locked);
            Check(!failure.Snapshot.Ready && !failure.Snapshot.Settings.Enabled, "迁移替换失败发布了启用状态。");
            Check(File.ReadAllBytes(locked).SequenceEqual(original) && File.ReadAllBytes(locked + ".v1.bak").SequenceEqual(original), "替换失败丢失原件或完整备份。");
        }
        Check(new GameFocusOverlayStore(locked).Snapshot.Ready, "解除占用后未复用相同备份继续迁移。");
    }

    private static void StrictFiles(string root)
    {
        var good = JsonSerializer.Serialize(new { version = 2, settings = new GameFocusOverlaySettings { Enabled = true } }, Options);
        var old = VersionOne();
        var invalid = new List<string>
        {
            "{broken", "{\"version\":99,\"settings\":{}}", good.Replace("\"version\":2", "\"version\":2,\"version\":2"),
            good.Replace("\"showRare\":true", "\"showRare\":true,\"showRare\":false"), good.Replace("\"x\":0.7", "\"x\":0.7,\"x\":0.2"),
            old.Replace("\"showRare\":false,", ""), old.Replace("\"showRare\":false", "\"showRare\":false,\"showRare\":true"),
            old.Replace("\"showNormal\":true", "\"showNormal\":1"), old.Replace("\"enabled\":true", "\"enabled\":true,\"editing\":true"),
            old.Replace("\"fontSize\":20", "\"fontSize\":50"), old.Replace("\"width\":0.4", "\"width\":0"),
        };
        foreach (var change in new Action<JsonObject>[]
        {
            value => value.Remove("version"), value => value["version"] = "2", value => value["version"] = 2.5,
            value => value["settings"] = null, value => value["unknown"] = true,
            value => value["settings"]!.AsObject().Remove("theme"), value => value["settings"]!["showRare"] = true,
            value => value["settings"]!["editing"] = false, value => value["settings"]!["enabled"] = 1,
            value => value["settings"]!["theme"] = "system", value => value["settings"]!["scrollPercent"] = 101,
            value => value["settings"]!["fontSize"] = 50, value => value["settings"]!["content"] = null,
            value => value["settings"]!["content"]!.AsObject().Remove("showFood"), value => value["settings"]!["content"]!["showFood"] = 1,
            value => value["settings"]!["content"]!["extra"] = false,
            value => value["settings"]!["layout"]!.AsObject().Remove("width"), value => value["settings"]!["layout"]!["width"] = 0,
            value => value["settings"]!["layout"]!["extra"] = 0,
        }) { var node = JsonNode.Parse(good)!.AsObject(); change(node); invalid.Add(node.ToJsonString()); }
        foreach (var text in invalid)
        {
            var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(path, text);
            var warnings = new List<string>(); var store = new GameFocusOverlayStore(path, warnings.Add);
            Check(!store.Snapshot.Ready && store.Snapshot.CanReset && !store.Snapshot.Settings.Enabled, "无效文件被接受：" + text);
            Check(warnings.Count == 1 && File.ReadAllText(path) == text && !File.Exists(path + ".v1.bak"), "读取失败改写磁盘或缺少诊断。");
            Throws<InvalidOperationException>(() => Update(store, "{\"enabled\":true}"), "坏配置未恢复可直接写入。");
            Throws<ArgumentException>(() => Update(store, "{\"resetLayout\":true,\"enabled\":true}"), "恢复混入启用操作未拒绝。");
            var reset = Update(store, "{\"resetLayout\":true}");
            Check(reset.Ready && !reset.CanReset && reset.Settings == new GameFocusOverlaySettings(), "错误配置显式恢复非全默认。");
            Check(new GameFocusOverlayStore(path).Snapshot.Ready, "恢复未保存可读版本2文件。");
        }
    }

    private static string VersionOne() => "{\"version\":1,\"settings\":{\"enabled\":true,\"backgroundOpacity\":51,\"contentOpacity\":83,\"borderWidth\":3,\"fontSize\":20,\"showRare\":false,\"showNormal\":true,\"rareFirst\":true,\"layout\":{\"x\":0.1,\"y\":0.2,\"width\":0.4,\"height\":0.5}}}";
    private static JsonObject Clone(JsonObject value) => JsonNode.Parse(value.ToJsonString())!.AsObject();
    private static GameFocusOverlayState Update(GameFocusOverlayStore store, string json)
    { using var document = JsonDocument.Parse(json); return store.Update(store.Snapshot.Revision, document.RootElement); }
    private static void Check(bool value, string message) { _count++; if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    { _count++; try { action(); } catch (T) { return; } throw new InvalidOperationException(message); }
    private static void FileWriteFails(Action action)
    {
        _count++; try { action(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("占用目标文件后原子替换仍成功。");
    }
}
