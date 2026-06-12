using System.Collections;
using System.Reflection;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal sealed class RuntimeMappedGuestCatalog
{
    private const string DataBaseCharacterTypeName = "GameData.Core.Collections.CharacterUtility.DataBaseCharacter";
    private const string DataBaseLanguageTypeName = "GameData.CoreLanguage.Collections.DataBaseLanguage";
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, RareCustomerIdentity> VariantAliasCache = new(StringComparer.OrdinalIgnoreCase);
    private static RuntimeMappedGuestCatalogSnapshot _snapshot = RuntimeMappedGuestCatalogSnapshot.Empty("not loaded");
    private static DateTime _lastReadAttemptUtc = DateTime.MinValue;
    private static bool _loaded;

    private readonly RareCustomerIdentityResolver _identityResolver;
    private readonly IReadOnlyDictionary<int, RareCustomer> _localRareCustomersById;
    private readonly IReadOnlyDictionary<string, RareCustomer> _uniqueLocalRareCustomersByName;

    public RuntimeMappedGuestCatalog(DataRepository repository)
    {
        _identityResolver = repository.RareCustomerIdentities;
        _localRareCustomersById = repository.RareCustomersById;
        _uniqueLocalRareCustomersByName = repository.RareCustomers
            .Where(customer => !string.IsNullOrWhiteSpace(customer.Name))
            .GroupBy(customer => customer.Name.Trim(), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    public RuntimeMappedGuestCatalogSnapshot Snapshot()
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return _snapshot;
        }
    }

    public RareCustomer? ResolveCustomer(int? runtimeId, string? runtimeNameOrStringId)
    {
        if (runtimeId.HasValue && _localRareCustomersById.TryGetValue(runtimeId.Value, out var currentLocalCustomer))
        {
            return currentLocalCustomer;
        }

        var snapshot = Snapshot();
        var entry = FindEntry(snapshot, runtimeId, runtimeNameOrStringId);
        if (entry == null) return null;

        if (entry.LocalRareCustomerId.HasValue
            && _localRareCustomersById.TryGetValue(entry.LocalRareCustomerId.Value, out var localCustomer))
        {
            return localCustomer;
        }

        if (entry.RuntimeId.HasValue && _localRareCustomersById.TryGetValue(entry.RuntimeId.Value, out var entryLocalCustomer))
        {
            return entryLocalCustomer;
        }

        if (entry.SourceGuestId.HasValue && _localRareCustomersById.TryGetValue(entry.SourceGuestId.Value, out var sourceLocalCustomer))
        {
            return sourceLocalCustomer;
        }

        return entry.RuntimeCustomer?.ToRareCustomer();
    }

    public RareCustomerIdentity? Resolve(int? runtimeId, string? runtimeNameOrStringId)
    {
        if (runtimeId.HasValue && _localRareCustomersById.TryGetValue(runtimeId.Value, out var currentLocalCustomer))
        {
            return new RareCustomerIdentity(currentLocalCustomer.Id, currentLocalCustomer.Name);
        }

        var snapshot = Snapshot();
        var entry = FindEntry(snapshot, runtimeId, runtimeNameOrStringId);

        if (entry == null)
        {
            return null;
        }

        if (entry.LocalRareCustomerId.HasValue && !string.IsNullOrWhiteSpace(entry.LocalRareCustomerName))
        {
            return new RareCustomerIdentity(entry.LocalRareCustomerId.Value, entry.LocalRareCustomerName);
        }

        if (entry.RuntimeId.HasValue && _localRareCustomersById.TryGetValue(entry.RuntimeId.Value, out var entryLocalCustomer))
        {
            return new RareCustomerIdentity(entryLocalCustomer.Id, entryLocalCustomer.Name);
        }

        if (entry.SourceGuestId.HasValue && _localRareCustomersById.TryGetValue(entry.SourceGuestId.Value, out var sourceLocalCustomer))
        {
            return new RareCustomerIdentity(sourceLocalCustomer.Id, sourceLocalCustomer.Name);
        }

        return entry.RuntimeCustomer == null
            ? null
            : new RareCustomerIdentity(entry.RuntimeCustomer.Id, entry.RuntimeCustomer.Name);
    }

    private static RuntimeMappedGuestEntry? FindEntry(
        RuntimeMappedGuestCatalogSnapshot snapshot,
        int? runtimeId,
        string? runtimeNameOrStringId)
    {
        RuntimeMappedGuestEntry? entry = null;

        if (runtimeId.HasValue)
        {
            snapshot.ByRuntimeId.TryGetValue(runtimeId.Value, out entry);
        }

        if (entry == null && !string.IsNullOrWhiteSpace(runtimeNameOrStringId))
        {
            snapshot.ByRuntimeStringId.TryGetValue(runtimeNameOrStringId.Trim(), out entry);
        }

        return entry;
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
            _loaded = nextSnapshot.ResolvedCount > 0;
        }
    }

    private RuntimeMappedGuestCatalogSnapshot ReadSnapshot()
    {
        var dataBaseCharacterType = FindType(DataBaseCharacterTypeName);
        if (dataBaseCharacterType == null)
        {
            return RuntimeMappedGuestCatalogSnapshot.Empty("DataBaseCharacter type not found");
        }
        var languageType = FindType(DataBaseLanguageTypeName);
        var foodTags = ReadTagDictionary(languageType, "GetAllFoodTags", "GetAllFoodTagsID", "GetFoodTag");
        var beverageTags = ReadTagDictionary(languageType, "GetAllBeverageTags", null, "GetBeverageTag");
        var specialGuestNames = ReadStringDictionary(languageType, "GetAllSpecialGuestsNames");

        var mappedGuests = InvokeStaticMethod(dataBaseCharacterType, "GetAllMappedGuests");
        var entries = new List<RuntimeMappedGuestEntry>();
        var mappedCount = 0;
        foreach (var mappedGuest in EnumerateObjects(mappedGuests))
        {
            if (mappedGuest == null) continue;
            mappedCount++;

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
            sourceDisplayName = ResolveSpecialGuestName(languageType, specialGuestNames, sourceGuestId, sourceDisplayName);
            var resolved = ResolveRuntimeIdentity(sourceGuestId, sourceStringId, sourceDisplayName);

            entries.Add(new RuntimeMappedGuestEntry
            {
                RuntimeId = runtimeId,
                RuntimeStringId = runtimeStringId?.Trim() ?? "",
                SourceGuestId = sourceGuestId,
                SourceStringId = sourceStringId?.Trim() ?? "",
                SourceDisplayName = sourceDisplayName?.Trim() ?? "",
                LocalRareCustomerId = resolved.Identity?.Id,
                LocalRareCustomerName = resolved.Identity?.Name ?? "",
                OverrideDestination = overrideDestination,
                AliasSource = resolved.Source == "unresolved" ? "mapped-unresolved" : $"mapped-{resolved.Source}",
                RuntimeTypeName = mappedGuest.GetType().FullName ?? mappedGuest.GetType().Name,
            });
        }

        var runtimeGuests = InvokeStaticMethod(dataBaseCharacterType, "GetSpecialGuestsAndMappedGuests");
        var runtimeGuestCount = 0;
        foreach (var runtimeGuest in EnumerateObjects(runtimeGuests))
        {
            if (runtimeGuest == null) continue;
            runtimeGuestCount++;

            var runtimeId = ToNullableInt(GetMemberValue(runtimeGuest, "ID") ?? GetMemberValue(runtimeGuest, "Id"));
            var runtimeStringId = GetMemberValue(runtimeGuest, "StringId")?.ToString()
                ?? GetMemberValue(runtimeGuest, "StrID")?.ToString()
                ?? "";
            var memberDisplayName = GetMemberValue(runtimeGuest, "Name")?.ToString()
                ?? GetMemberValue(runtimeGuest, "DisplayName")?.ToString()
                ?? GetMemberValue(runtimeGuest, "CharacterName")?.ToString();
            var runtimeDisplayName = ResolveSpecialGuestName(languageType, specialGuestNames, runtimeId, memberDisplayName);
            var resolved = ResolveRuntimeIdentity(runtimeId, runtimeStringId, runtimeDisplayName);
            var runtimeCustomer = resolved.Identity == null
                ? BuildRuntimeRareCustomer(runtimeGuest, runtimeId, runtimeStringId, runtimeDisplayName, foodTags, beverageTags)
                : null;

            entries.Add(new RuntimeMappedGuestEntry
            {
                RuntimeId = runtimeId,
                RuntimeStringId = runtimeStringId.Trim(),
                SourceGuestId = runtimeId,
                SourceStringId = runtimeStringId.Trim(),
                SourceDisplayName = runtimeDisplayName.Trim(),
                LocalRareCustomerId = resolved.Identity?.Id,
                LocalRareCustomerName = resolved.Identity?.Name ?? "",
                RuntimeCustomer = runtimeCustomer,
                OverrideDestination = "",
                AliasSource = resolved.Source == "unresolved"
                    ? runtimeCustomer == null ? "runtime-unresolved" : "runtime-synthetic"
                    : $"runtime-{resolved.Source}",
                RuntimeTypeName = runtimeGuest.GetType().FullName ?? runtimeGuest.GetType().Name,
            });
        }

        var normalizedEntries = ApplyVariantAliasNormalization(entries, out var variantAliasCount);
        var orderedEntries = normalizedEntries
            .GroupBy(BuildEntryKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(entry => entry.LocalRareCustomerId.HasValue)
                .ThenBy(entry => AliasSourcePriority(entry.AliasSource))
                .First())
            .OrderBy(entry => entry.RuntimeId ?? int.MaxValue)
            .ThenBy(entry => entry.RuntimeStringId, StringComparer.Ordinal)
            .ToList();
        return new RuntimeMappedGuestCatalogSnapshot(
            DateTime.UtcNow,
            orderedEntries,
            $"loaded: entries={orderedEntries.Count}; mapped={mappedCount}; runtimeGuests={runtimeGuestCount}; localResolved={orderedEntries.Count(entry => entry.LocalRareCustomerId.HasValue)}; runtimeSynthetic={orderedEntries.Count(entry => entry.RuntimeCustomer != null)}; variantAliases={variantAliasCount}");
    }

    private static IReadOnlyList<RuntimeMappedGuestEntry> ApplyVariantAliasNormalization(
        IReadOnlyList<RuntimeMappedGuestEntry> entries,
        out int appliedCount)
    {
        appliedCount = 0;
        var aliasGroups = entries
            .Where(entry => entry.LocalRareCustomerId.HasValue && !string.IsNullOrWhiteSpace(entry.LocalRareCustomerName))
            .Select(entry => new
            {
                Key = NormalizeRuntimeAliasKey(entry.RuntimeStringId),
                Entry = entry,
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Key = group.Key,
                Targets = group
                    .Select(item => new RareCustomerIdentity(item.Entry.LocalRareCustomerId!.Value, item.Entry.LocalRareCustomerName))
                    .Distinct()
                    .ToList(),
            })
            .Where(group => group.Targets.Count == 1)
            .ToDictionary(group => group.Key, group => group.Targets[0], StringComparer.OrdinalIgnoreCase);

        Dictionary<string, RareCustomerIdentity> aliases;
        lock (SyncRoot)
        {
            foreach (var alias in aliasGroups)
            {
                VariantAliasCache[alias.Key] = alias.Value;
            }

            aliases = new Dictionary<string, RareCustomerIdentity>(VariantAliasCache, StringComparer.OrdinalIgnoreCase);
        }

        if (aliases.Count == 0) return entries;

        var result = new List<RuntimeMappedGuestEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.LocalRareCustomerId.HasValue)
            {
                result.Add(entry);
                continue;
            }

            var key = NormalizeRuntimeAliasKey(entry.RuntimeStringId);
            if (string.IsNullOrWhiteSpace(key) || !aliases.TryGetValue(key, out var target))
            {
                result.Add(entry);
                continue;
            }

            appliedCount++;
            result.Add(CloneWithIdentity(entry, target, "variant-alias"));
        }

        return result;
    }

    private static RuntimeMappedGuestEntry CloneWithIdentity(
        RuntimeMappedGuestEntry entry,
        RareCustomerIdentity identity,
        string aliasSource)
    {
        var prefix = entry.AliasSource.StartsWith("mapped-", StringComparison.OrdinalIgnoreCase)
            ? "mapped"
            : "runtime";

        return new RuntimeMappedGuestEntry
        {
            RuntimeId = entry.RuntimeId,
            RuntimeStringId = entry.RuntimeStringId,
            SourceGuestId = entry.SourceGuestId,
            SourceStringId = entry.SourceStringId,
            SourceDisplayName = entry.SourceDisplayName,
            LocalRareCustomerId = identity.Id,
            LocalRareCustomerName = identity.Name,
            RuntimeCustomer = null,
            OverrideDestination = entry.OverrideDestination,
            AliasSource = $"{prefix}-{aliasSource}",
            RuntimeTypeName = entry.RuntimeTypeName,
        };
    }

    private RuntimeRareCustomer? BuildRuntimeRareCustomer(
        object runtimeGuest,
        int? runtimeId,
        string runtimeStringId,
        string runtimeDisplayName,
        IReadOnlyDictionary<int, string> foodTags,
        IReadOnlyDictionary<int, string> beverageTags)
    {
        if (!runtimeId.HasValue) return null;
        if (!IsUsableRuntimeCustomerName(runtimeDisplayName)) return null;
        if (IsSuppressedRuntimeStringId(runtimeStringId)) return null;
        if (ToBool(GetMemberValue(runtimeGuest, "DoNotShowInNotebook"))) return null;

        var spawnType = GetMemberValue(runtimeGuest, "SpawnType")?.ToString() ?? "";
        if (string.Equals(spawnType, "NeverCome", StringComparison.OrdinalIgnoreCase)) return null;

        var positiveTags = ReadRuntimeTagNames(
            GetMemberValue(runtimeGuest, "LikeFoodTag")
                ?? GetMemberValue(runtimeGuest, "LikeFoodTagUnfolded")
                ?? GetMemberValue(runtimeGuest, "LikeFoodTagOriginal"),
            foodTags,
            includeFoodTags: true);
        var negativeTags = ReadRuntimeTagNames(
            GetMemberValue(runtimeGuest, "HateFoodTag")
                ?? GetMemberValue(runtimeGuest, "HateFoodTagOriginal"),
            foodTags,
            includeFoodTags: true);
        var beverageTagNames = ReadRuntimeTagNames(
            GetMemberValue(runtimeGuest, "LikeBevTag")
                ?? GetMemberValue(runtimeGuest, "LikeBevTagUnfolded")
                ?? GetMemberValue(runtimeGuest, "LikeBevTagOriginal"),
            beverageTags,
            includeFoodTags: false);

        if (positiveTags.Count == 0 && beverageTagNames.Count == 0) return null;

        return new RuntimeRareCustomer
        {
            Id = runtimeId.Value,
            RuntimeStringId = runtimeStringId.Trim(),
            Name = runtimeDisplayName.Trim(),
            Places = new List<string>(),
            PositiveTags = positiveTags,
            NegativeTags = negativeTags,
            BeverageTags = beverageTagNames,
            Source = "runtime-special-guest",
        };
    }

    private ResolvedRuntimeIdentity ResolveRuntimeIdentity(int? runtimeId, string? runtimeStringId, string? runtimeDisplayName)
    {
        if (runtimeId.HasValue && _localRareCustomersById.TryGetValue(runtimeId.Value, out var localById))
        {
            return new ResolvedRuntimeIdentity(new RareCustomerIdentity(localById.Id, localById.Name), "local-id");
        }

        if (TryResolveByUniqueLocalName(runtimeDisplayName, out var localByDisplayName))
        {
            return new ResolvedRuntimeIdentity(new RareCustomerIdentity(localByDisplayName.Id, localByDisplayName.Name), "name");
        }

        var manualIdentity = _identityResolver.Resolve(runtimeId, runtimeStringId)
            ?? _identityResolver.Resolve(runtimeId, runtimeDisplayName);
        return manualIdentity == null
            ? new ResolvedRuntimeIdentity(null, "unresolved")
            : new ResolvedRuntimeIdentity(manualIdentity, "manual-alias");
    }

    private bool TryResolveByUniqueLocalName(string? runtimeDisplayName, out RareCustomer customer)
    {
        customer = null!;
        if (!IsUsableAliasName(runtimeDisplayName)) return false;
        return _uniqueLocalRareCustomersByName.TryGetValue(runtimeDisplayName!.Trim(), out customer!);
    }

    private static bool IsUsableAliasName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var name = value.Trim();
        if (name.Contains("?", StringComparison.Ordinal)) return false;
        if (name.StartsWith("#", StringComparison.Ordinal)) return false;
        if (name.Equals("Null", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool IsUsableRuntimeCustomerName(string? value)
    {
        if (!IsUsableAliasName(value)) return false;
        var name = value!.Trim();
        return !name.Equals("??????", StringComparison.Ordinal);
    }

    private static bool IsSuppressedRuntimeStringId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        return text.EndsWith("_Intro", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith("_Parallel", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith("_Current", StringComparison.OrdinalIgnoreCase)
            || text.Contains("_Angry", StringComparison.OrdinalIgnoreCase)
            || text.Contains("_Sad", StringComparison.OrdinalIgnoreCase)
            || text.Contains("_Happy", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeRuntimeAliasKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length == 0) return null;

        var underscoreIndex = text.IndexOf('_');
        if (underscoreIndex > 3
            && text.StartsWith("DLC", StringComparison.OrdinalIgnoreCase)
            && text.Skip(3).Take(underscoreIndex - 3).All(char.IsDigit))
        {
            text = text[(underscoreIndex + 1)..];
        }

        if (text.StartsWith("TBS_", StringComparison.OrdinalIgnoreCase))
        {
            text = text["TBS_".Length..];
        }

        var suffixes = new[]
        {
            "_Free",
            "_HardSell",
            "_Intro",
            "_Parallel",
            "_Current",
            "_OnlyHead",
            "_WithHead",
            "_Ghost",
            "_Joy",
            "_Angry",
            "_Sad",
            "_Happy",
        };
        foreach (var suffix in suffixes)
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^suffix.Length];
                break;
            }
        }

        return text.Length == 0 ? null : text;
    }

    private static List<string> ReadRuntimeTagNames(
        object? value,
        IReadOnlyDictionary<int, string> tagNames,
        bool includeFoodTags)
    {
        var result = new List<string>();

        void AddTagName(string? tagName)
        {
            var normalized = NormalizeRuntimeTagName(tagName, includeFoodTags);
            if (string.IsNullOrWhiteSpace(normalized)) return;
            result.Add(normalized);
        }

        foreach (var item in EnumerateObjects(value))
        {
            var id = ToNullableInt(item)
                ?? ToNullableInt(GetMemberValue(item, "tagId"))
                ?? ToNullableInt(GetMemberValue(item, "TagId"))
                ?? ToNullableInt(GetMemberValue(item, "ID"))
                ?? ToNullableInt(GetMemberValue(item, "Id"));
            if (id.HasValue && tagNames.TryGetValue(id.Value, out var mapped))
            {
                AddTagName(mapped);
            }
            else if (item != null)
            {
                AddTagName(CleanText(item));
            }
        }

        if (result.Count == 0)
        {
            var id = ToNullableInt(value);
            if (id.HasValue && tagNames.TryGetValue(id.Value, out var mapped))
            {
                AddTagName(mapped);
            }
        }

        return result
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string? NormalizeRuntimeTagName(string? value, bool includeFoodTags)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length == 0) return null;
        if (text.StartsWith("#", StringComparison.Ordinal)) return null;
        if (string.Equals(text, "Null", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.StartsWith("厨具", StringComparison.Ordinal)) return null;
        if (string.Equals(text, "黑暗物质", StringComparison.Ordinal)) return null;

        var normalized = FoodTags.NormalizeName(text) ?? text;
        if (includeFoodTags || !FoodTags.All.Contains(normalized)) return normalized;
        return normalized;
    }

    private static string BuildEntryKey(RuntimeMappedGuestEntry entry)
    {
        if (entry.RuntimeId.HasValue) return $"id:{entry.RuntimeId.Value}";
        if (!string.IsNullOrWhiteSpace(entry.RuntimeStringId)) return $"str:{entry.RuntimeStringId}";
        return $"type:{entry.RuntimeTypeName}:{entry.SourceDisplayName}";
    }

    private static int AliasSourcePriority(string aliasSource)
    {
        return aliasSource switch
        {
            "mapped-local-id" => 0,
            "runtime-local-id" => 1,
            "mapped-name" => 2,
            "runtime-name" => 3,
            "mapped-manual-alias" => 4,
            "runtime-manual-alias" => 5,
            "mapped-variant-alias" => 6,
            "runtime-variant-alias" => 7,
            "runtime-synthetic" => 8,
            "mapped-unresolved" => 9,
            "runtime-unresolved" => 10,
            _ => 10,
        };
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

    private static object? InvokeStaticMethod(Type? type, string name, params object?[] args)
    {
        if (type == null) return null;
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

    private static IReadOnlyDictionary<int, string> ReadTagDictionary(
        Type? languageType,
        string dictionaryMethod,
        string? idMethod,
        string refMethod)
    {
        var result = new Dictionary<int, string>();
        if (languageType == null) return result;

        foreach (var pair in EnumerateKeyValuePairs(InvokeStaticMethod(languageType, dictionaryMethod)))
        {
            var id = ToNullableInt(pair.Key);
            if (!id.HasValue) continue;
            result[id.Value] = CleanText(pair.Value);
        }

        if (result.Count > 0 || string.IsNullOrWhiteSpace(idMethod)) return result;

        foreach (var id in EnumerateObjects(InvokeStaticMethod(languageType, idMethod))
                     .Select(ToNullableInt)
                     .Where(id => id.HasValue)
                     .Select(id => id!.Value))
        {
            result[id] = CleanText(InvokeStaticMethod(languageType, refMethod, id));
        }

        return result;
    }

    private static IReadOnlyDictionary<int, string> ReadStringDictionary(Type? languageType, string dictionaryMethod)
    {
        var result = new Dictionary<int, string>();
        if (languageType == null) return result;

        foreach (var pair in EnumerateKeyValuePairs(InvokeStaticMethod(languageType, dictionaryMethod)))
        {
            var id = ToNullableInt(pair.Key);
            if (!id.HasValue) continue;

            var text = CleanText(pair.Value);
            if (!string.IsNullOrWhiteSpace(text)) result[id.Value] = text;
        }

        return result;
    }

    private static string ResolveLanguageName(Type? languageType, string methodName, int? id, string? fallback)
    {
        if (languageType != null && id.HasValue)
        {
            var value = InvokeStaticMethod(languageType, methodName, id.Value);
            var text = CleanText(value);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return string.IsNullOrWhiteSpace(fallback) ? "" : fallback.Trim();
    }

    private static string ResolveSpecialGuestName(
        Type? languageType,
        IReadOnlyDictionary<int, string> specialGuestNames,
        int? id,
        string? fallback)
    {
        if (id.HasValue
            && specialGuestNames.TryGetValue(id.Value, out var dictionaryName)
            && !string.IsNullOrWhiteSpace(dictionaryName))
        {
            return dictionaryName.Trim();
        }

        return ResolveLanguageName(languageType, "GetSpecialGuestLang", id, fallback);
    }

    private static string CleanText(object? value)
    {
        if (value == null) return "";
        if (value is string text) return text.Trim();

        foreach (var memberName in new[]
                 {
                     "Name",
                     "DisplayName",
                     "Title",
                     "Label",
                     "Text",
                     "name",
                     "title",
                     "text",
                 })
        {
            var memberValue = GetMemberValue(value, memberName);
            if (memberValue == null || ReferenceEquals(memberValue, value)) continue;
            var memberText = memberValue.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(memberText)) return memberText;
        }

        try
        {
            var objectText = value.ToString()?.Trim() ?? "";
            return objectText.StartsWith(value.GetType().FullName ?? "", StringComparison.Ordinal)
                ? ""
                : objectText;
        }
        catch
        {
            return "";
        }
    }

    private static IEnumerable<(object? Key, object? Value)> EnumerateKeyValuePairs(object? value)
    {
        if (value == null) yield break;

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                yield return (entry.Key, entry.Value);
            }

            yield break;
        }

        foreach (var item in EnumerateObjects(value))
        {
            var key = GetMemberValue(item, "Key")
                ?? GetMemberValue(item, "key")
                ?? GetMemberValue(item, "Item1");
            var itemValue = GetMemberValue(item, "Value")
                ?? GetMemberValue(item, "value")
                ?? GetMemberValue(item, "Item2");
            if (key != null || itemValue != null) yield return (key, itemValue);
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

    private static bool ToBool(object? value)
    {
        if (value == null) return false;
        if (value is bool boolValue) return boolValue;
        return bool.TryParse(value.ToString(), out var parsed) && parsed;
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
        RuntimeRareCustomers = entries
            .Select(entry => entry.RuntimeCustomer)
            .Where(customer => customer != null)
            .Cast<RuntimeRareCustomer>()
            .GroupBy(customer => customer.Id)
            .Select(group => group.First())
            .OrderBy(customer => customer.Id)
            .ToList();
    }

    public DateTime CapturedAtUtc { get; }
    public IReadOnlyList<RuntimeMappedGuestEntry> Entries { get; }
    public string Status { get; }
    public IReadOnlyDictionary<int, RuntimeMappedGuestEntry> ByRuntimeId { get; }
    public IReadOnlyDictionary<string, RuntimeMappedGuestEntry> ByRuntimeStringId { get; }
    public IReadOnlyList<RuntimeRareCustomer> RuntimeRareCustomers { get; }
    public int LocalResolvedCount => Entries.Count(entry => entry.LocalRareCustomerId.HasValue);
    public int RuntimeSyntheticCount => RuntimeRareCustomers.Count;
    public int ResolvedCount => LocalResolvedCount + RuntimeSyntheticCount;

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
    public RuntimeRareCustomer? RuntimeCustomer { get; init; }
    public string OverrideDestination { get; init; } = "";
    public string AliasSource { get; init; } = "";
    public string RuntimeTypeName { get; init; } = "";
}

internal sealed record ResolvedRuntimeIdentity(RareCustomerIdentity? Identity, string Source);
