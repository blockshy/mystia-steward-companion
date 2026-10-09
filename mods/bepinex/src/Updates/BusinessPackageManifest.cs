using System.Security.Cryptography;

namespace MystiaStewardCompanion.Updates;

/// <summary>验证业务重构后必须成套交付的精确文件集合；清单仅作完整性校验，不替代发布包的来源验证。</summary>
internal static class BusinessPackageManifest
{
    internal static readonly string[] Files =
    {
        "MystiaStewardCompanion.BepInEx.dll", "MystiaStewardCompanion.Contracts.dll",
        "MystiaStewardCompanion.Business.dll", "companion/mystia-steward-companion.exe",
        "mystia-steward-companion-updater.exe",
    };

    internal static void Validate(string directory)
    {
        var manifestPath = Path.Combine(directory, "business-bundle.sha256");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 4096)
            throw new InvalidDataException("更新包缺少有效的业务组件摘要。");
        var lines = File.ReadAllLines(manifestPath);
        if (lines.Length != Files.Length) throw new InvalidDataException("业务组件清单数量不匹配。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Length < 67 || line.Substring(64, 2) != "  ") throw new InvalidDataException("业务组件摘要格式无效。");
            var name = line[66..]; var expected = line[..64];
            if (!Files.Contains(name, StringComparer.Ordinal) || !seen.Add(name)
                || expected.Any(character => !Uri.IsHexDigit(character))) throw new InvalidDataException("业务组件清单包含未知、重复或无效项。");
            var path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidDataException($"业务组件缺失或为空：{name}");
            using var stream = File.OpenRead(path);
            using var algorithm = SHA256.Create();
            var actual = Convert.ToHexString(algorithm.ComputeHash(stream));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"业务组件摘要不匹配，拒绝混版安装：{name}");
        }
    }
}
