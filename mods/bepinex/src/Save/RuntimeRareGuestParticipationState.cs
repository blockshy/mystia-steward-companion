using System.Collections.ObjectModel;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// Public scalar identity for one exact rare-order lifecycle in one night-business generation.
/// </summary>
/// <remarks>
/// The trace id is process-local and is never used alone. Canonical guest id and lifecycle sequence
/// are part of the identity; guest name, desk, requested tags and managed-object hashes are not.
/// </remarks>
internal readonly record struct RuntimeRareGuestParticipationOrderIdentity(
    long BusinessGeneration,
    string TraceId,
    long OrderLifecycleSequence,
    int GuestId);

/// <summary>
/// One authoritative observation of a current rare-order lifecycle. The native binding is optional
/// until capture has resolved the exact order/controller tuple, and may only be enriched, never
/// replaced, for an already observed public identity.
/// </summary>
internal readonly record struct RuntimeRareGuestParticipationOrderObservation(
    RuntimeRareGuestParticipationOrderIdentity Identity,
    RuntimeOrderBindingToken? Binding);

internal sealed record RuntimeRareGuestParticipationOrderSnapshot(
    RuntimeRareGuestParticipationOrderIdentity Identity,
    RuntimeOrderBindingToken? Binding,
    bool Managed,
    bool Participating,
    string ReasonCode,
    int QueuePosition,
    long ObservationSequence);

internal enum RuntimeRareGuestParticipationAction
{
    Pause,
    EnableTail,
    EnableFront,
}

internal enum RuntimeRareGuestParticipationTargetScope
{
    Guest,
    Order,
}

internal readonly record struct RuntimeRareGuestParticipationDecision(
    bool Allowed,
    string State,
    string ReasonCode,
    string Message,
    long BusinessGeneration,
    long Revision,
    RuntimeRareGuestParticipationOrderSnapshot? Order);

/// <summary>
/// Holds the participation-state monitor across one admitted atomic runtime boundary.
/// </summary>
/// <remarks>
/// Participation and managed-roster mutations wait for an already admitted boundary to finish. A
/// denied permit never retains the monitor. An allowed permit is single-use and must be disposed on
/// the same thread that acquired it.
/// </remarks>
internal sealed class RuntimeRareGuestParticipationPermit : IDisposable
{
    private Action? _release;

    internal RuntimeRareGuestParticipationPermit(
        RuntimeRareGuestParticipationDecision decision,
        Action? release)
    {
        Decision = decision;
        _release = release;
    }

    public RuntimeRareGuestParticipationDecision Decision { get; }

    public bool Allowed => Decision.Allowed;

    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>
/// Immutable process-local projection of the current participation queue.
/// </summary>
internal sealed class RuntimeRareGuestParticipationSnapshot
{
    private readonly int[] _managedGuestIds;
    private readonly RuntimeRareGuestParticipationOrderSnapshot[] _orders;
    private readonly ReadOnlyCollection<int> _readOnlyManagedGuestIds;
    private readonly ReadOnlyCollection<RuntimeRareGuestParticipationOrderSnapshot> _readOnlyOrders;

    internal RuntimeRareGuestParticipationSnapshot(
        bool isActive,
        long businessGeneration,
        long revision,
        IEnumerable<int> managedGuestIds,
        IEnumerable<RuntimeRareGuestParticipationOrderSnapshot> orders)
    {
        _managedGuestIds = managedGuestIds.ToArray();
        _orders = orders.ToArray();
        _readOnlyManagedGuestIds = Array.AsReadOnly(_managedGuestIds);
        _readOnlyOrders = Array.AsReadOnly(_orders);
        IsActive = isActive;
        BusinessGeneration = businessGeneration;
        Revision = revision;
    }

    public bool IsActive { get; }

    public long BusinessGeneration { get; }

    public long Revision { get; }

    public IReadOnlyList<int> ManagedGuestIds => _readOnlyManagedGuestIds;

    /// <remarks>
    /// Participating orders are ordered by contiguous queue position. Paused orders follow in
    /// first-observed order. Consumers must not infer participation from list position; use
    /// <see cref="RuntimeRareGuestParticipationOrderSnapshot.Participating"/>.
    /// </remarks>
    public IReadOnlyList<RuntimeRareGuestParticipationOrderSnapshot> Orders => _readOnlyOrders;
}

/// <summary>
/// Structured optimistic-concurrency failure for Local API projection.
/// </summary>
internal sealed class RuntimeRareGuestParticipationConflictException : InvalidOperationException
{
    internal RuntimeRareGuestParticipationConflictException(
        string code,
        string message,
        long businessGeneration,
        long revision)
        : base(message)
    {
        Code = code;
        BusinessGeneration = businessGeneration;
        Revision = revision;
    }

    public string Code { get; }

    public long BusinessGeneration { get; }

    public long Revision { get; }
}

/// <summary>
/// Mod-authoritative, wrapper-free participation state for current rare-order lifecycles.
/// </summary>
/// <remarks>
/// Guests outside the configured managed set participate automatically. Every newly observed exact
/// lifecycle for a managed guest starts paused. Participation is represented by one explicit exact-
/// identity queue; pausing removes a lifecycle, while enabling inserts it at the requested position.
///
/// This class never mutates the game's native order collections. All writes are fenced by exact
/// business generation and participation revision. Runtime side-effect code that has resolved a
/// native binding must use <see cref="AcquireBoundSideEffectPermit"/>; the binding must be the exact
/// binding previously enriched for the same public lifecycle identity.
/// </remarks>
internal static class RuntimeRareGuestParticipationState
{
    internal const int MaximumManagedGuestIds = 512;
    internal const int MaximumCurrentOrders = 512;
    internal const int MaximumObservedLifecyclesPerBusiness = 4096;
    internal const string AutomaticParticipationReason = "guest-not-managed";
    internal const string DefaultPausedReason = "managed-lifecycle-default-paused";
    internal const string AuthorityResetPausedReason = "managed-lifecycle-authority-reset-paused";
    internal const string ManuallyEnabledReason = "managed-lifecycle-manually-enabled";
    internal const string ManuallyPausedReason = "managed-lifecycle-manually-paused";

