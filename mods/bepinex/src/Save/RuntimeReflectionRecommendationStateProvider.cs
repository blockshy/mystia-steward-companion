using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 通过反射读取游戏运行时数据，并转换为推荐引擎使用的 <see cref="RecommendationState"/>。
/// </summary>
/// <remarks>
/// 该 Provider 运行在游戏进程内，调用游戏公开的运行时静态方法读取热路径数据。
/// 所有反射访问都需要容忍字段缺失、DLC 差异和场景未就绪。
/// </remarks>
public sealed class RuntimeReflectionRecommendationStateProvider : IRecommendationStateProvider
{
    private const string RuntimeStorageTypeName = "GameData.RunTime.Common.RunTimeStorage";
    private const string RuntimePlayerDataTypeName = "GameData.RunTime.Common.RunTimePlayerData";
    private const string RuntimeDaySceneTypeName = "GameData.RunTime.DaySceneUtility.RunTimeDayScene";
    private const string FamousShopSwitchKey = "Aya_FamousIzakaya";

    private readonly DataRepository _repository;
    private readonly bool _includePlacedCookers;
    private readonly bool _includeDaySceneState;
    private readonly Dictionary<string, double> _performanceMs = new(StringComparer.Ordinal);

    public RuntimeReflectionRecommendationStateProvider(
        DataRepository repository,
        bool includePlacedCookers = true,
        bool includeDaySceneState = true)
    {
        _repository = repository;
        _includePlacedCookers = includePlacedCookers;
        _includeDaySceneState = includeDaySceneState;
    }

    public string Description => "Game runtime live data";
    public IReadOnlyDictionary<string, double> PerformanceMs => _performanceMs;

    /// <summary>
    /// 检查当前游戏域是否已经加载推荐所需的运行时类型和基础方法。
    /// </summary>
    /// <param name="reason">不可读取时给 UI 或日志展示的原因。</param>
    /// <returns>基础类型和入口方法均可用时返回 <c>true</c>。</returns>
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
            || FindStaticMethod(runtimeStorage, "GetAllBeveragesId") == null
            || FindStaticMethod(runtimeStorage, "GetAllIngredients") == null)
        {
            reason = "RunTimeStorage live-data methods are not available.";
            return false;
        }

        if (FindStaticMethod(runtimePlayerData, "GetLevel") == null
            || FindStaticMethod(runtimePlayerData, "GetPopFoodTags") == null)
        {
            reason = "RunTimePlayerData live-data methods are not available.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 读取当前存档的推荐状态快照。
    /// </summary>
    /// <returns>包含已解锁料理、库存、流行 Tag、稀客可用性和厨具快照的推荐状态。</returns>
    /// <exception cref="InvalidOperationException">游戏尚未载入存档或运行时返回空数据时抛出。</exception>
    public RecommendationState LoadState()
    {
        _performanceMs.Clear();
        var recipeGameIds = Measure("storage.recipes", ReadLiveRecipeIds);
        var ingredients = Measure("storage.ingredients", ReadLiveIngredients);
        var beverages = Measure("storage.beverages", ReadLiveBeverages);
        var famousShopEnabled = _includeDaySceneState
            && Measure("player.famousShop", () => ReadTrackedSwitch(FamousShopSwitchKey));
        var popularFoodTag = Measure("player.popularFood", () => ResolveFoodTag(ReadPopularFoodTags("Like")));
        var playerLevel = Measure("player.level", ReadPlayerLevel);

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
            PopularHateFoodTag = Measure("player.popularHateFood", () => ResolveFoodTag(ReadPopularFoodTags("Hate"))),
            FamousShopEnabled = famousShopEnabled,
            CollabStatus = Measure("player.collabStatus", ReadCollabStatus),
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

    /// <summary>
    /// 执行一段运行时读取并记录耗时。
    /// </summary>
    /// <remarks>
    /// 耗时会随本地 API 快照发布到伴随窗口，用于定位某个反射读取点导致的卡顿。
    /// </remarks>
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

    /// <summary>
    /// 将游戏运行时记录的稀客 ID 扩展为项目静态目录、来源 ID 和映射 ID 的并集。
    /// </summary>
    /// <remarks>
    /// 游戏内部同一个稀客可能同时存在 runtime id、source guest id 和本地目录 id。只保留原始 ID
    /// 会导致前端无法匹配静态稀客目录，因此这里在可用时读取映射表做兼容扩展。
    /// </remarks>
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
            // 映射扩展只是提高目录匹配率；失败时保留原始运行时 ID，避免因映射读取异常丢失全部稀客。
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

    private static List<int> ReadLiveRecipeIds()
    {
        return ReadIntCollection(InvokeStaticSafely(RuntimeStorageTypeName, "GetAllRecipeIndex")).ToList();
    }

    private static Dictionary<int, int> ReadLiveBeverages()
    {
        return ReadIntDictionary(InvokeStaticSafely(RuntimeStorageTypeName, "GetAllBeveragesId"));
    }

    private static Dictionary<int, int> ReadLiveIngredients()
    {
        return ReadObjectIntPairDictionary(InvokeStaticSafely(RuntimeStorageTypeName, "GetAllIngredients"));
    }

    private static int ReadPlayerLevel()
    {
        return ToInt(InvokeStaticSafely(RuntimePlayerDataTypeName, "GetLevel"));
    }

    private static IEnumerable<int> ReadPopularFoodTags(string popTypeName)
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
                    // Let the next refresh retry the live getter.
                }
            }
        }

        return Enumerable.Empty<int>();
    }

    private static bool ReadTrackedSwitch(string key)
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
                // Let the next refresh retry the live getter.
            }
        }

        return false;
    }

    private static Dictionary<string, bool> ReadCollabStatus()
    {
        var type = FindType(RuntimePlayerDataTypeName);
        return type == null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : ReadStringBoolDictionary(RuntimeReflectionUtility.GetStaticMemberValue(type, "CollabStatus"));
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

    private static object? InvokeStaticSafely(string typeName, string methodName)
    {
        try
        {
            var type = FindType(typeName);
            if (type == null) return null;

            var method = FindStaticMethod(type, methodName);
            return method == null ? null : InvokeMethod(method, null, Array.Empty<object?>());
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
