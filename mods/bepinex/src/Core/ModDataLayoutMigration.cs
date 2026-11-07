using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MystiaStewardCompanion.Core;

/// <summary>已解析但尚未绑定的 CFG 项；Value 保留 BepInEx 的转义文本，由宿主使用真实转换器校验。</summary>
internal sealed record ModConfigEntry(string Section, string Key, string Value);

/// <summary>
/// 只承载迁移代码构造的公开错误说明；不得传入配置值、本机路径或底层异常消息。
/// 插件入口仅允许此类型直接显示消息，其余异常只报告类型，避免泄露凭据和私有位置。
/// </summary>
internal sealed class ModDataValidationException : IOException
{
    internal ModDataValidationException(string message) : base(message) { }
}

/// <summary>
/// 在配置绑定及各 JSON 仓库升级之前建立可恢复的布局边界。只备份固定白名单的原始字节，
/// 不解释或升级业务 JSON；日志、更新包、图片和其他目录不会被递归收集。
/// </summary>
internal static partial class ModDataLayoutMigration
{
    internal const int CurrentLayoutVersion = 1;
    private const int MaximumConfigBytes = 1024 * 1024;
    private const int MaximumBackupFileBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string PreparedConfig = "prepared-plugin.cfg";
    private const string FreshConfig = "# mystia-steward-companion\n[LocalApi]\nToken = \n[Diagnostics]\nAggregateModLogPath = \n";