    private const int MaximumTraceDigits = 16;
    private static readonly object SyncRoot = new();

    private static bool _isActive;
    private static long _businessGeneration;
    private static long _revision;
    private static long _lastObservationSequence;
    private static HashSet<int> _managedGuestIds = new();
    private static Dictionary<RuntimeRareGuestParticipationOrderIdentity, OrderState> _orders = new();
    private static List<RuntimeRareGuestParticipationOrderIdentity> _participationQueue = new();
    private static Dictionary<PublicLifecycleKey, RuntimeRareGuestParticipationOrderIdentity> _knownPublicOwners = new();
    private static Dictionary<RuntimeRareGuestParticipationOrderIdentity, RuntimeOrderBindingToken> _knownBindingsByIdentity = new();
    private static Dictionary<RuntimeOrderBindingToken, RuntimeRareGuestParticipationOrderIdentity> _knownBindingOwners = new();

    public static RuntimeRareGuestParticipationSnapshot Snapshot
    {
        get
        {
            lock (SyncRoot) return BuildSnapshotLocked();
        }
    }

    /// <summary>
    /// Starts a strictly newer night-business generation with no retained order authorization.
    /// </summary>
    public static long BeginBusiness(long businessGeneration, IEnumerable<int> managedGuestIds)
    {
        if (businessGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(businessGeneration));
        var copiedManagedGuestIds = CopyManagedGuestIds(managedGuestIds);

        lock (SyncRoot)
        {
            if (businessGeneration <= _businessGeneration)
            {
                throw Conflict(
                    "business-generation-not-newer",
                    $"Business generation {businessGeneration} is not newer than {_businessGeneration}.");
            }

            _isActive = true;
            _businessGeneration = businessGeneration;
            _revision = 1;
            _lastObservationSequence = 0;
            _managedGuestIds = copiedManagedGuestIds;
            _orders = new Dictionary<RuntimeRareGuestParticipationOrderIdentity, OrderState>();
            _participationQueue = new List<RuntimeRareGuestParticipationOrderIdentity>();
            _knownPublicOwners = new Dictionary<PublicLifecycleKey, RuntimeRareGuestParticipationOrderIdentity>();
            _knownBindingsByIdentity = new Dictionary<RuntimeRareGuestParticipationOrderIdentity, RuntimeOrderBindingToken>();
            _knownBindingOwners = new Dictionary<RuntimeOrderBindingToken, RuntimeRareGuestParticipationOrderIdentity>();
            return _revision;
        }
    }

    /// <summary>
    /// Atomically replaces the managed guest set for the active business.
    /// </summary>
    /// <remarks>
    /// Becoming managed pauses every current lifecycle. Leaving the managed set makes every current
    /// lifecycle participate automatically and appends it to the queue tail. Authority
    /// reset mode advances revision even for an unchanged roster and revokes all current managed
    /// participation grants without reordering already-unmanaged lifecycles.
    /// </remarks>
    public static long ReplaceManagedGuestIds(
        long businessGeneration,
        long expectedRevision,
        IEnumerable<int> managedGuestIds,
        bool resetManagedParticipation = false)
    {
        ValidateExpectedRevision(expectedRevision);
        var nextManagedGuestIds = CopyManagedGuestIds(managedGuestIds);

        lock (SyncRoot)
        {
            RequireCurrent(businessGeneration, expectedRevision);
            return ReplaceManagedGuestIdsLocked(nextManagedGuestIds, resetManagedParticipation);
        }
    }

    /// <summary>
    /// Applies the already-validated primary profile to the current business without an external
    /// optimistic revision. This is reserved for the Mod's serialized authority transition path.
    /// </summary>
    public static long ApplyManagedGuestIdsFromAuthority(
        IEnumerable<int> managedGuestIds,
        bool resetManagedParticipation)
    {
        var nextManagedGuestIds = CopyManagedGuestIds(managedGuestIds);
        lock (SyncRoot)
        {
            if (!_isActive)
            {
                _managedGuestIds = nextManagedGuestIds;
                return _revision;
            }
            return ReplaceManagedGuestIdsLocked(nextManagedGuestIds, resetManagedParticipation);
        }
    }

