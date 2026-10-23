using System.Text.Json.Nodes;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Application.Automation;

/// <summary>
/// 纯托管自动化编排器。宿主以冻结快照驱动本对象，并将输出命令交给既有受检主线程适配器。
/// 本对象不持有 Unity 对象、不发 HTTP 请求、不续租，也不将一次许可视作后续游戏操作的永久许可。
/// </summary>
/// <remarks>
/// 公共入口按同一锁线性化；返回值均为副本。调用方可以在后台计算线程驱动 Advance，在主线程结果回调中调用 Complete。
/// 时间必须为宿主同一单调时间轴的毫秒值；测试可以直接注入时间，无须真实睡眠。
/// </remarks>
public sealed partial class AutomationCoordinator
{
    private const int MaximumTrackedOrders = 1024;
    private const int MaximumRejectedRecipes = 64;
    private const long RareTickMs = 1500;
    private const long NormalTickMs = 800;
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonObject> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _orders = new(StringComparer.Ordinal);
    // 保留的旧订单只供锅次/屏障诊断，不能进入当前快照的任务候选。
    private readonly HashSet<string> _currentOrderKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<long, PendingCommand> _pending = new();
    private readonly Queue<Continuation> _continuations = new();
    private readonly HashSet<int> _usedCookers = new();
    private readonly List<string> _rejectedRecipes = new();
    private AutomationInputView _input = AutomationInputView.FromOwnedJson(new JsonObject());
    private string _session = "";
    private long _businessGeneration;
    private string _authorityKey = "";
    private long _scopeVersion;
    private long _nextRequestId;
    private long _lastRareTick = long.MinValue;
    private long _lastNormalTick = long.MinValue;
    private long _cookerBucket = -1;
    private long _lastNow;
    private bool _authorized;
    private bool _runtimeEnabled;
    private bool _stopped;
    private string _message = "等待可信业务输入。";
    private string _rejectionScope = "";

    /// <summary>保存下发时的不可变请求上下文，用于拒绝迟到响应及跨事件响应。</summary>
    private sealed record PendingCommand(long Id, long Scope, string StateKey, string Action, string Stage,
        long EventSequence, JsonObject Order, JsonObject Payload, bool StopOnError, int MaximumRetries,
        int MaximumRollbacks, JsonObject? ContinuationPayload = null, string ContinuationAction = "");

    /// <summary>稀客预检后的准备、送酒后的即时完成，仍须在下一次最新输入下重新检查准入。</summary>
    private sealed record Continuation(string StateKey, string Action, JsonObject Payload, long Scope);

    /// <summary>
    /// 对账快照并生成本轮命令。输入属性为 snapshot、preferences、recommendations、normalExecutionTargets、data、favorites、authority。
    /// authority 至少含 sessionId、businessGeneration、authorityRevision、automationEpoch、leaseOwned、allowed。
    /// </summary>
    public JsonObject Advance(JsonObject input, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
        {
            if (_stopped) return Status(Array.Empty<JsonObject>());
            ValidateAdvanceTime(nowMs);
            // 公开 JSON 可以被调用方继续编辑。即使只读组件视图已引入，此处仍取得完整独立副本，
            // 保持既有所有权契约；绝不能让公开可变输入通过内部性能入口变成跨线程共享状态。
            return AdvanceCore(AutomationInputView.FromOwnedJson(J.Obj(J.Clone(input))), nowMs);
        }
    }

    /// <summary>
    /// 接收可信宿主的永久只读组件。所有状态转换仍在与 Advance/Complete 相同的锁内进行，
    /// 只省去输入包装与深复制，不跳过单调时钟、权限、租约、经营代次、订单身份或续接检查。
    /// 协调器会跨调用保留该视图；调用方必须遵守 AutomationInputView 的永久只读所有权契约。
    /// </summary>
    public JsonObject AdvanceFrozen(AutomationInputView input, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
        {
            if (_stopped) return Status(Array.Empty<JsonObject>());
            ValidateAdvanceTime(nowMs);
            return AdvanceCore(input, nowMs);
        }
    }

    /// <summary>调用者已持有协调器锁；两个入口共用原来的时间拒绝规则。</summary>
    private void ValidateAdvanceTime(long nowMs)
    {
        if (nowMs < 0 || nowMs < _lastNow)
            throw new ArgumentOutOfRangeException(nameof(nowMs), "自动化时间必须单调递增。");
    }

