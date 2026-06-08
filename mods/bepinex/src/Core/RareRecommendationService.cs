namespace MystiaStewardCompanion.Core;

public sealed class RareRecommendationService
{
    private const int AssumedBeverageScore = 1;
    private const bool AssumedBeverageMeets = true;
    private const int TargetFoodScore = 3;
    private const int MaxCandidates = 18;

    private readonly DataRepository _repository;

    public RareRecommendationService(DataRepository repository)
    {
        _repository = repository;
    }

    public List<RareRecipeResult> RankRecipes(
        RareCustomer customer,
        string requiredFoodTag,
        RecommendationState state,
        IReadOnlyCollection<int>? disabledIngredientIds = null,
        int maxExtraIngredients = 4)
    {
        var disabledIngredients = disabledIngredientIds ?? Array.Empty<int>();
        var results = new List<RareRecipeResult>();
        var usableIngredients = state.AvailableIngredientIds
            .Where(id => !disabledIngredients.Contains(id))
            .Select(id => _repository.IngredientsById.TryGetValue(id, out var ingredient) ? ingredient : null)
            .Where(ingredient => ingredient != null)
            .Cast<Ingredient>()
            .ToList();

        var customerPreferredTagSet = customer.PositiveTags.ToHashSet();
        var customerBannedIngredientIds = GetEasterIngredientIdsByEffect(customer.Id, RareEasterEffect.Ban);
        var customerPriorityIngredientIds = GetEasterIngredientIdsByEffect(customer.Id, RareEasterEffect.PriorityExGood);

        foreach (var recipe in _repository.Recipes)
        {
            if (!state.AvailableRecipeIds.Contains(recipe.Id)) continue;

            var hasUnavailableBaseIngredient = recipe.Ingredients.Any(name =>
            {
                if (!_repository.IngredientsByName.TryGetValue(name, out var ingredient)) return true;
                if (!state.AvailableIngredientIds.Contains(ingredient.Id)) return true;
                return disabledIngredients.Contains(ingredient.Id);
            });
            if (hasUnavailableBaseIngredient) continue;

            var baseIngredientIds = recipe.Ingredients
                .Select(name => _repository.IngredientsByName.TryGetValue(name, out var ingredient) ? ingredient.Id : -1)
                .Where(id => id >= 0)
                .ToHashSet();

            var baseEasterEffect = ResolveRareEasterEffect(customer.Id, recipe.Id, baseIngredientIds);
            if (baseEasterEffect.Effect == RareEasterEffect.Ban) continue;

            var extraSlots = Math.Max(0, Math.Min(5 - recipe.Ingredients.Count, maxExtraIngredients));
            var baseEval = EvaluateCombo(recipe, new List<Ingredient>(), customer, requiredFoodTag, state);

            var allCandidates = usableIngredients
                .Where(ingredient => !TagRules.HasForbiddenTag(ingredient.Tags, recipe.NegativeTags))
                .Where(ingredient => !customerBannedIngredientIds.Contains(ingredient.Id))
                .ToList();

            var relevant = new List<Ingredient>();
            foreach (var candidate in allCandidates)
            {
                var matchesPreferredOrRequired = candidate.Tags.Any(tag =>
                    customerPreferredTagSet.Contains(tag) || tag == requiredFoodTag);
                var canCancelNegative = TagRules.CanCancelNegativeByConflict(
                    baseEval.ActiveTags,
                    candidate.Tags,
                    customer.NegativeTags);
                var forcePriorityCandidate = customerPriorityIngredientIds.Contains(candidate.Id);

                if (matchesPreferredOrRequired || canCancelNegative || forcePriorityCandidate)
                {
                    relevant.Add(candidate);
                }
            }

            var baseIngredientNames = recipe.Ingredients.ToHashSet();
            var candidates = relevant
                .OrderByDescending(i => i.Tags.Contains(requiredFoodTag) ? 1 : 0)
                .ThenByDescending(i => i.Tags.Count(customerPreferredTagSet.Contains))
                .ThenByDescending(i => TagRules.CountConflictCancellations(baseEval.ActiveTags, i.Tags, customer.NegativeTags))
                .ThenByDescending(i => baseIngredientNames.Contains(i.Name) ? 1 : 0)
                .ThenByDescending(i => GetIngredientOwnedQty(i.Id, state.OwnedIngredientQty))
                .ThenBy(i => i.Price)
                .ThenBy(i => i.Id)
                .Take(MaxCandidates)
                .ToList();

            var bestCombo = (List<Ingredient>?)null;
            var bestEval = baseEval;
            var bestReason = IngredientTagReasonResult.Empty;
            var bestEasterEffect = baseEasterEffect.Effect == RareEasterEffect.PriorityExGood
                ? baseEasterEffect
                : ResolvedRareEasterEffect.Empty;

            if (baseEval.FoodScore >= TargetFoodScore && baseEval.MeetsRequiredFood)
            {
                bestCombo = new List<Ingredient>();
            }
            else if (extraSlots > 0)
            {
                var requiredFallbackCombo = baseEval.MeetsRequiredFood ? new List<Ingredient>() : null;
                ComboEvaluation? requiredFallbackEval = baseEval.MeetsRequiredFood ? baseEval : null;
                IngredientTagReasonResult? requiredFallbackReason = baseEval.MeetsRequiredFood
                    ? IngredientTagReasonResult.Empty
                    : null;
                var requiredFallbackCost = 0;
                var requiredFallbackEasterEffect =
                    baseEasterEffect.Effect == RareEasterEffect.PriorityExGood && baseEval.MeetsRequiredFood
                        ? baseEasterEffect
                        : ResolvedRareEasterEffect.Empty;

                for (var k = 1; k <= Math.Min(extraSlots, candidates.Count); k++)
                {
                    List<Ingredient>? bestComboForK = null;
                    ComboEvaluation? bestEvalForK = null;
                    IngredientTagReasonResult? bestReasonForK = null;
                    var bestCostForK = int.MaxValue;

                    List<Ingredient>? bestPriorityComboForK = null;
                    ComboEvaluation? bestPriorityEvalForK = null;
                    IngredientTagReasonResult? bestPriorityReasonForK = null;
                    var bestPriorityCostForK = int.MaxValue;
                    ResolvedRareEasterEffect? bestPriorityEasterForK = null;

                    foreach (var combo in EnumerateCombinations(candidates, k))
                    {
                        var eval = EvaluateCombo(recipe, combo, customer, requiredFoodTag, state);
                        var cost = combo.Sum(i => i.Price);
                        var reason = BuildExtraIngredientTagReasons(
                            combo,
                            baseEval.ActiveTags,
                            eval.ActiveTags,
                            customer.PositiveTags,
                            requiredFoodTag,
                            baseIngredientNames,
                            state.OwnedIngredientQty);

                        var comboIngredientIds = baseIngredientIds.ToHashSet();
                        foreach (var ingredient in combo) comboIngredientIds.Add(ingredient.Id);
                        var comboEasterEffect = ResolveRareEasterEffect(customer.Id, recipe.Id, comboIngredientIds);
                        if (comboEasterEffect.Effect == RareEasterEffect.Ban) continue;

                        if (eval.MeetsRequiredFood
                            && (requiredFallbackCombo == null
                                || requiredFallbackEval == null
                                || requiredFallbackReason == null
                                || ShouldReplaceRequiredFallback(
                                    requiredFallbackEval,
                                    requiredFallbackCombo,
                                    requiredFallbackReason,
                                    requiredFallbackCost,
                                    eval,
                                    combo,
                                    reason,
                                    cost)))
                        {
                            requiredFallbackCombo = combo;
                            requiredFallbackEval = eval;
                            requiredFallbackReason = reason;
                            requiredFallbackCost = cost;
                            requiredFallbackEasterEffect = comboEasterEffect.Effect == RareEasterEffect.PriorityExGood
                                ? comboEasterEffect
                                : baseEasterEffect.Effect == RareEasterEffect.PriorityExGood
                                    ? baseEasterEffect
                                    : ResolvedRareEasterEffect.Empty;
                        }

                        if (comboEasterEffect.Effect == RareEasterEffect.PriorityExGood
                            && eval.MeetsRequiredFood
                            && (bestPriorityComboForK == null
                                || IsReasonDataPreferred(reason, bestPriorityReasonForK, cost, bestPriorityCostForK)))
                        {
                            bestPriorityComboForK = combo;
                            bestPriorityEvalForK = eval;
                            bestPriorityReasonForK = reason;
                            bestPriorityCostForK = cost;
                            bestPriorityEasterForK = comboEasterEffect;
                        }

                        if (eval.FoodScore >= TargetFoodScore && eval.MeetsRequiredFood
                            && (bestComboForK == null || IsReasonDataPreferred(reason, bestReasonForK, cost, bestCostForK)))
                        {
                            bestComboForK = combo;
                            bestEvalForK = eval;
                            bestReasonForK = reason;
                            bestCostForK = cost;
                        }
                    }

                    if (bestPriorityComboForK != null
                        && bestPriorityEvalForK != null
                        && bestPriorityReasonForK != null
                        && bestPriorityEasterForK != null)
                    {
                        bestCombo = bestPriorityComboForK;
                        bestEval = bestPriorityEvalForK;
                        bestReason = bestPriorityReasonForK;
                        bestEasterEffect = bestPriorityEasterForK;
                        break;
                    }

                    if (bestComboForK != null && bestEvalForK != null && bestReasonForK != null)
                    {
                        bestCombo = bestComboForK;
                        bestEval = bestEvalForK;
                        bestReason = bestReasonForK;
                        break;
                    }
                }

                if (bestCombo == null && requiredFallbackCombo != null && requiredFallbackEval != null)
                {
                    bestCombo = requiredFallbackCombo;
                    bestEval = requiredFallbackEval;
                    bestReason = requiredFallbackReason ?? IngredientTagReasonResult.Empty;
                    bestEasterEffect = requiredFallbackEasterEffect;
                }
            }

            var selectedIngredients = bestCombo ?? new List<Ingredient>();
            var finalEval = bestCombo != null ? bestEval : baseEval;
            var finalFoodScore = finalEval.FoodScore;
            var rating = TagRules.GetRating(
                finalFoodScore,
                AssumedBeverageScore,
                finalEval.MeetsRequiredFood,
                AssumedBeverageMeets);

            if (bestEasterEffect.Effect == RareEasterEffect.PriorityExGood)
            {
                finalFoodScore = Math.Max(finalFoodScore, bestEasterEffect.ScoreFloor ?? finalFoodScore);
                rating = TagRules.GetRating(
                    finalFoodScore,
                    AssumedBeverageScore,
                    finalEval.MeetsRequiredFood,
                    AssumedBeverageMeets);
            }

            if (!finalEval.MeetsRequiredFood) continue;

            var baseCost = recipe.Ingredients.Sum(name =>
                _repository.IngredientsByName.TryGetValue(name, out var ingredient) ? ingredient.Price : 0);

            results.Add(new RareRecipeResult
            {
                Recipe = recipe,
                ExtraIngredients = selectedIngredients,
                ExtraIngredientReasonTags = selectedIngredients.Count > 0
                    ? bestReason.ReasonTagsByIngredient
                    : new Dictionary<int, List<string>>(),
                IsEasterPriority = bestEasterEffect.Effect == RareEasterEffect.PriorityExGood,
                IsEasterPinned = bestEasterEffect.Effect == RareEasterEffect.PriorityExGood && bestEasterEffect.PinOnQualified,
                IsEasterRecipeHighlight = bestEasterEffect.Effect == RareEasterEffect.PriorityExGood && bestEasterEffect.RecipeHighlight,
                EasterHighlightExtraIngredientIds = selectedIngredients
                    .Where(i => bestEasterEffect.IngredientHighlightIds.Contains(i.Id))
                    .Select(i => i.Id)
                    .ToList(),
                EasterReason = bestEasterEffect.Reason,
                EasterScoreFloor = bestEasterEffect.ScoreFloor,
                AllTags = finalEval.ActiveTags,
                CancelledTags = finalEval.CancelledTags,
                FoodScore = finalFoodScore,
                MeetsRequiredFood = finalEval.MeetsRequiredFood,
                Rating = rating,
                BaseCost = baseCost,
                ExtraCost = selectedIngredients.Sum(i => i.Price),
            });
        }

        return results
            .OrderByDescending(r => r.MeetsRequiredFood)
            .ThenByDescending(r => r.FoodScore)
            .ThenBy(r => r.ExtraIngredients.Count)
            .ThenBy(r => GetRecipeResourcePressure(r, state))
            .ThenByDescending(r => r.Recipe.Price)
            .ThenBy(r => r.ExtraCost)
            .ThenBy(r => r.Recipe.Id)
            .ToList();
    }

