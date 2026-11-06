using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 从游戏运行时数据库构建供业务层使用的纯托管目录；关键依赖读取失败时整批目录不可用。
/// 首次读取和重置后的重建必须由可访问游戏对象的线程发起；锁只保护缓存发布，不调度主线程。
/// </summary>
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

    /// <summary>复用已完整加载的目录；上次失败时重新采集，使数据库尚未就绪的情况可以恢复。</summary>
    public RuntimeStaticDataSnapshot Snapshot()
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return _snapshot;
        }
    }

    /// <summary>撤销已发布数据的可复用状态；反射方法元数据保留，后续仍按实际 Type 校验缓存。</summary>
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

        // 游戏读取放在发布锁外；调用方仍须串行安排采集，锁本身不提供跨线程快照一致性。
        var nextSnapshot = ReadSnapshot();
        lock (SyncRoot)
        {
            _snapshot = nextSnapshot;
            _loaded = nextSnapshot.IsComplete;
        }
    }

    /// <summary>
    /// 先建立映射根及配方依赖闭包，再读取名称、标签和客户目录，全部关键检查通过后发布。
    /// phase 标记用于定位失败阶段；任何必需项失败都返回空业务目录，避免把部分目录当成完整数据。
    /// </summary>
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

            phase = "read-recipe-descriptors";
            var recipeDescriptors = ReadRecipeDescriptors(methods.RefRecipe, recipeIds);
            // 配方引用可能不在映射根中；先补齐依赖 ID，再读取食材和成品，不能靠连续 ID 扫描推测。
            phase = "resolve-recipe-dependencies";
            var recipeDependencies = RuntimeRecipeDependencyProjection.Build(
                ingredientIds,
                foodIds,
                recipeDescriptors);
            phase = "read-izakaya-places";
            var places = ReadIzakayaPlaces(methods.RefIzakaya, izakayaIds);
            phase = "read-ingredients";
            var ingredients = ReadIngredients(
                methods.RefIngredient,
                methods.GetIngredientLang,
                recipeDependencies.IngredientIds,
                foodTags,
                recipeDependencies.IngredientSourceRecipeIds);
            phase = "read-beverages";
            var beverages = ReadBeverages(methods.RefBeverage, methods.GetBeverageLang, beverageIds, beverageTags);
            phase = "read-foods";
            var foods = ReadFoods(
                methods.RefFood,
                methods.GetFoodLang,
                recipeDependencies.FoodIds,
                foodTags,
                recipeDependencies.FoodSourceRecipeIds);
            phase = "materialize-recipes";
            var recipes = MaterializeRecipes(recipeDescriptors, foods, ingredients);
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

            // 此完整性针对本适配器所需目录及过滤规则，不表示游戏所有隐藏内容均向业务层开放。
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
                    $"mappedIngredients:{ingredientIds.Count}",
                    $"recipeIngredientDependencies:{recipeDependencies.DependencyIngredientIds.Count}",
                    $"beverages:{beverages.Count}",
                    $"foods:{foods.Count}",
                    $"mappedFoods:{foodIds.Count}",
                    $"recipeFoodDependencies:{recipeDependencies.DependencyFoodIds.Count}",
                    $"recipes:{recipes.Count}",
                    $"normal:{normalCustomers.Count}",
                    $"rare:{rareCustomers.Count}",
                    $"izakayas:{places.IzakayaCount}",
                    "tagRules:0"),
                Ingredients = ingredients,
                Beverages = beverages,
                Recipes = recipes,
                SpecialProducts = ReadSpecialProducts(methods.RefFood, methods.GetFoodLang, foodTags),
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

            var status = $"runtimeData={catalog.Status}; source=five-core-mapping-roots+recipe-dependencies";
            return new RuntimeStaticDataSnapshot
            {
                CapturedAtUtc = DateTime.UtcNow,
                Status = status,
                TagLines = BuildTagLines(foodTags, beverageTags),
                CoreLines = BuildCoreLines(
                    ingredients,
                    beverages,
                    foods.Values,
                    recipes,
                    recipeDependencies),
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

    /// <summary>读取映射根及配方依赖食材，并核对返回对象 ID；失败诊断保留引用配方来源。</summary>
    private static List<Ingredient> ReadIngredients(
        MethodInfo refIngredient,
        MethodInfo getIngredientLang,
        IReadOnlyList<int> ids,
        IReadOnlyDictionary<int, string> foodTags,
        IReadOnlyDictionary<int, IReadOnlyList<int>> sourceRecipeIds)
    {
        var result = new List<Ingredient>(ids.Count);
        foreach (var id in ids)
        {
            try
            {
                var runtime = InvokeRequiredStatic(refIngredient, id);
                RequireRuntimeObjectId(runtime, id, $"DataBaseCore.RefIngredient({id})");
                result.Add(new Ingredient
                {
                    Id = id,
                    Name = ReadRequiredLanguageName(getIngredientLang, id),
                    Tags = ReadTagNames(ReadRequiredIntArrayMember(runtime, "RawTags"), foodTags),
                    Price = ReadRequiredIntMember(runtime, "baseValue"),
                });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{DescribeCatalogEntryOrigin("ingredient", "IngredientsMapping", id, sourceRecipeIds)} "
                    + $"could not be resolved through DataBaseCore.RefIngredient: {ex.Message}",
                    ex);
            }
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
        IReadOnlyDictionary<int, string> foodTags,
        IReadOnlyDictionary<int, IReadOnlyList<int>> sourceRecipeIds)
    {
        var result = new Dictionary<int, RuntimeFoodData>();
        foreach (var id in ids)
        {
            try
            {
                var runtime = InvokeRequiredStatic(refFood, id);
                RequireRuntimeObjectId(runtime, id, $"DataBaseCore.RefFood({id})");
                result.Add(id, new RuntimeFoodData(
                    id,
                    ReadRequiredLanguageName(getFoodLang, id),
                    ReadTagNames(ReadRequiredIntArrayMember(runtime, "RawTags"), foodTags),
                    ReadTagNames(ReadRequiredIntArrayMember(runtime, "banTags"), foodTags),
                    ReadRequiredIntMember(runtime, "level"),
                    ReadRequiredIntMember(runtime, "baseValue")));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{DescribeCatalogEntryOrigin("food", "FoodsMapping", id, sourceRecipeIds)} "
                    + $"could not be resolved through DataBaseCore.RefFood: {ex.Message}",
                    ex);
            }
        }

        return result;
    }

    /// <summary>
    /// 黑暗料理由游戏烹饪失败转换产生，没有 RecipesMapping 条目。只读取 Food -1 的
    /// 精确原生定义与本地化，绝不追加伪配方。此能力读取失败时保留普通目录，单独让任务目标等待。
    /// </summary>
    private static List<SpecialFoodProduct> ReadSpecialProducts(
        MethodInfo refFood, MethodInfo getFoodLang, IReadOnlyDictionary<int, string> foodTags)
    {
        try
        {
            var food = InvokeRequiredStatic(refFood, -1);
            RequireRuntimeObjectId(food, -1, "DataBaseCore.RefFood(-1)");
            return new List<SpecialFoodProduct>
            {
                new()
                {
                    Id = -1, ProductKind = "dark-cuisine",
                    Name = ReadRequiredLanguageName(getFoodLang, -1),
                    PositiveTags = ReadTagNames(ReadRequiredIntArrayMember(food, "RawTags"), foodTags),
                    Level = ReadRequiredIntMember(food, "level"),
                    Price = ReadRequiredIntMember(food, "baseValue"),
                },
            };
        }
        catch
        {
            // 专项能力未知不能污染普通料理、任务列表或其他订单的计算。
            return new List<SpecialFoodProduct>();
        }
    }

    /// <summary>将已验证配方身份及依赖复制为托管描述，后续构建不保留原生配方对象。</summary>
    private static List<RuntimeRecipeDescriptor> ReadRecipeDescriptors(
        MethodInfo refRecipe,
        IReadOnlyList<int> ids)
    {
        var result = new List<RuntimeRecipeDescriptor>(ids.Count);
        foreach (var recipeId in ids)
        {
            try
            {
                var runtime = InvokeRequiredStatic(refRecipe, recipeId);
                RequireRuntimeObjectId(runtime, recipeId, $"DataBaseCore.RefRecipe({recipeId})");
                result.Add(new RuntimeRecipeDescriptor(
                    recipeId,
                    ReadRequiredIntMember(runtime, "foodID"),
                    ReadRequiredIntArrayMember(runtime, "ingredients").ToArray(),
                    NormalizeCooker(ReadRequiredMember(runtime, "cookerType").ToString() ?? "")));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"RecipesMapping[{recipeId}] could not be resolved through "
                    + $"DataBaseCore.RefRecipe: {ex.Message}",
                    ex);
            }
        }

        return result;
    }

    /// <summary>
    /// 以已读依赖组装配方；FoodId 是成品身份，RecipeId 保留原配方身份，两者不可互换。
    /// 缺少任一依赖立即拒绝目录，不用显示名称补配或省略重复食材。
    /// </summary>
    private static List<Recipe> MaterializeRecipes(
        IReadOnlyList<RuntimeRecipeDescriptor> descriptors,
        IReadOnlyDictionary<int, RuntimeFoodData> foods,
        IReadOnlyList<Ingredient> ingredients)
    {
        var ingredientNames = ingredients.ToDictionary(ingredient => ingredient.Id, ingredient => ingredient.Name);
        var result = new List<Recipe>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            if (!foods.TryGetValue(descriptor.FoodId, out var food))
            {
                throw new InvalidOperationException(
                    $"Recipe dependency closure omitted food ID {descriptor.FoodId} "
                    + $"referenced by recipe {descriptor.RecipeId}.");
            }

            var recipeIngredients = new List<string>(descriptor.IngredientIds.Count);
            foreach (var ingredientId in descriptor.IngredientIds)
            {
                if (!ingredientNames.TryGetValue(ingredientId, out var ingredientName))
                {
                    throw new InvalidOperationException(
                        $"Recipe dependency closure omitted ingredient ID {ingredientId} "
                        + $"referenced by recipe {descriptor.RecipeId}.");
                }

                recipeIngredients.Add(ingredientName);
            }

            result.Add(new Recipe
            {
                Id = descriptor.FoodId,
                RecipeId = descriptor.RecipeId,
                Name = food.Name,
                Ingredients = recipeIngredients,
                // 直接复制已验证原生配方的依赖，保留重复食材，不借用名称或可变原生集合。
                IngredientIds = descriptor.IngredientIds.ToList(),
                PositiveTags = food.PositiveTags,
                NegativeTags = food.NegativeTags,
                Cooker = descriptor.Cooker,
                Level = food.Level,
                Price = food.Price,
            });
        }

        return result;
    }

    /// <summary>仅纳入可见、具有已支持出没地点且至少有一类偏好的普客；筛选规则不代表原生名单为空。</summary>
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

    /// <summary>
    /// 投影具备可点单料理标签和酒水偏好的稀客；全地图类型使用支持的地点列表。
    /// 其他稀客允许地点列表为空，但有效条目的本地化名称缺失会使目录构建失败。
    /// </summary>
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

    /// <summary>按居酒屋配置汇总客户地点；不支持的地点跳过客池，已支持地点的客池不可读则拒绝目录。</summary>
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

    // 使用具体字典完整读取结果和各映射的 ID 域规则；缺失成员不能按空映射继续。
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

    /// <summary>要求非空、有界且键值类型精确的语言映射；拒绝重复 ID，负 ID 是否允许由映射用途决定。</summary>
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

    // 业务目录只需要偏好标签集合，此投影提取 tagId，不把原生抽取权重解释为推荐评分。
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

    /// <summary>严格解析每个标签 ID，再按规范名称去重；语言映射缺项不以数字或未知标签代替。</summary>
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

    private static void RequireRuntimeObjectId(object instance, int expectedId, string source)
    {
        var actualId = ReadRequiredIntMember(instance, "id");
        if (actualId != expectedId)
        {
            throw new InvalidOperationException(
                $"{source} returned object ID {actualId} instead of {expectedId}.");
        }
    }

    private static string DescribeCatalogEntryOrigin(
        string entryType,
        string mappingName,
        int id,
        IReadOnlyDictionary<int, IReadOnlyList<int>> sourceRecipeIds)
    {
        return sourceRecipeIds.TryGetValue(id, out var recipeIds)
            ? $"Recipe dependency {entryType} ID {id} referenced by RecipesMapping recipe IDs "
                + FormatIds(recipeIds)
            : $"{mappingName}[{id}]";
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

    // 缓存绑定到三个实际 Type；同名类型来自不同程序集实例时重新解析整组方法。
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

    /// <summary>要求名称、静态性及参数类型精确匹配且候选唯一；返回类型须非 void，具体值形状由读取步骤复核。</summary>
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

    // 展开反射包装异常并保留原异常堆栈，让失败阶段指向数据库调用本身而非反射外壳。
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
        IReadOnlyList<Recipe> recipes,
        RuntimeRecipeDependencyClosure recipeDependencies)
    {
        var lines = new List<string>
        {
            "[RecipeDependencyClosure]",
            $"ingredientDependencyCount={recipeDependencies.DependencyIngredientIds.Count}",
            $"ingredientDependencyIds={FormatIds(recipeDependencies.DependencyIngredientIds)}",
        };
        lines.AddRange(recipeDependencies.DependencyIngredientIds.Select(id =>
            $"  - ingredientId={id}; sourceRecipeIds="
            + FormatIds(recipeDependencies.IngredientSourceRecipeIds[id])));
        lines.Add($"foodDependencyCount={recipeDependencies.DependencyFoodIds.Count}");
        lines.Add($"foodDependencyIds={FormatIds(recipeDependencies.DependencyFoodIds)}");
        lines.AddRange(recipeDependencies.DependencyFoodIds.Select(id =>
            $"  - foodId={id}; sourceRecipeIds="
            + FormatIds(recipeDependencies.FoodSourceRecipeIds[id])));
        lines.Add("");
        lines.AddRange(new[]
        {
            "[Ingredients]",
            $"count={ingredients.Count}",
        });
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

    // 只转换已知枚举名称；未知名称保留原值，避免误归入任一已知厨具。
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
