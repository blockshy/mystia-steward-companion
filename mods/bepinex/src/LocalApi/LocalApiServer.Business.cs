using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application;
using MystiaStewardCompanion.Business.Application.Automation;
using MystiaStewardCompanion.Business.Domain.GameUi;
using MystiaStewardCompanion.Business.Domain.Orders;
using MystiaStewardCompanion.Business.Domain.Support;
using MystiaStewardCompanion.Contracts;
using MystiaStewardCompanion.Save;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>
/// C# 业务宿主：单一后台循环合并最新快照，执行纯计算，再通过已有游戏主线程队列提交动作。
/// HTTP 线程只登记最多十六个页面意图并读取缓存；绝不在 Unity 主线程执行候选搜索。
/// </summary>
internal sealed partial class LocalApiServer
{
    private readonly object _businessLock = new();
    private readonly AutomationCoordinator _businessCoordinator = new();
    // 只缓存完整值输入对应的候选；每帧的订单代次、权限和执行状态始终重新绑定。
    private readonly OrderCandidateCache _businessCandidateCache = new();
    private readonly CancellationTokenSource _businessStop = new();
    // 无图片服务的隔离宿主保持既有行为；生产由 LocalApi 构造函数注入只读就绪判断。
    private Func<bool>? _gameMediaReady;
    // 通知只表达“至少有新输入”，最多保留一项；不为每个快照排队，也不让主线程等待计算。
    // 与停止令牌共同控制后台等待，不在主线程Dispose，避免和并发快照通知争用已释放对象。
    private readonly SemaphoreSlim _businessWake = new(0, 1);
    private long _businessInputVersion = 1;
    private BusinessFrame? _businessFrame;
    // 最近一次成功展示的来源独立保留；错误会撤销执行帧，但同作用域的只读结果仍可解释错误前状态。
    private BusinessFrame? _businessResultFrame;
    private BusinessPublication? _businessResult;
    private string? _businessError;
    // HTTP 只读活动标志：仅表示后台正在求解订单，不等同于结果新鲜度或执行许可。
    // 完整快照的观察字段持续变化时 pending 仍可为 true，不能据此让客户端永久转圈。
    private int _businessCalculationActive;
    // 由唯一业务循环维护：同一经营代次的持续异常只进入一次不可用状态。
    // 成功发布新结果/目标后清除，保证下一次真实故障仍会撤销新发布的内容。
    private long? _businessUiUnavailableGeneration;
    private string _businessLastPayload = "";
    private JsonObject? _businessLastOrderResult;
    private JsonObject? _businessSerializedOrderResult;
    private string _businessSerializedOrderPrefix = "{";
    // 仅唯一业务循环持有。解析树发布后只读，引用相同仅由原始全文完全相等的复用路径产生；
    // 这不是基于时间戳或目录 ID 猜测输入未变，也不缓存权威、租约或命令执行许可。
    private BusinessFrame? _businessCapturedFrame;
    private BusinessOrderInput? _businessLastOrderInput;
    private readonly BusinessLoopDiagnostics _businessLoopDiagnostics = new();
    private string _businessCatalogJson = "";
    private JsonObject _businessCatalog = RuntimeDataNormalizer.Build(null);
    private Task? _businessTask;

    /// <summary>
    /// 宿主私有值树从创建起只读，跨轮次、协调器 Complete 和 HTTP 的保留期内均不得原地修改。
    /// 新快照只整体替换引用；公开可变 JSON 入口仍复制，宿主领域视图则借用这些冻结组件。
    /// </summary>
    private sealed record BusinessFrame(long Version, string SnapshotSignature, string SnapshotJson, string CatalogJson,
        string FavoritesJson, string CustomRecipesJson, string ProfileJson, CompanionDeviceAuthorityStateDto Authority,
        JsonObject Snapshot, JsonObject Data, JsonObject Preferences, JsonObject Favorites, JsonObject CustomRecipes,
        JsonObject SourceContext);

    /// <summary>
    /// 发布边界只保存不可变字符串。推荐中不变的大字段按计算结果引用序列化一次，当前普客目标
    /// 独立重绑并编码；HTTP 拼接这些已验证 JSON 片段，不在轮询时深复制整棵方案树。
    /// Prefix 是未闭合对象（结尾为逗号或左花括号），其余片段均由 JSON 序列化器产生。
    /// </summary>
    private sealed record BusinessPublication(string Metadata, string RecommendationPrefix, string NormalTargets);

    private sealed record BusinessOrderInput(JsonObject Snapshot, JsonObject Data, JsonObject Preferences,
        JsonObject Favorites, JsonObject CustomRecipes, string[] RejectedRecipeKeys)
    {
        internal bool Matches(BusinessFrame frame, string[] rejected) => ReferenceEquals(Snapshot, frame.Snapshot)
            && ReferenceEquals(Data, frame.Data) && ReferenceEquals(Preferences, frame.Preferences)
            && ReferenceEquals(Favorites, frame.Favorites) && ReferenceEquals(CustomRecipes, frame.CustomRecipes)
            && RejectedRecipeKeys.SequenceEqual(rejected, StringComparer.Ordinal);
    }

