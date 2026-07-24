using System.Reflection;
using System.Runtime.ExceptionServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// Reads live player state through exact per-ID game getters.
/// </summary>
public sealed class RuntimeReflectionRecommendationStateProvider
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

    public static bool CanReadRuntimeState(out string reason)
    {
        reason = "";
        var storageType = RuntimeReflectionUtility.FindType(RuntimeStorageTypeName);
        if (storageType == null)
        {
            reason = "RunTimeStorage type is not loaded.";
            return false;
        }

        var playerDataType = RuntimeReflectionUtility.FindType(RuntimePlayerDataTypeName);
        if (playerDataType == null)
        {
            reason = "RunTimePlayerData type is not loaded.";
            return false;
        }

        if (FindExactStaticMethod(storageType, "HaveRecipe", typeof(bool), typeof(int)) == null
            || FindExactStaticMethod(storageType, "GetIngredientCountById", typeof(int), typeof(int)) == null
            || FindExactStaticMethod(storageType, "GetBeverageCountById", typeof(int), typeof(int)) == null)
        {
            reason = "RunTimeStorage exact inventory getters are not available.";
            return false;
        }

        if (FindExactStaticMethod(playerDataType, "GetLevel", typeof(int)) == null
            || FindExactIntArrayStaticMethod(playerDataType, "GetPopFoodTags") == null)
        {
            reason = "RunTimePlayerData exact live-data getters are not available.";
            return false;
        }

        return true;
    }

    public RecommendationState LoadState()
    {
        _performanceMs.Clear();
        EnsureCompleteRepository();

        var storageType = RequireType(RuntimeStorageTypeName);
        var haveRecipeMethod = RequireExactStaticMethod(
            storageType,
            "HaveRecipe",
            typeof(bool),
            typeof(int));
        var ingredientQuantityMethod = RequireExactStaticMethod(
            storageType,
            "GetIngredientCountById",
            typeof(int),
            typeof(int));
        var beverageQuantityMethod = RequireExactStaticMethod(
            storageType,
            "GetBeverageCountById",
            typeof(int),
            typeof(int));

        var recipeIds = Measure(
            "storage.recipes",
            () => RuntimeStorageStateProjection.ReadAvailableRecipeIds(
                _repository.Recipes.Select(recipe => recipe.RecipeId),
                recipeId => InvokeRequiredBoolean(haveRecipeMethod, recipeId)));
        var ingredients = Measure(
            "storage.ingredients",
            () => RuntimeStorageStateProjection.ReadIngredientQuantities(
                _repository.Ingredients.Select(ingredient => ingredient.Id),
                ingredientId => InvokeRequiredInt32(ingredientQuantityMethod, ingredientId)));
        var beverages = Measure(
            "storage.beverages",
            () => RuntimeStorageStateProjection.ReadBeverageQuantities(
                _repository.Beverages.Select(beverage => beverage.Id),
                beverageId => InvokeRequiredInt32(beverageQuantityMethod, beverageId)));

        var playerDataType = RequireType(RuntimePlayerDataTypeName);
        var getLevelMethod = RequireExactStaticMethod(playerDataType, "GetLevel", typeof(int));
        var getFoodTagsMethod = RequireExactIntArrayStaticMethod(playerDataType, "GetPopFoodTags");
        var famousShopEnabled = _includeDaySceneState
            && Measure("player.famousShop", () => ReadTrackedSwitch(FamousShopSwitchKey));
        var popularFoodTag = Measure(
            "player.popularFood",
            () => ResolveFoodTag(ReadPopularFoodTags(getFoodTagsMethod, "Like")));
        var popularHateFoodTag = Measure(
            "player.popularHateFood",
            () => ResolveFoodTag(ReadPopularFoodTags(getFoodTagsMethod, "Hate")));
        var playerLevel = Measure("player.level", () => InvokeRequiredInt32(getLevelMethod));

        if (recipeIds.Count == 0 || (ingredients.Count == 0 && beverages.Count == 0 && playerLevel <= 0))
        {
            throw new InvalidOperationException("Game runtime data is empty; game progress may not be loaded.");
        }

        var parsed = new ParsedSaveData
        {
            RecipeGameIds = recipeIds,
            Ingredients = ingredients,
            Beverages = beverages,
            PopularFoodTag = famousShopEnabled && popularFoodTag == "招牌" ? null : popularFoodTag,
            PopularHateFoodTag = popularHateFoodTag,
            FamousShopEnabled = famousShopEnabled,
        };

        var state = Measure("state.fromSave", () => RecommendationState.FromSave(_repository, parsed));
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

    private void EnsureCompleteRepository()
    {
        if (_repository.Recipes.Count == 0
            || _repository.Ingredients.Count == 0
            || _repository.Beverages.Count == 0
            || _repository.FoodTagIdMap.Count == 0
            || _repository.BeverageTagIdMap.Count == 0)
        {
            throw new InvalidOperationException("Runtime data catalog is incomplete.");
        }
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

    private static IReadOnlyList<int> ReadPopularFoodTags(MethodInfo method, string popTypeName)
    {
        var parameterType = method.GetParameters()[0].ParameterType;
        var popType = Enum.Parse(parameterType, popTypeName, ignoreCase: false);
        var value = InvokeMethod(method, popType);
        if (!RuntimeConcreteCollectionReader.TryReadIntArray(value, out var tags, out var failure))
        {
            throw new InvalidOperationException(
                $"{method.DeclaringType?.FullName}.{method.Name} returned an unreadable int array: {failure}.");
        }

        return tags;
    }

    private static bool ReadTrackedSwitch(string key)
    {
        var type = RequireType(RuntimeDaySceneTypeName);
        var method = RequireExactStaticMethod(
            type,
            "GetTrackedSwitch",
            typeof(bool),
            typeof(string),
            typeof(bool));
        return InvokeRequiredBoolean(method, key, false);
    }

    private string? ResolveFoodTag(IEnumerable<int> tagIds)
    {
        foreach (var tagId in tagIds)
        {
            if (!_repository.FoodTagIdMap.TryGetValue(tagId.ToString(), out var tag))
            {
                throw new InvalidOperationException(
                    $"Popular food tag ID {tagId} is missing from the runtime tag map.");
            }

            var normalized = FoodTags.NormalizeName(tag);
            if (normalized != null && FoodTags.All.Contains(normalized)) return normalized;
        }

        return null;
    }

    private static Type RequireType(string fullName)
    {
        return RuntimeReflectionUtility.FindType(fullName)
            ?? throw new InvalidOperationException($"{fullName} is not loaded.");
    }

    private static MethodInfo RequireExactStaticMethod(
        Type type,
        string name,
        Type returnType,
        params Type[] parameterTypes)
    {
        return FindExactStaticMethod(type, name, returnType, parameterTypes)
            ?? throw new MissingMethodException(
                type.FullName,
                $"{name}({string.Join(", ", parameterTypes.Select(parameter => parameter.Name))}): {returnType.Name}");
    }

    private static MethodInfo? FindExactStaticMethod(
        Type type,
        string name,
        Type returnType,
        params Type[] parameterTypes)
    {
        var matches = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal)
                && method.ReturnType == returnType
                && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameterTypes))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static MethodInfo RequireExactIntArrayStaticMethod(Type type, string name)
    {
        return FindExactIntArrayStaticMethod(type, name)
            ?? throw new MissingMethodException(type.FullName, $"{name}(PopType): int[]");
    }

    private static MethodInfo? FindExactIntArrayStaticMethod(Type type, string name)
    {
        var matches = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method =>
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal)
                    || !IsExactIntArrayType(method.ReturnType))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsEnum;
            })
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool IsExactIntArrayType(Type type)
    {
        return type == typeof(int[]) || type == typeof(Il2CppStructArray<int>);
    }

    private static int InvokeRequiredInt32(MethodInfo method, params object?[] args)
    {
        var value = InvokeMethod(method, args);
        return value is int result
            ? result
            : throw new InvalidOperationException(
                $"{method.DeclaringType?.FullName}.{method.Name} returned {DescribeType(value)} instead of System.Int32.");
    }

    private static bool InvokeRequiredBoolean(MethodInfo method, params object?[] args)
    {
        var value = InvokeMethod(method, args);
        return value is bool result
            ? result
            : throw new InvalidOperationException(
                $"{method.DeclaringType?.FullName}.{method.Name} returned {DescribeType(value)} instead of System.Boolean.");
    }

    private static object? InvokeMethod(MethodInfo method, params object?[] args)
    {
        try
        {
            return method.Invoke(null, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static string DescribeType(object? value)
    {
        return value?.GetType().FullName ?? "null";
    }
}
