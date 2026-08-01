namespace MystiaStewardCompanion.Save;

internal enum SpecialFoodTargetMatchMode
{
    Any,
    All,
}

internal sealed record SpecialFoodTargetPolicy(
    string ChallengeType,
    string Owner,
    long BusinessGeneration,
    IReadOnlyList<string> FoodTags,
    SpecialFoodTargetMatchMode MatchMode,
    string Signature)
{
    public const string AnyValue = "any";
    public const string AllValue = "all";

    public string MatchModeValue => MatchMode == SpecialFoodTargetMatchMode.All
        ? AllValue
        : AnyValue;

    public bool Matches(IReadOnlyList<string> actualTags)
    {
        if (FoodTags.Count == 0 || actualTags.Count == 0) return false;

        return MatchMode == SpecialFoodTargetMatchMode.All
            ? FoodTags.All(tag => actualTags.Contains(tag, StringComparer.Ordinal))
            : FoodTags.Any(tag => actualTags.Contains(tag, StringComparer.Ordinal));
    }

    public bool HasSameIdentity(SpecialFoodTargetPolicy other)
    {
        return string.Equals(Signature, other.Signature, StringComparison.Ordinal)
            && string.Equals(ChallengeType, other.ChallengeType, StringComparison.Ordinal)
            && string.Equals(Owner, other.Owner, StringComparison.Ordinal)
            && BusinessGeneration == other.BusinessGeneration
            && MatchMode == other.MatchMode
            && FoodTags.SequenceEqual(other.FoodTags, StringComparer.Ordinal);
    }

    public static bool TryCreate(
        string challengeType,
        string owner,
        long businessGeneration,
        IEnumerable<string>? foodTags,
        string matchMode,
        string suppliedSignature,
        out SpecialFoodTargetPolicy? policy,
        out string error)
    {
        policy = null;
        error = "";

        var normalizedChallenge = challengeType ?? "";
        var normalizedOwner = owner ?? "";
        var normalizedTags = NormalizeTags(foodTags);
        if (normalizedChallenge.Length == 0
            || !string.Equals(normalizedChallenge, normalizedChallenge.Trim(), StringComparison.Ordinal))
        {
            error = "special target challenge is missing or has edge whitespace";
            return false;
        }

        if (normalizedOwner.Length == 0
            || !string.Equals(normalizedOwner, normalizedOwner.Trim(), StringComparison.Ordinal))
        {
            error = "special target owner is missing or has edge whitespace";
            return false;
        }

        if (businessGeneration <= 0)
        {
            error = $"special target business generation is invalid: {businessGeneration}";
            return false;
        }

        if (normalizedTags.Count == 0)
        {
            error = "special target food tags are empty";
            return false;
        }

        if (!TryParseMatchMode(matchMode, out var parsedMatchMode))
        {
            error = $"special target match mode is invalid: {matchMode}";
            return false;
        }

        var expectedSignature = BuildSignature(
            normalizedChallenge,
            normalizedOwner,
            businessGeneration,
            parsedMatchMode,
            normalizedTags);
        if (!string.Equals(suppliedSignature, expectedSignature, StringComparison.Ordinal))
        {
            error = $"special target signature mismatch: expected={expectedSignature}; supplied={suppliedSignature}";
            return false;
        }

        policy = new SpecialFoodTargetPolicy(
            normalizedChallenge,
            normalizedOwner,
            businessGeneration,
            normalizedTags,
            parsedMatchMode,
            expectedSignature);
        return true;
    }

    public static SpecialFoodTargetPolicy CreateActive(
        string challengeType,
        string owner,
        long businessGeneration,
        IEnumerable<string> foodTags,
        SpecialFoodTargetMatchMode matchMode)
    {
        var normalizedTags = NormalizeTags(foodTags);
        var signature = BuildSignature(
            challengeType,
            owner,
            businessGeneration,
            matchMode,
            normalizedTags);
        return new SpecialFoodTargetPolicy(
            challengeType,
            owner,
            businessGeneration,
            normalizedTags,
            matchMode,
            signature);
    }

    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags)
    {
        return (tags ?? Array.Empty<string>())
            .Select(tag => tag?.Trim() ?? "")
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();
    }

    public static string BuildSignature(
        string challengeType,
        string owner,
        long businessGeneration,
        SpecialFoodTargetMatchMode matchMode,
        IReadOnlyList<string> normalizedTags)
    {
        var mode = matchMode == SpecialFoodTargetMatchMode.All ? AllValue : AnyValue;
        return $"{challengeType}|{owner}|generation:{businessGeneration}|match:{mode}|food:{string.Join(",", normalizedTags)}";
    }

    private static bool TryParseMatchMode(string value, out SpecialFoodTargetMatchMode matchMode)
    {
        if (string.Equals(value, AnyValue, StringComparison.Ordinal))
        {
            matchMode = SpecialFoodTargetMatchMode.Any;
            return true;
        }

        if (string.Equals(value, AllValue, StringComparison.Ordinal))
        {
            matchMode = SpecialFoodTargetMatchMode.All;
            return true;
        }

        matchMode = default;
        return false;
    }
}