    /// <summary>
    /// 精确输入相同的空闲轮次直接复用原始只读计算结果。真正输入变化仍按原完整 payload 字符串
    /// 比较，避免仅捕获时间变化引起额外求解；后续普客目标绑定始终返回独立副本，
    /// Advance 仍每轮取得新权威并推进单调时间，绝不因为命中此缓存而延长旧命令有效期。
    /// </summary>
    private JsonObject EvaluateBusinessOrders(BusinessFrame frame, out bool reused, out double calculationMs)
    {
        Interlocked.Exchange(ref _businessCalculationActive, 1);
        try { return EvaluateBusinessOrdersCore(frame, out reused, out calculationMs); }
        finally
        {
            // 命中缓存、计算失败和正常完成均离开活动状态；不把等待新快照或页面查询算成订单求解。
            Interlocked.Exchange(ref _businessCalculationActive, 0);
        }
    }

    private JsonObject EvaluateBusinessOrdersCore(BusinessFrame frame, out bool reused, out double calculationMs)
    {
        var rejected = _businessCoordinator.RejectedRecipeKeys();
        reused = _businessLastOrderResult != null && _businessLastOrderInput?.Matches(frame, rejected) == true;
        calculationMs = 0;
        if (reused) return _businessLastOrderResult!;
        string payloadJson;
        JsonObject payload;
        using (_businessLoopDiagnostics.Measure(BusinessLoopStage.Payload))
        {
            payload = BusinessQueries.BuildOrderPayload(frame.Snapshot, frame.Data, frame.Preferences,
                frame.Favorites, frame.CustomRecipes, J.Array(rejected));
            payloadJson = payload.ToJsonString();
        }
        reused = _businessLastOrderResult != null && payloadJson == _businessLastPayload;
        var elapsed = Stopwatch.StartNew();
        JsonObject result;
        using (_businessLoopDiagnostics.Measure(BusinessLoopStage.Recommendations))
        {
            result = reused ? _businessLastOrderResult! : OrderRecommendationService.Evaluate(payload, _businessCandidateCache);
            if (!reused) MaterializeBusinessTree(result);
        }
        calculationMs = elapsed.Elapsed.TotalMilliseconds;
        _businessLastPayload = payloadJson;
        _businessLastOrderResult = result;
        _businessLastOrderInput = new(frame.Snapshot, frame.Data, frame.Preferences, frame.Favorites, frame.CustomRecipes, rejected);
        return result;
    }


    private static long BusinessNow => Environment.TickCount64;
    private void InvalidateBusinessInput()
    {
        // 必须先撤销旧版本，再发通知。唤醒只缩短等待，不能延长旧帧或旧命令的有效期。
        Interlocked.Increment(ref _businessInputVersion);
        if (_businessWake.CurrentCount != 0) return;
        try { _businessWake.Release(); }
        catch (SemaphoreFullException) { /* 并发发布已放入同一个通知，合并即可。 */ }
    }
    private static bool IsBusinessInputMutation(string path) => path.StartsWith("/devices/", StringComparison.Ordinal)
        || path.StartsWith("/favorites/", StringComparison.Ordinal) || path.StartsWith("/custom-recipes/", StringComparison.Ordinal)
        || path.StartsWith("/inventory/", StringComparison.Ordinal) || path == "/orders/rare/dismiss";

    private void StartBusinessHost()
    {
        if (_businessTask != null) throw new InvalidOperationException("业务宿主不能重复启动。");
        _businessTask = Task.Run(BusinessLoop);
    }

    private void StopBusinessHost()
    {
        _businessStop.Cancel();
        InvalidateBusinessInput();
        _businessCoordinator.Stop();
        // 不 Join：Dispose 常由 Unity 主线程调用，而正在执行的委托可能等待该线程返回确定结果。
    }

