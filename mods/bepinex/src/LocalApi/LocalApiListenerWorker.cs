using System.Net;
using System.Net.Sockets;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>
/// Owns one TCP listener and its blocking accept thread.
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
            // Stop is idempotent and must not turn shutdown into a listener failure.
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
            // The worker still terminates when the diagnostic callback itself fails.
        }
    }
}
