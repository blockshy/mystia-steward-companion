namespace MystiaStewardCompanion.Save;

internal enum AutomationCookingObservationKind
{
    Owned,
    Missing,
    OwnershipLost,
    Foreign,
    Unreadable,
}

internal enum AutomationCookingJobDirective
{
    None,
    DeliverOwnedResult,
}

internal readonly record struct AutomationCookingObservation(
    DateTime ObservedAtUtc,
    long Generation,
    AutomationCookingObservationKind Kind,
    int Phase,
    float Progress,
    string Detail = "",
    bool TimeoutEligible = true);

internal readonly record struct AutomationCookingTransition(
    string Outcome,
    string ReasonCode,
    string State,
    AutomationCookingJobDirective Directive,
    bool Terminal,
    bool Progressed);

internal sealed record AutomationCookingProcessResult(IReadOnlyList<string> Messages, bool Changed);

internal enum AutomationCookingControllerLeaseReleaseReason
{
    None,
    ManualHandoff,
    DeliveryCleanupCompleted,
    DeliveryCleanupTerminated,
    // 原生订单已经结束，只释放逻辑预约；不影响未由 Mod 送达的成品与现场。
    OrderTerminatedBeforeDelivery,
}

/// <summary>
/// 跟踪 Mod 对一个精确厨具控制器的逻辑预约。
/// </summary>
/// <remarks>
/// 预约独立于游戏的 LockedCookers 状态，同一任务释放后不能重新获取。
/// 待评价回执可以比预约存活更久，因此无需继续占用已经物理空闲的厨具。
/// </remarks>
internal sealed class AutomationCookingControllerLease
{
    public bool HoldsReservation { get; private set; } = true;
    public AutomationCookingControllerLeaseReleaseReason ReleaseReason { get; private set; }
    public DateTime? ReleasedAtUtc { get; private set; }

    public bool Release(
        AutomationCookingControllerLeaseReleaseReason reason,
        DateTime releasedAtUtc)
    {
        if (reason == AutomationCookingControllerLeaseReleaseReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (!HoldsReservation)
        {
            return false;
        }

        HoldsReservation = false;
        ReleaseReason = reason;
        ReleasedAtUtc = releasedAtUtc;
        return true;
    }
}

/// <summary>
/// 厨具所有权结束后，对实时订单评价收尾施加尝试次数和有效时长上限。
/// </summary>
internal sealed class AutomationOrderEvaluationCloseoutTracker
{
    private readonly AutomationEffectiveTimeoutClock _effectiveClock;

    public AutomationOrderEvaluationCloseoutTracker(
        DateTime startedAtUtc,
        int maxAttempts,
        TimeSpan minimumAttemptWindow,
        TimeSpan maxEffectiveDuration)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (minimumAttemptWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumAttemptWindow));
        if (maxEffectiveDuration <= TimeSpan.Zero || maxEffectiveDuration < minimumAttemptWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEffectiveDuration));
        }

        MaxAttempts = maxAttempts;
        MinimumAttemptWindow = minimumAttemptWindow;
        MaxEffectiveDuration = maxEffectiveDuration;
        _effectiveClock = new AutomationEffectiveTimeoutClock(startedAtUtc, initiallyEligible: true);
    }

    public int MaxAttempts { get; }
    public TimeSpan MinimumAttemptWindow { get; }
    public TimeSpan MaxEffectiveDuration { get; }
    public int AttemptCount { get; private set; }
    public TimeSpan EffectiveElapsed => _effectiveClock.Elapsed;
    public bool Exhausted =>
        AttemptCount >= MaxAttempts && EffectiveElapsed >= MinimumAttemptWindow
        || EffectiveElapsed >= MaxEffectiveDuration;

    public bool RecordFailure(DateTime observedAtUtc, bool eligible)
    {
        _effectiveClock.Observe(observedAtUtc, eligible);
        if (eligible)
        {
            AttemptCount++;
        }

        return Exhausted;
    }

    public void Suspend(DateTime observedAtUtc)
    {
        _effectiveClock.Observe(observedAtUtc, eligible: false);
    }
}

