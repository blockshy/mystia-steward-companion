using System.Collections.ObjectModel;

namespace MystiaStewardCompanion.Save;

internal enum RuntimeUiTargetKind
{
    Rare,
    Normal,
}

[Flags]
internal enum RuntimeUiTargetKinds
{
    None = 0,
    Rare = 1,
    Normal = 2,
}

/// <summary>
/// One exact order-owned UI target. Instances contain managed scalar data only and are safe to
/// publish from the local API thread.
/// </summary>
internal sealed class RuntimeUiTargetSnapshot
{
    private const int MaxIngredientIds = 12;
    private const int MaxExtraIngredientIds = 5;

    private readonly int[] _ingredientIds;
    private readonly int[] _extraIngredientIds;
    private readonly ReadOnlyCollection<int> _readOnlyIngredientIds;
    private readonly ReadOnlyCollection<int> _readOnlyExtraIngredientIds;

    public RuntimeUiTargetSnapshot(
        RuntimeUiTargetKind kind,
        RuntimeTargetHighlightColor color,
        bool listPinningEnabled,
        bool recipeVariantEnabled,
        bool cookerHighlightEnabled,
        bool seatHighlightEnabled,
        bool orderHighlightEnabled,
        string orderTraceId,
        string orderKey,
        long orderLifecycleSequence,
        int deskCode,
        int recipeId,
        IEnumerable<int> ingredientIds,
        IEnumerable<int> extraIngredientIds,
        int beverageId,
        int cookerTypeId,
        string targetRevision)
    {
        ArgumentNullException.ThrowIfNull(orderTraceId);
        ArgumentNullException.ThrowIfNull(orderKey);
        ArgumentNullException.ThrowIfNull(ingredientIds);
        ArgumentNullException.ThrowIfNull(extraIngredientIds);
        ArgumentNullException.ThrowIfNull(targetRevision);

        var normalizedIngredientIds = CopyExactIds(
            ingredientIds,
            MaxIngredientIds,
            preserveOrder: false,
            requireUnique: true,
            nameof(ingredientIds));
        var normalizedExtraIngredientIds = CopyExactIds(
            extraIngredientIds,
            MaxExtraIngredientIds,
            preserveOrder: true,
            requireUnique: false,
            nameof(extraIngredientIds));

        if (kind is not RuntimeUiTargetKind.Rare and not RuntimeUiTargetKind.Normal)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "UI target kind is not supported.");
        }
        if (!listPinningEnabled
            && !recipeVariantEnabled
            && !cookerHighlightEnabled
            && !seatHighlightEnabled
            && !orderHighlightEnabled)
        {
            throw new ArgumentException("A UI target must enable at least one target-owned feature.");
        }
        if (recipeVariantEnabled && !listPinningEnabled)
        {
            throw new ArgumentException(
                "A target recipe variant requires list pinning for the same target.",
                nameof(recipeVariantEnabled));
        }
        if (!RuntimeOrderTraceIdService.TryNormalizeTargetTraceId(
                kind,
                orderTraceId,
                enabled: true,
                out var normalizedTraceId,
                out var traceFailure)
            || !string.Equals(normalizedTraceId, orderTraceId, StringComparison.Ordinal))
        {
            throw new ArgumentException(traceFailure, nameof(orderTraceId));
        }
        if (kind == RuntimeUiTargetKind.Rare && orderKey.Length != 0)
        {
            throw new ArgumentException("A rare UI target must not carry an order key.", nameof(orderKey));
        }
        if (kind == RuntimeUiTargetKind.Normal && !IsExactNormalOrderKey(orderKey))
        {
            throw new ArgumentException(
                "A normal UI target order key must match ptr: followed by 1-16 lowercase hexadecimal digits and identify a nonzero pointer.",
                nameof(orderKey));
        }
        if (orderLifecycleSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(orderLifecycleSequence), "Order lifecycle sequence must be positive.");
        }
        if (deskCode < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deskCode), "Desk code must be non-negative.");
        }
        ValidateOptionalId(recipeId, nameof(recipeId));
        ValidateOptionalId(beverageId, nameof(beverageId));
        ValidateOptionalId(cookerTypeId, nameof(cookerTypeId));
        if (targetRevision.Length == 0 || targetRevision.Length > 1024 || targetRevision.Any(char.IsControl))
        {
            throw new ArgumentException(
                "UI target revision must contain 1-1024 non-control characters.",
                nameof(targetRevision));
        }

        Kind = kind;
        Color = color;
        ListPinningEnabled = listPinningEnabled;
        RecipeVariantEnabled = recipeVariantEnabled;
        CookerHighlightEnabled = cookerHighlightEnabled;
        SeatHighlightEnabled = seatHighlightEnabled;
        OrderHighlightEnabled = orderHighlightEnabled;
        OrderTraceId = orderTraceId;
        OrderKey = orderKey;
        OrderLifecycleSequence = orderLifecycleSequence;
        DeskCode = deskCode;
        RecipeId = recipeId;
        BeverageId = beverageId;
        CookerTypeId = cookerTypeId;
        TargetRevision = targetRevision;
        _ingredientIds = normalizedIngredientIds;
        _extraIngredientIds = normalizedExtraIngredientIds;
        _readOnlyIngredientIds = Array.AsReadOnly(_ingredientIds);
        _readOnlyExtraIngredientIds = Array.AsReadOnly(_extraIngredientIds);
    }

    public RuntimeUiTargetKind Kind { get; }

    public RuntimeUiTargetKinds Claim => Kind == RuntimeUiTargetKind.Rare
        ? RuntimeUiTargetKinds.Rare
        : RuntimeUiTargetKinds.Normal;

    public RuntimeTargetHighlightColor Color { get; }

    public bool ListPinningEnabled { get; }

    public bool RecipeVariantEnabled { get; }

    public bool CookerHighlightEnabled { get; }

    public bool SeatHighlightEnabled { get; }

    public bool OrderHighlightEnabled { get; }

    public string OrderTraceId { get; }

    public string OrderKey { get; }

    public long OrderLifecycleSequence { get; }

    public int DeskCode { get; }

    public int RecipeId { get; }

    public IReadOnlyList<int> IngredientIds => _readOnlyIngredientIds;

    public IReadOnlyList<int> ExtraIngredientIds => _readOnlyExtraIngredientIds;

    public int BeverageId { get; }

    public int CookerTypeId { get; }

    public string TargetRevision { get; }

    public bool ContainsIngredient(int ingredientId)
    {
        return Array.BinarySearch(_ingredientIds, ingredientId) >= 0;
    }

    public bool HasSameValues(RuntimeUiTargetSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Kind == other.Kind
            && Color == other.Color
            && ListPinningEnabled == other.ListPinningEnabled
            && RecipeVariantEnabled == other.RecipeVariantEnabled
            && CookerHighlightEnabled == other.CookerHighlightEnabled
            && SeatHighlightEnabled == other.SeatHighlightEnabled
            && OrderHighlightEnabled == other.OrderHighlightEnabled
            && string.Equals(OrderTraceId, other.OrderTraceId, StringComparison.Ordinal)
            && string.Equals(OrderKey, other.OrderKey, StringComparison.Ordinal)
            && OrderLifecycleSequence == other.OrderLifecycleSequence
            && DeskCode == other.DeskCode
            && RecipeId == other.RecipeId
            && BeverageId == other.BeverageId
            && CookerTypeId == other.CookerTypeId
            && string.Equals(TargetRevision, other.TargetRevision, StringComparison.Ordinal)
            && _ingredientIds.SequenceEqual(other._ingredientIds)
            && _extraIngredientIds.SequenceEqual(other._extraIngredientIds);
    }

    private static int[] CopyExactIds(
        IEnumerable<int> values,
        int maximumCount,
        bool preserveOrder,
        bool requireUnique,
        string parameterName)
    {
        var result = values.ToArray();
        if (result.Length > maximumCount)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"A UI target cannot contain more than {maximumCount} values.");
        }
        if (result.Any(value => value < 0))
        {
            throw new ArgumentException("UI target ids must be non-negative.", parameterName);
        }
        if (requireUnique && result.Distinct().Count() != result.Length)
        {
            throw new ArgumentException("UI target ids must be unique.", parameterName);
        }

        if (!preserveOrder) Array.Sort(result);
        return result;
    }

    private static bool IsExactNormalOrderKey(string value)
    {
        if (value.Length < 5 || value.Length > 20 || !value.StartsWith("ptr:", StringComparison.Ordinal))
        {
            return false;
        }

        var hasNonzeroDigit = false;
        for (var index = 4; index < value.Length; index += 1)
        {
            var character = value[index];
            if (character is >= '1' and <= '9' or >= 'a' and <= 'f') hasNonzeroDigit = true;
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        }
        return hasNonzeroDigit;
    }

    private static void ValidateOptionalId(int value, string parameterName)
    {
        if (value < -1)
        {
            throw new ArgumentOutOfRangeException(parameterName, "UI target ids use -1 as the only missing value.");
        }
    }
}

