using System.Text;
using BepInEx;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeStaticDataDiagnosticSink
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, string> LastSignatureByPath = new(StringComparer.OrdinalIgnoreCase);

    public static string ResolvePath(string? diagnosticsPath)
    {
        return ResolvePath(diagnosticsPath, "runtime-static-data.log");
    }

    public static string ResolvePath(string? diagnosticsPath, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(diagnosticsPath))
        {
            var directory = System.IO.Path.GetDirectoryName(diagnosticsPath.Trim());
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return System.IO.Path.Combine(directory, fileName);
            }
        }

        return System.IO.Path.Combine(Paths.ConfigPath, "MystiaStewardCompanion", fileName);
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

    public static void WriteStaticData(string? diagnosticsPath, RuntimeStaticDataSnapshot snapshot)
    {
        WriteSection(
            ResolvePath(diagnosticsPath, "runtime-tags.log"),
            "Runtime Tags",
            "DataBaseLanguage tag tables and DataBaseCore TagRules",
            snapshot,
            snapshot.TagLines);
        WriteSection(
            ResolvePath(diagnosticsPath, "runtime-database-diff.log"),
            "Runtime Core Database",
            "DataBaseCore ingredients, beverages, foods, and recipes with local-data comparison",
            snapshot,
            snapshot.CoreLines);
        WriteSection(
            ResolvePath(diagnosticsPath, "runtime-guests.log"),
            "Runtime Guests",
            "DataBaseCharacter normal guests, special guests, mapped guests, and guest easter data",
            snapshot,
            snapshot.GuestLines);
        WriteSection(
            ResolvePath(diagnosticsPath, "runtime-izakayas.log"),
            "Runtime Izakayas",
            "DataBaseCore izakaya scene pools and labels",
            snapshot,
            snapshot.IzakayaLines);
    }

    private static void WriteSection(
        string path,
        string title,
        string source,
        RuntimeStaticDataSnapshot snapshot,
        IReadOnlyList<string> lines)
    {
        var signature = BuildSignature(snapshot, lines);
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
            File.AppendAllText(path, FormatSection(title, source, snapshot, lines), Encoding.UTF8);
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

    private static string BuildSignature(RuntimeStaticDataSnapshot snapshot, IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        builder.Append(snapshot.Status).Append('|');
        foreach (var error in snapshot.ErrorLines)
        {
            builder.Append("err:").Append(error).Append(';');
        }

        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    private static string FormatSection(
        string title,
        string source,
        RuntimeStaticDataSnapshot snapshot,
        IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"==== mystia-steward-companion {title} ====");
        builder.AppendLine($"Utc: {DateTime.UtcNow:O}");
        builder.AppendLine($"Source: {source}");
        builder.AppendLine($"ReadAtUtc: {snapshot.CapturedAtUtc:O}");
        builder.AppendLine($"Status: {snapshot.Status}");
        builder.AppendLine($"Complete: {snapshot.IsComplete}");
        if (snapshot.ErrorLines.Count > 0)
        {
            builder.AppendLine("Errors:");
            foreach (var error in snapshot.ErrorLines)
            {
                builder.AppendLine($"  - {error}");
            }
        }

        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }

        builder.AppendLine();
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
