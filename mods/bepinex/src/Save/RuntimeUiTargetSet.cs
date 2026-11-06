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
    Slot2 = 4, Slot3 = 8, Slot4 = 16, Slot5 = 32, Slot6 = 64, Slot7 = 128,
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
        string targetRevision,
        int colorSlot = -1)
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

        // 槽位代表精确订单，不代表客人种类。旧的双目标构造默认仅用于独立专项测试；
        // 业务发布始终传入稳定分配器给出的槽位，不能用列表索引取色。
        ColorSlot = colorSlot == -1 ? (kind == RuntimeUiTargetKind.Rare ? 0 : 1) : colorSlot;
        if (ColorSlot is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(colorSlot));
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
        IdentityKey = $"{Kind}:{OrderTraceId}:{OrderLifecycleSequence}:{OrderKey}";
        _ingredientIds = normalizedIngredientIds;
        _extraIngredientIds = normalizedExtraIngredientIds;
        _readOnlyIngredientIds = Array.AsReadOnly(_ingredientIds);
        _readOnlyExtraIngredientIds = Array.AsReadOnly(_extraIngredientIds);
    }

    public RuntimeUiTargetKind Kind { get; }

    /// <summary>发布内至多八个订单各占一位；同一实体的声明按位合并，撤回一单不会抹除其他单。</summary>
    public int ColorSlot { get; }
    public RuntimeUiTargetKinds Claim => (RuntimeUiTargetKinds)(1 << ColorSlot);
    public string IdentityKey { get; }

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
            && ColorSlot == other.ColorSlot
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
/// 多订单原子发布，按颜色槽位稳定排列。每个精确订单及槽位只能出现一次，最多八单。
/// </summary>
internal sealed class RuntimeUiTargetSetSnapshot
{
    private readonly RuntimeUiTargetSnapshot[] _targets;
    private readonly ReadOnlyCollection<RuntimeUiTargetSnapshot> _readOnlyTargets;

    // 列表、厨具每帧按实体查询声明，索引仅在不可变发布创建时构建。
    // 避免每张卡、每个资源在 LateUpdate 中分配捕获式委托或重复遍历全部订单。
    private readonly Dictionary<int, RuntimeUiTargetKinds> _recipeClaims = new();
    private readonly Dictionary<int, RuntimeUiTargetKinds> _baseRecipeClaims = new();
    private readonly Dictionary<int, RuntimeUiTargetKinds> _variantRecipeClaims = new();
    private readonly Dictionary<int, RuntimeUiTargetKinds> _ingredientClaims = new();
    private readonly Dictionary<int, RuntimeUiTargetKinds> _beverageClaims = new();
    private readonly Dictionary<int, RuntimeUiTargetKinds> _cookerClaims = new();

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
        var normalizedTargets = targets.OrderBy(target => target.ColorSlot).ToArray();
        if (normalizedTargets.Length > 8
            || normalizedTargets.Select(target => target.IdentityKey).Distinct().Count() != normalizedTargets.Length
            || normalizedTargets.Select(target => target.ColorSlot).Distinct().Count() != normalizedTargets.Length)
        {
            throw new ArgumentException(
                "UI 目标发布最多包含八个不同订单，且每个订单使用独立颜色槽位。",
                nameof(targets));
        }

        Generation = generation;
        SessionGeneration = sessionGeneration;
        _targets = normalizedTargets;
        _readOnlyTargets = Array.AsReadOnly(_targets);
        // 发布对象不可变，以下投影仅包含托管值；在发布时计算一次，避免每个 LateUpdate
        // 为同一组普客/稀客目标重新分配筛选数组和座位字典。新 generation 必定创建新投影，
        // 不缓存 Unity/IL2CPP 对象，也不推迟调用方的当前订单、会话和权限核验。
        OrderHighlightTargets = Array.AsReadOnly(_targets.Where(target => target.OrderHighlightEnabled).ToArray());
        var seatClaims = new Dictionary<int, RuntimeSeatHighlightClaim>();
        foreach (var target in _targets)
        {
            if (target.ListPinningEnabled)
            {
                AddClaim(_recipeClaims, target.RecipeId, target.Claim);
                AddClaim(target.RecipeVariantEnabled && target.ExtraIngredientIds.Count > 0
                    ? _variantRecipeClaims : _baseRecipeClaims, target.RecipeId, target.Claim);
                foreach (var ingredientId in target.IngredientIds) AddClaim(_ingredientClaims, ingredientId, target.Claim);
                AddClaim(_beverageClaims, target.BeverageId, target.Claim);
            }
            if (target.CookerHighlightEnabled) AddClaim(_cookerClaims, target.CookerTypeId, target.Claim);
            if (!target.SeatHighlightEnabled || target.DeskCode < 0) continue;
            var combined = target.Claim;
            if (seatClaims.TryGetValue(target.DeskCode, out var existing)) combined |= existing.Claims;
            seatClaims[target.DeskCode] = new RuntimeSeatHighlightClaim(
                Generation, SessionGeneration, target.DeskCode, combined);
        }
        SeatHighlightClaims = new ReadOnlyDictionary<int, RuntimeSeatHighlightClaim>(seatClaims);

