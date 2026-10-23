using System.Text.Json.Nodes;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>仅测试宿主公开有界缓存计数，便于区分首次计算、输入变化和缓存淘汰；不进入生产API。</summary>
internal sealed partial class LocalApiServer
{
    internal JsonObject CacheStatistics => J.Object(("hits", _businessCandidateCache.Hits),
        ("misses", _businessCandidateCache.Misses), ("entries", _businessCandidateCache.EntryCount),
        ("bytes", _businessCandidateCache.RetainedBytes));
}
