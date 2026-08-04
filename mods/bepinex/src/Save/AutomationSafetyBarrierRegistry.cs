namespace MystiaStewardCompanion.Save;

internal sealed record AutomationSafetyBarrierRecord(
    long Sequence,
    long BusinessGeneration,
    string TargetIdentity,
    string Code,
    string Stage,
    string Message);

internal readonly record struct AutomationSafetyBarrierAcknowledgement(
    bool Found,
    IReadOnlyList<long> Sequences);

/// <summary>
/// Retains non-idempotent automation failures until the lease owner explicitly confirms the game state.
/// Callers synchronize access with the automation job lock so event publication and barrier registration stay atomic.
/// </summary>
internal sealed class AutomationSafetyBarrierRegistry
{
    private readonly Dictionary<long, AutomationSafetyBarrierRecord> _barriers = new();

    public static string BuildOrderLifecycleTargetIdentity(
        long businessGeneration,
        string orderKind,
        nint orderPointer,
        nint controllerPointer,
        long lifecycleSequence)
    {
        if (businessGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(businessGeneration));
        if (string.IsNullOrWhiteSpace(orderKind)) throw new ArgumentException("Concrete order kind is required.", nameof(orderKind));
        if (orderPointer == 0) throw new ArgumentOutOfRangeException(nameof(orderPointer));
        if (controllerPointer == 0) throw new ArgumentOutOfRangeException(nameof(controllerPointer));
        if (lifecycleSequence <= 0) throw new ArgumentOutOfRangeException(nameof(lifecycleSequence));

        return $"order-lifecycle:{businessGeneration}:{orderKind}:"
            + $"{(long)orderPointer:x}:{(long)controllerPointer:x}:"
            + lifecycleSequence;
    }

    public bool Contains(long sequence) => _barriers.ContainsKey(sequence);

    public void Register(AutomationSafetyBarrierRecord barrier)
    {
        if (barrier.Sequence <= 0) throw new ArgumentOutOfRangeException(nameof(barrier));
        if (barrier.BusinessGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(barrier));
        if (string.IsNullOrWhiteSpace(barrier.TargetIdentity))
        {
            throw new ArgumentException("Automation safety barrier target identity is required.", nameof(barrier));
        }

        _barriers[barrier.Sequence] = barrier;
    }

    /// <summary>
    /// Retires every unresolved barrier owned by one business generation at its exact lifecycle boundary.
    /// Active-generation barriers are never silently evicted by capacity.
    /// </summary>
    public IReadOnlyList<long> RetireBusinessGeneration(long businessGeneration)
    {
        if (businessGeneration <= 0) return Array.Empty<long>();

        var sequences = _barriers
            .Where(entry => entry.Value.BusinessGeneration == businessGeneration)
            .Select(entry => entry.Key)
            .OrderBy(value => value)
            .ToArray();
        foreach (var sequence in sequences)
        {
            _barriers.Remove(sequence);
        }

        return sequences;
    }

    public bool TryGetLatest(string targetIdentity, out AutomationSafetyBarrierRecord? barrier)
    {
        barrier = _barriers.Values
            .Where(candidate => string.Equals(candidate.TargetIdentity, targetIdentity, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Sequence)
            .FirstOrDefault();
        return barrier != null;
    }

    public AutomationSafetyBarrierAcknowledgement Acknowledge(long sequence)
    {
        if (!_barriers.TryGetValue(sequence, out var selected))
        {
            return new AutomationSafetyBarrierAcknowledgement(false, Array.Empty<long>());
        }

        var sequences = _barriers
            .Where(entry => entry.Key <= sequence
                && string.Equals(entry.Value.TargetIdentity, selected.TargetIdentity, StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .OrderBy(value => value)
            .ToArray();
        foreach (var acknowledgedSequence in sequences)
        {
            _barriers.Remove(acknowledgedSequence);
        }

        return new AutomationSafetyBarrierAcknowledgement(true, sequences);
    }
}