    private static long ReplaceManagedGuestIdsLocked(
        HashSet<int> nextManagedGuestIds,
        bool resetManagedParticipation)
    {
        if (!resetManagedParticipation && _managedGuestIds.SetEquals(nextManagedGuestIds))
        {
            return _revision;
        }

        var nextRevision = checked(_revision + 1);
        var nextOrders = new Dictionary<RuntimeRareGuestParticipationOrderIdentity, OrderState>(_orders.Count);
        var identitiesToRequeue = new HashSet<RuntimeRareGuestParticipationOrderIdentity>();

        foreach (var state in _orders.Values.OrderBy(value => value.ObservationSequence))
        {
            var wasManaged = _managedGuestIds.Contains(state.Identity.GuestId);
            var isManaged = nextManagedGuestIds.Contains(state.Identity.GuestId);
            if (resetManagedParticipation && isManaged)
            {
                nextOrders.Add(
                    state.Identity,
                    state with
                    {
                        ReasonCode = AuthorityResetPausedReason,
                    });
                continue;
            }
            if (wasManaged == isManaged)
            {
                nextOrders.Add(state.Identity, state);
                continue;
            }

            if (isManaged)
            {
                nextOrders.Add(
                    state.Identity,
                    state with
                    {
                        ReasonCode = DefaultPausedReason,
                    });
                continue;
            }

            identitiesToRequeue.Add(state.Identity);
            nextOrders.Add(
                state.Identity,
                state with
                {
                    ReasonCode = AutomaticParticipationReason,
                });
        }

        var nextQueue = _participationQueue
            .Where(identity =>
            {
                var wasManaged = _managedGuestIds.Contains(identity.GuestId);
                var isManaged = nextManagedGuestIds.Contains(identity.GuestId);
                return !(resetManagedParticipation && isManaged)
                    && wasManaged == isManaged;
            })
            .ToList();
        nextQueue.AddRange(
            nextOrders.Values
                .Where(state => identitiesToRequeue.Contains(state.Identity))
                .OrderBy(state => state.ObservationSequence)
                .Select(state => state.Identity));

        _managedGuestIds = nextManagedGuestIds;
        _orders = nextOrders;
        _participationQueue = nextQueue;
        _revision = nextRevision;
        return _revision;
    }

    /// <summary>
    /// Replaces the complete current rare-order set from one authoritative capture projection.
    /// </summary>
    /// <remarks>
    /// Only a collection explicitly marked complete may retire missing lifecycles. An incomplete or
    /// error collection preserves the previous state without consuming a revision. Re-publishing the
    /// same complete set is idempotent. A binding may be added later, but conflicting public or native
    /// identity is rejected atomically, including after that lifecycle has been retired.
    /// </remarks>
    public static long ReconcileCurrentOrders(
        long businessGeneration,
        long expectedRevision,
        bool collectionComplete,
        IEnumerable<RuntimeRareGuestParticipationOrderObservation>? currentOrders)
    {
        ValidateExpectedRevision(expectedRevision);
        if (!collectionComplete)
        {
            lock (SyncRoot)
            {
                RequireCurrent(businessGeneration, expectedRevision);
                return _revision;
            }
        }

        var observations = CopyAndValidateObservations(
            currentOrders ?? throw new ArgumentNullException(nameof(currentOrders)),
            businessGeneration);

        lock (SyncRoot)
        {
            RequireCurrent(businessGeneration, expectedRevision);
            ValidateIdentityContinuityLocked(observations);
            var newlyObservedCount = observations.Count(observation =>
                !_knownPublicOwners.ContainsKey(ToPublicLifecycleKey(observation.Identity)));
            if (_knownPublicOwners.Count + newlyObservedCount > MaximumObservedLifecyclesPerBusiness)
            {
                throw Conflict(
                    "lifecycle-history-capacity-exceeded",
                    $"Rare-order lifecycle history exceeded {MaximumObservedLifecyclesPerBusiness} entries in one business generation.");
            }

            var nextObservationSequence = _lastObservationSequence;
            var nextOrders = new Dictionary<RuntimeRareGuestParticipationOrderIdentity, OrderState>(observations.Length);
            var currentIdentitySet = observations
                .Select(observation => observation.Identity)
                .ToHashSet();
            var nextQueue = _participationQueue
                .Where(currentIdentitySet.Contains)
                .ToList();
            var nextKnownPublicOwners = new Dictionary<PublicLifecycleKey, RuntimeRareGuestParticipationOrderIdentity>(_knownPublicOwners);
            var nextKnownBindingsByIdentity = new Dictionary<RuntimeRareGuestParticipationOrderIdentity, RuntimeOrderBindingToken>(_knownBindingsByIdentity);
            var nextKnownBindingOwners = new Dictionary<RuntimeOrderBindingToken, RuntimeRareGuestParticipationOrderIdentity>(_knownBindingOwners);

            foreach (var observation in observations)
            {
                nextKnownPublicOwners.TryAdd(ToPublicLifecycleKey(observation.Identity), observation.Identity);
                var binding = observation.Binding;
                if (!binding.HasValue
                    && nextKnownBindingsByIdentity.TryGetValue(observation.Identity, out var knownBinding))
                {
                    binding = knownBinding;
                }
                if (binding.HasValue)
                {
                    nextKnownBindingsByIdentity.TryAdd(observation.Identity, binding.Value);
                    nextKnownBindingOwners.TryAdd(binding.Value, observation.Identity);
                }

                if (_orders.TryGetValue(observation.Identity, out var existing))
                {
                    binding = existing.Binding ?? binding;
                    nextOrders.Add(
                        observation.Identity,
                        binding == existing.Binding
                            ? existing
                            : existing with { Binding = binding });
                    continue;
                }

                nextObservationSequence = checked(nextObservationSequence + 1);
                var participatesAutomatically = !_managedGuestIds.Contains(observation.Identity.GuestId);
                if (participatesAutomatically) nextQueue.Add(observation.Identity);

                nextOrders.Add(
                    observation.Identity,
                    new OrderState(
                        observation.Identity,
                        binding,
                        participatesAutomatically
                            ? AutomaticParticipationReason
                            : DefaultPausedReason,
                        nextObservationSequence));
            }

            if (HaveSameOrders(_orders, nextOrders)
                && _participationQueue.SequenceEqual(nextQueue))
            {
                return _revision;
            }

            var nextRevision = checked(_revision + 1);
            _orders = nextOrders;
            _knownPublicOwners = nextKnownPublicOwners;
            _knownBindingsByIdentity = nextKnownBindingsByIdentity;
            _knownBindingOwners = nextKnownBindingOwners;
            _participationQueue = nextQueue;
            _lastObservationSequence = nextObservationSequence;
            _revision = nextRevision;
            return _revision;
        }
    }

