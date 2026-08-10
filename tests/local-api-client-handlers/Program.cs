using System.Collections.Concurrent;
using MystiaStewardCompanion.LocalApi;

var failures = new ConcurrentQueue<Exception>();
var pool = new BoundedHandlerPool<TestResource>(1, failures.Enqueue);

try
{
    VerifyConcurrencyLimitAndStop(pool);
    VerifyHandlerFailureReleasesSlot(pool, failures);
    VerifyDisposeFailureReleasesSlot();
    VerifyRequestsMustBeCompleteAndBounded();
    Console.WriteLine("PASS: client handlers are bounded and leak-free, and HTTP headers/bodies are complete and bounded.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyConcurrencyLimitAndStop(BoundedHandlerPool<TestResource> pool)
{
    using var started = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    pool.StartAccepting();

    var first = new TestResource();
    AssertEqual(true, pool.TryDispatch(first, _ =>
    {
        started.Set();
        release.Wait();
    }), "First client was not dispatched.");
    AssertEqual(true, started.Wait(TimeSpan.FromSeconds(1)), "First handler did not start.");

    var rejected = new TestResource();
    AssertEqual(false, pool.TryDispatch(rejected, _ => { }), "Pool accepted a client above its concurrency limit.");

    pool.StopAccepting();
    AssertEqual(false, pool.TryDispatch(new TestResource(), _ => { }), "Pool accepted a client after stop.");
    AssertEqual(false, pool.WaitForIdle(TimeSpan.FromMilliseconds(20)), "Pool reported idle while a handler was active.");
    release.Set();
    AssertEqual(true, pool.WaitForIdle(TimeSpan.FromSeconds(1)), "Pool did not become idle after the handler exited.");
}

static void VerifyHandlerFailureReleasesSlot(BoundedHandlerPool<TestResource> pool, ConcurrentQueue<Exception> failures)
{
    pool.StartAccepting();
    AssertEqual(true, pool.TryDispatch(new TestResource(), _ => throw new InvalidOperationException("expected handler failure")), "Failing handler was not dispatched.");
    AssertEqual(true, pool.WaitForIdle(TimeSpan.FromSeconds(1)), "Failing handler did not release its slot.");
    AssertEqual(true, failures.TryDequeue(out var failure), "Handler failure was not reported.");
    AssertEqual("expected handler failure", failure?.Message, "Unexpected handler failure detail.");
    pool.StopAccepting();
}

static void VerifyDisposeFailureReleasesSlot()
{
    var failures = new ConcurrentQueue<Exception>();
    var pool = new BoundedHandlerPool<ThrowingDisposeResource>(1, failures.Enqueue);
    pool.StartAccepting();
    AssertEqual(true, pool.TryDispatch(new ThrowingDisposeResource(), _ => { }), "Dispose-failing handler was not dispatched.");
    AssertEqual(true, pool.WaitForIdle(TimeSpan.FromSeconds(1)), "A dispose failure leaked an active handler slot.");
    AssertEqual(true, failures.TryDequeue(out var failure), "Dispose failure was not reported.");
    AssertEqual("expected dispose failure", failure?.Message, "Unexpected dispose failure detail.");
    pool.StopAccepting();
}

static void VerifyRequestsMustBeCompleteAndBounded()
{
    using var complete = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("POST /devices/register HTTP/1.1\r\nHost: localhost\r\nContent-Length: 2\r\n\r\n{}"));
    var request = HttpRequestReader.Read(complete, 128, 16);
    AssertEqual("POST /devices/register HTTP/1.1\r\nHost: localhost\r\nContent-Length: 2\r\n\r\n", request.Header, "Complete headers were not read exactly.");
    AssertEqual("{}", HttpRequestReader.ReadRequiredJsonBody(request), "Complete request body was not read exactly.");

    ExpectRequestError(
        new MemoryStream(System.Text.Encoding.ASCII.GetBytes("GET /health HTTP/1.1\r\nHost: localhost")),
        128,
        16,
        400);
    ExpectRequestError(
        new MemoryStream(System.Text.Encoding.ASCII.GetBytes(new string('a', 32))),
        32,
        16,
        431);
    ExpectRequestError(
        new MemoryStream(System.Text.Encoding.ASCII.GetBytes("POST /devices/register HTTP/1.1\r\nContent-Length: 17\r\n\r\n" + new string('a', 17))),
        128,
        16,
        413);
    ExpectRequestError(
        new MemoryStream(System.Text.Encoding.ASCII.GetBytes("POST /devices/register HTTP/1.1\r\nContent-Length: 3\r\n\r\n{}")),
        128,
        16,
        400);
    ExpectRequestError(
        new MemoryStream(System.Text.Encoding.ASCII.GetBytes("POST /devices/register HTTP/1.1\r\nContent-Length: 2\r\nContent-Length: 2\r\n\r\n{}")),
        128,
        16,
        400);
    ExpectRequestError(
        new MemoryStream(System.Text.Encoding.ASCII.GetBytes("POST /devices/register HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n")),
        128,
        16,
        400);
}

static void ExpectRequestError(Stream stream, int maxHeaderBytes, int maxBodyBytes, int expectedStatus)
{
    using (stream)
    {
        try
        {
            _ = HttpRequestReader.Read(stream, maxHeaderBytes, maxBodyBytes);
            throw new InvalidOperationException($"Expected HTTP {expectedStatus} request error was not thrown.");
        }
        catch (HttpRequestReadException ex)
        {
            AssertEqual(expectedStatus, ex.StatusCode, "Unexpected request error status.");
        }
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}

sealed class TestResource : IDisposable
{
    public void Dispose()
    {
    }
}

sealed class ThrowingDisposeResource : IDisposable
{
    public void Dispose()
    {
        throw new InvalidOperationException("expected dispose failure");
    }
}
