using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MystiaStewardCompanion.LocalApi;

const int RestartIterations = 20;
var unexpectedFailures = new ConcurrentQueue<Exception>();
var port = ReserveLoopbackPort();

try
{
    RunCycle("initial", port, unexpectedFailures);
    for (var iteration = 1; iteration <= RestartIterations; iteration += 1)
    {
        RunCycle($"restart-{iteration}", port, unexpectedFailures);
    }

    RunUnexpectedFailureCycle(port);

    if (!unexpectedFailures.IsEmpty)
    {
        throw new InvalidOperationException(
            $"Observed {unexpectedFailures.Count} unexpected listener failure(s): "
            + string.Join(" | ", unexpectedFailures.Select(static failure => failure.Message)));
    }

    Console.WriteLine($"PASS: initial start plus {RestartIterations} restarts stopped cleanly on port {port}; repeated Stop released the port, normal stops reported no failures, and a client callback failure was reported exactly once.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void RunUnexpectedFailureCycle(int port)
{
    using var failureReported = new ManualResetEventSlim();
    var failureCount = 0;
    using var worker = new LocalApiListenerWorker(
        "callback-failure",
        isLan: true,
        IPAddress.Loopback,
        port,
        "local-api-listener-smoke-callback-failure",
        client =>
        {
            client.Dispose();
            throw new InvalidOperationException("expected callback failure");
        },
        (_, exception) =>
        {
            if (exception.Message != "expected callback failure")
            {
                throw new InvalidOperationException("Unexpected failure detail.", exception);
            }

            Interlocked.Increment(ref failureCount);
            failureReported.Set();
        });

    worker.Start();
    using (var client = new TcpClient())
    {
        client.Connect(IPAddress.Loopback, port);
    }

    if (!failureReported.Wait(TimeSpan.FromSeconds(2)))
    {
        throw new TimeoutException("Callback failure was not reported.");
    }

    if (!SpinWait.SpinUntil(() => !worker.IsAlive, TimeSpan.FromSeconds(2)))
    {
        throw new TimeoutException("Listener remained alive after callback failure.");
    }

    if (failureCount != 1)
    {
        throw new InvalidOperationException($"Callback failure was reported {failureCount} times instead of once.");
    }

    if (!worker.Stop(TimeSpan.FromSeconds(2)) || !worker.Stop(TimeSpan.FromSeconds(2)))
    {
        throw new TimeoutException("Repeated Stop failed after callback failure.");
    }

    ProbePortReleased(port, "callback-failure");
}

static void RunCycle(string name, int port, ConcurrentQueue<Exception> unexpectedFailures)
{
    using var accepted = new ManualResetEventSlim();
    using var worker = new LocalApiListenerWorker(
        name,
        isLan: true,
        IPAddress.Loopback,
        port,
        $"local-api-listener-smoke-{name}",
        client =>
        {
            client.Dispose();
            accepted.Set();
        },
        (_, exception) => unexpectedFailures.Enqueue(exception));

    worker.Start();
    using (var client = new TcpClient())
    {
        client.Connect(IPAddress.Loopback, port);
        if (!accepted.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException($"{name}: listener did not accept a loopback client.");
        }
    }

    if (!worker.Stop(TimeSpan.FromSeconds(2)))
    {
        throw new TimeoutException($"{name}: listener thread did not stop within two seconds.");
    }

    if (!worker.Stop(TimeSpan.FromSeconds(2)))
    {
        throw new TimeoutException($"{name}: repeated Stop did not complete.");
    }

    if (worker.IsAlive)
    {
        throw new InvalidOperationException($"{name}: listener thread remained alive after Stop.");
    }

    ProbePortReleased(port, name);
}

static void ProbePortReleased(int port, string name)
{
    var portProbe = new TcpListener(IPAddress.Loopback, port);
    try
    {
        portProbe.Start();
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException($"{name}: listener did not release port {port}.", ex);
    }
    finally
    {
        portProbe.Stop();
    }
}

static int ReserveLoopbackPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    try
    {
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    finally
    {
        listener.Stop();
    }
}
