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

/// <summary>
/// Accumulates only time intervals whose endpoints are both eligible for runtime progress.
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
/// Bounds retries for a non-idempotent operation's idempotent cleanup stage.
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
/// Tracks one exact cooker result without touching Unity or IL2CPP objects.
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
