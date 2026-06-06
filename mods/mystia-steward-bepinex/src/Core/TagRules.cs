namespace MystiaSteward.Core;

public static class TagRules
{
    private static readonly (string Strong, string Weak)[] TagConflicts =
    {
        ("肉", "素"),
        ("重油", "清淡"),
        ("饱腹", "下酒"),
        ("大份", "小巧"),
        ("灼热", "凉爽"),
    };

    public static int CountConflictCancellations(
        IReadOnlyCollection<string> baseActiveTags,
        IReadOnlyCollection<string> addedTags,
        IReadOnlyCollection<string> customerDislikedTags)
    {
        var count = 0;
        foreach (var (strong, weak) in TagConflicts)
        {
            if (addedTags.Contains(strong)
                && baseActiveTags.Contains(weak)
                && customerDislikedTags.Contains(weak))
            {
                count++;
            }
        }

        return count;
    }

    public static bool CanCancelNegativeByConflict(
        IReadOnlyCollection<string> baseActiveTags,
        IReadOnlyCollection<string> addedTags,
        IReadOnlyCollection<string> customerDislikedTags)
    {
        return CountConflictCancellations(baseActiveTags, addedTags, customerDislikedTags) > 0;
    }

    public static TagConflictResult ResolveTagConflicts(IEnumerable<string> tags)
    {
        var list = tags.Distinct().ToList();
        var cancelled = new HashSet<string>();

        foreach (var (strong, weak) in TagConflicts)
        {
            if (list.Contains(strong) && list.Contains(weak))
            {
                cancelled.Add(weak);
            }
        }

        return new TagConflictResult(
            list.Where(t => !cancelled.Contains(t)).ToList(),
            cancelled.ToList());
    }

    public static List<string> GetDynamicTags(
        int recipePrice,
        int totalIngredientCount,
        string? popularFoodTag,
        string? popularHateFoodTag,
        IReadOnlyCollection<string> recipeTags,
        bool isFamousShop)
    {
        var dynamic = new List<string>();

        if (recipePrice < 20) dynamic.Add("实惠");
        if (recipePrice > 60) dynamic.Add("昂贵");
        if (totalIngredientCount >= 5) dynamic.Add("大份");
        if (isFamousShop && recipeTags.Contains("招牌")) dynamic.Add("流行喜爱");
        if (!string.IsNullOrWhiteSpace(popularFoodTag) && recipeTags.Contains(popularFoodTag)) dynamic.Add("流行喜爱");
        if (!string.IsNullOrWhiteSpace(popularHateFoodTag) && recipeTags.Contains(popularHateFoodTag)) dynamic.Add("流行厌恶");

        return dynamic;
    }

    public static bool HasForbiddenTag(
        IReadOnlyCollection<string> ingredientTags,
        IReadOnlyCollection<string> recipeNegativeTags)
    {
        return ingredientTags.Any(recipeNegativeTags.Contains);
    }

    public static List<string> MergeAllTags(
        IEnumerable<string> recipePositiveTags,
        IEnumerable<IEnumerable<string>> extraIngredientTags,
        IEnumerable<string> dynamicTags)
    {
        var tags = new HashSet<string>();
        foreach (var tag in recipePositiveTags) tags.Add(tag);
        foreach (var group in extraIngredientTags)
        {
            foreach (var tag in group) tags.Add(tag);
        }

        foreach (var tag in dynamicTags) tags.Add(tag);
        return tags.ToList();
    }

    public static int ScoreFoodForRare(
        IEnumerable<string> activeTags,
        IReadOnlyCollection<string> customerPreferredTags,
        IReadOnlyCollection<string> customerDislikedTags)
    {
        var score = 0;
        foreach (var tag in activeTags)
        {
            if (customerPreferredTags.Contains(tag)) score++;
            if (customerDislikedTags.Contains(tag)) score--;
        }

        return score;
    }

    public static Rating GetRating(
        int foodScore,
        int beverageScore,
        bool meetsRequiredFood,
        bool meetsRequiredBeverage)
    {
        var total = foodScore + beverageScore;
        if (total >= 4 && meetsRequiredFood && meetsRequiredBeverage) return Rating.ExGood;
        if (total >= 3 && (meetsRequiredFood || meetsRequiredBeverage)) return Rating.Good;
        if (total >= 2 && (meetsRequiredFood || meetsRequiredBeverage)) return Rating.Normal;
        if (total >= 1) return Rating.Bad;
        return Rating.ExBad;
    }
}

public sealed record TagConflictResult(List<string> ActiveTags, List<string> CancelledTags);
