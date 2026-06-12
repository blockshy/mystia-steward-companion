namespace MystiaStewardCompanion.Core;

public sealed class NormalRecommendationService
{
    private readonly DataRepository _repository;

    public NormalRecommendationService(DataRepository repository)
    {
        _repository = repository;
    }

    public List<NormalRecipeResult> ComputeRecipes(
        string place,
        RecommendationState state,
        IReadOnlyCollection<int>? disabledIngredientIds = null)
    {
        var customers = _repository.GetNormalCustomersByPlace(place);
        var disabledIngredients = disabledIngredientIds ?? Array.Empty<int>();
        var results = new List<NormalRecipeResult>();

        foreach (var recipe in _repository.Recipes)
        {
            if (!state.AvailableRecipeIds.Contains(recipe.Id)) continue;

            var hasDisabledIngredient = recipe.Ingredients.Any(name =>
            {
                if (!_repository.IngredientsByName.TryGetValue(name, out var ingredient)) return true;
                if (!state.AvailableIngredientIds.Contains(ingredient.Id)) return true;
                return disabledIngredients.Contains(ingredient.Id);
            });
            if (hasDisabledIngredient) continue;

            var effectiveTags = GetRecipeEffectiveTags(recipe, state);
            var ingredientCost = GetIngredientCost(recipe);
            var customerScores = new List<CustomerScore>();
            var matchedTags = new HashSet<string>();

            foreach (var customer in customers)
            {
                var matched = customer.PositiveTags.Where(effectiveTags.Contains).ToList();
                customerScores.Add(new CustomerScore { Name = customer.Name, Score = matched.Count });
                foreach (var tag in matched) matchedTags.Add(tag);
            }

            results.Add(new NormalRecipeResult
            {
                Recipe = recipe,
                CustomerScores = customerScores,
                TotalCoverage = customerScores.Sum(c => c.Score),
                Profit = recipe.Price - ingredientCost,
                IngredientCost = ingredientCost,
                MatchedTags = matchedTags.ToList(),
            });
        }

        return results;
    }

    public List<NormalBeverageResult> ComputeBeverages(string place, RecommendationState state)
    {
        var customers = _repository.GetNormalCustomersByPlace(place);
        var results = new List<NormalBeverageResult>();

        foreach (var beverage in _repository.Beverages)
        {
            if (!state.AvailableBeverageIds.Contains(beverage.Id)) continue;

            var customerScores = new List<CustomerScore>();
            var matchedTags = new HashSet<string>();

            foreach (var customer in customers)
            {
                var matched = customer.BeverageTags.Where(beverage.Tags.Contains).ToList();
                customerScores.Add(new CustomerScore { Name = customer.Name, Score = matched.Count });
                foreach (var tag in matched) matchedTags.Add(tag);
            }

            results.Add(new NormalBeverageResult
            {
                Beverage = beverage,
                CustomerScores = customerScores,
                TotalCoverage = customerScores.Sum(c => c.Score),
                MatchedTags = matchedTags.ToList(),
            });
        }

        return results;
    }

    private List<string> GetRecipeEffectiveTags(Recipe recipe, RecommendationState state)
    {
        var tags = new HashSet<string>(recipe.PositiveTags);
        if (recipe.Price < 20) tags.Add("实惠");
        if (recipe.Price > 60) tags.Add("昂贵");
        if (state.FamousShopEnabled && recipe.PositiveTags.Contains("招牌")) tags.Add("流行喜爱");
        if (!string.IsNullOrWhiteSpace(state.PopularFoodTag) && recipe.PositiveTags.Contains(state.PopularFoodTag)) tags.Add("流行喜爱");
        if (!string.IsNullOrWhiteSpace(state.PopularHateFoodTag) && recipe.PositiveTags.Contains(state.PopularHateFoodTag)) tags.Add("流行厌恶");
        return tags.ToList();
    }

    private int GetIngredientCost(Recipe recipe)
    {
        var cost = 0;
        foreach (var name in recipe.Ingredients)
        {
            if (_repository.IngredientsByName.TryGetValue(name, out var ingredient))
            {
                cost += ingredient.Price;
            }
        }

        return cost;
    }
}
