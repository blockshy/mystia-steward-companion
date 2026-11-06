namespace MystiaStewardCompanion.Save;

internal enum RuntimeAvailableMissionSourcePhase
{
    WaitingAfterPerformance,
}

internal enum RuntimeAvailableMissionStartOutcome
{
    Started,
    Retired,
    Uncertain,
}

internal sealed record RuntimeAvailableMissionPendingReference(
    string Source,
    int SourceOrdinal,
    string MissionLabel);

internal sealed record RuntimeAvailableMissionSourceTransition(
    string EventLabel,
    RuntimeAvailableMissionSourcePhase Phase,
    IReadOnlyList<RuntimeAvailableMissionPendingReference> References,
    DateTime ChangedAtUtc);

internal sealed record RuntimeAvailableMissionSourceSnapshot(
    bool HooksAttached,
    bool RuntimeAvailable,
    string HookStatus,
    long MissionGeneration,
    long SourceRevision,
    int OwnerThreadId,
    IReadOnlyList<RuntimeAvailableMissionSourceTransition> Transitions,
    DateTime ChangedAtUtc,
    string LastEvent,
    string LastError)
{
    public static RuntimeAvailableMissionSourceSnapshot Detached { get; } = new(
        HooksAttached: false,
        RuntimeAvailable: false,
        HookStatus: "not-attached",
        MissionGeneration: 0,
        SourceRevision: 0,
        OwnerThreadId: 0,
        Transitions: Array.Empty<RuntimeAvailableMissionSourceTransition>(),
        ChangedAtUtc: DateTime.MinValue,
        LastEvent: "detached",
        LastError: "");
}

/// <summary>按任务代次和所属线程记录演出前后任务启动边界，保存尚待原生启动的来源引用。</summary>
/// <remarks>锁保护托管状态；ownerThreadId 是调用方提供的身份，不会把后台调用自动切换到 Unity 主线程。</remarks>
internal sealed class RuntimeAvailableMissionSourceState
{
    public const string BeforePerformanceSource = "postMissions";
    public const string AfterPerformanceSource = "postMissionsAfterPerformance";

    private const int MaxTransitionCount = 4096;
    private const int MaxReferenceCount = 4096;
    private const int MaxIdentityLength = 512;

    private readonly object _gate = new();
    private readonly Dictionary<string, RuntimeAvailableMissionSourceTransition>
        _transitions = new(StringComparer.Ordinal);

    private bool _hooksAttached;
    private bool _runtimeAvailable;
    private string _hookStatus = "not-attached";
    private long _missionGeneration;
    private long _sourceRevision;
    private int _ownerThreadId;
    private DateTime _changedAtUtc = DateTime.MinValue;
    private string _lastEvent = "detached";
    private string _lastError = "";

    public RuntimeAvailableMissionSourceSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotLocked();
        }
    }

    public void SetHookStatus(
        bool attached,
        string hookStatus,
        DateTime changedAtUtc)
    {
        if (string.IsNullOrEmpty(hookStatus))
        {
            throw new ArgumentException(
                "A hook status is required.",
                nameof(hookStatus));
        }

        lock (_gate)
        {
            _hooksAttached = attached;
            _hookStatus = hookStatus;
            if (!attached)
            {
                _runtimeAvailable = false;
                _transitions.Clear();
                _lastError = hookStatus;
            }
            AdvanceRevisionLocked(
                changedAtUtc,
                attached ? "hooks-attached" : "hooks-unavailable");
        }
    }

    public void ResetForMissionGeneration(
        long missionGeneration,
        int ownerThreadId,
        DateTime changedAtUtc)
    {
        if (missionGeneration < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(missionGeneration));
        }
        if (ownerThreadId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerThreadId));
        }

        lock (_gate)
        {
            _missionGeneration = missionGeneration;
            _ownerThreadId = ownerThreadId;
            _runtimeAvailable = false;
            _transitions.Clear();
            _lastError = "";
            AdvanceRevisionLocked(changedAtUtc, "mission-generation-reset");
        }
    }

    public bool ArmMissionGeneration(
        long missionGeneration,
        int ownerThreadId,
        DateTime changedAtUtc)
    {
        lock (_gate)
        {
            if (!_hooksAttached
                || missionGeneration != _missionGeneration
                || ownerThreadId != _ownerThreadId
                || ownerThreadId < 1)
            {
                return false;
            }

            _runtimeAvailable = true;
            _lastError = "";
            AdvanceRevisionLocked(changedAtUtc, "mission-generation-armed");
            return true;
        }
    }

    public bool ObserveSchedulerBoundary(
        long missionGeneration,
        int ownerThreadId,
        string eventLabel,
        string boundary,
        DateTime changedAtUtc)
    {
        RequireIdentity(eventLabel, nameof(eventLabel));
        if (string.IsNullOrEmpty(boundary))
        {
            throw new ArgumentException(
                "A scheduler boundary is required.",
                nameof(boundary));
        }

        lock (_gate)
        {
            if (!OwnsGenerationLocked(missionGeneration, ownerThreadId))
            {
                return false;
            }

            AdvanceRevisionLocked(
                changedAtUtc,
                $"{boundary}:{eventLabel}");
            return true;
        }
    }

    /// <summary>仅在演出前启动结果全部确定后登记演出后引用；重复事件或不确定结果使当前来源状态失效。</summary>
    public bool CommitBeforePerformance(
        long missionGeneration,
        int ownerThreadId,
        string eventLabel,
        IReadOnlyList<RuntimeAvailableMissionStartOutcome> beforeOutcomes,
        IReadOnlyList<string> afterPerformanceMissionLabels,
        DateTime changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(beforeOutcomes);
        ArgumentNullException.ThrowIfNull(afterPerformanceMissionLabels);
        RequireIdentity(eventLabel, nameof(eventLabel));
        ValidateOutcomeList(beforeOutcomes);
        var pendingReferences = BuildReferences(
            AfterPerformanceSource,
            afterPerformanceMissionLabels);

        lock (_gate)
        {
            if (!OwnsGenerationLocked(missionGeneration, ownerThreadId))
            {
                return false;
            }
            if (beforeOutcomes.Any(
                    outcome => outcome == RuntimeAvailableMissionStartOutcome.Uncertain))
            {
                FailLocked(
                    "before-performance-start-outcome-uncertain",
                    changedAtUtc);
                return false;
            }
            if (_transitions.ContainsKey(eventLabel))
            {
                FailLocked(
                    $"duplicate-event-transition:{eventLabel}",
                    changedAtUtc);
                return false;
            }
            if (pendingReferences.Count > 0)
            {
                if (_transitions.Count >= MaxTransitionCount)
                {
                    FailLocked("event-transition-overflow", changedAtUtc);
                    return false;
                }
                _transitions.Add(
                    eventLabel,
                    new RuntimeAvailableMissionSourceTransition(
                        eventLabel,
                        RuntimeAvailableMissionSourcePhase.WaitingAfterPerformance,
                        pendingReferences,
                        changedAtUtc));
            }

            AdvanceRevisionLocked(
                changedAtUtc,
                $"before-performance-complete:{eventLabel}");
            return true;
        }
    }

    /// <summary>按来源、原始序号和任务标签逐项匹配待启动引用，确认结果后撤销过渡记录。</summary>
    public bool CommitAfterPerformance(
        long missionGeneration,
        int ownerThreadId,
        string eventLabel,
        IReadOnlyList<string> missionLabels,
        IReadOnlyList<RuntimeAvailableMissionStartOutcome> outcomes,
        DateTime changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(missionLabels);
        ArgumentNullException.ThrowIfNull(outcomes);
        RequireIdentity(eventLabel, nameof(eventLabel));
        var expectedReferences = BuildReferences(
            AfterPerformanceSource,
            missionLabels);
        ValidateOutcomeList(outcomes);
        if (missionLabels.Count != outcomes.Count)
        {
            throw new InvalidOperationException(
                "after-performance-outcome-count-mismatch");
        }

        lock (_gate)
        {
            if (!OwnsGenerationLocked(missionGeneration, ownerThreadId))
            {
                return false;
            }
            if (outcomes.Any(
                    outcome => outcome == RuntimeAvailableMissionStartOutcome.Uncertain))
            {
                FailLocked(
                    "after-performance-start-outcome-uncertain",
                    changedAtUtc);
                return false;
            }

            if (expectedReferences.Count == 0)
            {
                if (_transitions.ContainsKey(eventLabel))
                {
                    FailLocked(
                        $"unexpected-empty-after-performance-transition:{eventLabel}",
                        changedAtUtc);
                    return false;
                }
            }
            else if (!_transitions.TryGetValue(eventLabel, out var transition)
                || transition.Phase
                    != RuntimeAvailableMissionSourcePhase.WaitingAfterPerformance
                || !ReferencesEqual(transition.References, expectedReferences))
            {
                FailLocked(
                    $"after-performance-transition-mismatch:{eventLabel}",
                    changedAtUtc);
                return false;
            }
            else
            {
                _transitions.Remove(eventLabel);
            }

            AdvanceRevisionLocked(
                changedAtUtc,
                $"after-performance-complete:{eventLabel}");
            return true;
        }
    }

    public void FailMissionGeneration(
        long missionGeneration,
        int ownerThreadId,
        string error,
        DateTime changedAtUtc)
    {
        if (string.IsNullOrEmpty(error))
        {
            throw new ArgumentException(
                "A source failure reason is required.",
                nameof(error));
        }

        lock (_gate)
        {
            if (missionGeneration != _missionGeneration
                || ownerThreadId != _ownerThreadId)
            {
                return;
            }
            FailLocked(error, changedAtUtc);
        }
    }

    private bool OwnsGenerationLocked(long missionGeneration, int ownerThreadId)
    {
        return _hooksAttached
            && _runtimeAvailable
            && missionGeneration == _missionGeneration
            && ownerThreadId == _ownerThreadId
            && ownerThreadId > 0;
    }

    // 不保留无法确认的待启动任务，避免错误过渡继续向客户端呈现为可执行来源。
    private void FailLocked(string error, DateTime changedAtUtc)
    {
        _runtimeAvailable = false;
        _transitions.Clear();
        _lastError = error;
        AdvanceRevisionLocked(changedAtUtc, "source-runtime-unavailable");
    }

    private void AdvanceRevisionLocked(DateTime changedAtUtc, string lastEvent)
    {
        checked
        {
            _sourceRevision++;
        }
        _changedAtUtc = changedAtUtc;
        _lastEvent = lastEvent;
    }

    private RuntimeAvailableMissionSourceSnapshot SnapshotLocked()
    {
        return new RuntimeAvailableMissionSourceSnapshot(
            _hooksAttached,
            _runtimeAvailable,
            _hookStatus,
            _missionGeneration,
            _sourceRevision,
            _ownerThreadId,
            _transitions.Values
                .OrderBy(transition => transition.EventLabel, StringComparer.Ordinal)
                .Select(transition => transition with
                {
                    References = transition.References
                        .Select(reference => reference with { })
                        .ToArray(),
                })
                .ToArray(),
            _changedAtUtc,
            _lastEvent,
            _lastError);
    }

    private static IReadOnlyList<RuntimeAvailableMissionPendingReference>
        BuildReferences(
            string source,
            IReadOnlyList<string> missionLabels)
    {
        if (missionLabels.Count > MaxReferenceCount)
        {
            throw new InvalidOperationException(
                $"mission-reference-overflow:{missionLabels.Count}");
        }

        var references = new RuntimeAvailableMissionPendingReference[
            missionLabels.Count];
        for (var index = 0; index < missionLabels.Count; index++)
        {
            var missionLabel = missionLabels[index];
            RequireIdentity(missionLabel, $"mission-label-{index}");
            references[index] = new RuntimeAvailableMissionPendingReference(
                source,
                index,
                missionLabel);
        }
        return references;
    }

    private static void ValidateOutcomeList(
        IReadOnlyList<RuntimeAvailableMissionStartOutcome> outcomes)
    {
        if (outcomes.Count > MaxReferenceCount
            || outcomes.Any(outcome => !Enum.IsDefined(
                typeof(RuntimeAvailableMissionStartOutcome),
                outcome)))
        {
            throw new InvalidOperationException("mission-start-outcome-invalid");
        }
    }

    private static bool ReferencesEqual(
        IReadOnlyList<RuntimeAvailableMissionPendingReference> left,
        IReadOnlyList<RuntimeAvailableMissionPendingReference> right)
    {
        return left.Count == right.Count
            && left.Zip(right).All(pair =>
                string.Equals(pair.First.Source, pair.Second.Source, StringComparison.Ordinal)
                && pair.First.SourceOrdinal == pair.Second.SourceOrdinal
                && string.Equals(
                    pair.First.MissionLabel,
                    pair.Second.MissionLabel,
                    StringComparison.Ordinal));
    }

    private static void RequireIdentity(string identity, string source)
    {
        if (string.IsNullOrEmpty(identity)
            || identity.Length > MaxIdentityLength)
        {
            throw new InvalidOperationException($"{source}-invalid");
        }
    }
}
