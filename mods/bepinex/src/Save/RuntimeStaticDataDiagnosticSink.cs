using System.Text;
using BepInEx;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeStaticDataDiagnosticSink
{
    private const long MaxLogBytes = 256 * 1024;
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, string> LastSignatureByPath = new(StringComparer.OrdinalIgnoreCase);

    public static string ResolvePath(string? diagnosticsPath)
    {
        if (!string.IsNullOrWhiteSpace(diagnosticsPath))
        {
            var directory = System.IO.Path.GetDirectoryName(diagnosticsPath.Trim());
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return System.IO.Path.Combine(directory, "runtime-static-data.log");
            }
        }

        return System.IO.Path.Combine(Paths.ConfigPath, "MystiaStewardCompanion", "runtime-static-data.log");
    }

    public static void WriteMappedSpecialGuests(string path, RuntimeMappedGuestCatalogSnapshot snapshot)
    {
        var signature = BuildSignature(snapshot);
        lock (SyncRoot)
        {
            if (LastSignatureByPath.TryGetValue(path, out var lastSignature)
                && string.Equals(lastSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            LastSignatureByPath[path] = signature;
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            RotateIfNeeded(path);
            File.AppendAllText(path, FormatMappedSpecialGuests(snapshot), Encoding.UTF8);
        }
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length <= MaxLogBytes) return;

        var backupPath = path + ".bak";
        if (File.Exists(backupPath)) File.Delete(backupPath);
        File.Move(path, backupPath);
    }

    private static string BuildSignature(RuntimeMappedGuestCatalogSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.Append(snapshot.Status).Append('|');
        foreach (var entry in snapshot.Entries)
        {
            builder
                .Append(entry.RuntimeId?.ToString() ?? "")
                .Append(':')
                .Append(entry.RuntimeStringId)
                .Append("->")
                .Append(entry.SourceGuestId?.ToString() ?? "")
                .Append('/')
                .Append(entry.LocalRareCustomerId?.ToString() ?? "")
                .Append(';');
        }

        return builder.ToString();
    }

    private static string FormatMappedSpecialGuests(RuntimeMappedGuestCatalogSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("==== mystia-steward-companion Runtime Static Data ====");
        builder.AppendLine($"Utc: {DateTime.UtcNow:O}");
        builder.AppendLine("Source: DataBaseCharacter.GetAllMappedGuests()");
        builder.AppendLine($"ReadAtUtc: {snapshot.CapturedAtUtc:O}");
        builder.AppendLine($"Status: {snapshot.Status}");
        builder.AppendLine($"MappedGuests: {snapshot.Entries.Count}");
        builder.AppendLine($"ResolvedLocalGuests: {snapshot.ResolvedCount}");
        foreach (var entry in snapshot.Entries)
        {
            builder.AppendLine(
                $"  - runtimeId={FormatNullable(entry.RuntimeId)}; strId={entry.RuntimeStringId}; sourceGuestId={FormatNullable(entry.SourceGuestId)}; sourceStringId={entry.SourceStringId}; sourceName={entry.SourceDisplayName}; localId={FormatNullable(entry.LocalRareCustomerId)}; localName={entry.LocalRareCustomerName}; overrideDestination={entry.OverrideDestination}; type={entry.RuntimeTypeName}");
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private static string FormatNullable(int? value)
    {
        return value.HasValue ? value.Value.ToString() : "";
    }
}
