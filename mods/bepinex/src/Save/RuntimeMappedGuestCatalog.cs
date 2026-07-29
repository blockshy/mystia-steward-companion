using System.Reflection;
using System.Runtime.ExceptionServices;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal sealed class RuntimeMappedGuestCatalog
{
    private const string DataBaseCharacterTypeName = "GameData.Core.Collections.CharacterUtility.DataBaseCharacter";
    private const int MaxMappedGuests = 4096;

    private static readonly object SyncRoot = new();
    private static RuntimeMappedGuestCatalogSnapshot _snapshot = RuntimeMappedGuestCatalogSnapshot.Empty("not loaded");
    private static RuntimeMappedGuestMethodSet? _cachedMethods;
    private static bool _loaded;

    private readonly IReadOnlyDictionary<int, RareCustomer> _rareCustomersById;

    public RuntimeMappedGuestCatalog(DataRepository repository)
    {
        _rareCustomersById = repository.RareCustomersById;
    }

    public RuntimeMappedGuestCatalogSnapshot Snapshot()
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return _snapshot;
        }
    }

    public static bool TryGetLoadedSnapshot(out RuntimeMappedGuestCatalogSnapshot snapshot)
    {
        lock (SyncRoot)
        {
            snapshot = _snapshot;
            return _loaded && snapshot.IsComplete;
        }
    }

    public static void ResetSnapshot()
    {
        lock (SyncRoot)
        {
            _loaded = false;
            _snapshot = RuntimeMappedGuestCatalogSnapshot.Empty("not loaded");
        }
    }

    public RareCustomerIdentity? Resolve(int? runtimeId, string? runtimeStringId)
    {
        var entry = FindEntry(Snapshot(), runtimeId, runtimeStringId);
        if (entry?.LocalRareCustomerId is int localId
            && _rareCustomersById.TryGetValue(localId, out var mappedCustomer))
        {
            return new RareCustomerIdentity(mappedCustomer.Id, mappedCustomer.Name);
        }

        return null;
    }

    private static RuntimeMappedGuestEntry? FindEntry(
        RuntimeMappedGuestCatalogSnapshot snapshot,
        int? runtimeId,
        string? runtimeStringId)
    {
        if (runtimeId.HasValue && snapshot.ByRuntimeId.TryGetValue(runtimeId.Value, out var byId))
        {
            return byId;
        }

        if (!string.IsNullOrWhiteSpace(runtimeStringId)
            && snapshot.ByRuntimeStringId.TryGetValue(runtimeStringId.Trim(), out var byStringId))
        {
            return byStringId;
        }

        return null;
    }

    private void EnsureLoaded()
    {
        lock (SyncRoot)
        {
            if (_loaded) return;
        }

        var nextSnapshot = ReadSnapshot();
        lock (SyncRoot)
        {
            _snapshot = nextSnapshot;
            _loaded = nextSnapshot.IsComplete;
        }
    }

    private RuntimeMappedGuestCatalogSnapshot ReadSnapshot()
    {
        try
        {
            var characterType = RuntimeReflectionUtility.FindType(DataBaseCharacterTypeName)
                ?? throw new InvalidOperationException($"{DataBaseCharacterTypeName} is not loaded.");
            var methods = ResolveRequiredMethods(characterType);
            var source = InvokeRequiredStatic(methods.GetAllMappedGuests);
            if (!RuntimeConcreteCollectionReader.TryReadReferenceArray(source, out var values, out var failure))
            {
                throw new InvalidOperationException($"GetAllMappedGuests returned an unreadable array: {failure}.");
            }

            if (values.Count > MaxMappedGuests)
            {
                throw new InvalidOperationException(
                    $"GetAllMappedGuests exceeded the {MaxMappedGuests}-item limit.");
            }

            var mappings = new List<RuntimeMappedGuestMetadata>(values.Count);
            var ids = new HashSet<int>();
            var stringIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                if (value == null) throw new InvalidOperationException("GetAllMappedGuests returned a null entry.");
                var id = ReadRequiredNonNegativeInt(value, "id");
                var stringId = ReadRequiredString(value, "stringId");
                var sourceGuestId = ReadRequiredNonNegativeInt(value, "sourceGuestID");
                if (!ids.Add(id)) throw new InvalidOperationException($"Duplicate mapped guest ID {id}.");
                if (!stringIds.Add(stringId))
                {
                    throw new InvalidOperationException($"Duplicate mapped guest StringId '{stringId}'.");
                }

                mappings.Add(new RuntimeMappedGuestMetadata(
                    id,
                    stringId,
                    sourceGuestId,
                    value.GetType().FullName ?? value.GetType().Name));
            }

            var mappingsById = mappings.ToDictionary(mapping => mapping.RuntimeId);
            var baseGuestsById = ReadBaseGuestIdentities(methods.GetAllSpecialGuests);
            ValidateCombinedIdentityDomain(baseGuestsById, mappings);

            var entries = new List<RuntimeMappedGuestEntry>(baseGuestsById.Count + mappings.Count);
            foreach (var baseGuest in baseGuestsById.Values.OrderBy(guest => guest.Id))
            {
                _rareCustomersById.TryGetValue(baseGuest.Id, out var customer);
                entries.Add(new RuntimeMappedGuestEntry
                {
                    RuntimeId = baseGuest.Id,
                    RuntimeStringId = baseGuest.StringId,
                    SourceGuestId = baseGuest.Id,
                    SourceStringId = baseGuest.StringId,
                    SourceDisplayName = customer?.Name ?? "",
                    LocalRareCustomerId = customer?.Id,
                    LocalRareCustomerName = customer?.Name ?? "",
                    AliasSource = "base-identity",
                    RuntimeTypeName = baseGuest.RuntimeTypeName,
                });
            }

            foreach (var mapping in mappings.OrderBy(mapping => mapping.RuntimeId))
            {
                var canonicalId = ResolveCanonicalSourceId(mapping, mappingsById, baseGuestsById);
                var baseGuest = baseGuestsById[canonicalId];
                _rareCustomersById.TryGetValue(canonicalId, out var customer);

                entries.Add(new RuntimeMappedGuestEntry
                {
                    RuntimeId = mapping.RuntimeId,
                    RuntimeStringId = mapping.RuntimeStringId,
                    SourceGuestId = canonicalId,
                    SourceStringId = baseGuest.StringId,
                    SourceDisplayName = customer?.Name ?? "",
                    LocalRareCustomerId = customer?.Id,
                    LocalRareCustomerName = customer?.Name ?? "",
                    AliasSource = mapping.SourceGuestId == canonicalId
                        ? "mapped-source-id"
                        : "mapped-source-chain",
                    RuntimeTypeName = mapping.RuntimeTypeName,
                });
            }

            return new RuntimeMappedGuestCatalogSnapshot(
                DateTime.UtcNow,
                entries,
                isComplete: true,
                $"loaded: base={baseGuestsById.Count}; mapped={mappings.Count}; "
                    + $"recommendable={entries.Count(entry => entry.LocalRareCustomerId.HasValue)}; "
                    + "source=GetAllSpecialGuests+GetAllMappedGuests");
        }
        catch (Exception ex)
        {
            return RuntimeMappedGuestCatalogSnapshot.Empty($"unavailable: {ex.Message}");
        }
    }

    private static IReadOnlyDictionary<int, RuntimeBaseGuestIdentityMetadata> ReadBaseGuestIdentities(
        MethodInfo getAllSpecialGuests)
    {
        var source = InvokeRequiredStatic(getAllSpecialGuests);
        if (!RuntimeConcreteCollectionReader.TryReadReferenceArray(source, out var values, out var failure))
        {
            throw new InvalidOperationException($"GetAllSpecialGuests returned an unreadable array: {failure}.");
        }

        if (values.Count > MaxMappedGuests)
        {
            throw new InvalidOperationException(
                $"GetAllSpecialGuests exceeded the {MaxMappedGuests}-item limit.");
        }

        var result = new Dictionary<int, RuntimeBaseGuestIdentityMetadata>();
        var stringIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (value == null) throw new InvalidOperationException("GetAllSpecialGuests returned a null entry.");
            var id = ReadRequiredNonNegativeInt(value, "id");
            var stringId = ReadRequiredString(value, "stringId");
            if (!stringIds.Add(stringId))
            {
                throw new InvalidOperationException($"Duplicate base special guest StringId '{stringId}'.");
            }

            if (!result.TryAdd(
                    id,
                    new RuntimeBaseGuestIdentityMetadata(
                        id,
                        stringId,
                        value.GetType().FullName ?? value.GetType().Name)))
            {
                throw new InvalidOperationException($"Duplicate base special guest ID {id}.");
            }
        }

        return result;
    }

    private static void ValidateCombinedIdentityDomain(
        IReadOnlyDictionary<int, RuntimeBaseGuestIdentityMetadata> baseGuestsById,
        IReadOnlyList<RuntimeMappedGuestMetadata> mappings)
    {
        var ids = baseGuestsById.Keys.ToHashSet();
        var stringIds = baseGuestsById.Values
            .Select(guest => guest.StringId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings)
        {
            if (!ids.Add(mapping.RuntimeId))
            {
                throw new InvalidOperationException(
                    $"Mapped guest ID {mapping.RuntimeId} conflicts with the base special guest identity domain.");
            }

            if (!stringIds.Add(mapping.RuntimeStringId))
            {
                throw new InvalidOperationException(
                    $"Mapped guest StringId '{mapping.RuntimeStringId}' conflicts with the base special guest identity domain.");
            }
        }
    }

    private int ResolveCanonicalSourceId(
        RuntimeMappedGuestMetadata start,
        IReadOnlyDictionary<int, RuntimeMappedGuestMetadata> mappingsById,
        IReadOnlyDictionary<int, RuntimeBaseGuestIdentityMetadata> baseGuestsById)
    {
        var current = start.SourceGuestId;
        var visited = new HashSet<int> { start.RuntimeId };
        while (true)
        {
            if (baseGuestsById.ContainsKey(current)) return current;
            if (!visited.Add(current))
            {
                throw new InvalidOperationException(
                    $"Mapped guest {start.RuntimeId}/{start.RuntimeStringId} contains a source cycle at {current}.");
            }

            if (!mappingsById.TryGetValue(current, out var next))
            {
                throw new InvalidOperationException(
                    $"Mapped guest {start.RuntimeId}/{start.RuntimeStringId} has missing source {current}.");
            }

            current = next.SourceGuestId;
        }
    }

    private static RuntimeMappedGuestMethodSet ResolveRequiredMethods(Type characterType)
    {
        lock (SyncRoot)
        {
            var cached = _cachedMethods;
            if (cached?.CharacterType == characterType) return cached;

            _cachedMethods = new RuntimeMappedGuestMethodSet(
                characterType,
                RequireExactStaticMethod(characterType, "GetAllMappedGuests"),
                RequireExactStaticMethod(characterType, "GetAllSpecialGuests"));
            return _cachedMethods;
        }
    }

    private static MethodInfo RequireExactStaticMethod(Type type, string methodName)
    {
        var matches = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal)
                && !method.IsGenericMethod
                && method.ReturnType != typeof(void)
                && method.GetParameters().Length == 0)
            .Take(2)
            .ToList();
        if (matches.Count != 1) throw new MissingMethodException(type.FullName, $"{methodName}()");

        return matches[0];
    }

    private static object InvokeRequiredStatic(MethodInfo method)
    {
        try
        {
            return method.Invoke(null, Array.Empty<object?>())
                ?? throw new InvalidOperationException(
                    $"{method.DeclaringType?.FullName}.{method.Name} returned null.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static object ReadRequiredMember(object instance, string memberName)
    {
        return RuntimeReflectionUtility.GetMemberValue(instance, memberName)
            ?? throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{memberName} is unavailable.");
    }

    private static int ReadRequiredNonNegativeInt(object instance, string memberName)
    {
        var value = ReadRequiredMember(instance, memberName);
        if (value is not int result || result < 0)
        {
            throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{memberName} returned an invalid Int32 value.");
        }

        return result;
    }

    private static string ReadRequiredString(object instance, string memberName)
    {
        var value = ReadRequiredMember(instance, memberName);
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{memberName} returned an invalid String value.");
        }

        return text.Trim();
    }

    private sealed record RuntimeMappedGuestMethodSet(
        Type CharacterType,
        MethodInfo GetAllMappedGuests,
        MethodInfo GetAllSpecialGuests);
}

internal sealed class RuntimeMappedGuestCatalogSnapshot
{
    public RuntimeMappedGuestCatalogSnapshot(
        DateTime capturedAtUtc,
        IReadOnlyList<RuntimeMappedGuestEntry> entries,
        bool isComplete,
        string status)
    {
        CapturedAtUtc = capturedAtUtc;
        Entries = entries;
        IsComplete = isComplete;
        Status = status;
        ByRuntimeId = entries.ToDictionary(entry => entry.RuntimeId!.Value);
        ByRuntimeStringId = entries.ToDictionary(
            entry => entry.RuntimeStringId,
            entry => entry,
            StringComparer.OrdinalIgnoreCase);
    }

    public DateTime CapturedAtUtc { get; }
    public IReadOnlyList<RuntimeMappedGuestEntry> Entries { get; }
    public bool IsComplete { get; }
    public string Status { get; }
    public IReadOnlyDictionary<int, RuntimeMappedGuestEntry> ByRuntimeId { get; }
    public IReadOnlyDictionary<string, RuntimeMappedGuestEntry> ByRuntimeStringId { get; }
    public int LocalResolvedCount => Entries.Count(entry => entry.LocalRareCustomerId.HasValue);
    public int ResolvedCount => Entries.Count;

    public static RuntimeMappedGuestCatalogSnapshot Empty(string status)
    {
        return new RuntimeMappedGuestCatalogSnapshot(
            DateTime.UtcNow,
            Array.Empty<RuntimeMappedGuestEntry>(),
            isComplete: false,
            status);
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
    public string AliasSource { get; init; } = "";
    public string RuntimeTypeName { get; init; } = "";
}

internal sealed record RuntimeMappedGuestMetadata(
    int RuntimeId,
    string RuntimeStringId,
    int SourceGuestId,
    string RuntimeTypeName);

internal sealed record RuntimeBaseGuestIdentityMetadata(
    int Id,
    string StringId,
    string RuntimeTypeName);
