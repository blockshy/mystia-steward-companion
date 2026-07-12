namespace MystiaStewardCompanion.Ui;

/// <summary>
/// Serializes automation command execution with ownership epoch changes.
/// </summary>
internal sealed class AutomationCommandEpochFence
{
    private readonly object _syncRoot = new();
    private long _currentEpoch;

    public AutomationCommandEpochFence(long initialEpoch = 0)
    {
        _currentEpoch = initialEpoch;
    }

    public long CurrentEpoch
    {
        get
        {
            lock (_syncRoot) return _currentEpoch;
        }
    }

    public int Advance(long nextEpoch, Func<long, int> cancelQueuedCommands)
    {
        lock (_syncRoot)
        {
            if (nextEpoch <= _currentEpoch) return 0;

            _currentEpoch = nextEpoch;
            return cancelQueuedCommands(nextEpoch);
        }
    }

    public TResult RunExclusive<TResult>(Func<long, TResult> action)
    {
        lock (_syncRoot) return action(_currentEpoch);
    }
}
