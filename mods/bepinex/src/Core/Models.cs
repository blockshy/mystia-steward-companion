using System.Text.Json.Serialization;

namespace MystiaStewardCompanion.Core;

public static class PlaceNames
{
    public static readonly string[] All =
    {
        "妖怪兽道", "人间之里", "博丽神社", "红魔馆", "迷途竹林",
        "魔法森林", "妖怪之山", "旧地狱", "地灵殿", "命莲寺",
        "神灵庙", "太阳花田", "辉针城", "月之都", "魔界",
    };
}

public sealed class Recipe
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public string Name { get; set; } = "";
    public List<string> Ingredients { get; set; } = new();
    public List<string> PositiveTags { get; set; } = new();
    public List<string> NegativeTags { get; set; } = new();
    public string Cooker { get; set; } = "";
    public int Price { get; set; }
}

public sealed class Ingredient
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public int Price { get; set; }
}

public sealed class Beverage
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public int Price { get; set; }
}

public sealed class RuntimeDataCatalog
{
    public bool IsComplete { get; init; }
    public string Source { get; init; } = "";
    public string Status { get; init; } = "";
    public List<Recipe> Recipes { get; init; } = new();
    public List<Ingredient> Ingredients { get; init; } = new();
    public List<Beverage> Beverages { get; init; } = new();
    public List<NormalCustomer> NormalCustomers { get; init; } = new();
    public List<RareCustomer> RareCustomers { get; init; } = new();
    public Dictionary<string, string> FoodTagIdMap { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> BeverageTagIdMap { get; init; } = new(StringComparer.Ordinal);
    public List<TagPriorityRule> TagPriorityRules { get; init; } = new();

    public static RuntimeDataCatalog Empty(string status)
    {
        return new RuntimeDataCatalog
        {
            IsComplete = false,
            Status = status,
        };
    }
}

public sealed class TagPriorityRule
{
    public int Id { get; init; }
    public List<int> TagIds { get; init; } = new();
    public List<string> Tags { get; init; } = new();
}

public sealed class PlacedCookerInfo
{
    public int ControllerIndex { get; init; }
    public List<int> TypeIds { get; init; } = new();
    public List<string> TypeNames { get; init; } = new();
    public string Name { get; init; } = "";
    public bool IsOpen { get; init; }
    public string Source { get; init; } = "";
}

public sealed class NormalCustomer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Places { get; set; } = new();
    public List<string> PositiveTags { get; set; } = new();
    public List<string> BeverageTags { get; set; } = new();
}

public sealed class RareCustomer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Places { get; set; } = new();
    public List<string> PositiveTags { get; set; } = new();
    public List<string> NegativeTags { get; set; } = new();
    public List<string> BeverageTags { get; set; } = new();
}

public sealed class RuntimeRareCustomer
{
    public int Id { get; init; }
    public string RuntimeStringId { get; init; } = "";
    public string Name { get; init; } = "";
    public List<string> Places { get; init; } = new();
    public List<string> PositiveTags { get; init; } = new();
    public List<string> NegativeTags { get; init; } = new();
    public List<string> BeverageTags { get; init; } = new();
    public string Source { get; init; } = "";

    public RareCustomer ToRareCustomer()
    {
        return new RareCustomer
        {
            Id = Id,
            Name = Name,
            Places = Places.ToList(),
            PositiveTags = PositiveTags.ToList(),
            NegativeTags = NegativeTags.ToList(),
            BeverageTags = BeverageTags.ToList(),
        };
    }
}

public sealed class NightBusinessOrder
{
    public int DeskCode { get; init; }
    public int? GuestId { get; init; }
    public string GuestName { get; init; } = "";
    public int FoodTagId { get; init; }
    public string FoodTag { get; init; } = "";
    public int BeverageTagId { get; init; }
    public string BeverageTag { get; init; } = "";
    public string Source { get; init; } = "";
    public DateTime? FirstSeenAtUtc { get; init; }
    public DateTime? LastSeenAtUtc { get; init; }
    public bool IsFreeOrder { get; init; }
    public bool HasServedFood { get; init; }
    public bool HasServedBeverage { get; init; }
}

public sealed class NightBusinessGuest
{
    public int DeskCode { get; init; }
    public int? GuestId { get; init; }
    public string GuestName { get; init; } = "";
    public string Source { get; init; } = "";
    public int? Fund { get; init; }
    public int? BaseFundCarry { get; init; }
    public int? MaxFundCarry { get; init; }
    public int? ExtraFundByBuff { get; init; }
    public bool? WillPayMoney { get; init; }
}

public sealed class NightBusinessContext
{
    public string? Place { get; init; }
    public string? PlaceLabel { get; init; }
    public List<NightBusinessGuest> ActiveRareGuests { get; init; } = new();
    public List<NightBusinessOrder> Orders { get; init; } = new();
    public string Source { get; init; } = "";
    public string? Error { get; init; }
}

