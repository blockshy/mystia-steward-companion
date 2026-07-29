namespace MystiaStewardCompanion.Save;

internal enum RuntimeScheduledEventDiagnosticPhase
{
    Detached,
    WaitingForMissionRuntime,
    WaitingForDayScene,
    Capturing,
    Ready,
    Unavailable,
}

internal sealed record RuntimeScheduledEventDiagnosticLimits(
    int MaxCaptureAttemptsPerStableWindow,
    int MaxScheduledBucketCount,
    int MaxEventsPerBucket,
    int MaxScheduledEventCount,
    int MaxPostMissionReferences,
    int MaxFinishedEventCount,
    int MaxFinishedMissionCount,
    int MaxLabelLength);

internal sealed record RuntimeScheduledEventTriggerDiagnostic(
    int TriggerType,
    string TriggerTypeName,
    string? TriggerId,
    int TimeDayType,
    string TimeDayTypeName,
    int TimeCalculateType,
    string TimeCalculateTypeName,
    int TimeDay,
    int TimeRangeMinimum,
    int TimeRangeMaximum);

internal sealed record RuntimeScheduledEventDiagnosticEntry(
    string Label,
    int Bucket,
    string BucketSource,
    int BucketOrdinal,
    bool? DefinitionExists,
    bool DefinitionAvailable,
    string DefinitionStatus,
    bool Finished,
    string Disposition,
    string Reason,
    RuntimeScheduledEventTriggerDiagnostic? Trigger,
    RuntimeScheduledEventEligibilityDiagnostic? Eligibility,
    IReadOnlyList<string> PostMissions,
    IReadOnlyList<string> PostMissionsAfterPerformance);

internal sealed record RuntimeScheduledEventMissionReferenceDiagnostic(
    string EventLabel,
    int EventBucket,
    string Source,
    int SourceOrdinal,
    string MissionLabel,
    bool? DefinitionExists,
    bool DefinitionAvailable,
    string DefinitionStatus,
    string Title,
    string TitleStatus,
    bool HasReceiver,
    string Receiver,
    int DefinitionConditionCount,
    bool Active,
    bool Finished,
    string SourceEventEligibilityDisposition,
    string SourceEventEligibilityReason,
    string Disposition,
    string Reason,
    IReadOnlyList<string> PreNodes,
    bool LoopedMission);

internal sealed record RuntimeScheduledEventDiagnosticSnapshot(
    string ReaderStatus,
    RuntimeScheduledEventDiagnosticPhase Phase,
    bool CaptureComplete,
    long MissionGeneration,
    long SourceMissionChangeVersion,
    long DaySceneGeneration,
    long ChangeVersion,
    int OwnerThreadId,
    int CorrectedDay,
    int ScheduledBucketCount,
    int ReadBucketCount,
    int ScheduledEventCount,
    int EventDefinitionFailureCount,
    int EligibleEventCount,
    int IneligibleEventCount,
    int NotApplicableEventCount,
    int ExcludedEventCount,
    int EligibilityFailureCount,
    int PostMissionReferenceCount,
    int MissionDefinitionFailureCount,
    int CandidateMissionReferenceCount,
    int SkippedMissionReferenceCount,
    int InvalidMissionReferenceCount,
    long CaptureElapsedMilliseconds,
    DateTime ChangedAtUtc,
    string LastEvent,
    string LastError)
{
    public static RuntimeScheduledEventDiagnosticSnapshot Detached { get; } = new(
        ReaderStatus: "not attached",
        Phase: RuntimeScheduledEventDiagnosticPhase.Detached,
        CaptureComplete: false,
        MissionGeneration: 0,
        SourceMissionChangeVersion: 0,
        DaySceneGeneration: 0,
        ChangeVersion: 0,
        OwnerThreadId: 0,
        CorrectedDay: 0,
        ScheduledBucketCount: 0,
        ReadBucketCount: 0,
        ScheduledEventCount: 0,
        EventDefinitionFailureCount: 0,
        EligibleEventCount: 0,
        IneligibleEventCount: 0,
        NotApplicableEventCount: 0,
        ExcludedEventCount: 0,
        EligibilityFailureCount: 0,
        PostMissionReferenceCount: 0,
        MissionDefinitionFailureCount: 0,
        CandidateMissionReferenceCount: 0,
        SkippedMissionReferenceCount: 0,
        InvalidMissionReferenceCount: 0,
        CaptureElapsedMilliseconds: 0,
        ChangedAtUtc: DateTime.MinValue,
        LastEvent: "detached",
        LastError: "");
}