/// <summary>
/// Atomic rare/normal UI target publication. The collection is always ordered rare then normal
/// and contains at most one target of each kind.
/// </summary>
internal sealed class RuntimeUiTargetSetSnapshot
{
    private readonly RuntimeUiTargetSnapshot[] _targets;
    private readonly ReadOnlyCollection<RuntimeUiTargetSnapshot> _readOnlyTargets;

    public static readonly RuntimeUiTargetSetSnapshot Disabled = new(
        generation: 0,
        sessionGeneration: 0,
        Array.Empty<RuntimeUiTargetSnapshot>());

    public RuntimeUiTargetSetSnapshot(
        long generation,
        long sessionGeneration,
        IEnumerable<RuntimeUiTargetSnapshot> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var normalizedTargets = targets.OrderBy(target => target.Kind).ToArray();
        if (normalizedTargets.Length > 2
            || normalizedTargets.Select(target => target.Kind).Distinct().Count() != normalizedTargets.Length)
        {
            throw new ArgumentException(
                "A UI target publication accepts at most one rare and one normal target.",
                nameof(targets));
        }

        Generation = generation;
        SessionGeneration = sessionGeneration;
        _targets = normalizedTargets;
        _readOnlyTargets = Array.AsReadOnly(_targets);

        var rareColor = RuntimeTargetHighlightColor.DefaultRare;
        var normalColor = RuntimeTargetHighlightColor.DefaultNormal;
        foreach (var target in _targets)
        {
            if (target.Kind == RuntimeUiTargetKind.Rare) rareColor = target.Color;
            else normalColor = target.Color;
        }
        Palette = new RuntimeTargetHighlightPalette(rareColor, normalColor);
    }

