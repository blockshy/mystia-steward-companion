using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Orders;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 在真实生产缓存内验证节点所有权：分别修改首次求解返回树和命中返回树，后续命中必须仍完整
/// 等于原结果。此探针不复制/替换生产缓存算法；它故意修改客户端可拥有的返回值来暴露共享节点。
/// </summary>
internal static class CandidateCacheOwnershipProbe
{
    internal static JsonObject Evaluate(JsonObject input)
    {
        var cache = new OrderCandidateCache();
        var kind = Str(input["kind"]);
        var data = Obj(input["data"]); var demand = Obj(input["demand"]); var context = Obj(input["context"]);
        var originalInput = input.ToJsonString();
        JsonArray Read() => kind switch
        {
            "food" => cache.Food(data, demand, context, input["options"] as JsonObject),
            "beverage" => cache.Beverage(data, demand, context),
            _ => throw new ArgumentException("未知候选所有权类型。"),
        };
        void Mutate(JsonArray value)
        {
            if (value.Count == 0 || value[0] is not JsonObject row) throw new InvalidOperationException("所有权夹具必须包含实际候选。");
            var item = Obj(row[kind == "food" ? "recipe" : "beverage"]);
            if (!ReferenceEquals(row.Parent, value) || !ReferenceEquals(item.Parent, row))
                throw new InvalidOperationException("候选/目录副本必须归属于当前返回树。");
            item["name"] = "调用方修改目录副本";
            row["activeTags"] = new JsonArray("调用方修改标签");
            if (kind == "food")
            {
                if (row["extraIngredients"] is not JsonArray extras || extras.Count == 0)
                    throw new InvalidOperationException("料理所有权夹具必须包含加料，不能跳过材料节点检查。");
                Obj(extras[0])["price"] = 999999;
            }
        }

        var cold = Read(); var expected = cold.ToJsonString();
        Mutate(cold);
        if (input.ToJsonString() != originalInput) throw new InvalidOperationException("首次返回节点修改污染了输入。");
        var firstHit = Read();
        if (firstHit.ToJsonString() != expected) throw new InvalidOperationException("首次返回节点修改污染了缓存。");
        if (ReferenceEquals(cold, firstHit) || ReferenceEquals(cold[0], firstHit[0]))
            throw new InvalidOperationException("缓存命中必须返回独立节点。");
        Mutate(firstHit);
        var secondHit = Read();
        if (secondHit.ToJsonString() != expected || input.ToJsonString() != originalInput)
            throw new InvalidOperationException("命中返回节点修改污染了后续结果或输入。");
        if (cache.Misses != 1 || cache.Hits != 2) throw new InvalidOperationException("所有权测试必须覆盖一次冷求解和两次真实命中。");
        return Object(("kind", kind), ("verified", true), ("candidateCount", secondHit.Count),
            ("cacheHits", cache.Hits), ("cacheMisses", cache.Misses));
    }
}
