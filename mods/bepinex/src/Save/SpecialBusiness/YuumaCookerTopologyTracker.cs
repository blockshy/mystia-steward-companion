namespace MystiaStewardCompanion.Save;

internal sealed record YuumaCookerTopologyMutationFrame(
    long BusinessGeneration,
    long Epoch,
    long Sequence,
    string Source);

internal readonly record struct YuumaCookerTopologySnapshotProbe(
    long BusinessGeneration,
    long Epoch,
    long Revision);

internal sealed record YuumaCookerTopologyLease(
    long BusinessGeneration,
    long Epoch,
    long Revision,
    string SnapshotSignature,
    int ControllerCount,
    int LockedControllerCount);

internal sealed class YuumaCookerTopologyTracker
{
    private readonly HashSet<long> _activeMutationSequences = new();

    private long _businessGeneration;
    private long _epoch;
    private long _revision;
    private long _nextMutationSequence;
    private long _validatedRevision = -1;
    private string _validatedSignature = "";

    public int MutationDepth => _activeMutationSequences.Count;

    public long Revision => _revision;

    public string Describe(bool hooksReady, string resetReason)
    {
        return $"hooksReady={hooksReady}; generation={_businessGeneration}; epoch={_epoch}; "
            + $"revision={_revision}; validatedRevision={_validatedRevision}; "
            + $"mutationDepth={MutationDepth}; signature={_validatedSignature}; reset={resetReason}";
    }

    public YuumaCookerTopologyMutationFrame? BeginMutation(long businessGeneration, string source)
    {
        if (businessGeneration <= 0) return null;

        if (!TryEnsureGeneration(businessGeneration)) return null;
        var sequence = ++_nextMutationSequence;
        _activeMutationSequences.Add(sequence);
        return new YuumaCookerTopologyMutationFrame(
            businessGeneration,
            _epoch,
            sequence,
            source);
    }

    public bool CompleteMutation(
        YuumaCookerTopologyMutationFrame? frame,
        bool originalRan)
    {
        if (frame == null
            || frame.BusinessGeneration != _businessGeneration
            || frame.Epoch != _epoch
            || !_activeMutationSequences.Remove(frame.Sequence))
        {
            return false;
        }

        if (originalRan)
        {
            _revision++;
            InvalidateValidation();
        }
        return true;
    }

    public bool TryBeginSnapshot(
        long businessGeneration,
        bool hooksReady,
        out YuumaCookerTopologySnapshotProbe probe,
        out string diagnostic)
    {
        probe = default;
        if (businessGeneration <= 0)
        {
            diagnostic = "business-generation-unavailable";
            return false;
        }

        if (!TryEnsureGeneration(businessGeneration))
        {
            diagnostic = $"stale-business-generation; current={_businessGeneration}; requested={businessGeneration}";
            return false;
        }
        if (!hooksReady)
        {
            diagnostic = "required-hooks-unavailable";
            return false;
        }

        if (MutationDepth != 0)
        {
            diagnostic = $"topology-mutation-active; depth={MutationDepth}; revision={_revision}";
            return false;
        }

        probe = new YuumaCookerTopologySnapshotProbe(
            _businessGeneration,
            _epoch,
            _revision);
        diagnostic = "snapshot-probe-ready";
        return true;
    }

    public bool TryCommitSnapshot(
        YuumaCookerTopologySnapshotProbe probe,
        bool hooksReady,
        string snapshotSignature,
        int controllerCount,
        int lockedControllerCount,
        out YuumaCookerTopologyLease lease,
        out string diagnostic)
    {
        lease = null!;
        if (!TryValidateProbe(probe, hooksReady, out diagnostic)) return false;
        if (!IsCanonicalSignature(snapshotSignature)
            || controllerCount < 0
            || lockedControllerCount < 0
            || lockedControllerCount > controllerCount)
        {
            diagnostic = "snapshot-identity-invalid";
            return false;
        }

        _validatedRevision = probe.Revision;
        _validatedSignature = snapshotSignature;
        lease = new YuumaCookerTopologyLease(
            probe.BusinessGeneration,
            probe.Epoch,
            probe.Revision,
            snapshotSignature,
            controllerCount,
            lockedControllerCount);
        diagnostic = $"lease-acquired; revision={probe.Revision}; controllers={controllerCount}; "
            + $"locked={lockedControllerCount}; signature={snapshotSignature}";
        return true;
    }

    public bool TryValidateSnapshot(
        YuumaCookerTopologySnapshotProbe probe,
        bool hooksReady,
        YuumaCookerTopologyLease lease,
        string snapshotSignature,
        int controllerCount,
        int lockedControllerCount,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!TryValidateProbe(probe, hooksReady, out diagnostic)) return false;
        if (lease.BusinessGeneration != probe.BusinessGeneration
            || lease.Epoch != probe.Epoch
            || lease.Revision != probe.Revision
            || _validatedRevision != probe.Revision
            || !string.Equals(_validatedSignature, lease.SnapshotSignature, StringComparison.Ordinal))
        {
            diagnostic = "lease-revision-or-epoch-stale";
            return false;
        }

        if (!string.Equals(snapshotSignature, lease.SnapshotSignature, StringComparison.Ordinal)
            || controllerCount != lease.ControllerCount
            || lockedControllerCount != lease.LockedControllerCount)
        {
            InvalidateValidation();
            diagnostic = "fresh-snapshot-does-not-match-lease";
            return false;
        }

        diagnostic = $"lease-valid; revision={probe.Revision}; controllers={controllerCount}; "
            + $"locked={lockedControllerCount}; signature={snapshotSignature}";
        return true;
    }

    public void Reset()
    {
        _epoch++;
        _businessGeneration = 0;
        _revision = 0;
        _nextMutationSequence = 0;
        _activeMutationSequences.Clear();
        InvalidateValidation();
    }

    private bool TryValidateProbe(
        YuumaCookerTopologySnapshotProbe probe,
        bool hooksReady,
        out string diagnostic)
    {
        if (!hooksReady)
        {
            diagnostic = "required-hooks-unavailable";
            return false;
        }

        if (probe.BusinessGeneration != _businessGeneration
            || probe.Epoch != _epoch
            || probe.Revision != _revision)
        {
            diagnostic = "topology-changed-during-snapshot";
            return false;
        }

        if (MutationDepth != 0)
        {
            diagnostic = $"topology-mutation-active; depth={MutationDepth}; revision={_revision}";
            return false;
        }

        diagnostic = "snapshot-probe-current";
        return true;
    }

    private bool TryEnsureGeneration(long businessGeneration)
    {
        if (_businessGeneration == businessGeneration) return true;
        if (_businessGeneration > 0 && businessGeneration < _businessGeneration) return false;

        _epoch++;
        _businessGeneration = businessGeneration;
        _revision = 0;
        _nextMutationSequence = 0;
        _activeMutationSequences.Clear();
        InvalidateValidation();
        return true;
    }

    private void InvalidateValidation()
    {
        _validatedRevision = -1;
        _validatedSignature = "";
    }

    private static bool IsCanonicalSignature(string value)
    {
        if (value.Length != 64) return false;

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
