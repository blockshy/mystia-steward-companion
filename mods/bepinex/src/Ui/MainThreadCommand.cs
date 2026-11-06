using System.Runtime.ExceptionServices;

namespace MystiaStewardCompanion.Ui;

internal interface IMainThreadCommand : IDisposable
{
    bool Cancel(Exception error);
}

/// <summary>协调工作线程等待与主线程执行，使用原子状态转换保证排队命令只能被执行或取消一次。</summary>
/// <remarks>派生类保存托管请求值；实际 Unity 访问只能在主线程取得执行权后进行。</remarks>
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

    /// <summary>仅取消尚未开始的命令；已开始的操作可能产生副作用，不能用超时伪装成已取消。</summary>
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

            // 超时后若无法撤销排队，则等待最终执行结果。此处刻意不再使用超时，
            // 避免调用方把可能已改变游戏状态的操作当作失败并重复提交。
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