    /// <summary>调用者已持锁并确认时间有效。这里只替换只读输入组件，所有可变状态仍归协调器独占。</summary>
    private JsonObject AdvanceCore(AutomationInputView input, long nowMs)
    {
        _lastNow = nowMs;
        _input = input;
        var authority = _input.Authority;
        var snapshot = _input.Snapshot;
        var preferences = _input.Preferences;
        var session = J.Str(authority["sessionId"]);
        var generation = (long)J.Num(authority["businessGeneration"]);
        if (session != _session || generation != _businessGeneration)
        {
            ClearSession();
            _session = session;
            _businessGeneration = generation;
        }
        var key = string.Join("|", _session, _businessGeneration, J.Key(J.Num(authority["authorityRevision"])),
            J.Key(J.Num(authority["automationEpoch"])), J.Bool(authority["leaseOwned"]), J.Bool(authority["allowed"]));
        if (key != _authorityKey)
        {
            _authorityKey = key;
            InvalidateRequests();
        }
        _authorized = session.Length > 0 && generation > 0 && J.Num(authority["authorityRevision"]) > 0
            && J.Num(authority["automationEpoch"]) > 0 && J.Bool(authority["leaseOwned"]) && J.Bool(authority["allowed"]);
        var enabled = _authorized && J.Bool(preferences["automationEnabled"])
            && J.Bool(snapshot["nightBusinessAutomationAllowed"])
            && J.Num(snapshot["nightBusinessGeneration"]) == generation;
        if (_runtimeEnabled && !enabled) InvalidateRequests();
        _runtimeEnabled = enabled;
        IndexAndReconcileOrders(snapshot, preferences, nowMs);
        var commands = new List<JsonObject>();
        if (!_runtimeEnabled)
        {
            // allowed 是宿主对现场、配置和设备权限的合并结果，不能将 false 一律解释为
            // 客户端没有控制权。这里只选择展示原因，原 enabled/authorized 判定完全不变。
            // 若本轮原本允许执行，却由订单对账检测出重复身份或容量问题，则保留其明确说明。
            if (!enabled) _message = DisabledAutomationMessage(snapshot, preferences);
            return Status(commands);
        }
        var bucket = nowMs / RareTickMs;
        if (_cookerBucket != bucket)
        {
            _cookerBucket = bucket;
            _usedCookers.Clear();
        }
        // 尚未返回的开锅请求保留其精确槽位，不能因跨调度周期就重复预约。
        foreach (var request in _pending.Values)
        {
            if (J.Bool(request.Payload["autoStartCooking"]) && J.Num(request.Payload["cookerControllerIndex"], -1) >= 0)
                _usedCookers.Add((int)J.Num(request.Payload["cookerControllerIndex"]));
            if (request.ContinuationPayload is JsonObject continuation && J.Bool(continuation["autoStartCooking"]) && J.Num(continuation["cookerControllerIndex"], -1) >= 0)
                _usedCookers.Add((int)J.Num(continuation["cookerControllerIndex"]));
        }

        DrainContinuations(commands, nowMs);
        var normalDue = _lastNormalTick == long.MinValue || nowMs - _lastNormalTick >= NormalTickMs;
        var rareDue = _lastRareTick == long.MinValue || nowMs - _lastRareTick >= RareTickMs;
        // 普客需求先预约；稀客只能使用剩余物理槽位。并发数在实际准入后才消耗。
        if (normalDue && J.Bool(preferences["autoNormalOrderEnabled"]))
        {
            PlanNormal(commands, nowMs);
            _lastNormalTick = nowMs;
        }
        if (rareDue && J.Bool(preferences["autoRareOrderEnabled"]))
        {
            PlanRare(commands, nowMs);
            _lastRareTick = nowMs;
        }
        _message = commands.Count > 0 ? $"本轮提交 {commands.Count} 项受检自动化命令。" : IdleSchedulingMessage();
        return Status(commands);
    }

