using MystiaSteward.Core;

namespace MystiaSteward.LocalApi;

internal sealed class LocalApiSnapshot
{
    public string PluginVersion { get; init; } = "";
    public DateTime CapturedAtUtc { get; init; }
    public string ActiveSceneName { get; init; } = "";
    public bool RuntimeLoaded { get; init; }
    public string Status { get; init; } = "";
    public string RuntimeSource { get; init; } = "";
    public string DataDirectory { get; init; } = "";
    public RecommendationStateSnapshot? RecommendationState { get; init; }
    public NightBusinessContext? NightBusiness { get; init; }
}

internal sealed class RecommendationStateSnapshot
{
    public List<int> AvailableRecipeIds { get; init; } = new();
    public List<int> AvailableBeverageIds { get; init; } = new();
    public List<int> AvailableIngredientIds { get; init; } = new();
    public Dictionary<int, int> OwnedIngredientQty { get; init; } = new();
    public Dictionary<int, int> OwnedBeverageQty { get; init; } = new();
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
            PopularFoodTag = state.PopularFoodTag,
            PopularHateFoodTag = state.PopularHateFoodTag,
            FamousShopEnabled = state.FamousShopEnabled,
        };
    }
}
