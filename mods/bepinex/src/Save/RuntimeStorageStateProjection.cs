namespace MystiaStewardCompanion.Save;

/// <summary>按有界、去重后的目录编号读取库存与配方解锁状态，将运行时访问委托的结果投影为托管集合。</summary>
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

            // 零库存不占用结果项；保留 -1 哨兵，不能把该有效库存状态截断为零。
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
