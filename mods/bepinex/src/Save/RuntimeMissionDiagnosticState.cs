namespace MystiaStewardCompanion.Save;

internal enum RuntimeMissionDiagnosticPhase
{
    Detached,
    WaitingForLoad,
    CapturingLoadSeed,
    LoadSeedReady,
    Initializing,
    Ready,
    Unavailable,
}

internal enum RuntimeMissionDiagnosticFreshness
{
    Unverified,
    Tracking,
    Fulfilled,
}

internal sealed record RuntimeMissionDiagnosticSnapshot(
    string HookStatus,
    RuntimeMissionDiagnosticPhase Phase,
    bool RuntimeAvailable,
    long Generation,
    long ChangeVersion,
    int OwnerThreadId,
    int TrackingBucketCount,
    int TrackingBufferCount,
    int ActiveMissionCount,
    int UnverifiedMissionCount,
    int TrackingMissionCount,
    int FulfilledMissionCount,
    int FinishedUniqueMissionCount,
    int DefinitionAvailableCount,
    int DefinitionFailureCount,
    int TitleAvailableCount,
    int ServeInWorkMissionCount,
    int LoadJsonLength,
    string LoadJsonSha256,
    long SerializeElapsedMilliseconds,
    long ParseElapsedMilliseconds,
    long DefinitionReadElapsedMilliseconds,
    string FileVersion,
    int SavedGameDay,
    int CurrentDate,
    int SelectedDlcCount,
    int ParsedDlcPartitionCount,
    int ParsedTrackingMissionCount,
    int ParsedFinishedMissionCount,
    int SeedTrackingMissionCount,
    long StartAttemptCount,
    long StartCommitCount,
    long RemoveCount,
    long FinishCount,
    long StateRefreshCount,
    long FinishNodeObservationCount,
    DateTime ChangedAtUtc,
    string LastEvent,
    string LastError)
{
    public static RuntimeMissionDiagnosticSnapshot Detached { get; } = new(
        HookStatus: "not attached",
        Phase: RuntimeMissionDiagnosticPhase.Detached,
        RuntimeAvailable: false,
        Generation: 0,
        ChangeVersion: 0,
        OwnerThreadId: 0,
        TrackingBucketCount: 0,
        TrackingBufferCount: 0,
        ActiveMissionCount: 0,
        UnverifiedMissionCount: 0,
        TrackingMissionCount: 0,
        FulfilledMissionCount: 0,
        FinishedUniqueMissionCount: 0,
        DefinitionAvailableCount: 0,
        DefinitionFailureCount: 0,
        TitleAvailableCount: 0,
        ServeInWorkMissionCount: 0,
        LoadJsonLength: 0,
        LoadJsonSha256: "",
        SerializeElapsedMilliseconds: 0,
        ParseElapsedMilliseconds: 0,
        DefinitionReadElapsedMilliseconds: 0,
        FileVersion: "",
        SavedGameDay: 0,
        CurrentDate: 0,
        SelectedDlcCount: 0,
        ParsedDlcPartitionCount: 0,
        ParsedTrackingMissionCount: 0,
        ParsedFinishedMissionCount: 0,
        SeedTrackingMissionCount: 0,
        StartAttemptCount: 0,
        StartCommitCount: 0,
        RemoveCount: 0,
        FinishCount: 0,
        StateRefreshCount: 0,
        FinishNodeObservationCount: 0,
        ChangedAtUtc: DateTime.MinValue,
        LastEvent: "detached",
        LastError: "");
}

internal readonly record struct RuntimeMissionDiagnosticLoadToken(
    long Generation,
    int ThreadId);

internal readonly record struct RuntimeMissionDiagnosticInitializationToken(
    long Generation,
    int ThreadId);

internal sealed record RuntimeMissionDiagnosticLoadMetrics(
    int JsonLength,
    string JsonSha256,
    long SerializeElapsedMilliseconds,
    long ParseElapsedMilliseconds,
    string FileVersion,
    int SavedGameDay,
    int ParsedTrackingMissionCount,
    int ParsedFinishedMissionCount,
    int ParsedDlcPartitionCount);

internal sealed record RuntimeMissionDiagnosticTrackedSeed(
    nint Identity,
    string Label,
    IReadOnlyList<bool> FinishStates);

internal sealed record RuntimeMissionDiagnosticLoadedSeed(
    string SourcePartition,
    bool SourceIsCore,
    int SourceBucket,
    int MergedBucket,
    int SourceOrdinal,
    string Label,
    int SavedFinishStateCount,
    int SavedTrueFinishStateCount,
    int ConditionDataCount,
    RuntimeMissionDiagnosticTrackedSeed RefreshedState,
    RuntimeMissionDefinitionDiagnosticReadResult Definition);

internal sealed record RuntimeMissionDiagnosticInitializationSeed(
    long Generation,
    int ThreadId,
    int RuntimeTrackingBucketCount,
    int SeedTrackingBucketCount,
    int TrackingBufferCount,
    int CurrentDate,
    IReadOnlyList<string> SelectedDlcPartitions,
    IReadOnlyList<RuntimeMissionDiagnosticLoadedSeed> TrackedMissions,
    IReadOnlyList<string> SeedFinishedMissionLabels,
    IReadOnlyList<string> RuntimeFinishedMissionLabels,
    long DefinitionReadElapsedMilliseconds);

internal sealed record RuntimeMissionDiagnosticTaskSnapshot(
    string Label,
    string Title,
    string TitleStatus,
    bool HasReceiver,
    string Receiver,
    string CharacterName,
    IReadOnlyList<string> SceneNames,
    string PresentationStatus,
    string SourcePartition,
    bool SourceIsCore,
    int SourceBucket,
    int MergedBucket,
    int SourceOrdinal,
    int SavedFinishStateCount,
    int SavedTrueFinishStateCount,
    int ConditionDataCount,
    bool Active,
    bool NativeIdentityBound,
    RuntimeMissionDiagnosticFreshness Freshness,
    int CurrentFinishStateCount,
    int CurrentTrueFinishStateCount,
    bool? Fulfilled,
    int DefinitionConditionCount,
    IReadOnlyList<RuntimeMissionDefinitionDiagnosticCondition> Conditions,
    IReadOnlyList<int> ServeInWorkFoodIds,
    string DefinitionStatus,
    string ValidationError);

internal sealed record RuntimeMissionDiagnosticReport(
    RuntimeMissionDiagnosticSnapshot Summary,
    IReadOnlyList<RuntimeMissionDiagnosticTaskSnapshot> Tasks);

internal sealed record RuntimeMissionServeInWorkDefinition(
    string Label,
    string Receiver,
    IReadOnlyList<int> FoodIds,
    RuntimeMissionDiagnosticFreshness Freshness);

