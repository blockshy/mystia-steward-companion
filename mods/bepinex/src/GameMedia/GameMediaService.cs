using System.Diagnostics;

namespace MystiaStewardCompanion.GameMedia;

/// <summary>
/// 图片初始化状态机：HTTP 只登记重读意图，Controller 主线程每帧最多提取一个实体。
/// 持久缓存核验/写入在后台执行；状态只在整代完成后 ready，任何失败均保留明确错误和重试入口。
/// </summary>
internal sealed class GameMediaService : IDisposable
{
    private readonly object _gate = new();
    private readonly GameMediaStore _store;
    private readonly int _mainThreadId;
    private IRuntimeGameMediaExtractor? _extractor;
    private GameMediaPlan? _plan;
    private GameMediaManifest? _manifest;
    private GameMediaStatus _status = new("waiting", null, 0, 0, "等待游戏图片目录就绪。", null);
    private Task<GameMediaManifest?>? _loading;
    private Task<GameMediaManifest>? _committing;
    private Task<(GameMediaCachedPlan? Cache, string Reason)>? _startupLoading;
    private bool _startupDiagnosticPending;
    private bool _verifyConfiguredPlan;
    private Task? _planPreparation;
    private CancellationTokenSource _jobStop = new();
    private Dictionary<string, GameMediaImage> _images = new(StringComparer.Ordinal);
    private List<GameMediaMissing> _missing = new();
    private long _epoch;
    private long _planRetryAt;
    private long _itemStartedAt;
    private int _index;
    private long _bytes;
    private bool _forceReload;
    private bool _hasCacheCandidate;
    private bool _disposed;
    private string? _buildingGeneration;

    /// <summary>仅在可执行边界变化时通知宿主失效；进度数字变化不反复使业务输入版本递增。</summary>
    internal event Action? ReadinessChanged;
    /// <summary>固定作业转换事件；不在每帧、每张图片或等待重试时输出日志。</summary>
    internal event Action<GameMediaDiagnostic>? Diagnostic;
    internal GameMediaService(GameMediaStore store, Task<string?>? startupFingerprint = null)
    {
        _store = store;
        _mainThreadId = Environment.CurrentManagedThreadId;
        // 只读取一次固定指针的存在性元数据，不在 Unity 主线程校验整份缓存。候选尚未验证，
        // 仍禁止全部业务副作用；独立 verifying 状态避免把每次启动的安全校验冒充重新提取。
        _hasCacheCandidate = store.HasCacheCandidate;
        _status = InitialStatus();
        if (_hasCacheCandidate && startupFingerprint != null)
        {
            // 启动身份任务只交付纯托管摘要。旧缓存没有私有凭据时一次返回 miss，之后
            // 等真实目录，不在未读档期间每帧或每五秒重复读取整代图片与文件指纹。
            _startupLoading = Task.Run(async () =>
            {
                var fingerprint = await startupFingerprint.ConfigureAwait(false);
                if (fingerprint == null) return ((GameMediaCachedPlan?)null, "startup-identity-unavailable");
                var restored = _store.TryLoadStartup(fingerprint, out var reason);
                return (restored, reason);
            });
            _startupDiagnosticPending = true;
            _status = new("verifying", null, 0, 0, "正在验证已保存的游戏图片。", null);
        }
    }
    internal bool IsReady { get { lock (_gate) return !_disposed && _status.Phase == "ready"; } }
    internal GameMediaStatus Status { get { lock (_gate) return _status; } }

    /// <summary>目录实例更新时撤销旧代，取消旧作业；不会把旧目录缓存冒充新目录的已验证结果。</summary>
    internal void Configure(IRuntimeGameMediaExtractor extractor)
    {
        RequireMainThread();
        lock (_gate)
        {
            if (_disposed) return;
            if (_status.Phase == "ready" && _plan != null)
            {
                // 主菜单已完整验证图片后，读档仅带来真实目录，不应先撤销同一份图片再重读
                // 全部 PNG。新提取器先构建真实计划；完全相同才继续原代，变化立即撤销。
                _extractor = extractor;
                _verifyConfiguredPlan = true;
                _planPreparation = null;
                _planRetryAt = 0;
                return;
            }
            // 主菜单已复用图片但尚无真实提取器时也允许排队重读；目录随后到来不能把
            // 用户的强制重读悄悄改成普通缓存命中。保持原意图，其余作业状态仍完整重置。
            var forceReload = _forceReload;
            ResetJobLocked();
            _extractor = extractor;
            _forceReload = forceReload;
            _status = forceReload
                ? new("waiting", null, 0, 0, "已排队重新读取游戏图片。", null)
                : InitialStatus();
        }
        ReadinessChanged?.Invoke();
    }