/// <summary>
/// 只累计前后两个采样端点均允许运行时推进的正时间间隔，暂停跨越的间隔不计入超时。
/// </summary>
internal sealed class AutomationEffectiveTimeoutClock
{
    private DateTime _lastObservedAtUtc;
    private bool _lastObservationEligible;

    public AutomationEffectiveTimeoutClock(DateTime startedAtUtc, bool initiallyEligible)
    {
        _lastObservedAtUtc = startedAtUtc;
        _lastObservationEligible = initiallyEligible;
    }

    public TimeSpan Elapsed { get; private set; }

    public void Observe(DateTime observedAtUtc, bool eligible)
    {
        var interval = observedAtUtc - _lastObservedAtUtc;
        if (interval > TimeSpan.Zero && _lastObservationEligible && eligible)
        {
            Elapsed += interval;
        }

        _lastObservedAtUtc = observedAtUtc;
        _lastObservationEligible = eligible;
    }

    public void Reset(DateTime observedAtUtc, bool eligible)
    {
        Elapsed = TimeSpan.Zero;
        _lastObservedAtUtc = observedAtUtc;
        _lastObservationEligible = eligible;
    }
}

/// <summary>
/// 区分不可重复的业务提交与可重试的清理阶段；仅明确已提交后才允许有界清理重试。
/// </summary>
internal sealed class AutomationBoundedCleanupTracker
{
    private AutomationCommitState _commitState = AutomationCommitState.Ready;

    public AutomationBoundedCleanupTracker(int maxAttempts)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        MaxAttempts = maxAttempts;
    }

    public int MaxAttempts { get; }
    public int AttemptCount { get; private set; }
    public bool Committed => _commitState == AutomationCommitState.Committed;
    public bool CommitUncertain => _commitState == AutomationCommitState.Uncertain;
    public bool CommitAttemptInProgress => _commitState == AutomationCommitState.Attempting;
    public bool CanCommit => _commitState == AutomationCommitState.Ready;
    public bool Completed { get; private set; }
    public bool Exhausted => !Completed && AttemptCount >= MaxAttempts;

    public bool TryBeginCommit()
    {
        if (_commitState != AutomationCommitState.Ready) return false;
        _commitState = AutomationCommitState.Attempting;
        return true;
    }

    /// <summary>解析已开始的提交；只有明确未提交才回到可重试状态，不确定结果保持阻止重提。</summary>
    public void ResolveCommit(AutomationCommitResolution resolution)
    {
        if (_commitState != AutomationCommitState.Attempting)
        {
            throw new InvalidOperationException("No non-idempotent operation is awaiting commit resolution.");
        }

        _commitState = resolution switch
        {
            AutomationCommitResolution.NotCommitted => AutomationCommitState.Ready,
            AutomationCommitResolution.Committed => AutomationCommitState.Committed,
            AutomationCommitResolution.Uncertain => AutomationCommitState.Uncertain,
            _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
        };
    }

    public bool TryBeginAttempt(bool eligible)
    {
        if (!Committed || !eligible || Completed || Exhausted) return false;
        AttemptCount++;
        return true;
    }

    public void Complete()
    {
        Completed = true;
    }
}

internal enum AutomationCommitResolution
{
    NotCommitted,
    Committed,
    Uncertain,
}

internal enum AutomationCommitState
{
    Ready,
    Attempting,
    Committed,
    Uncertain,
}

/// <summary>
/// 依据托管观测跟踪一个精确厨具成品的阶段与停滞，不直接访问 Unity 或 IL2CPP 对象。
/// </summary>
internal sealed class AutomationCookingJobTracker
{
    private const int MissingObservationLimit = 6;
    private const int UnreadableObservationLimit = 3;
    private const int OwnershipObservationFailureLimit = 6;
    private const int RegressiveObservationLimit = 3;
    private const float ProgressEpsilon = 0.0001f;
    private static readonly TimeSpan MissingObservationGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProgressStallTimeout = TimeSpan.FromSeconds(90);