    private async Task BusinessLoop()
    {
        while (!_businessStop.IsCancellationRequested)
        {
            var iteration = Stopwatch.StartNew();
            _businessLoopDiagnostics.Configure(AggregateModLogService.Enabled);
            var iterationMeasurement = _businessLoopDiagnostics.Measure(BusinessLoopStage.Iteration);
            var cacheHits = _businessCandidateCache.Hits;
            var cacheMisses = _businessCandidateCache.Misses;
            var reused = false; var stale = false; var failed = false;
            // 此前的所有通知都由接下来的最新快照覆盖；只取一次，不循环消费持续到来的通知。
            _businessWake.Wait(0);
            JsonObject? generatedAutomation = null;
            try
            {
                BusinessFrame frame;
                using (_businessLoopDiagnostics.Measure(BusinessLoopStage.Capture)) frame = CaptureBusinessFrame();
                var result = EvaluateBusinessOrders(frame, out reused, out var calculationMs);
                if (!IsBusinessFrameCurrent(frame))
                {
                    // 与 main 的 active/queued Worker 语义一致：已完成的计算即使落后于新观察，
                    // 仍可成为同作用域的最新只读展示。不能反复丢弃成功结果，让持续快照把页面饿死。
                    // 此分支绝不推进自动化、发布游戏辅助或把原签名重新盖成当前签名。
                    stale = true;
                    using (_businessLoopDiagnostics.Measure(BusinessLoopStage.Publish))
                        PublishCompletedBusinessDisplay(frame, result, calculationMs);
                    continue;
                }
                // 只重绑本轮需要更新身份的普客目标，稀客推荐与详情树保持冻结并供领域入口只读共享。
                JsonArray normalTargets;
                using (_businessLoopDiagnostics.Measure(BusinessLoopStage.Bind))
                    normalTargets = BusinessQueries.BindNormalTargetRows(result["normalExecutionTargets"], frame.Snapshot);

                JsonObject automation;
                using (_businessLoopDiagnostics.Measure(BusinessLoopStage.Automation))
                {
                    automation = AdvanceBusinessAutomation(frame, result, normalTargets, BusinessNow);
                }
                generatedAutomation = automation;
                if (!IsBusinessFrameCurrent(frame))
                {
                    stale = true;
                    CancelUnsubmittedBusinessCommands(generatedAutomation);
                    using (_businessLoopDiagnostics.Measure(BusinessLoopStage.Publish))
                        PublishCompletedBusinessDisplay(frame, result, calculationMs);
                    continue;
                }
                JsonObject gameUiTargets;
                using (_businessLoopDiagnostics.Measure(BusinessLoopStage.Targets)) gameUiTargets = PublishBusinessGameUi(frame, result, normalTargets);
                using (_businessLoopDiagnostics.Measure(BusinessLoopStage.Publish))
                {
                    // 大字段首次编码也在锁外完成；即使随后失效，仍必须经过锁内当前性检查才能发布。
                    var publication = CreateBusinessPublication(frame, result, normalTargets, gameUiTargets, calculationMs);
                    lock (_businessLock)
                    {
                        if (IsBusinessFrameCurrent(frame))
                        {
                            _businessFrame = frame;
                            _businessResultFrame = frame;
                            _businessResult = publication;
                            _businessError = null;
                            _businessUiUnavailableGeneration = null;
                        }
                        else
                        {
                            stale = true;
                            // 锁内不读取设备/快照等其它锁；下面统一走只读完成结果的来源校验。
                        }
                    }
                    if (stale) PublishCompletedBusinessDisplay(frame, result, calculationMs);
                }

                // 先发布只读页面结果。游戏动作会等待主线程并触发新快照，若将页面排在动作之后，
                // 自动化持续推进时每次都可能因原帧失效而跳过页面，导致页面永久没有成功结果。
                using (_businessLoopDiagnostics.Measure(BusinessLoopStage.Page)) ExecuteOnePageQuery(frame);
                using (_businessLoopDiagnostics.Measure(BusinessLoopStage.Commands))
                    foreach (var command in J.Objects(automation["commands"])) ExecuteBusinessCommand(frame, command);
            }
            catch (OperationCanceledException) when (_businessStop.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                failed = true;
                CancelUnsubmittedBusinessCommands(generatedAutomation);
                EnterBusinessInputUnavailable(exception);
            }
            finally
            {
                // 所有阶段已经离开业务/权威/缓存锁；诊断只能在此写入。每五秒最多一条，关闭
                // 聚合日志时不读取线程分配计数、不格式化。同步阶段不跨 await，避免线程切换污染分配量。
                iterationMeasurement.Dispose();
                _businessLoopDiagnostics.Complete(reused, stale, failed,
                    _businessCandidateCache.Hits - cacheHits, _businessCandidateCache.Misses - cacheMisses);
                try
                {
                    if (_businessLoopDiagnostics.TryDrain(Stopwatch.GetTimestamp(), Stopwatch.Frequency, out var report))
                    {
                        AggregateModLogService.AppendSection("performance", "Background business window", report);
                        // 与性能窗口共用五秒节流。只复制有界调度事实，随后离开协调器锁再写日志；
                        // 可区分缺料、等待厨具、未收敛推荐和暂停状态，不额外读取游戏对象。
                        AggregateModLogService.AppendSection("automation", "Scheduling state",
                            _businessCoordinator.SchedulingDiagnostics().ToJsonString());
                    }
                }
                catch { /* 诊断异常不得改变宿主重试、停止或授权语义。 */ }
                // 整轮至少50ms，连续变化也不会成为忙循环；空闲时最长再等250ms。
                // 计算期间合并的新输入直接唤醒下一轮，避免固定等待与250ms快照更新错相，
                // 让已经算完的当前结果只剩很短的可读取窗口。所有发布/执行版本检查保持不变。
                try
                {
                    var remaining = 50 - (int)Math.Min(50, iteration.ElapsedMilliseconds);
                    if (remaining > 0) await Task.Delay(remaining, _businessStop.Token).ConfigureAwait(false);
                    await _businessWake.WaitAsync(250, _businessStop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            }
        }
    }

    /// <summary>
    /// 将连续失败视为一次状态迁移，而非每个轮询周期都创建新的 UI 权威屏障。
    /// 首次失败立即撤销旧输入与目标；重复失败保留错误且继续重试，经营代次变化则撤销新代次一次。
    /// 这既避免无主设备/损坏输入期间反复推进目标代次，也不会吞掉不同的错误原因或阻止恢复。
    /// </summary>
    private void EnterBusinessInputUnavailable(Exception exception)
    {
        var generation = RuntimeNightBusinessLifecycle.Generation;
        if (_businessUiUnavailableGeneration != generation)
        {
            InvalidateBusinessInput();
            RuntimeUiPinningService.ClearTargetsForAuthorityTransition(
                generation, "C# 业务输入不可用，等待新帧恢复。");
            _businessUiUnavailableGeneration = generation;
        }

        // 保留诊断但不发布为当前结果；后续周期允许从新输入恢复，不自动回退到客户端算法。
        lock (_businessLock)
        {
            var message = exception.GetBaseException().Message;
            if (_businessError != message) _log.LogWarning($"C# business input unavailable: {message}");
            _businessError = message;
            _businessFrame = null;
        }
    }

    /// <summary>计算后失效或宿主异常时显式撤销未提交命令，防止领域状态永久保留 in-flight 占位。</summary>
    private void CancelUnsubmittedBusinessCommands(JsonObject? generatedAutomation)
    {
        foreach (var command in J.Objects(generatedAutomation?["commands"]))
            _businessCoordinator.Abandon((long)J.Num(command["requestId"]));
    }

    private BusinessFrame CaptureBusinessFrame()
    {
        if (_gameMediaReady?.Invoke() == false) throw new InvalidOperationException("游戏图片尚未就绪，业务暂停。");
        lock (_authorityTransitionLock)
        {
            var authority = _deviceAuthorityStore.ReadBusinessState(DateTime.UtcNow);
            string snapshotJson, catalogJson, signature; long version;
            lock (_snapshotLock)
            {
                snapshotJson = _snapshotJson; catalogJson = _runtimeDataJson; signature = _snapshotSignature;
                version = Interlocked.Read(ref _businessInputVersion);
            }
            var favoritesJson = _favoriteStore.GetJson();
            var customJson = _customRecipeStore.GetJson();
            // 收藏文件亦可由合法外部编辑更新；发现内容变化时统一撤销旧结果，不以时间戳猜测。
            var previous = _businessFrame;
            if (previous != null && previous.Version == version
                && (previous.FavoritesJson != favoritesJson || previous.CustomRecipesJson != customJson))
            {
                version = Interlocked.Increment(ref _businessInputVersion);
            }
            if (_businessCatalogJson != catalogJson)
            {
                _businessCatalog = RuntimeDataNormalizer.Build(JsonNode.Parse(catalogJson));
                MaterializeBusinessTree(_businessCatalog);
                _businessCatalogJson = catalogJson;
            }
            var captured = _businessCapturedFrame;
            var profileJson = authority.ActiveProfile.GetRawText();
            // 文件仍逐次读取，保留合法外部编辑检测；只是对完全相同的原始正文复用已经解析的
            // 私有只读树。业务版本、设备在线状态和权威对象每轮重新读取，不复用旧许可。
            var snapshot = captured?.SnapshotJson == snapshotJson ? captured.Snapshot : JsonNode.Parse(snapshotJson)!.AsObject();
            var preferences = captured?.ProfileJson == profileJson ? captured.Preferences : JsonNode.Parse(profileJson)!.AsObject();
            var favorites = captured?.FavoritesJson == favoritesJson ? captured.Favorites : JsonNode.Parse(favoritesJson)!.AsObject();
            var custom = captured?.CustomRecipesJson == customJson ? captured.CustomRecipes : JsonNode.Parse(customJson)!.AsObject();
            if (!ReferenceEquals(snapshot, captured?.Snapshot)) MaterializeBusinessTree(snapshot);
            if (!ReferenceEquals(preferences, captured?.Preferences)) MaterializeBusinessTree(preferences);
            if (!ReferenceEquals(favorites, captured?.Favorites)) MaterializeBusinessTree(favorites);
            if (!ReferenceEquals(custom, captured?.CustomRecipes)) MaterializeBusinessTree(custom);
            var frame = new BusinessFrame(version, signature, snapshotJson, catalogJson, favoritesJson, customJson, profileJson, authority,
                snapshot, _businessCatalog, preferences, favorites, custom, BuildBusinessSourceContext(snapshot, authority));
            _businessCapturedFrame = frame;
            return frame;
        }
    }

    /// <summary>
    /// 在唯一生产线程完成 JSON 容器的懒展开，再交给只读消费者。锁定游戏运行时的
    /// System.Text.Json 6.0.7 在首次展开对象时会先清 JsonElement、后设置字典；仅“不主动修改”
    /// 仍不足以安全并发读取。递归枚举每个容器一次，后续只共享已物化节点，不每轮遍历旧树。
    /// ToJsonString/WriteTo 会直接写尚未展开的 JsonElement，因此不能替代这个所有权边界。
    /// </summary>
    private static void MaterializeBusinessTree(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var field in obj) MaterializeBusinessTree(field.Value);
        else if (node is JsonArray array)
            foreach (var value in array) MaterializeBusinessTree(value);
    }

    private static JsonObject BuildBusinessSourceContext(JsonObject snapshot, CompanionDeviceAuthorityStateDto authority)
        => BusinessDisplayContext.Build(snapshot, authority.RegistryId, authority.AuthorityRevision,
            authority.ActiveProfileRevision, authority.ActiveProfileHash);

    /// <summary>
    /// HTTP 返回旧展示前复核最新场景、目录和权威配置，避免等待后台下一轮才清除跨作用域内容。
    /// 此读取不触碰 Unity、不续租、不刷新在线心跳，也不影响执行使用的完整帧版本门禁。
    /// </summary>
    private JsonObject? ReadBusinessSourceContext(string expectedCatalogJson)
    {
        try
        {
            lock (_authorityTransitionLock)
            {
                var authority = _deviceAuthorityStore.ReadBusinessState(DateTime.UtcNow);
                string snapshotJson;
                lock (_snapshotLock)
                {
                    // 目录可能先于对应快照签名发布；直接比较原始目录值，不能短暂继承旧目录结果。
                    if (!string.Equals(_runtimeDataJson, expectedCatalogJson, StringComparison.Ordinal)) return null;
                    snapshotJson = _snapshotJson;
                }
                return BuildBusinessSourceContext(JsonNode.Parse(snapshotJson)!.AsObject(), authority);
            }
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException
            or InvalidOperationException or CompanionDeviceAuthorityException)
        {
            // 真实错误仍由业务循环记录并返回；不能为显示旧结果猜测损坏或尚未建立的来源作用域。
            return null;
        }
    }

    /// <summary>此方法也在 Unity 队列开始执行时调用，必须只读托管版本且不取得 authority-transition 锁。</summary>
    private bool IsBusinessFrameCurrent(BusinessFrame frame) => !_businessStop.IsCancellationRequested
        && (_gameMediaReady?.Invoke() ?? true)
        && frame.Version == Interlocked.Read(ref _businessInputVersion)
        && frame.Authority.AuthorityRevision == _deviceAuthorityStore.ReadAuthorityRevision();

    /// <summary>
    /// 夜间副作用必须同时属于已采集的 Active 快照和当前真实 Active 代次。白天仍可使用当前业务帧
    /// 计算只读推荐，因此本判断独立于 IsBusinessFrameCurrent；缺失、未知或非精确阶段不能猜测为营业中。
    /// 生命周期快照是不可变托管值，排队许可复核可直接读取，不获取主线程/权威/租约锁。
    /// </summary>
    private static bool IsBusinessNightRuntimeCurrent(BusinessFrame frame)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        var generation = (long)J.Num(frame.Snapshot["nightBusinessGeneration"]);
        return string.Equals(J.Str(frame.Snapshot["nightBusinessLifecyclePhase"]), "Active", StringComparison.Ordinal)
            && generation > 0 && lifecycle.IsActive && lifecycle.Generation == generation;
    }

