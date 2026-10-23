using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>
/// 只管理服务端固定导出目录中的诊断 ZIP。调用方只能提交列举所得的不透明 ID，
/// 不能提交路径；ID 绑定文件名、长度和最后写入时间，替换后的文件需要重新列举确认。
/// 所有目录祖先与目标文件拒绝重解析点，不递归枚举、不删除目录或非诊断文件。
/// </summary>
internal sealed class DiagnosticPackageStore
{
    private static readonly Regex PackageName = new(
        @"^mystia-steward-companion-diagnostics-[0-9]{8}-[0-9]{6}(?:-[0-9a-f]{32})?\.zip$",
        RegexOptions.CultureInvariant);
    private readonly string _directory;
    private readonly object _sync = new();
    // 只允许本实例确实列举过的版本被删除；重启、换文件或重复删除都必须重新获取列表。
    private readonly Dictionary<string, string> _listed = new(StringComparer.Ordinal);

    public DiagnosticPackageStore(string directory) => _directory = Path.GetFullPath(directory);

    /// <summary>
    /// 返回本实例实际管理的绝对目录。导出回执使用同一来源，避免注入隔离存储后
    /// 仍访问全局 BepInEx 路径，或返回与实际 ZIP 所在位置不一致的目录。
    /// 该属性不创建目录、不枚举文件，也不放宽各操作原有的重解析点校验。
    /// </summary>
    public string DirectoryPath => _directory;

    public IReadOnlyList<LocalApiDiagnosticPackageItemDto> List()
    {
        lock (_sync)
        {
            ValidateAncestors(_directory);
            _listed.Clear();
            if (!Directory.Exists(_directory)) return Array.Empty<LocalApiDiagnosticPackageItemDto>();
            var result = new List<LocalApiDiagnosticPackageItemDto>();
            foreach (var path in Directory.EnumerateFiles(_directory, "*.zip", SearchOption.TopDirectoryOnly))
            {
                if (!PackageName.IsMatch(Path.GetFileName(path))) continue;
                if (result.Count >= 2048) throw new IOException("诊断包数量过多，请先在本机整理导出目录。");
                var item = ReadItem(path);
                if (item == null) continue;
                _listed.Add(item.Id, path);
                result.Add(item);
            }
            return result.OrderByDescending(item => item.CreatedAtUtc, StringComparer.Ordinal)
                .ThenBy(item => item.FileName, StringComparer.Ordinal).ToArray();
        }
    }

    public void Delete(string id)
    {
        lock (_sync)
        {
            ValidateAncestors(_directory);
            if (id.Length != 64 || !_listed.TryGetValue(id, out var path))
                throw new InvalidOperationException("诊断包列表已变化，请刷新后重新确认删除。");
            var current = ReadItem(path);
            if (current == null || current.Id != id)
                throw new InvalidOperationException("诊断包已被替换或移除，请刷新后重新确认删除。");
            // 最后一次验证后只删除精确文件；不用递归删除，也不接收客户端文件名拼接路径。
            ValidateAncestors(path);
            File.Delete(path);
            _listed.Remove(id);
        }
    }

    /// <summary>导出采用唯一临时文件后发布，列表不会读到尚未完成的 ZIP。</summary>
    public string Create(Action<ZipArchive> write)
    {
        lock (_sync)
        {
            ValidateAncestors(_directory);
            Directory.CreateDirectory(_directory);
            ValidateAncestors(_directory);
            var name = "mystia-steward-companion-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N") + ".zip";
            var final = Path.Combine(_directory, name);
            var temporary = final + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create)) write(archive);
                ValidateAncestors(_directory);
                File.Move(temporary, final);
                return final;
            }
            finally
            {
                // 仅清理由本次随机名称创建的未发布文件，不触及任何用户 ZIP。
                ValidateAncestors(_directory);
                if (File.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0)
                    File.Delete(temporary);
            }
        }
    }

    private LocalApiDiagnosticPackageItemDto? ReadItem(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _directory, StringComparison.OrdinalIgnoreCase)
            || !PackageName.IsMatch(Path.GetFileName(path))) return null;
        ValidateAncestors(path);
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        var length = info.Length;
        var modified = info.LastWriteTimeUtc.Ticks;
        // 文件名不是所有权证据；旧版导出的固定清单与快照必须同时存在，任意改名 ZIP 不纳管。
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var manifest = archive.GetEntry("manifest.json");
            if (manifest == null || manifest.Length > 65536 || manifest.Length == 0
                || archive.GetEntry("snapshot/current-snapshot.json") == null
                || archive.GetEntry("snapshot/runtime-data.json") == null) return null;
            using var manifestStream = manifest.Open();
            using var document = JsonDocument.Parse(manifestStream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("generatedAtUtc", out var generated)
                || generated.ValueKind != JsonValueKind.String || !generated.TryGetDateTime(out _)
                || !root.TryGetProperty("aggregateModLogMaxFileCount", out var count) || !count.TryGetInt32(out _)
                || !root.TryGetProperty("baseUrl", out var baseUrl) || baseUrl.ValueKind != JsonValueKind.String) return null;
        }
        catch (InvalidDataException) { return null; }
        catch (JsonException) { return null; }
        info.Refresh();
        if (!info.Exists || info.Length != length || info.LastWriteTimeUtc.Ticks != modified)
            throw new IOException("诊断包正在变化，请稍后刷新。");
        var identity = info.Name + "\n" + length.ToString(CultureInfo.InvariantCulture) + "\n" + modified.ToString(CultureInfo.InvariantCulture);
        return new LocalApiDiagnosticPackageItemDto
        {
            Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant(),
            FileName = info.Name, ByteLength = length, CreatedAtUtc = info.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static void ValidateAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("诊断目录或文件包含链接，已拒绝访问。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
