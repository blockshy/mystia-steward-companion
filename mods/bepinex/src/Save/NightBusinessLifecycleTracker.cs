namespace MystiaStewardCompanion.Save;

internal enum NightBusinessLifecyclePhase
{
    Inactive,
    Active,
    Closing,
    Destroyed,
}

internal sealed record NightBusinessLifecycleSnapshot(
    long Generation,
    long Version,
    NightBusinessLifecyclePhase Phase,
    string Source,
    DateTime ChangedAtUtc,
    int ThreadId)
{
    public bool IsActive => Phase == NightBusinessLifecyclePhase.Active;
}

/// <summary>
/// Pure managed state machine for one night-business runtime generation.
/// </summary>
internal sealed class NightBusinessLifecycleTracker
{
    private readonly object _syncRoot = new();
    private NightBusinessLifecycleSnapshot _snapshot = new(
        0,
        0,
        NightBusinessLifecyclePhase.Inactive,
        "not started",
        DateTime.MinValue,
        0);

    public NightBusinessLifecycleSnapshot Snapshot
    {
        get
        {
            lock (_syncRoot) return _snapshot;
        }
    }

    public bool TryActivate(string source, DateTime changedAtUtc, int threadId, out NightBusinessLifecycleSnapshot snapshot)
    {
        lock (_syncRoot)
        {
            if (_snapshot.Phase is NightBusinessLifecyclePhase.Active or NightBusinessLifecyclePhase.Closing)
            {
                snapshot = _snapshot;
                return false;
            }

            _snapshot = new NightBusinessLifecycleSnapshot(
                checked(_snapshot.Generation + 1),
                checked(_snapshot.Version + 1),
                NightBusinessLifecyclePhase.Active,
                NormalizeSource(source),
                changedAtUtc,
                threadId);
            snapshot = _snapshot;
            return true;
        }
    }

    public bool TryBeginClosing(string source, DateTime changedAtUtc, int threadId, out NightBusinessLifecycleSnapshot snapshot)
    {
        lock (_syncRoot)
        {
            if (_snapshot.Phase is NightBusinessLifecyclePhase.Closing or NightBusinessLifecyclePhase.Destroyed)
            {
                snapshot = _snapshot;
                return false;
            }

            _snapshot = new NightBusinessLifecycleSnapshot(
                _snapshot.Generation,
                checked(_snapshot.Version + 1),
                NightBusinessLifecyclePhase.Closing,
                NormalizeSource(source),
                changedAtUtc,
                threadId);
            snapshot = _snapshot;
            return true;
        }
    }

    public bool TryMarkDestroyed(string source, DateTime changedAtUtc, int threadId, out NightBusinessLifecycleSnapshot snapshot)
    {
        lock (_syncRoot)
        {
            if (_snapshot.Phase == NightBusinessLifecyclePhase.Destroyed)
            {
                snapshot = _snapshot;
                return false;
            }

            _snapshot = new NightBusinessLifecycleSnapshot(
                _snapshot.Generation,
                checked(_snapshot.Version + 1),
                NightBusinessLifecyclePhase.Destroyed,
                NormalizeSource(source),
                changedAtUtc,
                threadId);
            snapshot = _snapshot;
            return true;
        }
    }

    private static string NormalizeSource(string source)
    {
        return string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim();
    }
}