    private int _missingObservations;
    private int _unreadableObservations;
    private int _ownershipObservationFailures;
    private int _regressiveObservations;
    private readonly AutomationEffectiveTimeoutClock _stallClock;

    public AutomationCookingJobTracker(long expectedGeneration, DateTime startedAtUtc, int phase, float progress)
    {
        ExpectedGeneration = expectedGeneration;
        StartedAtUtc = startedAtUtc;
        LastObservedAtUtc = startedAtUtc;
        LastProgressAtUtc = startedAtUtc;
        LastPhase = phase;
        LastProgress = progress;
        State = "cooking";
        Outcome = "progressed";
        ReasonCode = "cooking-started";
        _stallClock = new AutomationEffectiveTimeoutClock(startedAtUtc, initiallyEligible: true);
    }

    public long ExpectedGeneration { get; }
    public DateTime StartedAtUtc { get; }
    public DateTime LastObservedAtUtc { get; private set; }
    public DateTime LastProgressAtUtc { get; private set; }
    public int LastPhase { get; private set; }
    public float LastProgress { get; private set; }
    public string State { get; private set; }
    public string Outcome { get; private set; }
    public string ReasonCode { get; private set; }
    public int OwnershipObservationFailures => _ownershipObservationFailures;
    public int RegressiveObservations => _regressiveObservations;
    public TimeSpan EffectiveStallElapsed => _stallClock.Elapsed;

    public void Suspend(DateTime observedAtUtc)
    {
        _stallClock.Observe(observedAtUtc, eligible: false);
        LastObservedAtUtc = observedAtUtc;
    }

    public void EnterManualHandoff(DateTime observedAtUtc)
    {
        _stallClock.Observe(observedAtUtc, eligible: false);
        LastObservedAtUtc = observedAtUtc;
        State = "manual-handoff";
        Outcome = "waiting";
        ReasonCode = "cooking-manual-handoff";
    }

    public void EnterEvaluationPending(DateTime observedAtUtc, string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            throw new ArgumentException("Evaluation-pending reason code is required.", nameof(reasonCode));
        }

