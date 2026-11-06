using System.Security.Cryptography;
using System.Text;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>对调用方已规范化的内容计算稳定摘要，用于快照变化判断，不作为鉴权签名。</summary>
internal static class LocalApiSnapshotSignature
{
    public static string Compute(string canonicalContent)
    {
        ArgumentNullException.ThrowIfNull(canonicalContent);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalContent));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
