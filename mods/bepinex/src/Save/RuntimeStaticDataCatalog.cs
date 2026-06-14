using System.Collections;
using System.Globalization;
using System.Reflection;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal sealed class RuntimeStaticDataCatalog
{
    private const string DataBaseCoreTypeName = "GameData.Core.Collections.DataBaseCore";
    private const string DataBaseLanguageTypeName = "GameData.CoreLanguage.Collections.DataBaseLanguage";
    private const string DataBaseCharacterTypeName = "GameData.Core.Collections.CharacterUtility.DataBaseCharacter";

    private static readonly HashSet<string> NonOrderableRareFoodTags = new(StringComparer.Ordinal)
    {
        "流行喜爱",
        "流行厌恶",
    };

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly object SyncRoot = new();
    private static RuntimeStaticDataSnapshot _snapshot = RuntimeStaticDataSnapshot.Empty("not loaded");
    private static DateTime _lastReadAttemptUtc = DateTime.MinValue;
    private static bool _loaded;

    private readonly DataRepository _repository;
    private readonly IReadOnlyDictionary<int, Ingredient> _localIngredientsById;
    private readonly IReadOnlyDictionary<int, Beverage> _localBeveragesById;
    private readonly IReadOnlyDictionary<int, Recipe> _localRecipesByRecipeId;
    private readonly IReadOnlyDictionary<int, NormalCustomer> _localNormalCustomersById;
    private readonly IReadOnlyDictionary<int, RareCustomer> _localRareCustomersById;

    public RuntimeStaticDataCatalog(DataRepository repository)
    {
        _repository = repository;
        _localIngredientsById = repository.Ingredients
            .GroupBy(ingredient => ingredient.Id)
            .ToDictionary(group => group.Key, group => group.First());
        _localBeveragesById = repository.Beverages
            .GroupBy(beverage => beverage.Id)
            .ToDictionary(group => group.Key, group => group.First());
        _localRecipesByRecipeId = repository.Recipes
            .GroupBy(recipe => recipe.RecipeId)
            .ToDictionary(group => group.Key, group => group.First());
        _localNormalCustomersById = repository.NormalCustomers
            .GroupBy(customer => customer.Id)
            .ToDictionary(group => group.Key, group => group.First());
        _localRareCustomersById = repository.RareCustomersById;
    }

    public RuntimeStaticDataSnapshot Snapshot(RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot)
    {
        EnsureLoaded(mappedGuestSnapshot);
        lock (SyncRoot)
        {
            return _snapshot;
        }
    }

    public static void ResetRetryDelay()
    {
        lock (SyncRoot)
        {
            _lastReadAttemptUtc = DateTime.MinValue;
        }
    }

    private void EnsureLoaded(RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot)
    {
        lock (SyncRoot)
        {
            if (_loaded) return;
            if (DateTime.UtcNow - _lastReadAttemptUtc < RetryInterval) return;
            _lastReadAttemptUtc = DateTime.UtcNow;
        }

        var nextSnapshot = ReadSnapshot(mappedGuestSnapshot);
        lock (SyncRoot)
        {
            _snapshot = nextSnapshot;
            _loaded = nextSnapshot.IsComplete;
        }
    }

    private RuntimeStaticDataSnapshot ReadSnapshot(RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot)
    {
        var errors = new List<string>();
        var coreType = FindType(DataBaseCoreTypeName);
        var languageType = FindType(DataBaseLanguageTypeName);
        var characterType = FindType(DataBaseCharacterTypeName);

        if (coreType == null) errors.Add($"{DataBaseCoreTypeName} not found");
        if (languageType == null) errors.Add($"{DataBaseLanguageTypeName} not found");
        if (characterType == null) errors.Add($"{DataBaseCharacterTypeName} not found");

        var foodTags = ReadTagDictionary(languageType, "GetAllFoodTags", "GetAllFoodTagsId", "GetFoodTag");
        var beverageTags = ReadTagDictionary(languageType, "GetAllBeverageTags", null, "GetBeverageTag");
        var specialGuestNames = ReadStringDictionary(languageType, "GetAllSpecialGuestsNames");
        var tagLines = BuildTagLines(coreType, languageType, foodTags, beverageTags, errors);
        var coreLines = BuildCoreLines(coreType, languageType, foodTags, beverageTags, errors);
        var guestLines = BuildGuestLines(characterType, languageType, specialGuestNames, foodTags, beverageTags, mappedGuestSnapshot, errors);
        var izakayaLines = BuildIzakayaLines(coreType, languageType, errors);
        var coreEntryCount = CountDataRows(coreLines);
        var guestEntryCount = CountDataRows(guestLines);
        var izakayaEntryCount = CountDataRows(izakayaLines);

        var status = string.Join("; ", new[]
        {
            $"foodTags={foodTags.Count}",
            $"beverageTags={beverageTags.Count}",
            $"coreEntries={coreEntryCount}",
            $"guestEntries={guestEntryCount}",
            $"izakayaEntries={izakayaEntryCount}",
            errors.Count == 0 ? "errors=0" : $"errors={errors.Count}",
        });
        var runtimeData = BuildRuntimeDataCatalog(
            status,
            foodTags,
            coreLines,
            guestLines,
            izakayaLines);

        return new RuntimeStaticDataSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            Status = status,
            TagLines = tagLines,
            CoreLines = coreLines,
            GuestLines = guestLines,
            IzakayaLines = izakayaLines,
            ErrorLines = errors,
            DataCatalog = runtimeData,
            IsComplete = coreType != null
                && languageType != null
                && characterType != null
                && foodTags.Count > 0
                && beverageTags.Count > 0
                && coreEntryCount > 0
                && guestEntryCount > 0
                && izakayaEntryCount > 0,
        };
    }

    private static RuntimeDataCatalog BuildRuntimeDataCatalog(
        string status,
        IReadOnlyDictionary<int, string> foodTags,
        IReadOnlyList<string> coreLines,
        IReadOnlyList<string> guestLines,
        IReadOnlyList<string> izakayaLines)
    {
        try
        {
            var ingredientRows = ParseSectionRows(coreLines, "Ingredients").ToList();
            var beverageRows = ParseSectionRows(coreLines, "Beverages").ToList();
            var foodRows = ParseSectionRows(coreLines, "Foods").ToList();
            var recipeRows = ParseSectionRows(coreLines, "Recipes").ToList();
            var normalRows = ParseSectionRows(guestLines, "NormalGuests").ToList();
            var rareRows = ParseSectionRows(guestLines, "SpecialGuests").ToList();
            var izakayaPlaceRows = ParseSectionRows(izakayaLines, "IzakayaGuestPlaces").ToList();
            var normalPlacesById = BuildRuntimePlacesByGuestId(izakayaPlaceRows, "normal");
            var rarePlacesById = BuildRuntimePlacesByGuestId(izakayaPlaceRows, "rare");

            var ingredients = ingredientRows
                .Select(row => new Ingredient
                {
                    Id = ParseInt(row, "id") ?? -1,
                    Name = Field(row, "name"),
                    Tags = ParseTagNames(Field(row, "tags")),
                    Price = ParseInt(row, "value") ?? 0,
                })
                .Where(ingredient => ingredient.Id >= 0 && !string.IsNullOrWhiteSpace(ingredient.Name))
                .GroupBy(ingredient => ingredient.Id)
                .Select(group => group.First())
                .OrderBy(ingredient => ingredient.Id)
                .ToList();

            var beverages = beverageRows
                .Select(row =>
                {
                    var rawTags = ParseTagNames(Field(row, "rawTags"));
                    return new Beverage
                    {
                        Id = ParseInt(row, "id") ?? -1,
                        Name = Field(row, "name"),
                        Tags = rawTags.Count > 0 ? rawTags : ParseTagNames(Field(row, "tags")),
                        Price = ParseInt(row, "trueValue") ?? ParseInt(row, "value") ?? 0,
                    };
                })
                .Where(beverage => beverage.Id >= 0 && !string.IsNullOrWhiteSpace(beverage.Name))
                .GroupBy(beverage => beverage.Id)
                .Select(group => group.First())
                .OrderBy(beverage => beverage.Id)
                .ToList();

            var foods = foodRows
                .Select(row => new RuntimeFoodRow(
                    ParseInt(row, "id") ?? -1,
                    Field(row, "name"),
                    ParseTagNames(Field(row, "rawTags")).Count > 0
                        ? ParseTagNames(Field(row, "rawTags"))
                        : ParseTagNames(Field(row, "tags")),
                    ParseTagNames(Field(row, "banTags")),
                    ParseInt(row, "trueValue") ?? ParseInt(row, "value") ?? 0))
                .Where(food => food.Id >= 0 && !string.IsNullOrWhiteSpace(food.Name))
                .GroupBy(food => food.Id)
                .ToDictionary(group => group.Key, group => group.First());

            var recipes = recipeRows
                .Select(row =>
                {
                    var foodId = ParseInt(row, "foodId");
                    if (!foodId.HasValue || !foods.TryGetValue(foodId.Value, out var food)) return null;

                    return new Recipe
                    {
                        Id = food.Id,
                        RecipeId = ParseInt(row, "recipeId") ?? food.Id,
                        Name = food.Name,
                        Ingredients = ParseNamedCollection(Field(row, "ingredients")),
                        PositiveTags = food.PositiveTags,
                        NegativeTags = food.NegativeTags,
                        Cooker = NormalizeCooker(Field(row, "cooker")),
                        Price = food.Price,
                    };
                })
                .Where(recipe => recipe != null)
                .Cast<Recipe>()
                .Where(recipe => recipe.RecipeId >= 0 && !string.IsNullOrWhiteSpace(recipe.Name))
                .GroupBy(recipe => recipe.RecipeId)
                .Select(group => group.First())
                .OrderBy(recipe => recipe.RecipeId)
                .ToList();

            var normalCustomers = normalRows
                .Select(row =>
                {
                    var id = ParseInt(row, "id") ?? -1;
                    if (IsSuppressedRuntimeCustomerRow(row)) return null;

                    return new NormalCustomer
                    {
                        Id = id,
                        Name = Field(row, "name"),
                        Places = ResolvePlaces(Field(row, "localPlaces"), normalPlacesById, id),
                        PositiveTags = ParseTagNames(Field(row, "likeFood")),
                        BeverageTags = ParseTagNames(Field(row, "likeBev")),
                    };
                })
                .Where(customer => customer != null)
                .Cast<NormalCustomer>()
                .Where(customer => customer.Id >= 0
                    && IsUsableDisplayName(customer.Name)
                    && customer.Places.Count > 0
                    && (customer.PositiveTags.Count > 0 || customer.BeverageTags.Count > 0))
                .GroupBy(customer => customer.Id)
                .Select(group => group.First())
                .OrderBy(customer => customer.Id)
                .ToList();

            var rareCustomers = rareRows
                .Select(row =>
                {
                    var negative = ParseTagNames(Field(row, "hateFood"));
                    if (negative.Count == 0) negative = ParseTagNames(Field(row, "hateFoodOriginal"));
                    var positive = ParseTagNames(Field(row, "likeFood"));
                    if (positive.Count == 0) positive = ParseTagNames(Field(row, "likeFoodOriginal"));
                    var id = ParseInt(row, "id") ?? -1;
                    if (IsSuppressedRuntimeCustomerRow(row)) return null;

                    return new RareCustomer
                    {
                        Id = id,
                        Name = Field(row, "name"),
                        Places = ResolvePlaces(
                            Field(row, "localPlaces"),
                            rarePlacesById,
                            id,
                            Field(row, "spawnType")),
                        PositiveTags = positive,
                        NegativeTags = negative,
                        BeverageTags = ParseTagNames(Field(row, "likeBev")),
                    };
                })
                .Where(customer => customer != null)
                .Cast<RareCustomer>()
                .Where(customer => customer.Id >= 0
                    && IsUsableDisplayName(customer.Name)
                    && customer.Places.Count > 0
                    && customer.PositiveTags.Any(IsOrderableRareFoodTag)
                    && customer.BeverageTags.Count > 0)
                .GroupBy(customer => customer.Id)
                .Select(group => group.First())
                .OrderBy(customer => customer.Id)
                .ToList();

            var foodTagIdMap = foodTags
                .OrderBy(pair => pair.Key)
                .ToDictionary(
                    pair => pair.Key.ToString(CultureInfo.InvariantCulture),
                    pair => NormalizeTagName(pair.Value),
                    StringComparer.Ordinal);

            var isComplete = ingredients.Count > 0
                && beverages.Count > 0
                && recipes.Count > 0
                && normalCustomers.Count > 0
                && rareCustomers.Count > 0
                && foodTagIdMap.Count > 0;

            return new RuntimeDataCatalog
            {
                IsComplete = isComplete,
                Source = "game-runtime",
                Status = $"runtimeData=ingredients:{ingredients.Count},beverages:{beverages.Count},recipes:{recipes.Count},normal:{normalCustomers.Count},rare:{rareCustomers.Count}; {status}",
                Ingredients = ingredients,
                Beverages = beverages,
                Recipes = recipes,
                NormalCustomers = normalCustomers,
                RareCustomers = rareCustomers,
                FoodTagIdMap = foodTagIdMap,
            };
        }
        catch (Exception ex)
        {
            return RuntimeDataCatalog.Empty($"runtime data build failed: {ex.Message}");
        }
    }

    private static IEnumerable<Dictionary<string, string>> ParseSectionRows(IReadOnlyList<string> lines, string section)
    {
        var active = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                active = string.Equals(trimmed, $"[{section}]", StringComparison.Ordinal);
                continue;
            }

            if (!active || !trimmed.StartsWith("- ", StringComparison.Ordinal)) continue;
            var row = ParseFields(trimmed[2..]);
            if (row.Count > 0) yield return row;
        }
    }

    private static Dictionary<string, string> ParseFields(string line)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in line.Split(';'))
        {
            var index = part.IndexOf('=');
            if (index <= 0) continue;
            var key = part[..index].Trim();
            var value = part[(index + 1)..].Trim();
            if (key.Length == 0) continue;
            if (!fields.ContainsKey(key)) fields[key] = value;
        }

        return fields;
    }

    private static string Field(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var value) ? value.Trim() : "";
    }

    private static int? ParseInt(IReadOnlyDictionary<string, string> row, string key)
    {
        var value = Field(row, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static List<string> ParseTagNames(string value)
    {
        return ParseNamedCollection(value)
            .Select(NormalizeTagName)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> ParseNamedCollection(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)
            || string.Equals(trimmed, "[]", StringComparison.Ordinal)
            || string.Equals(trimmed, "missing", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string>();
        }

        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var at = part.IndexOf('@');
                var text = at >= 0 ? part[..at] : part;
                var paren = text.LastIndexOf('(');
                if (paren > 0) text = text[..paren];
                return text.Trim();
            })
            .Where(text => !string.IsNullOrWhiteSpace(text) && !text.StartsWith("#", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyDictionary<int, List<string>> BuildRuntimePlacesByGuestId(
        IEnumerable<IReadOnlyDictionary<string, string>> rows,
        string key)
    {
        var result = new Dictionary<int, HashSet<string>>();
        foreach (var row in rows)
        {
            var place = Field(row, "place");
            if (!PlaceNames.All.Contains(place, StringComparer.Ordinal)) continue;

            foreach (var id in ParseIntCollection(Field(row, key)))
            {
                if (!result.TryGetValue(id, out var places))
                {
                    places = new HashSet<string>(StringComparer.Ordinal);
                    result[id] = places;
                }

                places.Add(place);
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToList());
    }

    private static List<int> ParseIntCollection(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)
            || string.Equals(trimmed, "[]", StringComparison.Ordinal)
            || string.Equals(trimmed, "missing", StringComparison.OrdinalIgnoreCase))
        {
            return new List<int>();
        }

        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var at = part.IndexOf('@');
                var text = at >= 0 ? part[..at] : part;
                var paren = text.LastIndexOf('(');
                if (paren > 0) text = text[..paren];
                if (text.StartsWith("#", StringComparison.Ordinal)) text = text[1..];
                return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : (int?)null;
            })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    private static List<string> ResolvePlaces(
        string value,
        IReadOnlyDictionary<int, List<string>> runtimePlacesById,
        int id,
        string spawnType = "")
    {
        var places = ParseNamedCollection(value)
            .Where(place => PlaceNames.All.Contains(place, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (places.Count > 0) return places;
        if (string.Equals(spawnType, "EveryWhere", StringComparison.OrdinalIgnoreCase)) return PlaceNames.All.ToList();
        if (runtimePlacesById.TryGetValue(id, out var runtimePlaces) && runtimePlaces.Count > 0) return runtimePlaces;
        return new List<string>();
    }

    private static bool IsUsableDisplayName(string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0) return false;
        if (text.Equals("missing", StringComparison.OrdinalIgnoreCase)) return false;
        if (text.Equals("null", StringComparison.OrdinalIgnoreCase)) return false;
        if (text.Contains('?')) return false;
        if (text.StartsWith("#", StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool IsOrderableRareFoodTag(string tag)
    {
        return !string.IsNullOrWhiteSpace(tag) && !NonOrderableRareFoodTags.Contains(tag.Trim());
    }

    private static bool IsSuppressedRuntimeCustomerRow(IReadOnlyDictionary<string, string> row)
    {
        return IsTruthy(Field(row, "doNotShow"))
            || string.Equals(Field(row, "spawnType"), "NeverCome", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruthy(string value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.Ordinal);
    }

    private static string NormalizeTagName(string value)
    {
        var normalized = FoodTags.NormalizeName(value.Trim()) ?? value.Trim();
        return normalized;
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

    private static int CountDataRows(IReadOnlyList<string> lines)
    {
        return lines.Count(line =>
        {
            var text = line.TrimStart();
            return text.StartsWith("- id=", StringComparison.Ordinal)
                || text.StartsWith("- recipeId=", StringComparison.Ordinal);
        });
    }

    private List<string> BuildTagLines(
        Type? coreType,
        Type? languageType,
        IReadOnlyDictionary<int, string> foodTags,
        IReadOnlyDictionary<int, string> beverageTags,
        List<string> errors)
    {
        var lines = new List<string>
        {
            "[FoodTags]",
            $"count={foodTags.Count}",
        };
        lines.AddRange(foodTags.OrderBy(pair => pair.Key).Select(pair => $"  - id={pair.Key}; name={pair.Value}"));

        lines.Add("");
        lines.Add("[BeverageTags]");
        lines.Add($"count={beverageTags.Count}");
        lines.AddRange(beverageTags.OrderBy(pair => pair.Key).Select(pair => $"  - id={pair.Key}; name={pair.Value}"));

        lines.Add("");
        lines.Add("[FoodTagDlcMapping]");
        var foodTagDlcMapping = languageType == null ? null : GetStaticMemberValue(languageType, "FoodTagsDLCMapping");
        var mappingCount = 0;
        foreach (var pair in EnumerateKeyValuePairs(foodTagDlcMapping))
        {
            mappingCount++;
            lines.Add($"  - tag={FormatTagKey(pair.Key, foodTags)}; dlc={FormatSimpleValue(pair.Value)}");
        }
        lines.Insert(lines.Count - mappingCount, $"count={mappingCount}");

        lines.Add("");
        lines.Add("[TagRules]");
        var tagRuleCount = 0;
        if (coreType == null)
        {
            lines.Add("count=0");
        }
        else
        {
            try
            {
                var tagRules = GetStaticMemberValue(coreType, "TagRules");
                foreach (var pair in EnumerateKeyValuePairs(tagRules))
                {
                    tagRuleCount++;
                    lines.Add($"  - id={FormatSimpleValue(pair.Key)}; rules={FormatIntCollection(pair.Value, foodTags)}");
                }

                lines.Insert(lines.Count - tagRuleCount, $"count={tagRuleCount}");
            }
            catch (Exception ex)
            {
                errors.Add($"TagRules: {ex.Message}");
                lines.Add("count=0");
            }
        }

        return lines;
    }

    private List<string> BuildCoreLines(
        Type? coreType,
        Type? languageType,
        IReadOnlyDictionary<int, string> foodTags,
        IReadOnlyDictionary<int, string> beverageTags,
        List<string> errors)
    {
        var lines = new List<string>();
        if (coreType == null)
        {
            lines.Add("DataBaseCore unavailable.");
            return lines;
        }

        AppendRuntimeTable(
            lines,
            "Ingredients",
            ReadIds(coreType, "GetAllIngredients", "Ingredients", errors),
            id =>
            {
                var ingredient = InvokeStaticMethod(coreType, "RefIngredient", id);
                _localIngredientsById.TryGetValue(id, out var local);
                return $"id={id}; name={ResolveLanguageName(languageType, "GetIngredientLang", id, local?.Name)}; local={FormatLocalName(local?.Name)}; level={FormatMember(ingredient, "Level")}; value={FormatMember(ingredient, "BaseValue")}; tags={FormatTagCollection(GetMemberValue(ingredient, "Tags"), foodTags)}; prefix={FormatTagCollection(GetMemberValue(ingredient, "Prefix"), foodTags)}; flags=isFish:{FormatMember(ingredient, "IsFish")},isMeat:{FormatMember(ingredient, "IsMeat")},isVeg:{FormatMember(ingredient, "IsVeg")}; localTags={FormatStringCollection(local?.Tags)}; localPrice={local?.Price.ToString(CultureInfo.InvariantCulture) ?? "missing"}";
            });

        AppendRuntimeTable(
            lines,
            "Beverages",
            ReadIds(coreType, "GetAllBeverages", "Beverages", errors),
            id =>
            {
                var beverage = InvokeStaticMethod(coreType, "RefBeverage", id);
                _localBeveragesById.TryGetValue(id, out var local);
                return $"id={id}; name={ResolveLanguageName(languageType, "GetBeverageLang", id, local?.Name)}; local={FormatLocalName(local?.Name)}; level={FormatMember(beverage, "Level")}; value={FormatMember(beverage, "BaseValue")}; trueValue={FormatMember(beverage, "TrueValue")}; tags={FormatTagCollection(GetMemberValue(beverage, "Tags"), beverageTags)}; rawTags={FormatTagCollection(GetMemberValue(beverage, "RawTags"), beverageTags)}; banTags={FormatTagCollection(GetMemberValue(beverage, "BanTags"), beverageTags)}; additiveTags={FormatTagCollection(GetMemberValue(beverage, "AdditiveTags"), beverageTags)}; localTags={FormatStringCollection(local?.Tags)}; localPrice={local?.Price.ToString(CultureInfo.InvariantCulture) ?? "missing"}";
            });

        AppendRuntimeTable(
            lines,
            "Foods",
            ReadIds(coreType, "GetAllFoods", "Foods", errors),
            id =>
            {
                var food = InvokeStaticMethod(coreType, "RefFood", id);
                var localRecipe = _repository.Recipes.FirstOrDefault(recipe => recipe.Id == id);
                return $"id={id}; name={ResolveLanguageName(languageType, "GetFoodLang", id, localRecipe?.Name)}; localRecipe={FormatLocalName(localRecipe?.Name)}; level={FormatMember(food, "Level")}; value={FormatMember(food, "BaseValue")}; trueValue={FormatMember(food, "TrueValue")}; tags={FormatTagCollection(GetMemberValue(food, "Tags"), foodTags)}; rawTags={FormatTagCollection(GetMemberValue(food, "RawTags"), foodTags)}; banTags={FormatTagCollection(GetMemberValue(food, "BanTags"), foodTags)}; additiveTags={FormatTagCollection(GetMemberValue(food, "AdditiveTags"), foodTags)}; localPositiveTags={FormatStringCollection(localRecipe?.PositiveTags)}; localNegativeTags={FormatStringCollection(localRecipe?.NegativeTags)}; localPrice={localRecipe?.Price.ToString(CultureInfo.InvariantCulture) ?? "missing"}";
            });

        AppendRuntimeTable(
            lines,
            "Recipes",
            ReadIds(coreType, "GetAllRecipes", "Recipes", errors),
            id =>
            {
                var recipe = InvokeStaticMethod(coreType, "RefRecipe", id);
                var foodId = ToNullableInt(GetMemberValue(recipe, "FoodID") ?? GetMemberValue(recipe, "FoodId"));
                _localRecipesByRecipeId.TryGetValue(id, out var local);
                var ingredientIds = GetMemberValue(recipe, "Ingredients");
                return $"recipeId={id}; foodId={FormatNullable(foodId)}; name={ResolveLanguageName(languageType, "GetFoodToRecipeLang", id, local?.Name)}; foodName={ResolveLanguageName(languageType, "GetFoodLang", foodId, local?.Name)}; local={FormatLocalName(local?.Name)}; ingredients={FormatCoreIdCollection(ingredientIds, runtimeId => ResolveLanguageName(languageType, "GetIngredientLang", runtimeId, null))}; cooker={FormatMember(recipe, "CookerType")}; baseCookTime={FormatMember(recipe, "BaseCookTime")}; cookTime={FormatMember(recipe, "CookTime")}; localIngredients={FormatStringCollection(local?.Ingredients)}; localCooker={local?.Cooker ?? "missing"}";
            });

        return lines;
    }

    private List<string> BuildGuestLines(
        Type? characterType,
        Type? languageType,
        IReadOnlyDictionary<int, string> specialGuestNames,
        IReadOnlyDictionary<int, string> foodTags,
        IReadOnlyDictionary<int, string> beverageTags,
        RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot,
        List<string> errors)
    {
        var lines = new List<string>();
        if (characterType == null)
        {
            lines.Add("DataBaseCharacter unavailable.");
            return lines;
        }

        AppendRuntimeObjectTable(
            lines,
            "NormalGuests",
            InvokeStaticMethod(characterType, "GetAllNormalGuests"),
            guest =>
            {
                var id = ToNullableInt(GetMemberValue(guest, "Id") ?? GetMemberValue(guest, "ID"));
                NormalCustomer? local = null;
                if (id.HasValue) _localNormalCustomersById.TryGetValue(id.Value, out local);
                return $"id={FormatNullable(id)}; name={ResolveLanguageName(languageType, "GetNormalGuestLang", id, local?.Name)}; local={FormatLocalName(local?.Name)}; likeFood={FormatTagCollection(GetMemberValue(guest, "LikeFoodTag"), foodTags)}; likeBev={FormatTagCollection(GetMemberValue(guest, "LikeBevTag"), beverageTags)}; isLikeAllFood={FormatMember(guest, "IsLikeAllFoodTag")}; isLikeAllBev={FormatMember(guest, "IsLikeAllBevTag")}; isChild={FormatMember(guest, "IsChild")}; doNotShow={FormatMember(guest, "DoNotShowInNotebook")}; easter={FormatEasterData(GetMemberValue(guest, "GuestFoodEasterEggData"))}; localPlaces={FormatStringCollection(local?.Places)}; localFoodTags={FormatStringCollection(local?.PositiveTags)}; localBevTags={FormatStringCollection(local?.BeverageTags)}";
            },
            errors);

        AppendRuntimeObjectTable(
            lines,
            "SpecialGuests",
            InvokeStaticMethod(characterType, "GetAllSpecialGuests"),
            guest =>
            {
                var id = ToNullableInt(GetMemberValue(guest, "Id") ?? GetMemberValue(guest, "ID"));
                var stringId = GetMemberValue(guest, "StringId")?.ToString()
                    ?? GetMemberValue(guest, "StrID")?.ToString()
                    ?? "";
                RareCustomer? local = null;
                if (id.HasValue) _localRareCustomersById.TryGetValue(id.Value, out local);
                local ??= ResolveMappedLocalCustomer(mappedGuestSnapshot, id, stringId);
                return FormatSpecialGuestLine(guest, id, local, languageType, specialGuestNames, foodTags, beverageTags);
            },
            errors);

        AppendRuntimeObjectTable(
            lines,
            "SpecialGuestsAndMappedGuests",
            InvokeStaticMethod(characterType, "GetSpecialGuestsAndMappedGuests"),
            guest =>
            {
                var id = ToNullableInt(GetMemberValue(guest, "Id") ?? GetMemberValue(guest, "ID"));
                var stringId = GetMemberValue(guest, "StringId")?.ToString() ?? "";
                var mappedEntry = mappedGuestSnapshot.Entries.FirstOrDefault(entry =>
                    (id.HasValue && entry.RuntimeId == id.Value)
                    || (!string.IsNullOrWhiteSpace(stringId)
                        && string.Equals(entry.RuntimeStringId, stringId, StringComparison.OrdinalIgnoreCase)));
                return $"id={FormatNullable(id)}; stringId={stringId}; name={ResolveSpecialGuestName(languageType, specialGuestNames, id, mappedEntry?.LocalRareCustomerName)}; mappedSourceId={FormatNullable(mappedEntry?.SourceGuestId)}; mappedSourceName={mappedEntry?.SourceDisplayName ?? ""}; localId={FormatNullable(mappedEntry?.LocalRareCustomerId)}; localName={mappedEntry?.LocalRareCustomerName ?? ""}; aliasSource={mappedEntry?.AliasSource ?? ""}; overrideDestination={mappedEntry?.OverrideDestination ?? ""}";
            },
            errors);

        lines.Add("");
        lines.Add("[OriginSpecialGuestMappings]");
        var mappingCount = 0;
        foreach (var originValue in EnumerateObjects(InvokeStaticMethod(characterType, "GetAllOriginSGuestMapping")))
        {
            var originId = ToNullableInt(originValue);
            if (!originId.HasValue) continue;

            var args = new object?[] { originId.Value, 0 };
            var hasTarget = InvokeStaticMethodWithArgs(characterType, "TryGetTargetSGuestMapping", args) is bool boolValue && boolValue;
            var targetId = hasTarget ? ToNullableInt(args[1]) : null;
            mappingCount++;
            lines.Add($"  - origin={originId.Value}; target={FormatNullable(targetId)}; originName={ResolveSpecialGuestName(languageType, specialGuestNames, originId, null)}; targetName={ResolveSpecialGuestName(languageType, specialGuestNames, targetId, null)}");
        }
        lines.Insert(lines.Count - mappingCount, $"count={mappingCount}");

        return lines;
    }

    private List<string> BuildIzakayaLines(Type? coreType, Type? languageType, List<string> errors)
    {
        var lines = new List<string>();
        var placeRows = new List<string>();
        if (coreType == null)
        {
            lines.Add("DataBaseCore unavailable.");
            return lines;
        }

        AppendRuntimeTable(
            lines,
            "Izakayas",
            ReadIds(coreType, "GetAllIzakayas", "Izakayas", errors),
            id =>
            {
                var izakaya = InvokeStaticMethod(coreType, "RefIzakaya", id);
                var placeName = ResolveIzakayaPlaceName(languageType, izakaya, id);
                if (!string.IsNullOrWhiteSpace(placeName))
                {
                    var normalIds = ReadPoolGuestIds(GetMemberValue(izakaya, "NormalGuestPool"), isSpecialPool: false);
                    var rareIds = ReadPoolGuestIds(GetMemberValue(izakaya, "SpecialGuestPool"), isSpecialPool: true)
                        .Concat(ReadPoolGuestIds(GetMemberValue(izakaya, "OriginalSpecialGuestPool"), isSpecialPool: true))
                        .Distinct()
                        .OrderBy(value => value)
                        .ToList();
                    placeRows.Add($"  - place={placeName}; normal={FormatIdList(normalIds)}; rare={FormatIdList(rareIds)}");
                }

                return $"id={id}; name={ResolveLanguageName(languageType, "GetIzakayaLang", id, null)}; level={FormatMember(izakaya, "IzakayaLevel")}; daySceneMapLabel={FormatMember(izakaya, "DaySceneMapLabel")}; daySceneMapName={FormatMember(izakaya, "DaySceneMapName")}; baseFundRange={FormatSimpleValue(GetMemberValue(izakaya, "BaseFundRange"))}; normalGuestPool={FormatPool(GetMemberValue(izakaya, "NormalGuestPool"))}; specialGuestPool={FormatPool(GetMemberValue(izakaya, "SpecialGuestPool"))}; originalSpecialGuestPool={FormatPool(GetMemberValue(izakaya, "OriginalSpecialGuestPool"))}; specialGuestGachaInterval={FormatMember(izakaya, "SpecialGuestGachaInterval")}; normalGuestSpanInterval={FormatMember(izakaya, "NormalGuestSpanInterval")}; passerby={FormatMember(izakaya, "SpawnPasserbyGuest")}/{FormatMember(izakaya, "PasserbyGuestSpanInterval")}";
            });

        lines.Add("");
        lines.Add("[IzakayaGuestPlaces]");
        lines.Add($"count={placeRows.Count}");
        lines.AddRange(placeRows);

        return lines;
    }

    private string FormatSpecialGuestLine(
        object? guest,
        int? id,
        RareCustomer? local,
        Type? languageType,
        IReadOnlyDictionary<int, string> specialGuestNames,
        IReadOnlyDictionary<int, string> foodTags,
        IReadOnlyDictionary<int, string> beverageTags)
    {
        var stringId = GetMemberValue(guest, "StringId")?.ToString()
            ?? GetMemberValue(guest, "StrID")?.ToString()
            ?? "";
        return $"id={FormatNullable(id)}; stringId={stringId}; name={ResolveSpecialGuestName(languageType, specialGuestNames, id, local?.Name)}; local={FormatLocalName(local?.Name)}; likeFood={FormatTagCollection(GetMemberValue(guest, "LikeFoodTag"), foodTags)}; likeFoodOriginal={FormatTagCollection(GetMemberValue(guest, "LikeFoodTagOriginal"), foodTags)}; likeFoodUnfolded={FormatTagCollection(GetMemberValue(guest, "LikeFoodTagUnfolded"), foodTags)}; hateFood={FormatTagCollection(GetMemberValue(guest, "HateFoodTag"), foodTags)}; hateFoodOriginal={FormatTagCollection(GetMemberValue(guest, "HateFoodTagOriginal"), foodTags)}; likeBev={FormatTagCollection(GetMemberValue(guest, "LikeBevTag"), beverageTags)}; likeBevOriginal={FormatTagCollection(GetMemberValue(guest, "LikeBevTagOriginal"), beverageTags)}; likeBevUnfolded={FormatTagCollection(GetMemberValue(guest, "LikeBevTagUnfolded"), beverageTags)}; specialFoodText={ResolveLanguageDictionary(languageType, "GetSpecialFoodTagLang", id, foodTags)}; specialBevText={ResolveLanguageDictionary(languageType, "GetSpecialBevTagLang", id, beverageTags)}; spawnType={FormatMember(guest, "SpawnType")}; destination={FormatMember(guest, "Destination")}; doNotShow={FormatMember(guest, "DoNotShowInNotebook")}; easter={FormatEasterData(GetMemberValue(guest, "GuestFoodEasterEggData"))}; localPlaces={FormatStringCollection(local?.Places)}; localPositive={FormatStringCollection(local?.PositiveTags)}; localNegative={FormatStringCollection(local?.NegativeTags)}; localBev={FormatStringCollection(local?.BeverageTags)}";
    }

    private RareCustomer? ResolveMappedLocalCustomer(RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot, int? id, string? stringId)
    {
        var mappedEntry = mappedGuestSnapshot.Entries.FirstOrDefault(entry =>
            (id.HasValue && entry.RuntimeId == id.Value)
            || (!string.IsNullOrWhiteSpace(stringId)
                && string.Equals(entry.RuntimeStringId, stringId.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (mappedEntry == null || !mappedEntry.LocalRareCustomerId.HasValue) return null;
        var localId = mappedEntry.LocalRareCustomerId.Value;
        return _localRareCustomersById.TryGetValue(localId, out var local)
            ? local
            : null;
    }

    private static void AppendRuntimeTable(List<string> lines, string title, RuntimeIdReadResult readResult, Func<int, string> formatter)
    {
        lines.Add("");
        lines.Add($"[{title}]");
        lines.Add($"count={readResult.Ids.Count}");
        foreach (var diagnostic in readResult.Diagnostics)
        {
            lines.Add($"read={diagnostic}");
        }

        foreach (var id in readResult.Ids.OrderBy(id => id))
        {
            string formatted;
            try
            {
                formatted = formatter(id);
            }
            catch (Exception ex)
            {
                formatted = $"id={id}; error={ex.Message}";
            }

            lines.Add($"  - {formatted}");
        }
    }

    private static void AppendRuntimeObjectTable(
        List<string> lines,
        string title,
        object? values,
        Func<object?, string> formatter,
        List<string> errors)
    {
        lines.Add("");
        lines.Add($"[{title}]");
        var objects = EnumerateObjects(values).Where(value => value != null).ToList();
        lines.Add($"count={objects.Count}");

        foreach (var value in objects)
        {
            try
            {
                lines.Add($"  - {formatter(value)}");
            }
            catch (Exception ex)
            {
                errors.Add($"{title}: {ex.Message}");
                lines.Add($"  - error={ex.Message}; value={FormatSimpleValue(value)}");
            }
        }
    }

    private RuntimeIdReadResult ReadIds(Type coreType, string methodName, string fallbackMemberName, List<string> errors)
    {
        var diagnostics = new List<string>();
        try
        {
            var methodValue = InvokeStaticMethod(coreType, methodName);
            var methodIds = EnumerateIds(methodValue);
            diagnostics.Add($"method={methodName}; type={FormatTypeName(methodValue)}; count={methodIds.Count}");
            if (methodIds.Count > 0)
            {
                return new RuntimeIdReadResult(methodIds, diagnostics);
            }

            var fallbackValue = GetStaticMemberValue(coreType, fallbackMemberName);
            var fallbackIds = EnumerateKeyValuePairs(fallbackValue)
                .Select(pair => ToNullableInt(pair.Key))
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            diagnostics.Add($"fallbackMember={fallbackMemberName}; type={FormatTypeName(fallbackValue)}; keyCount={fallbackIds.Count}");
            return new RuntimeIdReadResult(fallbackIds, diagnostics);
        }
        catch (Exception ex)
        {
            errors.Add($"{methodName}/{fallbackMemberName}: {ex.Message}");
            diagnostics.Add($"error={ex.GetType().Name}: {ex.Message}");
            return new RuntimeIdReadResult(Array.Empty<int>(), diagnostics);
        }
    }

    private static IReadOnlyList<int> EnumerateIds(object? value)
    {
        return EnumerateObjects(value)
                .Select(ToNullableInt)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
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

    private static string ResolveLanguageDictionary(
        Type? languageType,
        string methodName,
        int? id,
        IReadOnlyDictionary<int, string> tagNames)
    {
        if (languageType == null || !id.HasValue) return "[]";

        var parts = EnumerateKeyValuePairs(InvokeStaticMethod(languageType, methodName, id.Value))
            .Select(pair =>
            {
                var key = FormatTagKey(pair.Key, tagNames);
                var value = CleanText(pair.Value);
                return string.IsNullOrWhiteSpace(value) ? key : $"{key}={value}";
            })
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        return parts.Count == 0 ? "[]" : $"[{string.Join(", ", parts)}]";
    }

    private static string FormatMember(object? instance, string memberName)
    {
        return FormatSimpleValue(GetMemberValue(instance, memberName));
    }

    private static string FormatLocalName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "missing" : value.Trim();
    }

    private static string FormatTagKey(object? key, IReadOnlyDictionary<int, string> tagNames)
    {
        var id = ToNullableInt(key);
        return id.HasValue ? FormatTag(id.Value, tagNames) : FormatSimpleValue(key);
    }

    private static string FormatTag(int id, IReadOnlyDictionary<int, string> tagNames)
    {
        return tagNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
            ? $"{name}({id})"
            : $"#{id}({id})";
    }

    private static string FormatTagCollection(object? value, IReadOnlyDictionary<int, string> tagNames)
    {
        var parts = new List<string>();
        foreach (var item in EnumerateObjects(value))
        {
            var id = ToNullableInt(item)
                ?? ToNullableInt(GetMemberValue(item, "tagId"))
                ?? ToNullableInt(GetMemberValue(item, "TagId"))
                ?? ToNullableInt(GetMemberValue(item, "ID"))
                ?? ToNullableInt(GetMemberValue(item, "Id"));
            if (id.HasValue)
            {
                var weight = ToNullableFloat(GetMemberValue(item, "weight") ?? GetMemberValue(item, "Weight"));
                parts.Add(weight.HasValue
                    ? $"{FormatTag(id.Value, tagNames)}@{weight.Value.ToString("0.###", CultureInfo.InvariantCulture)}"
                    : FormatTag(id.Value, tagNames));
            }
            else if (item != null)
            {
                parts.Add(FormatSimpleValue(item));
            }
        }

        if (parts.Count == 0)
        {
            var id = ToNullableInt(value);
            if (id.HasValue) parts.Add(FormatTag(id.Value, tagNames));
        }

        return parts.Count == 0 ? "[]" : $"[{string.Join(", ", parts)}]";
    }

    private static string FormatIntCollection(object? value, IReadOnlyDictionary<int, string> tagNames)
    {
        var parts = EnumerateObjects(value)
            .Select(item =>
            {
                var id = ToNullableInt(item);
                return id.HasValue ? FormatTag(id.Value, tagNames) : FormatSimpleValue(item);
            })
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        return parts.Count == 0 ? "[]" : $"[{string.Join(", ", parts)}]";
    }

    private static string FormatCoreIdCollection(object? value, Func<int, string> nameResolver)
    {
        var parts = EnumerateObjects(value)
            .Select(ToNullableInt)
            .Where(id => id.HasValue)
            .Select(id =>
            {
                var name = nameResolver(id!.Value);
                return string.IsNullOrWhiteSpace(name) ? $"#{id.Value}" : $"{name}({id.Value})";
            })
            .ToList();
        return parts.Count == 0 ? "[]" : $"[{string.Join(", ", parts)}]";
    }

    private static string FormatStringCollection(IEnumerable<string>? values)
    {
        if (values == null) return "missing";
        var list = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        return list.Count == 0 ? "[]" : $"[{string.Join(", ", list)}]";
    }

    private static string FormatPool(object? value)
    {
        var parts = new List<string>();
        foreach (var item in EnumerateObjects(value).Take(80))
        {
            var id = ToNullableInt(item)
                ?? ToNullableInt(GetMemberValue(item, "ID"))
                ?? ToNullableInt(GetMemberValue(item, "Id"))
                ?? ToNullableInt(GetMemberValue(item, "GuestID"))
                ?? ToNullableInt(GetMemberValue(item, "GuestId"))
                ?? ToNullableInt(GetMemberValue(item, "SpecialGuestID"))
                ?? ToNullableInt(GetMemberValue(item, "SpecialGuestId"));
            var weight = GetMemberValue(item, "Weight")
                ?? GetMemberValue(item, "weight")
                ?? GetMemberValue(item, "Prob")
                ?? GetMemberValue(item, "Probability");
            if (id.HasValue)
            {
                parts.Add(weight == null ? id.Value.ToString(CultureInfo.InvariantCulture) : $"{id.Value}@{FormatSimpleValue(weight)}");
            }
            else if (item != null)
            {
                parts.Add(DescribeSimpleObject(item, 6));
            }
        }

        return parts.Count == 0 ? "[]" : $"[{string.Join(", ", parts)}]";
    }

    private static IReadOnlyList<int> ReadPoolGuestIds(object? value, bool isSpecialPool)
    {
        var result = new HashSet<int>();
        foreach (var item in EnumerateObjects(value).Take(200))
        {
            CollectPoolGuestIds(item, isSpecialPool, result, 0);
        }

        return result.OrderBy(id => id).ToList();
    }

    private static void CollectPoolGuestIds(object? value, bool isSpecialPool, HashSet<int> result, int depth)
    {
        if (value == null || depth > 4) return;

        var direct = ToNullableInt(value);
        if (direct.HasValue)
        {
            result.Add(direct.Value);
            return;
        }

        var idMembers = isSpecialPool
            ? new[] { "GroupID", "GroupId", "groupID", "groupId", "SpecialGuestID", "SpecialGuestId", "GuestID", "GuestId", "ID", "Id" }
            : new[] { "GuestID", "GuestId", "guestID", "guestId", "NormalGuestID", "NormalGuestId", "ID", "Id" };

        foreach (var member in idMembers)
        {
            var id = ToNullableInt(GetMemberValue(value, member));
            if (id.HasValue) result.Add(id.Value);
        }

        foreach (var member in new[]
                 {
                     "Data",
                     "data",
                     "Values",
                     "values",
                     "Guests",
                     "guests",
                     "IDs",
                     "Ids",
                     "ids",
                     "GuestIDs",
                     "GuestIds",
                     "guestIds",
                 })
        {
            foreach (var item in EnumerateObjects(GetMemberValue(value, member)).Take(200))
            {
                CollectPoolGuestIds(item, isSpecialPool, result, depth + 1);
            }
        }
    }

    private static string FormatIdList(IEnumerable<int> values)
    {
        var list = values.Distinct().OrderBy(value => value).ToList();
        return list.Count == 0 ? "[]" : $"[{string.Join(",", list)}]";
    }

    private static string ResolveIzakayaPlaceName(Type? languageType, object? izakaya, int id)
    {
        foreach (var candidate in new[]
                 {
                     CleanText(GetMemberValue(izakaya, "DaySceneMapName")),
                     CleanText(GetMemberValue(izakaya, "daySceneMapName")),
                     CleanText(GetMemberValue(izakaya, "DaySceneMapLabel")),
                     CleanText(GetMemberValue(izakaya, "daySceneMapLabel")),
                     ResolveLanguageName(languageType, "GetIzakayaLang", id, null),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var place = PlaceNames.All.FirstOrDefault(name =>
                string.Equals(name, candidate.Trim(), StringComparison.Ordinal)
                || candidate.Contains(name, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(place)) return place;
        }

        return "";
    }

    private static string FormatEasterData(object? value)
    {
        if (value == null) return "";
        return $"{value.GetType().FullName ?? value.GetType().Name} {DescribeSimpleObject(value, 12)}";
    }

    private static string FormatSimpleValue(object? value)
    {
        if (value == null) return "";
        if (value is string text) return text.Trim();
        if (value is bool boolValue) return boolValue ? "true" : "false";
        if (value is IFormattable formattable
            && (value is int
                || value is long
                || value is short
                || value is float
                || value is double
                || value is decimal))
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        var enumerableValues = EnumerateObjects(value).Take(24).ToList();
        if (enumerableValues.Count > 0 && value is not string)
        {
            return $"[{string.Join(", ", enumerableValues.Select(FormatSimpleValue))}]";
        }

        return CleanText(value);
    }

    private static string DescribeSimpleObject(object? value, int maxMembers)
    {
        if (value == null) return "";
        var type = value.GetType();
        var members = new List<string>();
        foreach (var member in EnumerateReadableMembers(type).Take(maxMembers))
        {
            object? memberValue;
            try
            {
                memberValue = member switch
                {
                    FieldInfo field => field.GetValue(value),
                    PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(value),
                    _ => null,
                };
            }
            catch
            {
                continue;
            }

            if (memberValue == null) continue;
            if (!IsSimpleOrEnumerable(memberValue)) continue;
            members.Add($"{member.Name}={FormatSimpleValue(memberValue)}");
        }

        return members.Count == 0 ? "{}" : $"{{{string.Join("; ", members)}}}";
    }

    private static bool IsSimpleOrEnumerable(object value)
    {
        if (value is string) return true;
        var type = value.GetType();
        return type.IsPrimitive
            || type.IsEnum
            || value is decimal
            || value is IEnumerable;
    }

    private static IEnumerable<MemberInfo> EnumerateReadableMembers(Type type)
    {
        while (type != null)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.IsStatic) continue;
                yield return field;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0) continue;
                yield return property;
            }

            type = type.BaseType!;
        }
    }

    private static string FormatTypeName(object? value)
    {
        return value == null ? "null" : value.GetType().FullName ?? value.GetType().Name;
    }

    private static string CleanText(object? value)
    {
        if (value == null) return "";

        if (value is string stringValue) return stringValue.Trim();

        foreach (var memberName in new[]
                 {
                     "Name",
                     "DisplayName",
                     "Title",
                     "Label",
                     "Text",
                     "Description",
                     "ShortDescription",
                     "name",
                     "title",
                     "text",
                     "description",
                 })
        {
            var memberValue = GetMemberValue(value, memberName);
            if (memberValue == null || ReferenceEquals(memberValue, value)) continue;
            var memberText = memberValue.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(memberText)) return memberText;
        }

        try
        {
            var text = value.ToString()?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith(value.GetType().FullName ?? "", StringComparison.Ordinal))
            {
                return text;
            }

            var described = DescribeSimpleObject(value, 4);
            return described == "{}" ? text : described;
        }
        catch
        {
            return "";
        }
    }

    private static string FormatNullable(int? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";
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
        return InvokeStaticMethodWithArgs(type, name, args);
    }

    private static object? InvokeStaticMethodWithArgs(Type? type, string name, object?[] args)
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

    private static object? InvokeInstanceMethod(object? instance, string name, params object?[] args)
    {
        if (instance == null) return null;
        var method = instance
            .GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length);
        if (method == null) return null;

        try
        {
            return method.Invoke(instance, args);
        }
        catch
        {
            return null;
        }
    }

    private static object? GetStaticMemberValue(Type type, string name)
    {
        foreach (var fieldName in BuildFieldNameCandidates(name))
        {
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (TryReadField(null, field, out var fieldValue) && fieldValue != null) return fieldValue;
        }

        var property = FindProperty(type, name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (TryReadProperty(null, property, out var propertyValue) && propertyValue != null) return propertyValue;

        return null;
    }

    private static object? GetMemberValue(object? instance, string name)
    {
        if (instance == null || string.IsNullOrWhiteSpace(name)) return null;
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
        if (value is string) yield break;

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }

            yield break;
        }

        var enumerator = InvokeInstanceMethod(value, "GetEnumerator");
        if (enumerator != null)
        {
            while (true)
            {
                var moved = InvokeInstanceMethod(enumerator, "MoveNext");
                if (moved is not bool boolMoved || !boolMoved) yield break;
                yield return GetMemberValue(enumerator, "Current");
            }
        }

        var count = ToNullableInt(GetMemberValue(value, "Count") ?? GetMemberValue(value, "Length")) ?? 0;
        var indexer = value.GetType().GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (count <= 0 || indexer == null) yield break;

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

    private static IEnumerable<(object? Key, object? Value)> EnumerateKeyValuePairs(object? dictionary)
    {
        foreach (var item in EnumerateObjects(dictionary))
        {
            var key = GetMemberValue(item, "Key") ?? GetMemberValue(item, "key");
            var value = GetMemberValue(item, "Value") ?? GetMemberValue(item, "value");
            if (key != null || value != null) yield return (key, value);
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
        if (value is byte byteValue) return byteValue;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static float? ToNullableFloat(object? value)
    {
        if (value == null) return null;
        if (value is float floatValue) return floatValue;
        if (value is double doubleValue) return (float)doubleValue;
        if (value is decimal decimalValue) return (float)decimalValue;
        return float.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
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

internal sealed record RuntimeIdReadResult(IReadOnlyList<int> Ids, IReadOnlyList<string> Diagnostics);
internal sealed record RuntimeFoodRow(int Id, string Name, List<string> PositiveTags, List<string> NegativeTags, int Price);