public sealed class RuntimeMissionInfo
{
    public string Label { get; init; } = "";
    public string Title { get; init; } = "";
    public string CharacterLabel { get; init; } = "";
    public string CharacterName { get; init; } = "";
    public List<string> Places { get; init; } = new();
    public string Source { get; init; } = "";
    public string Status { get; init; } = "available";
    public bool Started { get; init; }
    public bool Finished { get; init; }
    public int? TargetRecipeId { get; init; }
    public string? TargetRecipeName { get; init; }
}

public sealed class RuntimeMissionServeTarget
{
    public int GuestId { get; init; }
    public string GuestName { get; init; } = "";
    public string GuestLabel { get; init; } = "";
    public string MissionLabel { get; init; } = "";
    public string MissionTitle { get; init; } = "";
    public int RecipeId { get; init; }
    public string RecipeName { get; init; } = "";
    public string Status { get; init; } = "tracking";
    public string Source { get; init; } = "";
}

public sealed class RuntimeMissionContext
{
    public List<RuntimeMissionInfo> AvailableMissions { get; init; } = new();
    public List<RuntimeMissionServeTarget> ServeTargets { get; init; } = new();
    public string Source { get; init; } = "";
    public string? Error { get; init; }
}

public sealed class NormalBusinessOrder
{
    public string OrderKey { get; init; } = "";
    public int DeskCode { get; init; }
    public string GuestName { get; init; } = "";
    public int FoodId { get; init; }
    public string FoodName { get; init; } = "";
    public int BeverageId { get; init; }
    public string BeverageName { get; init; } = "";
    public bool HasServedFood { get; init; }
    public bool HasServedBeverage { get; init; }
    public bool ReadyToEvaluate { get; init; }
    public bool HasEvaluated { get; init; }
    public bool ControllerAvailable { get; init; }
    public bool CanAutomate { get; init; } = true;
    public string ActionBlockReason { get; init; } = "";
    public DateTime? FirstSeenAtUtc { get; init; }
    public string Source { get; init; } = "";
}

public sealed class NormalBusinessContext
{
    public List<NormalBusinessOrder> Orders { get; init; } = new();
    public string Source { get; init; } = "";
    public string? Error { get; init; }
}

public sealed class ParsedSaveData
{
    public List<int> RecipeGameIds { get; init; } = new();
    public Dictionary<int, int> Ingredients { get; init; } = new();
    public Dictionary<int, int> Beverages { get; init; } = new();
    public int PlayerLevel { get; init; }
    public List<string> ActivatedDlc { get; init; } = new();
    public string? PopularFoodTag { get; init; }
    public string? PopularHateFoodTag { get; init; }
    public bool FamousShopEnabled { get; init; }
    public Dictionary<string, bool> CollabStatus { get; init; } = new();
}

public sealed class RecommendationState
{
    public HashSet<int> AvailableRecipeIds { get; } = new();
    public HashSet<int> AvailableBeverageIds { get; } = new();
    public HashSet<int> AvailableIngredientIds { get; } = new();
    public Dictionary<int, int> OwnedIngredientQty { get; } = new();
    public Dictionary<int, int> OwnedBeverageQty { get; } = new();
    public HashSet<int> PlacedCookerTypeIds { get; } = new();
    public List<PlacedCookerInfo> PlacedCookers { get; } = new();
    public string PlacedCookerStatus { get; set; } = "";
    public string? PopularFoodTag { get; set; }
    public string? PopularHateFoodTag { get; set; }
    public bool FamousShopEnabled { get; set; }

    public static RecommendationState AllAvailable(DataRepository repository)
    {
        var state = new RecommendationState();
        foreach (var recipe in repository.Recipes) state.AvailableRecipeIds.Add(recipe.Id);
        foreach (var beverage in repository.Beverages)
        {
            state.AvailableBeverageIds.Add(beverage.Id);
            state.OwnedBeverageQty[beverage.Id] = 99;
        }
        foreach (var ingredient in repository.Ingredients)
        {
            state.AvailableIngredientIds.Add(ingredient.Id);
            state.OwnedIngredientQty[ingredient.Id] = 99;
        }

        return state;
    }

    public static RecommendationState FromSave(DataRepository repository, ParsedSaveData save)
    {
        var state = new RecommendationState
        {
            PopularFoodTag = save.PopularFoodTag,
            PopularHateFoodTag = save.PopularHateFoodTag,
            FamousShopEnabled = save.FamousShopEnabled,
        };

        foreach (var gameId in save.RecipeGameIds)
        {
            if (repository.RecipeIdToId.TryGetValue(gameId, out var localId))
            {
                state.AvailableRecipeIds.Add(localId);
            }
        }

        foreach (var pair in save.Beverages)
        {
            state.AvailableBeverageIds.Add(pair.Key);
            state.OwnedBeverageQty[pair.Key] = pair.Value;
        }
        foreach (var pair in save.Ingredients)
        {
            state.AvailableIngredientIds.Add(pair.Key);
            state.OwnedIngredientQty[pair.Key] = pair.Value;
        }

        return state;
    }
}
