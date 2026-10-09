using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;

namespace MystiaStewardCompanion.Business.Domain.Orders;

/// <summary>
/// 宿主持有的有界纯计算缓存。候选键直接由函数的全部值参数构成，包含目录、库存、厨具、禁用项、
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
    private sealed record Entry(string Key, string Value, long Bytes);

    /// <summary>默认最多保留32项、32MiB UTF-16正文，任一上限先达到即按最近使用顺序淘汰。</summary>
    public OrderCandidateCache(int maximumEntries = 32, long maximumBytes = 32 * 1024 * 1024)
    {
        if (maximumEntries <= 0 || maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _maximumEntries = maximumEntries; _maximumBytes = maximumBytes;
    }
    public int EntryCount { get { lock (_gate) return _entries.Count; } }
    public long RetainedBytes { get { lock (_gate) return _retainedBytes; } }
    public long Hits { get { lock (_gate) return _hits; } }
    public long Misses { get { lock (_gate) return _misses; } }

    /// <summary>完整料理候选函数输入；options显式为空时使用规范空对象。</summary>
    public JsonArray Food(JsonObject data, JsonObject demand, JsonObject context, JsonObject? options = null)
    {
        options ??= new JsonObject();
        var key = Key("food", data, demand, context, options);
        return (JsonArray)Get(key, () => RecommendationEngine.BuildRareFoodCandidates(data, demand, context, options));
    }
    /// <summary>完整酒水候选函数输入；该结果不含动态订单或执行状态。</summary>
    public JsonArray Beverage(JsonObject data, JsonObject demand, JsonObject context) =>
        (JsonArray)Get(Key("beverage", data, demand, context), () => RecommendationEngine.BuildRareBeverageCandidates(data, demand, context));

    /// <summary>
    /// 缓存单订单的纯推荐投影。调用方负责提供完整语义输入，并在命中后重新附加当前订单；
    /// 显式拒绝缓存order字段，防止将观察时间、送达状态或旧身份误用为当前事实。
    /// </summary>
    internal JsonObject Recommendation(JsonObject input, Func<JsonObject> build) => (JsonObject)Get(Key("recommendation", input), () =>
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
        lock (_gate)
        {
            if (_entries.ContainsKey(key)) return value;
            while (_entries.Count >= _maximumEntries || _retainedBytes + bytes > _maximumBytes)
            {
                var oldest = _lru.First!; _entries.Remove(oldest.Value.Key); _lru.RemoveFirst(); _retainedBytes -= oldest.Value.Bytes;
            }
            _entries.Add(key, _lru.AddLast(new Entry(key, json, bytes))); _retainedBytes += bytes;
        }
        return value;
    }
}