internal sealed record RuntimeScheduledEventDiagnosticReport(
    RuntimeScheduledEventDiagnosticSnapshot Summary,
    RuntimeScheduledEventDiagnosticLimits Limits,
    IReadOnlyList<RuntimeScheduledEventDiagnosticEntry> Events,
    IReadOnlyList<RuntimeScheduledEventMissionReferenceDiagnostic> MissionReferences);

internal readonly record struct RuntimeScheduledEventDiagnosticCaptureToken(
    long MissionGeneration,
    long DaySceneGeneration,
    long StateRevision,
    int ThreadId);

internal sealed record RuntimeScheduledMissionSourceReadResult(
    bool Complete,
    long SourceMissionChangeVersion,
    int CorrectedDay,
    int ScheduledBucketCount,
    int ReadBucketCount,
    IReadOnlyList<string> FinishedEvents,
    IReadOnlyList<string> FinishedMissions,
    IReadOnlyList<RuntimeScheduledEventDiagnosticEntry> Events,
    IReadOnlyList<RuntimeScheduledEventMissionReferenceDiagnostic> MissionReferences,
    long CaptureElapsedMilliseconds,
    string Error);

internal static class RuntimeScheduledEventDiagnosticBounds
{
    public static void ValidateCount(int count, int maximumCount, string source)
    {
        if (count < 0 || count > maximumCount)
        {
            throw new InvalidOperationException(
                $"{source}-overflow:{count}; limit={maximumCount}");
        }
    }

    public static IReadOnlySet<string> BuildMembershipSet(
        IReadOnlyList<string> labels,
        int maximumCount,
        string source)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ValidateCount(labels.Count, maximumCount, source);
        return new HashSet<string>(labels, StringComparer.Ordinal);
    }
}

internal static class RuntimeScheduledEventDiagnosticIdentity
{
    public static string ReadHistoryLabel(
        object? value,
        int maximumLength,
        string source,
        int index)
    {
        return ReadLabel(
            value,
            maximumLength,
            source,
            index,
            allowEmpty: true);
    }

    public static string ReadNodeLabel(
        object? value,
        int maximumLength,
        string source,
        int index)
    {
        return ReadLabel(
            value,
            maximumLength,
            source,
            index,
            allowEmpty: false);
    }

    public static string? ReadOptionalIdentifier(
        object? value,
        int maximumLength,
        string source,
        int index)
    {
        ValidateMaximumLength(maximumLength);
        if (value == null)
        {
            return null;
        }

        return ReadLabel(value, maximumLength, source, index, allowEmpty: true);
    }

    private static string ReadLabel(
        object? value,
        int maximumLength,
        string source,
        int index,
        bool allowEmpty)
    {
        ValidateMaximumLength(maximumLength);
        if (value == null)
        {
            throw Invalid(
                source,
                index,
                "null",
                length: null,
                maximumLength,
                actualType: null);
        }
        if (value is not string identifier)
        {
            throw Invalid(
                source,
                index,
                "type",
                length: null,
                maximumLength,
                value.GetType().FullName);
        }
        if (!allowEmpty && identifier.Length == 0)
        {
            throw Invalid(
                source,
                index,
                "empty",
                identifier.Length,
                maximumLength,
                actualType: null);
        }
        if (identifier.Length > maximumLength)
        {
            throw Invalid(
                source,
                index,
                "overlong",
                identifier.Length,
                maximumLength,
                actualType: null);
        }

        return identifier;
    }

    private static InvalidOperationException Invalid(
        string source,
        int index,
        string reason,
        int? length,
        int maximumLength,
        string? actualType)
    {
        var message = "opaque-identifier-invalid:"
            + $"source={source}; index={index}; reason={reason}; "
            + $"length={length?.ToString() ?? "-1"}; limit={maximumLength}";
        if (!string.IsNullOrEmpty(actualType))
        {
            message += $"; actualType={actualType}";
        }

        return new InvalidOperationException(message);
    }

