namespace MystiaStewardCompanion.Save;

internal static class RuntimeStorageStateProjection
{
    private const int MaxCatalogItems = 4096;

    public static List<int> ReadAvailableRecipeIds(
        IEnumerable<int> recipeIds,
        Func<int, bool> haveRecipe)
    {
        ArgumentNullException.ThrowIfNull(recipeIds);
        ArgumentNullException.ThrowIfNull(haveRecipe);

        var result = new List<int>();
        foreach (var recipeId in ReadBoundedDistinctIds(recipeIds, "recipe"))
        {
            if (haveRecipe(recipeId)) result.Add(recipeId);
        }

        return result;
    }

    public static Dictionary<int, int> ReadIngredientQuantities(
        IEnumerable<int> ingredientIds,
        Func<int, int> getQuantity)
    {
        return ReadQuantities(ingredientIds, getQuantity, "ingredient");
    }

    public static Dictionary<int, int> ReadBeverageQuantities(
        IEnumerable<int> beverageIds,
        Func<int, int> getQuantity)
    {
        return ReadQuantities(beverageIds, getQuantity, "beverage");
    }

    private static Dictionary<int, int> ReadQuantities(
        IEnumerable<int> ids,
        Func<int, int> getQuantity,
        string itemType)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(getQuantity);

        var result = new Dictionary<int, int>();
        foreach (var id in ReadBoundedDistinctIds(ids, itemType))
        {
            var quantity = getQuantity(id);
            if (quantity < -1)
            {
                throw new InvalidOperationException(
                    $"Runtime {itemType} {id} returned invalid quantity {quantity}.");
            }

            if (quantity != 0) result.Add(id, quantity);
        }

        return result;
    }

    private static IReadOnlyList<int> ReadBoundedDistinctIds(
        IEnumerable<int> ids,
        string itemType)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var id in ids)
        {
            if (id < 0)
            {
                throw new InvalidOperationException($"Runtime {itemType} catalog contains negative ID {id}.");
            }

            if (!seen.Add(id)) continue;
            if (result.Count == MaxCatalogItems)
            {
                throw new InvalidOperationException(
                    $"Runtime {itemType} catalog exceeded the {MaxCatalogItems}-item limit.");
            }

            result.Add(id);
        }

        return result;
    }
}
