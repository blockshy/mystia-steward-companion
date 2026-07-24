namespace MystiaStewardCompanion.Save;

internal enum RuntimeCoreMappingIdDomain
{
    NonNegativeContent,
    Signed,
}

internal static class RuntimeCoreMappingProjection
{
    private const int MaxMappingItems = 4096;

    public static IReadOnlyList<int> ReadIds(
        IReadOnlyList<RuntimeDictionaryEntry> entries,
        string mappingName,
        RuntimeCoreMappingIdDomain idDomain)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrWhiteSpace(mappingName))
        {
            throw new ArgumentException("Mapping name is required.", nameof(mappingName));
        }

        if (idDomain is not RuntimeCoreMappingIdDomain.NonNegativeContent
            and not RuntimeCoreMappingIdDomain.Signed)
        {
            throw new ArgumentOutOfRangeException(nameof(idDomain), idDomain, "Unknown core mapping ID domain.");
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException($"{mappingName} is empty.");
        }

        if (entries.Count > MaxMappingItems)
        {
            throw new InvalidOperationException(
                $"{mappingName} exceeded the {MaxMappingItems}-item limit.");
        }

        var seenIds = new HashSet<int>();
        var ids = new List<int>();
        foreach (var entry in entries)
        {
            if (entry.Key is not int id)
            {
                var actualType = entry.Key?.GetType().FullName ?? "<null>";
                throw new InvalidOperationException(
                    $"{mappingName} contains key type {actualType} instead of Int32.");
            }

            if (entry.Value is not string value || string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"{mappingName}[{id}] contains an invalid String value.");
            }

            if (!seenIds.Add(id))
            {
                throw new InvalidOperationException($"{mappingName} contains duplicate ID {id}.");
            }

            if (idDomain == RuntimeCoreMappingIdDomain.NonNegativeContent && id < 0) continue;
            ids.Add(id);
        }

        if (ids.Count == 0)
        {
            var domainDescription = idDomain == RuntimeCoreMappingIdDomain.NonNegativeContent
                ? "non-negative content"
                : "signed";
            throw new InvalidOperationException(
                $"{mappingName} contains no {domainDescription} IDs.");
        }

        return ids.OrderBy(id => id).ToList();
    }
}
