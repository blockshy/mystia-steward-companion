using MystiaStewardCompanion.Ui;

try
{
    VerifyQueuedTimeoutCancelsExecution();
    VerifyRunningCommandReturnsDefinitiveResult();
    VerifyCancellationWakesWaiter();
    VerifyFailurePropagation();
    Console.WriteLine("PASS: queued timeouts cancel, running commands return definitive results, cancellation wakes waiters, and failures propagate.");
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