    /// <summary>
    /// Pauses or enables exact current managed lifecycle targets as one atomic queue revision.
    /// </summary>
    /// <remarks>
    /// Guest scope requires the caller to echo the complete current identity set it observed for the
    /// guest. Order scope requires exactly one identity. Enable actions only insert paused targets;
    /// already participating targets retain their existing position. Front insertion leaves the
    /// entire existing queue unchanged and inserts newly enabled targets, in observation order,
    /// immediately after the last strictly validated participating protected identity. With no
    /// protected identities, insertion starts at queue position one.
    /// </remarks>
    public static long MutateParticipation(
        long businessGeneration,
        long expectedRevision,
        RuntimeRareGuestParticipationAction action,
        RuntimeRareGuestParticipationTargetScope targetScope,
        int guestId,
        IEnumerable<RuntimeRareGuestParticipationOrderIdentity> expectedTargetOrders,
        IEnumerable<RuntimeRareGuestParticipationOrderIdentity>? protectedOrders = null)
    {
        ValidateExpectedRevision(expectedRevision);
        if (!Enum.IsDefined(typeof(RuntimeRareGuestParticipationAction), action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }
        if (!Enum.IsDefined(typeof(RuntimeRareGuestParticipationTargetScope), targetScope))
        {
            throw new ArgumentOutOfRangeException(nameof(targetScope));
        }
        if (guestId < 0) throw new ArgumentOutOfRangeException(nameof(guestId));
        var expectedOrders = CopyAndValidateMutationTargets(
            expectedTargetOrders,
            businessGeneration,
            guestId,
            targetScope);
        var protectedIdentities = CopyAndValidateProtectedOrders(
            protectedOrders ?? Array.Empty<RuntimeRareGuestParticipationOrderIdentity>(),
            businessGeneration);
        if (action != RuntimeRareGuestParticipationAction.EnableFront
            && protectedIdentities.Length > 0)
        {
            throw new ArgumentException(
                "Protected rare-order identities are only valid for front insertion.",
                nameof(protectedOrders));
        }

        lock (SyncRoot)
        {
            RequireCurrent(businessGeneration, expectedRevision);
            if (!_managedGuestIds.Contains(guestId))
            {
                throw Conflict(
                    "order-not-managed",
                    $"Rare guest {guestId} participates automatically and cannot be manually paused.");
            }

            OrderState[] targetStates;
            if (targetScope == RuntimeRareGuestParticipationTargetScope.Guest)
            {
                var currentStates = _orders.Values
                    .Where(state => state.Identity.GuestId == guestId)
                    .OrderBy(state => state.ObservationSequence)
                    .ToArray();
                var expectedSet = expectedOrders.ToHashSet();
                if (currentStates.Length != expectedSet.Count
                    || currentStates.Any(state => !expectedSet.Contains(state.Identity)))
                {
                    throw Conflict(
                        "guest-current-orders-mismatch",
                        $"Rare guest {guestId} current lifecycle set changed before the participation mutation.");
                }
                targetStates = currentStates;
            }
            else
            {
                var targetIdentity = expectedOrders[0];
                if (!_orders.TryGetValue(targetIdentity, out var targetState))
                {
                    throw Conflict(
                        "current-order-not-found",
                        $"Rare-order lifecycle {Format(targetIdentity)} is not in the current authoritative set.");
                }
                targetStates = new[] { targetState };
            }

            var protectedSet = protectedIdentities.ToHashSet();
            foreach (var protectedIdentity in protectedIdentities)
            {
                if (!_orders.ContainsKey(protectedIdentity))
                {
                    throw Conflict(
                        "protected-order-not-current",
                        $"Protected rare-order lifecycle {Format(protectedIdentity)} is not current.");
                }
                if (!_participationQueue.Contains(protectedIdentity))
                {
                    throw Conflict(
                        "protected-order-not-participating",
                        $"Protected rare-order lifecycle {Format(protectedIdentity)} is paused.");
                }
            }

            var targetIdentities = targetStates
                .OrderBy(state => state.ObservationSequence)
                .Select(state => state.Identity)
                .ToArray();
            var queuedSet = _participationQueue.ToHashSet();
            Dictionary<RuntimeRareGuestParticipationOrderIdentity, OrderState> nextOrders;
            List<RuntimeRareGuestParticipationOrderIdentity> nextQueue;
            if (action == RuntimeRareGuestParticipationAction.Pause)
            {
                var identitiesToPause = targetIdentities
                    .Where(queuedSet.Contains)
                    .ToHashSet();
                if (identitiesToPause.Count == 0) return _revision;

                nextOrders = new Dictionary<RuntimeRareGuestParticipationOrderIdentity, OrderState>(_orders);
                foreach (var identity in identitiesToPause)
                {
                    nextOrders[identity] = nextOrders[identity] with
                    {
                        ReasonCode = ManuallyPausedReason,
                    };
                }
                nextQueue = _participationQueue
                    .Where(identity => !identitiesToPause.Contains(identity))
                    .ToList();
            }
            else
            {
                var identitiesToEnable = targetIdentities
                    .Where(identity => !queuedSet.Contains(identity))
                    .ToArray();
                if (identitiesToEnable.Length == 0) return _revision;

                nextOrders = new Dictionary<RuntimeRareGuestParticipationOrderIdentity, OrderState>(_orders);
                foreach (var identity in identitiesToEnable)
                {
                    nextOrders[identity] = nextOrders[identity] with
                    {
                        ReasonCode = ManuallyEnabledReason,
                    };
                }

                if (action == RuntimeRareGuestParticipationAction.EnableTail)
                {
                    nextQueue = _participationQueue
                        .Concat(identitiesToEnable)
                        .ToList();
                }
                else
                {
                    var insertionIndex = protectedSet.Count == 0
                        ? 0
                        : _participationQueue
                            .Select((identity, index) => (identity, index))
                            .Where(entry => protectedSet.Contains(entry.identity))
                            .Max(entry => entry.index) + 1;
                    nextQueue = new List<RuntimeRareGuestParticipationOrderIdentity>(
                        checked(_participationQueue.Count + identitiesToEnable.Length));
                    nextQueue.AddRange(_participationQueue.Take(insertionIndex));
                    nextQueue.AddRange(identitiesToEnable);
                    nextQueue.AddRange(_participationQueue.Skip(insertionIndex));
                }
            }

            var nextRevision = checked(_revision + 1);
            _orders = nextOrders;
            _participationQueue = nextQueue;
            _revision = nextRevision;
            return _revision;
        }
    }

    /// <summary>
    /// Acquires a permit for pre-binding admission using the exact public lifecycle identity.
    /// </summary>
    public static RuntimeRareGuestParticipationPermit AcquireAdmissionPermit(
        RuntimeRareGuestParticipationOrderIdentity identity)
    {
        return AcquirePermit(identity, requiredBinding: null, requireBinding: false);
    }

    /// <summary>
    /// Enriches one already-admitted public lifecycle with the exact active native binding resolved
    /// by the order-matching transaction. This internal identity refinement does not change public
    /// queue semantics or consume a participation revision.
    /// </summary>
    public static bool TryEnrichCurrentBinding(
        RuntimeRareGuestParticipationOrderIdentity identity,
        RuntimeOrderBindingToken binding,
        out RuntimeRareGuestParticipationDecision decision)
    {
        lock (SyncRoot)
        {
            decision = EvaluatePermitLocked(identity, binding, requireBinding: false);
            if (!decision.Allowed || !IsValidBinding(identity, binding))
            {
                if (decision.Allowed)
                {
                    decision = DenyPermit(
                        "invalid-order-binding",
                        "Rare-order native binding does not match the admitted public lifecycle identity.");
                }
                return false;
            }

            var state = _orders[identity];
            if ((state.Binding.HasValue && state.Binding.Value != binding)
                || (_knownBindingsByIdentity.TryGetValue(identity, out var knownBinding)
                    && knownBinding != binding)
                || (_knownBindingOwners.TryGetValue(binding, out var bindingOwner)
                    && bindingOwner != identity))
            {
                decision = DenyPermit(
                    "order-binding-mismatch",
                    "Rare-order public lifecycle or native binding is already owned by a different exact identity.");
                return false;
            }

            _knownBindingsByIdentity[identity] = binding;
            _knownBindingOwners[binding] = identity;
            if (!state.Binding.HasValue)
            {
                state = state with { Binding = binding };
                _orders[identity] = state;
            }
            decision = new RuntimeRareGuestParticipationDecision(
                true,
                "participating",
                "participation-allowed",
                "Rare-order lifecycle is admitted by the current participation queue.",
                _businessGeneration,
                _revision,
                ToSnapshotLocked(state));
            return true;
        }
    }

    /// <summary>
    /// Acquires a permit for a native side-effect boundary. Public identity alone cannot authorize
    /// this path: the exact native binding must already be enriched into the same current observation.
    /// </summary>
    /// <remarks>
    /// This verifies participation identity and the retained binding token. The caller must still
    /// verify that token against <c>RuntimeOrderTerminalReceiptStore</c> while holding the permit,
    /// immediately before the native side effect.
    /// </remarks>
    public static RuntimeRareGuestParticipationPermit AcquireBoundSideEffectPermit(
        RuntimeRareGuestParticipationOrderIdentity identity,
        RuntimeOrderBindingToken binding)
    {
        return AcquirePermit(identity, binding, requireBinding: true);
    }

    private static RuntimeRareGuestParticipationPermit AcquirePermit(
        RuntimeRareGuestParticipationOrderIdentity identity,
        RuntimeOrderBindingToken? requiredBinding,
        bool requireBinding)
    {
        Monitor.Enter(SyncRoot);
        try
        {
            var decision = EvaluatePermitLocked(identity, requiredBinding, requireBinding);
            if (!decision.Allowed)
            {
                Monitor.Exit(SyncRoot);
                return new RuntimeRareGuestParticipationPermit(decision, release: null);
            }

            return new RuntimeRareGuestParticipationPermit(
                decision,
                () => Monitor.Exit(SyncRoot));
        }
        catch
        {
            Monitor.Exit(SyncRoot);
            throw;
        }
    }

    private static RuntimeRareGuestParticipationDecision EvaluatePermitLocked(
        RuntimeRareGuestParticipationOrderIdentity identity,
        RuntimeOrderBindingToken? requiredBinding,
        bool requireBinding)
    {
        if (!IsValidIdentity(identity))
        {
            return DenyPermit(
                "invalid-order-identity",
                "Rare-order participation identity is incomplete or malformed.");
        }
        if (requireBinding
            && (!requiredBinding.HasValue || !IsValidBinding(identity, requiredBinding.Value)))
        {
            return DenyPermit(
                "invalid-order-binding",
                "Rare-order native binding does not match the requested public lifecycle identity.");
        }
        if (!_isActive)
        {
            return DenyPermit(
                "business-inactive",
                "Rare-guest participation has no active business generation.");
        }
        if (identity.BusinessGeneration != _businessGeneration)
        {
            return DenyPermit(
                "business-generation-mismatch",
                $"Rare-order generation {identity.BusinessGeneration} does not match current generation {_businessGeneration}.");
        }
        if (!_orders.TryGetValue(identity, out var state))
        {
            return DenyPermit(
                "current-order-not-found",
                $"Rare-order lifecycle {Format(identity)} is not in the current authoritative set.");
        }
        if (!_participationQueue.Contains(identity))
        {
            return DenyPermit(
                "order-paused",
                $"Rare-order lifecycle {Format(identity)} is paused.");
        }

        if (requireBinding)
        {
            if (!state.Binding.HasValue)
            {
                return DenyPermit(
                    "order-binding-unavailable",
                    $"Rare-order lifecycle {Format(identity)} has no enriched native binding.");
            }
            if (state.Binding.Value != requiredBinding.GetValueOrDefault())
            {
                return DenyPermit(
                    "order-binding-mismatch",
                    $"Rare-order lifecycle {Format(identity)} does not own the requested native binding.");
            }
        }

        return new RuntimeRareGuestParticipationDecision(
            true,
            "participating",
            "participation-allowed",
            "Rare-order lifecycle is admitted by the current participation queue.",
            _businessGeneration,
            _revision,
            ToSnapshotLocked(state));
    }

    private static RuntimeRareGuestParticipationDecision DenyPermit(
        string reasonCode,
        string message)
    {
        return new RuntimeRareGuestParticipationDecision(
            false,
            "suspended-participation",
            reasonCode,
            message,
            _businessGeneration,
            _revision,
            Order: null);
    }

    /// <summary>
    /// Idempotently ends the current generation and drops all runtime order authorization.
    /// </summary>
    /// <remarks>
    /// Repeated Closing/Destroyed/controller notifications for an already inactive state are no-ops.
    /// An active different generation is rejected so a stale boundary cannot retire newer authority.
    /// </remarks>
    public static long EndBusinessIfCurrent(long businessGeneration)
    {
        if (businessGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(businessGeneration));
        lock (SyncRoot)
        {
            if (!_isActive) return _revision;
            RequireActiveGeneration(businessGeneration);
            var nextRevision = checked(_revision + 1);
            _isActive = false;
            _managedGuestIds = new HashSet<int>();
            _orders = new Dictionary<RuntimeRareGuestParticipationOrderIdentity, OrderState>();
            _participationQueue = new List<RuntimeRareGuestParticipationOrderIdentity>();
            _knownPublicOwners = new Dictionary<PublicLifecycleKey, RuntimeRareGuestParticipationOrderIdentity>();
            _knownBindingsByIdentity = new Dictionary<RuntimeRareGuestParticipationOrderIdentity, RuntimeOrderBindingToken>();
            _knownBindingOwners = new Dictionary<RuntimeOrderBindingToken, RuntimeRareGuestParticipationOrderIdentity>();
            _lastObservationSequence = 0;
            _revision = nextRevision;
            return _revision;
        }
    }

    /// <summary>
    /// Clears process-local state. Business boundaries must use <see cref="EndBusinessIfCurrent"/> so stale
    /// generations remain fenced; this reset is reserved for process initialization and smoke tests.
    /// </summary>
    internal static void Reset()
    {
        lock (SyncRoot)
        {
            _isActive = false;
            _businessGeneration = 0;
            _revision = 0;
            _lastObservationSequence = 0;
            _managedGuestIds = new HashSet<int>();
            _orders = new Dictionary<RuntimeRareGuestParticipationOrderIdentity, OrderState>();
            _participationQueue = new List<RuntimeRareGuestParticipationOrderIdentity>();
            _knownPublicOwners = new Dictionary<PublicLifecycleKey, RuntimeRareGuestParticipationOrderIdentity>();
            _knownBindingsByIdentity = new Dictionary<RuntimeRareGuestParticipationOrderIdentity, RuntimeOrderBindingToken>();
            _knownBindingOwners = new Dictionary<RuntimeOrderBindingToken, RuntimeRareGuestParticipationOrderIdentity>();
        }
    }

    private static RuntimeRareGuestParticipationSnapshot BuildSnapshotLocked()
    {
        var queuePositions = _participationQueue
            .Select((identity, index) => new { identity, position = index + 1 })
            .ToDictionary(item => item.identity, item => item.position);
        var orders = _orders.Values
            .OrderBy(state => queuePositions.ContainsKey(state.Identity) ? 0 : 1)
            .ThenBy(state => queuePositions.TryGetValue(state.Identity, out var position)
                ? position
                : state.ObservationSequence)
            .Select(state => ToSnapshotLocked(
                state,
                queuePositions.GetValueOrDefault(state.Identity)))
            .ToArray();
        return new RuntimeRareGuestParticipationSnapshot(
            _isActive,
            _businessGeneration,
            _revision,
            _managedGuestIds.OrderBy(value => value),
            orders);
    }

    private static RuntimeRareGuestParticipationOrderSnapshot ToSnapshotLocked(OrderState state)
    {
        var index = _participationQueue.IndexOf(state.Identity);
        return ToSnapshotLocked(state, index < 0 ? 0 : index + 1);
    }

    private static RuntimeRareGuestParticipationOrderSnapshot ToSnapshotLocked(
        OrderState state,
        int queuePosition)
    {
        return new RuntimeRareGuestParticipationOrderSnapshot(
            state.Identity,
            state.Binding,
            _managedGuestIds.Contains(state.Identity.GuestId),
            queuePosition > 0,
            state.ReasonCode,
            queuePosition,
            state.ObservationSequence);
    }

    private static HashSet<int> CopyManagedGuestIds(IEnumerable<int> managedGuestIds)
    {
        ArgumentNullException.ThrowIfNull(managedGuestIds);
        var values = managedGuestIds.ToArray();
        if (values.Length > MaximumManagedGuestIds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(managedGuestIds),
                $"At most {MaximumManagedGuestIds} rare guest ids may be managed.");
        }
        if (values.Any(value => value < 0))
        {
            throw new ArgumentException("Managed rare guest ids must be non-negative.", nameof(managedGuestIds));
        }

        var result = new HashSet<int>(values);
        if (result.Count != values.Length)
        {
            throw new ArgumentException("Managed rare guest ids must be unique.", nameof(managedGuestIds));
        }
        return result;
    }

