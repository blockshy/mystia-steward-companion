namespace MystiaStewardCompanion.LocalApi;

/// <summary>限制同时处理的连接资源数量；达到上限或停止接收时直接释放新资源，不建立无界等待队列。</summary>
/// <remarks>每次分派应提供独立资源，且 Dispose 须允许重复调用；停止接收与处理器退出都可能触发释放。</remarks>
internal sealed class BoundedHandlerPool<TResource>
    where TResource : class, IDisposable
{
    private readonly object _lock = new();
    private readonly HashSet<TResource> _activeResources = new();
    private readonly ManualResetEventSlim _idle = new(true);
    private readonly int _maxConcurrentHandlers;
    private readonly Action<Exception>? _reportFailure;
    private bool _accepting;

    public BoundedHandlerPool(int maxConcurrentHandlers, Action<Exception>? reportFailure = null)
    {
        if (maxConcurrentHandlers <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrentHandlers));
        _maxConcurrentHandlers = maxConcurrentHandlers;
        _reportFailure = reportFailure;
    }

    public void StartAccepting()
    {
        lock (_lock)
        {
            if (_activeResources.Count > 0)
            {
                throw new InvalidOperationException("Handlers from the previous run are still active.");
            }

            _accepting = true;
        }
    }

    public bool TryDispatch(TResource resource, Action<TResource> handler)
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        var accepted = false;
        lock (_lock)
        {
            if (_accepting && _activeResources.Count < _maxConcurrentHandlers)
            {
                _activeResources.Add(resource);
                _idle.Reset();
                accepted = true;
            }
        }

        if (!accepted)
        {
            DisposeResource(resource);
            return false;
        }

        if (ThreadPool.QueueUserWorkItem(_ => RunHandler(resource, handler))) return true;

        try
        {
            DisposeResource(resource);
        }
        finally
        {
            Complete(resource);
        }
        ReportFailure(new InvalidOperationException("Handler could not be queued."));
        return false;
    }

    /// <summary>停止接收并释放当前资源以中断阻塞 I/O；是否已全部退出须另外通过 WaitForIdle 确认。</summary>
    public void StopAccepting()
    {
        TResource[] activeResources;
        lock (_lock)
        {
            _accepting = false;
            activeResources = _activeResources.ToArray();
        }

        foreach (var resource in activeResources)
        {
            DisposeResource(resource);
        }
    }

    public bool WaitForIdle(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        return _idle.Wait(timeout);
    }

    private void RunHandler(TResource resource, Action<TResource> handler)
    {
        try
        {
            handler(resource);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
        finally
        {
            try
            {
                DisposeResource(resource);
            }
            finally
            {
                Complete(resource);
            }
        }
    }

    private void Complete(TResource resource)
    {
        lock (_lock)
        {
            _activeResources.Remove(resource);
            if (_activeResources.Count == 0) _idle.Set();
        }
    }

    private void ReportFailure(Exception exception)
    {
        try
        {
            _reportFailure?.Invoke(exception);
        }
        catch
        {
            // 诊断回调自身失败也必须继续资源清理，不能遗留活跃计数导致停机一直等待。
        }
    }

    private void DisposeResource(TResource resource)
    {
        try
        {
            resource.Dispose();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }
}