    public long Generation { get; }

    public long SessionGeneration { get; }

    public IReadOnlyList<RuntimeUiTargetSnapshot> Targets => _readOnlyTargets;

    public RuntimeTargetHighlightPalette Palette { get; }

    public bool TryGetTarget(RuntimeUiTargetKind kind, out RuntimeUiTargetSnapshot target)
    {
        target = _targets.FirstOrDefault(candidate => candidate.Kind == kind)!;
        return target != null;
    }

    public RuntimeUiTargetKinds GetRecipeClaims(int recipeId)
    {
        return GetClaims(target => target.ListPinningEnabled && target.RecipeId == recipeId);
    }

    /// <summary>
    /// Returns claims that belong on the authoritative base-recipe row. Variant-enabled targets
    /// with actual extras are represented by their exact synthetic row instead.
    /// </summary>
    public RuntimeUiTargetKinds GetBaseRecipeClaims(int recipeId)
    {
        return GetClaims(target =>
            target.ListPinningEnabled
            && target.RecipeId == recipeId
            && (!target.RecipeVariantEnabled || target.ExtraIngredientIds.Count == 0));
    }

    public RuntimeUiTargetKinds GetRecipeVariantClaims(int recipeId)
    {
        return GetClaims(target =>
            target.ListPinningEnabled
            && target.RecipeId == recipeId
            && target.RecipeVariantEnabled
            && target.ExtraIngredientIds.Count > 0);
    }

    public bool HasRecipeVariants(int recipeId)
    {
        return GetRecipeVariantClaims(recipeId) != RuntimeUiTargetKinds.None;
    }

    public RuntimeUiTargetKinds GetIngredientClaims(int ingredientId)
    {
        return GetClaims(target => target.ListPinningEnabled && target.ContainsIngredient(ingredientId));
    }

    public RuntimeUiTargetKinds GetBeverageClaims(int beverageId)
    {
        return GetClaims(target => target.ListPinningEnabled && target.BeverageId == beverageId);
    }

    public RuntimeUiTargetKinds GetCookerClaims(int cookerTypeId)
    {
        return GetClaims(target => target.CookerHighlightEnabled && target.CookerTypeId == cookerTypeId);
    }

    public bool HasSameValues(
        long sessionGeneration,
        IReadOnlyList<RuntimeUiTargetSnapshot> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (SessionGeneration != sessionGeneration || _targets.Length != targets.Count)
        {
            return false;
        }

        for (var index = 0; index < _targets.Length; index += 1)
        {
            if (!_targets[index].HasSameValues(targets[index])) return false;
        }
        return true;
    }

    private RuntimeUiTargetKinds GetClaims(Func<RuntimeUiTargetSnapshot, bool> predicate)
    {
        var claims = RuntimeUiTargetKinds.None;
        foreach (var target in _targets)
        {
            if (predicate(target)) claims |= target.Claim;
        }
        return claims;
    }
}
