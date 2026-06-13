using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

public sealed class RuntimeReflectionRecommendationStateProvider : IRecommendationStateProvider
{
    private const string RuntimeStorageTypeName = "GameData.RunTime.Common.RunTimeStorage";
    private const string RuntimePlayerDataTypeName = "GameData.RunTime.Common.RunTimePlayerData";
    private const string RuntimeDaySceneTypeName = "GameData.RunTime.DaySceneUtility.RunTimeDayScene";
    private const string FamousShopSwitchKey = "Aya_FamousIzakaya";

    private readonly DataRepository _repository;
    private readonly bool _includePlacedCookers;
    private readonly Dictionary<string, double> _performanceMs = new(StringComparer.Ordinal);

    public RuntimeReflectionRecommendationStateProvider(DataRepository repository, bool includePlacedCookers = true)
    {
        _repository = repository;
        _includePlacedCookers = includePlacedCookers;
    }

    public string Description => "Game runtime live data";
    public IReadOnlyDictionary<string, double> PerformanceMs => _performanceMs;

    public static bool CanReadRuntimeState(out string reason)
    {
        reason = "";

        var runtimeStorage = FindType(RuntimeStorageTypeName);
        if (runtimeStorage == null)
        {
            reason = "RunTimeStorage type is not loaded.";
            return false;
        }

        var runtimePlayerData = FindType(RuntimePlayerDataTypeName);
        if (runtimePlayerData == null)
        {
            reason = "RunTimePlayerData type is not loaded.";
            return false;
        }

        if (FindStaticMethod(runtimeStorage, "GetAllRecipeIndex") == null
            && FindStaticMethod(runtimeStorage, "GenerateSaveData") == null)
        {
            reason = "RunTimeStorage live-data methods are not available.";
            return false;
        }

        if (FindStaticMethod(runtimePlayerData, "GetLevel") == null
            && FindStaticMethod(runtimePlayerData, "GenerateSaveData") == null)
        {
            reason = "RunTimePlayerData live-data methods are not available.";
            return false;
        }

        return true;
    }

    public RecommendationState LoadState()
    {
        _performanceMs.Clear();
        var storagePartial = Measure("storage.saveData", () => TryInvokeStaticSafely(RuntimeStorageTypeName, "GenerateSaveData"));
        var playerPartial = Measure("player.saveData", () => TryInvokeStaticSafely(RuntimePlayerDataTypeName, "GenerateSaveData"));
        var dayScenePartial = Measure("dayScene.saveData", () => TryInvokeStaticSafely(RuntimeDaySceneTypeName, "GenerateSaveData"));

        var recipeGameIds = Measure("storage.recipes", () => ReadLiveRecipeIds(storagePartial));
        var ingredients = Measure("storage.ingredients", () => ReadLiveIngredients(storagePartial));
        var beverages = Measure("storage.beverages", () => ReadLiveBeverages(storagePartial));
        var trackedSwitch = Measure("dayScene.switches", () => ReadStringBoolDictionary(GetMemberValue(dayScenePartial, "trackedSwitch")));
        var famousShopEnabled = Measure("player.famousShop", () => ReadTrackedSwitch(FamousShopSwitchKey, trackedSwitch));
        var popularFoodTag = Measure("player.popularFood", () => ResolveFoodTag(ReadPopularFoodTags("Like", GetMemberValue(playerPartial, "popLikeFoodTags"))));
        var playerLevel = Measure("player.level", () => ReadPlayerLevel(playerPartial));

        if (recipeGameIds.Count == 0 && ingredients.Count == 0 && beverages.Count == 0 && playerLevel <= 0)
        {
            throw new InvalidOperationException("Game runtime data is empty; game progress may not be loaded.");
        }

        var parsed = new ParsedSaveData
        {
            RecipeGameIds = recipeGameIds,
            Ingredients = ingredients,
            Beverages = beverages,
            PlayerLevel = playerLevel,
            PopularFoodTag = famousShopEnabled && popularFoodTag == "招牌" ? null : popularFoodTag,
            PopularHateFoodTag = Measure("player.popularHateFood", () => ResolveFoodTag(ReadPopularFoodTags("Hate", GetMemberValue(playerPartial, "popHateFoodTags")))),
            FamousShopEnabled = famousShopEnabled,
            CollabStatus = Measure("player.collabStatus", () => ReadStringBoolDictionary(GetMemberValue(playerPartial, "collabStatus"))),
        };

        var state = Measure("state.fromSave", () => RecommendationState.FromSave(_repository, parsed));
        var rareCustomerIds = Measure(
            "rare.available",
            () => ExpandAvailableRareCustomerIds(RuntimeRareCustomerAvailabilityService.ReadAvailableRareCustomerIds()).ToList());
        foreach (var rareCustomerId in rareCustomerIds)
        {
            state.AvailableRareCustomerIds.Add(rareCustomerId);
        }

        if (_includePlacedCookers)
        {
            Measure("cookerSnapshot", () => RuntimeCookerSnapshotService.ApplyTo(state));
        }
        else
        {
            state.PlacedCookerStatus = "not in night business scene";
        }

        return state;
    }