    /// <summary>
    /// 成功后才可绑定主配置和启动数据服务。未知布局、损坏配置、原件冲突及 I/O 失败均抛错，
    /// 不回退默认配置。checkpoint 仅供隔离故障测试，在明确持久化边界注入中断。
    /// </summary>
    internal static void EnsureReady(ModDataPaths paths, Action<IReadOnlyList<ModConfigEntry>> validateConfig,
        Action<string>? checkpoint = null, Func<string, bool>? isLegacyCompanion = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(validateConfig);
        RequireOrdinaryPath(paths.Root);
        var state = ReadState(paths.LayoutStatePath);
        if (state?.Status == "complete")
        {
            // 完成后只认新配置。旧原件可能用于旧版回退，不能再参与覆盖或重新导入。
            ValidateConfiguration(RequiredBytes(paths.PluginConfigPath, MaximumConfigBytes), validateConfig);
            return;
        }

        // 先验证配置，再产生任何迁移写入；不存在的配置属于首次安装，空或损坏文件则不是。
        // prepared 已经记录了唯一的原件集合与待发布配置。尤其日志路径规范化后新旧 CFG
        // 允许不同，恢复不能重新按“首次迁移的新旧文件必须一致”作决策。
        var captured = state == null ? Capture(paths) : null;
        var desired = captured == null ? null : PrepareConfiguration(captured, validateConfig, isLegacyCompanion);
        if (state != null)
        {
            var pendingBackup = Path.Combine(paths.MigrationBackupRoot, state.BackupDirectory);
            VerifyBackup(pendingBackup, state);
            ValidateConfiguration(RequiredBytes(Path.Combine(pendingBackup, PreparedConfig), MaximumConfigBytes), validateConfig);
        }
        RequireOrdinaryPath(paths.MigrationBackupRoot);
        Directory.CreateDirectory(paths.Root);
        RequireOrdinaryPath(paths.Root);
        var lockPath = Path.Combine(paths.Root, ".layout-migration.lock");
        RequireOrdinaryPath(lockPath);
        using var ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // 同进程/跨进程迁移都必须取得文件锁；锁不声称能阻止外部编辑器修改配置。
        state = ReadState(paths.LayoutStatePath, out var preparedStateHash);
        if (state?.Status == "complete")
        {
            ValidateConfiguration(RequiredBytes(paths.PluginConfigPath, MaximumConfigBytes), validateConfig);
            return;
        }
        if (state == null)
        {
            // 等锁期间另一迁移可能已完成；仅在锁内仍没有记录时使用首次采集结果。
            captured ??= Capture(paths);
            desired ??= PrepareConfiguration(captured, validateConfig, isLegacyCompanion);
            AssertSourcesUnchanged(paths, captured, desired);
            state = PrepareBackup(paths, captured, desired);
            var preparedStateBytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            DurablePublish(paths.LayoutStatePath, preparedStateBytes, null);
            preparedStateHash = Hash(preparedStateBytes);
            checkpoint?.Invoke("after-prepared");
        }

        var backup = Path.Combine(paths.MigrationBackupRoot, state.BackupDirectory);
        VerifyBackup(backup, state);
        if (Hash(RequiredBytes(paths.LayoutStatePath, MaximumConfigBytes)) != preparedStateHash)
            throw new ModDataValidationException("迁移记录在发布期间发生变化；未覆盖外部修改。");
        var prepared = RequiredBytes(Path.Combine(backup, PreparedConfig), MaximumConfigBytes);
        ValidateConfiguration(prepared, validateConfig);
        var originals = state.Files.ToDictionary(item => item.Key,
            item => RequiredBytes(Path.Combine(backup, item.Key), MaximumBackupFileBytes), StringComparer.Ordinal);
        AssertSourcesUnchanged(paths, originals, prepared);
        checkpoint?.Invoke("before-config-publish");
        var target = OptionalBytes(paths.PluginConfigPath, MaximumConfigBytes);
        if (target == null || !target.AsSpan().SequenceEqual(prepared))
        {
            // 已有目标只能从其已备份的原内容规范化日志字段；绝不以旧 CFG 覆盖不同的新目标。
            var expected = originals.TryGetValue("current-plugin.cfg", out var previous) ? Hash(previous) : null;
            DurablePublish(paths.PluginConfigPath, prepared, expected);
        }
        checkpoint?.Invoke("after-config-publish");
        AssertSourcesUnchanged(paths, originals, prepared);
        checkpoint?.Invoke("before-complete");
        var previousStateHash = Hash(RequiredBytes(paths.LayoutStatePath, MaximumConfigBytes));
        // 完成标记只能接续本轮核验过的 prepared 记录，不能把外部改写的记录当成可覆盖旧值。
        if (previousStateHash != preparedStateHash)
            throw new ModDataValidationException("迁移记录在发布期间发生变化；未覆盖外部修改。");
        state.Status = "complete";
        DurablePublish(paths.LayoutStatePath, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), previousStateHash);
    }

    /// <summary>固定逻辑名避免将本机绝对路径写入迁移记录，且不会接受记录中提供的任意源路径。</summary>
    private static Dictionary<string, string> Sources(ModDataPaths paths) => new(StringComparer.Ordinal)
    {
        ["legacy-plugin.cfg"] = paths.LegacyPluginConfigPath,
        ["current-plugin.cfg"] = paths.PluginConfigPath,
        ["favorites.json"] = paths.FavoritesPath,
        ["custom-recipes.json"] = paths.CustomRecipesPath,
        ["custom-recipes.json.v1.backup"] = paths.CustomRecipesPath + ".v1.backup",
        ["companion-devices.json"] = paths.DeviceAuthorityPath,
        ["game-focus-overlay.json"] = paths.GameFocusOverlayPath,
        ["game-focus-overlay.json.v1.bak"] = paths.GameFocusOverlayPath + ".v1.bak",
    };

    private static Dictionary<string, byte[]> Capture(ModDataPaths paths)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (key, path) in Sources(paths))
        {
            var bytes = OptionalBytes(path, key.EndsWith(".cfg", StringComparison.Ordinal) ? MaximumConfigBytes : MaximumBackupFileBytes);
            if (bytes != null) result.Add(key, bytes);
        }
        return result;
    }

    private static byte[] PrepareConfiguration(IReadOnlyDictionary<string, byte[]> files,
        Action<IReadOnlyList<ModConfigEntry>> validateConfig, Func<string, bool>? isLegacyCompanion)
    {
        files.TryGetValue("legacy-plugin.cfg", out var legacy);
        files.TryGetValue("current-plugin.cfg", out var current);
        if (legacy != null) ValidateConfiguration(legacy, validateConfig);
        if (current != null) ValidateConfiguration(current, validateConfig);
        if (legacy != null && current != null && !legacy.AsSpan().SequenceEqual(current))
            throw new ModDataValidationException("新旧主配置内容冲突；请检查并保留所需版本，迁移未覆盖配置。");
        var source = current ?? legacy ?? Utf8.GetBytes(FreshConfig);
        var text = DecodeConfiguration(source);
        var section = "";
        var changed = false;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1].Trim();
            var separator = line.IndexOf('=');
            if (section == "Diagnostics" && separator > 0 && line[..separator].Trim() == "AggregateModLogPath"
                && line[(separator + 1)..].Trim().Length != 0)
            {
                lines[index] = "AggregateModLogPath = ";
                changed = true;
            }
            // 仅首次导入旧 CFG 时识别已知公开 Tauri 载荷。未知自定义路径与已完成布局不重置，
            // 恢复 prepared 时也只使用既有待发布原件，不重新探测可能变化的外部程序。
            if (legacy != null && section == "Companion" && separator > 0
                && line[..separator].Trim() == "ExecutablePath" && isLegacyCompanion != null
                && isLegacyCompanion(line[(separator + 1)..].Trim()))
            {
                lines[index] = "ExecutablePath = ";
                changed = true;
            }
        }
        // 无需规范化时保留字节（包括 BOM/换行）；路径迁移不得顺便改写 Token 或其他选项。
        return changed ? Utf8.GetBytes(string.Join('\n', lines)) : source;
    }

    /// <summary>
    /// 只接受 BepInEx 的分节和单行键值语法，拒绝重复项和未识别行；未知选项仍原样保留，
    /// 已知类型及 Token 由宿主真实转换器检查，避免 ConfigFile 忽略错误后保存默认值。
    /// </summary>
    internal static void ValidateConfiguration(byte[] bytes, Action<IReadOnlyList<ModConfigEntry>> validateConfig)
    {
        var text = DecodeConfiguration(bytes);
        var section = "";
        var entries = new List<ModConfigEntry>();
        var keys = new HashSet<(string Section, string Key)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                section = line[1..^1].Trim();
                if (section.Length == 0 || section.Contains('[') || section.Contains(']'))
                    throw new ModDataValidationException("主配置分节格式无效。");
                continue;
            }
            var separator = line.IndexOf('=');
            if (section.Length == 0 || separator <= 0) throw new ModDataValidationException("主配置存在无法识别的行。");
            var key = line[..separator].Trim();
            if (key.Length == 0 || !keys.Add((section, key))) throw new ModDataValidationException("主配置键为空或重复。");
            entries.Add(new(section, key, line[(separator + 1)..].Trim()));
        }
        if (entries.Count == 0) throw new ModDataValidationException("主配置为空，不能用默认配置覆盖。");
        validateConfig(entries);
    }

    private static string DecodeConfiguration(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumConfigBytes) throw new ModDataValidationException("主配置大小无效。");
        try
        {
            var text = Utf8.GetString(bytes);
            if (text.StartsWith('\uFEFF')) text = text[1..];
            if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                throw new ModDataValidationException("主配置包含无效控制字符。");
            return text;
        }
        catch (DecoderFallbackException) { throw new ModDataValidationException("主配置不是有效 UTF-8 文本。"); }
    }

    private static LayoutState PrepareBackup(ModDataPaths paths, Dictionary<string, byte[]> originals, byte[] prepared)
    {
        Directory.CreateDirectory(paths.MigrationBackupRoot);
        RequireOrdinaryPath(paths.MigrationBackupRoot);
        var name = "layout-v1-" + Guid.NewGuid().ToString("N");
        var stage = Path.Combine(paths.MigrationBackupRoot, "." + name + ".pending");
        var destination = Path.Combine(paths.MigrationBackupRoot, name);
        RequireOrdinaryPath(stage);
        Directory.CreateDirectory(stage);
        RequireOrdinaryPath(stage);
        var state = new LayoutState { LayoutVersion = CurrentLayoutVersion, Status = "prepared", BackupDirectory = name,
            ConfigurationSha256 = Hash(prepared), Files = originals.Select(pair => new BackupEntry
                { Key = pair.Key, Length = pair.Value.LongLength, Sha256 = Hash(pair.Value) }).ToArray() };
        foreach (var (key, bytes) in originals) DurablePublish(Path.Combine(stage, key), bytes, null);
        DurablePublish(Path.Combine(stage, PreparedConfig), prepared, null);
        DurablePublish(Path.Combine(stage, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), null);
        VerifyBackup(stage, state);
        RequireOrdinaryPath(destination);
        Directory.Move(stage, destination);
        return state;
    }

    private static void VerifyBackup(string directory, LayoutState state)
    {
        RequireOrdinaryPath(directory);
        var manifest = ReadState(Path.Combine(directory, "manifest.json"));
        if (manifest == null || manifest.Status != "prepared" || manifest.BackupDirectory != state.BackupDirectory
            || manifest.ConfigurationSha256 != state.ConfigurationSha256
            || JsonSerializer.Serialize(manifest.Files, JsonOptions) != JsonSerializer.Serialize(state.Files, JsonOptions))
            throw new ModDataValidationException("迁移记录与原件备份清单不一致。");
        foreach (var file in state.Files)
        {
            var bytes = RequiredBytes(Path.Combine(directory, file.Key), MaximumBackupFileBytes);
            if (bytes.LongLength != file.Length || Hash(bytes) != file.Sha256)
                throw new ModDataValidationException("迁移备份摘要不匹配，未继续写入。");
        }
        if (Hash(RequiredBytes(Path.Combine(directory, PreparedConfig), MaximumConfigBytes)) != state.ConfigurationSha256)
            throw new ModDataValidationException("待发布配置的迁移备份已改变。");
    }

    private static void AssertSourcesUnchanged(ModDataPaths paths, IReadOnlyDictionary<string, byte[]> originals, byte[] prepared)
    {
        foreach (var (key, path) in Sources(paths))
        {
            var actual = OptionalBytes(path, MaximumBackupFileBytes);
            if (key == "current-plugin.cfg" && actual != null && actual.AsSpan().SequenceEqual(prepared)) continue;
            originals.TryGetValue(key, out var expected);
            if ((actual == null) != (expected == null) || actual != null && !actual.AsSpan().SequenceEqual(expected))
                throw new ModDataValidationException("迁移期间源文件或目标配置发生变化；未覆盖外部修改。");
        }
    }

    private static LayoutState? ReadState(string path) => ReadState(path, out _);

    /// <summary>解析记录和计算预期摘要使用同一份字节，避免后读的新内容被误认作已经核验的状态。</summary>
    private static LayoutState? ReadState(string path, out string? originalHash)
    {
        var bytes = OptionalBytes(path, MaximumConfigBytes);
        originalHash = bytes == null ? null : Hash(bytes);
        if (bytes == null) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            ExactFields(root, "layoutVersion", "status", "backupDirectory", "configurationSha256", "files");
            if (!root.GetProperty("layoutVersion").TryGetInt32(out var version) || version != CurrentLayoutVersion)
                throw new ModDataValidationException("不支持此数据目录布局版本；不能降级覆盖。");
            var state = JsonSerializer.Deserialize<LayoutState>(bytes, JsonOptions)!;
            if (state.Status is not ("prepared" or "complete") || !state.BackupDirectory.StartsWith("layout-v1-", StringComparison.Ordinal)
                || !Guid.TryParseExact(state.BackupDirectory[10..], "N", out _) || !ValidHash(state.ConfigurationSha256)
                || state.Files == null || state.Files.Length > 8)
                throw new ModDataValidationException("数据目录迁移记录格式无效。");
            var allowed = new HashSet<string>(new[] { "legacy-plugin.cfg", "current-plugin.cfg", "favorites.json", "custom-recipes.json",
                "custom-recipes.json.v1.backup", "companion-devices.json", "game-focus-overlay.json", "game-focus-overlay.json.v1.bak" }, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in root.GetProperty("files").EnumerateArray()) ExactFields(raw, "key", "length", "sha256");
            foreach (var file in state.Files)
                if (file == null || !allowed.Contains(file.Key) || !seen.Add(file.Key) || file.Length < 0
                    || file.Length > MaximumBackupFileBytes || !ValidHash(file.Sha256))
                    throw new ModDataValidationException("数据目录迁移记录含未知或无效文件。");
            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NullReferenceException)
        { throw new ModDataValidationException("数据目录迁移记录损坏，未自动重建。"); }
    }

    private static void ExactFields(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ModDataValidationException("迁移记录结构无效。");
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || expected.Except(names, StringComparer.Ordinal).Any()) throw new ModDataValidationException("迁移记录字段无效。");
    }

    /// <summary>校验完整父链，拒绝文件和目录链接；权限错误必须传播，不能由 File.Exists 折叠成“不存在”。</summary>
    private static void RequireOrdinaryPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            var attributes = Attributes(current);
            if (attributes.HasValue && (attributes.Value & FileAttributes.ReparsePoint) != 0)
                throw new ModDataValidationException("数据路径包含链接或重解析点，迁移已停止。");
        }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static byte[]? OptionalBytes(string path, int maximum)
    {
        RequireOrdinaryPath(path);
        var attributes = Attributes(path);
        if (attributes == null) return null;
        if ((attributes.Value & FileAttributes.Directory) != 0) throw new ModDataValidationException("预期配置文件的位置是目录。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new ModDataValidationException("迁移文件超出大小上限。");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        if (buffer.Length > maximum) throw new ModDataValidationException("迁移文件在读取期间超出大小上限。");
        return buffer.ToArray();
    }

    private static byte[] RequiredBytes(string path, int maximum) => OptionalBytes(path, maximum)
        ?? throw new ModDataValidationException("迁移所需文件缺失，未自动重建。");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// 临时文件显式刷新到磁盘并回读校验后才发布；覆盖必须匹配调用方刚核对的旧摘要。
    /// 本方法不宣称提供多个文件的一次性文件系统事务，跨文件恢复由 prepared 记录完成。
    /// </summary>
    private static void DurablePublish(string path, byte[] bytes, string? expectedHash)
    {
        RequireOrdinaryPath(path);
        var current = OptionalBytes(path, MaximumBackupFileBytes);
        if (current == null ? expectedHash != null : expectedHash == null || Hash(current) != expectedHash)
            throw new ModDataValidationException("迁移目标已存在或内容改变，拒绝覆盖。");
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".layout-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (Hash(RequiredBytes(temporary, MaximumBackupFileBytes)) != Hash(bytes)) throw new IOException("迁移临时文件回读校验失败。");
            RequireOrdinaryPath(path);
            current = OptionalBytes(path, MaximumBackupFileBytes);
            if (current == null ? expectedHash != null : expectedHash == null || Hash(current) != expectedHash)
                throw new ModDataValidationException("发布前目标已改变，拒绝覆盖。");
            File.Move(temporary, path, overwrite: current != null);
            if (Hash(RequiredBytes(path, MaximumBackupFileBytes)) != Hash(bytes)) throw new IOException("迁移发布结果回读校验失败。");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class LayoutState
    {
        public int LayoutVersion { get; set; }
        public string Status { get; set; } = "";
        public string BackupDirectory { get; set; } = "";
        public string ConfigurationSha256 { get; set; } = "";
        public BackupEntry[] Files { get; set; } = Array.Empty<BackupEntry>();
    }
    private sealed class BackupEntry
    {
        public string Key { get; set; } = "";
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
