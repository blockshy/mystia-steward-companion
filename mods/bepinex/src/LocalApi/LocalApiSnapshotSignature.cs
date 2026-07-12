using System.Security.Cryptography;
using System.Text;

namespace MystiaStewardCompanion.LocalApi;

internal static class LocalApiSnapshotSignature
{
    public static string Compute(string canonicalContent)
    {
        ArgumentNullException.ThrowIfNull(canonicalContent);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalContent));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