    public List<RareBeverageResult> RankBeverages(
        RareCustomer customer,
        string requiredBeverageTag,
        RecommendationState state)
    {
        return _repository.Beverages
            .Where(beverage => state.AvailableBeverageIds.Contains(beverage.Id))
            .Select(beverage =>
            {
                var matchedTags = beverage.Tags.Where(customer.BeverageTags.Contains).ToList();
                return new RareBeverageResult
                {
                    Beverage = beverage,
                    BevScore = matchedTags.Count,
                    MeetsRequiredBev = beverage.Tags.Contains(requiredBeverageTag),
                    MatchedTags = matchedTags,
                };
            })
            .Where(result => result.MeetsRequiredBev)
            .OrderByDescending(result => result.MeetsRequiredBev)
            .ThenByDescending(result => result.Beverage.Price)
            .ToList();
    }

    private ComboEvaluation EvaluateCombo(
        Recipe recipe,
        List<Ingredient> extraIngredients,
        RareCustomer customer,
        string requiredFoodTag,
        RecommendationState state)
    {
        var (activeTags, cancelledTags) = ResolveFinalFoodTags(recipe, extraIngredients, state);
        var foodScore = TagRules.ScoreFoodForRare(activeTags, customer.PositiveTags, customer.NegativeTags);
        var meetsRequiredFood = activeTags.Contains(requiredFoodTag);
        return new ComboEvaluation(foodScore, meetsRequiredFood, activeTags, cancelledTags);
    }