    /// <summary>
    /// 根据已经采集的结构化事实说明未执行原因，不从图片 ready、场景名称或文案反向授予权限。
    /// 配置关闭和已知的非营业/教学状态优先于设备控制权；缺失或未知值只表述等待确认。
    /// 客户端本地暂停不会传入此协议，因此无租约不能被猜测成“用户手动暂停”。
    /// </summary>
    private string DisabledAutomationMessage(JsonObject snapshot, JsonObject preferences)
    {
        if (preferences["automationEnabled"] is not JsonValue configured
            || !configured.TryGetValue<bool>(out var automationEnabled)) return "等待自动化配置同步。";
        if (!automationEnabled) return "自动化已关闭。";

        var phase = J.Str(snapshot["nightBusinessLifecyclePhase"]);
        var daytime = J.Str(snapshot["activeSceneName"]) == "Day";
        if ((phase is "Inactive" or "Destroyed") && daytime)
            return "当前为日间，自动化等待夜间营业。";
        if (phase == "Inactive") return "尚未开始夜间营业，自动化正在待命。";
        if (phase == "Closing") return "夜间营业正在结束，自动化已停止。";
        if (phase == "Destroyed") return "本次夜间营业已结束，等待下一次营业。";
        if (phase != "Active") return "等待夜间营业状态确认。";

        if (!J.Bool(snapshot["nightBusinessAutomationAllowed"]))
        {
            // 原生教学检测按本次营业代次锁存，不能承诺教学面板关闭后自动恢复。
            return J.Str(snapshot["nightBusinessAutomationBlockReason"]) switch
            {
                "night-business-tutorial-active" => "本次夜间营业包含教学，自动化保持暂停；请手动完成本次营业。",
                "night-business-tutorial-state-unavailable" => "尚未确认夜间教学状态，自动化已暂停。",
                "night-business-lifecycle-unavailable" => "等待当前夜间营业状态同步。",
                _ => "当前营业尚未满足自动化执行条件，等待现场状态确认。",
            };
        }
        if (_businessGeneration <= 0 || J.Num(snapshot["nightBusinessGeneration"]) != _businessGeneration)
            return "等待当前夜间营业状态同步。";
        if (!J.Bool(snapshot["runtimeLoaded"])) return "等待当前游戏现场状态读取完成。";
        // 宿主合并许可也包含最新快照和资源状态，不能在缺少细分事实时只归因于设备。
        return !_authorized
            ? "等待游戏状态、主设备连接与自动化配置就绪。"
            : "当前经营状态暂停自动化。";
    }

    /// <summary>
    /// 区分仍可调度的等待与所有未完成订单均已暂停。此处只汇总当前精确订单状态，
    /// 不改变回退预算、暂停或准入条件，也不把“厨具空闲”解释为可绕过暂停重新开锅。
    /// </summary>
    private string IdleSchedulingMessage()
    {
        var remaining = 0;
        var paused = 0;
        var manual = 0;
        foreach (var key in _currentOrderKeys)
        {
            if (!_states.TryGetValue(key, out var state) || J.Bool(state["completed"])) continue;
            remaining++;
            if (!J.Bool(state["paused"])) continue;
            paused++;
            if (J.Bool(state["manualResolutionRequired"])) manual++;
        }
        if (remaining == 0 || paused != remaining) return "等待订单、厨具或阶段状态更新。";
        return manual > 0
            ? $"当前 {paused} 笔未完成订单均已暂停，其中 {manual} 笔需要人工确认；请查看自动化状态中的具体原因。"
            : $"当前 {paused} 笔未完成订单均已暂停；请查看自动化状态中的原因，并按需点击“重试暂停订单”。";
    }

