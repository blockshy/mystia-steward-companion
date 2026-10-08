namespace MystiaStewardCompanion.FocusProbe;

// This model has no Unity/IL2CPP references. Only immutable managed values cross threads.
internal sealed record HeartbeatSnapshot(long Sequence, long Ticks, uint ThreadId, string? Error);

internal sealed class HeartbeatState
{
    private HeartbeatSnapshot _current;
    public uint ThreadId { get; }
    public long Frequency { get; }

    public HeartbeatState(uint threadId, long frequency)
    {
        Guard.That(threadId != 0 && frequency > 0, "Heartbeat thread/frequency is invalid.");
        ThreadId = threadId; Frequency = frequency;
        _current = new HeartbeatSnapshot(0, 0, threadId, null);
    }

    public HeartbeatSnapshot Snapshot => Volatile.Read(ref _current);

    public void Publish(uint threadId, long ticks)
    {
        while (true)
        {
            var previous = Snapshot;
            if (previous.Error != null) return;
            if (threadId != ThreadId || ticks <= previous.Ticks || previous.Sequence == long.MaxValue)
            {
                Stop("Unity Update thread changed, its monotonic clock did not advance, or its sequence overflowed.");
                return;
            }
            var next = new HeartbeatSnapshot(previous.Sequence + 1, ticks, threadId, null);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, next, previous), previous)) return;
        }
    }

    public void Stop(string error)
    {
        while (true)
        {
            var previous = Snapshot;
            if (previous.Error != null) return;
            var next = previous with { Error = error };
            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, next, previous), previous)) return;
        }
    }

    public HeartbeatSnapshot RequireCurrent(HeartbeatSnapshot minimum, uint gameThreadId)
    {
        ValidateBaseline(minimum);
        var current = Snapshot;
        ValidateLive(current);
        Guard.That(gameThreadId == ThreadId && current.Sequence >= minimum.Sequence && current.Ticks >= minimum.Ticks,
            "Unity heartbeat does not match the exact game window thread or its retained progress.");
        return current;
    }

    public async Task<HeartbeatSnapshot> WaitForAdvanceAsync(HeartbeatSnapshot baseline, TimeSpan timeout, CancellationToken cancellation)
    {
        ValidateBaseline(baseline);
        Guard.That(timeout > TimeSpan.Zero && timeout <= TimeSpan.FromSeconds(100), "Heartbeat wait bound is invalid.");
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                var current = Snapshot;
                ValidateLive(current);
                Guard.That(current.Sequence >= baseline.Sequence && current.Ticks >= baseline.Ticks, "Heartbeat regressed behind its request baseline.");
                if (current.Sequence > baseline.Sequence && current.Ticks > baseline.Ticks) return current;
                // Sampling interval only: elapsed time never constitutes readiness.
                await Task.Delay(10, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new InvalidDataException("No subsequent Unity Update was observed before the bounded heartbeat deadline.");
        }
    }

    private void ValidateBaseline(HeartbeatSnapshot baseline)
    {
        Guard.That(baseline.ThreadId == ThreadId && baseline.Error == null && baseline.Sequence >= 0 && baseline.Ticks >= 0 &&
            (baseline.Sequence == 0) == (baseline.Ticks == 0), "Heartbeat baseline is invalid or stopped.");
    }
    private void ValidateLive(HeartbeatSnapshot current)
    {
        Guard.That(current.Error == null, "Unity heartbeat stopped: " + current.Error);
        Guard.That(current.ThreadId == ThreadId, "Unity heartbeat thread differs from its registration thread.");
    }
}
