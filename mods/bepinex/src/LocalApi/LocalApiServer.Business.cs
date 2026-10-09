using System.Diagnostics;
using System.Globalization;
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
    private readonly Dictionary<string, PageQuery> _businessPageQueries = new(StringComparer.Ordinal);
    private long _businessInputVersion = 1;
    private BusinessFrame? _businessFrame;
    // 最近一次成功展示的来源独立保留；错误会撤销执行帧，但同作用域的只读结果仍可解释错误前状态。
    private BusinessFrame? _businessResultFrame;
    private JsonObject? _businessResult;
    private string? _businessError;
    // 由唯一业务循环维护：同一经营代次的持续异常只进入一次不可用状态。
    // 成功发布新结果/目标后清除，保证下一次真实故障仍会撤销新发布的内容。
    private long? _businessUiUnavailableGeneration;
    private string _businessLastPayload = "";
    private JsonObject? _businessLastOrderResult;
    private string _businessCatalogJson = "";
    private JsonObject _businessCatalog = RuntimeDataNormalizer.Build(null);
    private long _businessQuerySequence;
    private Task? _businessTask;

    /// <summary>帧内 JSON 仅由后台线程读取；发布后不修改，响应与领域调用均复制值树。</summary>
    private sealed record BusinessFrame(long Version, string SnapshotSignature, string SnapshotJson, string CatalogJson,
        string FavoritesJson, string CustomRecipesJson, CompanionDeviceAuthorityStateDto Authority,
        JsonObject Snapshot, JsonObject Data, JsonObject Preferences, JsonObject Favorites, JsonObject CustomRecipes,
        JsonObject SourceContext);

    private sealed class PageQuery
    {
        public string Key { get; init; } = "";
        public string IntentJson { get; init; } = "";
        public long Sequence { get; set; }
        public long Version { get; set; }
        public JsonObject? Result { get; set; }
        public string? Error { get; set; }
        public JsonObject? SourceContext { get; set; }
        public string SourceSnapshotSignature { get; set; } = "";
        public string SourceCatalogJson { get; set; } = "";
    }

    private static long BusinessNow => Environment.TickCount64;
    private void InvalidateBusinessInput() => Interlocked.Increment(ref _businessInputVersion);
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
            JsonObject? generatedAutomation = null;
            try
            {
                var frame = CaptureBusinessFrame();
                var rejected = J.Arr(_businessCoordinator.Snapshot()["rejectedRecipeKeys"]);
                var payload = BusinessQueries.BuildOrderPayload(frame.Snapshot, frame.Data, frame.Preferences,
                    frame.Favorites, frame.CustomRecipes, rejected);
                var payloadJson = payload.ToJsonString();
                var elapsed = Stopwatch.StartNew();
                var result = _businessLastOrderResult != null && payloadJson == _businessLastPayload
                    ? J.Obj(J.Clone(_businessLastOrderResult)) : OrderRecommendationService.Evaluate(payload, _businessCandidateCache);
                elapsed.Stop();
                _businessLastPayload = payloadJson;
                _businessLastOrderResult = result;
                if (!IsBusinessFrameCurrent(frame)) continue;
                result = BusinessQueries.BindCurrentNormalTargets(result, frame.Snapshot);

                var authority = BuildBusinessAutomationAuthority(frame);
                var currentRecommendations = J.Object(("recommendations", result["recommendations"]),
                    ("isCurrent", true), ("pending", false), ("error", null));
                var normalTargets = J.Object(("targets", result["normalExecutionTargets"]),
                    ("isCurrent", true), ("pending", false), ("error", null));
                var automation = _businessCoordinator.Advance(J.Object(("snapshot", frame.Snapshot),
                    ("preferences", frame.Preferences), ("recommendations", currentRecommendations),
                    ("normalExecutionTargets", normalTargets), ("data", frame.Data), ("favorites", frame.Favorites),
                    ("authority", authority)), BusinessNow);
                generatedAutomation = automation;
                if (!IsBusinessFrameCurrent(frame))
                {
                    CancelUnsubmittedBusinessCommands(generatedAutomation);
                    continue;
                }
                var gameUiTargets = PublishBusinessGameUi(frame, result);
                lock (_businessLock)
                {
                    if (IsBusinessFrameCurrent(frame))
                    {
                        _businessFrame = frame;
                        _businessResultFrame = frame;
                        _businessResult = J.Object(("protocolVersion", BusinessProtocol.Version),
                            ("inputVersion", frame.Version.ToString(CultureInfo.InvariantCulture)),
                            ("sourceSnapshotSignature", frame.SnapshotSignature),
                            ("sourceContext", frame.SourceContext),
                            ("authorityRevision", frame.Authority.AuthorityRevision),
                            ("runtimeSets", Cookers.BuildRuntimeSets(frame.Snapshot["recommendationState"], frame.Data)),
                            ("gameUiTargets", gameUiTargets),
                            ("recommendations", result), ("calculationMs", elapsed.Elapsed.TotalMilliseconds));
                        _businessError = null;
                        _businessUiUnavailableGeneration = null;
                    }
                }

                // 先发布只读页面结果。游戏动作会等待主线程并触发新快照，若将页面排在动作之后，
                // 自动化持续推进时每次都可能因原帧失效而跳过页面，导致页面永久没有成功结果。
                ExecuteOnePageQuery(frame);
                foreach (var command in J.Objects(automation["commands"])) ExecuteBusinessCommand(frame, command);
            }
            catch (OperationCanceledException) when (_businessStop.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                CancelUnsubmittedBusinessCommands(generatedAutomation);
                EnterBusinessInputUnavailable(exception);
            }
            finally
            {
                // 即使输入连续变化也让出线程，避免被丢弃的计算形成忙循环。
                try { await Task.Delay(250, _businessStop.Token).ConfigureAwait(false); }
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
                _businessCatalogJson = catalogJson;
            }
            var snapshot = JsonNode.Parse(snapshotJson)!.AsObject();
            return new BusinessFrame(version, signature, snapshotJson, catalogJson, favoritesJson, customJson, authority,
                snapshot, _businessCatalog,
                JsonNode.Parse(authority.ActiveProfile.GetRawText())!.AsObject(),
                JsonNode.Parse(favoritesJson)!.AsObject(), JsonNode.Parse(customJson)!.AsObject(),
                BuildBusinessSourceContext(snapshot, authority));
        }
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

    private JsonObject PublishBusinessGameUi(BusinessFrame frame, JsonObject result)
    {
        var slots = J.Object(("rare", null), ("normal", null));
        // Closing/Destroyed 会由既有生命周期边界撤销游戏目标；此处既不提交空集合，也不反复 Clear。
        // 只读经营结果和稀客/普客页面不依赖夜间 UI 发布，白天保留上一营业代次也应正常计算。
        if (!IsBusinessNightRuntimeCurrent(frame)) return slots;
        var targets = new List<RuntimeUiTargetSnapshot>();
        var online = frame.Authority.Devices.Any(device => device.IsPrimary && device.Online);
        if (online && J.Bool(frame.Snapshot["nightBusinessAutomationAllowed"]))
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
                var args = J.Object(("operation", kind), ("data", frame.Data),
                    ("recommendations", result["recommendations"]), ("orderSortMode", preferences["serviceOrderSortMode"]),
                    ("color", preferences[kind + "TargetHighlightColor"]), ("features", features),
                    ("orders", J.Obj(frame.Snapshot["normalBusiness"])["orders"]),
                    ("executionTargets", result["normalExecutionTargets"]), ("executionTargetsCurrent", true),
                    ("specialBusiness", frame.Snapshot["specialBusiness"]), ("businessGeneration", frame.Snapshot["nightBusinessGeneration"]),
                    ("options", J.Object(("prioritizeMissionRecipe", J.Bool(preferences["missionRecipePriorityEnabled"])
                        && !J.Bool(frame.Snapshot["specialBusiness"]?["active"])), ("specialBusiness", frame.Snapshot["specialBusiness"]))));
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
        JsonObject? published; BusinessFrame? frame; string? error;
        lock (_businessLock)
        {
            // 发布结果和帧只整体替换，发布后不再修改。锁内仅获取一致引用，不让HTTP深复制与序列化阻塞新帧发布。
            published = _businessResult; frame = _businessResultFrame; error = _businessError;
        }
        var sameContext = frame != null && BusinessDisplayContext.Matches(frame.SourceContext, ReadBusinessSourceContext(frame.CatalogJson));
        var result = sameContext ? J.Obj(J.Clone(published)) : new JsonObject();
        var current = sameContext && frame != null && IsBusinessFrameCurrent(frame) && error == null;
        result["protocolVersion"] = BusinessProtocol.Version; result["isCurrent"] = current;
        result["pending"] = !current && error == null; result["error"] = error;
        result["automation"] = sameContext ? _businessCoordinator.Snapshot() : null;
        return result.ToJsonString();
    }

    private string QueueBusinessPageQuery(string request, HttpRequestData requestData)
    {
        var (clientId, _) = ReadRequiredClientIdentity(request);
        var raw = HttpRequestReader.ReadRequiredJsonBody(requestData);
        JsonObject intent;
        try { intent = BusinessProtocol.ParsePageQuery(raw); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { throw new CompanionDeviceAuthorityException(400, exception.Message); }
        var json = intent.ToJsonString(); var key = clientId + ":" + J.Str(intent["kind"]);
        JsonObject? result; JsonObject? sourceContext; string sourceSignature; string sourceCatalogJson;
        BusinessFrame? frame; long version; string? queryError; string? businessError;
        lock (_businessLock)
        {
            if (!_businessPageQueries.TryGetValue(key, out var query) || query.IntentJson != json)
            {
                if (_businessPageQueries.Count >= BusinessProtocol.MaxPageQueries && !_businessPageQueries.ContainsKey(key))
                    _businessPageQueries.Remove(_businessPageQueries.Values.OrderBy(item => item.Sequence).First().Key);
                query = new PageQuery { Key = key, IntentJson = json, Sequence = ++_businessQuerySequence };
                _businessPageQueries[key] = query;
            }
            // 页面投影与经营结果一样是发布后只读值；仅查询注册与引用快照需要互斥，JSON复制在锁外完成。
            result = query.Result; version = query.Version; queryError = query.Error;
            sourceContext = query.SourceContext; sourceSignature = query.SourceSnapshotSignature;
            sourceCatalogJson = query.SourceCatalogJson;
            frame = _businessFrame; businessError = _businessError;
        }
        var sameContext = BusinessDisplayContext.Matches(sourceContext, ReadBusinessSourceContext(sourceCatalogJson));
        var current = sameContext && result != null && version == Interlocked.Read(ref _businessInputVersion)
            && frame != null && IsBusinessFrameCurrent(frame);
        return J.Object(("protocolVersion", BusinessProtocol.Version), ("isCurrent", current),
            ("pending", !current && queryError == null && businessError == null), ("error", queryError ?? businessError),
            // 非当前结果只可用于展示，并保留自身签名；绝不伪装成最新帧或把展示作用域当成执行许可。
            ("result", sameContext ? result : null), ("inputVersion", version.ToString(CultureInfo.InvariantCulture)),
            ("sourceContext", sameContext ? sourceContext : null),
            ("sourceSnapshotSignature", sameContext ? sourceSignature : "")).ToJsonString();
    }

    private void ExecuteOnePageQuery(BusinessFrame frame)
    {
        PageQuery? query;
        lock (_businessLock) query = _businessPageQueries.Values.Where(item => item.Version != frame.Version)
            .OrderBy(item => item.Sequence).FirstOrDefault();
        if (query == null || !IsBusinessFrameCurrent(frame)) return;
        JsonObject? result = null; string? error = null;
        try { result = BusinessQueries.EvaluatePage(JsonNode.Parse(query.IntentJson)!.AsObject(), frame.Snapshot,
            frame.Data, frame.Preferences, frame.Favorites, frame.CustomRecipes, _businessCandidateCache); }
        catch (Exception exception) { error = exception.GetBaseException().Message; }
        lock (_businessLock)
        {
            if (!IsBusinessFrameCurrent(frame) || !_businessPageQueries.TryGetValue(query.Key, out var current) || !ReferenceEquals(query, current)) return;
            query.Result = result; query.Error = error; query.Version = frame.Version; query.Sequence = ++_businessQuerySequence;
            query.SourceContext = frame.SourceContext; query.SourceSnapshotSignature = frame.SnapshotSignature;
            query.SourceCatalogJson = frame.CatalogJson;
        }
    }

    private string RetryBusinessAutomation(string request, HttpRequestData requestData)
    {
        var intent = ReadJsonRequest<BusinessRetryRequest>(requestData, "protocolVersion", "kind", "key");
        if (intent.ProtocolVersion != BusinessProtocol.Version) return BusinessProtocolUpgradeRequired();
        if (intent.Kind is not ("rare" or "normal") || string.IsNullOrWhiteSpace(intent.Key) || intent.Key.Length > 300)
            throw new CompanionDeviceAuthorityException(400, "重试订单标识无效。");
        if (!TryRequireAutomationLease(request, out var error, out _)) return ToJson(error);
        var ok = _businessCoordinator.Retry(intent.Kind, intent.Key, BusinessNow);
        return J.Object(("ok", ok), ("error", ok ? null : "订单已过期或仍有待确认安全屏障，不能重试。")).ToJsonString();
    }

    private sealed class BusinessRetryRequest
    {
        public int ProtocolVersion { get; init; }
        public string Kind { get; init; } = "";
        public string Key { get; init; } = "";
    }

    private static string BusinessProtocolUpgradeRequired() => J.Object(("ok", false),
        ("protocolVersion", BusinessProtocol.Version), ("isCurrent", false), ("pending", false),
        ("error", "业务计算与调度已由 Mod 接管，请更新伴随客户端；旧客户端不能提交计算后的动作或游戏辅助目标。")).ToJsonString();

    /// <summary>复用现有适配器的严格字段解析，数组仍按旧协议的逗号分隔形式编码。</summary>
    private static string ToBusinessQuery(JsonObject payload) => string.Join("&", payload.Select(field =>
        Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value is JsonArray array
            ? string.Join(",", array.Select(BusinessScalar)) : BusinessScalar(field.Value))));
    private static string BusinessScalar(JsonNode? value) => value == null ? "" : value is JsonValue scalar
        && scalar.TryGetValue<string>(out var text) ? text : value.ToJsonString();
}