    private JsonObject BuildBusinessAutomationAuthority(BusinessFrame frame)
    {
        lock (_authorityTransitionLock)
        lock (_automationLeaseLock)
        {
            var now = DateTime.UtcNow;
            PruneExpiredAutomationLease(now);
            var allowed = IsBusinessFrameCurrent(frame) && IsBusinessNightRuntimeCurrent(frame)
                && J.Str(frame.Data["source"]) == "runtime" && frame.SnapshotSignature.Length > 0
                && _deviceAuthorityStore.TryAuthorizePrimary(frame.Authority.PrimaryDeviceId,
                    frame.Authority.AuthorityRevision, now, out _, recordActivity: false);
            var owned = allowed && _automationLease != null
                && _automationLease.ClientId == frame.Authority.PrimaryDeviceId
                && _automationLease.AuthorityRevision == frame.Authority.AuthorityRevision;
            return J.Object(("sessionId", frame.Snapshot["automationSessionId"]),
                ("businessGeneration", frame.Snapshot["nightBusinessGeneration"]),
                ("authorityRevision", frame.Authority.AuthorityRevision), ("automationEpoch", _automationCommandEpoch),
                ("leaseOwned", owned), ("allowed", allowed),
                ("leaseExpiresAtUnixMs", owned ? new DateTimeOffset(_automationLease!.ExpiresAtUtc).ToUnixTimeMilliseconds() : 0));
        }
    }

