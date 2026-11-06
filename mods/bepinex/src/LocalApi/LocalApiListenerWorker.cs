using System.Net;
using System.Net.Sockets;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>
/// 持有一个 TCP 监听器及其阻塞接收线程，停止时关闭监听器以唤醒 AcceptTcpClient。
/// </summary>
internal sealed class LocalApiListenerWorker : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Thread _thread;
    private readonly Action<TcpClient> _clientAccepted;
    private readonly Action<LocalApiListenerWorker, Exception> _unexpectedFailure;
    private int _stopRequested;
    private int _failureReported;
    private bool _started;

    /// <summary>构造时绑定并开始监听端口；接收线程由 Start 单独启动，构造失败会释放已创建的监听器。</summary>
    public LocalApiListenerWorker(
        string name,
        bool isLan,
        IPAddress bindAddress,
        int port,
        string threadName,
        Action<TcpClient> clientAccepted,
        Action<LocalApiListenerWorker, Exception> unexpectedFailure)
    {
        Name = name;
        IsLan = isLan;
        BindAddress = bindAddress;
        _clientAccepted = clientAccepted;
        _unexpectedFailure = unexpectedFailure;
        _listener = new TcpListener(bindAddress, port);
        try
        {
            _listener.Start();
            _thread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = threadName,
            };
        }
        catch
        {
            _listener.Stop();
            throw;
        }
    }

    public string Name { get; }
    public bool IsLan { get; }
    public IPAddress BindAddress { get; }
    public bool IsAlive => _thread.IsAlive;

    public void Start()
    {
        if (_started) throw new InvalidOperationException($"Local API {Name} listener worker has already started.");
        if (IsStopRequested) throw new ObjectDisposedException(nameof(LocalApiListenerWorker));

        _started = true;
        try
        {
            _thread.Start();
        }
        catch
        {
            RequestStop();
            throw;
        }
    }

    /// <summary>请求停止并限时等待接收线程退出；从接收线程自身调用时不等待自身，返回 false。</summary>
    public bool Stop(TimeSpan joinTimeout)
    {
        if (joinTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(joinTimeout));

        RequestStop();
        if (!_started || !_thread.IsAlive) return true;
        if (ReferenceEquals(Thread.CurrentThread, _thread)) return false;
        return _thread.Join(joinTimeout);
    }

    public void Dispose()
    {
        Stop(TimeSpan.FromSeconds(2));
    }

    private bool IsStopRequested => Volatile.Read(ref _stopRequested) != 0;

    private void ListenLoop()
    {
        try
        {
            while (!IsStopRequested)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception) when (IsStopRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    ReportUnexpectedFailure(ex);
                    return;
                }

                if (IsStopRequested)
                {
                    client.Dispose();
                    return;
                }

                try
                {
                    _clientAccepted(client);
                }
                catch (Exception ex)
                {
                    client.Dispose();
                    ReportUnexpectedFailure(ex);
                    return;
                }
            }
        }
        finally
        {
            RequestStop();
        }
    }

    private void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;

        try
        {
            _listener.Stop();
        }
        catch
        {
            // 停止请求保持幂等；关闭期间的异常不应作为意外监听故障重复上报。
        }
    }

    private void ReportUnexpectedFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _failureReported, 1) != 0) return;

        try
        {
            _unexpectedFailure(this, exception);
        }
        catch
        {
            // 即使故障通知回调也失败，接收线程仍沿原路径结束并释放监听器。
        }
    }
}
