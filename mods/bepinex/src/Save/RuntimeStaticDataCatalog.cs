using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal sealed class RuntimeStaticDataCatalog
{
    private const string DataBaseCoreTypeName = "GameData.Core.Collections.DataBaseCore";
    private const string DataBaseLanguageTypeName = "GameData.CoreLanguage.Collections.DataBaseLanguage";
    private const string DataBaseCharacterTypeName = "GameData.Core.Collections.CharacterUtility.DataBaseCharacter";
    private const int MaxCatalogItems = 4096;

    private static readonly HashSet<string> NonOrderableRareFoodTags = new(StringComparer.Ordinal)
    {
        "流行喜爱",
        "流行厌恶",
    };

    private static readonly object SyncRoot = new();
    private static RuntimeStaticDataSnapshot _snapshot = RuntimeStaticDataSnapshot.Empty("not loaded");
    private static RuntimeStaticMethodSet? _cachedMethods;
    private static bool _loaded;

    public RuntimeStaticDataSnapshot Snapshot()
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return _snapshot;
        }
    }

    public static void ResetSnapshot()
    {
        lock (SyncRoot)
        {
            _loaded = false;
            _snapshot = RuntimeStaticDataSnapshot.Empty("not loaded");
        }
    }

    private static void EnsureLoaded()
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

    private static RuntimeStaticDataSnapshot ReadSnapshot()
    {
        var phase = "resolve-types";
        try
        {
            var coreType = RequireType(DataBaseCoreTypeName);
            var languageType = RequireType(DataBaseLanguageTypeName);
            var characterType = RequireType(DataBaseCharacterTypeName);
            phase = "resolve-methods";
            var methods = ResolveRequiredMethods(coreType, languageType, characterType);

            phase = "read-language-dictionaries";
            var foodTags = ReadRequiredIntStringDictionary(methods.GetAllFoodTags, allowNegativeIds: true);
            var beverageTags = ReadRequiredIntStringDictionary(methods.GetAllBeverageTags, allowNegativeIds: true);
            var specialGuestNames = ReadRequiredIntStringDictionary(
                methods.GetAllSpecialGuestsNames,
                allowNegativeIds: false);

            phase = "read-core-mapping-ids";
            var ingredientIds = ReadCoreMappingIds(
                coreType,
                "IngredientsMapping",
                RuntimeCoreMappingIdDomain.NonNegativeContent);
            var beverageIds = ReadCoreMappingIds(
                coreType,
                "BeveragesMapping",
                RuntimeCoreMappingIdDomain.NonNegativeContent);
            var foodIds = ReadCoreMappingIds(
                coreType,
                "FoodsMapping",
                RuntimeCoreMappingIdDomain.NonNegativeContent);
            var recipeIds = ReadCoreMappingIds(
                coreType,
                "RecipesMapping",
                RuntimeCoreMappingIdDomain.NonNegativeContent);
            var izakayaIds = ReadCoreMappingIds(
                coreType,
                "IzakayasMapping",
                RuntimeCoreMappingIdDomain.Signed);

            phase = "read-izakaya-places";
            var places = ReadIzakayaPlaces(methods.RefIzakaya, izakayaIds);
            phase = "read-ingredients";
            var ingredients = ReadIngredients(methods.RefIngredient, methods.GetIngredientLang, ingredientIds, foodTags);
            phase = "read-beverages";
            var beverages = ReadBeverages(methods.RefBeverage, methods.GetBeverageLang, beverageIds, beverageTags);
            phase = "read-foods";
            var foods = ReadFoods(methods.RefFood, methods.GetFoodLang, foodIds, foodTags);
            phase = "read-recipes";
            var recipes = ReadRecipes(methods.RefRecipe, recipeIds, foods, ingredients);
            phase = "read-normal-customers";
            var normalCustomers = ReadNormalCustomers(
                methods.GetAllNormalGuests,
                methods.GetNormalGuestLang,
                foodTags,
                beverageTags,
                places.NormalPlacesByGuestId);
            phase = "read-rare-customers";
            var rareCustomers = ReadRareCustomers(
                methods.GetAllSpecialGuests,
                specialGuestNames,
                foodTags,
                beverageTags,
                places.RarePlacesByGuestId);

            phase = "validate-catalog";
            var catalog = new RuntimeDataCatalog
            {
                IsComplete = ingredients.Count > 0
                    && beverages.Count > 0
                    && recipes.Count > 0
                    && normalCustomers.Count > 0
                    && rareCustomers.Count > 0
                    && foodTags.Count > 0
                    && beverageTags.Count > 0,
                Source = "game-runtime",
                Status = string.Join(
                    ",",
                    $"ingredients:{ingredients.Count}",
                    $"beverages:{beverages.Count}",
                    $"recipes:{recipes.Count}",
                    $"normal:{normalCustomers.Count}",
                    $"rare:{rareCustomers.Count}",
                    $"izakayas:{places.IzakayaCount}",
                    "tagRules:0"),
                Ingredients = ingredients,
                Beverages = beverages,
                Recipes = recipes,
                NormalCustomers = normalCustomers,
                RareCustomers = rareCustomers,
                FoodTagIdMap = ToStringKeyDictionary(foodTags),
                BeverageTagIdMap = ToStringKeyDictionary(beverageTags),
                TagPriorityRules = new List<TagPriorityRule>(),
            };

            if (!catalog.IsComplete)
            {
                throw new InvalidOperationException($"Runtime data catalog is incomplete: {catalog.Status}.");
            }

            var status = $"runtimeData={catalog.Status}; source=five-core-mappings";
            return new RuntimeStaticDataSnapshot
            {
                CapturedAtUtc = DateTime.UtcNow,
                Status = status,
                TagLines = BuildTagLines(foodTags, beverageTags),
                CoreLines = BuildCoreLines(ingredients, beverages, foods.Values, recipes),
                GuestLines = BuildGuestLines(normalCustomers, rareCustomers),
                IzakayaLines = places.DiagnosticLines,
                DataCatalog = catalog,
                IsComplete = true,
            };
        }
        catch (Exception ex)
        {
            var detail = $"{phase}: {ex.Message}";
            var message = $"runtime data unavailable: {detail}";
            return new RuntimeStaticDataSnapshot
            {
                CapturedAtUtc = DateTime.UtcNow,
                Status = message,
                ErrorLines = new[] { detail },
                DataCatalog = RuntimeDataCatalog.Empty(message),
                IsComplete = false,
            };
        }
    }

    private static List<Ingredient> ReadIngredients(
        MethodInfo refIngredient,
        MethodInfo getIngredientLang,
        IReadOnlyList<int> ids,
        IReadOnlyDictionary<int, string> foodTags)
    {
        var result = new List<Ingredient>(ids.Count);
        foreach (var id in ids)
        {
            var runtime = InvokeRequiredStatic(refIngredient, id);
            result.Add(new Ingredient
            {
                Id = id,
                Name = ReadRequiredLanguageName(getIngredientLang, id),
                Tags = ReadTagNames(ReadRequiredIntArrayMember(runtime, "RawTags"), foodTags),
                Price = ReadRequiredIntMember(runtime, "baseValue"),
            });
        }

        return result;
    }

    private static List<Beverage> ReadBeverages(
        MethodInfo refBeverage,
        MethodInfo getBeverageLang,
        IReadOnlyList<int> ids,
        IReadOnlyDictionary<int, string> beverageTags)
    {
        var result = new List<Beverage>(ids.Count);
        foreach (var id in ids)
        {
            var runtime = InvokeRequiredStatic(refBeverage, id);
            result.Add(new Beverage
            {
                Id = id,
                Name = ReadRequiredLanguageName(getBeverageLang, id),
                Tags = ReadTagNames(ReadRequiredIntArrayMember(runtime, "RawTags"), beverageTags),
                Level = ReadRequiredIntMember(runtime, "level"),
                Price = ReadRequiredIntMember(runtime, "baseValue"),
            });
        }

        return result;
    }

    private static IReadOnlyDictionary<int, RuntimeFoodData> ReadFoods(
        MethodInfo refFood,
        MethodInfo getFoodLang,
        IReadOnlyList<int> ids,
        IReadOnlyDictionary<int, string> foodTags)
    {
        var result = new Dictionary<int, RuntimeFoodData>();
        foreach (var id in ids)
        {
            var runtime = InvokeRequiredStatic(refFood, id);
            result.Add(id, new RuntimeFoodData(
                id,
                ReadRequiredLanguageName(getFoodLang, id),
                ReadTagNames(ReadRequiredIntArrayMember(runtime, "RawTags"), foodTags),
                ReadTagNames(ReadRequiredIntArrayMember(runtime, "banTags"), foodTags),
                ReadRequiredIntMember(runtime, "level"),
                ReadRequiredIntMember(runtime, "baseValue")));
        }

        return result;
    }

    private static List<Recipe> ReadRecipes(
        MethodInfo refRecipe,
        IReadOnlyList<int> ids,
        IReadOnlyDictionary<int, RuntimeFoodData> foods,
        IReadOnlyList<Ingredient> ingredients)
    {
        var ingredientNames = ingredients.ToDictionary(ingredient => ingredient.Id, ingredient => ingredient.Name);
        var result = new List<Recipe>(ids.Count);
        foreach (var recipeId in ids)
        {
            var runtime = InvokeRequiredStatic(refRecipe, recipeId);
            var foodId = ReadRequiredIntMember(runtime, "foodID");
            if (!foods.TryGetValue(foodId, out var food))
            {
                throw new InvalidOperationException(
                    $"RecipesMapping[{recipeId}] references unknown food ID {foodId}.");
            }

            var ingredientIds = ReadRequiredIntArrayMember(runtime, "ingredients");
            var recipeIngredients = new List<string>(ingredientIds.Count);
            foreach (var ingredientId in ingredientIds)
            {
                if (!ingredientNames.TryGetValue(ingredientId, out var ingredientName))
                {
                    throw new InvalidOperationException(
                        $"Recipe {recipeId} references unknown ingredient ID {ingredientId}.");
                }

                recipeIngredients.Add(ingredientName);
            }

            result.Add(new Recipe
            {
                Id = foodId,
                RecipeId = recipeId,
                Name = food.Name,
                Ingredients = recipeIngredients,
                PositiveTags = food.PositiveTags,
                NegativeTags = food.NegativeTags,
                Cooker = NormalizeCooker(ReadRequiredMember(runtime, "cookerType").ToString() ?? ""),
                Level = food.Level,
                Price = food.Price,
            });
        }

        return result;
    }

    private static List<NormalCustomer> ReadNormalCustomers(
        MethodInfo getAllNormalGuests,
        MethodInfo getNormalGuestLang,
        IReadOnlyDictionary<int, string> foodTags,
        IReadOnlyDictionary<int, string> beverageTags,
        IReadOnlyDictionary<int, IReadOnlyList<string>> placesById)
    {
        var guests = ReadRequiredReferenceArray(InvokeRequiredStatic(getAllNormalGuests));
        var result = new List<NormalCustomer>();
        foreach (var guest in guests)
        {
            if (guest == null) throw new InvalidOperationException("GetAllNormalGuests returned a null entry.");
            var id = ReadRequiredIntMember(guest, "id");
            var hidden = ReadRequiredBoolMember(guest, "doNotShowInNotebook");
            if (hidden || !placesById.TryGetValue(id, out var places) || places.Count == 0) continue;

            var positiveTags = ReadTagNames(ReadRequiredIntArrayMember(guest, "likeFoodTag"), foodTags);
            var customerBeverageTags = ReadTagNames(
                ReadRequiredIntArrayMember(guest, "likeBevTag"),
                beverageTags);
            if (positiveTags.Count == 0 && customerBeverageTags.Count == 0) continue;

            result.Add(new NormalCustomer
            {
                Id = id,
                Name = ReadRequiredLanguageName(getNormalGuestLang, id),
                Places = places.ToList(),
                PositiveTags = positiveTags,
                BeverageTags = customerBeverageTags,
            });
        }

        return result.OrderBy(customer => customer.Id).ToList();
    }

    private static List<RareCustomer> ReadRareCustomers(
        MethodInfo getAllSpecialGuests,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyDictionary<int, string> foodTags,
        IReadOnlyDictionary<int, string> beverageTags,
        IReadOnlyDictionary<int, IReadOnlyList<string>> placesById)
    {
        var guests = ReadRequiredReferenceArray(InvokeRequiredStatic(getAllSpecialGuests));
        var result = new List<RareCustomer>();
        foreach (var guest in guests)
        {
            if (guest == null) throw new InvalidOperationException("GetAllSpecialGuests returned a null entry.");
            var id = ReadRequiredIntMember(guest, "id");
            var spawnType = ReadRequiredMember(guest, "spawnType").ToString() ?? "";
            var places = string.Equals(spawnType, "EveryWhere", StringComparison.OrdinalIgnoreCase)
                ? PlaceNames.All.ToList()
                : placesById.TryGetValue(id, out var mappedPlaces)
                    ? mappedPlaces.ToList()
                    : new List<string>();

            var positiveTags = ReadWeightedTagNames(
                ReadRequiredReferenceArrayMember(guest, "likeFoodTag"),
                foodTags);
            var negativeTags = ReadTagNames(
                ReadRequiredIntArrayMember(guest, "hateFoodTag"),
                foodTags);
            var customerBeverageTags = ReadWeightedTagNames(
                ReadRequiredReferenceArrayMember(guest, "likeBevTag"),
                beverageTags);
            if (!positiveTags.Any(IsOrderableRareFoodTag) || customerBeverageTags.Count == 0) continue;
            if (!names.TryGetValue(id, out var name) || !IsUsableDisplayName(name))
            {
                throw new InvalidOperationException($"Special guest {id} has no valid localized name.");
            }

            result.Add(new RareCustomer
            {
                Id = id,
                Name = name.Trim(),
                Places = places,
                PositiveTags = positiveTags,
                NegativeTags = negativeTags,
                BeverageTags = customerBeverageTags,
            });
        }

        return result.OrderBy(customer => customer.Id).ToList();
    }

    private static RuntimeIzakayaPlaces ReadIzakayaPlaces(
        MethodInfo refIzakaya,
        IReadOnlyList<int> ids)
    {
        var normal = new Dictionary<int, HashSet<string>>();
        var rare = new Dictionary<int, HashSet<string>>();
        var lines = new List<string>
        {
            "[Izakayas]",
            $"count={ids.Count}",
        };

        foreach (var id in ids)
        {
            var izakaya = InvokeRequiredStatic(refIzakaya, id);
            var place = ResolveIzakayaPlaceName(izakaya);
            if (string.IsNullOrWhiteSpace(place))
            {
                lines.Add($"  - id={id}; place=unsupported; pools=skipped");
                continue;
            }

            IReadOnlyList<int> normalIds;
            IReadOnlyList<int> rareIds;
            try
            {
                normalIds = ReadNormalGuestPoolIds(izakaya);
                rareIds = ReadSpecialGuestPoolIds(izakaya);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"IzakayasMapping[{id}] place '{place}' has unreadable guest pools: {ex.Message}",
                    ex);
            }

            lines.Add(
                $"  - id={id}; place={place}; normal={FormatIds(normalIds)}; rare={FormatIds(rareIds)}");

            AddPlaces(normal, normalIds, place);
            AddPlaces(rare, rareIds, place);
        }

        return new RuntimeIzakayaPlaces(
            ids.Count,
            FreezePlaces(normal),
            FreezePlaces(rare),
            lines);
    }

    private static IReadOnlyList<int> ReadNormalGuestPoolIds(object izakaya)
    {
        var result = new HashSet<int>();
        foreach (var group in ReadRequiredReferenceArrayMember(izakaya, "normalGuestPool"))
        {
            if (group == null) throw new InvalidOperationException("NormalGuestPool contains a null group.");
            foreach (var id in ReadRequiredIntArrayMember(group, "data")) result.Add(id);
        }

        return result.OrderBy(id => id).ToList();
    }

    private static IReadOnlyList<int> ReadSpecialGuestPoolIds(object izakaya)
    {
        var result = new HashSet<int>();
        foreach (var group in ReadRequiredReferenceArrayMember(izakaya, "specialGuestPool"))
        {
            if (group == null) throw new InvalidOperationException("SpecialGuestPool contains a null group.");
            result.Add(ReadRequiredIntMember(group, "groupId"));
        }

        return result.OrderBy(id => id).ToList();
    }

    private static string ResolveIzakayaPlaceName(object izakaya)
    {
        var mapLabel = ReadExactStringProperty(izakaya, "DaySceneMapLabel");
        if (string.IsNullOrWhiteSpace(mapLabel)) return "";

        var displayName = ReadExactStringProperty(izakaya, "DaySceneMapName");
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException(
                $"{izakaya.GetType().FullName}.DaySceneMapName returned no localized name for '{mapLabel}'.");
        }

        return PlaceNames.All.FirstOrDefault(name =>
            string.Equals(name, displayName.Trim(), StringComparison.Ordinal)
            || displayName.Contains(name, StringComparison.Ordinal)) ?? "";
    }

    private static string? ReadExactStringProperty(object instance, string propertyName)
    {
        var type = instance.GetType();
        var property = type.GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{type.FullName}.{propertyName} is unavailable.");
        if (property.GetIndexParameters().Length != 0 || property.PropertyType != typeof(string))
        {
            throw new InvalidOperationException(
                $"{type.FullName}.{propertyName} does not have the required String property shape.");
        }

        object? value;
        try
        {
            value = property.GetValue(instance);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw new InvalidOperationException(
                $"{type.FullName}.{propertyName} threw: {ex.InnerException.Message}",
                ex.InnerException);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{type.FullName}.{propertyName} could not be read: {ex.Message}",
                ex);
        }

        return value switch
        {
            null => null,
            string result => result,
            _ => throw new InvalidOperationException(
                $"{type.FullName}.{propertyName} returned {value.GetType().FullName} instead of String."),
        };
    }

    private static IReadOnlyList<int> ReadCoreMappingIds(
        Type coreType,
        string memberName,
        RuntimeCoreMappingIdDomain idDomain)
    {
        var source = RuntimeReflectionUtility.GetStaticMemberValue(coreType, memberName)
            ?? throw new InvalidOperationException($"{DataBaseCoreTypeName}.{memberName} is unavailable.");
        if (!RuntimeConcreteCollectionReader.TryReadDictionary(source, out var entries, out var failure))
        {
            throw new InvalidOperationException(
                $"{DataBaseCoreTypeName}.{memberName} is unreadable: {failure}.");
        }

        return RuntimeCoreMappingProjection.ReadIds(entries, memberName, idDomain);
    }

    private static IReadOnlyDictionary<int, string> ReadRequiredIntStringDictionary(
        MethodInfo method,
        bool allowNegativeIds)
    {
        var source = InvokeRequiredStatic(method);
        var owner = method.DeclaringType?.FullName ?? "<unknown>";
        if (!RuntimeConcreteCollectionReader.TryReadDictionary(source, out var entries, out var failure))
        {
            throw new InvalidOperationException(
                $"{owner}.{method.Name} returned an unreadable dictionary: {failure}.");
        }

        if (entries.Count == 0 || entries.Count > MaxCatalogItems)
        {
            throw new InvalidOperationException(
                $"{owner}.{method.Name} returned invalid count {entries.Count}.");
        }

        var result = new Dictionary<int, string>();
        foreach (var entry in entries)
        {
            if (entry.Key is not int id
                || (!allowNegativeIds && id < 0)
                || entry.Value is not string text
                || string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException(
                    $"{owner}.{method.Name} contains an invalid Int32/String entry.");
            }

            if (!result.TryAdd(id, text.Trim()))
            {
                throw new InvalidOperationException(
                    $"{owner}.{method.Name} contains duplicate ID {id}.");
            }
        }

        return result;
    }

    private static string ReadRequiredLanguageName(MethodInfo method, int id)
    {
        var language = InvokeRequiredStatic(method, id);
        var name = RuntimeReflectionUtility.GetMemberValue(language, "Name") as string;
        if (!IsUsableDisplayName(name))
        {
            throw new InvalidOperationException(
                $"{method.DeclaringType?.FullName}.{method.Name}({id}) returned no valid Name.");
        }

        return name!.Trim();
    }

    private static IReadOnlyList<int> ReadRequiredIntArrayMember(object instance, string memberName)
    {
        var value = ReadRequiredMember(instance, memberName);
        if (!RuntimeConcreteCollectionReader.TryReadIntArray(value, out var items, out var failure))
        {
            throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{memberName} is not an exact int array: {failure}.");
        }

        if (items.Count > MaxCatalogItems)
        {
            throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{memberName} exceeded the {MaxCatalogItems}-item limit.");
        }

        return items;
    }

    private static IReadOnlyList<object?> ReadRequiredReferenceArrayMember(
        object instance,
        string memberName)
    {
        return ReadRequiredReferenceArray(ReadRequiredMember(instance, memberName));
    }

    private static IReadOnlyList<object?> ReadRequiredReferenceArray(object? source)
    {
        if (!RuntimeConcreteCollectionReader.TryReadReferenceArray(source, out var items, out var failure))
        {
            throw new InvalidOperationException($"Runtime reference array is unreadable: {failure}.");
        }

        if (items.Count > MaxCatalogItems)
        {
            throw new InvalidOperationException(
                $"Runtime reference array exceeded the {MaxCatalogItems}-item limit.");
        }

        return items;
    }

    private static List<string> ReadWeightedTagNames(
        IReadOnlyList<object?> weightedTags,
        IReadOnlyDictionary<int, string> tagNames)
    {
        var ids = new List<int>(weightedTags.Count);
        foreach (var weightedTag in weightedTags)
        {
            if (weightedTag == null) throw new InvalidOperationException("WeightedTag array contains null.");
            ids.Add(ReadRequiredIntMember(weightedTag, "tagId"));
        }

        return ReadTagNames(ids, tagNames);
    }

    private static List<string> ReadTagNames(
        IEnumerable<int> ids,
        IReadOnlyDictionary<int, string> tagNames)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!tagNames.TryGetValue(id, out var rawName))
            {
                throw new InvalidOperationException($"Runtime tag ID {id} is missing from the language map.");
            }

            var normalized = NormalizeTagName(rawName);
            if (normalized.Length > 0 && seen.Add(normalized)) result.Add(normalized);
        }

        return result;
    }

    private static object ReadRequiredMember(object instance, string memberName)
    {
        return RuntimeReflectionUtility.GetMemberValue(instance, memberName)
            ?? throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{memberName} is unavailable.");
    }

    private static int ReadRequiredIntMember(object instance, string memberName)
    {
        var value = ReadRequiredMember(instance, memberName);
        return value is int result
            ? result
            : throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{memberName} returned {value.GetType().FullName} instead of Int32.");
    }

    private static bool ReadRequiredBoolMember(object instance, string memberName)
    {
        var value = ReadRequiredMember(instance, memberName);
        return value is bool result
            ? result
            : throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{memberName} returned {value.GetType().FullName} instead of Boolean.");
    }

    private static Type RequireType(string fullName)
    {
        return RuntimeReflectionUtility.FindType(fullName)
            ?? throw new InvalidOperationException($"{fullName} is not loaded.");
    }

    private static RuntimeStaticMethodSet ResolveRequiredMethods(
        Type coreType,
        Type languageType,
        Type characterType)
    {
        lock (SyncRoot)
        {
            var cached = _cachedMethods;
            if (cached != null
                && cached.CoreType == coreType
                && cached.LanguageType == languageType
                && cached.CharacterType == characterType)
            {
                return cached;
            }

            _cachedMethods = new RuntimeStaticMethodSet(
                coreType,
                languageType,
                characterType,
                RequireExactStaticMethod(languageType, "GetAllFoodTags"),
                RequireExactStaticMethod(languageType, "GetAllBeverageTags"),
                RequireExactStaticMethod(languageType, "GetAllSpecialGuestsNames"),
                RequireExactStaticMethod(coreType, "RefIngredient", typeof(int)),
                RequireExactStaticMethod(coreType, "RefBeverage", typeof(int)),
                RequireExactStaticMethod(coreType, "RefFood", typeof(int)),
                RequireExactStaticMethod(coreType, "RefRecipe", typeof(int)),
                RequireExactStaticMethod(coreType, "RefIzakaya", typeof(int)),
                RequireExactStaticMethod(languageType, "GetIngredientLang", typeof(int)),
                RequireExactStaticMethod(languageType, "GetBeverageLang", typeof(int)),
                RequireExactStaticMethod(languageType, "GetFoodLang", typeof(int)),
                RequireExactStaticMethod(languageType, "GetNormalGuestLang", typeof(int)),
                RequireExactStaticMethod(characterType, "GetAllNormalGuests"),
                RequireExactStaticMethod(characterType, "GetAllSpecialGuests"));
            return _cachedMethods;
        }
    }

    private static MethodInfo RequireExactStaticMethod(
        Type type,
        string methodName,
        params Type[] parameterTypes)
    {
        var matches = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal)
                && !method.IsGenericMethod
                && method.ReturnType != typeof(void)
                && method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .SequenceEqual(parameterTypes))
            .Take(2)
            .ToList();
        if (matches.Count != 1)
        {
            throw new MissingMethodException(
                type.FullName,
                $"{methodName}({string.Join(", ", parameterTypes.Select(parameter => parameter.Name))})");
        }

        return matches[0];
    }

    private static object InvokeRequiredStatic(MethodInfo method, params object?[] args)
    {
        try
        {
            return method.Invoke(null, args)
                ?? throw new InvalidOperationException(
                    $"{method.DeclaringType?.FullName}.{method.Name} returned null.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static void AddPlaces(
        IDictionary<int, HashSet<string>> target,
        IEnumerable<int> ids,
        string place)
    {
        foreach (var id in ids)
        {
            if (!target.TryGetValue(id, out var places))
            {
                places = new HashSet<string>(StringComparer.Ordinal);
                target[id] = places;
            }

            places.Add(place);
        }
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<string>> FreezePlaces(
        IReadOnlyDictionary<int, HashSet<string>> source)
    {
        return source.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.OrderBy(place => place, StringComparer.Ordinal).ToList());
    }

    private static Dictionary<string, string> ToStringKeyDictionary(
        IReadOnlyDictionary<int, string> source)
    {
        return source.OrderBy(pair => pair.Key).ToDictionary(
            pair => pair.Key.ToString(CultureInfo.InvariantCulture),
            pair => NormalizeTagName(pair.Value),
            StringComparer.Ordinal);
    }

    private static List<string> BuildTagLines(
        IReadOnlyDictionary<int, string> foodTags,
        IReadOnlyDictionary<int, string> beverageTags)
    {
        var lines = new List<string> { "[FoodTags]", $"count={foodTags.Count}" };
        lines.AddRange(foodTags.OrderBy(pair => pair.Key).Select(pair => $"  - id={pair.Key}; name={pair.Value}"));
        lines.Add("");
        lines.Add("[BeverageTags]");
        lines.Add($"count={beverageTags.Count}");
        lines.AddRange(beverageTags.OrderBy(pair => pair.Key).Select(pair => $"  - id={pair.Key}; name={pair.Value}"));
        return lines;
    }

    private static List<string> BuildCoreLines(
        IReadOnlyList<Ingredient> ingredients,
        IReadOnlyList<Beverage> beverages,
        IEnumerable<RuntimeFoodData> foods,
        IReadOnlyList<Recipe> recipes)
    {
        var lines = new List<string>
        {
            "[Ingredients]",
            $"count={ingredients.Count}",
        };
        lines.AddRange(ingredients.Select(item =>
            $"  - id={item.Id}; name={item.Name}; value={item.Price}; tags=[{string.Join(",", item.Tags)}]"));
        lines.Add("");
        lines.Add("[Beverages]");
        lines.Add($"count={beverages.Count}");
        lines.AddRange(beverages.Select(item =>
            $"  - id={item.Id}; name={item.Name}; level={item.Level}; value={item.Price}; tags=[{string.Join(",", item.Tags)}]"));
        var foodList = foods.OrderBy(item => item.Id).ToList();
        lines.Add("");
        lines.Add("[Foods]");
        lines.Add($"count={foodList.Count}");
        lines.AddRange(foodList.Select(item =>
            $"  - id={item.Id}; name={item.Name}; level={item.Level}; value={item.Price}; rawTags=[{string.Join(",", item.PositiveTags)}]; banTags=[{string.Join(",", item.NegativeTags)}]"));
        lines.Add("");
        lines.Add("[Recipes]");
        lines.Add($"count={recipes.Count}");
        lines.AddRange(recipes.Select(item =>
            $"  - recipeId={item.RecipeId}; foodId={item.Id}; name={item.Name}; ingredients=[{string.Join(",", item.Ingredients)}]; cooker={item.Cooker}"));
        return lines;
    }

    private static List<string> BuildGuestLines(
        IReadOnlyList<NormalCustomer> normalCustomers,
        IReadOnlyList<RareCustomer> rareCustomers)
    {
        var lines = new List<string>
        {
            "[NormalGuests]",
            $"count={normalCustomers.Count}",
        };
        lines.AddRange(normalCustomers.Select(customer =>
            $"  - id={customer.Id}; name={customer.Name}; places=[{string.Join(",", customer.Places)}]; likeFood=[{string.Join(",", customer.PositiveTags)}]; likeBev=[{string.Join(",", customer.BeverageTags)}]"));
        lines.Add("");
        lines.Add("[SpecialGuests]");
        lines.Add($"count={rareCustomers.Count}");
        lines.AddRange(rareCustomers.Select(customer =>
            $"  - id={customer.Id}; name={customer.Name}; places=[{string.Join(",", customer.Places)}]; likeFood=[{string.Join(",", customer.PositiveTags)}]; hateFood=[{string.Join(",", customer.NegativeTags)}]; likeBev=[{string.Join(",", customer.BeverageTags)}]"));
        return lines;
    }

    private static string FormatIds(IEnumerable<int> ids)
    {
        return $"[{string.Join(",", ids)}]";
    }

    private static bool IsUsableDisplayName(string? value)
    {
        var text = value?.Trim() ?? "";
        return text.Length > 0
            && !text.Equals("missing", StringComparison.OrdinalIgnoreCase)
            && !text.Equals("null", StringComparison.OrdinalIgnoreCase)
            && !text.Contains('?')
            && !text.StartsWith("#", StringComparison.Ordinal);
    }

    private static bool IsOrderableRareFoodTag(string tag)
    {
        return !string.IsNullOrWhiteSpace(tag) && !NonOrderableRareFoodTags.Contains(tag.Trim());
    }

    private static string NormalizeTagName(string value)
    {
        var text = value.Trim();
        return FoodTags.NormalizeName(text) ?? text;
    }

    private static string NormalizeCooker(string value)
    {
        return value.Trim() switch
        {
            "Pot" => "煮锅",
            "Grill" => "烧烤架",
            "Fryer" => "油锅",
            "Steamer" => "蒸锅",
            "CuttingBoard" => "料理台",
            var other => other,
        };
    }

    private sealed record RuntimeStaticMethodSet(
        Type CoreType,
        Type LanguageType,
        Type CharacterType,
        MethodInfo GetAllFoodTags,
        MethodInfo GetAllBeverageTags,
        MethodInfo GetAllSpecialGuestsNames,
        MethodInfo RefIngredient,
        MethodInfo RefBeverage,
        MethodInfo RefFood,
        MethodInfo RefRecipe,
        MethodInfo RefIzakaya,
        MethodInfo GetIngredientLang,
        MethodInfo GetBeverageLang,
        MethodInfo GetFoodLang,
        MethodInfo GetNormalGuestLang,
        MethodInfo GetAllNormalGuests,
        MethodInfo GetAllSpecialGuests);
}

internal sealed class RuntimeStaticDataSnapshot
{
    public DateTime CapturedAtUtc { get; init; }
    public string Status { get; init; } = "";
    public IReadOnlyList<string> TagLines { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CoreLines { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GuestLines { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> IzakayaLines { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ErrorLines { get; init; } = Array.Empty<string>();
    public RuntimeDataCatalog DataCatalog { get; init; } = RuntimeDataCatalog.Empty("not loaded");
    public bool IsComplete { get; init; }

    public static RuntimeStaticDataSnapshot Empty(string status)
    {
        return new RuntimeStaticDataSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            Status = status,
            DataCatalog = RuntimeDataCatalog.Empty(status),
            IsComplete = false,
        };
    }
}

internal sealed record RuntimeFoodData(
    int Id,
    string Name,
    List<string> PositiveTags,
    List<string> NegativeTags,
    int Level,
    int Price);

internal sealed record RuntimeIzakayaPlaces(
    int IzakayaCount,
    IReadOnlyDictionary<int, IReadOnlyList<string>> NormalPlacesByGuestId,
    IReadOnlyDictionary<int, IReadOnlyList<string>> RarePlacesByGuestId,
    IReadOnlyList<string> DiagnosticLines);
