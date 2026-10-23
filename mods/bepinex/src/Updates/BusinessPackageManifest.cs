using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MystiaStewardCompanion.Updates;

/// <summary>
/// 校验新版 Mod、Flutter 和独立更新器的唯一完整清单。清单覆盖全部托管载荷，
/// 自身由外层 ZIP 的来源与摘要保护；不接受旧五文件清单或部分客户端覆盖。
/// </summary>
internal static class BusinessPackageManifest
{
    internal static readonly string[] Files =
    {
        "MystiaStewardCompanion.BepInEx.dll", "MystiaStewardCompanion.Contracts.dll",
        "MystiaStewardCompanion.Business.dll", "mystia-steward-companion-updater.exe",
        "companion/mystia-steward-companion.exe", "companion/flutter_windows.dll",
        "companion/msvcp140.dll", "companion/vcruntime140.dll", "companion/vcruntime140_1.dll",
        "companion/data/app.so", "companion/data/icudtl.dat", "companion/data/flutter_assets/AssetManifest.bin",
        "companion/data/flutter_assets/FontManifest.json", "companion/data/flutter_assets/NOTICES.Z",
        "companion/THIRD-PARTY-NOTICES.txt",
    };
    private const string ManifestName = "package-manifest.json";
    private const int MaximumFiles = 8192;
    private const long MaximumFileSize = 536870912;
    private const long MaximumPackageSize = 2147483648;