    /// <summary>回填确定的游戏响应；命令轮次、经营会话或事件序号已经变化时丢弃结果。</summary>
    public void Complete(long requestId, JsonObject response, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate)
        {
            if (!_pending.Remove(requestId, out var request)) return;
            if (!IsCurrent(request)) return;
            if (request.Action == "ack-barrier") { CompleteAcknowledgement(request, response, nowMs); return; }
            var automation = J.Obj(response["automation"]);
            if (J.Str(automation["reasonCode"]) == "automation-lease-unavailable" || J.Str(automation["stage"]) == "lease")
            {
                _authorized = false;
                _runtimeEnabled = false;
                InvalidateRequests();
                return;
            }
            var state = _states[request.StateKey];
            ApplyOrderResponse(state, request, response, nowMs);
            // 被宿主或适配器拒绝的请求不能触发“预检后准备”续接；等待只允许既有流程继续对账。
            if (J.Str(automation["outcome"]) is not ("progressed" or "completed" or "waiting")) return;
            if (J.Bool(state["paused"]) || J.Bool(state["completed"]) || J.Num(state["nextAttemptAtMs"]) > nowMs) return;
            if (request.ContinuationPayload is not null)
            {
                _continuations.Enqueue(new Continuation(request.StateKey, request.ContinuationAction,
                    J.Obj(J.Clone(request.ContinuationPayload)), request.Scope));
            }
            else if (request.Action == "prepare-rare" && J.Bool(request.Payload["autoCompleteOrder"])
                && DidStep(response, "beverage-delivered", "servedBeverage"))
            {
                var payload = J.Obj(J.Clone(request.Payload));
                payload["autoStartCooking"] = false;
                payload["autoTakeBeverage"] = false;
                ClearReservation(payload);
                _continuations.Enqueue(new Continuation(request.StateKey, "complete-rare", payload, request.Scope));
            }
        }
    }

    /// <summary>
    /// 宿主已证明命令尚未进入游戏适配器时放弃该命令。此入口不代表运行时失败，不消耗重试预算，
    /// 也不生成后续动作；已调用适配器或提交结果不确定的命令必须用 Complete/Fail 回填，禁止使用本入口。
    /// </summary>
    public void Abandon(long requestId)
    {
        lock (_gate)
        {
            if (!_pending.Remove(requestId, out var request)) return;
            foreach (var payload in new[] { request.Payload, request.ContinuationPayload })
            {
                if (payload is null || !J.Bool(payload["autoStartCooking"])) continue;
                var index = (int)J.Num(payload["cookerControllerIndex"], -1);
                if (index >= 0) _usedCookers.Remove(index);
            }
            if (request.Action == "complete-normal") _lastNormalTick = long.MinValue;
            else if (request.Action is "prepare-rare" or "complete-rare") _lastRareTick = long.MinValue;
        }
    }

    /// <summary>回填尚未取得业务响应的传输错误；不可用租约和不确定游戏提交应通过结构化响应报告。</summary>
    public void Fail(long requestId, string message, long nowMs)
    {
        lock (_gate)
        {
            if (!_pending.Remove(requestId, out var request) || !IsCurrent(request)) return;
            if (request.Action == "ack-barrier") { _message = message; return; }
            var state = _states[request.StateKey];
            AutomationMachine.TransportFailure(state, request.Stage, message, nowMs, request.StopOnError, request.MaximumRetries);
            AutomationMachine.Detail(state, message, nowMs);
        }
    }

    /// <summary>用户主动重试一笔暂停订单；不会解除必须人工确认的屏障。</summary>
    public bool Retry(string kind, string orderKey, long nowMs)
    {
        lock (_gate)
        {
            if (!_authorized || !_states.TryGetValue(StateKey(kind, orderKey), out var state)) return false;
            if (!AutomationMachine.Retry(state, nowMs)) return false;
            // 显式重试只重开既有预算，不立即执行动作。同步替换旧暂停说明，避免在下一次
            // 受检调度之前仍向用户显示“已暂停”或上一锅“制作中”的过期进度。
            AutomationMachine.Detail(state, J.Str(state["lastError"]), nowMs);
            _message = "已接受暂停订单重试，等待下一轮受检调度。";
            _lastRareTick = _lastNormalTick = long.MinValue;
            return true;
        }
    }

    /// <summary>准备人工确认命令。必须引用当前未解决事件，且全局只允许一笔确认在途。</summary>
    public JsonObject BeginAcknowledge(long sequence, long nowMs)
    {
        lock (_gate)
        {
            if (!_authorized || sequence <= 0 || _pending.Values.Any(x => x.Action == "ack-barrier"))
                return J.Object(("ok", false), ("error", "当前无法执行确认：请检查主设备连接、待确认事项是否有效，以及是否已有确认正在处理。"));
            var events = J.Objects(_input.Snapshot["automationEvents"]);
            if (!events.Any(x => J.Num(x["sequence"]) == sequence && IsManualEvent(x)))
                return J.Object(("ok", false), ("error", "当前游戏状态中不存在该待确认事项。"));
            var id = ++_nextRequestId;
            var payload = J.Object(("sequence", sequence));
            _pending[id] = new PendingCommand(id, _scopeVersion, "", "ack-barrier", "", sequence,
                new JsonObject(), payload, true, 1, 1);
            return J.Object(("ok", true), ("command", CommandNode(_pending[id])));
        }
    }

    /// <summary>等价于 Complete，便于宿主明确区分人工确认响应通道。</summary>
    public void CompleteAck(long requestId, JsonObject response, long nowMs) => Complete(requestId, response, nowMs);

    /// <summary>返回当前状态副本，供 HTTP 线程读取；不会触发计算、游戏读取或命令下发。</summary>
    public JsonObject Snapshot()
    {
        lock (_gate) return Status(Array.Empty<JsonObject>());
    }

    /// <summary>
    /// 业务候选输入只需要本经营范围内已拒绝的配方键。直接复制这个有界集合，避免每轮为读取
    /// 至多 64 个字符串而构造完整订单状态、头像诊断、资源概览及其 JSON 副本。
    /// 返回数组与内部集合隔离；调用方不得借此取得执行权限，权威和时间仍由 Advance 重新核验。
    /// </summary>
    public string[] RejectedRecipeKeys()
    {
        lock (_gate) return _rejectedRecipes.ToArray();
    }

    /// <summary>永久停止当前宿主实例；未开始命令失效，已发生的游戏动作仍由适配层取得确定结果。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _authorized = _runtimeEnabled = false;
            InvalidateRequests();
        }
    }

    private void ClearSession()
    {
        InvalidateRequests();
        _states.Clear();
        _orders.Clear();
        _currentOrderKeys.Clear();
        _usedCookers.Clear();
        _rejectedRecipes.Clear();
        _cookerBucket = -1;
        _rejectionScope = "";
    }

    private void InvalidateRequests()
    {
        _scopeVersion++;
        _pending.Clear();
        _continuations.Clear();
        _lastRareTick = _lastNormalTick = long.MinValue;
    }

    private bool IsCurrent(PendingCommand request) => !_stopped && _authorized && request.Scope == _scopeVersion
        && (request.Action == "ack-barrier" || _runtimeEnabled && _states.TryGetValue(request.StateKey, out var state)
            && J.Num(state["lastRuntimeEventSequence"]) <= request.EventSequence);

    private JsonObject CommandNode(PendingCommand request) => J.Object(("requestId", request.Id), ("scopeVersion", request.Scope),
        ("action", request.Action), ("stateKey", request.StateKey), ("payload", request.Payload),
        ("authorityRevision", _input.Authority["authorityRevision"]),
        ("automationEpoch", _input.Authority["automationEpoch"]), ("businessGeneration", _businessGeneration));

    private bool CompleteAcknowledgement(PendingCommand request, JsonObject response, long nowMs)
    {
        var acknowledged = J.Numbers(response["acknowledgedSequences"]);
        if (!J.Bool(response["ok"]) || J.Num(response["sequence"]) != request.EventSequence
            || acknowledged.Length == 0 || J.Num(response["acknowledgedCount"]) != acknowledged.Length
            || acknowledged.Distinct().Count() != acknowledged.Length || !acknowledged.Contains(request.EventSequence)
            || acknowledged.Any(x => x <= 0 || Math.Truncate(x) != x))
        {
            _message = "人工确认结果与请求不一致，订单仍保持暂停。";
            return false;
        }
        foreach (var (key, state) in _states.ToArray())
        {
            if (!J.Bool(state["manualResolutionRequired"]) || !acknowledged.Contains(J.Num(state["lastRuntimeEventSequence"]))) continue;
            _states[key] = ResetAfterAcknowledgement(state, nowMs, J.Str(response["status"], "已确认游戏状态，等待下一轮重新判断。"));
        }
        _lastRareTick = _lastNormalTick = long.MinValue;
        return true;
    }

    private static JsonObject ResetAfterAcknowledgement(JsonObject state, long nowMs, string message)
    {
        var next = AutomationMachine.Empty(J.Str(state["kind"]), J.Str(state["orderKey"]), nowMs);
        next["lastRuntimeEventSequence"] = J.Clone(state["lastRuntimeEventSequence"]);
        next["lastError"] = message;
        return next;
    }

    private static string StateKey(string kind, string key) => kind + ":" + key;

    /// <summary>规范订单标识只使用捕获的原生身份和生命周期；缺失时返回空字符串并拒绝执行。</summary>
    public static string OrderKey(string kind, JsonObject order)
    {
        var sequence = J.Num(order["orderLifecycleSequence"]);
        if (sequence <= 0 || !J.SafeInteger(order["orderLifecycleSequence"])) return "";
        var trace = J.Str(order["traceId"]).Trim();
        var identity = kind == "normal" ? Nonempty(J.Str(order["orderKey"]), trace) : trace.Length > 0 ? "trace:" + trace : "";
        return identity.Length == 0 ? "" : identity + "|lifecycle:" + J.Key(sequence);
    }
}
