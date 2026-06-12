namespace MystiaStewardCompanion.LocalApi;

internal sealed class OrderPreparationRequest
{
    public string OrderKey { get; init; } = "";
    public int DeskCode { get; init; }
    public int? GuestId { get; init; }
    public string GuestName { get; init; } = "";
    public string FoodTag { get; init; } = "";
    public string BeverageTag { get; init; } = "";
    public int FoodId { get; init; } = -1;
    public int RecipeId { get; init; } = -1;
    public string RecipeName { get; init; } = "";
    public IReadOnlyList<int> ExtraIngredientIds { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> AcceptableFoodIds { get; init; } = Array.Empty<int>();
    public int TrayBacklogMinSeconds { get; init; }
    public int BeverageId { get; init; } = -1;
    public string BeverageName { get; init; } = "";
    public bool AutoTakeBeverage { get; init; }
    public bool AutoStartCooking { get; init; }
    public bool AutoCollectCooking { get; init; }
    public bool FavoritesOnly { get; init; }
    public bool StopOnError { get; init; }
    public bool RecipeFavorite { get; init; }
    public bool BeverageFavorite { get; init; }
}

internal sealed class OrderPreparationResult
{
    public bool Ok { get; set; }
    public bool Prepared { get; set; }
    public string? Error { get; set; }
    public OrderPreparationOrder Order { get; init; } = new();
    public int RecipeId { get; init; }
    public string RecipeName { get; init; } = "";
    public int BeverageId { get; init; }
    public string BeverageName { get; init; } = "";
    public bool ServedFood { get; set; }
    public bool ServedBeverage { get; set; }
    public bool CompletedOrder { get; set; }
    public List<OrderPreparationStep> Steps { get; } = new();
}

internal sealed class OrderPreparationOrder
{
    public int DeskCode { get; init; }
    public int? GuestId { get; init; }
    public string GuestName { get; init; } = "";
    public string FoodTag { get; init; } = "";
    public string BeverageTag { get; init; } = "";
}

internal sealed class OrderPreparationStep
{
    public string Name { get; init; } = "";
    public bool Ok { get; init; }
    public bool Skipped { get; init; }
    public string Message { get; init; } = "";
}
