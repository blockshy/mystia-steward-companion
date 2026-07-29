namespace MystiaStewardCompanion.Save;

internal enum RuntimeServeInWorkMissionDefinitionStatus
{
    Pending,
    Matched,
    Mismatch,
}

internal sealed record RuntimeServeInWorkMissionSignal(
    int RawGuestId,
    int CanonicalGuestId,
    int FoodId,
    long MissionGeneration,
    long BusinessGeneration,
    DateTime ObservedAtUtc);

internal readonly record struct RuntimeServeInWorkMissionSignalKey(
    int CanonicalGuestId,
    int FoodId);

internal sealed record RuntimeServeInWorkMissionDiagnosticEvent(
    long Sequence,
    string Signature,
    string Code,
    long MissionGeneration,
    long BusinessGeneration,
    string NightPhase,
    int RawGuestId,
    int? CanonicalGuestId,
    int FoodId,
    bool? Result,
    RuntimeServeInWorkMissionDefinitionStatus? DefinitionStatus,
    int? ExpectedFoodId,
    DateTime ObservedAtUtc);

internal sealed record RuntimeServeInWorkMissionDiagnosticSnapshot(
    string HookStatus,
    bool HookAttached,
    long MissionGeneration,
    long BusinessGeneration,
    string NightPhase,
    long ChangeVersion,
    long CallCount,
    long TrueResultCount,
    long FalseResultCount,
    long NativeExceptionCount,
    long IdentityMissingCount,
    long DefinitionPendingCount,
    long DefinitionMismatchCount,
    long InvalidFoodCount,
    long RejectedObservationCount,
    long LateObservationCount,
    DateTime ChangedAtUtc,
    string LastEvent,
    string LastError,
    IReadOnlyList<RuntimeServeInWorkMissionSignal> Signals,
    IReadOnlyList<RuntimeServeInWorkMissionDiagnosticEvent> Events)
{
    public static RuntimeServeInWorkMissionDiagnosticSnapshot Detached { get; } = new(
        HookStatus: "not attached",
        HookAttached: false,
        MissionGeneration: 0,
        BusinessGeneration: 0,
        NightPhase: "Inactive",
        ChangeVersion: 0,
        CallCount: 0,
        TrueResultCount: 0,
        FalseResultCount: 0,
        NativeExceptionCount: 0,
        IdentityMissingCount: 0,
        DefinitionPendingCount: 0,
        DefinitionMismatchCount: 0,
        InvalidFoodCount: 0,
        RejectedObservationCount: 0,
        LateObservationCount: 0,
        ChangedAtUtc: DateTime.MinValue,
        LastEvent: "detached",
        LastError: "",
        Signals: Array.Empty<RuntimeServeInWorkMissionSignal>(),
        Events: Array.Empty<RuntimeServeInWorkMissionDiagnosticEvent>());
}

/// <summary>
/// Pure managed observation state for calls the game naturally makes to its ServeInWork helper.
/// </summary>
internal sealed class RuntimeServeInWorkMissionDiagnosticState
{
    private const int MaxEvents = 64;
    private const string ActivePhase = "Active";
    private const string ClosingPhase = "Closing";
    private const string DestroyedPhase = "Destroyed";

    private readonly object _syncRoot = new();
    private readonly Dictionary<int, RuntimeServeInWorkMissionSignal> _signals = new();
    private readonly List<RuntimeServeInWorkMissionDiagnosticEvent> _events = new();
    private readonly HashSet<string> _eventSignatures = new(StringComparer.Ordinal);

    private RuntimeServeInWorkMissionDiagnosticSnapshot _snapshot =
        RuntimeServeInWorkMissionDiagnosticSnapshot.Detached;
    private long _eventSequence;

    public RuntimeServeInWorkMissionDiagnosticSnapshot Snapshot()
    {
        lock (_syncRoot)
        {
            return _snapshot with
            {
                Signals = _signals.Values
                    .OrderBy(signal => signal.CanonicalGuestId)
                    .ToArray(),
                Events = _events.ToArray(),
            };
        }
    }

    public void SetHookStatus(
        string hookStatus,
        bool attached,
        DateTime changedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(hookStatus))
        {
            throw new ArgumentException("Hook status is required.", nameof(hookStatus));
        }

