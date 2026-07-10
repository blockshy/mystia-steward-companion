using System.Runtime.ExceptionServices;

namespace MystiaStewardCompanion.Ui;

internal interface IMainThreadCommand : IDisposable
{
    bool Cancel(Exception error);
}

internal abstract class MainThreadCommand<TResult> : IMainThreadCommand
    where TResult : class
{
    private const int Queued = 0;
    private const int Running = 1;
    private const int Completed = 2;
    private const int Cancelled = 3;

    private readonly ManualResetEventSlim _completion = new(false);
    private int _state;
    private int _disposed;
    private TResult? _result;
    private Exception? _error;

    public bool TryBegin()
    {
        return Interlocked.CompareExchange(ref _state, Running, Queued) == Queued;
    }

    public void Complete(TResult result)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));
        if (Volatile.Read(ref _state) != Running)
        {
            throw new InvalidOperationException("Only a running main-thread command can complete.");
        }

        _result = result;
        Volatile.Write(ref _state, Completed);
        _completion.Set();
    }

    public void Fail(Exception error)
    {
        if (error == null) throw new ArgumentNullException(nameof(error));
        if (Volatile.Read(ref _state) != Running)
        {
            throw new InvalidOperationException("Only a running main-thread command can fail.");
        }

        _error = error;
        Volatile.Write(ref _state, Completed);
        _completion.Set();
    }

    public bool Cancel(Exception error)
    {
        if (error == null) throw new ArgumentNullException(nameof(error));
        if (Interlocked.CompareExchange(ref _state, Cancelled, Queued) != Queued) return false;

        _error = error;
        _completion.Set();
        return true;
    }

    public TResult WaitForResult(TimeSpan timeout, string timeoutMessage)
    {
        if (!_completion.Wait(timeout))
        {
            var timeoutError = new TimeoutException(timeoutMessage);
            if (Cancel(timeoutError)) throw timeoutError;

            // Once execution has started, wait for its definitive result so callers never retry an
            // operation that may already have changed game state.
            _completion.Wait();
        }

        if (_error != null) ExceptionDispatchInfo.Capture(_error).Throw();
        return _result ?? throw new InvalidOperationException("Main-thread command did not produce a result.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _completion.Dispose();
    }
}
