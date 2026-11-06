using System.Text.Json;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Ui;

/// <summary>游戏电脑独立保存的只读显示设置；不加入业务profile，不触发推荐重算或自动化配置变化。</summary>
internal sealed record GameFocusOverlayState(long Revision, GameFocusOverlaySettings Settings,
    bool Ready = true, string Status = "")
{
    /// <summary>仅服务端确认的持久化读取失败允许显式恢复；客户端网络失败不得伪造此标记。</summary>
    public bool CanReset => !Ready;
}

/// <summary>
/// 专注层的原子设置仓库。外部只拿不可变状态，保存完成才发布新值；失败保留旧内存和旧磁盘。
/// 布局由客户端提交完整比例矩形并使用相同修订检查；游戏内显示层没有编辑会话或写入入口。
/// </summary>
internal sealed class GameFocusOverlayStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string? _path;
    private GameFocusOverlayState _state = new(1, new GameFocusOverlaySettings());

    public GameFocusOverlayStore(string? path = null, Action<string>? warning = null)
    {
        _path = path;
        if (path == null || !File.Exists(path)) return;
        try
        {
            // 先保留原始字节再解析；版本1迁移备份不能通过重新序列化丢失BOM、空白或字段顺序。
            var original = File.ReadAllBytes(path);
            var content = original.AsMemory();
            if (original.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf)
                content = content[3..];
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            RequireProperties(root, "version", "settings");
            var version = Integer(root.GetProperty("version"), "version");
            var settings = version switch
            {
                1 => ReadVersionOne(root.GetProperty("settings")),
                2 => ReadVersionTwo(root.GetProperty("settings")),
                _ => throw new ArgumentException("不支持的显示设置版本。"),
            };
            Validate(settings);
            if (version == 1)
            {
                BackupVersionOne(path, original);
                // 同目录的其它工具若已经改写配置，拒绝覆盖其新版本。恢复后下次启动再重新读取，
                // 不能把开始迁移时的旧快照无条件压到新配置上。
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(original))
                    throw new IOException("迁移期间显示设置已变化。");
                Persist(settings);
            }
            _state = new(1, settings);
        }
        catch (Exception ex)
        {
            // 配置损坏、备份失败或版本迁移失败均不发布默认启用状态，也不覆盖原配置。
            // 用户显式恢复时才允许替换；失败原因只用于诊断，不参与自动重试或业务许可。
            _state = new(1, new GameFocusOverlaySettings(), false, "显示设置无法读取，请重置显示设置后重新配置。");
            warning?.Invoke($"Game focus overlay settings unavailable: {ex.Message}");
        }
    }

    public GameFocusOverlayState Snapshot => Volatile.Read(ref _state);

    /// <summary>
    /// 顶层支持部分更新；layout/content是完整子对象，不按旧客户端字段或缺失值进行猜测合并。
    /// 全部字段验证与落盘位于同一修订事务内，版本冲突只能补读，不能自动重放用户操作。
    /// </summary>
    public GameFocusOverlayState Update(long expectedRevision, JsonElement changes)
    {
        lock (_gate)
        {
            if (_state.Revision != expectedRevision) throw new InvalidOperationException("显示设置已变化，请刷新后重试。");
            if (changes.ValueKind != JsonValueKind.Object || !changes.EnumerateObject().Any())
                throw new ArgumentException("缺少显示设置改动。");
            var settings = _state.Settings;
            var reset = false;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in changes.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new ArgumentException("显示设置字段不能重复。");
                settings = property.Name switch
                {
                    "enabled" => settings with { Enabled = Boolean(property.Value, property.Name) },
                    "backgroundOpacity" => settings with { BackgroundOpacity = Integer(property.Value, property.Name) },
                    "contentOpacity" => settings with { ContentOpacity = Integer(property.Value, property.Name) },
                    "borderWidth" => settings with { BorderWidth = Integer(property.Value, property.Name) },
                    "fontSize" => settings with { FontSize = Integer(property.Value, property.Name) },
                    "theme" => settings with { Theme = Text(property.Value, property.Name) },
                    "scrollPercent" => settings with { ScrollPercent = Integer(property.Value, property.Name) },
                    "layout" => settings with { Layout = ReadLayout(property.Value) },
                    "content" => settings with { Content = ReadContent(property.Value) },
                    "resetLayout" => settings,
                    _ => throw new ArgumentException($"不支持的显示设置字段：{property.Name}。"),
                };
                if (property.Name == "resetLayout") reset = Boolean(property.Value, property.Name);
            }
            // 恢复动作必须独立提交，避免“恢复默认同时启用显示”意外绕过坏配置的显式恢复确认。
            if (reset && keys.Count != 1) throw new ArgumentException("重置显示设置必须单独提交。");
            if (!_state.Ready && !reset) throw new InvalidOperationException(_state.Status);
            if (reset) settings = _state.Ready
                ? settings with { Layout = GameFocusOverlayLayout.Default, ScrollPercent = 0 }
                : new GameFocusOverlaySettings();
            Validate(settings);
            if (_state.Ready && settings == _state.Settings) return _state;
            // 先计算新修订并落盘，再发布引用；溢出或IO异常都不能产生“磁盘已改、内存失败”。
            var next = new GameFocusOverlayState(checked(_state.Revision + 1), settings);
            Persist(settings);
            Volatile.Write(ref _state, next);
            return next;
        }
    }

    private void Persist(GameFocusOverlaySettings settings)
    {
        if (_path != null) AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(new { version = 2, settings }, JsonOptions));
    }

    /// <summary>
    /// 版本1只有三项顶层内容偏好。严格读全旧协议后迁入新子对象，其它内容默认显示，主题和
    /// 滚动恢复新版本默认值；原有启用、外观和比例布局完整保留，不迁移已废弃的编辑会话。
    /// </summary>
    private static GameFocusOverlaySettings ReadVersionOne(JsonElement settings)
    {
        RequireProperties(settings, "enabled", "backgroundOpacity", "contentOpacity", "borderWidth", "fontSize",
            "showRare", "showNormal", "rareFirst", "layout");
        return ReadAppearance(settings) with
        {
            Content = new()
            {
                ShowRare = Boolean(settings.GetProperty("showRare"), "showRare"),
                ShowNormal = Boolean(settings.GetProperty("showNormal"), "showNormal"),
                RareFirst = Boolean(settings.GetProperty("rareFirst"), "rareFirst"),
            },
        };
    }

    private static GameFocusOverlaySettings ReadVersionTwo(JsonElement settings)
    {
        RequireProperties(settings, "enabled", "backgroundOpacity", "contentOpacity", "borderWidth", "fontSize",
            "layout", "content", "theme", "scrollPercent");
        return ReadAppearance(settings) with
        {
            Content = ReadContent(settings.GetProperty("content")),
            Theme = Text(settings.GetProperty("theme"), "theme"),
            ScrollPercent = Integer(settings.GetProperty("scrollPercent"), "scrollPercent"),
        };
    }

    private static GameFocusOverlaySettings ReadAppearance(JsonElement settings) => new()
    {
        Enabled = Boolean(settings.GetProperty("enabled"), "enabled"),
        BackgroundOpacity = Integer(settings.GetProperty("backgroundOpacity"), "backgroundOpacity"),
        ContentOpacity = Integer(settings.GetProperty("contentOpacity"), "contentOpacity"),
        BorderWidth = Integer(settings.GetProperty("borderWidth"), "borderWidth"),
        FontSize = Integer(settings.GetProperty("fontSize"), "fontSize"),
        Layout = ReadLayout(settings.GetProperty("layout")),
    };

    private static GameFocusOverlayContent ReadContent(JsonElement content)
    {
        RequireProperties(content, "showRare", "showNormal", "rareFirst", "showOrderTags", "showFood",
            "showIngredients", "showExtras", "showCooker", "showBeverage");
        return new()
        {
            ShowRare = Boolean(content.GetProperty("showRare"), "showRare"),
            ShowNormal = Boolean(content.GetProperty("showNormal"), "showNormal"),
            RareFirst = Boolean(content.GetProperty("rareFirst"), "rareFirst"),
            ShowOrderTags = Boolean(content.GetProperty("showOrderTags"), "showOrderTags"),
            ShowFood = Boolean(content.GetProperty("showFood"), "showFood"),
            ShowIngredients = Boolean(content.GetProperty("showIngredients"), "showIngredients"),
            ShowExtras = Boolean(content.GetProperty("showExtras"), "showExtras"),
            ShowCooker = Boolean(content.GetProperty("showCooker"), "showCooker"),
            ShowBeverage = Boolean(content.GetProperty("showBeverage"), "showBeverage"),
        };
    }

    private static GameFocusOverlayLayout ReadLayout(JsonElement layout)
    {
        RequireProperties(layout, "x", "y", "width", "height");
        return new(Number(layout.GetProperty("x"), "x"), Number(layout.GetProperty("y"), "y"),
            Number(layout.GetProperty("width"), "width"), Number(layout.GetProperty("height"), "height"));
    }

    /// <summary>每个对象只接受完整的精确字段；Remove同时检测未知字段和重复字段，不默认补缺项。</summary>
    private static void RequireProperties(JsonElement value, params string[] properties)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("显示设置结构不正确。");
        var remaining = new HashSet<string>(properties, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw new ArgumentException("显示设置包含重复或未知字段。");
        if (remaining.Count != 0) throw new ArgumentException("显示设置缺少必要字段。");
    }

    private static bool Boolean(JsonElement value, string field) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw new ArgumentException($"{field}必须为布尔值。");
    private static int Integer(JsonElement value, string field) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
        ? result : throw new ArgumentException($"{field}必须为整数。");
    private static double Number(JsonElement value, string field) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result)
        && double.IsFinite(result) ? result : throw new ArgumentException($"{field}必须为有限数字。");
    private static string Text(JsonElement value, string field) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new ArgumentException($"{field}必须为文本。");

    /// <summary>
    /// 写出精确版本1备份，禁止覆盖已有不同内容。使用同目录临时文件和不覆盖重命名保证
    /// 备份要么完整存在，要么不发布；随后版本2替换失败时，原配置和完整备份都可恢复。
    /// </summary>
    private static void BackupVersionOne(string path, byte[] original)
    {
        var backup = Path.GetFullPath(path) + ".v1.bak";
        if (File.Exists(backup))
        {
            if (!File.ReadAllBytes(backup).AsSpan().SequenceEqual(original))
                throw new IOException("已有版本1备份与当前配置不同，未覆盖备份。");
            return;
        }
        var temporary = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(original);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, backup, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>保存值必须有限且完全位于屏幕内；运行时像素最小尺寸由几何策略保证，不静默钳制API输入。</summary>
    internal static void Validate(GameFocusOverlaySettings settings)
    {
        var p = settings.Layout;
        if (settings.BackgroundOpacity is < 0 or > 100 || settings.ContentOpacity is < 20 or > 100
            || settings.BorderWidth is < 0 or > 6 || settings.FontSize is < 12 or > 24
            || settings.ScrollPercent is < 0 or > 100 || settings.Theme is not ("light" or "dark") || settings.Content == null
            || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Width) || !double.IsFinite(p.Height)
            || p.X < 0 || p.Y < 0 || p.Width <= 0 || p.Height <= 0 || p.X + p.Width > 1.000001 || p.Y + p.Height > 1.000001)
            throw new ArgumentException("游戏内专注显示设置超出允许范围。");
    }
}