    private (List<string> ActiveTags, List<string> CancelledTags) ResolveFinalFoodTags(
        Recipe recipe,
        List<Ingredient> extraIngredients,
        RecommendationState state)
    {
        var totalIngredientCount = recipe.Ingredients.Count + extraIngredients.Count;
        var baseTags = new HashSet<string>(recipe.PositiveTags);
        foreach (var ingredient in extraIngredients)
        {
            foreach (var tag in ingredient.Tags) baseTags.Add(tag);
        }

        var dynamicTags = TagRules.GetDynamicTags(
            recipe.Price,
            totalIngredientCount,
            null,
            null,
            baseTags,
            state.FamousShopEnabled);

        var allTags = TagRules.MergeAllTags(
            recipe.PositiveTags,
            extraIngredients.Select(i => i.Tags),
            dynamicTags);

        var conflictResult = TagRules.ResolveTagConflicts(allTags);
        var finalTags = conflictResult.ActiveTags.ToHashSet();

        if (!string.IsNullOrWhiteSpace(state.PopularFoodTag) && conflictResult.ActiveTags.Contains(state.PopularFoodTag))
        {
            finalTags.Add("流行喜爱");
        }

        if (!string.IsNullOrWhiteSpace(state.PopularHateFoodTag) && conflictResult.ActiveTags.Contains(state.PopularHateFoodTag))
        {
            finalTags.Add("流行厌恶");
        }

        return (finalTags.ToList(), conflictResult.CancelledTags);
    }

