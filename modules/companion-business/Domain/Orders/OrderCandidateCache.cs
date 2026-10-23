using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;

namespace MystiaStewardCompanion.Business.Domain.Orders;

/// <summary>
/// 宿主持有的有界纯计算缓存。候选键保留函数的全部值参数，仅去掉已核实未读取的另一侧点单标签，包含目录、库存、厨具、禁用项、
/// 喜好、点单及特殊材料约束；订单与页面推荐投影另以完整语义输入为键，输出必须排除动态order字段。
/// 缓存只保存不可变JSON字符串，命中仍返回独立值树，绝不保存或复用旧快照、目标代次和自动化许可。
/// </summary>
public sealed class OrderCandidateCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private readonly int _maximumEntries;
    private readonly long _maximumBytes;
    private long _retainedBytes;
    private long _hits;
    private long _misses;
    private sealed record Entry(string Key, string Value, long Bytes, bool IsProjection);

    /// <summary>
    /// 默认最多保留32项、32MiB UTF-16正文。最终投影优先于中间候选，同级按最近使用顺序淘汰。
    /// 两级共用原总上限，不为满桌额外扩大内存，也不以缓存命中授予任何执行资格。
    /// </summary>
    public OrderCandidateCache(int maximumEntries = 32, long maximumBytes = 32 * 1024 * 1024)
    {
        if (maximumEntries <= 0 || maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _maximumEntries = maximumEntries; _maximumBytes = maximumBytes;
    }
    public int EntryCount { get { lock (_gate) return _entries.Count; } }
    public long RetainedBytes { get { lock (_gate) return _retainedBytes; } }
    public long Hits { get { lock (_gate) return _hits; } }
    public long Misses { get { lock (_gate) return _misses; } }

    /// <summary>料理候选保留所有相关输入；仅私有键排除不参与料理计算的顶层酒水点单标签。</summary>
    public JsonArray Food(JsonObject data, JsonObject demand, JsonObject context, JsonObject? options = null)
    {
        options ??= new JsonObject();
        var key = Key("food", data, CandidateDemand(demand, "requiredBeverageTag"), context, options);
        return (JsonArray)Get(key, () => RecommendationEngine.BuildRareFoodCandidates(data, demand, context, options));
    }
    /// <summary>酒水候选同样只排除顶层料理点单标签，不排除顾客、特殊目标或任何上下文字段。</summary>
    public JsonArray Beverage(JsonObject data, JsonObject demand, JsonObject context) =>
        (JsonArray)Get(Key("beverage", data, CandidateDemand(demand, "requiredFoodTag"), context), () => RecommendationEngine.BuildRareBeverageCandidates(data, demand, context));

    /// <summary>
    /// 只规范私有候选键；真正求解仍接收未经改写的完整原始需求。料理全链只读取料理标签，
    /// 酒水全链只读取酒水标签，另一侧点单变化不应迫使同一候选重复求解。必须先独立复制，
    /// 不能从调用方节点 Remove；也不能顺便删顾客画像、特殊目标、库存、预算或选项。
    /// 最终料理/酒水配对投影仍以完整双侧需求为键，此处的复用不会合并不同点单的执行方案。
    /// </summary>
    private static JsonObject CandidateDemand(JsonObject demand, string unusedField)
    {
        var keyDemand = RecommendationJson.Obj(RecommendationJson.Clone(demand));
        keyDemand.Remove(unusedField);
        return keyDemand;
    }

    /// <summary>
    /// 缓存已验证需求的纯推荐投影。调用方负责传入本轮解析的需求、规则、排序、权限策略及预算，
    /// 共享输入在同批订单中只编码一次；命中后重新附加当前订单。
    /// 显式拒绝缓存order字段，防止将观察时间、送达状态或旧身份误用为当前事实。
    /// </summary>
    internal JsonObject Recommendation(string sharedInput, JsonObject order, JsonObject? budget, Func<JsonObject> build) =>
        (JsonObject)Get("recommendation\n" + sharedInput + "\n" + order.ToJsonString() + "\n" + (budget?.ToJsonString() ?? "null"), () =>
    {
        var result = build();
        if (result.ContainsKey("order")) throw new InvalidOperationException("纯推荐投影缓存不得保存订单状态。");
        return result;
    });

    /// <summary>
    /// 缓存只读页面的最终投影，避免命中候选后仍重复执行料理/酒水笛卡尔组合和排序。
    /// 输入由查询组合根提供完整页面语义；缓存只允许三个展示字段，不能携带宿主输入版本、订单或执行许可。
    /// 页面与候选、订单共用本实例的条目数及字节上限，不建立额外无界缓存。
    /// </summary>
    internal JsonObject Page(JsonObject input, Func<JsonObject> build) => (JsonObject)Get(Key("page", input), () =>
    {
        var result = build();
        if (result.Count != 3 || !result.ContainsKey("kind") || result["recipes"] is not JsonArray
            || result["beverages"] is not JsonArray)
            throw new InvalidOperationException("纯页面缓存只能保存kind、recipes和beverages展示投影。");
        return result;
    });

    /// <summary>宿主销毁或主动回收时清除缓存；命中计数保留为本实例生命周期的性能诊断。</summary>
    public void Clear() { lock (_gate) { _entries.Clear(); _lru.Clear(); _retainedBytes = 0; } }

    private static string Key(string operation, params JsonObject[] input) =>
        operation + "\n" + string.Join("\n", input.Select(value => value.ToJsonString()));

    private JsonNode Get(string key, Func<JsonNode> build)
    {
        string? json = null;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _lru.Remove(node); _lru.AddLast(node); _hits++; json = node.Value.Value;
            }
            else _misses++;
        }
        if (json != null) return JsonNode.Parse(json)!;
        // 昂贵搜索不占缓存锁，保证状态查询和其他独立宿主不被计算锁住。
        var value = build(); json = value.ToJsonString();
        var bytes = 2L * (key.Length + json.Length);
        if (bytes > _maximumBytes) return value;
        var isProjection = key.StartsWith("recommendation\n", StringComparison.Ordinal)
            || key.StartsWith("page\n", StringComparison.Ordinal);
        lock (_gate)
        {
            if (_entries.ContainsKey(key)) return value;
            while (_entries.Count >= _maximumEntries || _retainedBytes + bytes > _maximumBytes)
            {
                // 一桌计算先产生体积较大的候选，再产生最终展示投影。单级 LRU 会让后几桌的
                // 候选驱逐前几桌投影，下一轮按相同顺序计算时整圈全未命中。优先淘汰可重建
                // 的中间候选；新的候选绝不能驱逐最终投影，放不下时本次直接返回且不缓存。
                // 只有新的最终投影确实需要容量，才允许淘汰最久未用的同级投影。
                var oldest = _lru.First;
                while (oldest != null && oldest.Value.IsProjection) oldest = oldest.Next;
                if (oldest == null)
                {
                    if (!isProjection) return value;
                    oldest = _lru.First!;
                }
                _entries.Remove(oldest.Value.Key); _lru.Remove(oldest); _retainedBytes -= oldest.Value.Bytes;
            }
            _entries.Add(key, _lru.AddLast(new Entry(key, json, bytes, isProjection))); _retainedBytes += bytes;
        }
        return value;
    }
}
