using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application;
using MystiaStewardCompanion.Contracts;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.LocalApi;

internal sealed partial class LocalApiServer
{
    // 全部槽位与序号只在 _businessLock 内访问。会话不持有游戏对象或执行许可；
    // 单调时间避免系统时钟回拨延长隐藏页面的寿命，最多保留十六个槽位。
    private readonly Dictionary<string, PageSession> _businessPageSessions = new(StringComparer.Ordinal);
    private long _businessQuerySequence;
    private static long PageQueryNow => (long)(Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency);

    private sealed class PageSession
    {
        public string Id = "", ClientId = "";
        public long LastSequence, ExpiresAt;
        public PageQuery? Query;
    }

    private sealed class PageQuery
    {
        public string IntentJson = "";
        public long Sequence, Version = -1, ExpiresAt;
        public JsonObject? Result, SourceContext;
        public string? Error;
        public string SourceSnapshotSignature = "", SourceCatalogJson = "";
    }

    /// <summary>
    /// 只创建空订阅槽；不能借一次迟到的打开响应恢复查询。不同设备独立持有随机标识，
    /// 常规状态轮询既不创建订阅，也不会撤销其它设备的可见页面。
    /// </summary>
    private string OpenBusinessPageSession(string request, HttpRequestData requestData)
    {
        var (clientId, _) = ReadRequiredClientIdentity(request);
        try { BusinessProtocol.ValidatePageSessionOpen(HttpRequestReader.ReadRequiredJsonBody(requestData)); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        { throw new CompanionDeviceAuthorityException(400, exception.Message); }
        var now = PageQueryNow;
        var session = new PageSession { Id = Guid.NewGuid().ToString("N"), ClientId = clientId,
            ExpiresAt = now + BusinessProtocol.PageSessionLifetimeMilliseconds };
        lock (_businessLock)
        {
            PrunePageSessionsLocked(now);
            if (_businessPageSessions.Count >= BusinessProtocol.MaxPageQueries)
                _businessPageSessions.Remove(_businessPageSessions.Values.OrderBy(item => item.ExpiresAt).First().Id);
            _businessPageSessions.Add(session.Id, session);
        }
        return J.Object(("ok", true), ("protocolVersion", BusinessProtocol.Version), ("sessionId", session.Id)).ToJsonString();
    }

    /// <summary>
    /// 更新/撤销共享同一严格递增序列：即使网络逆序，旧更新也不能越过 null 撤销重新登记。
    /// 过期或被淘汰的会话不允许由更新重建，客户端只能显式重新打开空会话。
    /// </summary>
    private string UpdateBusinessPageSession(string request, HttpRequestData requestData)
    {
        var (clientId, _) = ReadRequiredClientIdentity(request);
        (string SessionId, long Sequence, JsonObject? Intent) update;
        try { update = BusinessProtocol.ParsePageSessionUpdate(HttpRequestReader.ReadRequiredJsonBody(requestData)); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        { throw new CompanionDeviceAuthorityException(400, exception.Message); }
        JsonObject? result, sourceContext;
        string sourceSignature, sourceCatalogJson;
        BusinessFrame? frame;
        long version;
        string? queryError, businessError;
        var now = PageQueryNow;
        lock (_businessLock)
        {
            PrunePageSessionsLocked(now);
            if (!_businessPageSessions.TryGetValue(update.SessionId, out var session) || session.ClientId != clientId)
                throw new CompanionDeviceAuthorityException(409, "page-session-expired");
            if (update.Sequence <= session.LastSequence)
                throw new CompanionDeviceAuthorityException(409, "page-sequence-stale");
            session.LastSequence = update.Sequence;
            session.ExpiresAt = now + BusinessProtocol.PageSessionLifetimeMilliseconds;
            if (update.Intent == null)
            {
                // 保留有限期的序号墓碑，不能删槽后让已在途的旧请求隐式复活。
                session.Query = null;
                return J.Object(("ok", true), ("protocolVersion", BusinessProtocol.Version), ("cancelled", true)).ToJsonString();
            }
            var json = update.Intent.ToJsonString();
            if (session.Query == null || session.Query.IntentJson != json)
                session.Query = new PageQuery { IntentJson = json, Sequence = ++_businessQuerySequence };
            var query = session.Query;
            query.ExpiresAt = now + BusinessProtocol.PageIntentLifetimeMilliseconds;
            result = query.Result; version = query.Version; queryError = query.Error;
            sourceContext = query.SourceContext; sourceSignature = query.SourceSnapshotSignature;
            sourceCatalogJson = query.SourceCatalogJson; frame = _businessFrame; businessError = _businessError;
        }
        var sameContext = BusinessDisplayContext.Matches(sourceContext, ReadBusinessSourceContext(sourceCatalogJson));
        var current = sameContext && result != null && version == Interlocked.Read(ref _businessInputVersion)
            && frame != null && IsBusinessFrameCurrent(frame);
        return J.Object(("protocolVersion", BusinessProtocol.Version), ("isCurrent", current),
            ("pending", !current && queryError == null && businessError == null), ("error", queryError ?? businessError),
            ("result", sameContext ? result : null), ("inputVersion", version.ToString(CultureInfo.InvariantCulture)),
            ("sourceContext", sameContext ? sourceContext : null),
            ("sourceSnapshotSignature", sameContext ? sourceSignature : "")).ToJsonString();
    }

    /// <summary>TTL 只清理页面计算；不修改设备在线状态、共享配置、自动化租约或其它设备槽位。</summary>
    private void PrunePageSessionsLocked(long now)
    {
        if (_businessPageSessions.Count == 0) return;
        foreach (var session in _businessPageSessions.Values.ToArray())
        {
            if (session.ExpiresAt <= now) _businessPageSessions.Remove(session.Id);
            else if (session.Query?.ExpiresAt <= now) session.Query = null;
        }
    }

    private void ExecuteOnePageQuery(BusinessFrame frame)
    {
        PageSession? session;
        PageQuery? query;
        lock (_businessLock)
        {
            PrunePageSessionsLocked(PageQueryNow);
            session = _businessPageSessions.Values.Where(item => item.Query != null && item.Query.Version != frame.Version)
                .OrderBy(item => item.Query!.Sequence).FirstOrDefault();
            query = session?.Query;
        }
        if (query == null || session == null || !IsBusinessFrameCurrent(frame)) return;
        JsonObject? result = null; string? error = null;
        try { result = BusinessQueries.EvaluatePage(JsonNode.Parse(query.IntentJson)!.AsObject(), frame.Snapshot,
            frame.Data, frame.Preferences, frame.Favorites, frame.CustomRecipes, _businessCandidateCache); }
        catch (Exception exception) { error = exception.GetBaseException().Message; }
        lock (_businessLock)
        {
            PrunePageSessionsLocked(PageQueryNow);
            // 纯计算已开始时不强制中断领域算法；撤销/切条件/过期后必须丢弃该次结果，不能重新发布。
            if (!IsBusinessFrameCurrent(frame) || !_businessPageSessions.TryGetValue(session.Id, out var current)
                || !ReferenceEquals(session, current) || !ReferenceEquals(query, current.Query)) return;
            query.Result = result; query.Error = error; query.Version = frame.Version; query.Sequence = ++_businessQuerySequence;
            query.SourceContext = frame.SourceContext; query.SourceSnapshotSignature = frame.SnapshotSignature;
            query.SourceCatalogJson = frame.CatalogJson;
        }
    }
}
