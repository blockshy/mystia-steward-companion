using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;

namespace MystiaStewardCompanion.Ui;

internal sealed partial class StewardOverlayController
{
    private GameFocusOverlayStore? _gameFocusOverlayStore;
    private RuntimeGameFocusOverlay? _gameFocusOverlay;
    private LocalApiSnapshot? _gameFocusLiveSnapshot;
    private LocalApiSnapshot? _gameFocusAppliedSnapshot;
    private GameFocusOverlayPlans? _gameFocusAppliedPlans;
    private GameFocusOverlayState? _gameFocusAppliedState;
    private GameFocusOverlaySourceScope? _gameFocusAppliedSourceScope;
    private string _gameFocusMediaGeneration = "";
    private bool _gameFocusVisible;

    /// <summary>
    /// 每帧只检查不可变引用与场景门槛。文本投影只在快照/计划/设置实际变化时重建，
    /// 读取推荐树与方案选择都留在业务后台，禁用时也不创建Canvas、字体或渲染服务。
    /// </summary>
    private void UpdateGameFocusOverlay()
    {
        var store = _gameFocusOverlayStore;
        if (store == null) return;
        var state = store.Snapshot;
        if (!state.Ready || !state.Settings.Enabled)
        {
            DisposeGameFocusOverlay();
            return;
        }
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        var snapshot = _gameFocusLiveSnapshot;
        var visible = lifecycle.IsActive && snapshot != null && snapshot.NightBusinessGeneration == lifecycle.Generation
            && snapshot.ActiveSceneName == _activeSceneName && snapshot.RuntimeLoaded && _gameMediaService?.IsReady != false;
        var mediaGeneration = _gameMediaService?.Status.Generation ?? "";
        if (_gameFocusOverlay == null && !visible) return;
        // 委托只读取现有媒体缓存，渲染器在后台调用；Unity解码由它在主线程限量完成。
        // 永久穿透的面板没有任何输入、拖动保存或自动暂停回调。
        _gameFocusOverlay ??= new RuntimeGameFocusOverlay(
            (generation, key) => _gameMediaService?.Image(generation, key), message => _log?.LogWarning(message));
        var plans = _localApiServer?.FocusOverlayPlans ?? GameFocusOverlayPlans.Empty;
        var sourceScope = snapshot == null ? null : _localApiServer?.ReadFocusOverlaySourceScope(snapshot);
        if (!ReferenceEquals(state, _gameFocusAppliedState) || !ReferenceEquals(snapshot, _gameFocusAppliedSnapshot)
            || !ReferenceEquals(plans, _gameFocusAppliedPlans) || visible != _gameFocusVisible
            || !ReferenceEquals(sourceScope, _gameFocusAppliedSourceScope)
            || mediaGeneration != _gameFocusMediaGeneration)
        {
            _gameFocusOverlay.Apply(state.Settings,
                visible ? GameFocusOverlayProjection.Compose(snapshot!, plans, state.Settings, mediaGeneration, sourceScope) : GameFocusOverlayFrame.Hidden);
            _gameFocusAppliedState = state;
            _gameFocusAppliedSnapshot = snapshot;
            _gameFocusAppliedPlans = plans;
            _gameFocusAppliedSourceScope = sourceScope;
            _gameFocusVisible = visible;
            _gameFocusMediaGeneration = mediaGeneration;
        }
        _gameFocusOverlay.Tick();
    }

    private void DisposeGameFocusOverlay()
    {
        _gameFocusOverlay?.Dispose();
        _gameFocusOverlay = null;
        _gameFocusAppliedState = null;
        _gameFocusAppliedSnapshot = null;
        _gameFocusAppliedPlans = null;
        _gameFocusAppliedSourceScope = null;
        _gameFocusVisible = false;
        _gameFocusMediaGeneration = "";
    }
}