internal sealed class RuntimeMissionDiagnosticState
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, TrackedMissionState> _missionsByLabel =
        new(StringComparer.Ordinal);
    private readonly Dictionary<nint, string> _labelsByIdentity = new();
    private readonly HashSet<string> _finishedMissionLabels = new(StringComparer.Ordinal);

    private int _activeMissionCount;
    private int _unverifiedMissionCount;
    private int _trackingMissionCount;
    private int _fulfilledMissionCount;
    private int _definitionAvailableCount;
    private int _titleAvailableCount;
    private int _serveInWorkMissionCount;

    private RuntimeMissionDiagnosticSnapshot _snapshot = RuntimeMissionDiagnosticSnapshot.Detached;

    public RuntimeMissionDiagnosticSnapshot Snapshot()
    {
        return Volatile.Read(ref _snapshot) with { };
    }

    public RuntimeMissionDiagnosticReport Report()
    {
        lock (_syncRoot)
        {
            var tasks = _missionsByLabel.Values
                .OrderBy(mission => mission.SourceIsCore ? 0 : 1)
                .ThenBy(mission => mission.SourcePartition, StringComparer.Ordinal)
                .ThenBy(mission => mission.MergedBucket)
                .ThenBy(mission => mission.SourceOrdinal)
                .ThenBy(mission => mission.Label, StringComparer.Ordinal)
                .Select(ToTaskSnapshot)
                .ToArray();
            return new RuntimeMissionDiagnosticReport(SnapshotLocked(), tasks);
        }
    }

    public RuntimeTrackedMissionsSnapshot ReadTrackedMissions()
    {
        lock (_syncRoot)
        {
            var current = SnapshotLocked();
            var unavailableStatus = ResolveTrackedMissionUnavailableStatus(current);
            if (unavailableStatus.Length > 0)
            {
                return UnavailableTrackedMissions(current.Generation, unavailableStatus);
            }

            var activeMissions = _missionsByLabel.Values
                .Where(mission => mission.Active)
                .OrderBy(mission => mission.Label, StringComparer.Ordinal)
                .ToArray();
            if (activeMissions.Length != _activeMissionCount)
            {
                return UnavailableTrackedMissions(
                    current.Generation,
                    RuntimeTrackedMissionsSnapshot.MissionDataIncompleteStatus);
            }

            var result = new RuntimeTrackedMissionSnapshot[activeMissions.Length];
            for (var index = 0; index < activeMissions.Length; index++)
            {
                if (!TryProjectTrackedMission(activeMissions[index], out result[index]))
                {
                    return UnavailableTrackedMissions(
                        current.Generation,
                        RuntimeTrackedMissionsSnapshot.MissionDataIncompleteStatus);
                }
            }

            return new RuntimeTrackedMissionsSnapshot(
                RuntimeAvailable: true,
                current.Generation,
                RuntimeTrackedMissionsSnapshot.ReadyStatus,
                result);
        }
    }

    public void SetHookStatus(string hookStatus, bool attached, DateTime changedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(hookStatus))
        {
            throw new ArgumentException("Hook status is required.", nameof(hookStatus));
        }

        lock (_syncRoot)
        {
            var current = _snapshot;
            Publish(current with
            {
                HookStatus = hookStatus.Trim(),
                Phase = attached && current.Phase == RuntimeMissionDiagnosticPhase.Detached
                    ? RuntimeMissionDiagnosticPhase.WaitingForLoad
                    : current.Phase,
                ChangeVersion = checked(current.ChangeVersion + 1),
                ChangedAtUtc = changedAtUtc,
                LastEvent = attached ? "hooks-attached" : "hooks-unavailable",
                LastError = attached ? current.LastError : "hook-installation-incomplete",
            });
        }
    }

    public RuntimeMissionDiagnosticLoadToken BeginLoadCapture(
        int threadId,
        DateTime changedAtUtc)
    {
        if (threadId <= 0) throw new ArgumentOutOfRangeException(nameof(threadId));

        lock (_syncRoot)
        {
            ClearMissionDataLocked();
            var current = _snapshot;
            var generation = checked(current.Generation + 1);
            Publish(current with
            {
                Phase = RuntimeMissionDiagnosticPhase.CapturingLoadSeed,
                RuntimeAvailable = false,
                Generation = generation,
                ChangeVersion = checked(current.ChangeVersion + 1),
                OwnerThreadId = threadId,
                TrackingBucketCount = 0,
                TrackingBufferCount = 0,
                ActiveMissionCount = 0,
                UnverifiedMissionCount = 0,
                TrackingMissionCount = 0,
                FulfilledMissionCount = 0,
                FinishedUniqueMissionCount = 0,
                DefinitionAvailableCount = 0,
                DefinitionFailureCount = 0,
                TitleAvailableCount = 0,
                ServeInWorkMissionCount = 0,
                LoadJsonLength = 0,
                LoadJsonSha256 = "",
                SerializeElapsedMilliseconds = 0,
                ParseElapsedMilliseconds = 0,
                DefinitionReadElapsedMilliseconds = 0,
                FileVersion = "",
                SavedGameDay = 0,
                CurrentDate = 0,
                SelectedDlcCount = 0,
                ParsedDlcPartitionCount = 0,
                ParsedTrackingMissionCount = 0,
                ParsedFinishedMissionCount = 0,
                SeedTrackingMissionCount = 0,
                StartAttemptCount = 0,
                StartCommitCount = 0,
                RemoveCount = 0,
                FinishCount = 0,
                StateRefreshCount = 0,
                FinishNodeObservationCount = 0,
                ChangedAtUtc = changedAtUtc,
                LastEvent = "load-capture-started",
                LastError = "",
            });
            return new RuntimeMissionDiagnosticLoadToken(generation, threadId);
        }
    }

    public bool TryMarkLoadSeedReady(
        RuntimeMissionDiagnosticLoadToken token,
        RuntimeMissionDiagnosticLoadMetrics metrics,
        DateTime changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ValidateLoadMetrics(metrics);

        lock (_syncRoot)
        {
            if (!IsCurrentLoadCapture(token)) return false;
            var current = _snapshot;
            Publish(current with
            {
                Phase = RuntimeMissionDiagnosticPhase.LoadSeedReady,
                ChangeVersion = checked(current.ChangeVersion + 1),
                LoadJsonLength = metrics.JsonLength,
                LoadJsonSha256 = metrics.JsonSha256,
                SerializeElapsedMilliseconds = metrics.SerializeElapsedMilliseconds,
                ParseElapsedMilliseconds = metrics.ParseElapsedMilliseconds,
                FileVersion = metrics.FileVersion,
                SavedGameDay = metrics.SavedGameDay,
                ParsedDlcPartitionCount = metrics.ParsedDlcPartitionCount,
                ParsedTrackingMissionCount = metrics.ParsedTrackingMissionCount,
                ParsedFinishedMissionCount = metrics.ParsedFinishedMissionCount,
                ChangedAtUtc = changedAtUtc,
                LastEvent = "load-seed-ready",
                LastError = "",
            });
            return true;
        }
    }

    public bool TryBeginInitialization(
        RuntimeMissionDiagnosticLoadToken loadToken,
        int threadId,
        DateTime changedAtUtc,
        out RuntimeMissionDiagnosticInitializationToken token)
    {
        token = default;
        lock (_syncRoot)
        {
            if (_snapshot.Phase != RuntimeMissionDiagnosticPhase.LoadSeedReady
                || _snapshot.Generation != loadToken.Generation
                || _snapshot.OwnerThreadId != loadToken.ThreadId
                || threadId != loadToken.ThreadId)
            {
                return false;
            }

            var current = _snapshot;
            Publish(current with
            {
                Phase = RuntimeMissionDiagnosticPhase.Initializing,
                ChangeVersion = checked(current.ChangeVersion + 1),
                ChangedAtUtc = changedAtUtc,
                LastEvent = "initialize-started",
                LastError = "",
            });
            token = new RuntimeMissionDiagnosticInitializationToken(
                loadToken.Generation,
                loadToken.ThreadId);
            return true;
        }
    }

    public bool TryCommitInitialization(
        RuntimeMissionDiagnosticInitializationToken token,
        RuntimeMissionDiagnosticInitializationSeed seed,
        DateTime changedAtUtc,
        out RuntimeMissionDiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(seed);
        lock (_syncRoot)
        {
            if (!IsCurrentInitialization(token))
            {
                snapshot = SnapshotLocked();
                return false;
            }

            var validationFailure = ValidateInitializationSeed(token, seed);
            if (validationFailure.Length > 0)
            {
                snapshot = MarkUnavailableLocked(
                    token.Generation,
                    validationFailure,
                    seed.RuntimeTrackingBucketCount,
                    seed.TrackingBufferCount,
                    changedAtUtc);
                return false;
            }

            foreach (var label in seed.SeedFinishedMissionLabels)
            {
                _finishedMissionLabels.Add(label);
            }

            foreach (var loaded in seed.TrackedMissions)
            {
                var definition = loaded.Definition.Success
                    ? loaded.Definition.Definition
                    : null;
                var validationError = DefinitionValidationError(
                    loaded.Label,
                    loaded.RefreshedState.FinishStates.Count,
                    definition,
                    loaded.Definition.Failure,
                    stateVerified: true);
                var freshness = ResolveFreshness(
                    stateVerified: true,
                    loaded.RefreshedState.FinishStates,
                    definition,
                    validationError);
                _labelsByIdentity.Add(
                    loaded.RefreshedState.Identity,
                    loaded.Label);
                AddMissionLocked(new TrackedMissionState(
                    loaded.Label,
                    loaded.SourcePartition,
                    loaded.SourceIsCore,
                    loaded.SourceBucket,
                    loaded.MergedBucket,
                    loaded.SourceOrdinal,
                    loaded.SavedFinishStateCount,
                    loaded.SavedTrueFinishStateCount,
                    loaded.ConditionDataCount,
                    Identity: loaded.RefreshedState.Identity,
                    Active: true,
                    CurrentFinishStates: loaded.RefreshedState.FinishStates.ToArray(),
                    Freshness: freshness,
                    Definition: definition,
                    DefinitionStatus: loaded.Definition.Success
                        ? "available"
                        : loaded.Definition.Failure,
                    ValidationError: validationError,
                    Presentation: InitialPresentation(definition),
                    PresentationDaySceneGeneration: 0,
                    PresentationMappedCapturedAtUtc: DateTime.MinValue,
                    PresentationAttemptCount: 0,
                    PresentationNextAttemptAtUtc: DateTime.MinValue));
            }

            var current = _snapshot;
            PublishCountsLocked(
                current with
                {
                    Phase = RuntimeMissionDiagnosticPhase.Ready,
                    RuntimeAvailable = true,
                    TrackingBucketCount = seed.RuntimeTrackingBucketCount,
                    TrackingBufferCount = seed.TrackingBufferCount,
                    CurrentDate = seed.CurrentDate,
                    SelectedDlcCount = seed.SelectedDlcPartitions.Count,
                    SeedTrackingMissionCount = seed.TrackedMissions.Count,
                    DefinitionReadElapsedMilliseconds =
                        seed.DefinitionReadElapsedMilliseconds,
                    StateRefreshCount = checked(
                        current.StateRefreshCount + seed.TrackedMissions.Count),
                    ChangedAtUtc = changedAtUtc,
                    LastEvent = "initialize-refreshed-committed",
                    LastError = "",
                },
                changedAtUtc);
            snapshot = SnapshotLocked();
            return true;
        }
    }

    public void FailLoadCapture(
        RuntimeMissionDiagnosticLoadToken token,
        string failure,
        DateTime changedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(failure))
        {
            throw new ArgumentException("Failure is required.", nameof(failure));
        }

        lock (_syncRoot)
        {
            if (_snapshot.Generation != token.Generation
                || _snapshot.OwnerThreadId != token.ThreadId
                || _snapshot.Phase is not (
                    RuntimeMissionDiagnosticPhase.CapturingLoadSeed
                    or RuntimeMissionDiagnosticPhase.LoadSeedReady))
            {
                return;
            }

            MarkUnavailableLocked(
                token.Generation,
                failure,
                trackingBucketCount: 0,
                trackingBufferCount: 0,
                changedAtUtc);
        }
    }

    public void FailInitialization(
        RuntimeMissionDiagnosticInitializationToken token,
        string failure,
        int trackingBucketCount,
        int trackingBufferCount,
        DateTime changedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(failure))
        {
            throw new ArgumentException("Failure is required.", nameof(failure));
        }

        lock (_syncRoot)
        {
            if (!IsCurrentInitialization(token)) return;
            MarkUnavailableLocked(
                token.Generation,
                failure,
                trackingBucketCount,
                trackingBufferCount,
                changedAtUtc);
        }
    }

    public void FailCurrentGeneration(
        long generation,
        int threadId,
        string failure,
        DateTime changedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(failure))
        {
            throw new ArgumentException("Failure is required.", nameof(failure));
        }

        lock (_syncRoot)
        {
            if (_snapshot.Generation != generation
                || _snapshot.Phase is RuntimeMissionDiagnosticPhase.Detached
                    or RuntimeMissionDiagnosticPhase.WaitingForLoad
                    or RuntimeMissionDiagnosticPhase.Unavailable)
            {
                return;
            }

            MarkUnavailableLocked(
                generation,
                _snapshot.OwnerThreadId == threadId
                    ? failure
                    : "lifecycle-thread-mismatch",
                _snapshot.TrackingBucketCount,
                _snapshot.TrackingBufferCount,
                changedAtUtc);
        }
    }

    public bool ObserveStartAttempt(long generation, int threadId, DateTime changedAtUtc)
    {
        lock (_syncRoot)
        {
            if (!CanObserve(generation, threadId)) return false;
            var current = _snapshot;
            Publish(current with
            {
                ChangeVersion = checked(current.ChangeVersion + 1),
                StartAttemptCount = checked(current.StartAttemptCount + 1),
                ChangedAtUtc = changedAtUtc,
                LastEvent = "start-observed",
            });
            return true;
        }
    }

    public bool TryCommitStartedMission(
        long generation,
        int threadId,
        RuntimeMissionDiagnosticTrackedSeed seed,
        RuntimeMissionDefinitionDiagnosticReadResult definitionRead,
        bool stateVerified,
        DateTime changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(definitionRead);
        lock (_syncRoot)
        {
            if (!CanObserve(generation, threadId)) return false;
            if (!IsValidTrackedSeed(seed))
            {
                MarkUnavailableLocked(
                    generation,
                    "invalid-started-mission",
                    _snapshot.TrackingBucketCount,
                    _snapshot.TrackingBufferCount,
                    changedAtUtc);
                return false;
            }

            if (_missionsByLabel.TryGetValue(seed.Label, out var duplicateLabel)
                && duplicateLabel.Active)
            {
                MarkUnavailableLocked(
                    generation,
                    "duplicate-active-mission-label",
                    _snapshot.TrackingBucketCount,
                    _snapshot.TrackingBufferCount,
                    changedAtUtc);
                return false;
            }

            if (_labelsByIdentity.TryGetValue(seed.Identity, out var previousLabel))
            {
                if (!_missionsByLabel.TryGetValue(previousLabel, out var previousMission)
                    || previousMission.Identity != seed.Identity)
                {
                    MarkUnavailableLocked(
                        generation,
                        "invalid-reused-mission-identity",
                        _snapshot.TrackingBucketCount,
                        _snapshot.TrackingBufferCount,
                        changedAtUtc);
                    return false;
                }
                if (previousMission.Active)
                {
                    MarkUnavailableLocked(
                        generation,
                        "duplicate-active-mission-identity",
                        _snapshot.TrackingBucketCount,
                        _snapshot.TrackingBufferCount,
                        changedAtUtc);
                    return false;
                }
                if (!TryReleaseInactiveMissionIdentityLocked(previousLabel))
                {
                    MarkUnavailableLocked(
                        generation,
                        "invalid-reused-mission-identity",
                        _snapshot.TrackingBucketCount,
                        _snapshot.TrackingBufferCount,
                        changedAtUtc);
                    return false;
                }
            }
            if (_missionsByLabel.ContainsKey(seed.Label)
                && !TryReleaseInactiveMissionIdentityLocked(seed.Label))
            {
                MarkUnavailableLocked(
                    generation,
                    "invalid-restarted-mission-identity",
                    _snapshot.TrackingBucketCount,
                    _snapshot.TrackingBufferCount,
                    changedAtUtc);
                return false;
            }
            if (_labelsByIdentity.ContainsKey(seed.Identity))
            {
                MarkUnavailableLocked(
                    generation,
                    "mission-identity-release-incomplete",
                    _snapshot.TrackingBucketCount,
                    _snapshot.TrackingBufferCount,
                    changedAtUtc);
                return false;
            }

            var definition = definitionRead.Success ? definitionRead.Definition : null;
            var validationError = DefinitionValidationError(
                seed.Label,
                seed.FinishStates.Count,
                definition,
                definitionRead.Failure,
                stateVerified);
            var freshness = ResolveFreshness(
                stateVerified,
                seed.FinishStates,
                definition,
                validationError);
            _labelsByIdentity.Add(seed.Identity, seed.Label);
            AddOrReplaceMissionLocked(new TrackedMissionState(
                seed.Label,
                SourcePartition: "runtime",
                SourceIsCore: false,
                SourceBucket: 0,
                MergedBucket: 0,
                SourceOrdinal: checked((int)_snapshot.StartCommitCount),
                SavedFinishStateCount: 0,
                SavedTrueFinishStateCount: 0,
                ConditionDataCount: 0,
                Identity: seed.Identity,
                Active: true,
                CurrentFinishStates: stateVerified
                    ? seed.FinishStates.ToArray()
                    : null,
                Freshness: freshness,
                Definition: definition,
                DefinitionStatus: definitionRead.Success
                    ? "available"
                    : definitionRead.Failure,
                ValidationError: validationError,
                Presentation: InitialPresentation(definition),
                PresentationDaySceneGeneration: 0,
                PresentationMappedCapturedAtUtc: DateTime.MinValue,
                PresentationAttemptCount: 0,
                PresentationNextAttemptAtUtc: DateTime.MinValue));
            PublishCountsLocked(
                _snapshot with
                {
                    StartCommitCount = checked(_snapshot.StartCommitCount + 1),
                    StateRefreshCount = stateVerified
                        ? checked(_snapshot.StateRefreshCount + 1)
                        : _snapshot.StateRefreshCount,
                    LastEvent = "start-committed",
                    LastError = "",
                },
                changedAtUtc);
            return true;
        }
    }

    public bool TryObserveStateRefresh(
        long generation,
        int threadId,
        RuntimeMissionDiagnosticTrackedSeed seed,
        DateTime changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(seed);
        lock (_syncRoot)
        {
            if (!CanObserve(generation, threadId)) return false;
            if (!IsValidTrackedSeed(seed)
                || !TryResolveCallbackMissionLocked(seed, requireActive: true, out var mission))
            {
                MarkUnavailableLocked(
                    generation,
                    "invalid-state-refresh-identity",
                    _snapshot.TrackingBucketCount,
                    _snapshot.TrackingBufferCount,
                    changedAtUtc);
                return false;
            }

            var validationError = DefinitionValidationError(
                mission.Label,
                seed.FinishStates.Count,
                mission.Definition,
                mission.DefinitionStatus == "available" ? "" : mission.DefinitionStatus,
                stateVerified: true);
            var freshness = ResolveFreshness(
                stateVerified: true,
                seed.FinishStates,
                mission.Definition,
                validationError);
            ReplaceMissionLocked(mission, mission with
            {
                Identity = seed.Identity,
                CurrentFinishStates = seed.FinishStates.ToArray(),
                Freshness = freshness,
                ValidationError = validationError,
            });
            PublishCountsLocked(
                _snapshot with
                {
                    StateRefreshCount = checked(_snapshot.StateRefreshCount + 1),
                    LastEvent = validationError.Length == 0
                        ? "state-refresh-verified"
                        : "state-refresh-unverified",
                    LastError = validationError,
                },
                changedAtUtc,
                preserveLastError: validationError.Length > 0);
            return validationError.Length == 0;
        }
    }

    public bool TryObserveRemoval(
        long generation,
        int threadId,
        RuntimeMissionDiagnosticTrackedSeed seed,
        DateTime changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(seed);
        lock (_syncRoot)
        {
            if (!CanObserve(generation, threadId)) return false;
            if (!IsValidTrackedSeed(seed)
                || !TryResolveCallbackMissionLocked(seed, requireActive: true, out var mission))
            {
                MarkUnavailableLocked(
                    generation,
                    "unknown-mission-removal",
                    _snapshot.TrackingBucketCount,
                    _snapshot.TrackingBufferCount,
                    changedAtUtc);
                return false;
            }

            ReplaceMissionLocked(mission, mission with
            {
                Identity = seed.Identity,
                Active = false,
            });
            PublishCountsLocked(
                _snapshot with
                {
                    RemoveCount = checked(_snapshot.RemoveCount + 1),
                    LastEvent = "remove-observed",
                    LastError = "",
                },
                changedAtUtc);
            return true;
        }
    }

    public bool TryObserveFinish(
        long generation,
        int threadId,
        RuntimeMissionDiagnosticTrackedSeed seed,
        DateTime changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(seed);
        lock (_syncRoot)
        {
            if (!CanObserve(generation, threadId)) return false;
            if (!IsValidTrackedSeed(seed)
                || !TryResolveCallbackMissionLocked(seed, requireActive: false, out var mission))
            {
                MarkUnavailableLocked(
                    generation,
                    "unknown-mission-finish",
                    _snapshot.TrackingBucketCount,
                    _snapshot.TrackingBufferCount,
                    changedAtUtc);
                return false;
            }

            ReplaceMissionLocked(mission, mission with
            {
                Identity = seed.Identity,
                Active = false,
            });
            _finishedMissionLabels.Add(mission.Label);
            PublishCountsLocked(
                _snapshot with
                {
                    FinishCount = checked(_snapshot.FinishCount + 1),
                    LastEvent = "finish-observed",
                    LastError = "",
                },
                changedAtUtc);
            return true;
        }
    }

    public bool TryObserveFinishedLabel(
        long generation,
        int threadId,
        string? label,
        DateTime changedAtUtc)
    {
        lock (_syncRoot)
        {
            if (!CanObserve(generation, threadId)) return false;
            if (string.IsNullOrWhiteSpace(label))
            {
                MarkUnavailableLocked(
                    generation,
                    "invalid-finished-mission-label",
                    _snapshot.TrackingBucketCount,
                    _snapshot.TrackingBufferCount,
                    changedAtUtc);
                return false;
            }

            _finishedMissionLabels.Add(label);
            DeactivateByLabelLocked(label);
            PublishCountsLocked(
                _snapshot with
                {
                    LastEvent = "finished-label-observed",
                    LastError = "",
                },
                changedAtUtc);
            return true;
        }
    }

    public bool TryObserveFinishNode(
        long generation,
        int threadId,
        IReadOnlyList<string> appendedFinishedLabels,
        DateTime changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(appendedFinishedLabels);
        lock (_syncRoot)
        {
            if (!CanObserve(generation, threadId)) return false;
            if (appendedFinishedLabels.Any(string.IsNullOrWhiteSpace))
            {
                MarkUnavailableLocked(
                    generation,
                    "invalid-finish-node-label",
                    _snapshot.TrackingBucketCount,
                    _snapshot.TrackingBufferCount,
                    changedAtUtc);
                return false;
            }

            foreach (var label in appendedFinishedLabels)
            {
                _finishedMissionLabels.Add(label);
                DeactivateByLabelLocked(label);
            }

            PublishCountsLocked(
                _snapshot with
                {
                    FinishNodeObservationCount =
                        checked(_snapshot.FinishNodeObservationCount + 1),
                    LastEvent = "finish-node-observed",
                    LastError = "",
                },
                changedAtUtc);
            return true;
        }
    }

    public bool TryGetServeInWorkDefinitions(
        long generation,
        out IReadOnlyList<RuntimeMissionServeInWorkDefinition> definitions)
    {
        lock (_syncRoot)
        {
            definitions = Array.Empty<RuntimeMissionServeInWorkDefinition>();
            if (_snapshot.Phase != RuntimeMissionDiagnosticPhase.Ready
                || !_snapshot.RuntimeAvailable
                || generation != _snapshot.Generation
                || _snapshot.DefinitionFailureCount != 0)
            {
                return false;
            }

            var serveInWorkMissions = _missionsByLabel.Values
                .Where(mission => mission.Active
                    && mission.Definition != null
                    && mission.Definition.ServeInWorkFoodIds.Count > 0)
                .ToArray();
            if (serveInWorkMissions.Any(mission =>
                    !mission.Definition!.HasReceiver
                    || string.IsNullOrWhiteSpace(mission.Definition.Receiver)))
            {
                return false;
            }

            definitions = serveInWorkMissions
                .Select(mission => new RuntimeMissionServeInWorkDefinition(
                    mission.Label,
                    mission.Definition!.Receiver,
                    mission.Definition.ServeInWorkFoodIds.ToArray(),
                    mission.Freshness))
                .OrderBy(definition => definition.Label, StringComparer.Ordinal)
                .ToArray();
            return true;
        }
    }

    public bool TryReadPresentationRequests(
        long generation,
        long daySceneGeneration,
        DateTime mappedCapturedAtUtc,
        DateTime nowUtc,
        out IReadOnlyList<RuntimeMissionPresentationRequest> requests)
    {
        lock (_syncRoot)
        {
            requests = Array.Empty<RuntimeMissionPresentationRequest>();
            if (_snapshot.Phase != RuntimeMissionDiagnosticPhase.Ready
                || !_snapshot.RuntimeAvailable
                || generation != _snapshot.Generation
                || daySceneGeneration < 1
                || mappedCapturedAtUtc == DateTime.MinValue
                || Environment.CurrentManagedThreadId != _snapshot.OwnerThreadId)
            {
                return false;
            }

            requests = _missionsByLabel.Values
                .Where(mission =>
                    mission.Active
                    && mission.Definition is { HasReceiver: true }
                    && !string.IsNullOrWhiteSpace(mission.Definition.Receiver)
                    && (mission.PresentationDaySceneGeneration
                            != daySceneGeneration
                        || mission.PresentationMappedCapturedAtUtc
                            != mappedCapturedAtUtc
                        || (!string.Equals(
                                mission.Presentation.PresentationStatus,
                                RuntimeMissionPresentation.ReadyStatus,
                                StringComparison.Ordinal)
                            && mission.PresentationAttemptCount
                                < RuntimeMissionPresentation.MaxAttemptCount
                            && nowUtc >= mission.PresentationNextAttemptAtUtc)))
                .OrderBy(mission => mission.Label, StringComparer.Ordinal)
                .Select(mission => new RuntimeMissionPresentationRequest(
                    mission.Label,
                    mission.Definition!.Receiver))
                .ToArray();
            return true;
        }
    }

    public bool TryApplyPresentations(
        long generation,
        long daySceneGeneration,
        DateTime mappedCapturedAtUtc,
        IReadOnlyList<RuntimeMissionPresentationApply> results,
        DateTime changedAtUtc,
        out int readyCount)
    {
        ArgumentNullException.ThrowIfNull(results);
        readyCount = 0;
        lock (_syncRoot)
        {
            if (_snapshot.Phase != RuntimeMissionDiagnosticPhase.Ready
                || !_snapshot.RuntimeAvailable
                || generation != _snapshot.Generation
                || daySceneGeneration < 1
                || mappedCapturedAtUtc == DateTime.MinValue
                || Environment.CurrentManagedThreadId != _snapshot.OwnerThreadId)
            {
                return false;
            }

            var requestedMissions =
                new (TrackedMissionState Mission,
                    RuntimeMissionPresentation Presentation)[results.Count];
            var requestedLabels = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < results.Count; index++)
            {
                var result = results[index];
                if (result == null
                    || string.IsNullOrWhiteSpace(result.Label)
                    || !requestedLabels.Add(result.Label)
                    || !IsValidPresentation(
                        result.ReceiverLabel,
                        result.Presentation)
                    || !_missionsByLabel.TryGetValue(
                        result.Label,
                        out var mission)
                    || !mission.Active
                    || mission.Definition is not { HasReceiver: true }
                    || !string.Equals(
                        mission.Definition.Receiver,
                        result.ReceiverLabel,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                requestedMissions[index] = (mission, result.Presentation);
            }

            var changed = false;
            foreach (var requested in requestedMissions)
            {
                var mission = requested.Mission;
                var presentation = requested.Presentation;
                if (string.Equals(
                        presentation.PresentationStatus,
                        RuntimeMissionPresentation.ReadyStatus,
                        StringComparison.Ordinal))
                {
                    readyCount++;
                }

                var sameBinding =
                    mission.PresentationDaySceneGeneration == daySceneGeneration
                    && mission.PresentationMappedCapturedAtUtc
                        == mappedCapturedAtUtc;
                var attemptCount = sameBinding
                    ? checked(mission.PresentationAttemptCount + 1)
                    : 1;
                var ready = string.Equals(
                    presentation.PresentationStatus,
                    RuntimeMissionPresentation.ReadyStatus,
                    StringComparison.Ordinal);
                var nextAttemptAtUtc = !ready
                    && attemptCount <= RuntimeMissionPresentation.MaxRetryCount
                        ? changedAtUtc
                            + RuntimeMissionPresentation.RetryDelayAfterAttempt(
                                attemptCount)
                        : DateTime.MaxValue;
                ReplaceMissionLocked(mission, mission with
                {
                    Presentation = presentation with
                    {
                        SceneNames = presentation.SceneNames.ToArray(),
                    },
                    PresentationDaySceneGeneration = daySceneGeneration,
                    PresentationMappedCapturedAtUtc = mappedCapturedAtUtc,
                    PresentationAttemptCount = attemptCount,
                    PresentationNextAttemptAtUtc = nextAttemptAtUtc,
                });
                changed = true;
            }

            if (changed)
            {
                PublishCountsLocked(
                    _snapshot with
                    {
                        LastEvent = "mission-presentation-refreshed",
                        LastError = "",
                    },
                    changedAtUtc);
            }
            return true;
        }
    }

    private string ValidateInitializationSeed(
        RuntimeMissionDiagnosticInitializationToken token,
        RuntimeMissionDiagnosticInitializationSeed seed)
    {
        if (seed.Generation != token.Generation || seed.ThreadId != token.ThreadId)
        {
            return "initialize-seed-identity-mismatch";
        }
        if (seed.RuntimeTrackingBucketCount < 0
            || seed.SeedTrackingBucketCount < 0
            || seed.RuntimeTrackingBucketCount != seed.SeedTrackingBucketCount)
        {
            return "tracking-bucket-count-mismatch";
        }
        if (seed.TrackingBufferCount != 0) return "tracking-buffer-not-empty";
        if (seed.CurrentDate < 0) return "initialize-current-date-invalid";
        if (seed.DefinitionReadElapsedMilliseconds < 0)
        {
            return "definition-read-duration-invalid";
        }
        if (seed.SelectedDlcPartitions.Any(string.IsNullOrWhiteSpace)
            || seed.SelectedDlcPartitions.Distinct(StringComparer.Ordinal).Count()
                != seed.SelectedDlcPartitions.Count)
        {
            return "selected-dlc-labels-invalid";
        }
        if (seed.TrackedMissions.Count > _snapshot.ParsedTrackingMissionCount)
        {
            return "tracking-mission-count-mismatch";
        }

        var labels = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<nint>();
        foreach (var mission in seed.TrackedMissions)
        {
            if (string.IsNullOrWhiteSpace(mission.Label)
                || string.IsNullOrWhiteSpace(mission.SourcePartition)
                || mission.SourceBucket < -1
                || mission.MergedBucket < -1
                || mission.SourceOrdinal < 0
                || mission.SavedFinishStateCount < 0
                || mission.SavedTrueFinishStateCount < 0
                || mission.SavedTrueFinishStateCount > mission.SavedFinishStateCount
                || mission.ConditionDataCount < 0
                || !IsValidTrackedSeed(mission.RefreshedState)
                || !string.Equals(
                    mission.Label,
                    mission.RefreshedState.Label,
                    StringComparison.Ordinal)
                || !identities.Add(mission.RefreshedState.Identity)
                || !labels.Add(mission.Label))
            {
                return "invalid-loaded-mission-seed";
            }

            if (mission.Definition.Success
                && (mission.Definition.Definition == null
                    || !string.Equals(
                        mission.Label,
                        mission.Definition.Definition.Label,
                        StringComparison.Ordinal)))
            {
                return "mission-definition-identity-mismatch";
            }
        }

        if (!TryBuildLabelMultiset(
                seed.SeedFinishedMissionLabels,
                out var seedFinished)
            || !TryBuildLabelMultiset(
                seed.RuntimeFinishedMissionLabels,
                out var runtimeFinished))
        {
            return "finished-mission-labels-invalid";
        }
        if (seedFinished.Count != runtimeFinished.Count
            || seedFinished.Any(pair =>
                !runtimeFinished.TryGetValue(pair.Key, out var runtimeCount)
                || runtimeCount != pair.Value))
        {
            return "finished-mission-multiset-mismatch";
        }

        return "";
    }

    private bool TryResolveCallbackMissionLocked(
        RuntimeMissionDiagnosticTrackedSeed seed,
        bool requireActive,
        out TrackedMissionState mission)
    {
        mission = null!;
        if (_labelsByIdentity.TryGetValue(seed.Identity, out var boundLabel))
        {
            if (!_missionsByLabel.TryGetValue(boundLabel, out var boundMission)
                || boundMission.Identity != seed.Identity)
            {
                return false;
            }

            if (string.Equals(boundLabel, seed.Label, StringComparison.Ordinal))
            {
                mission = boundMission;
                return !requireActive || mission.Active;
            }

            if (boundMission.Active
                || !_missionsByLabel.TryGetValue(seed.Label, out var replacement)
                || (requireActive && !replacement.Active)
                || replacement.Identity != 0
                || !TryReleaseInactiveMissionIdentityLocked(boundLabel))
            {
                return false;
            }

            _labelsByIdentity.Add(seed.Identity, seed.Label);
            mission = replacement with { Identity = seed.Identity };
            _missionsByLabel[seed.Label] = mission;
            return true;
        }

        if (!_missionsByLabel.TryGetValue(seed.Label, out var unbound)
            || (requireActive && !unbound.Active)
            || unbound.Identity != 0)
        {
            return false;
        }

        _labelsByIdentity.Add(seed.Identity, seed.Label);
        mission = unbound with { Identity = seed.Identity };
        _missionsByLabel[seed.Label] = mission;
        return true;
    }

    private bool TryReleaseInactiveMissionIdentityLocked(string label)
    {
        if (!_missionsByLabel.TryGetValue(label, out var mission)
            || mission.Active)
        {
            return false;
        }
        if (mission.Identity == 0)
        {
            return true;
        }
        if (!_labelsByIdentity.TryGetValue(mission.Identity, out var boundLabel)
            || !string.Equals(boundLabel, label, StringComparison.Ordinal)
            || !_labelsByIdentity.Remove(mission.Identity))
        {
            return false;
        }

        _missionsByLabel[label] = mission with { Identity = 0 };
        return true;
    }

    private static string DefinitionValidationError(
        string label,
        int finishStateCount,
        RuntimeMissionDefinitionDiagnostic? definition,
        string definitionFailure,
        bool stateVerified)
    {
        if (definition == null)
        {
            return string.IsNullOrWhiteSpace(definitionFailure)
                ? "definition-unavailable"
                : definitionFailure;
        }
        if (!string.Equals(label, definition.Label, StringComparison.Ordinal))
        {
            return "definition-label-mismatch";
        }
        if (definition.HasReceiver
            && (string.IsNullOrWhiteSpace(definition.Receiver)
                || definition.Receiver.Length
                    > RuntimeMissionPresentation.MaxReceiverLength))
        {
            return "definition-receiver-invalid";
        }
        if (stateVerified && finishStateCount != definition.ConditionCount)
        {
            return "refreshed-condition-count-mismatch";
        }

        return "";
    }

    private static RuntimeMissionDiagnosticFreshness ResolveFreshness(
        bool stateVerified,
        IReadOnlyList<bool> finishStates,
        RuntimeMissionDefinitionDiagnostic? definition,
        string validationError)
    {
        if (!stateVerified
            || definition == null
            || validationError.Length > 0)
        {
            return RuntimeMissionDiagnosticFreshness.Unverified;
        }

        return finishStates.All(value => value)
            ? RuntimeMissionDiagnosticFreshness.Fulfilled
            : RuntimeMissionDiagnosticFreshness.Tracking;
    }

    private static void ValidateLoadMetrics(RuntimeMissionDiagnosticLoadMetrics metrics)
    {
        if (metrics.JsonLength <= 0
            || metrics.JsonSha256.Length != 64
            || metrics.JsonSha256.Any(character =>
                character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            || metrics.SerializeElapsedMilliseconds < 0
            || metrics.ParseElapsedMilliseconds < 0
            || string.IsNullOrWhiteSpace(metrics.FileVersion)
            || metrics.SavedGameDay < 0
            || metrics.ParsedTrackingMissionCount < 0
            || metrics.ParsedFinishedMissionCount < 0
            || metrics.ParsedDlcPartitionCount < 0)
        {
            throw new ArgumentException("Load metrics are invalid.", nameof(metrics));
        }
    }

    private static bool TryBuildLabelMultiset(
        IReadOnlyList<string> labels,
        out Dictionary<string, int> result)
    {
        result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (string.IsNullOrWhiteSpace(label)) return false;
            result[label] = result.TryGetValue(label, out var count)
                ? checked(count + 1)
                : 1;
        }
        return true;
    }

    private bool IsCurrentLoadCapture(RuntimeMissionDiagnosticLoadToken token)
    {
        return _snapshot.Phase == RuntimeMissionDiagnosticPhase.CapturingLoadSeed
            && _snapshot.Generation == token.Generation
            && _snapshot.OwnerThreadId == token.ThreadId;
    }

    private bool IsCurrentInitialization(RuntimeMissionDiagnosticInitializationToken token)
    {
        return _snapshot.Phase == RuntimeMissionDiagnosticPhase.Initializing
            && _snapshot.Generation == token.Generation
            && _snapshot.OwnerThreadId == token.ThreadId;
    }

    private bool CanObserve(long generation, int threadId)
    {
        if (_snapshot.Phase != RuntimeMissionDiagnosticPhase.Ready
            || !_snapshot.RuntimeAvailable
            || _snapshot.Generation != generation)
        {
            return false;
        }

        if (_snapshot.OwnerThreadId == threadId) return true;
        MarkUnavailableLocked(
            generation,
            "lifecycle-thread-mismatch",
            _snapshot.TrackingBucketCount,
            _snapshot.TrackingBufferCount,
            DateTime.UtcNow);
        return false;
    }

    private RuntimeMissionDiagnosticSnapshot MarkUnavailableLocked(
        long generation,
        string failure,
        int trackingBucketCount,
        int trackingBufferCount,
        DateTime changedAtUtc)
    {
        var current = _snapshot;
        PublishCountsLocked(
            current with
            {
                Phase = RuntimeMissionDiagnosticPhase.Unavailable,
                RuntimeAvailable = false,
                Generation = generation,
                TrackingBucketCount = Math.Max(0, trackingBucketCount),
                TrackingBufferCount = Math.Max(0, trackingBufferCount),
                ChangedAtUtc = changedAtUtc,
                LastEvent = "capture-unavailable",
                LastError = failure,
            },
            changedAtUtc,
            preserveLastError: true);
        return SnapshotLocked();
    }

    private void PublishCountsLocked(
        RuntimeMissionDiagnosticSnapshot baseSnapshot,
        DateTime changedAtUtc,
        bool preserveLastError = false)
    {
        Publish(baseSnapshot with
        {
            ChangeVersion = checked(baseSnapshot.ChangeVersion + 1),
            ActiveMissionCount = _activeMissionCount,
            UnverifiedMissionCount = _unverifiedMissionCount,
            TrackingMissionCount = _trackingMissionCount,
            FulfilledMissionCount = _fulfilledMissionCount,
            FinishedUniqueMissionCount = _finishedMissionLabels.Count,
            DefinitionAvailableCount = _definitionAvailableCount,
            DefinitionFailureCount = _activeMissionCount - _definitionAvailableCount,
            TitleAvailableCount = _titleAvailableCount,
            ServeInWorkMissionCount = _serveInWorkMissionCount,
            ChangedAtUtc = changedAtUtc,
            LastError = preserveLastError ? baseSnapshot.LastError : "",
        });
    }

    private static RuntimeMissionDiagnosticTaskSnapshot ToTaskSnapshot(
        TrackedMissionState mission)
    {
        var finishStates = mission.CurrentFinishStates;
        return new RuntimeMissionDiagnosticTaskSnapshot(
            mission.Label,
            mission.Definition?.Title ?? "",
            mission.Definition?.TitleStatus ?? "unavailable:definition",
            mission.Definition?.HasReceiver ?? false,
            mission.Definition?.Receiver ?? "",
            mission.Presentation.CharacterName,
            mission.Presentation.SceneNames.ToArray(),
            mission.Presentation.PresentationStatus,
            mission.SourcePartition,
            mission.SourceIsCore,
            mission.SourceBucket,
            mission.MergedBucket,
            mission.SourceOrdinal,
            mission.SavedFinishStateCount,
            mission.SavedTrueFinishStateCount,
            mission.ConditionDataCount,
            mission.Active,
            mission.Identity != 0,
            mission.Freshness,
            finishStates?.Length ?? 0,
            finishStates?.Count(value => value) ?? 0,
            mission.Freshness == RuntimeMissionDiagnosticFreshness.Unverified
                ? null
                : mission.Freshness == RuntimeMissionDiagnosticFreshness.Fulfilled,
            mission.Definition?.ConditionCount ?? 0,
            mission.Definition?.Conditions.ToArray()
                ?? Array.Empty<RuntimeMissionDefinitionDiagnosticCondition>(),
            mission.Definition?.ServeInWorkFoodIds.ToArray()
                ?? Array.Empty<int>(),
            mission.DefinitionStatus,
            mission.ValidationError);
    }

    private static bool TryProjectTrackedMission(
        TrackedMissionState mission,
        out RuntimeTrackedMissionSnapshot snapshot)
    {
        snapshot = null!;
        var definition = mission.Definition;
        if (!mission.Active
            || string.IsNullOrWhiteSpace(mission.Label)
            || definition == null
            || !string.Equals(
                mission.DefinitionStatus,
                "available",
                StringComparison.Ordinal)
            || !string.Equals(
                definition.Label,
                mission.Label,
                StringComparison.Ordinal)
            || !string.Equals(
                definition.TitleStatus,
                "available",
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(definition.Title)
            || definition.ConditionCount < 0
            || definition.ConditionCount != definition.Conditions.Count
            || !string.IsNullOrEmpty(mission.ValidationError))
        {
            return false;
        }

        RuntimeTrackedMissionStatus status;
        int? completedConditionCount;
        bool?[] conditionStates;
        switch (mission.Freshness)
        {
            case RuntimeMissionDiagnosticFreshness.Unverified:
                if (mission.CurrentFinishStates != null) return false;
                status = RuntimeTrackedMissionStatus.Unverified;
                completedConditionCount = null;
                conditionStates = new bool?[definition.ConditionCount];
                break;
            case RuntimeMissionDiagnosticFreshness.Tracking:
                if (!TryReadVerifiedConditionStates(
                        mission,
                        definition.ConditionCount,
                        expectedFulfilled: false,
                        out conditionStates,
                        out var trackingCompleted))
                {
                    return false;
                }

                status = RuntimeTrackedMissionStatus.Tracking;
                completedConditionCount = trackingCompleted;
                break;
            case RuntimeMissionDiagnosticFreshness.Fulfilled:
                if (!TryReadVerifiedConditionStates(
                        mission,
                        definition.ConditionCount,
                        expectedFulfilled: true,
                        out conditionStates,
                        out var fulfilledCompleted))
                {
                    return false;
                }

                status = RuntimeTrackedMissionStatus.Fulfilled;
                completedConditionCount = fulfilledCompleted;
                break;
            default:
                return false;
        }

        snapshot = new RuntimeTrackedMissionSnapshot(
            mission.Label,
            definition.Title,
            mission.Presentation.ReceiverLabel,
            mission.Presentation.CharacterName,
            mission.Presentation.SceneNames.ToArray(),
            mission.Presentation.PresentationStatus,
            status,
            definition.ConditionCount,
            completedConditionCount,
            conditionStates);
        return true;
    }

    private static bool TryReadVerifiedConditionStates(
        TrackedMissionState mission,
        int conditionCount,
        bool expectedFulfilled,
        out bool?[] conditionStates,
        out int completedConditionCount)
    {
        conditionStates = Array.Empty<bool?>();
        completedConditionCount = 0;
        var currentStates = mission.CurrentFinishStates;
        if (currentStates == null || currentStates.Length != conditionCount)
        {
            return false;
        }

        completedConditionCount = currentStates.Count(value => value);
        var fulfilled = completedConditionCount == conditionCount;
        if (fulfilled != expectedFulfilled)
        {
            return false;
        }

        conditionStates = currentStates
            .Select(static value => (bool?)value)
            .ToArray();
        return true;
    }

    private static string ResolveTrackedMissionUnavailableStatus(
        RuntimeMissionDiagnosticSnapshot snapshot)
    {
        if (snapshot.Phase == RuntimeMissionDiagnosticPhase.Ready
            && snapshot.RuntimeAvailable)
        {
            return "";
        }

        return snapshot.Phase switch
        {
            RuntimeMissionDiagnosticPhase.Detached =>
                RuntimeTrackedMissionsSnapshot.NotAttachedStatus,
            RuntimeMissionDiagnosticPhase.WaitingForLoad =>
                RuntimeTrackedMissionsSnapshot.WaitingForLoadStatus,
            RuntimeMissionDiagnosticPhase.CapturingLoadSeed
                or RuntimeMissionDiagnosticPhase.LoadSeedReady
                or RuntimeMissionDiagnosticPhase.Initializing =>
                RuntimeTrackedMissionsSnapshot.LoadingStatus,
            _ => RuntimeTrackedMissionsSnapshot.RuntimeUnavailableStatus,
        };
    }

    private static RuntimeTrackedMissionsSnapshot UnavailableTrackedMissions(
        long generation,
        string status)
    {
        return new RuntimeTrackedMissionsSnapshot(
            RuntimeAvailable: false,
            generation,
            status,
            Array.Empty<RuntimeTrackedMissionSnapshot>());
    }

    private void DeactivateByLabelLocked(string label)
    {
        if (_missionsByLabel.TryGetValue(label, out var mission))
        {
            ReplaceMissionLocked(mission, mission with { Active = false });
        }
    }

    private void ClearMissionDataLocked()
    {
        _missionsByLabel.Clear();
        _labelsByIdentity.Clear();
        _finishedMissionLabels.Clear();
        _activeMissionCount = 0;
        _unverifiedMissionCount = 0;
        _trackingMissionCount = 0;
        _fulfilledMissionCount = 0;
        _definitionAvailableCount = 0;
        _titleAvailableCount = 0;
        _serveInWorkMissionCount = 0;
    }

    private void AddMissionLocked(TrackedMissionState mission)
    {
        _missionsByLabel.Add(mission.Label, mission);
        AddActiveContributionLocked(mission);
    }

    private void AddOrReplaceMissionLocked(TrackedMissionState mission)
    {
        if (_missionsByLabel.TryGetValue(mission.Label, out var existing))
        {
            RemoveActiveContributionLocked(existing);
        }

        _missionsByLabel[mission.Label] = mission;
        AddActiveContributionLocked(mission);
    }

    private void ReplaceMissionLocked(
        TrackedMissionState previous,
        TrackedMissionState replacement)
    {
        if (!string.Equals(previous.Label, replacement.Label, StringComparison.Ordinal)
            || !_missionsByLabel.TryGetValue(previous.Label, out var current)
            || !ReferenceEquals(current, previous))
        {
            throw new InvalidOperationException(
                "Mission state replacement does not match the current tracked entry.");
        }

        RemoveActiveContributionLocked(previous);
        _missionsByLabel[replacement.Label] = replacement;
        AddActiveContributionLocked(replacement);
    }

    private void AddActiveContributionLocked(TrackedMissionState mission)
    {
        if (!mission.Active) return;

        _activeMissionCount = checked(_activeMissionCount + 1);
        switch (mission.Freshness)
        {
            case RuntimeMissionDiagnosticFreshness.Unverified:
                _unverifiedMissionCount = checked(_unverifiedMissionCount + 1);
                break;
            case RuntimeMissionDiagnosticFreshness.Tracking:
                _trackingMissionCount = checked(_trackingMissionCount + 1);
                break;
            case RuntimeMissionDiagnosticFreshness.Fulfilled:
                _fulfilledMissionCount = checked(_fulfilledMissionCount + 1);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown mission freshness {mission.Freshness}.");
        }

        if (mission.Definition == null) return;
        _definitionAvailableCount = checked(_definitionAvailableCount + 1);
        if (string.Equals(
                mission.Definition.TitleStatus,
                "available",
                StringComparison.Ordinal))
        {
            _titleAvailableCount = checked(_titleAvailableCount + 1);
        }
        if (mission.Definition.ServeInWorkFoodIds.Count > 0)
        {
            _serveInWorkMissionCount = checked(_serveInWorkMissionCount + 1);
        }
    }

    private void RemoveActiveContributionLocked(TrackedMissionState mission)
    {
        if (!mission.Active) return;

        _activeMissionCount = checked(_activeMissionCount - 1);
        switch (mission.Freshness)
        {
            case RuntimeMissionDiagnosticFreshness.Unverified:
                _unverifiedMissionCount = checked(_unverifiedMissionCount - 1);
                break;
            case RuntimeMissionDiagnosticFreshness.Tracking:
                _trackingMissionCount = checked(_trackingMissionCount - 1);
                break;
            case RuntimeMissionDiagnosticFreshness.Fulfilled:
                _fulfilledMissionCount = checked(_fulfilledMissionCount - 1);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown mission freshness {mission.Freshness}.");
        }

        if (mission.Definition == null) return;
        _definitionAvailableCount = checked(_definitionAvailableCount - 1);
        if (string.Equals(
                mission.Definition.TitleStatus,
                "available",
                StringComparison.Ordinal))
        {
            _titleAvailableCount = checked(_titleAvailableCount - 1);
        }
        if (mission.Definition.ServeInWorkFoodIds.Count > 0)
        {
            _serveInWorkMissionCount = checked(_serveInWorkMissionCount - 1);
        }
    }

    private static bool IsValidTrackedSeed(RuntimeMissionDiagnosticTrackedSeed seed)
    {
        return seed.Identity != 0
            && !string.IsNullOrWhiteSpace(seed.Label)
            && seed.FinishStates != null;
    }

    private static RuntimeMissionPresentation InitialPresentation(
        RuntimeMissionDefinitionDiagnostic? definition)
    {
        return definition is { HasReceiver: true }
            ? RuntimeMissionPresentation.Pending(definition.Receiver)
            : RuntimeMissionPresentation.NoReceiver;
    }

    private static bool IsValidPresentation(
        string receiver,
        RuntimeMissionPresentation? presentation)
    {
        return !string.IsNullOrWhiteSpace(receiver)
            && receiver.Length <= RuntimeMissionPresentation.MaxReceiverLength
            && RuntimeMissionPresentation.IsValid(presentation)
            && string.Equals(
                receiver,
                presentation!.ReceiverLabel,
                StringComparison.Ordinal);
    }

    private RuntimeMissionDiagnosticSnapshot SnapshotLocked()
    {
        return _snapshot with { };
    }

    private void Publish(RuntimeMissionDiagnosticSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
    }

    private sealed record TrackedMissionState(
        string Label,
        string SourcePartition,
        bool SourceIsCore,
        int SourceBucket,
        int MergedBucket,
        int SourceOrdinal,
        int SavedFinishStateCount,
        int SavedTrueFinishStateCount,
        int ConditionDataCount,
        nint Identity,
        bool Active,
        bool[]? CurrentFinishStates,
        RuntimeMissionDiagnosticFreshness Freshness,
        RuntimeMissionDefinitionDiagnostic? Definition,
        string DefinitionStatus,
        string ValidationError,
        RuntimeMissionPresentation Presentation,
        long PresentationDaySceneGeneration,
        DateTime PresentationMappedCapturedAtUtc,
        int PresentationAttemptCount,
        DateTime PresentationNextAttemptAtUtc);
}