    private T Measure<T>(string key, Func<T> action)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            _performanceMs[key] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);
        }
    }

    private void Measure(string key, Action action)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            _performanceMs[key] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);
        }
    }

    private HashSet<int> ExpandAvailableRareCustomerIds(HashSet<int> recordedIds)
    {
        var result = new HashSet<int>(recordedIds);
        if (recordedIds.Count == 0) return result;

        try
        {
            var mappedGuests = new RuntimeMappedGuestCatalog(_repository).Snapshot();
            foreach (var entry in mappedGuests.Entries)
            {
                if (!MatchesRecordedRareCustomer(entry, recordedIds)) continue;
                AddIfValid(result, entry.RuntimeId);
                AddIfValid(result, entry.SourceGuestId);
                AddIfValid(result, entry.LocalRareCustomerId);
                AddIfValid(result, entry.RuntimeCustomer?.Id);
            }
        }
        catch
        {
            // Mapping expansion is an optimization. The raw recorded ids are still usable.
        }

        return result;
    }

    private static bool MatchesRecordedRareCustomer(RuntimeMappedGuestEntry entry, HashSet<int> recordedIds)
    {
        return IsRecorded(entry.RuntimeId, recordedIds)
            || IsRecorded(entry.SourceGuestId, recordedIds)
            || IsRecorded(entry.LocalRareCustomerId, recordedIds)
            || IsRecorded(entry.RuntimeCustomer?.Id, recordedIds);
    }

    private static bool IsRecorded(int? id, HashSet<int> recordedIds)
    {
        return id.HasValue && recordedIds.Contains(id.Value);
    }

    private static void AddIfValid(HashSet<int> target, int? id)
    {
        if (id.HasValue && id.Value >= 0) target.Add(id.Value);
    }

    private static List<int> ReadLiveRecipeIds(object? storagePartial)
    {
        var recipeIds = ReadIntCollection(TryInvokeStaticSafely(RuntimeStorageTypeName, "GetAllRecipeIndex")).ToList();
        if (recipeIds.Count > 0) return recipeIds;

        return ReadIntCollection(GetMemberValue(storagePartial, "recipes")).ToList();
    }

    private static Dictionary<int, int> ReadLiveBeverages(object? storagePartial)
    {
        var beverages = ReadIntDictionary(TryInvokeStaticSafely(RuntimeStorageTypeName, "GetAllBeveragesId"));
        return beverages.Count > 0
            ? beverages
            : ReadIntDictionary(GetMemberValue(storagePartial, "beverages"));
    }

    private static Dictionary<int, int> ReadLiveIngredients(object? storagePartial)
    {
        var ingredients = ReadObjectIntPairDictionary(TryInvokeStaticSafely(RuntimeStorageTypeName, "GetAllIngredients"));
        return ingredients.Count > 0
            ? ingredients
            : ReadIntDictionary(GetMemberValue(storagePartial, "ingredients"));
    }

    private static int ReadPlayerLevel(object? playerPartial)
    {
        var level = ToInt(TryInvokeStaticSafely(RuntimePlayerDataTypeName, "GetLevel"));
        return level > 0 ? level : ReadIntMember(playerPartial, "level");
    }

    private static IEnumerable<int> ReadPopularFoodTags(string popTypeName, object? fallback)
    {
        var type = FindType(RuntimePlayerDataTypeName);
        var method = type == null ? null : FindStaticMethod(type, "GetPopFoodTags");
        if (method != null)
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType.IsEnum)
            {
                object? popType = null;
                try
                {
                    popType = Enum.Parse(parameters[0].ParameterType, popTypeName);
                }
                catch
                {
                    popType = popTypeName == "Like"
                        ? Enum.ToObject(parameters[0].ParameterType, 0)
                        : Enum.ToObject(parameters[0].ParameterType, 1);
                }

                try
                {
                    var result = InvokeMethod(method, null, new[] { popType });
                    var values = ReadIntCollection(result).ToList();
                    if (values.Count > 0) return values;
                }
                catch
                {
                    // Fall back to the generated runtime player-data snapshot.
                }
            }
        }

        return ReadIntCollection(fallback).ToList();
    }

    private static bool ReadTrackedSwitch(string key, IReadOnlyDictionary<string, bool> fallback)
    {
        var type = FindType(RuntimeDaySceneTypeName);
        var method = type == null ? null : FindStaticMethod(type, "GetTrackedSwitch");
        if (method != null)
        {
            try
            {
                var result = InvokeMethod(method, null, new object?[] { key, false });
                return ToBool(result);
            }
            catch
            {
                // Fall back to the generated runtime day-scene snapshot.
            }
        }

        return fallback.TryGetValue(key, out var enabled) && enabled;
    }

    private string? ResolveFoodTag(IEnumerable<int> tagIds)
    {
        foreach (var tagId in tagIds)
        {
            var key = tagId.ToString();
            if (!_repository.FoodTagIdMap.TryGetValue(key, out var tag)) continue;

            var normalized = FoodTags.NormalizeName(tag);
            if (normalized != null && FoodTags.All.Contains(normalized)) return normalized;
        }

        return null;
    }

    private static object? TryInvokeStatic(string typeName, string methodName)
    {
        var type = FindType(typeName);
        if (type == null) return null;

        var method = FindStaticMethod(type, methodName);
        return method == null ? null : InvokeMethod(method, null, Array.Empty<object?>());
    }

    private static object? TryInvokeStaticSafely(string typeName, string methodName)
    {
        try
        {
            return TryInvokeStatic(typeName, methodName);
        }
        catch
        {
            return null;
        }
    }

    private static object? InvokeMethod(MethodInfo method, object? instance, object?[] args)
    {
        try
        {
            return method.Invoke(instance, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static MethodInfo? FindStaticMethod(Type type, string name)
    {
        return type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    }

    private static Type? FindType(string fullName)
    {
        var direct = Type.GetType(fullName, false);
        if (direct != null) return direct;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = null;
            try
            {
                type = assembly.GetType(fullName, false);
            }
            catch
            {
                // Some IL2CPP interop assemblies can throw while resolving unrelated types.
            }

            if (type != null) return type;
        }

        return null;
    }

    private static object? GetMemberValue(object? instance, string name)
    {
        if (instance == null) return null;
        var type = instance.GetType();

        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property != null) return property.GetValue(instance);

        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null) return field.GetValue(instance);

        var pascalName = char.ToUpperInvariant(name[0]) + name[1..];
        property = type.GetProperty(pascalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property != null) return property.GetValue(instance);

        field = type.GetField(pascalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(instance);
    }

    private static int ReadIntMember(object? instance, string name)
    {
        return ToInt(GetMemberValue(instance, name));
    }

    private static IEnumerable<int> ReadIntCollection(object? value)
    {
        foreach (var item in EnumerateObjects(value))
        {
            yield return ToInt(item);
        }
    }

    private static Dictionary<int, int> ReadIntDictionary(object? value)
    {
        var result = new Dictionary<int, int>();
        if (value == null) return result;

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                result[ToInt(entry.Key)] = ToInt(entry.Value);
            }

            return result;
        }

        foreach (var item in EnumerateObjects(value))
        {
            var key = GetMemberValue(item, "Key") ?? GetMemberValue(item, "key");
            var itemValue = GetMemberValue(item, "Value") ?? GetMemberValue(item, "value");
            if (key == null || itemValue == null) continue;
            result[ToInt(key)] = ToInt(itemValue);
        }

        if (result.Count > 0) return result;

        var keys = GetMemberValue(value, "Keys");
        if (keys == null) return result;

        foreach (var key in EnumerateObjects(keys))
        {
            var itemValue = ReadIndexedValue(value, key);
            if (itemValue == null) continue;
            result[ToInt(key)] = ToInt(itemValue);
        }

        return result;
    }

    private static Dictionary<int, int> ReadObjectIntPairDictionary(object? value)
    {
        var result = new Dictionary<int, int>();
        if (value == null) return result;

        foreach (var item in EnumerateObjects(value))
        {
            var key = GetMemberValue(item, "Key") ?? GetMemberValue(item, "key");
            var itemValue = GetMemberValue(item, "Value") ?? GetMemberValue(item, "value");
            if (key == null || itemValue == null) continue;

            var id = ReadObjectId(key);
            if (id == null) continue;

            result[id.Value] = ToInt(itemValue);
        }

        return result;
    }

    private static int? ReadObjectId(object? value)
    {
        if (value == null) return null;

        var id = GetMemberValue(value, "Id") ?? GetMemberValue(value, "id");
        if (id == null) return null;

        return ToInt(id);
    }

    private static Dictionary<string, bool> ReadStringBoolDictionary(object? value)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (value == null) return result;

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString();
                if (!string.IsNullOrWhiteSpace(key)) result[key] = ToBool(entry.Value);
            }

            return result;
        }

        foreach (var item in EnumerateObjects(value))
        {
            var key = (GetMemberValue(item, "Key") ?? GetMemberValue(item, "key"))?.ToString();
            if (string.IsNullOrWhiteSpace(key)) continue;

            var itemValue = GetMemberValue(item, "Value") ?? GetMemberValue(item, "value");
            if (itemValue == null) continue;
            result[key] = ToBool(itemValue);
        }

        if (result.Count > 0) return result;

        var keys = GetMemberValue(value, "Keys");
        if (keys == null) return result;

        foreach (var keyObject in EnumerateObjects(keys))
        {
            var key = keyObject?.ToString();
            if (string.IsNullOrWhiteSpace(key)) continue;

            var itemValue = ReadIndexedValue(value, keyObject);
            if (itemValue == null) continue;
            result[key] = ToBool(itemValue);
        }

        return result;
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

        var reflected = false;
        foreach (var item in EnumerateObjectsByReflection(value))
        {
            reflected = true;
            yield return item;
        }

        if (reflected) yield break;

        var count = ReadCount(value);
        if (count <= 0) yield break;

        var indexer = FindIntIndexer(value.GetType());
        var getItem = FindIntGetItem(value.GetType());
        if (indexer == null && getItem == null) yield break;

        for (var i = 0; i < count; i++)
        {
            yield return indexer != null
                ? indexer.GetValue(value, new object[] { i })
                : getItem?.Invoke(value, new object[] { i });
        }
    }

    private static PropertyInfo? FindIntIndexer(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (property.Name != "Item") continue;

            var parameters = property.GetIndexParameters();
            if (parameters is { Length: 1 } && parameters[0].ParameterType == typeof(int)) return property;
        }

        return null;
    }

    private static MethodInfo? FindIntGetItem(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (method.Name != "get_Item") continue;

            var parameters = method.GetParameters();
            if (parameters is { Length: 1 } && parameters[0].ParameterType == typeof(int)) return method;
        }

        return null;
    }

    private static object? ReadIndexedValue(object instance, object? key)
    {
        var type = instance.GetType();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (property.Name != "Item") continue;

            var parameters = property.GetIndexParameters();
            if (parameters is not { Length: 1 } || !CanUseIndexParameter(parameters[0].ParameterType, key)) continue;

            try
            {
                return property.GetValue(instance, new[] { key });
            }
            catch
            {
                // Try the next overload or get_Item method.
            }
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (method.Name != "get_Item") continue;

            var parameters = method.GetParameters();
            if (parameters is not { Length: 1 } || !CanUseIndexParameter(parameters[0].ParameterType, key)) continue;

            try
            {
                return method.Invoke(instance, new[] { key });
            }
            catch
            {
                // Try the next overload.
            }
        }

        return null;
    }

    private static bool CanUseIndexParameter(Type parameterType, object? key)
    {
        if (key == null) return !parameterType.IsValueType;

        var keyType = key.GetType();
        if (parameterType.IsAssignableFrom(keyType)) return true;

        return parameterType.IsPrimitive && key is IConvertible;
    }

    private static IEnumerable<object?> EnumerateObjectsByReflection(object value)
    {
        var getEnumerator = value.GetType().GetMethod("GetEnumerator", Type.EmptyTypes);
        if (getEnumerator == null) yield break;

        var enumerator = getEnumerator.Invoke(value, Array.Empty<object?>());
        if (enumerator == null) yield break;

        var moveNext = enumerator.GetType().GetMethod("MoveNext", Type.EmptyTypes);
        var current = enumerator.GetType().GetProperty("Current");
        if (moveNext == null || current == null) yield break;

        while (moveNext.Invoke(enumerator, Array.Empty<object?>()) is bool next && next)
        {
            yield return current.GetValue(enumerator);
        }
    }

    private static int ReadCount(object value)
    {
        var count = GetMemberValue(value, "Count") ?? GetMemberValue(value, "Length");
        return ToInt(count);
    }

    private static int ToInt(object? value)
    {
        if (value == null) return 0;
        if (value is int intValue) return intValue;
        if (value is long longValue) return (int)longValue;
        if (value is short shortValue) return shortValue;
        if (int.TryParse(value.ToString(), out var parsed)) return parsed;
        return 0;
    }

    private static bool ToBool(object? value)
    {
        if (value == null) return false;
        if (value is bool boolValue) return boolValue;
        if (bool.TryParse(value.ToString(), out var parsed)) return parsed;
        return false;
    }
}