    private static RuntimeRareGuestParticipationOrderObservation[] CopyAndValidateObservations(
        IEnumerable<RuntimeRareGuestParticipationOrderObservation> currentOrders,
        long businessGeneration)
    {
        ArgumentNullException.ThrowIfNull(currentOrders);
        if (businessGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(businessGeneration));

        var values = currentOrders.ToArray();
        if (values.Length > MaximumCurrentOrders)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentOrders),
                $"At most {MaximumCurrentOrders} current rare-order lifecycles may be published.");
        }

        var identities = new HashSet<RuntimeRareGuestParticipationOrderIdentity>();
        var publicKeys = new HashSet<PublicLifecycleKey>();
        var bindings = new HashSet<RuntimeOrderBindingToken>();
        foreach (var value in values)
        {
            ValidateIdentity(value.Identity, nameof(currentOrders));
            if (value.Identity.BusinessGeneration != businessGeneration)
            {
                throw new ArgumentException(
                    "Every current rare-order identity must match the published business generation.",
                    nameof(currentOrders));
            }
            if (!identities.Add(value.Identity))
            {
                throw new ArgumentException("Current rare-order identities must be unique.", nameof(currentOrders));
            }
            if (!publicKeys.Add(ToPublicLifecycleKey(value.Identity)))
            {
                throw new ArgumentException(
                    "One public trace/lifecycle pair cannot identify multiple canonical guests.",
                    nameof(currentOrders));
            }
            if (!value.Binding.HasValue) continue;
            ValidateBinding(value.Identity, value.Binding.Value, nameof(currentOrders));
            if (!bindings.Add(value.Binding.Value))
            {
                throw new ArgumentException(
                    "One native order binding cannot identify multiple public rare-order lifecycles.",
                    nameof(currentOrders));
            }
        }
        return values;
    }

    private static RuntimeRareGuestParticipationOrderIdentity[] CopyAndValidateMutationTargets(
        IEnumerable<RuntimeRareGuestParticipationOrderIdentity> expectedTargetOrders,
        long businessGeneration,
        int guestId,
        RuntimeRareGuestParticipationTargetScope targetScope)
    {
        ArgumentNullException.ThrowIfNull(expectedTargetOrders);
        if (businessGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(businessGeneration));

        var values = expectedTargetOrders.ToArray();
        if (values.Length == 0)
        {
            throw new ArgumentException(
                "At least one expected target rare-order lifecycle is required.",
                nameof(expectedTargetOrders));
        }
        if (values.Length > MaximumCurrentOrders)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedTargetOrders));
        }
        if (targetScope == RuntimeRareGuestParticipationTargetScope.Order
            && values.Length != 1)
        {
            throw new ArgumentException(
                "Order-scoped participation mutations require exactly one lifecycle identity.",
                nameof(expectedTargetOrders));
        }

        var identities = new HashSet<RuntimeRareGuestParticipationOrderIdentity>();
        foreach (var value in values)
        {
            ValidateIdentity(value, nameof(expectedTargetOrders));
            if (value.BusinessGeneration != businessGeneration)
            {
                throw new ArgumentException(
                    "Every expected target identity must match the requested business generation.",
                    nameof(expectedTargetOrders));
            }
            if (value.GuestId != guestId)
            {
                throw new ArgumentException(
                    "Every expected target identity must belong to the requested canonical guest id.",
                    nameof(expectedTargetOrders));
            }
            if (!identities.Add(value))
            {
                throw new ArgumentException(
                    "Expected target identities must be unique.",
                    nameof(expectedTargetOrders));
            }
        }
        return values;
    }

    private static RuntimeRareGuestParticipationOrderIdentity[] CopyAndValidateProtectedOrders(
        IEnumerable<RuntimeRareGuestParticipationOrderIdentity> protectedOrders,
        long businessGeneration)
    {
        ArgumentNullException.ThrowIfNull(protectedOrders);
        var values = protectedOrders.ToArray();
        if (values.Length > MaximumCurrentOrders)
        {
            throw new ArgumentOutOfRangeException(nameof(protectedOrders));
        }

        var identities = new HashSet<RuntimeRareGuestParticipationOrderIdentity>();
        foreach (var value in values)
        {
            ValidateIdentity(value, nameof(protectedOrders));
            if (value.BusinessGeneration != businessGeneration)
            {
                throw new ArgumentException(
                    "Every protected rare-order identity must match the requested business generation.",
                    nameof(protectedOrders));
            }
            if (!identities.Add(value))
            {
                throw new ArgumentException(
                    "Protected rare-order identities must be unique.",
                    nameof(protectedOrders));
            }
        }
        return values;
    }

    private static void ValidateIdentityContinuityLocked(
        IReadOnlyList<RuntimeRareGuestParticipationOrderObservation> observations)
    {
        foreach (var observation in observations)
        {
            var identity = observation.Identity;
            var publicOwnerKnown = _knownPublicOwners.TryGetValue(
                ToPublicLifecycleKey(identity),
                out var publicOwner);
            if (publicOwnerKnown && publicOwner != identity)
            {
                throw Conflict(
                    "exact-identity-conflict",
                    $"Public rare-order lifecycle {identity.TraceId}/{identity.OrderLifecycleSequence} changed canonical guest identity.");
            }
            if (observation.Binding.HasValue
                && _knownBindingOwners.TryGetValue(observation.Binding.Value, out var bindingOwner)
                && bindingOwner != identity)
            {
                throw Conflict(
                    "exact-identity-conflict",
                    $"Native rare-order binding moved from {Format(bindingOwner)} to {Format(identity)}.");
            }

            if (_knownBindingsByIdentity.TryGetValue(identity, out var existingBinding)
                && observation.Binding.HasValue
                && existingBinding != observation.Binding.Value)
            {
                throw Conflict(
                    "exact-identity-conflict",
                    $"Rare-order lifecycle {Format(identity)} changed its exact native binding.");
            }
            if (publicOwnerKnown
                && publicOwner == identity
                && !_orders.ContainsKey(identity))
            {
                throw Conflict(
                    "retired-lifecycle-reappeared",
                    $"Retired rare-order lifecycle {Format(identity)} cannot re-enter the current authoritative set.");
            }
        }
    }

    private static bool HaveSameOrders(
        IReadOnlyDictionary<RuntimeRareGuestParticipationOrderIdentity, OrderState> left,
        IReadOnlyDictionary<RuntimeRareGuestParticipationOrderIdentity, OrderState> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value) || value != pair.Value) return false;
        }
        return true;
    }

    private static void RequireCurrent(long businessGeneration, long expectedRevision)
    {
        RequireActiveGeneration(businessGeneration);
        if (expectedRevision != _revision)
        {
            throw Conflict(
                "participation-revision-mismatch",
                $"Participation revision {expectedRevision} does not match current revision {_revision}.");
        }
    }

    private static void RequireActiveGeneration(long businessGeneration)
    {
        if (!_isActive)
        {
            throw Conflict("business-inactive", "Rare-guest participation has no active business generation.");
        }
        if (businessGeneration != _businessGeneration)
        {
            throw Conflict(
                "business-generation-mismatch",
                $"Business generation {businessGeneration} does not match current generation {_businessGeneration}.");
        }
    }

    private static RuntimeRareGuestParticipationConflictException Conflict(string code, string message)
    {
        return new RuntimeRareGuestParticipationConflictException(
            code,
            message,
            _businessGeneration,
            _revision);
    }

    private static void ValidateExpectedRevision(long expectedRevision)
    {
        if (expectedRevision <= 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
    }

    private static void ValidateIdentity(
        RuntimeRareGuestParticipationOrderIdentity identity,
        string parameterName)
    {
        if (!IsValidIdentity(identity))
        {
            throw new ArgumentException(
                "Rare-order identity requires a positive business generation/lifecycle, an exact R-trace, and a non-negative canonical guest id.",
                parameterName);
        }
    }

    private static bool IsValidIdentity(RuntimeRareGuestParticipationOrderIdentity identity)
    {
        return identity.BusinessGeneration > 0
            && identity.OrderLifecycleSequence > 0
            && identity.GuestId >= 0
            && IsExactRareTraceId(identity.TraceId);
    }

    private static void ValidateBinding(
        RuntimeRareGuestParticipationOrderIdentity identity,
        RuntimeOrderBindingToken binding,
        string parameterName)
    {
        if (!IsValidBinding(identity, binding))
        {
            throw new ArgumentException(
                "Native rare-order binding must be Special and exactly match the public business generation and lifecycle.",
                parameterName);
        }
    }

    private static bool IsValidBinding(
        RuntimeRareGuestParticipationOrderIdentity identity,
        RuntimeOrderBindingToken binding)
    {
        return binding.BusinessGeneration == identity.BusinessGeneration
            && binding.OrderKind == RuntimeOrderKind.Special
            && binding.OrderPointer != 0
            && binding.ControllerPointer != 0
            && binding.LifecycleSequence == identity.OrderLifecycleSequence;
    }

    private static bool IsExactRareTraceId(string traceId)
    {
        if (traceId == null
            || traceId.Length < 3
            || traceId.Length > 2 + MaximumTraceDigits
            || traceId[0] != 'R'
            || traceId[1] != '-')
        {
            return false;
        }

        for (var index = 2; index < traceId.Length; index += 1)
        {
            var character = traceId[index];
            if (character < '0' || character > '9') return false;
        }
        return true;
    }

    private static PublicLifecycleKey ToPublicLifecycleKey(
        RuntimeRareGuestParticipationOrderIdentity identity)
    {
        return new PublicLifecycleKey(identity.TraceId, identity.OrderLifecycleSequence);
    }

    private static string Format(RuntimeRareGuestParticipationOrderIdentity identity)
    {
        return $"generation={identity.BusinessGeneration}; trace={identity.TraceId}; lifecycle={identity.OrderLifecycleSequence}; guest={identity.GuestId}";
    }

    private sealed record OrderState(
        RuntimeRareGuestParticipationOrderIdentity Identity,
        RuntimeOrderBindingToken? Binding,
        string ReasonCode,
        long ObservationSequence);

    private readonly record struct PublicLifecycleKey(string TraceId, long OrderLifecycleSequence);
}
