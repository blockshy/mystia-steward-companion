namespace MystiaStewardCompanion.Ui;

/// <summary>
/// 将自动化命令执行与所有权纪元变更串行化，确保执行检查和旧排队命令撤销不会交错。
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

    /// <summary>只接受更大的纪元，并在同一锁内撤销旧排队命令；撤销委托不得等待依赖该锁的其他线程。</summary>
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
