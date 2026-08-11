namespace MystiaStewardCompanion.Save;

internal sealed record RuntimeRecipeDescriptor(
    int RecipeId,
    int FoodId,
    IReadOnlyList<int> IngredientIds,
    string Cooker);

internal sealed record RuntimeRecipeDependencyClosure(
    IReadOnlyList<int> IngredientIds,
    IReadOnlyList<int> FoodIds,
    IReadOnlyList<int> DependencyIngredientIds,
    IReadOnlyList<int> DependencyFoodIds,
    IReadOnlyDictionary<int, IReadOnlyList<int>> IngredientSourceRecipeIds,
    IReadOnlyDictionary<int, IReadOnlyList<int>> FoodSourceRecipeIds);

/// <summary>
/// Expands the five verified mapping roots only through explicit dependencies of mapped recipes.
/// This does not enumerate the game's inflated ingredient or food dictionaries.
/// </summary>
internal static class RuntimeRecipeDependencyProjection
{
    private const int MaxCatalogItems = 4096;
    private const int MaxRecipeIngredientReferences = 16384;

    public static RuntimeRecipeDependencyClosure Build(
        IReadOnlyList<int> mappedIngredientIds,
        IReadOnlyList<int> mappedFoodIds,
        IReadOnlyList<RuntimeRecipeDescriptor> recipes)
    {
        ArgumentNullException.ThrowIfNull(mappedIngredientIds);
        ArgumentNullException.ThrowIfNull(mappedFoodIds);
        ArgumentNullException.ThrowIfNull(recipes);

        var ingredientIds = ReadMappedIds(mappedIngredientIds, "IngredientsMapping");
        var foodIds = ReadMappedIds(mappedFoodIds, "FoodsMapping");
        if (recipes.Count == 0 || recipes.Count > MaxCatalogItems)
        {
            throw new InvalidOperationException(
                $"RecipesMapping returned invalid descriptor count {recipes.Count}.");
        }

        var mappedIngredientSet = new HashSet<int>(ingredientIds);
        var mappedFoodSet = new HashSet<int>(foodIds);
        var allIngredientIds = new HashSet<int>(mappedIngredientSet);
        var allFoodIds = new HashSet<int>(mappedFoodSet);
        var ingredientSources = new Dictionary<int, HashSet<int>>();
        var foodSources = new Dictionary<int, HashSet<int>>();
        var seenRecipeIds = new HashSet<int>();
        var ingredientReferenceCount = 0;

        foreach (var recipe in recipes)
        {
            if (recipe == null)
            {
                throw new InvalidOperationException("RecipesMapping produced a null recipe descriptor.");
            }

            if (recipe.RecipeId < 0 || !seenRecipeIds.Add(recipe.RecipeId))
            {
                throw new InvalidOperationException(
                    recipe.RecipeId < 0
                        ? $"RecipesMapping produced negative recipe ID {recipe.RecipeId}."
                        : $"RecipesMapping produced duplicate recipe ID {recipe.RecipeId}.");
            }

            if (recipe.FoodId < 0)
            {
                throw new InvalidOperationException(
                    $"Recipe {recipe.RecipeId} references negative food ID {recipe.FoodId}.");
            }

            AddDependency(allFoodIds, foodSources, recipe.FoodId, recipe.RecipeId, "food");
            if (recipe.IngredientIds == null)
            {
                throw new InvalidOperationException(
                    $"Recipe {recipe.RecipeId} has no ingredient ID collection.");
            }

            foreach (var ingredientId in recipe.IngredientIds)
            {
                ingredientReferenceCount += 1;
                if (ingredientReferenceCount > MaxRecipeIngredientReferences)
                {
                    throw new InvalidOperationException(
                        $"Mapped recipes exceeded the {MaxRecipeIngredientReferences}-ingredient-reference limit.");
                }

                if (ingredientId < 0)
                {
                    throw new InvalidOperationException(
                        $"Recipe {recipe.RecipeId} references negative ingredient ID {ingredientId}.");
                }

                AddDependency(
                    allIngredientIds,
                    ingredientSources,
                    ingredientId,
                    recipe.RecipeId,
                    "ingredient");
            }
        }

        var dependencyIngredientIds = allIngredientIds
            .Where(id => !mappedIngredientSet.Contains(id))
            .OrderBy(id => id)
            .ToArray();
        var dependencyFoodIds = allFoodIds
            .Where(id => !mappedFoodSet.Contains(id))
            .OrderBy(id => id)
            .ToArray();

        return new RuntimeRecipeDependencyClosure(
            allIngredientIds.OrderBy(id => id).ToArray(),
            allFoodIds.OrderBy(id => id).ToArray(),
            dependencyIngredientIds,
            dependencyFoodIds,
            FreezeSources(ingredientSources, dependencyIngredientIds),
            FreezeSources(foodSources, dependencyFoodIds));
    }

    private static IReadOnlyList<int> ReadMappedIds(IReadOnlyList<int> ids, string mappingName)
    {
        if (ids.Count == 0 || ids.Count > MaxCatalogItems)
        {
            throw new InvalidOperationException(
                $"{mappingName} returned invalid projected count {ids.Count}.");
        }

        var result = new HashSet<int>();
        foreach (var id in ids)
        {
            if (id < 0)
            {
                throw new InvalidOperationException(
                    $"{mappingName} projected negative content ID {id}.");
            }

            if (!result.Add(id))
            {
                throw new InvalidOperationException(
                    $"{mappingName} projected duplicate content ID {id}.");
            }
        }

        return result.OrderBy(id => id).ToArray();
    }

    private static void AddDependency(
        ISet<int> targetIds,
        IDictionary<int, HashSet<int>> sourceRecipeIds,
        int targetId,
        int recipeId,
        string targetType)
    {
        if (targetIds.Add(targetId) && targetIds.Count > MaxCatalogItems)
        {
            throw new InvalidOperationException(
                $"Mapped recipes expanded the {targetType} catalog beyond the {MaxCatalogItems}-item limit.");
        }

        if (!sourceRecipeIds.TryGetValue(targetId, out var sources))
        {
            sources = new HashSet<int>();
            sourceRecipeIds.Add(targetId, sources);
        }

        sources.Add(recipeId);
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> FreezeSources(
        IReadOnlyDictionary<int, HashSet<int>> sourceRecipeIds,
        IEnumerable<int> dependencyIds)
    {
        return dependencyIds.ToDictionary(
            id => id,
            id => (IReadOnlyList<int>)sourceRecipeIds[id].OrderBy(recipeId => recipeId).ToArray());
    }
}
