using System.Collections;
using System.Reflection;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal sealed class RuntimeMappedGuestCatalog
{
    private const string DataBaseCharacterTypeName = "GameData.Core.Collections.CharacterUtility.DataBaseCharacter";
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly object SyncRoot = new();
    private static RuntimeMappedGuestCatalogSnapshot _snapshot = RuntimeMappedGuestCatalogSnapshot.Empty("not loaded");
    private static DateTime _lastReadAttemptUtc = DateTime.MinValue;
    private static bool _loaded;

    private readonly RareCustomerIdentityResolver _identityResolver;

    public RuntimeMappedGuestCatalog(DataRepository repository)
    {
        _identityResolver = repository.RareCustomerIdentities;
    }

    public RuntimeMappedGuestCatalogSnapshot Snapshot()
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return _snapshot;
        }
    }

    public RareCustomerIdentity? Resolve(int? runtimeId, string? runtimeNameOrStringId)
    {
        var snapshot = Snapshot();
        RuntimeMappedGuestEntry? entry = null;

        if (runtimeId.HasValue)
        {
            snapshot.ByRuntimeId.TryGetValue(runtimeId.Value, out entry);
        }

        if (entry == null && !string.IsNullOrWhiteSpace(runtimeNameOrStringId))
        {
            snapshot.ByRuntimeStringId.TryGetValue(runtimeNameOrStringId.Trim(), out entry);
        }

        if (entry == null || !entry.LocalRareCustomerId.HasValue || string.IsNullOrWhiteSpace(entry.LocalRareCustomerName))
        {
            return null;
        }

        return new RareCustomerIdentity(entry.LocalRareCustomerId.Value, entry.LocalRareCustomerName);
    }

    private void EnsureLoaded()
    {
        lock (SyncRoot)
        {
            if (_loaded) return;
            if (DateTime.UtcNow - _lastReadAttemptUtc < RetryInterval) return;
            _lastReadAttemptUtc = DateTime.UtcNow;
        }

        var nextSnapshot = ReadSnapshot();
        lock (SyncRoot)
        {
            _snapshot = nextSnapshot;
            _loaded = nextSnapshot.Entries.Count > 0;
        }
    }

    private RuntimeMappedGuestCatalogSnapshot ReadSnapshot()
    {
        var dataBaseCharacterType = FindType(DataBaseCharacterTypeName);
        if (dataBaseCharacterType == null)
        {
            return RuntimeMappedGuestCatalogSnapshot.Empty("DataBaseCharacter type not found");
        }

        var mappedGuests = InvokeStaticMethod(dataBaseCharacterType, "GetAllMappedGuests");
        if (mappedGuests == null)
        {
            return RuntimeMappedGuestCatalogSnapshot.Empty("GetAllMappedGuests returned null");
        }

        var entries = new List<RuntimeMappedGuestEntry>();
        foreach (var mappedGuest in EnumerateObjects(mappedGuests))
        {
            if (mappedGuest == null) continue;

            var runtimeId = ToNullableInt(GetMemberValue(mappedGuest, "ID") ?? GetMemberValue(mappedGuest, "Id"));
            var runtimeStringId = GetMemberValue(mappedGuest, "StrID")?.ToString()
                ?? GetMemberValue(mappedGuest, "StringId")?.ToString();
            var sourceGuestId = ToNullableInt(GetMemberValue(mappedGuest, "SourceGuestID") ?? GetMemberValue(mappedGuest, "SourceGuestId"));
            var overrideDestination = GetMemberValue(mappedGuest, "OverrideDestination")?.ToString() ?? "";
            var sourceGuest = sourceGuestId.HasValue
                ? InvokeStaticMethod(dataBaseCharacterType, "RefSGuest", sourceGuestId.Value)
                : null;
            var sourceStringId = GetMemberValue(sourceGuest, "StringId")?.ToString()
                ?? GetMemberValue(sourceGuest, "StrID")?.ToString();
            var sourceDisplayName = GetMemberValue(sourceGuest, "Name")?.ToString()
                ?? GetMemberValue(sourceGuest, "DisplayName")?.ToString()
                ?? GetMemberValue(sourceGuest, "CharacterName")?.ToString();
            var identity = _identityResolver.Resolve(sourceGuestId, sourceStringId)
                ?? _identityResolver.Resolve(sourceGuestId, sourceDisplayName);

            entries.Add(new RuntimeMappedGuestEntry
            {
                RuntimeId = runtimeId,
                RuntimeStringId = runtimeStringId?.Trim() ?? "",
                SourceGuestId = sourceGuestId,
                SourceStringId = sourceStringId?.Trim() ?? "",
                SourceDisplayName = sourceDisplayName?.Trim() ?? "",
                LocalRareCustomerId = identity?.Id,
                LocalRareCustomerName = identity?.Name ?? "",
                OverrideDestination = overrideDestination,
                RuntimeTypeName = mappedGuest.GetType().FullName ?? mappedGuest.GetType().Name,
            });
        }

        var orderedEntries = entries
            .OrderBy(entry => entry.RuntimeId ?? int.MaxValue)
            .ThenBy(entry => entry.RuntimeStringId, StringComparer.Ordinal)
            .ToList();
        return new RuntimeMappedGuestCatalogSnapshot(
            DateTime.UtcNow,
            orderedEntries,
            $"loaded: {orderedEntries.Count}");
    }

    private static Type? FindType(string fullName)
    {
        var direct = Type.GetType(fullName, false);
        if (direct != null) return direct;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            catch
            {
                // Ignore assemblies that cannot resolve unrelated IL2CPP types.
            }
        }

        return null;
    }

    private static object? InvokeStaticMethod(Type type, string name, params object?[] args)
    {
        var method = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length);
        if (method == null) return null;

        try
        {
            return method.Invoke(null, args);
        }
        catch
        {
            return null;
        }
    }

    private static object? GetMemberValue(object? instance, string name)
    {
        if (instance == null) return null;
        var type = instance.GetType();

        while (type != null)
        {
            foreach (var fieldName in BuildFieldNameCandidates(name))
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (TryReadField(instance, field, out var fieldValue) && fieldValue != null) return fieldValue;
            }

            var property = FindProperty(type, name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadProperty(instance, property, out var propertyValue) && propertyValue != null) return propertyValue;

            var pascalName = char.ToUpperInvariant(name[0]) + name[1..];
            property = FindProperty(type, pascalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadProperty(instance, property, out propertyValue) && propertyValue != null) return propertyValue;

            type = type.BaseType;
        }

        return null;
    }

    private static IEnumerable<object?> EnumerateObjects(object? value)
    {
        if (value == null) yield break;

        if (value is IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }

            yield break;
        }

        var count = ToNullableInt(GetMemberValue(value, "Count") ?? GetMemberValue(value, "Length")) ?? 0;
        if (count <= 0) yield break;

        var indexer = value.GetType().GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (indexer == null) yield break;

        for (var i = 0; i < count; i++)
        {
            object? item;
            try
            {
                item = indexer.GetValue(value, new object[] { i });
            }
            catch
            {
                yield break;
            }

            yield return item;
        }
    }

    private static PropertyInfo? FindProperty(Type type, string name, BindingFlags flags)
    {
        try
        {
            return type.GetProperty(name, flags);
        }
        catch (AmbiguousMatchException)
        {
            return type.GetProperties(flags).FirstOrDefault(property => property.Name == name);
        }
    }

    private static bool TryReadProperty(object? instance, PropertyInfo? property, out object? value)
    {
        value = null;
        if (property == null) return false;

        try
        {
            value = property.GetValue(instance);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadField(object? instance, FieldInfo? field, out object? value)
    {
        value = null;
        if (field == null) return false;

        try
        {
            value = field.GetValue(instance);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> BuildFieldNameCandidates(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;

        yield return name;
        yield return $"<{name}>k__BackingField";
        yield return $"m_{name}";
        yield return $"_{name}";

        var camelName = char.ToLowerInvariant(name[0]) + name[1..];
        if (string.Equals(camelName, name, StringComparison.Ordinal)) yield break;

        yield return camelName;
        yield return $"<{camelName}>k__BackingField";
        yield return $"m_{camelName}";
        yield return $"_{camelName}";
    }

    private static int? ToNullableInt(object? value)
    {
        if (value == null) return null;
        if (value is int intValue) return intValue;
        if (value is long longValue) return (int)longValue;
        if (value is short shortValue) return shortValue;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }
}

internal sealed class RuntimeMappedGuestCatalogSnapshot
{
    public RuntimeMappedGuestCatalogSnapshot(DateTime capturedAtUtc, IReadOnlyList<RuntimeMappedGuestEntry> entries, string status)
    {
        CapturedAtUtc = capturedAtUtc;
        Entries = entries;
        Status = status;
        ByRuntimeId = entries
            .Where(entry => entry.RuntimeId.HasValue)
            .GroupBy(entry => entry.RuntimeId!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        ByRuntimeStringId = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.RuntimeStringId))
            .GroupBy(entry => entry.RuntimeStringId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public DateTime CapturedAtUtc { get; }
    public IReadOnlyList<RuntimeMappedGuestEntry> Entries { get; }
    public string Status { get; }
    public IReadOnlyDictionary<int, RuntimeMappedGuestEntry> ByRuntimeId { get; }
    public IReadOnlyDictionary<string, RuntimeMappedGuestEntry> ByRuntimeStringId { get; }
    public int ResolvedCount => Entries.Count(entry => entry.LocalRareCustomerId.HasValue);

    public static RuntimeMappedGuestCatalogSnapshot Empty(string status)
    {
        return new RuntimeMappedGuestCatalogSnapshot(DateTime.UtcNow, Array.Empty<RuntimeMappedGuestEntry>(), status);
    }
}

internal sealed class RuntimeMappedGuestEntry
{
    public int? RuntimeId { get; init; }
    public string RuntimeStringId { get; init; } = "";
    public int? SourceGuestId { get; init; }
    public string SourceStringId { get; init; } = "";
    public string SourceDisplayName { get; init; } = "";
    public int? LocalRareCustomerId { get; init; }
    public string LocalRareCustomerName { get; init; } = "";
    public string OverrideDestination { get; init; } = "";
    public string RuntimeTypeName { get; init; } = "";
}
