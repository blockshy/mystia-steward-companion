using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.LocalApi;

internal sealed class LocalApiSnapshot
{
    public string PluginVersion { get; init; } = "";
    public string AutomationSessionId { get; init; } = "";
    public long NightBusinessGeneration { get; init; }
    public string NightBusinessLifecyclePhase { get; init; } = "Inactive";
    public string RuntimeNightBusinessLifecycleStatus { get; init; } = "";
    public string SnapshotSignature { get; set; } = "";
    public DateTime CapturedAtUtc { get; init; }
    public string ActiveSceneName { get; init; } = "";
    public string ActiveDayMapLabel { get; init; } = "";
    public string ActiveDayMapName { get; init; } = "";
    public bool RuntimeLoaded { get; init; }
    public long RuntimeDaySceneGeneration { get; init; }
    public bool RuntimeDaySceneReady { get; init; }
    public long MissionGeneration { get; init; }
    public string Status { get; init; } = "";
    public string RuntimeSource { get; init; } = "";
    public string RuntimeSceneReadinessStatus { get; init; } = "";
    public string RuntimeUiPinningStatus { get; init; } = "";
    public RecommendationStateSnapshot? RecommendationState { get; init; }
    public NightBusinessContext? NightBusiness { get; init; }
    public SpecialBusinessContext? SpecialBusiness { get; init; }
    public NormalBusinessContext? NormalBusiness { get; init; }
    public List<AutomationRuntimeEvent> AutomationEvents { get; init; } = new();
    public List<AutomationCookingJobSnapshot> AutomationCookingJobs { get; init; } = new();
    public bool RuntimeDataComplete { get; init; }
    public string RuntimeDataSource { get; init; } = "";
    public string RuntimeDataStatus { get; init; } = "";
    public string RuntimeDataSignature { get; init; } = "";
    public Dictionary<string, double> PerformanceMs { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class AutomationRuntimeEvent
{
    public long Sequence { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string Code { get; init; } = "";
    public string JobId { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string ReasonCode { get; init; } = "";
    public bool Terminal { get; init; }
    public long Generation { get; init; }
    public int CookerPhase { get; init; } = -1;
    public float CookerProgress { get; init; } = -1f;
    public string TraceId { get; init; } = "";
    public string TargetKind { get; init; } = "";
    public string OrderKey { get; init; } = "";
    public int DeskCode { get; init; } = -1;
    public int? GuestId { get; init; }
    public string GuestName { get; init; } = "";
    public int FoodId { get; init; } = -1;
    public string FoodName { get; init; } = "";
    public int BeverageId { get; init; } = -1;
    public string BeverageName { get; init; } = "";
    public int RecipeId { get; init; } = -1;
    public List<int> ExtraIngredientIds { get; init; } = new();
    public int ActualFoodId { get; init; } = -1;
    public List<string> TargetFoodTags { get; init; } = new();
    public List<string> ActualFoodTags { get; init; } = new();
    public string Message { get; init; } = "";
}

internal sealed class AutomationCookingJobSnapshot
{
    public string JobId { get; init; } = "";
    public string TargetKind { get; init; } = "";
    public string TraceId { get; init; } = "";
    public string OrderKey { get; init; } = "";
    public int DeskCode { get; init; } = -1;
    public int? GuestId { get; init; }
    public string GuestName { get; init; } = "";
    public int FoodId { get; init; } = -1;
    public string FoodName { get; init; } = "";
    public int RecipeId { get; init; } = -1;
    public string State { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string ReasonCode { get; init; } = "";
    public long SpecialTargetRevision { get; init; }
    public bool AllowYuumaControlledProgression { get; init; }
    public bool AutoDeliverFood { get; init; }
    public string ControllerId { get; init; } = "";
    public string ResultId { get; init; } = "";
    public long Generation { get; init; }
    public long ContentRevision { get; init; }
    public int CookerPhase { get; init; } = -1;
    public float CookerProgress { get; init; } = -1f;
    public int OwnershipObservationFailures { get; init; }
    public int RegressiveObservations { get; init; }
    public int DeliveryFailureAttempts { get; init; }
    public int ManualHandoffReadFailures { get; init; }
    public bool WarmerStoreCommitted { get; init; }
    public bool WarmerStoreCommitUncertain { get; init; }
    public int WarmerResetAttempts { get; init; }
    public bool FoodDeliveryCommitted { get; init; }
    public bool FoodDeliveryCommitUncertain { get; init; }
    public int FoodDeliveryCleanupAttempts { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime LastObservedAtUtc { get; init; }
    public DateTime LastProgressAtUtc { get; init; }
}

internal sealed class AutomationCommandCancellationResult
{
    public long CommandEpoch { get; init; }
    public int CancelledJobs { get; init; }
    public int CancelledCommands { get; init; }
}

internal sealed class LocalApiAutomationCancellationDto
{
    public bool Ok { get; init; }
    public string Status { get; init; } = "";
    public string? Error { get; init; }
    public long CommandEpoch { get; init; }
    public int CancelledJobs { get; init; }
    public int CancelledCommands { get; init; }
    public bool LeaseReleased { get; init; }
}

internal sealed class RecommendationStateSnapshot
{
    public List<int> AvailableRecipeIds { get; init; } = new();
    public List<int> AvailableBeverageIds { get; init; } = new();
    public List<int> AvailableIngredientIds { get; init; } = new();
    public Dictionary<int, int> OwnedIngredientQty { get; init; } = new();
    public Dictionary<int, int> OwnedBeverageQty { get; init; } = new();
    public List<int> PlacedCookerTypeIds { get; init; } = new();
    public List<PlacedCookerInfo> PlacedCookers { get; init; } = new();
    public bool PlacedCookerSnapshotComplete { get; init; }
    public int PlacedCookerControllerCount { get; init; }
    public int PlacedCookerEmptyControllerCount { get; init; }
    public int PlacedCookerLockedControllerCount { get; init; }
    public int PlacedCookerReadFailureCount { get; init; }
    public string PlacedCookerStatus { get; init; } = "";
    public string? PopularFoodTag { get; init; }
    public string? PopularHateFoodTag { get; init; }
    public bool FamousShopEnabled { get; init; }

    public static RecommendationStateSnapshot From(RecommendationState state)
    {
        return new RecommendationStateSnapshot
        {
            AvailableRecipeIds = state.AvailableRecipeIds.OrderBy(id => id).ToList(),
            AvailableBeverageIds = state.AvailableBeverageIds.OrderBy(id => id).ToList(),
            AvailableIngredientIds = state.AvailableIngredientIds.OrderBy(id => id).ToList(),
            OwnedIngredientQty = state.OwnedIngredientQty
                .OrderBy(item => item.Key)
                .ToDictionary(item => item.Key, item => item.Value),
            OwnedBeverageQty = state.OwnedBeverageQty
                .OrderBy(item => item.Key)
                .ToDictionary(item => item.Key, item => item.Value),
            PlacedCookerTypeIds = state.PlacedCookerTypeIds.OrderBy(id => id).ToList(),
            PlacedCookers = state.PlacedCookers
                .OrderBy(cooker => cooker.ControllerIndex)
                .Select(cooker => new PlacedCookerInfo
                {
                    ControllerIndex = cooker.ControllerIndex,
                    GridPosition = new CookerGridPosition
                    {
                        X = cooker.GridPosition.X,
                        Y = cooker.GridPosition.Y,
                        Z = cooker.GridPosition.Z,
                    },
                    ControllerIdentity = cooker.ControllerIdentity,
                    TypeIds = cooker.TypeIds.ToList(),
                    TypeNames = cooker.TypeNames.ToList(),
                    Name = cooker.Name,
                    ChallengeLocked = cooker.ChallengeLocked,
                    CouldOpen = cooker.CouldOpen,
                    AutomationAvailable = cooker.AutomationAvailable,
                    AutomationAvailability = cooker.AutomationAvailability,
                    AutomationAvailabilityDiagnostic = cooker.AutomationAvailabilityDiagnostic,
                    Source = cooker.Source,
                })
                .ToList(),
            PlacedCookerSnapshotComplete = state.PlacedCookerSnapshotComplete,
            PlacedCookerControllerCount = state.PlacedCookerControllerCount,
            PlacedCookerEmptyControllerCount = state.PlacedCookerEmptyControllerCount,
            PlacedCookerLockedControllerCount = state.PlacedCookerLockedControllerCount,
            PlacedCookerReadFailureCount = state.PlacedCookerReadFailureCount,
            PlacedCookerStatus = state.PlacedCookerStatus,
            PopularFoodTag = state.PopularFoodTag,
            PopularHateFoodTag = state.PopularHateFoodTag,
            FamousShopEnabled = state.FamousShopEnabled,
        };
    }
}
