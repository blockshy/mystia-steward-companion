namespace MystiaStewardCompanion.LocalApi;

internal sealed class OrderPreparationRequest
{
    public long AutomationEpoch { get; init; }
    public string TraceId { get; init; } = "";
    public string OrderKey { get; init; } = "";
    public int DeskCode { get; init; }
    public int? GuestId { get; init; }
    public string GuestName { get; init; } = "";
    public string SpecialBusinessRole { get; init; } = "";
    public string FoodTag { get; init; } = "";
    public string BeverageTag { get; init; } = "";
    public int MatchFoodId { get; init; } = -1;
    public int MatchBeverageId { get; init; } = -1;
    public int FoodId { get; init; } = -1;
    public int RecipeId { get; init; } = -1;
    public string RecipeName { get; init; } = "";
    public IReadOnlyList<int> ExtraIngredientIds { get; init; } = Array.Empty<int>();
    public IReadOnlyList<string> PredictedFoodTags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> WackyTargetFoodTags { get; init; } = Array.Empty<string>();
    public string ExecutionMode { get; init; } = "";
    public string ExecutionReason { get; init; } = "";
    public int BeverageId { get; init; } = -1;
    public string BeverageName { get; init; } = "";
    public bool AutoTakeBeverage { get; init; }
    public bool AutoStartCooking { get; init; }
    public bool AutoCollectCooking { get; init; }
    public bool AutoDeliverFood { get; init; }
    public bool AutoCompleteOrder { get; init; }
    public bool RecipeFavoritesOnly { get; init; }
    public bool BeverageFavoritesOnly { get; init; }
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
    public OrderAutomationStageResult Automation { get; } = new();
    public List<OrderPreparationStep> Steps { get; } = new();
}

internal sealed class OrderAutomationStageResult
{
    public string Outcome { get; set; } = "";
    public string Stage { get; set; } = "";
    public string ReasonCode { get; set; } = "";
    public string JobId { get; set; } = "";
    public int RetryAfterMs { get; set; }
}

internal sealed class OrderPreparationOrder
{
    public string TraceId { get; init; } = "";
    public int DeskCode { get; init; }
    public int? GuestId { get; init; }
    public string GuestName { get; init; } = "";
    public string FoodTag { get; init; } = "";
    public string BeverageTag { get; init; } = "";
}

internal sealed class OrderPreparationStep
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Ok { get; init; }
    public bool Skipped { get; init; }
    public string Message { get; init; } = "";
}

internal sealed class AutomationSafetyBarrierAckResult
{
    public bool Ok { get; init; }
    public long Sequence { get; init; }
    public int AcknowledgedCount { get; init; }
    public IReadOnlyList<long> AcknowledgedSequences { get; init; } = Array.Empty<long>();
    public string Status { get; init; } = "";
    public string Error { get; init; } = "";
}