    private static void ValidateMaximumLength(int maximumLength)
    {
        if (maximumLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }
    }
}

internal sealed class RuntimeScheduledEventDiagnosticState
{
    private readonly object _syncRoot = new();
    private readonly RuntimeScheduledEventDiagnosticLimits _limits;

    private bool _attached;
    private bool _armed;
    private long _stateRevision;
    private long _changeVersion;
    private RuntimeScheduledEventDiagnosticReport _report;

    public RuntimeScheduledEventDiagnosticState(
        RuntimeScheduledEventDiagnosticLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        ValidateLimits(limits);
        _report = new RuntimeScheduledEventDiagnosticReport(
            RuntimeScheduledEventDiagnosticSnapshot.Detached,
            limits,
            Array.Empty<RuntimeScheduledEventDiagnosticEntry>(),
            Array.Empty<RuntimeScheduledEventMissionReferenceDiagnostic>());
    }

    public RuntimeScheduledEventDiagnosticSnapshot Snapshot()
    {
        lock (_syncRoot)
        {
            return _report.Summary with { };
        }
    }

    public RuntimeScheduledEventDiagnosticReport Report()
    {
        lock (_syncRoot)
        {
            return new RuntimeScheduledEventDiagnosticReport(
                _report.Summary with { },
                _limits,
                _report.Events.ToArray(),
                _report.MissionReferences.ToArray());
        }
    }

    public void SetReaderStatus(
        string status,
        bool attached,
        DateTime changedAtUtc)
    {
        lock (_syncRoot)
        {
            _attached = attached;
            _armed = false;
            PublishLocked(
                RuntimeScheduledEventDiagnosticSnapshot.Detached with
                {
                    ReaderStatus = NormalizeStatus(status, attached ? "attached" : "unavailable"),
                    Phase = attached
                        ? RuntimeScheduledEventDiagnosticPhase.WaitingForMissionRuntime
                        : RuntimeScheduledEventDiagnosticPhase.Unavailable,
                    ChangedAtUtc = changedAtUtc,
                    LastEvent = attached ? "reader attached" : "reader unavailable",
                    LastError = attached ? "" : NormalizeStatus(status, "reader-unavailable"),
                },
                Array.Empty<RuntimeScheduledEventDiagnosticEntry>(),
                Array.Empty<RuntimeScheduledEventMissionReferenceDiagnostic>());
        }
    }

    public void ResetForMissionGeneration(
        long missionGeneration,
        int ownerThreadId,
        DateTime changedAtUtc)
    {
        if (missionGeneration < 1) throw new ArgumentOutOfRangeException(nameof(missionGeneration));
        if (ownerThreadId < 1) throw new ArgumentOutOfRangeException(nameof(ownerThreadId));

        lock (_syncRoot)
        {
            if (!_attached) return;

            _armed = false;
            PublishLocked(
                EmptySnapshotLocked(
                    RuntimeScheduledEventDiagnosticPhase.WaitingForMissionRuntime,
                    missionGeneration,
                    daySceneGeneration: 0,
                    ownerThreadId,
                    changedAtUtc,
                    "mission generation reset",
                    ""),
                Array.Empty<RuntimeScheduledEventDiagnosticEntry>(),
                Array.Empty<RuntimeScheduledEventMissionReferenceDiagnostic>());
        }
    }

    public bool ArmMissionGeneration(
        long missionGeneration,
        int ownerThreadId,
        DateTime changedAtUtc)
    {
        lock (_syncRoot)
        {
            var current = _report.Summary;
            if (!_attached
                || missionGeneration < 1
                || ownerThreadId < 1
                || current.MissionGeneration != missionGeneration
                || current.OwnerThreadId != ownerThreadId)
            {
                return false;
            }

            _armed = true;
            PublishLocked(
                EmptySnapshotLocked(
                    RuntimeScheduledEventDiagnosticPhase.WaitingForDayScene,
                    missionGeneration,
                    daySceneGeneration: 0,
                    ownerThreadId,
                    changedAtUtc,
                    "mission generation armed",
                    ""),
                Array.Empty<RuntimeScheduledEventDiagnosticEntry>(),
                Array.Empty<RuntimeScheduledEventMissionReferenceDiagnostic>());
            return true;
        }
    }

