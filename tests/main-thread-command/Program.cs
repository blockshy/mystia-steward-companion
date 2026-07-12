using MystiaStewardCompanion.Ui;

try
{
    VerifyQueuedTimeoutCancelsExecution();
    VerifyRunningCommandReturnsDefinitiveResult();
    VerifyCancellationWakesWaiter();
    VerifyFailurePropagation();
    VerifyEpochAdvanceWaitsForRunningCommand();
    Console.WriteLine("PASS: queued timeouts cancel, running commands return definitive results, cancellation wakes waiters, failures propagate, and epoch changes wait for the current execution boundary.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyQueuedTimeoutCancelsExecution()
{
    using var command = new TestCommand();
    try
    {
        _ = command.WaitForResult(TimeSpan.FromMilliseconds(20), "expected timeout");
        throw new InvalidOperationException("Queued command did not time out.");
    }
    catch (TimeoutException ex) when (ex.Message == "expected timeout")
    {
    }

    AssertEqual(false, command.TryBegin(), "A timed-out queued command could still begin.");
}

static void VerifyRunningCommandReturnsDefinitiveResult()
{
    using var command = new TestCommand();
    var waiter = Task.Run(() => command.WaitForResult(TimeSpan.FromMilliseconds(20), "must not escape after start"));
    AssertEqual(true, SpinWait.SpinUntil(command.TryBegin, TimeSpan.FromSeconds(1)), "Command did not start.");
    Thread.Sleep(50);
    command.Complete(new TestResult("completed"));
    AssertEqual("completed", waiter.GetAwaiter().GetResult().Value, "Running command result was not returned.");
}

static void VerifyCancellationWakesWaiter()
{
    using var command = new TestCommand();
    var waiter = Task.Run(() => command.WaitForResult(TimeSpan.FromSeconds(5), "unexpected timeout"));
    Thread.Sleep(20);
    var cancellation = new ObjectDisposedException("controller");
    AssertEqual(true, command.Cancel(cancellation), "Queued command could not be cancelled.");
    try
    {
        _ = waiter.GetAwaiter().GetResult();
        throw new InvalidOperationException("Cancelled command returned a result.");
    }
    catch (ObjectDisposedException)
    {
    }
}

static void VerifyFailurePropagation()
{
    using var command = new TestCommand();
    AssertEqual(true, command.TryBegin(), "Command did not enter running state.");
    command.Fail(new InvalidOperationException("expected failure"));
    try
    {
        _ = command.WaitForResult(TimeSpan.FromSeconds(1), "unexpected timeout");
        throw new InvalidOperationException("Failed command returned a result.");
    }
    catch (InvalidOperationException ex) when (ex.Message == "expected failure")
    {
    }
}

static void VerifyEpochAdvanceWaitsForRunningCommand()
{
    var fence = new AutomationCommandEpochFence(initialEpoch: 4);
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var execution = Task.Run(() => fence.RunExclusive(currentEpoch =>
    {
        AssertEqual(4L, currentEpoch, "The command did not start in its validated epoch.");
        entered.Set();
        release.Wait(TimeSpan.FromSeconds(2));
        return "executed";
    }));
    AssertEqual(true, entered.Wait(TimeSpan.FromSeconds(1)), "The command did not enter the epoch fence.");

    var cancelledEpoch = 0L;
    var advance = Task.Run(() => fence.Advance(5, nextEpoch =>
    {
        cancelledEpoch = nextEpoch;
        return 3;
    }));
    AssertEqual(false, advance.Wait(TimeSpan.FromMilliseconds(50)), "The epoch advanced while an old command was still executing.");

    release.Set();
    AssertEqual("executed", execution.GetAwaiter().GetResult(), "The fenced command did not finish normally.");
    AssertEqual(3, advance.GetAwaiter().GetResult(), "Queued-command cancellation result was not returned.");
    AssertEqual(5L, cancelledEpoch, "The cancellation callback observed the wrong epoch.");
    AssertEqual(5L, fence.CurrentEpoch, "The fence did not publish the new epoch after execution completed.");
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}

sealed class TestCommand : MainThreadCommand<TestResult>
{
}

sealed record TestResult(string Value);
