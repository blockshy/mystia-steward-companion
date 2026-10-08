using System.Diagnostics;

namespace MystiaStewardCompanion.Plugin.CompanionControl;

internal enum ControlExitOutcome { Acknowledged, NoRegistration, NoSession, NotApplicable, Failed, Cancelled, TimedOut }

// Starts at the final native entry, before logging or acquiring product locks.
internal sealed class ControlExitBudget
{
    private readonly Func<long> _timestamp;
    private readonly long _frequency, _started;
    private readonly TimeSpan _duration;
    internal ControlExitBudget() : this(TimeSpan.FromSeconds(ControlExitCompletion.TimeoutSeconds), Stopwatch.GetTimestamp, Stopwatch.Frequency) { }
    internal ControlExitBudget(TimeSpan duration, Func<long> timestamp, long frequency)
    {
        ControlFrame.Require(duration > TimeSpan.Zero && duration <= TimeSpan.FromSeconds(ControlExitCompletion.TimeoutSeconds) && frequency > 0,
            "exit_clock_budget");
        _timestamp = timestamp; _frequency = frequency; _duration = duration; _started = timestamp();
        ControlFrame.Require(_started >= 0, "exit_clock_timestamp");
    }
    internal TimeSpan Remaining
    {
        get
        {
            var now = _timestamp();
            if (now < _started) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(Math.Max(0, _duration.TotalSeconds - (double)(now - _started) / _frequency));
        }
    }
}

internal static class ControlExitDispatch
{
    internal static ControlExitOutcome Run(ControlExitBudget budget, Func<ControlExitBudget, ControlExitOutcome> work)
    {
        if (budget.Remaining == TimeSpan.Zero) return ControlExitOutcome.TimedOut;
        // Logging and existing potentially contended lifecycle/submission locks
        // stay on this one worker. The final native thread waits only on its task.
        var pending = Task.Run(() =>
        {
            try { return budget.Remaining == TimeSpan.Zero ? ControlExitOutcome.TimedOut : work(budget); }
            catch { return ControlExitOutcome.Failed; }
        });
        if (!pending.Wait(budget.Remaining) || budget.Remaining == TimeSpan.Zero) return ControlExitOutcome.TimedOut;
        return pending.GetAwaiter().GetResult();
    }
}

// One result and one monotonic deadline for an entire session's shutdown.
// This type never invokes transport, logging, Unity, or a caller continuation inline.
internal sealed class ControlExitCompletion
{
    internal const int TimeoutSeconds = 13;
    private readonly object _sync = new();
    private readonly TaskCompletionSource<ControlExitOutcome> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<long> _timestamp;
    private readonly long _frequency;
    private ControlExitBudget? _budget, _limit;

    internal ControlExitCompletion() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency) { }
    internal ControlExitCompletion(Func<long> timestamp, long frequency)
    {
        ControlFrame.Require(frequency > 0, "exit_clock_frequency");
        _timestamp = timestamp;
        _frequency = frequency;
    }
    internal Task<ControlExitOutcome> Completion => _result.Task;
    internal bool Start(ControlExitBudget? limit = null)
    {
        lock (_sync)
        {
            ConstrainLocked(limit);
            if (_budget != null || _result.Task.IsCompleted) return false;
            _budget = new ControlExitBudget(TimeSpan.FromSeconds(TimeoutSeconds), _timestamp, _frequency);
            return true;
        }
    }
    internal void Constrain(ControlExitBudget? limit)
    { lock (_sync) ConstrainLocked(limit); }
    private void ConstrainLocked(ControlExitBudget? limit)
    {
        if (limit != null && (_limit == null || limit.Remaining < _limit.Remaining)) _limit = limit;
    }
    internal TimeSpan Remaining
    {
        get { lock (_sync) return RemainingLocked(); }
    }
    private TimeSpan RemainingLocked()
    {
        if (_result.Task.IsCompleted) return TimeSpan.Zero;
        ControlFrame.Require(_budget != null, "exit_not_started");
        var own = _budget!.Remaining;
        return _limit != null && _limit.Remaining < own ? _limit.Remaining : own;
    }
    internal bool Complete(ControlExitOutcome outcome)
    {
        lock (_sync)
        {
            if (_result.Task.IsCompleted) return false;
            if (outcome == ControlExitOutcome.Acknowledged)
            {
                ControlFrame.Require(_budget != null, "exit_ack_before_request");
                if (RemainingLocked() == TimeSpan.Zero) outcome = ControlExitOutcome.TimedOut;
            }
            return _result.TrySetResult(outcome);
        }
    }
    internal ControlExitOutcome Wait(ControlExitBudget? limit = null)
    {
        // Never call while holding the launcher's lifecycle or session submission lock.
        Constrain(limit);
        if (limit != null && limit.Remaining == TimeSpan.Zero) return ControlExitOutcome.TimedOut;
        var remaining = Remaining;
        if (!_result.Task.Wait(remaining)) Complete(ControlExitOutcome.TimedOut);
        return _result.Task.GetAwaiter().GetResult();
    }
}