    public void WaitForDayScene(
        long missionGeneration,
        long daySceneGeneration,
        int ownerThreadId,
        string reason,
        DateTime changedAtUtc)
    {
        lock (_syncRoot)
        {
            var current = _report.Summary;
            if (!_attached
                || !_armed
                || current.MissionGeneration != missionGeneration
                || current.OwnerThreadId != ownerThreadId)
            {
                return;
            }

            var normalizedReason = NormalizeStatus(reason, "day-scene-not-ready");
            if (current.Phase == RuntimeScheduledEventDiagnosticPhase.WaitingForDayScene
                && current.DaySceneGeneration == daySceneGeneration
                && string.Equals(current.LastEvent, normalizedReason, StringComparison.Ordinal))
            {
                return;
            }

            PublishLocked(
                EmptySnapshotLocked(
                    RuntimeScheduledEventDiagnosticPhase.WaitingForDayScene,
                    missionGeneration,
                    daySceneGeneration,
                    ownerThreadId,
                    changedAtUtc,
                    normalizedReason,
                    ""),
                Array.Empty<RuntimeScheduledEventDiagnosticEntry>(),
                Array.Empty<RuntimeScheduledEventMissionReferenceDiagnostic>());
        }
    }

    public bool TryBeginCapture(
        long missionGeneration,
        long daySceneGeneration,
        int ownerThreadId,
        DateTime changedAtUtc,
        out RuntimeScheduledEventDiagnosticCaptureToken token)
    {
        token = default;
        lock (_syncRoot)
        {
            var current = _report.Summary;
            if (!_attached
                || !_armed
                || missionGeneration < 1
                || daySceneGeneration < 1
                || ownerThreadId < 1
                || current.MissionGeneration != missionGeneration
                || current.OwnerThreadId != ownerThreadId)
            {
                return false;
            }

            if (current.DaySceneGeneration == daySceneGeneration
                && current.Phase is RuntimeScheduledEventDiagnosticPhase.Ready
                    or RuntimeScheduledEventDiagnosticPhase.Unavailable
                    or RuntimeScheduledEventDiagnosticPhase.Capturing)
            {
                return false;
            }

            PublishLocked(
                EmptySnapshotLocked(
                    RuntimeScheduledEventDiagnosticPhase.Capturing,
                    missionGeneration,
                    daySceneGeneration,
                    ownerThreadId,
                    changedAtUtc,
                    "capture started",
                    ""),
                Array.Empty<RuntimeScheduledEventDiagnosticEntry>(),
                Array.Empty<RuntimeScheduledEventMissionReferenceDiagnostic>());
            token = new RuntimeScheduledEventDiagnosticCaptureToken(
                missionGeneration,
                daySceneGeneration,
                _stateRevision,
                ownerThreadId);
            return true;
        }
    }