    /// <summary>只登记意图。等待/提取中的请求复用同一作业，不能并发排队或在 HTTP 线程访问 Unity。</summary>
    internal GameMediaStatus RequestReload()
    {
        GameMediaStatus result; var changed = false;
        lock (_gate)
        {
            if (_disposed) throw new GameMediaException(503, "game-media-stopped");
            if (_status.Phase is "waiting" or "verifying" or "extracting") return _status;
            ResetJobLocked();
            _forceReload = true;
            _status = new("waiting", null, 0, 0, "已排队重新读取游戏图片。", null);
            result = _status; changed = true;
        }
        if (changed) ReadinessChanged?.Invoke();
        return result;
    }

    internal GameMediaManifest Manifest()
    {
        lock (_gate)
        {
            if (_disposed || _status.Phase != "ready" || _manifest == null)
                throw new GameMediaException(409, "game-media-not-ready");
            return _manifest;
        }
    }

    /// <summary>请求与读取完成后均核对同一代次；重读途中不能把旧代 PNG 交给新客户端缓存。</summary>
    internal byte[] Image(string generation, string key)
    {
        var manifest = Manifest();
        if (generation != manifest.Generation) throw new GameMediaException(409, "game-media-generation-stale");
        byte[] bytes;
        try { bytes = _store.ReadImage(manifest, key); }
        catch (GameMediaException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            Fail("图片缓存校验失败，请重新读取。", expectedGeneration: generation);
            throw new GameMediaException(409, "game-media-cache-invalid");
        }
        if (Manifest().Generation != generation) throw new GameMediaException(409, "game-media-generation-stale");
        return bytes;
    }