    private static IEnumerable<List<Ingredient>> EnumerateCombinations(IReadOnlyList<Ingredient> items, int size)
    {
        if (size <= 0 || size > items.Count) yield break;

        var indices = Enumerable.Range(0, size).ToArray();
        while (true)
        {
            yield return indices.Select(index => items[index]).ToList();

            var i = size - 1;
            while (i >= 0 && indices[i] == items.Count - size + i) i--;
            if (i < 0) break;

            indices[i]++;
            for (var j = i + 1; j < size; j++) indices[j] = indices[j - 1] + 1;
        }
    }

    private static int GetIngredientOwnedQty(int ingredientId, IReadOnlyDictionary<int, int> ownedIngredientQty)
    {
        return ownedIngredientQty.TryGetValue(ingredientId, out var qty) ? qty : 0;
    }

    private int GetRecipeResourcePressure(RareRecipeResult result, RecommendationState state)
    {
        var basePressure = result.Recipe.Ingredients.Sum(name =>
            _repository.IngredientsByName.TryGetValue(name, out var ingredient)
                ? GetIngredientResourcePressure(ingredient, state)
                : 0);
        var extraPressure = result.ExtraIngredients.Sum(ingredient => GetIngredientResourcePressure(ingredient, state));

        return basePressure + extraPressure * 2;
    }

    private static int GetIngredientResourcePressure(Ingredient ingredient, RecommendationState state)
    {
        const int lowStockThreshold = 5;
        var qty = state.OwnedIngredientQty.TryGetValue(ingredient.Id, out var ownedQty)
            ? Math.Max(0, ownedQty)
            : 0;
        var stockPenalty = qty <= 0
            ? (lowStockThreshold + 1) * 100
            : Math.Max(0, lowStockThreshold + 1 - qty) * 100;

        return stockPenalty + ingredient.Price;
    }