    public bool TryCommitCapture(
        RuntimeScheduledEventDiagnosticCaptureToken token,
        RuntimeScheduledMissionSourceReadResult result,
        DateTime changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_syncRoot)
        {
            if (!IsCurrentCaptureLocked(token)) return false;

            var events = result.Events.ToArray();
            var references = result.MissionReferences.ToArray();
            ValidateResult(result, events, references);
            var eventDefinitionFailures = events.Count(entry => !entry.DefinitionAvailable);
            var eligibleEvents = events.Count(entry => string.Equals(
                entry.Eligibility?.Disposition,
                "eligible",
                StringComparison.Ordinal));
            var ineligibleEvents = events.Count(entry => string.Equals(
                entry.Eligibility?.Disposition,
                "ineligible",
                StringComparison.Ordinal));
            var notApplicableEvents = events.Count(entry => string.Equals(
                entry.Eligibility?.Disposition,
                "not-applicable",
                StringComparison.Ordinal));
            var excludedEvents = events.Count(entry => string.Equals(
                entry.Eligibility?.Disposition,
                "excluded",
                StringComparison.Ordinal));
            var eligibilityFailures = events.Count(entry => string.Equals(
                entry.Eligibility?.Disposition,
                "invalid",
                StringComparison.Ordinal));
            var missionDefinitionFailures = references.Count(entry => !entry.DefinitionAvailable);
            var candidates = references.Count(entry =>
                string.Equals(entry.Disposition, "candidate", StringComparison.Ordinal));
            var skipped = references.Count(entry =>
                string.Equals(entry.Disposition, "skipped", StringComparison.Ordinal));
            var invalid = references.Count(entry =>
                string.Equals(entry.Disposition, "invalid", StringComparison.Ordinal));
            var error = result.Complete
                ? ""
                : NormalizeStatus(result.Error, "capture-incomplete");

            PublishLocked(
                new RuntimeScheduledEventDiagnosticSnapshot(
                    ReaderStatus: _report.Summary.ReaderStatus,
                    Phase: result.Complete
                        ? RuntimeScheduledEventDiagnosticPhase.Ready
                        : RuntimeScheduledEventDiagnosticPhase.Unavailable,
                    CaptureComplete: result.Complete,
                    MissionGeneration: token.MissionGeneration,
                    SourceMissionChangeVersion: result.SourceMissionChangeVersion,
                    DaySceneGeneration: token.DaySceneGeneration,
                    ChangeVersion: 0,
                    OwnerThreadId: token.ThreadId,
                    CorrectedDay: result.CorrectedDay,
                    ScheduledBucketCount: result.ScheduledBucketCount,
                    ReadBucketCount: result.ReadBucketCount,
                    ScheduledEventCount: events.Length,
                    EventDefinitionFailureCount: eventDefinitionFailures,
                    EligibleEventCount: eligibleEvents,
                    IneligibleEventCount: ineligibleEvents,
                    NotApplicableEventCount: notApplicableEvents,
                    ExcludedEventCount: excludedEvents,
                    EligibilityFailureCount: eligibilityFailures,
                    PostMissionReferenceCount: references.Length,
                    MissionDefinitionFailureCount: missionDefinitionFailures,
                    CandidateMissionReferenceCount: candidates,
                    SkippedMissionReferenceCount: skipped,
                    InvalidMissionReferenceCount: invalid,
                    CaptureElapsedMilliseconds: result.CaptureElapsedMilliseconds,
                    ChangedAtUtc: changedAtUtc,
                    LastEvent: result.Complete ? "capture completed" : "capture incomplete",
                    LastError: error),
                events,
                references);
            return true;
        }
    }

    public bool FailCapture(
        RuntimeScheduledEventDiagnosticCaptureToken token,
        string failure,
        long captureElapsedMilliseconds,
        DateTime changedAtUtc)
    {
        lock (_syncRoot)
        {
            if (!IsCurrentCaptureLocked(token)) return false;

            var current = _report.Summary;
            PublishLocked(
                EmptySnapshotLocked(
                    RuntimeScheduledEventDiagnosticPhase.Unavailable,
                    token.MissionGeneration,
                    token.DaySceneGeneration,
                    token.ThreadId,
                    changedAtUtc,
                    "capture failed",
                    NormalizeStatus(failure, "capture-failed")) with
                {
                    CaptureElapsedMilliseconds = Math.Max(0, captureElapsedMilliseconds),
                    CorrectedDay = current.CorrectedDay,
                },
                Array.Empty<RuntimeScheduledEventDiagnosticEntry>(),
                Array.Empty<RuntimeScheduledEventMissionReferenceDiagnostic>());
            return true;
        }
    }

    private bool IsCurrentCaptureLocked(
        RuntimeScheduledEventDiagnosticCaptureToken token)
    {
        var current = _report.Summary;
        return _attached
            && _armed
            && current.Phase == RuntimeScheduledEventDiagnosticPhase.Capturing
            && current.MissionGeneration == token.MissionGeneration
            && current.DaySceneGeneration == token.DaySceneGeneration
            && current.OwnerThreadId == token.ThreadId
            && _stateRevision == token.StateRevision;
    }

    private RuntimeScheduledEventDiagnosticSnapshot EmptySnapshotLocked(
        RuntimeScheduledEventDiagnosticPhase phase,
        long missionGeneration,
        long daySceneGeneration,
        int ownerThreadId,
        DateTime changedAtUtc,
        string lastEvent,
        string lastError)
    {
        return RuntimeScheduledEventDiagnosticSnapshot.Detached with
        {
            ReaderStatus = _report.Summary.ReaderStatus,
            Phase = phase,
            MissionGeneration = missionGeneration,
            DaySceneGeneration = daySceneGeneration,
            OwnerThreadId = ownerThreadId,
            ChangedAtUtc = changedAtUtc,
            LastEvent = lastEvent,
            LastError = lastError,
        };
    }

    private void PublishLocked(
        RuntimeScheduledEventDiagnosticSnapshot snapshot,
        IReadOnlyList<RuntimeScheduledEventDiagnosticEntry> events,
        IReadOnlyList<RuntimeScheduledEventMissionReferenceDiagnostic> references)
    {
        _stateRevision++;
        _changeVersion++;
        _report = new RuntimeScheduledEventDiagnosticReport(
            snapshot with { ChangeVersion = _changeVersion },
            _limits,
            events,
            references);
    }

    private void ValidateResult(
        RuntimeScheduledMissionSourceReadResult result,
        IReadOnlyList<RuntimeScheduledEventDiagnosticEntry> events,
        IReadOnlyList<RuntimeScheduledEventMissionReferenceDiagnostic> references)
    {
        if (result.CorrectedDay < 0
            || result.SourceMissionChangeVersion < 0
            || result.ScheduledBucketCount < 0
            || result.ScheduledBucketCount > _limits.MaxScheduledBucketCount
            || result.ReadBucketCount < 0
            || result.ReadBucketCount > 2
            || events.Count > _limits.MaxScheduledEventCount
            || references.Count > _limits.MaxPostMissionReferences
            || result.CaptureElapsedMilliseconds < 0)
        {
            throw new InvalidOperationException("Scheduled event diagnostic result exceeds its hard limits.");
        }
        RuntimeScheduledEventDiagnosticBounds.ValidateCount(
            result.FinishedEvents.Count,
            _limits.MaxFinishedEventCount,
            "finished-events");
        RuntimeScheduledEventDiagnosticBounds.ValidateCount(
            result.FinishedMissions.Count,
            _limits.MaxFinishedMissionCount,
            "finished-missions");

        if (result.Complete && !string.IsNullOrEmpty(result.Error))
        {
            throw new InvalidOperationException("A complete scheduled event diagnostic result contains an error.");
        }

        if (events.Any(entry =>
                entry.Eligibility != null
                && entry.Eligibility.Disposition is not (
                    "eligible"
                    or "ineligible"
                    or "not-applicable"
                    or "excluded"
                    or "invalid")))
        {
            throw new InvalidOperationException(
                "A scheduled event contains an unsupported eligibility disposition.");
        }

        if (events.Any(entry =>
                entry.DefinitionAvailable != (entry.Eligibility != null)))
        {
            throw new InvalidOperationException(
                "Scheduled event definition and eligibility availability disagree.");
        }

        var hasInvalidEvent = events.Any(entry =>
            string.Equals(entry.Disposition, "invalid", StringComparison.Ordinal));
        var hasInvalidEligibility = events.Any(entry =>
            string.Equals(
                entry.Eligibility?.Disposition,
                "invalid",
                StringComparison.Ordinal));
        var hasInvalidMissionReference = references.Any(entry =>
            string.Equals(entry.Disposition, "invalid", StringComparison.Ordinal));
        var expectedComplete = !hasInvalidEvent
            && !hasInvalidEligibility
            && !hasInvalidMissionReference;
        if (result.Complete != expectedComplete)
        {
            throw new InvalidOperationException(
                "Scheduled event diagnostic completion disagrees with invalid evidence.");
        }
        if (!result.Complete && string.IsNullOrEmpty(result.Error))
        {
            throw new InvalidOperationException(
                "An incomplete scheduled event diagnostic result is missing its error.");
        }
    }

    private static void ValidateLimits(
        RuntimeScheduledEventDiagnosticLimits limits)
    {
        if (limits.MaxCaptureAttemptsPerStableWindow != 1
            || limits.MaxScheduledBucketCount < 1
            || limits.MaxEventsPerBucket < 1
            || limits.MaxScheduledEventCount < 1
            || limits.MaxPostMissionReferences < 1
            || limits.MaxFinishedEventCount < 1
            || limits.MaxFinishedMissionCount < 1
            || limits.MaxLabelLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }
    }

    private static string NormalizeStatus(string value, string fallback)
    {
        var normalized = value?.Trim() ?? "";
        return normalized.Length == 0 ? fallback : normalized;
    }
}