        lock (_syncRoot)
        {
            if (!attached)
            {
                _signals.Clear();
            }

            var current = _snapshot;
            Publish(current with
            {
                HookStatus = hookStatus.Trim(),
                HookAttached = attached,
                ChangeVersion = checked(current.ChangeVersion + 1),
                ChangedAtUtc = changedAtUtc,
                LastEvent = attached ? "hooks-attached" : "hooks-unavailable",
                LastError = attached ? "" : "hook-installation-incomplete",
            });
            RecordEventLocked(
                attached ? "hooks-attached" : "hooks-unavailable",
                rawGuestId: -1,
                canonicalGuestId: null,
                foodId: -1,
                result: null,
                definitionStatus: null,
                expectedFoodId: null,
                changedAtUtc);
        }
    }

    public bool ResetForMissionGeneration(
        long missionGeneration,
        DateTime changedAtUtc)
    {
        if (missionGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(missionGeneration),
                "Mission generation must be positive.");
        }

        lock (_syncRoot)
        {
            var current = _snapshot;
            if (missionGeneration < current.MissionGeneration)
            {
                RejectLocked(
                    "mission-generation-reset-stale",
                    late: true,
                    rawGuestId: -1,
                    canonicalGuestId: null,
                    foodId: -1,
                    result: null,
                    definitionStatus: null,
                    expectedFoodId: null,
                    changedAtUtc);
                return false;
            }

            if (missionGeneration == current.MissionGeneration)
            {
                return true;
            }

            _signals.Clear();
            Publish(current with
            {
                MissionGeneration = missionGeneration,
                ChangeVersion = checked(current.ChangeVersion + 1),
                ChangedAtUtc = changedAtUtc,
                LastEvent = "mission-generation-reset",
                LastError = "",
            });
            RecordEventLocked(
                "mission-generation-reset",
                rawGuestId: -1,
                canonicalGuestId: null,
                foodId: -1,
                result: null,
                definitionStatus: null,
                expectedFoodId: null,
                changedAtUtc);
            return true;
        }
    }

    public bool ApplyBusinessBoundary(
        long businessGeneration,
        string phase,
        DateTime changedAtUtc)
    {
        if (businessGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(businessGeneration));
        }

        var normalizedPhase = NormalizePhase(phase);
        lock (_syncRoot)
        {
            var current = _snapshot;
            if (businessGeneration < current.BusinessGeneration)
            {
                RejectLocked(
                    "business-boundary-stale",
                    late: true,
                    rawGuestId: -1,
                    canonicalGuestId: null,
                    foodId: -1,
                    result: null,
                    definitionStatus: null,
                    expectedFoodId: null,
                    changedAtUtc);
                return false;
            }

            if (businessGeneration == current.BusinessGeneration
                && IsPhaseRegression(current.NightPhase, normalizedPhase))
            {
                RejectLocked(
                    "business-phase-regression",
                    late: true,
                    rawGuestId: -1,
                    canonicalGuestId: null,
                    foodId: -1,
                    result: null,
                    definitionStatus: null,
                    expectedFoodId: null,
                    changedAtUtc);
                return false;
            }

            if (businessGeneration != current.BusinessGeneration
                || !string.Equals(current.NightPhase, normalizedPhase, StringComparison.Ordinal))
            {
                _signals.Clear();
            }

            Publish(current with
            {
                BusinessGeneration = businessGeneration,
                NightPhase = normalizedPhase,
                ChangeVersion = checked(current.ChangeVersion + 1),
                ChangedAtUtc = changedAtUtc,
                LastEvent = "business-boundary",
                LastError = "",
            });
            RecordEventLocked(
                $"business-boundary:{normalizedPhase.ToLowerInvariant()}",
                rawGuestId: -1,
                canonicalGuestId: null,
                foodId: -1,
                result: null,
                definitionStatus: null,
                expectedFoodId: null,
                changedAtUtc);
            return true;
        }
    }

    public bool ObserveResult(
        long expectedMissionGeneration,
        long expectedBusinessGeneration,
        int rawGuestId,
        int? canonicalGuestId,
        int foodId,
        bool result,
        RuntimeServeInWorkMissionDefinitionStatus definitionStatus,
        int? expectedFoodId,
        DateTime observedAtUtc)
    {
        lock (_syncRoot)
        {
            if (!CanObserveLocked(
                    expectedMissionGeneration,
                    expectedBusinessGeneration,
                    "result",
                    rawGuestId,
                    canonicalGuestId,
                    foodId,
                    result,
                    definitionStatus,
                    expectedFoodId,
                    observedAtUtc))
            {
                return false;
            }

            var current = _snapshot;
            Publish(current with
            {
                CallCount = checked(current.CallCount + 1),
                TrueResultCount = result
                    ? checked(current.TrueResultCount + 1)
                    : current.TrueResultCount,
                FalseResultCount = result
                    ? current.FalseResultCount
                    : checked(current.FalseResultCount + 1),
                ChangeVersion = checked(current.ChangeVersion + 1),
                ChangedAtUtc = observedAtUtc,
                LastEvent = result ? "result-true" : "result-false",
                LastError = "",
            });

            if (!result)
            {
                if (!HasCanonicalIdentity(canonicalGuestId))
                {
                    IncrementIdentityMissingLocked(observedAtUtc);
                }

                ClearSignalLocked(rawGuestId, canonicalGuestId);
                RecordEventLocked(
                    "result-false",
                    rawGuestId,
                    canonicalGuestId,
                    foodId,
                    result,
                    definitionStatus,
                    expectedFoodId,
                    observedAtUtc);
                return true;
            }

            if (!HasCanonicalIdentity(canonicalGuestId))
            {
                IncrementIdentityMissingLocked(observedAtUtc);
                ClearSignalLocked(rawGuestId, canonicalGuestId);
                RecordEventLocked(
                    "identity-missing",
                    rawGuestId,
                    canonicalGuestId,
                    foodId,
                    result,
                    definitionStatus,
                    expectedFoodId,
                    observedAtUtc);
                return false;
            }

            if (foodId < 0)
            {
                IncrementInvalidFoodLocked(observedAtUtc);
                ClearSignalLocked(rawGuestId, canonicalGuestId);
                RecordEventLocked(
                    "food-id-invalid",
                    rawGuestId,
                    canonicalGuestId,
                    foodId,
                    result,
                    definitionStatus,
                    expectedFoodId,
                    observedAtUtc);
                return false;
            }

            if (definitionStatus == RuntimeServeInWorkMissionDefinitionStatus.Pending)
            {
                IncrementDefinitionPendingLocked(observedAtUtc);
                ClearSignalLocked(rawGuestId, canonicalGuestId);
                RecordEventLocked(
                    "definition-pending",
                    rawGuestId,
                    canonicalGuestId,
                    foodId,
                    result,
                    definitionStatus,
                    expectedFoodId,
                    observedAtUtc);
                return false;
            }

            if (definitionStatus == RuntimeServeInWorkMissionDefinitionStatus.Mismatch
                || expectedFoodId is null
                || expectedFoodId.Value < 0
                || expectedFoodId.Value != foodId)
            {
                IncrementDefinitionMismatchLocked(observedAtUtc);
                ClearSignalLocked(rawGuestId, canonicalGuestId);
                RecordEventLocked(
                    "definition-mismatch",
                    rawGuestId,
                    canonicalGuestId,
                    foodId,
                    result,
                    definitionStatus,
                    expectedFoodId,
                    observedAtUtc);
                return false;
            }

            var signal = new RuntimeServeInWorkMissionSignal(
                rawGuestId,
                canonicalGuestId.GetValueOrDefault(),
                foodId,
                expectedMissionGeneration,
                expectedBusinessGeneration,
                observedAtUtc);
            _signals[canonicalGuestId.GetValueOrDefault()] = signal;
            Publish(_snapshot with
            {
                ChangeVersion = checked(_snapshot.ChangeVersion + 1),
                ChangedAtUtc = observedAtUtc,
                LastEvent = "signal-committed",
            });
            RecordEventLocked(
                "signal-committed",
                rawGuestId,
                canonicalGuestId,
                foodId,
                result,
                definitionStatus,
                expectedFoodId,
                observedAtUtc);
            return true;
        }
    }

    public bool ObserveNativeException(
        long expectedMissionGeneration,
        long expectedBusinessGeneration,
        int rawGuestId,
        int? canonicalGuestId,
        string exceptionType,
        DateTime observedAtUtc)
    {
        var normalizedException = string.IsNullOrWhiteSpace(exceptionType)
            ? "unknown"
            : exceptionType.Trim();

        lock (_syncRoot)
        {
            if (!CanObserveLocked(
                    expectedMissionGeneration,
                    expectedBusinessGeneration,
                    "native-exception",
                    rawGuestId,
                    canonicalGuestId,
                    foodId: -1,
                    result: null,
                    definitionStatus: null,
                    expectedFoodId: null,
                    observedAtUtc))
            {
                return false;
            }

            ClearSignalLocked(rawGuestId, canonicalGuestId);
            var current = _snapshot;
            Publish(current with
            {
                CallCount = checked(current.CallCount + 1),
                NativeExceptionCount = checked(current.NativeExceptionCount + 1),
                ChangeVersion = checked(current.ChangeVersion + 1),
                ChangedAtUtc = observedAtUtc,
                LastEvent = "native-exception",
                LastError = normalizedException,
            });
            RecordEventLocked(
                $"native-exception:{normalizedException}",
                rawGuestId,
                canonicalGuestId,
                foodId: -1,
                result: null,
                definitionStatus: null,
                expectedFoodId: null,
                observedAtUtc);
            return true;
        }
    }

    public bool ClearForMissionIdentity(
        long expectedMissionGeneration,
        int rawGuestId,
        int? canonicalGuestId,
        DateTime changedAtUtc)
    {
        lock (_syncRoot)
        {
            if (expectedMissionGeneration != _snapshot.MissionGeneration)
            {
                RejectLocked(
                    "mission-identity-clear-stale",
                    late: true,
                    rawGuestId,
                    canonicalGuestId,
                    foodId: -1,
                    result: null,
                    definitionStatus: null,
                    expectedFoodId: null,
                    changedAtUtc);
                return false;
            }

            var removed = ClearSignalLocked(rawGuestId, canonicalGuestId);
            Publish(_snapshot with
            {
                ChangeVersion = checked(_snapshot.ChangeVersion + 1),
                ChangedAtUtc = changedAtUtc,
                LastEvent = "mission-identity-cleared",
                LastError = "",
            });
            RecordEventLocked(
                "mission-identity-cleared",
                rawGuestId,
                canonicalGuestId,
                foodId: -1,
                result: null,
                definitionStatus: null,
                expectedFoodId: null,
                changedAtUtc);
            return removed;
        }
    }

    public bool ReconcileForMissionLifecycle(
        long expectedMissionGeneration,
        IReadOnlyCollection<RuntimeServeInWorkMissionSignalKey> activeSignals,
        DateTime changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(activeSignals);
        if (activeSignals.Any(signal =>
                signal.CanonicalGuestId < 0
                || signal.FoodId < 0))
        {
            throw new ArgumentException(
                "Active ServeInWork signal identities must be non-negative.",
                nameof(activeSignals));
        }

        var activeSignalSet = activeSignals.ToHashSet();
        lock (_syncRoot)
        {
            if (expectedMissionGeneration != _snapshot.MissionGeneration)
            {
                RejectLocked(
                    "mission-lifecycle-reconcile-stale",
                    late: true,
                    rawGuestId: -1,
                    canonicalGuestId: null,
                    foodId: -1,
                    result: null,
                    definitionStatus: null,
                    expectedFoodId: null,
                    changedAtUtc);
                return false;
            }

            var removedCanonicalIds = _signals
                .Where(pair => !activeSignalSet.Contains(
                    new RuntimeServeInWorkMissionSignalKey(
                        pair.Value.CanonicalGuestId,
                        pair.Value.FoodId)))
                .Select(pair => pair.Key)
                .ToArray();
            if (removedCanonicalIds.Length == 0)
            {
                return true;
            }

            foreach (var canonicalGuestId in removedCanonicalIds)
            {
                _signals.Remove(canonicalGuestId);
            }

            Publish(_snapshot with
            {
                ChangeVersion = checked(_snapshot.ChangeVersion + 1),
                ChangedAtUtc = changedAtUtc,
                LastEvent = "mission-lifecycle-reconciled",
                LastError = "",
            });
            RecordEventLocked(
                $"mission-lifecycle-reconciled:{removedCanonicalIds.Length}",
                rawGuestId: -1,
                canonicalGuestId: null,
                foodId: -1,
                result: null,
                definitionStatus: null,
                expectedFoodId: null,
                changedAtUtc);
            return true;
        }
    }

    public bool ClearForMissionLifecycle(
        long expectedMissionGeneration,
        DateTime changedAtUtc)
    {
        lock (_syncRoot)
        {
            if (expectedMissionGeneration != _snapshot.MissionGeneration)
            {
                RejectLocked(
                    "mission-lifecycle-clear-stale",
                    late: true,
                    rawGuestId: -1,
                    canonicalGuestId: null,
                    foodId: -1,
                    result: null,
                    definitionStatus: null,
                    expectedFoodId: null,
                    changedAtUtc);
                return false;
            }

            if (_signals.Count == 0)
            {
                return true;
            }

            _signals.Clear();
            Publish(_snapshot with
            {
                ChangeVersion = checked(_snapshot.ChangeVersion + 1),
                ChangedAtUtc = changedAtUtc,
                LastEvent = "mission-lifecycle-cleared",
                LastError = "",
            });
            RecordEventLocked(
                "mission-lifecycle-cleared",
                rawGuestId: -1,
                canonicalGuestId: null,
                foodId: -1,
                result: null,
                definitionStatus: null,
                expectedFoodId: null,
                changedAtUtc);
            return true;
        }
    }

    private bool CanObserveLocked(
        long expectedMissionGeneration,
        long expectedBusinessGeneration,
        string source,
        int rawGuestId,
        int? canonicalGuestId,
        int foodId,
        bool? result,
        RuntimeServeInWorkMissionDefinitionStatus? definitionStatus,
        int? expectedFoodId,
        DateTime observedAtUtc)
    {
        var generationsMatch = expectedMissionGeneration > 0
            && expectedMissionGeneration == _snapshot.MissionGeneration
            && expectedBusinessGeneration >= 0
            && expectedBusinessGeneration == _snapshot.BusinessGeneration;
        if (!generationsMatch)
        {
            RejectLocked(
                $"{source}-stale",
                late: true,
                rawGuestId,
                canonicalGuestId,
                foodId,
                result,
                definitionStatus,
                expectedFoodId,
                observedAtUtc);
            return false;
        }

        if (!_snapshot.HookAttached
            || _snapshot.BusinessGeneration <= 0
            || !string.Equals(_snapshot.NightPhase, ActivePhase, StringComparison.Ordinal))
        {
            RejectLocked(
                $"{source}-inactive",
                late: false,
                rawGuestId,
                canonicalGuestId,
                foodId,
                result,
                definitionStatus,
                expectedFoodId,
                observedAtUtc);
            return false;
        }

        return true;
    }

    private void RejectLocked(
        string code,
        bool late,
        int rawGuestId,
        int? canonicalGuestId,
        int foodId,
        bool? result,
        RuntimeServeInWorkMissionDefinitionStatus? definitionStatus,
        int? expectedFoodId,
        DateTime observedAtUtc)
    {
        var current = _snapshot;
        Publish(current with
        {
            RejectedObservationCount = checked(current.RejectedObservationCount + 1),
            LateObservationCount = late
                ? checked(current.LateObservationCount + 1)
                : current.LateObservationCount,
            ChangeVersion = checked(current.ChangeVersion + 1),
            ChangedAtUtc = observedAtUtc,
            LastEvent = code,
        });
        RecordEventLocked(
            code,
            rawGuestId,
            canonicalGuestId,
            foodId,
            result,
            definitionStatus,
            expectedFoodId,
            observedAtUtc);
    }

    private void IncrementIdentityMissingLocked(DateTime changedAtUtc)
    {
        Publish(_snapshot with
        {
            IdentityMissingCount = checked(_snapshot.IdentityMissingCount + 1),
            ChangeVersion = checked(_snapshot.ChangeVersion + 1),
            ChangedAtUtc = changedAtUtc,
            LastEvent = "identity-missing",
        });
    }

    private void IncrementDefinitionPendingLocked(DateTime changedAtUtc)
    {
        Publish(_snapshot with
        {
            DefinitionPendingCount = checked(_snapshot.DefinitionPendingCount + 1),
            ChangeVersion = checked(_snapshot.ChangeVersion + 1),
            ChangedAtUtc = changedAtUtc,
            LastEvent = "definition-pending",
        });
    }

    private void IncrementDefinitionMismatchLocked(DateTime changedAtUtc)
    {
        Publish(_snapshot with
        {
            DefinitionMismatchCount = checked(_snapshot.DefinitionMismatchCount + 1),
            ChangeVersion = checked(_snapshot.ChangeVersion + 1),
            ChangedAtUtc = changedAtUtc,
            LastEvent = "definition-mismatch",
        });
    }

    private void IncrementInvalidFoodLocked(DateTime changedAtUtc)
    {
        Publish(_snapshot with
        {
            InvalidFoodCount = checked(_snapshot.InvalidFoodCount + 1),
            ChangeVersion = checked(_snapshot.ChangeVersion + 1),
            ChangedAtUtc = changedAtUtc,
            LastEvent = "food-id-invalid",
        });
    }

    private bool ClearSignalLocked(int rawGuestId, int? canonicalGuestId)
    {
        var removed = false;
        if (HasCanonicalIdentity(canonicalGuestId))
        {
            removed = _signals.Remove(canonicalGuestId!.Value);
        }

        var rawMatches = _signals
            .Where(pair => pair.Value.RawGuestId == rawGuestId)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var canonicalId in rawMatches)
        {
            removed |= _signals.Remove(canonicalId);
        }

        return removed;
    }

    private void RecordEventLocked(
        string code,
        int rawGuestId,
        int? canonicalGuestId,
        int foodId,
        bool? result,
        RuntimeServeInWorkMissionDefinitionStatus? definitionStatus,
        int? expectedFoodId,
        DateTime observedAtUtc)
    {
        var signature = string.Join(
            "|",
            code,
            _snapshot.MissionGeneration,
            _snapshot.BusinessGeneration,
            _snapshot.NightPhase,
            rawGuestId,
            canonicalGuestId?.ToString() ?? "missing",
            foodId,
            result?.ToString() ?? "none",
            definitionStatus?.ToString() ?? "none",
            expectedFoodId?.ToString() ?? "missing");
        if (!_eventSignatures.Add(signature))
        {
            return;
        }

        _events.Add(new RuntimeServeInWorkMissionDiagnosticEvent(
            Sequence: checked(++_eventSequence),
            Signature: signature,
            Code: code,
            MissionGeneration: _snapshot.MissionGeneration,
            BusinessGeneration: _snapshot.BusinessGeneration,
            NightPhase: _snapshot.NightPhase,
            RawGuestId: rawGuestId,
            CanonicalGuestId: canonicalGuestId,
            FoodId: foodId,
            Result: result,
            DefinitionStatus: definitionStatus,
            ExpectedFoodId: expectedFoodId,
            ObservedAtUtc: observedAtUtc));

        while (_events.Count > MaxEvents)
        {
            var removed = _events[0];
            _events.RemoveAt(0);
            _eventSignatures.Remove(removed.Signature);
        }
    }

    private void Publish(RuntimeServeInWorkMissionDiagnosticSnapshot snapshot)
    {
        _snapshot = snapshot with
        {
            Signals = Array.Empty<RuntimeServeInWorkMissionSignal>(),
            Events = Array.Empty<RuntimeServeInWorkMissionDiagnosticEvent>(),
        };
    }

    private static bool HasCanonicalIdentity(int? canonicalGuestId)
    {
        return canonicalGuestId is >= 0;
    }

    private static string NormalizePhase(string phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
        {
            return "Unknown";
        }

        var trimmed = phase.Trim();
        if (string.Equals(trimmed, ActivePhase, StringComparison.OrdinalIgnoreCase))
        {
            return ActivePhase;
        }

        if (string.Equals(trimmed, ClosingPhase, StringComparison.OrdinalIgnoreCase))
        {
            return ClosingPhase;
        }

        if (string.Equals(trimmed, DestroyedPhase, StringComparison.OrdinalIgnoreCase))
        {
            return DestroyedPhase;
        }

        if (string.Equals(trimmed, "Inactive", StringComparison.OrdinalIgnoreCase))
        {
            return "Inactive";
        }

        return trimmed;
    }

    private static bool IsPhaseRegression(string currentPhase, string nextPhase)
    {
        return PhaseRank(nextPhase) < PhaseRank(currentPhase);
    }

    private static int PhaseRank(string phase)
    {
        return phase switch
        {
            ActivePhase => 1,
            ClosingPhase => 2,
            DestroyedPhase => 3,
            _ => 0,
        };
    }
}