    /// <summary>由 Unity Update 调用，不循环补做积压项，避免图片初始化随目录数量阻塞一整帧。</summary>
    internal void Tick()
    {
        RequireMainThread();
        IRuntimeGameMediaExtractor? extractor; long epoch;
        lock (_gate)
        {
            if (_disposed || _status.Phase == "failed" || _status.Phase == "ready" && !_verifyConfiguredPlan) return;
            extractor = _extractor; epoch = _epoch;
        }
        try
        {
            if (_verifyConfiguredPlan && extractor != null)
            {
                if (_planPreparation?.IsCompleted != true && Environment.TickCount64 < _planRetryAt) return;
                _planPreparation = null;
                _planRetryAt = Environment.TickCount64 + 5000;
                if (!extractor.TryCreatePlan(out var actual, out _))
                {
                    _planPreparation = extractor.PendingPlanPreparation;
                    return;
                }
                if (actual == null) throw new InvalidDataException("图片目录没有返回计划。");
                GameMediaStore.ValidatePlan(actual);
                lock (_gate)
                {
                    if (_disposed || epoch != _epoch) return;
                    if (SamePlan(_plan!, actual))
                    {
                        _verifyConfiguredPlan = false;
                        _plan = actual;
                        return;
                    }
                    // 原始计划一旦明确变化，先撤销 ready，再启动新计划验证。此处不重用
                    // 展示图片计划作为业务目录，也不修改任何租约/业务 current 判定。
                    ResetJobLocked();
                    epoch = _epoch;
                    _forceReload = false;
                    _plan = actual;
                    _status = InitialStatus();
                    _loading = Task.Run(() => _store.TryLoad(actual));
                }
                ReadinessChanged?.Invoke();
                PublishDiagnostic("cache-check", epoch);
                return;
            }
            if (_startupLoading != null)
            {
                if (_startupDiagnosticPending)
                {
                    _startupDiagnosticPending = false;
                    PublishDiagnostic("cache-check", epoch);
                }
                if (!_startupLoading.IsCompleted) return;
                // 原因与缓存放在同一个任务结果中，旧作业迟到不能改写新代的诊断字段。
                var startup = _startupLoading.GetAwaiter().GetResult();
                var restored = startup.Cache;
                _startupLoading = null;
                if (restored != null)
                {
                    lock (_gate)
                    {
                        if (_disposed || epoch != _epoch) return;
                        _plan = restored.Plan;
                    }
                    PublishDiagnostic("cache-hit", epoch, cacheHit: true, generation: restored.Manifest.Generation);
                    Complete(restored.Manifest, epoch, cacheHit: true);
                    return;
                }
                lock (_gate)
                {
                    if (_disposed || epoch != _epoch) return;
                    // 用户只需要知道下一步动作；具体身份/文件失败码留在低频诊断中。旧代缺少
                    // 凭据并不表示 PNG 损坏，进入一次存档验证后可在原代补齐，不强制重新提图。
                    var startupMessage = startup.Reason switch
                    {
                        "startup-receipt-missing" => "图片缓存需要一次完整确认，请进入存档。",
                        "startup-identity-changed" => "游戏资源或 DLC 状态已变化，请进入存档更新图片缓存。",
                        "cache-content-invalid" or "startup-receipt-invalid" => "图片缓存校验未通过，请进入存档重新确认。",
                        _ => "暂时无法确认游戏资源状态，等待游戏目录就绪。",
                    };
                    _status = new("waiting", null, 0, 0, startupMessage, null);
                }
                PublishDiagnostic("cache-miss", epoch, cacheHit: false, reason: startup.Reason);
            }
            if (extractor == null) return;
            if (_plan == null)
            {
                // 后台指纹通常很快完成；完成信号允许下一帧继续，不再无条件等满五秒。
                // 没有纯托管准备信号时仍按五秒读取目录，且所有重试都留在本主线程执行。
                if (_planPreparation?.IsCompleted != true && Environment.TickCount64 < _planRetryAt) return;
                _planPreparation = null;
                _planRetryAt = Environment.TickCount64 + 5000;
                if (!extractor.TryCreatePlan(out var plan, out _))
                {
                    _planPreparation = extractor.PendingPlanPreparation;
                    return;
                }
                if (plan == null) throw new InvalidDataException("图片目录没有返回计划。");
                GameMediaStore.ValidatePlan(plan);
                lock (_gate)
                {
                    if (_disposed || epoch != _epoch) return;
                    _plan = plan;
                    _status = new(!_forceReload && _hasCacheCandidate ? "verifying" : "waiting", null, 0, plan.Items.Count,
                        _forceReload ? "已排队重新读取游戏图片。" : "正在验证游戏图片缓存。", null);
                    var force = _forceReload;
                    _loading = Task.Run(() => force ? null : _store.TryLoad(plan));
                }
                if (!_forceReload) PublishDiagnostic("cache-check", epoch);
                return;
            }
            if (_loading != null)
            {
                if (!_loading.IsCompleted) return;
                var loaded = _loading.GetAwaiter().GetResult();
                _loading = null;
                if (loaded != null)
                {
                    PublishDiagnostic("cache-hit", epoch, cacheHit: true, generation: loaded.Generation);
                    Complete(loaded, epoch, cacheHit: true);
                    return;
                }
                _buildingGeneration = Guid.NewGuid().ToString("N");
                _itemStartedAt = Environment.TickCount64;
                lock (_gate)
                {
                    if (_disposed || epoch != _epoch) return;
                    // 只有缓存缺失、无效或显式重读，才进入真正的逐帧提取阶段。
                    _status = new("extracting", null, 0, _plan.Items.Count, "正在读取游戏图片。", null);
                }
                PublishDiagnostic(_forceReload ? "forced-extract" : "cache-miss", epoch, cacheHit: false);
            }
            if (_committing != null)
            {
                if (_committing.IsCompleted) Complete(_committing.GetAwaiter().GetResult(), epoch, cacheHit: false);
                return;
            }
            var item = _plan.Items[_index];
            var state = extractor.Extract(item, out var image, out var message);
            if (state == GameMediaExtractState.Pending)
            {
                if (Environment.TickCount64 - _itemStartedAt > 30000) throw new TimeoutException("游戏图片读取超时。");
                return;
            }
            if (state == GameMediaExtractState.Failed) throw new InvalidDataException("游戏图片提取失败。");
            lock (_gate)
            {
                if (_disposed || epoch != _epoch) return;
                if (state == GameMediaExtractState.Ready)
                {
                    if (image == null || image.Png == null || image.Png.Length == 0
                        || image.Png.Length > GameMediaStore.MaximumImageBytes)
                        throw new InvalidDataException("游戏图片大小无效。");
                    _bytes += image.Png.Length;
                    if (_bytes > GameMediaStore.MaximumTotalBytes) throw new InvalidDataException("游戏图片总大小超限。");
                    _images.Add(item.Key, image);
                }
                else if (state == GameMediaExtractState.Missing)
                {
                    // 缺图采用明确 fallback，错误只记录通用原因，不回传原生异常路径/调试信息。
                    _missing.Add(new GameMediaMissing(item.Key, "游戏未提供已确认的图片引用。"));
                }
                else throw new InvalidDataException("未知图片提取状态。");
                _index++;
                _itemStartedAt = Environment.TickCount64;
                _status = new("extracting", null, _index, _plan.Items.Count,
                    _index == _plan.Items.Count ? "正在校验并保存游戏图片。" : "正在读取游戏图片。", null);
                if (_index == _plan.Items.Count)
                {
                    var plan = _plan; var generation = _buildingGeneration!;
                    var images = _images; var missing = _missing; var token = _jobStop.Token;
                    _committing = Task.Run(() => _store.Commit(plan, generation, images, missing, token));
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fail(exception is TimeoutException ? "游戏图片读取超时，请重新读取。" : "游戏图片初始化失败，请重新读取。", epoch: epoch);
        }
    }

    private void Complete(GameMediaManifest manifest, long epoch, bool cacheHit)
    {
        lock (_gate)
        {
            if (_disposed || epoch != _epoch) return;
            _manifest = manifest;
            _hasCacheCandidate = true;
            _images = new(StringComparer.Ordinal); _missing = new();
            _loading = null; _committing = null;
            _status = new("ready", manifest.Generation, _plan!.Items.Count, _plan.Items.Count,
                manifest.Missing.Count == 0 ? "游戏图片已就绪。" : "游戏图片已就绪，缺失项目使用通用图标。", null);
        }
        PublishDiagnostic("complete", epoch, cacheHit);
        ReadinessChanged?.Invoke();
    }
    private void Fail(string message, long? epoch = null, string? expectedGeneration = null)
    {
        long failedEpoch;
        lock (_gate)
        {
            if (_disposed || _status.Phase == "failed" || epoch.HasValue && epoch != _epoch
                || expectedGeneration != null && _manifest?.Generation != expectedGeneration) return;
            _jobStop.Cancel();
            _status = new("failed", null, _index, _plan?.Items.Count ?? 0, message, message);
            failedEpoch = _epoch;
        }
        PublishDiagnostic("failed", failedEpoch);
        ReadinessChanged?.Invoke();
    }
    private void PublishDiagnostic(string eventName, long epoch, bool? cacheHit = null, string? generation = null,
        string? reason = null)
    {
        GameMediaDiagnostic diagnostic;
        lock (_gate)
        {
            if (_disposed || epoch != _epoch) return;
            diagnostic = new(eventName, _status.Phase, _status.Completed, _status.Total,
                generation ?? _status.Generation, cacheHit, reason);
        }
        // 外部日志回调不能持有资源状态锁，也不能因磁盘写入异常把已经完成的缓存作业变成失败。
        // 分别调用订阅者，确保一个故障日志接收器不会阻断其它诊断接收器；此处分配仅发生于
        // 每作业数个固定转换点，不会给主线程的每帧等待/逐图片提取新增日志或委托数组分配。
        var handlers = Diagnostic;
        if (handlers == null) return;
        foreach (Action<GameMediaDiagnostic> handler in handlers.GetInvocationList())
        {
            try { handler(diagnostic); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { /* 日志失败不改变图片作业结果。 */ }
        }
    }
    private void ResetJobLocked()
    {
        _jobStop.Cancel();
        // 后台任务可能仍读取自己的 token，保留旧 CTS 到垃圾回收，不从主线程等待任务或释放其句柄。
        _jobStop = new(); _epoch++; _plan = null; _manifest = null; _loading = null; _committing = null;
        _startupLoading = null; _startupDiagnosticPending = false;
        _verifyConfiguredPlan = false;
        _planPreparation = null;
        _index = 0; _bytes = 0; _planRetryAt = 0; _buildingGeneration = null;
        _images = new(StringComparer.Ordinal); _missing = new();
    }
    private GameMediaStatus InitialStatus() => _hasCacheCandidate
        ? new("verifying", null, 0, 0, "等待游戏目录以验证已有图片缓存。", null)
        : new("waiting", null, 0, 0, "等待游戏图片目录就绪。", null);

    private static bool SamePlan(GameMediaPlan left, GameMediaPlan right)
    {
        // 目录顺序无业务含义，但 ID、名称、配方双身份、厨具以及重复食材的原始顺序均必须
        // 一致。不能只比图片数量/目录 hash，也不能用 record 默认的数组引用相等判断。
        if (left.SourceFingerprint != right.SourceFingerprint || left.StartupFingerprint != right.StartupFingerprint
            || left.Items.Count != right.Items.Count || left.Recipes.Count != right.Recipes.Count) return false;
        if (!left.Items.OrderBy(value => value.Key, StringComparer.Ordinal)
            .SequenceEqual(right.Items.OrderBy(value => value.Key, StringComparer.Ordinal))) return false;
        var a = left.Recipes.OrderBy(value => value.FoodId).ThenBy(value => value.RecipeId).ToArray();
        var b = right.Recipes.OrderBy(value => value.FoodId).ThenBy(value => value.RecipeId).ToArray();
        return a.Zip(b).All(pair => pair.First.FoodId == pair.Second.FoodId
            && pair.First.RecipeId == pair.Second.RecipeId && pair.First.CookerType == pair.Second.CookerType
            && pair.First.IngredientIds.SequenceEqual(pair.Second.IngredientIds));
    }
    private void RequireMainThread()
    {
        if (Environment.CurrentManagedThreadId != _mainThreadId) throw new InvalidOperationException("图片提取只能在创建服务的主线程推进。");
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _epoch++; _jobStop.Cancel(); }
        ReadinessChanged?.Invoke();
    }
}
