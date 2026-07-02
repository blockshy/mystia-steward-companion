using System.Text;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeStaticDataDiagnosticFormatter
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, string> LastSignatureBySection = new(StringComparer.OrdinalIgnoreCase);

    public static AggregateLogSection? FormatMappedSpecialGuests(RuntimeMappedGuestCatalogSnapshot snapshot)
    {
        var signature = BuildSignature(snapshot);
        lock (SyncRoot)
        {
            if (LastSignatureBySection.TryGetValue("runtime-static-data", out var lastSignature)
                && string.Equals(lastSignature, signature, StringComparison.Ordinal))
            {
                return null;
            }

            LastSignatureBySection["runtime-static-data"] = signature;
        }

        return new AggregateLogSection("runtime-static-data", "Runtime Static Data", FormatMappedSpecialGuestsContent(snapshot));
    }

    public static IReadOnlyList<AggregateLogSection> FormatStaticData(RuntimeStaticDataSnapshot snapshot)
    {
        var sections = new List<AggregateLogSection>();
        AddSection(
            sections,
            "runtime-tags",
            "Runtime Tags",
            "DataBaseLanguage tag tables and DataBaseCore TagRules",
            snapshot,
            snapshot.TagLines);
        AddSection(
            sections,
            "runtime-database",
            "Runtime Core Database",
            "DataBaseCore ingredients, beverages, foods, and recipes with local-data comparison",
            snapshot,
            snapshot.CoreLines);
        AddSection(
            sections,
            "runtime-guests",
            "Runtime Guests",
            "DataBaseCharacter normal guests, special guests, mapped guests, and guest easter data",
            snapshot,
            snapshot.GuestLines);
        AddSection(
            sections,
            "runtime-izakayas",
            "Runtime Izakayas",
            "DataBaseCore izakaya scene pools and labels",
            snapshot,
            snapshot.IzakayaLines);
        return sections;
    }

    private static void AddSection(
        ICollection<AggregateLogSection> sections,
        string channel,
        string title,
        string source,
        RuntimeStaticDataSnapshot snapshot,
        IReadOnlyList<string> lines)
    {
        var signature = BuildSignature(snapshot, lines);
        lock (SyncRoot)
        {
            if (LastSignatureBySection.TryGetValue(channel, out var lastSignature)
                && string.Equals(lastSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            LastSignatureBySection[channel] = signature;
        }

        sections.Add(new AggregateLogSection(channel, title, FormatSectionContent(source, snapshot, lines)));
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
                .Append('/')
                .Append(entry.RuntimeCustomer?.Id.ToString() ?? "")
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

    private static string FormatSectionContent(
        string source,
        RuntimeStaticDataSnapshot snapshot,
        IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
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

        return builder.ToString();
    }

    private static string FormatMappedSpecialGuestsContent(RuntimeMappedGuestCatalogSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Source: DataBaseCharacter.GetAllMappedGuests() + GetSpecialGuestsAndMappedGuests()");
        builder.AppendLine($"ReadAtUtc: {snapshot.CapturedAtUtc:O}");
        builder.AppendLine($"Status: {snapshot.Status}");
        builder.AppendLine($"RuntimeGuestAliases: {snapshot.Entries.Count}");
        builder.AppendLine($"ResolvedLocalGuests: {snapshot.LocalResolvedCount}");
        builder.AppendLine($"RuntimeSyntheticGuests: {snapshot.RuntimeSyntheticCount}");
        foreach (var entry in snapshot.Entries)
        {
            builder.AppendLine(
                $"  - runtimeId={FormatNullable(entry.RuntimeId)}; strId={entry.RuntimeStringId}; sourceGuestId={FormatNullable(entry.SourceGuestId)}; sourceStringId={entry.SourceStringId}; sourceName={entry.SourceDisplayName}; localId={FormatNullable(entry.LocalRareCustomerId)}; localName={entry.LocalRareCustomerName}; runtimeCustomer={FormatRuntimeCustomer(entry.RuntimeCustomer)}; aliasSource={entry.AliasSource}; overrideDestination={entry.OverrideDestination}; type={entry.RuntimeTypeName}");
        }

        return builder.ToString();
    }

    private static string FormatNullable(int? value)
    {
        return value.HasValue ? value.Value.ToString() : "";
    }

    private static string FormatRuntimeCustomer(RuntimeRareCustomer? customer)
    {
        if (customer == null) return "";
        return $"{customer.Name}({customer.Id}); food=[{string.Join(",", customer.PositiveTags)}]; hate=[{string.Join(",", customer.NegativeTags)}]; bev=[{string.Join(",", customer.BeverageTags)}]";
    }
}

internal sealed record AggregateLogSection(string Channel, string Title, string Content);
