using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.GameUi;

/// <summary>
/// 单一业务循环持有的订单颜色分配器。只保存经营代次及精确标识，没有游戏对象；
/// 订单仍存在时固定槽位，换桌/换推荐/切换辅助功能都不会重新分配颜色。
/// </summary>
public sealed class OrderHighlightAssignmentService
{
    // 声明位是跨类别的稳定身份存储，不等同于用户配置上限；收缩配色时不能压缩仍在用的槽位。
    public const int Capacity = 8;
    public const int MaximumColorsPerKind = 3;
    private long _generation;
    private readonly Dictionary<string, int> _slots = new(StringComparer.Ordinal);
    // 原生高亮使用全局八位声明槽；颜色序号在类别内唯一，一种颜色只辅助该类一笔订单。
    // 跨类别仍允许用户选择相同颜色，但声明槽独立，撤回一笔不会影响另一类的同色订单。
    private readonly Dictionary<string, int> _paletteSlots = new(StringComparer.Ordinal);

    /// <summary>
    /// 为尚未提交的业务帧建立轻量副本。候选计算或协议转换后帧可能已失效，调用方只有在
    /// 最终当前性检查通过时才用此副本替换已提交实例，避免过期名单污染后续只读颜色。
    /// 数据最多八笔，仅复制托管标识和整数，不复制方案树，也不包含游戏对象。
    /// </summary>
    public OrderHighlightAssignmentService Fork()
    {
        var next = new OrderHighlightAssignmentService { _generation = _generation };
        foreach (var pair in _slots) next._slots.Add(pair.Key, pair.Value);
        foreach (var pair in _paletteSlots) next._paletteSlots.Add(pair.Key, pair.Value);
        return next;
    }

