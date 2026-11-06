namespace MystiaStewardCompanion.Save;

internal enum CookControllerFoodResultKind
{
    CatalogFood,
    DarkCuisine,
}

/// <summary>厨具成品的托管值身份；黑暗料理使用独立种类，不能作为普通目录料理编号解析。</summary>
internal readonly record struct CookControllerFoodResultIdentity(
    int SellableType,
    int FoodId,
    CookControllerFoodResultKind Kind)
{
    public const string ExactManagedTypeName = "GameData.Core.Collections.Sellable";
    public const int FoodSellableType = 0;
    public const int DarkCuisineFoodId = -1;

    public bool IsDarkCuisine => Kind == CookControllerFoodResultKind.DarkCuisine;
}

/// <summary>同时验证精确 Sellable 类型、Food 分类和编号域，拒绝仅凭编号猜测成品种类。</summary>
internal static class CookControllerFoodResultIdentityPolicy
{
    public static bool TryCreate(
        string? managedTypeName,
        int sellableType,
        int foodId,
        out CookControllerFoodResultIdentity identity,
        out string diagnostic)
    {
        identity = default;
        if (!string.Equals(
                managedTypeName,
                CookControllerFoodResultIdentity.ExactManagedTypeName,
                StringComparison.Ordinal))
        {
            diagnostic =
                $"CookController.Result 必须是精确类型 {CookControllerFoodResultIdentity.ExactManagedTypeName}，实际为 {managedTypeName ?? "null"}";
            return false;
        }

        if (sellableType != CookControllerFoodResultIdentity.FoodSellableType)
        {
            diagnostic = $"Sellable.Type 必须为 Food(0)，实际为 {sellableType}";
            return false;
        }

        if (foodId < CookControllerFoodResultIdentity.DarkCuisineFoodId)
        {
            diagnostic = $"Sellable.Id 超出厨具料理成品域：{foodId}";
            return false;
        }

        var kind = foodId == CookControllerFoodResultIdentity.DarkCuisineFoodId
            ? CookControllerFoodResultKind.DarkCuisine
            : CookControllerFoodResultKind.CatalogFood;
        identity = new CookControllerFoodResultIdentity(sellableType, foodId, kind);
        diagnostic = "";
        return true;
    }
}
