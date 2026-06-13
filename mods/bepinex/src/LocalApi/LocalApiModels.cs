using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.LocalApi;

internal sealed class LocalApiSnapshot
{
    public string PluginVersion { get; init; } = "";
    public DateTime CapturedAtUtc { get; init; }
    public string ActiveSceneName { get; init; } = "";
    public string ActiveDayMapLabel { get; init; } = "";
    public string ActiveDayMapName { get; init; } = "";
    public bool RuntimeLoaded { get; init; }
    public string Status { get; init; } = "";
    public string RuntimeSource { get; init; } = "";
    public string RuntimeUiPinningStatus { get; init; } = "";
    public RecommendationStateSnapshot? RecommendationState { get; init; }
    public NightBusinessContext? NightBusiness { get; init; }
    public RuntimeMissionContext? RuntimeMissions { get; init; }
    public NormalBusinessContext? NormalBusiness { get; init; }
    public List<RuntimeRareCustomer> RuntimeRareCustomers { get; init; } = new();
    public RuntimeDataCatalog? RuntimeData { get; init; }
    public Dictionary<string, double> PerformanceMs { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class RecommendationStateSnapshot
{
    public List<int> AvailableRecipeIds { get; init; } = new();
    public List<int> AvailableBeverageIds { get; init; } = new();
    public List<int> AvailableIngredientIds { get; init; } = new();
    public List<int> AvailableRareCustomerIds { get; init; } = new();
    public Dictionary<int, int> OwnedIngredientQty { get; init; } = new();
    public Dictionary<int, int> OwnedBeverageQty { get; init; } = new();
    public List<int> PlacedCookerTypeIds { get; init; } = new();
    public List<PlacedCookerInfo> PlacedCookers { get; init; } = new();
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
            AvailableRareCustomerIds = state.AvailableRareCustomerIds.OrderBy(id => id).ToList(),
            OwnedIngredientQty = state.OwnedIngredientQty
                .OrderBy(item => item.Key)
                .ToDictionary(item => item.Key, item => item.Value),
            OwnedBeverageQty = state.OwnedBeverageQty
                .OrderBy(item => item.Key)
                .ToDictionary(item => item.Key, item => item.Value),
            PlacedCookerTypeIds = state.PlacedCookerTypeIds.OrderBy(id => id).ToList(),
            PlacedCookers = state.PlacedCookers
                .OrderBy(cooker => cooker.ControllerIndex)
                .ToList(),
            PlacedCookerStatus = state.PlacedCookerStatus,
            PopularFoodTag = state.PopularFoodTag,
            PopularHateFoodTag = state.PopularHateFoodTag,
            FamousShopEnabled = state.FamousShopEnabled,
        };
    }
}