    private static bool IsReasonDataPreferred(
        IngredientTagReasonResult next,
        IngredientTagReasonResult? previous,
        int nextCost,
        int previousCost)
    {
        if (previous == null) return true;
        if (next.AssignedBaseReuseScore != previous.AssignedBaseReuseScore)
        {
            return next.AssignedBaseReuseScore > previous.AssignedBaseReuseScore;
        }

        if (next.AssignedQtyScore != previous.AssignedQtyScore)
        {
            return next.AssignedQtyScore > previous.AssignedQtyScore;
        }

        if (next.AssignedPriceScore != previous.AssignedPriceScore)
        {
            return next.AssignedPriceScore < previous.AssignedPriceScore;
        }

        return nextCost < previousCost;
    }

    private static bool ShouldReplaceRequiredFallback(
        ComboEvaluation previousEval,
        List<Ingredient> previousCombo,
        IngredientTagReasonResult previousReason,
        int previousCost,
        ComboEvaluation nextEval,
        List<Ingredient> nextCombo,
        IngredientTagReasonResult nextReason,
        int nextCost)
    {
        if (nextEval.FoodScore != previousEval.FoodScore) return nextEval.FoodScore > previousEval.FoodScore;
        if (nextCombo.Count != previousCombo.Count) return nextCombo.Count < previousCombo.Count;
        if (nextReason.AssignedBaseReuseScore != previousReason.AssignedBaseReuseScore)
        {
            return nextReason.AssignedBaseReuseScore > previousReason.AssignedBaseReuseScore;
        }

        if (nextReason.AssignedQtyScore != previousReason.AssignedQtyScore)
        {
            return nextReason.AssignedQtyScore > previousReason.AssignedQtyScore;
        }

        if (nextReason.AssignedPriceScore != previousReason.AssignedPriceScore)
        {
            return nextReason.AssignedPriceScore < previousReason.AssignedPriceScore;
        }

        return nextCost < previousCost;
    }

    private static IngredientTagReasonResult BuildExtraIngredientTagReasons(
        List<Ingredient> selectedIngredients,
        List<string> baseActiveTags,
        List<string> finalActiveTags,
        List<string> customerPreferredTags,
        string requiredFoodTag,
        HashSet<string> baseIngredientNames,
        IReadOnlyDictionary<int, int> ownedIngredientQty)
    {
        var result = new IngredientTagReasonResult();
        var neededTags = new List<string>();

        if (!baseActiveTags.Contains(requiredFoodTag) && finalActiveTags.Contains(requiredFoodTag))
        {
            neededTags.Add(requiredFoodTag);
        }

        foreach (var tag in customerPreferredTags)
        {
            if (tag == requiredFoodTag) continue;
            if (!baseActiveTags.Contains(tag) && finalActiveTags.Contains(tag))
            {
                neededTags.Add(tag);
            }
        }

        foreach (var tag in neededTags)
        {
            var chosen = selectedIngredients
                .Where(ingredient => ingredient.Tags.Contains(tag))
                .OrderByDescending(ingredient => baseIngredientNames.Contains(ingredient.Name) ? 1 : 0)
                .ThenByDescending(ingredient => GetIngredientOwnedQty(ingredient.Id, ownedIngredientQty))
                .ThenBy(ingredient => ingredient.Price)
                .ThenBy(ingredient => ingredient.Id)
                .FirstOrDefault();

            if (chosen == null) continue;
            if (!result.ReasonTagsByIngredient.TryGetValue(chosen.Id, out var tags))
            {
                tags = new List<string>();
                result.ReasonTagsByIngredient[chosen.Id] = tags;
            }

            tags.Add(tag);
            if (baseIngredientNames.Contains(chosen.Name)) result.AssignedBaseReuseScore++;
            result.AssignedQtyScore += GetIngredientOwnedQty(chosen.Id, ownedIngredientQty);
            result.AssignedPriceScore += chosen.Price;
        }

        return result;
    }

    private static HashSet<int> GetEasterIngredientIdsByEffect(int customerId, RareEasterEffect effect)
    {
        return RareEasterRules
            .Where(rule => rule.Effect == effect && rule.CustomerIds.Contains(customerId) && rule.IngredientIds != null)
            .SelectMany(rule => rule.IngredientIds!)
            .ToHashSet();
    }