        _stallClock.Observe(observedAtUtc, eligible: false);
        LastObservedAtUtc = observedAtUtc;
        State = "evaluation-pending";
        Outcome = "waiting";
        ReasonCode = reasonCode;
    }

    public void MarkManualHandoffExpired(DateTime observedAtUtc)
    {
        _stallClock.Observe(observedAtUtc, eligible: false);
        LastObservedAtUtc = observedAtUtc;
        State = "manual-handoff-expired";
        Outcome = "waiting";
        ReasonCode = "cooking-manual-handoff-expired";
    }

    public AutomationCookingTransition Observe(AutomationCookingObservation observation)
    {
        _stallClock.Observe(observation.ObservedAtUtc, observation.TimeoutEligible);
        LastObservedAtUtc = observation.ObservedAtUtc;

        // 身份代次变更或观察到他人成品立即终止；短暂缺失与读取失败才适用各自的有界等待。
        if (observation.Generation != ExpectedGeneration
            || observation.Kind == AutomationCookingObservationKind.Foreign)
        {
            return Transition(
                "interrupted",
                "cooking-controller-reused",
                "terminal",
                AutomationCookingJobDirective.None,
                terminal: true,
                progressed: false);
        }

        if (observation.Kind == AutomationCookingObservationKind.OwnershipLost)
        {
            return Transition(
                "interrupted",
                "cooking-ownership-lost",
                "terminal",
                AutomationCookingJobDirective.None,
                terminal: true,
                progressed: false);
        }

        if (observation.Kind == AutomationCookingObservationKind.Missing)
        {
            _ownershipObservationFailures++;
            _missingObservations++;
            if ((_missingObservations >= MissingObservationLimit
                    || _ownershipObservationFailures >= OwnershipObservationFailureLimit)
                && _stallClock.Elapsed >= MissingObservationGrace)
            {
                return Transition(
                    "blocked",
                    "cooking-result-missing",
                    "terminal",
                    AutomationCookingJobDirective.None,
                    terminal: true,
                    progressed: false);
            }

            return Transition(
                "waiting",
                "cooking-result-temporarily-missing",
                State,
                AutomationCookingJobDirective.None,
                terminal: false,
                progressed: false);
        }

        if (observation.Kind == AutomationCookingObservationKind.Unreadable)
        {
            _ownershipObservationFailures++;
            _unreadableObservations++;
            if (_unreadableObservations >= UnreadableObservationLimit
                || _ownershipObservationFailures >= OwnershipObservationFailureLimit)
            {
                return Transition(
                    "blocked",
                    "cooking-result-unreadable",
                    "terminal",
                    AutomationCookingJobDirective.None,
                    terminal: true,
                    progressed: false);
            }

            return Transition(
                "waiting",
                "cooking-result-temporarily-unreadable",
                State,
                AutomationCookingJobDirective.None,
                terminal: false,
                progressed: false);
        }

        _missingObservations = 0;
        _unreadableObservations = 0;
        _ownershipObservationFailures = 0;
        var phaseAdvanced = observation.Phase > LastPhase;
        var progressAdvanced = observation.Phase == LastPhase
            && observation.Progress > LastProgress + ProgressEpsilon;
        var regressed = observation.Phase < LastPhase
            || (observation.Phase == LastPhase && observation.Progress < LastProgress - ProgressEpsilon);
        if (regressed)
        {
            _regressiveObservations++;
            if (_regressiveObservations >= RegressiveObservationLimit)
            {
                return Transition(
                    "blocked",
                    "cooking-progress-regressed",
                    "terminal",
                    AutomationCookingJobDirective.None,
                    terminal: true,
                    progressed: false);
            }
        }
        else
        {
            _regressiveObservations = 0;
        }

        var progressed = phaseAdvanced || progressAdvanced;
        if (progressed)
        {
            LastProgressAtUtc = observation.ObservedAtUtc;
            LastPhase = observation.Phase;
            LastProgress = observation.Progress;
            _stallClock.Reset(observation.ObservedAtUtc, observation.TimeoutEligible);
        }

        if (observation.Phase == 3)
        {
            return Transition(
                progressed ? "progressed" : "waiting",
                "cooking-result-ready",
                "ready",
                AutomationCookingJobDirective.DeliverOwnedResult,
                terminal: false,
                progressed);
        }

        if (_stallClock.Elapsed >= ProgressStallTimeout)
        {
            return Transition(
                "blocked",
                "cooking-progress-stalled",
                "terminal",
                AutomationCookingJobDirective.None,
                terminal: true,
                progressed: false);
        }

        // 进度接近完成仍可能处于原生收尾；只有阶段 3 才发出可送达指令。
        if (observation.Phase == 2 && observation.Progress >= 0.999f)
        {
            return Transition(
                progressed ? "progressed" : "waiting",
                "cooking-native-finalize-waiting",
                "cooking",
                AutomationCookingJobDirective.None,
                terminal: false,
                progressed);
        }

        return Transition(
            progressed ? "progressed" : "waiting",
            progressed ? "cooking-progress" : "cooking-in-progress",
            "cooking",
            AutomationCookingJobDirective.None,
            terminal: false,
            progressed);
    }

    private AutomationCookingTransition Transition(
        string outcome,
        string reasonCode,
        string state,
        AutomationCookingJobDirective directive,
        bool terminal,
        bool progressed)
    {
        Outcome = outcome;
        ReasonCode = reasonCode;
        State = state;
        return new AutomationCookingTransition(outcome, reasonCode, state, directive, terminal, progressed);
    }
}