        Palette = new RuntimeTargetHighlightPalette(_targets);

    }

    public long Generation { get; }

    public long SessionGeneration { get; }

    public IReadOnlyList<RuntimeUiTargetSnapshot> Targets => _readOnlyTargets;

    public IReadOnlyList<RuntimeUiTargetSnapshot> OrderHighlightTargets { get; }

    public IReadOnlyDictionary<int, RuntimeSeatHighlightClaim> SeatHighlightClaims { get; }

    public RuntimeTargetHighlightPalette Palette { get; }

    /// <summary>仅用精确订单键查询；同一种类存在多单时不能返回第一项冒充目标。</summary>
    public bool TryGetTarget(string identityKey, out RuntimeUiTargetSnapshot target)
    {
        // 八单上界使简单遍历足够；比较缓存标识，不生成 LINQ 闭包或拼接新字符串。
        foreach (var candidate in _targets)
        {
            if (!string.Equals(candidate.IdentityKey, identityKey, StringComparison.Ordinal)) continue;
            target = candidate;
            return true;
        }
        target = null!;
        return false;
    }

    public RuntimeUiTargetKinds GetRecipeClaims(int recipeId) => ReadClaim(_recipeClaims, recipeId);

    /// <summary>有实际加料的变体只占据合成行，不把其声明同时叠到权威基础配方行。</summary>
    public RuntimeUiTargetKinds GetBaseRecipeClaims(int recipeId) => ReadClaim(_baseRecipeClaims, recipeId);
    public RuntimeUiTargetKinds GetRecipeVariantClaims(int recipeId) => ReadClaim(_variantRecipeClaims, recipeId);
    public bool HasRecipeVariants(int recipeId) => GetRecipeVariantClaims(recipeId) != RuntimeUiTargetKinds.None;
    public RuntimeUiTargetKinds GetIngredientClaims(int ingredientId) => ReadClaim(_ingredientClaims, ingredientId);
    public RuntimeUiTargetKinds GetBeverageClaims(int beverageId) => ReadClaim(_beverageClaims, beverageId);
    public RuntimeUiTargetKinds GetCookerClaims(int cookerTypeId) => ReadClaim(_cookerClaims, cookerTypeId);

    /// <summary>
    /// 取得某个显示条目所有有效声明中最早的点单序号。普客与稀客共用生命周期计数器，
    /// 因而可跨类别排序；同物品只占一行，最早订单撤回后由剩余最早订单接续其优先级。
    /// 无声明返回最大值，使非目标条目保持游戏原有相对顺序；不改变辅助候选或颜色槽。
    /// </summary>
    internal long GetOrderPriority(RuntimeUiTargetKinds claims)
    {
        var earliest = long.MaxValue;
        foreach (var target in _targets)
        {
            if ((target.Claim & claims) == RuntimeUiTargetKinds.None) continue;
            earliest = Math.Min(earliest, target.OrderLifecycleSequence);
        }
        return earliest;
    }

    private static RuntimeUiTargetKinds ReadClaim(Dictionary<int, RuntimeUiTargetKinds> index, int id) =>
        index.TryGetValue(id, out var claim) ? claim : RuntimeUiTargetKinds.None;

    private static void AddClaim(Dictionary<int, RuntimeUiTargetKinds> index, int id, RuntimeUiTargetKinds claim)
    {
        // -1 表示未提供实体；不能令缺失的配方/酒水在查询负占位时得到真实订单声明。
        if (id < 0) return;
        index.TryGetValue(id, out var previous);
        index[id] = previous | claim;
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


}

/// <summary>一次目标发布内的纯托管座位声明；同桌普客/稀客合并标志，保留完整发布及经营代次。</summary>
internal readonly record struct RuntimeSeatHighlightClaim(
    long TargetSetGeneration,
    long SessionGeneration,
    int DeskCode,
    RuntimeUiTargetKinds Claims);
