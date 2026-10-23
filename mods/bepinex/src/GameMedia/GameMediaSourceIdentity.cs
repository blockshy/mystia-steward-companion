using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MystiaStewardCompanion.GameMedia;

/// <summary>
/// 图片来源的纯托管指纹。资源索引做真实 SHA256；大体积 bundle 只记录精确文件名、长度和
/// 最后写入时间，避免每次启动全读数 GB。该元数据不是内容哈希，正常游戏更新由索引摘要
/// 识别；手动改包而同时伪造全部元数据不在此快速检查的证明范围，可由显式重读更新。
/// </summary>
internal sealed record GameMediaSourceIdentity(string SourceFingerprint, string? StartupFingerprint,
    string? StartupFailureReason = null, int BundleCount = 0)
{
    internal const string AlgorithmVersion = "notebook-pixel-avatar-v1-sprite-mesh-128";
    // 当前完整 DLC 安装实测有 7,934 个资源包，旧 4,096 上限会使每次启动永久失去复用凭据。
    // 这里约束的是后台读取的文件元数据条目，不是图片数量；保留 65,536 的独立有界容量，
    // 允许游戏资源增长，同时防止异常目录造成无界枚举/排序。图片本身仍遵守 Store 的严格上限。
    internal const int MaximumBundleFiles = 65536;
    private sealed record BundleStamp(string Name, long Length, long ModifiedUtcTicks);

    internal static GameMediaSourceIdentity Read(string catalogPath, string unityVersion,
        string applicationVersion, IReadOnlyList<string>? activeDlcLabels)
    {
        if (string.IsNullOrWhiteSpace(unityVersion) || string.IsNullOrWhiteSpace(applicationVersion))
            throw new InvalidDataException("游戏资源版本不可确认。");
        RequireOrdinaryPath(catalogPath);
        using var stream = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 64 * 1024 * 1024)
            throw new InvalidDataException("游戏资源索引大小不在预期范围。");
        using var sha256 = SHA256.Create();
        var catalogHash = Convert.ToHexString(sha256.ComputeHash(stream));
        // 保持原有 source 指纹字节公式，旧 PNG 仍能在真实目录通过后原代升级启动凭据。
        var source = Hash(AlgorithmVersion + "\n" + unityVersion + "\n" + applicationVersion + "\n" + catalogHash);
        if (!ValidLabels(activeDlcLabels)) return new(source, null, "dlc-state-unavailable");
        var files = new List<BundleStamp>();
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(catalogPath)!, "StandaloneWindows64");
            RequireOrdinaryPath(directory);
            // 父链只验证一次；FileInfo 枚举已携带文件元数据，不为每个 bundle 重复 stat 整条
            // 父目录链。逐项仍拒绝链接和非法长度，全部磁盘工作继续在调用方后台任务中完成。
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false,
                MatchType = MatchType.Win32,
            };
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.bundle", options))
            {
                if (files.Count >= MaximumBundleFiles)
                    return new(source, null, "bundle-count-limit", files.Count + 1);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    return new(source, null, "bundle-path-unsupported", files.Count);
                if (file.Length <= 0 || file.Name.Length > 240)
                    return new(source, null, "bundle-metadata-invalid", files.Count);
                files.Add(new(file.Name, file.Length, file.LastWriteTimeUtc.Ticks));
            }
            if (files.Count == 0) return new(source, null, "bundle-set-empty");
            // 排序仅规范集合表示，不改变游戏 DLC 规则；不保存目录路径或账户信息。
            var payload = JsonSerializer.Serialize(new
            {
                version = 1, source,
                dlc = activeDlcLabels!.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                bundles = files.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray(),
            });
            return new(source, Hash(payload), BundleCount: files.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or NotSupportedException)
        {
            // 附加启动证据未知时仍可由真实目录提取图片，但不能持久化可提前复用的凭据。
            // 原生异常可能包含用户路径，只提供固定原因码供低频诊断，不将异常正文传给客户端。
            return new(source, null, "bundle-metadata-unavailable", files.Count);
        }
    }

    internal static bool ValidLabels(IReadOnlyList<string>? labels) => labels is { Count: > 0 and <= 64 }
        && labels.Contains("CORE", StringComparer.Ordinal)
        && labels.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 128)
        && labels.Distinct(StringComparer.Ordinal).Count() == labels.Count;

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void RequireOrdinaryPath(string path)
    {
        // 不沿目录链接读取任意本机资源；缺失或访问失败同样作为证据不足处理。
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("资源路径包含链接。");
    }
}