    private void ExecuteBusinessCommand(BusinessFrame frame, JsonObject command)
    {
        var requestId = (long)J.Num(command["requestId"]);
        var authority = BuildBusinessAutomationAuthority(frame);
        if (!J.Bool(authority["leaseOwned"]) || J.Num(authority["automationEpoch"]) != J.Num(command["automationEpoch"]))
        {
            _businessCoordinator.Abandon(requestId);
            return;
        }
        var action = J.Str(command["action"]);
        Func<OrderPreparationRequest, OrderPreparationResult> handler = action switch
        {
            "prepare-rare" => _prepareOrder, "complete-rare" => _completeOrder, "complete-normal" => _completeNormalOrder,
            _ => throw new InvalidOperationException("业务核心生成了未知动作。"),
        };
        // 固定本次准入时的租约截止时间。主线程排队期间即使没有新快照、没有触发租约清理，
        // 自然到期也必须阻止尚未开始的动作；后续续约不能追认旧排队命令，应由下一轮重新计算。
        // 许可闭包只读不可变截止时间与托管版本，不能获取 authority/lease 锁，避免与主线程 fence 反锁。
        var leaseExpiresAtUnixMs = (long)J.Num(authority["leaseExpiresAtUnixMs"]);
        // 共用原有请求解析与游戏适配委托；epoch 和输入许可由宿主注入，客户端无法自行提供。
        var response = BuildOrderActionJson(ToBusinessQuery(J.Obj(command["payload"])), handler,
            (long)J.Num(authority["automationEpoch"]), () => IsBusinessFrameCurrent(frame) && IsBusinessNightRuntimeCurrent(frame)
                && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < leaseExpiresAtUnixMs);
        _businessCoordinator.Complete(requestId, JsonNode.Parse(response)!.AsObject(), BusinessNow);
    }