    private static ResolvedRareEasterEffect ResolveRareEasterEffect(
        int customerId,
        int recipeId,
        HashSet<int> ingredientIds)
    {
        string? priorityReason = null;
        int? priorityScoreFloor = null;
        var priorityPinOnQualified = false;
        var priorityRecipeHighlight = false;
        var ingredientHighlightIds = new HashSet<int>();

        foreach (var rule in RareEasterRules)
        {
            if (!rule.CustomerIds.Contains(customerId)) continue;
            if (rule.RecipeIds != null && !rule.RecipeIds.Contains(recipeId)) continue;
            if (rule.IngredientIds != null && !rule.IngredientIds.All(ingredientIds.Contains)) continue;

            if (rule.Effect == RareEasterEffect.Ban)
            {
                return new ResolvedRareEasterEffect(
                    RareEasterEffect.Ban,
                    rule.Reason,
                    null,
                    false,
                    false,
                    new List<int>());
            }

            priorityReason ??= rule.Reason;
            if (rule.ScoreFloor != null)
            {
                priorityScoreFloor = Math.Max(priorityScoreFloor ?? int.MinValue, rule.ScoreFloor.Value);
            }

            priorityPinOnQualified = priorityPinOnQualified || rule.PinOnQualified != false;
            priorityRecipeHighlight = priorityRecipeHighlight || rule.RecipeHighlight != false;
            if (rule.IngredientIds != null)
            {
                foreach (var id in rule.IngredientIds) ingredientHighlightIds.Add(id);
            }
        }

        return priorityReason == null
            ? ResolvedRareEasterEffect.Empty
            : new ResolvedRareEasterEffect(
                RareEasterEffect.PriorityExGood,
                priorityReason,
                priorityScoreFloor,
                priorityPinOnQualified,
                priorityRecipeHighlight,
                ingredientHighlightIds.ToList());
    }

    private static readonly RareEasterRule[] RareEasterRules =
    {
        new(new[] { 4008 }, new[] { 69 }, null, RareEasterEffect.PriorityExGood, "蕾米莉亚 × 猩红恶魔蛋糕（彩蛋）", 4, null, null),
        new(new[] { 1003 }, new[] { 35 }, null, RareEasterEffect.PriorityExGood, "饕餮尤魔 × 油豆腐（彩蛋）", 3, null, null),
        new(new[] { 10 }, new[] { 5002 }, null, RareEasterEffect.PriorityExGood, "雾雨魔理沙 × 牛肉鸳鸯火锅（彩蛋）", 4, null, null),
        new(new[] { 1000 }, null, new[] { 1000 }, RareEasterEffect.PriorityExGood, "河城荷取 × 黄瓜（彩蛋）", 3, false, false),
        new(new[] { 2006 }, new[] { 70 }, null, RareEasterEffect.Ban, "古明地恋 × 无意识妖怪慕斯（禁推）", null, null, null),
        new(new[] { 5001, 5002 }, new[] { 4001 }, null, RareEasterEffect.Ban, "绵月丰姬/绵月依姬 × 蜜桃红烧肉（禁推）", null, null, null),
        new(new[] { 1001 }, null, new[] { 5000 }, RareEasterEffect.Ban, "犬走椛 × 可可豆（禁推）", null, null, null),
    };
}

internal sealed record ComboEvaluation(
    int FoodScore,
    bool MeetsRequiredFood,
    List<string> ActiveTags,
    List<string> CancelledTags);

internal sealed class IngredientTagReasonResult
{
    public static IngredientTagReasonResult Empty => new();
    public Dictionary<int, List<string>> ReasonTagsByIngredient { get; } = new();
    public int AssignedBaseReuseScore { get; set; }
    public int AssignedQtyScore { get; set; }
    public int AssignedPriceScore { get; set; }
}

internal enum RareEasterEffect
{
    None,
    PriorityExGood,
    Ban,
}

internal sealed record RareEasterRule(
    int[] CustomerIds,
    int[]? RecipeIds,
    int[]? IngredientIds,
    RareEasterEffect Effect,
    string Reason,
    int? ScoreFloor,
    bool? PinOnQualified,
    bool? RecipeHighlight);

internal sealed record ResolvedRareEasterEffect(
    RareEasterEffect Effect,
    string? Reason,
    int? ScoreFloor,
    bool PinOnQualified,
    bool RecipeHighlight,
    List<int> IngredientHighlightIds)
{
    public static readonly ResolvedRareEasterEffect Empty = new(
        RareEasterEffect.None,
        null,
        null,
        false,
        false,
        new List<int>());
}
