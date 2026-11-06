using System.Text.Json;
using MystiaStewardCompanion.Ui;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.LocalApi;

internal sealed partial class LocalApiServer
{
    // 仅保存值与绘制投影，不持有 Unity 对象。GET 不改变业务版本；POST 由统一主设备
    // 手工写入门禁保护，也不占用自动化 lease，不刷新候选计算。
    private GameFocusOverlayStore _focusOverlayStore = new();
    internal GameFocusOverlayStore FocusOverlayStore => _focusOverlayStore;
    private GameFocusOverlayPlans _focusOverlayPlans = GameFocusOverlayPlans.Empty;
    private System.Text.Json.Nodes.JsonObject? _focusPlanResult;
    private string _focusPlanColors = "";
    private MystiaStewardCompanion.GameMedia.GameMediaManifest? _focusPlanMedia;
    private GameFocusOverlayPlans? _focusPreparedPlans;
    internal GameFocusOverlayPlans FocusOverlayPlans => Volatile.Read(ref _focusOverlayPlans);

    // 以下三项仅由Unity主线程读取/更新。权威与目录源各自原子发布，不持有业务/存储锁，
    // 因此不会和后台业务的authority-transition -> snapshot顺序形成锁反转。
    private LocalApiSnapshot? _focusScopeSnapshot;
    private CompanionAuthorityDisplayScope? _focusScopeAuthority;
    private string? _focusScopeCatalog;
    private GameFocusOverlaySourceScope? _focusCurrentScope;

    internal GameFocusOverlaySourceScope ReadFocusOverlaySourceScope(LocalApiSnapshot snapshot)
    {
        var authority = _deviceAuthorityStore.DisplayScope;
        var catalog = Volatile.Read(ref _runtimeDataJson);
        if (ReferenceEquals(snapshot, _focusScopeSnapshot) && ReferenceEquals(authority, _focusScopeAuthority)
            && ReferenceEquals(catalog, _focusScopeCatalog) && _focusCurrentScope != null) return _focusCurrentScope;
        var next = GameFocusOverlaySourceScope.FromSnapshot(snapshot, authority, catalog);
        // 采样时间等无关字段造成新快照时保留同一scope引用，不让正常250ms采集变成占位闪烁。
        if (next != _focusCurrentScope) _focusCurrentScope = next;
        _focusScopeSnapshot = snapshot;
        _focusScopeAuthority = authority;
        _focusScopeCatalog = catalog;
        return _focusCurrentScope!;
    }

    /// <summary>
    /// 唯一后台业务循环在业务锁外准备小型不可变视图；此处绝不发布。
    /// 必须等调用方完成业务结果最终校验，再在同一提交边界整体替换，失败或被新结果取代的准备项不可见。
    /// </summary>
    private GameFocusOverlayPlans? PrepareFocusOverlayPlans(BusinessFrame frame, System.Text.Json.Nodes.JsonObject result,
        System.Text.Json.Nodes.JsonObject gameUiTargets)
    {
        if (!_focusOverlayStore.Snapshot.Settings.Enabled) return null;
        // 颜色、资源代次可以独立于候选引用改变；只编码最多八单的小型颜色表，
        // 不重算候选，也不能以原结果引用相同为由冻结旧配色或旧配方图片依赖。
        MystiaStewardCompanion.GameMedia.GameMediaManifest? media = null;
        try { media = _gameMediaService?.Manifest(); }
        catch (MystiaStewardCompanion.GameMedia.GameMediaException) { }
        var colors = gameUiTargets["orderColors"]?.ToJsonString() ?? "";
        var scope = new GameFocusOverlaySourceScope(frame.SourceContext.ToJsonString(), frame.CatalogJson);
        if (ReferenceEquals(result, _focusPlanResult)
            && ReferenceEquals(media, _focusPlanMedia) && colors == _focusPlanColors
            && _focusPreparedPlans?.SourceScope == scope) return _focusPreparedPlans;
        var plans = GameFocusOverlayProjection.Capture(frame.Snapshot, result, frame.Data, media, gameUiTargets, scope);
        _focusPreparedPlans = plans;
        _focusPlanResult = result;
        _focusPlanMedia = media;
        _focusPlanColors = colors;
        return plans;
    }

    /// <summary>调用方已验证并接受业务发布；锁内只交换引用，不执行JSON、图标或方案工作。</summary>
    private void CommitFocusOverlayPlans(GameFocusOverlayPlans? plans)
    {
        if (plans != null) Volatile.Write(ref _focusOverlayPlans, plans);
    }

    private object UpdateFocusOverlay(HttpRequestData request)
    {
        var body = ReadJsonRequest<FocusOverlayUpdateRequest>(request, "expectedRevision", "changes");
        try
        {
            var overlay = _focusOverlayStore.Update(body.ExpectedRevision, body.Changes);
            return new { ok = true, status = "游戏内专注显示已更新。", overlay };
        }
        catch (ArgumentException ex) { throw new CompanionDeviceAuthorityException(400, ex.Message); }
        catch (InvalidOperationException ex) { throw new CompanionDeviceAuthorityException(409, ex.Message); }
    }

    private sealed class FocusOverlayUpdateRequest
    {
        public long ExpectedRevision { get; init; }
        public JsonElement Changes { get; init; }
    }
}