    /// <summary>
    /// 每轮仍重新读取权威并推进时间，冻结组件仅省去 JSON 包装与复制。协调器可在后续完成回调中
    /// 持有视图，因此所有组件（包括本轮新建的目标和权威）在传入后都不可再被宿主修改。
    /// </summary>
    private JsonObject AdvanceBusinessAutomation(BusinessFrame frame, JsonObject result, JsonArray normalTargets, long now)
        => _businessCoordinator.AdvanceFrozen(AutomationInputView.FromFrozenComponents(
            frame.Snapshot, frame.Preferences, frame.Data, frame.Favorites, BuildBusinessAutomationAuthority(frame),
            new AutomationResultView(J.Arr(result["recommendations"]), true, false, null),
            new AutomationResultView(normalTargets, true, false, null)), now);

    /// <summary>
    /// 大推荐树只有引用变化时才重新编码。这里按原字段顺序写入除普客目标外的所有字段；不筛除
    /// 内层详情、阻断原因或未来新增字段，避免性能优化悄悄裁减前端协议。
    /// </summary>
    private BusinessPublication CreateBusinessPublication(BusinessFrame frame, JsonObject result,
        JsonArray normalTargets, JsonObject gameUiTargets, double calculationMs)
    {
        if (!ReferenceEquals(_businessSerializedOrderResult, result))
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var field in result)
                {
                    if (field.Key == "normalExecutionTargets") continue;
                    writer.WritePropertyName(field.Key);
                    if (field.Value == null) writer.WriteNullValue(); else field.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            var json = Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
            _businessSerializedOrderPrefix = json.Length == 2 ? "{" : json[..^1] + ",";
            _businessSerializedOrderResult = result;
        }
        var metadata = J.Object(("protocolVersion", BusinessProtocol.Version),
            ("inputVersion", frame.Version.ToString(CultureInfo.InvariantCulture)),
            ("sourceSnapshotSignature", frame.SnapshotSignature), ("sourceContext", frame.SourceContext),
            ("authorityRevision", frame.Authority.AuthorityRevision),
            ("runtimeSets", Cookers.BuildRuntimeSets(frame.Snapshot["recommendationState"], frame.Data)),
            ("gameUiTargets", gameUiTargets), ("calculationMs", calculationMs));
        return new(metadata.ToJsonString(), _businessSerializedOrderPrefix, normalTargets.ToJsonString());
    }

    /// <summary>
    /// 保存已完成但过期的成功计算，供当前场景查看。来源作用域必须仍相同，旧快照版本与签名
    /// 原样保留，因此 HTTP 的 isCurrent 仍为 false。执行帧、自动化输入和游戏高亮均不更新；
    /// 特别清空普客执行目标与辅助投影，不能把“允许展示”误解释为重新授予旧目标资格。
    /// 列表中的实时订单事实由客户端当前 snapshot 驱动，只有精确匹配的方案可以附加展示。
    /// </summary>
    private bool PublishCompletedBusinessDisplay(BusinessFrame frame, JsonObject result, double calculationMs)
    {
        if (_businessStop.IsCancellationRequested || _gameMediaReady?.Invoke() == false
            || !BusinessDisplayContext.Matches(frame.SourceContext, ReadBusinessSourceContext(frame.CatalogJson))) return false;
        var publication = CreateBusinessPublication(frame, result, new JsonArray(),
            J.Object(("rare", null), ("normal", null)), calculationMs);
        lock (_businessLock)
        {
            if (_businessStop.IsCancellationRequested || _gameMediaReady?.Invoke() == false
                || _businessResultFrame != null && _businessResultFrame.Version > frame.Version) return false;
            _businessResultFrame = frame;
            _businessResult = publication;
            // 只有唯一业务循环写入错误；本轮捕获和求解已成功，撤销其此前的输入错误。
            // 不设置 _businessFrame，也不允许先前的执行帧获得新版本。
            _businessError = null;
            // HTTP 每次仍用最新来源上下文复核；校验后切场也不会把跨场景结果暴露给客户端。
            return true;
        }
    }

    private JsonObject PublishBusinessGameUi(BusinessFrame frame, JsonObject result, JsonArray normalTargets)
    {
        var slots = J.Object(("rare", null), ("normal", null));
        // Closing/Destroyed 会由既有生命周期边界撤销游戏目标；此处既不提交空集合，也不反复 Clear。
        // 只读经营结果和稀客/普客页面不依赖夜间 UI 发布，白天保留上一营业代次也应正常计算。
        if (!IsBusinessNightRuntimeCurrent(frame)) return slots;
        var targets = new List<RuntimeUiTargetSnapshot>();
        var online = frame.Authority.Devices.Any(device => device.IsPrimary && device.Online && !device.SyncPending);
        // 模块停用时仍发布空目标以撤销上一轮效果，不能提前返回留下旧高亮。
        // 分项偏好保持不变，重新启用后按已保存的普客/稀客配置生成目标。
        if (online && J.Bool(frame.Preferences["gameUiEnabled"])
            && J.Bool(frame.Snapshot["nightBusinessAutomationAllowed"]))
        {
            foreach (var kind in new[] { "rare", "normal" })
            {
                var preferences = frame.Preferences;
                var features = J.Object(("listPinningEnabled", preferences[kind + "GameUiPinningEnabled"]),
                    ("recipeVariantEnabled", preferences[kind + "RecipeVariantEnabled"]),
                    ("cookerHighlightEnabled", preferences[kind + "CookerHighlightEnabled"]),
                    ("seatHighlightEnabled", preferences[kind + "SeatHighlightEnabled"]),
                    ("orderHighlightEnabled", preferences[kind + "OrderHighlightEnabled"]));
                if (!features.Any(item => J.Bool(item.Value))) continue;
                // 两类目标同步只读同一批冻结组件；普客无需复制稀客整套方案，也无需把目录挂进新父节点。
                var args = new FrozenGameUiTargetInput(kind, frame.Data,
                    Recommendations: result["recommendations"], Orders: frame.Snapshot["normalBusiness"]?["orders"],
                    ExecutionTargets: normalTargets, ExecutionTargetsCurrent: true,
                    Color: J.Str(preferences[kind + "TargetHighlightColor"]),
                    Features: features, SpecialBusiness: frame.Snapshot["specialBusiness"],
                    BusinessGeneration: J.Num(frame.Snapshot["nightBusinessGeneration"]),
                    PrioritizeMissionRecipe: J.Bool(preferences["missionRecipePriorityEnabled"])
                        && !J.Bool(frame.Snapshot["specialBusiness"]?["active"]));
                if (GameUiTargetService.Evaluate(args) is not JsonObject target) continue;
                slots[kind] = J.Clone(target);
                var wire = new JsonObject();
                foreach (var field in new[] { "kind", "color", "traceId", "orderKey", "orderLifecycleSequence", "deskCode", "recipeId", "ingredientIds", "extraIngredientIds", "beverageId", "cookerTypeId" })
                    wire["target0" + char.ToUpperInvariant(field[0]) + field[1..]] = J.Clone(target[field]);
                // 偏好与业务投影沿用 main 的 CSS 颜色格式（#RRGGBB），游戏目标协议则只接收 RRGGBB。
                // 仅在适配边界去掉一个明确的前缀；后续仍由真实协议解析器严格校验大写十六进制，
                // 不改写业务返回值，也不以 TrimStart 或大小写转换掩盖损坏输入。
                var color = J.Str(target["color"]);
                if (color.Length != 7 || color[0] != '#')
                    throw new FormatException("Business UI target color must use the #RRGGBB format.");
                wire["target0Color"] = color[1..];
                wire["target0Revision"] = J.Clone(target["targetRevision"]);
                foreach (var field in features) wire["target0" + char.ToUpperInvariant(field.Key[0]) + field.Key[1..]] = J.Clone(field.Value);
                targets.Add(ReadUiPinningTarget(ToBusinessQuery(wire), 0));
            }
        }
        lock (_authorityTransitionLock)
        {
            // 构造目标期间可能切场；旧帧不得发布到下一代。真正发布内部仍保留更靠近提交的严格门禁，
            // 若校验之后再切场，本轮按原错误路径失败关闭；下一轮跳过非 Active 发布后恢复只读结果。
            if (!IsBusinessFrameCurrent(frame) || !IsBusinessNightRuntimeCurrent(frame))
                return J.Object(("rare", null), ("normal", null));
            RuntimeUiPinningService.UpdateTargets((long)J.Num(frame.Snapshot["nightBusinessGeneration"]), targets);
            // 即使后续构造状态或提交命令失败，本轮已发布的目标也必须再次撤销。
            _businessUiUnavailableGeneration = null;
        }
        return slots;
    }

    private string GetBusinessStatusJson(string query)
    {
        if (ReadIntQuery(query, "protocolVersion", 0) != BusinessProtocol.Version) return BusinessProtocolUpgradeRequired();
        BusinessPublication? published; BusinessFrame? frame; string? error;
        lock (_businessLock)
        {
            // 发布结果和帧只整体替换，发布后不再修改。锁内仅获取一致引用，不让HTTP深复制与序列化阻塞新帧发布。
            published = _businessResult; frame = _businessResultFrame; error = _businessError;
        }
        var sameContext = frame != null && BusinessDisplayContext.Matches(frame.SourceContext, ReadBusinessSourceContext(frame.CatalogJson));
        var current = sameContext && frame != null && IsBusinessFrameCurrent(frame) && error == null;
        // 自动化状态仍逐次读取，不能与推荐一起缓存，否则超时、完成、重试和租约变化会被遮蔽。
        var status = new JsonObject
        {
            ["isCurrent"] = current, ["pending"] = !current && error == null, ["error"] = error,
            ["isCalculating"] = Volatile.Read(ref _businessCalculationActive) != 0,
            ["automation"] = sameContext ? _businessCoordinator.Snapshot() : null,
        };
        if (!sameContext || published == null)
        {
            status["protocolVersion"] = BusinessProtocol.Version;
            return status.ToJsonString();
        }
        // 只拼接序列化器产生的对象边界，不直接插入用户文本；唯一大型分配是必要的 HTTP 正文。
        var suffix = status.ToJsonString();
        return string.Concat(published.Metadata[..^1],
            ",\"recommendations\":", published.RecommendationPrefix, "\"normalExecutionTargets\":",
            published.NormalTargets, "},", suffix[1..]);
    }


    private string RetryBusinessAutomation(string request, HttpRequestData requestData)
    {
        var intent = ReadJsonRequest<BusinessRetryRequest>(requestData,
            "protocolVersion", "automationSessionId", "businessGeneration", "kind", "key");
        if (intent.Kind is not ("rare" or "normal") || string.IsNullOrWhiteSpace(intent.Key) || intent.Key.Length > 300)
            throw new CompanionDeviceAuthorityException(400, "重试订单标识无效。");
        lock (_authorityTransitionLock)
        {
            RequireBusinessInteractionScope(intent.ProtocolVersion, intent.AutomationSessionId, intent.BusinessGeneration);
            if (!TryRequireAutomationLease(request, out var error, out _)) return ToJson(error);
            var ok = _businessCoordinator.Retry(intent.Kind, intent.Key, BusinessNow);
            return J.Object(("ok", ok), ("error", ok ? null : "订单已过期或仍有需要人工确认的事项，不能重试。")).ToJsonString();
        }
    }

    /// <summary>
    /// 单订单重置只修改业务规划；复用主设备租约、真实生命周期与图片就绪检查，并要求当前
    /// 已发布帧仍然有效。协调器还比较冻结输入引用，防止核验后后台已推进到另一个订单快照。
    /// </summary>
    private string ResetBusinessAutomation(string request, HttpRequestData requestData)
    {
        var intent = ReadJsonRequest<BusinessRetryRequest>(requestData,
            "protocolVersion", "automationSessionId", "businessGeneration", "kind", "key");
        if (intent.Kind is not ("rare" or "normal") || string.IsNullOrWhiteSpace(intent.Key) || intent.Key.Length > 300)
            throw new CompanionDeviceAuthorityException(400, "重置订单标识无效。");
        lock (_authorityTransitionLock)
        {
            RequireBusinessInteractionScope(intent.ProtocolVersion, intent.AutomationSessionId, intent.BusinessGeneration);
            if (!TryRequireAutomationLease(request, out var error, out _)) return ToJson(error);
            BusinessFrame? frame;
            lock (_businessLock) frame = _businessFrame;
            if (frame == null || !IsBusinessFrameCurrent(frame) || !IsBusinessNightRuntimeCurrent(frame))
                throw new CompanionDeviceAuthorityException(409, "经营状态正在更新，请刷新后重新确认。");
            return _businessCoordinator.Reset(intent.Kind, intent.Key, intent.AutomationSessionId,
                intent.BusinessGeneration, frame.Snapshot, BusinessNow).ToJsonString();
        }
    }

    /// <summary>快照身份和真实生命周期同时复核；不依赖下一次轮询清理旧窗口的操作。</summary>
    private void RequireBusinessInteractionScope(int protocolVersion, string sessionId, long generation)
    {
        try
        {
            lock (_snapshotLock)
                ClientProtocol.ValidateInteractionScope(protocolVersion, sessionId, generation,
                    JsonNode.Parse(_snapshotJson)!.AsObject());
            var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
            if (!lifecycle.IsActive || lifecycle.Generation != generation)
                throw new ArgumentException("经营场景已经关闭或切换，不能提交旧人工操作。");
        }
        catch (ArgumentException exception)
        {
            throw new CompanionDeviceAuthorityException(409, exception.Message);
        }
    }

    private sealed class BusinessBarrierAckRequest
    {
        public int ProtocolVersion { get; init; }
        public string AutomationSessionId { get; init; } = "";
        public long BusinessGeneration { get; init; }
        public long Sequence { get; init; }
    }

    private sealed class BusinessRetryRequest
    {
        public int ProtocolVersion { get; init; }
        public string AutomationSessionId { get; init; } = "";
        public long BusinessGeneration { get; init; }
        public string Kind { get; init; } = "";
        public string Key { get; init; } = "";
    }

    private static string BusinessProtocolUpgradeRequired() => J.Object(("ok", false),
        ("protocolVersion", BusinessProtocol.Version), ("isCurrent", false), ("pending", false),
        ("isCalculating", false),
        ("error", "业务协议版本不匹配，请安装同一版本的客户端与 Mod 完整包。")).ToJsonString();

    /// <summary>复用游戏适配器的严格字段解析；数组按该内部参数格式使用逗号分隔。</summary>
    private static string ToBusinessQuery(JsonObject payload) => string.Join("&", payload.Select(field =>
        Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value is JsonArray array
            ? string.Join(",", array.Select(BusinessScalar)) : BusinessScalar(field.Value))));
    private static string BusinessScalar(JsonNode? value) => value == null ? "" : value is JsonValue scalar
        && scalar.TryGetValue<string>(out var text) ? text : value.ToJsonString();
}