    /// <summary>校验字段、路径、精确集合、文件大小及摘要，不加载任何程序集或执行载荷。</summary>
    internal static void Validate(string directory)
    {
        try { ValidateCore(directory); }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("完整更新包校验失败：" + error.Message, error); }
    }

    private static void ValidateCore(string directory)
    {
        AssertNoReparseAncestors(directory);
        var manifestPath = Path.Combine(directory, ManifestName);
        Require(File.Exists(manifestPath) && new FileInfo(manifestPath).Length is > 0 and <= 4194304, "缺少或超长 package-manifest.json。");
        // 先遍历物理文件再读取清单，避免目录链接让摘要验证读到安装边界之外。
        var actual = ReadTree(directory);
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath), new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        AssertKeys(root, "schemaVersion", "product", "version", "sourceCommit", "sourceDigest", "toolchainSha256", "businessProtocolVersion", "clientProtocolVersion", "files");
        foreach (var name in new[] { "schemaVersion", "businessProtocolVersion", "clientProtocolVersion" }) RequireCanonicalInteger(root.GetProperty(name));
        Require(root.GetProperty("schemaVersion").GetInt32() == 1 && root.GetProperty("product").GetString() == "mystia-steward-companion", "不支持的包规范或产品。");
        Require(root.GetProperty("businessProtocolVersion").GetInt32() == 1 && root.GetProperty("clientProtocolVersion").GetInt32() == 2, "成套协议版本不一致。");
        Require(Regex.IsMatch(root.GetProperty("version").GetString() ?? "", @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$"), "产品版本无效。");
        Require(Regex.IsMatch(root.GetProperty("sourceCommit").GetString() ?? "", "^[0-9a-f]{40}$"), "源码提交无效。");
        foreach (var key in new[] { "sourceDigest", "toolchainSha256" }) Require(IsHash(root.GetProperty(key).GetString()), key + "无效。");
        var entries = root.GetProperty("files");
        Require(entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() >= Files.Length && entries.GetArrayLength() <= MaximumFiles, "载荷数量无效。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exactNames = new HashSet<string>(StringComparer.Ordinal); long total = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            AssertKeys(entry, "path", "size", "sha256");
            var name = entry.GetProperty("path").GetString() ?? ""; ValidateRelativePath(name);
            Require((Files.Contains(name, StringComparer.Ordinal) || name.StartsWith("companion/", StringComparison.Ordinal)) && seen.Add(name), "未知或重复托管路径：" + name);
            exactNames.Add(name);
            RequireCanonicalInteger(entry.GetProperty("size"));
            var size = entry.GetProperty("size").GetInt64(); var expected = entry.GetProperty("sha256").GetString();
            Require(size >= 0 && size <= MaximumFileSize && IsHash(expected), "无效文件元数据：" + name);
            // 必需运行数据与许可不能仅因扩展名不是 DLL/EXE 而接受零长度；其它空资源仍允许。
            Require(size > 0 || !(Files.Contains(name, StringComparer.Ordinal) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)), "必需或程序组件为空：" + name);
            total += size; Require(total <= MaximumPackageSize, "包总大小超过2GiB。");
            Require(actual.TryGetValue(name, out var file) && file.Length == size, "载荷缺失或大小不符：" + name);
            using var stream = file!.OpenRead(); using var algorithm = SHA256.Create();
            Require(Convert.ToHexString(algorithm.ComputeHash(stream)).ToLowerInvariant() == expected, "载荷摘要不匹配：" + name);
        }
        foreach (var name in Files) Require(exactNames.Contains(name), "缺少必需组件：" + name);
        Require(actual.Count == exactNames.Count + 1 && actual.Keys.All(name => name == ManifestName || exactNames.Contains(name)), "包内存在清单外文件。");
    }

    /// <summary>逐层拒绝链接与大小写冲突，不追随重解析点扫描外部目录。</summary>
    private static Dictionary<string, FileInfo> ReadTree(string directory)
    {
        var files = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<DirectoryInfo>(); queue.Enqueue(new DirectoryInfo(directory)); long bytes = 0;
        while (queue.Count > 0)
        {
            foreach (var item in queue.Dequeue().EnumerateFileSystemInfos())
            {
                Require((item.Attributes & FileAttributes.ReparsePoint) == 0, "包内含链接：" + item.FullName);
                var relative = Path.GetRelativePath(directory, item.FullName).Replace('\\', '/'); ValidateRelativePath(relative);
                Require(folded.Add(relative), "包内路径大小写冲突：" + relative);
                if (item is DirectoryInfo child) queue.Enqueue(child);
                else if (item is FileInfo file)
                {
                    Require(file.Length <= MaximumFileSize && files.Count < MaximumFiles + 1, "包文件大小或数量超过上限。");
                    bytes += file.Length; Require(bytes <= MaximumPackageSize, "包总大小超过上限。"); files.Add(relative, file);
                }
                else throw new InvalidDataException("包内不是普通文件：" + relative);
            }
        }
        return files;
    }

    private static void AssertNoReparseAncestors(string path)
    {
        for (var cursor = new DirectoryInfo(Path.GetFullPath(path)); cursor != null; cursor = cursor.Parent)
            Require(cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) == 0, "包目录含链接或不存在：" + cursor.FullName);
    }
    /// <summary>
    /// 在任何文件写入之前检查 ZIP 中央目录，拒绝越界、大小写重复、链接和超限展开载荷。
    /// 目标必须是新的暂存目录；不删除调用方已有目录，也不允许 overwrite 掩盖重复条目。
    /// </summary>
    internal static void Extract(string packagePath, string extractRoot, CancellationToken cancellationToken)
    {
        Require(!Directory.Exists(extractRoot) && !File.Exists(extractRoot), "解压目标必须不存在。");
        AssertNoReparseAncestors(Path.GetDirectoryName(Path.GetFullPath(extractRoot))!);
        using var archive = System.IO.Compression.ZipFile.OpenRead(packagePath);
        Require(archive.Entries.Count is > 0 and <= MaximumFiles * 2, "ZIP条目数量超过上限。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.EndsWith('/') ? entry.FullName[..^1] : entry.FullName;
            ValidateRelativePath(name);
            Require(name == "mystia-steward-companion" || name.StartsWith("mystia-steward-companion/", StringComparison.Ordinal), "ZIP根目录不符合产品规范。");
            Require(names.Add(name), "ZIP含重复或大小写冲突路径。");
            // Unix mode 的链接/设备类型与 DOS reparse 属性均不可作为普通载荷解压。
            var kind = (entry.ExternalAttributes >> 16) & 0xf000;
            Require(kind is 0 or 0x8000 or 0x4000 && (entry.ExternalAttributes & 0x400) == 0, "ZIP包含链接或特殊文件。");
            Require(entry.Length >= 0 && entry.Length <= MaximumFileSize, "ZIP单文件超过上限。");
            total += entry.Length; Require(total <= MaximumPackageSize, "ZIP展开总量超过上限。");
        }
        Directory.CreateDirectory(extractRoot);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(extractRoot, entry.FullName);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open(); using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920]; long written = 0; int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested(); written += count;
                Require(written <= entry.Length && written <= MaximumFileSize, "ZIP实际展开长度超过声明。");
                output.Write(buffer, 0, count);
            }
            Require(written == entry.Length, "ZIP实际展开长度与声明不符。");
        }
    }

    private static void ValidateRelativePath(string name)
    {
        Require(name.Length is > 0 and <= 240 && !name.StartsWith('/') && !Regex.IsMatch(name, "[\\\\:\\x00-\\x1f\\x7f<>\"|?*]"), "清单路径含危险字符。");
        foreach (var part in name.Split('/')) Require(part.Length > 0 && part is not "." and not ".." && !part.EndsWith('.') && !part.EndsWith(' ')
            && !Regex.IsMatch(part, @"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)", RegexOptions.IgnoreCase), "清单路径未规范化：" + name);
    }
    private static void AssertKeys(JsonElement value, params string[] expected)
    {
        Require(value.ValueKind == JsonValueKind.Object, "清单字段必须是对象。");
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        Require(names.Length == expected.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length && expected.All(name => names.Contains(name, StringComparer.Ordinal)), "清单字段集合无效。");
    }
    private static bool IsHash(string? value) => value != null && Regex.IsMatch(value, "^[0-9a-f]{64}$");
    /// <summary>拒绝 1.0、1e0 和 -0 等 JSON 数值别名，与 Node/Rust 整数字段语义一致。</summary>
    private static void RequireCanonicalInteger(JsonElement value) => Require(value.ValueKind == JsonValueKind.Number && Regex.IsMatch(value.GetRawText(), "^(0|[1-9][0-9]*)$"), "清单整数必须是规范非负十进制。");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