    /// <summary>
    /// 只用本轮明确读到的强身份生成显示行；完整性仅决定是否能撤回消失订单的旧槽位。
    /// 未知、部分读取及重复身份都不能冒充“订单已完成”，但也不沿用旧行生成游戏目标。
    /// </summary>
    public JsonArray Project(long generation, JsonNode? rareOrders, JsonNode? normalOrders, JsonNode? preferences,
        bool rareOrdersReadComplete, bool normalOrdersReadComplete,
        IReadOnlyList<JsonObject>? rareCandidates = null, IReadOnlyList<JsonObject>? normalCandidates = null,
        bool rareCandidatesReadComplete = true)
    {
        if (_generation != generation) { _slots.Clear(); _paletteSlots.Clear(); _generation = generation; }
        if (generation <= 0) { _slots.Clear(); _paletteSlots.Clear(); return new JsonArray(); }
        var colors = ReadPalettes(preferences);
        // 即便调用方误将完整位置为 true，缺失数组、非对象或缺强身份仍不能证明空集。
        rareOrdersReadComplete &= IsCompleteOrderArray(rareOrders, "rare") && rareCandidatesReadComplete;
        normalOrdersReadComplete &= IsCompleteOrderArray(normalOrders, "normal");
        var all = Objects(rareOrders).Select(order => (Kind: "rare", Order: order))
            .Concat(Objects(normalOrders).Where(order => !Bool(order["hasEvaluated"]))
                .Select(order => (Kind: "normal", Order: order)))
            .Where(item => ExactKey(item.Kind, item.Order) != null).ToArray();
        var groups = all.GroupBy(item => ExactKey(item.Kind, item.Order)!, StringComparer.Ordinal).ToArray();
        // 歧义身份本轮不显示，但保留其已有槽位；否则暂时重复会在恢复后导致订单换色。
        var current = groups.Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
        var observedKeys = groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var currentKeys = current.Select(item => ExactKey(item.Kind, item.Order)!).ToHashSet(StringComparer.Ordinal);
        // 候选沿用游戏辅助的执行优先顺序，暂停、已送齐或无合法方案的订单不占辅助名额。
        // 未传入候选的纯值调用按点单时间选择；候选必须与本轮已确认的原始订单精确匹配。
        var selected = new List<(string Kind, JsonObject Order)>();
        foreach (var kind in new[] { "rare", "normal" })
        {
            var supplied = kind == "rare" ? rareCandidates : normalCandidates;
            var candidates = supplied ?? current.Where(item => item.Kind == kind)
                .OrderBy(item => Str(item.Order["firstSeenAtUtc"]), StringComparer.Ordinal)
                .ThenBy(item => ExactKey(kind, item.Order), StringComparer.Ordinal).Select(item => item.Order).ToArray();
            selected.AddRange(candidates.GroupBy(order => ExactKey(kind, order), StringComparer.Ordinal)
                .Where(group => group.Key != null && currentKeys.Contains(group.Key) && group.Count() == 1)
                .Take(colors[kind].Length).Select(group => (kind, group.Single())));
        }
        var selectedKeys = selected.Select(item => ExactKey(item.Kind, item.Order)!).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _slots.Keys.ToArray())
        {
            var kind = key.StartsWith("rare:", StringComparison.Ordinal) ? "rare" : "normal";
            var complete = kind == "rare" ? rareOrdersReadComplete : normalOrdersReadComplete;
            // 配色缩减是明确配置变更，越界颜色立即撤回；原始列表未知或身份歧义时则保留原槽，
            // 不能因为短暂少读一单便把其颜色交给另一单。完整帧才可释放未入选的辅助订单。
            var obsoleteColor = _paletteSlots[key] >= colors[kind].Length;
            var noLongerSelected = !selectedKeys.Contains(key)
                && (!observedKeys.Contains(key) || currentKeys.Contains(key));
            if (!obsoleteColor && !(complete && noLongerSelected)) continue;
            _slots.Remove(key);
            _paletteSlots.Remove(key);
        }
        foreach (var item in selected)
        {
            var key = ExactKey(item.Kind, item.Order)!;
            if (_slots.ContainsKey(key)) continue;
            var usedColors = _paletteSlots.Where(pair => pair.Key.StartsWith(item.Kind + ":", StringComparison.Ordinal))
                .Select(pair => pair.Value).ToHashSet();
            var availableColors = Enumerable.Range(0, colors[item.Kind].Length).Where(index => !usedColors.Contains(index)).ToArray();
            if (availableColors.Length == 0) continue;
            var preferred = item.Kind == "rare" ? 0 : 1;
            var available = Enumerable.Range(0, Capacity).Where(slot => !_slots.ContainsValue(slot)).ToArray();
            if (available.Length == 0) continue; // 超出已证实的八笔容量不复用声明槽或冒充其他订单。
            _slots[key] = available.Contains(preferred) ? preferred : available[0];
            _paletteSlots[key] = availableColors[0];
        }
        var result = new JsonArray();
        foreach (var item in selected)
        {
            if (!_slots.TryGetValue(ExactKey(item.Kind, item.Order)!, out var slot)) continue;
            result.Add(Object(("kind", item.Kind), ("businessGeneration", generation),
                ("traceId", item.Order["traceId"]), ("orderLifecycleSequence", item.Order["orderLifecycleSequence"]),
                ("orderKey", item.Kind == "normal" ? Str(item.Order["orderKey"]) : ""),
                ("colorSlot", slot), ("color", colors[item.Kind][_paletteSlots[ExactKey(item.Kind, item.Order)!]])));
        }
        return result;
    }

    /// <summary>宿主传入采集上下文，缺少明确完整位的历史/故障快照默认不能释放颜色。</summary>
    public JsonArray ProjectSnapshot(long generation, JsonNode? rareContext, JsonNode? normalContext, JsonNode? preferences,
        IReadOnlyList<JsonObject>? rareCandidates = null, IReadOnlyList<JsonObject>? normalCandidates = null,
        bool rareCandidatesReadComplete = true)
        => Project(generation, (rareContext as JsonObject)?["orders"], (normalContext as JsonObject)?["orders"], preferences,
            Bool((rareContext as JsonObject)?["ordersReadComplete"]), Bool((normalContext as JsonObject)?["ordersReadComplete"]),
            rareCandidates, normalCandidates, rareCandidatesReadComplete);

    /// <summary>
    /// 刷新期间的只读完成结果只能读取本经营代次已有的槽位，不能用过期名单分配、释放或
    /// 重置分配器。客户端还按最新现场精确身份联结，已结束订单不能借此恢复为执行目标。
    /// </summary>
    public JsonArray ReadKnown(long generation, JsonNode? rareOrders, JsonNode? normalOrders, JsonNode? preferences)
    {
        var result = new JsonArray();
        if (generation <= 0 || _generation != generation) return result;
        var colors = ReadPalettes(preferences);
        var rows = Objects(rareOrders).Select(order => (Kind: "rare", Order: order))
            .Concat(Objects(normalOrders).Where(order => !Bool(order["hasEvaluated"]))
                .Select(order => (Kind: "normal", Order: order)))
            .Where(item => ExactKey(item.Kind, item.Order) != null)
            .GroupBy(item => ExactKey(item.Kind, item.Order)!, StringComparer.Ordinal);
        foreach (var group in rows)
        {
            if (group.Count() != 1 || !_slots.TryGetValue(group.Key, out var slot)) continue;
            var item = group.Single();
            if (_paletteSlots[group.Key] >= colors[item.Kind].Length) continue;
            result.Add(Object(("kind", item.Kind), ("businessGeneration", generation),
                ("traceId", item.Order["traceId"]), ("orderLifecycleSequence", item.Order["orderLifecycleSequence"]),
                ("orderKey", item.Kind == "normal" ? Str(item.Order["orderKey"]) : ""),
                ("colorSlot", slot), ("color", colors[item.Kind][_paletteSlots[group.Key]])));
        }
        return result;
    }

    private static bool IsCompleteOrderArray(JsonNode? value, string kind)
        => value is JsonArray rows && rows.All(row => row is JsonObject order && ExactKey(kind, order) != null);

    /// <summary>只消费当前协议的完整配置。每类1–3色，实际颜色数同时决定该类辅助订单上限。</summary>
    private static Dictionary<string, string[]> ReadPalettes(JsonNode? preferences)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var kind in new[] { "rare", "normal" })
        {
            var raw = (preferences as JsonObject)?[kind + "OrderHighlightPalette"];
            var colors = Strings(raw).ToArray();
            if (raw is not JsonArray array || array.Count != colors.Length || colors.Length is < 1 or > MaximumColorsPerKind
                || colors.Distinct(StringComparer.Ordinal).Count() != colors.Length
                || colors.Any(color => !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-F]{6}$")))
                throw new ArgumentException("每类订单调色板必须包含 1–3 种不同的大写 #RRGGBB 颜色。", nameof(preferences));
            result.Add(kind, colors);
        }
        return result;
    }

    /// <summary>颜色只匹配完整身份；同桌、同名或相同料理不构成同一订单。</summary>
    public static string? ExactKey(string kind, JsonObject order)
    {
        var trace = Str(order["traceId"]);
        var lifecycle = Num(order["orderLifecycleSequence"]);
        if (kind is not ("rare" or "normal") || !Integer(order["orderLifecycleSequence"]) || lifecycle <= 0
            || !System.Text.RegularExpressions.Regex.IsMatch(trace, kind == "rare" ? @"^R-[0-9]{1,16}$" : @"^N-[0-9]{1,16}$")) return null;
        var key = kind == "normal" ? Str(order["orderKey"]) : "";
        if (kind == "normal" && (!System.Text.RegularExpressions.Regex.IsMatch(key, @"^ptr:[0-9a-f]{1,16}$")
            || !key[4..].Any(character => character is >= '1' and <= '9' or >= 'a' and <= 'f'))) return null;
        return $"{kind}:{trace}:{Key(lifecycle)}:{key}";
    }
}
