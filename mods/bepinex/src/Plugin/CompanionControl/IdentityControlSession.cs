using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace MystiaStewardCompanion.Plugin.CompanionControl;

internal sealed record IdentityControlOptions(string ExecutablePath, string ApiEndpoint, string ApiToken,
    Action<string> Log);

internal sealed class IdentityControlSession
{
    private enum WorkKind { Prepare, Activate, Exit }
    private sealed record Work(WorkKind Kind, ControlInput Input);
    private readonly IdentityControlOptions _options;
    private readonly Channel<Work> _work = Channel.CreateBounded<Work>(new BoundedChannelOptions(8)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _sync = new();
    private readonly ControlRequestSequence _sequence = new();
    private readonly ControlLifecycle _lifecycle = new();
    private readonly ControlExitCompletion _exit = new();
    private bool _prepared, _autoRequested, _faulted;
    private bool _workerEnded;
    private uint _updateThread;
    private SafeProcessHandle? _gameHandle, _clientHandle;
    private Process? _startedProcess;
    private ControlProcess? _game, _client;
    private NamedPipeClientStream? _pipe;
    private ControlFrame? _registration;
    private string _path = "";
    private long _handoff;
    private int _diagnosticSession;
    internal long Handoff => Interlocked.Read(ref _handoff);

    internal IdentityControlSession(IdentityControlOptions options, long initialHandoff = 0)
    {
        _options = options;
        _handoff = initialHandoff;
        _ = Task.Run(RunAsync);
    }
    // A dead retained HANDLE cannot be rebound to a recycled PID. A new
    // session requires a new real Update and a new physical action; nothing
    // from the abandoned queue is replayed.
    internal bool CanReplaceExitedClient
    {
        get
        {
            lock (_sync)
            {
                if (_lifecycle.Stopping || _clientHandle == null || _client == null) return false;
                return ControlWindows.RetainedExited(_clientHandle, _client);
            }
        }
    }
    internal void RequestAutoLaunch()
    {
        lock (_sync)
        {
            if (_autoRequested || _lifecycle.Stopping || _faulted) return;
            _autoRequested = true;
            if (_prepared) Enqueue(new Work(WorkKind.Activate, new ControlInput(ControlInputSource.AutoLaunch, 0, _updateThread)));
        }
    }
    // Invoked only from the existing Unity Update input path, never from Load/Initialize.
    internal void Prepare(uint updateThread)
    {
        lock (_sync)
        {
            if (_prepared || _lifecycle.Stopping || _faulted) return;
            ControlExitDiagnostic.Prepare(_options.ExecutablePath, updateThread, ref _diagnosticSession);
            try { NativeExitRegistration.EnsureRegistered(updateThread); }
            catch (Exception ex)
            {
                _faulted = true; _exit.Complete(ControlExitOutcome.Failed); _work.Writer.TryComplete(); _cancel.Cancel();
                Log("native_exit_registration_failed", ex is ControlFailure ? ex.Message : ex.GetType().Name, null);
                return;
            }
            _prepared = true;
            _updateThread = updateThread;
            Enqueue(new Work(WorkKind.Prepare, new ControlInput(ControlInputSource.AutoLaunch, 0, updateThread)));
            if (_autoRequested) Enqueue(new Work(WorkKind.Activate, new ControlInput(ControlInputSource.AutoLaunch, 0, updateThread)));
        }
    }
    internal void Activate(ControlInput input)
    {
        lock (_sync)
        {
            if (!_prepared || _lifecycle.Stopping || _faulted) return;
            Enqueue(new Work(WorkKind.Activate, input));
        }
    }
    internal ControlExitCompletion NotifyExit(ControlExitBudget? budget = null)
    {
        ControlExitDiagnostic.Session(_diagnosticSession, ExitSessionStage.NotifyEntered);
        lock (_sync)
        {
            _exit.Constrain(budget);
            if (budget != null && budget.Remaining == TimeSpan.Zero)
            { _exit.Complete(ControlExitOutcome.TimedOut); return _exit; }
            if (_lifecycle.Stopping) { ControlExitDiagnostic.Session(_diagnosticSession, ExitSessionStage.NotifyStopping); return _exit; }
            if (_faulted || _workerEnded)
            {
                ControlExitDiagnostic.Session(_diagnosticSession, ExitSessionStage.NotifyFaulted);
                _exit.Complete(ControlExitOutcome.Failed);
                return _exit;
            }
            if (!_exit.Start(budget)) return _exit;
            _lifecycle.Stop();
            ControlExitDiagnostic.Session(_diagnosticSession, ExitSessionStage.StopSet);
            if (!_prepared)
            {
                _exit.Complete(ControlExitOutcome.NoRegistration);
                _work.Writer.TryComplete();
                _cancel.Cancel();
                return _exit;
            }
            // A logging exception cannot prevent enqueueing; a blocked logger
            // may consume the shared deadline. Keep its order before worker ACK.
            try { Log("exit_requested", "queued", null); } catch { }
            Enqueue(new Work(WorkKind.Exit, new ControlInput(ControlInputSource.Exit, 0, _updateThread)));
            ControlExitDiagnostic.Session(_diagnosticSession, _faulted ? ExitSessionStage.QueueRejected : ExitSessionStage.Queued);
            _work.Writer.TryComplete();
            if (!_exit.Completion.IsCompleted) _cancel.CancelAfter(_exit.Remaining);
            return _exit;
        }
    }
    internal void Cancel()
    {
        ControlExitDiagnostic.Session(_diagnosticSession, ExitSessionStage.Cancelled);
        lock (_sync)
        {
            _lifecycle.Stop(); _exit.Complete(ControlExitOutcome.Cancelled); _work.Writer.TryComplete(); _cancel.Cancel();
            if (_workerEnded) { _clientHandle?.Dispose(); _clientHandle = null; }
        }
    }
    private void Enqueue(Work work)
    {
        if (_work.Writer.TryWrite(work)) return;
        _faulted = true;
        _exit.Complete(ControlExitOutcome.Failed);
        _cancel.Cancel();
        Log("queue_rejected", "capacity", work.Input);
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var work in _work.Reader.ReadAllAsync(_cancel.Token).ConfigureAwait(false))
            {
                _cancel.Token.ThrowIfCancellationRequested();
                if (work.Kind == WorkKind.Exit)
                {
                    ControlExitDiagnostic.Session(_diagnosticSession, ExitSessionStage.WorkerDequeued);
                    if (_registration == null)
                    {
                        ControlExitDiagnostic.Session(_diagnosticSession, ExitSessionStage.NoRegistration);
                        _exit.Complete(ControlExitOutcome.NoRegistration);
                    }
                    else await ExitAsync().ConfigureAwait(false);
                    return;
                }
                lock (_sync) { if (_lifecycle.Stopping) continue; }
                if (_game == null) Initialize();
                if (_registration == null)
                {
                    var connected = await RegisterAsync(work.Input.ThreadId, work.Kind == WorkKind.Activate).ConfigureAwait(false);
                    if (!connected) { Log("registration_absent", "no_listener", work.Input); continue; }
                }
                if (work.Kind == WorkKind.Activate) await ActivateAsync(work.Input).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ControlExitDiagnostic.Session(_diagnosticSession, ExitSessionStage.Failed);
            lock (_sync) _faulted = true;
            _exit.Complete(ex is OperationCanceledException && _cancel.IsCancellationRequested
                ? (_lifecycle.Stopping && !_exit.Completion.IsCompleted && _exit.Remaining == TimeSpan.Zero
                    ? ControlExitOutcome.TimedOut : ControlExitOutcome.Cancelled)
                : ControlExitOutcome.Failed);
            // Never expose exception messages, process arguments, API credentials or pipe nonce.
            Log("session_failed", ex is ControlFailure ? ex.Message : ex.GetType().Name, null);
        }
        finally
        {
            // Faulted logging or a completed channel must not strand a caller
            // waiting at the final native exit boundary.
            _exit.Complete(ControlExitOutcome.Failed);
            _work.Writer.TryComplete();
            _pipe?.Dispose(); _gameHandle?.Dispose(); _startedProcess?.Dispose();
            lock (_sync)
            {
                _workerEnded = true;
                // Keep the client identity after a fault until an explicit
                // cancellation/replacement, so retirement uses its HANDLE.
                if (_lifecycle.Stopping) { _clientHandle?.Dispose(); _clientHandle = null; }
            }
        }
    }
    private void Initialize()
    {
        ControlFrame.Require(OperatingSystem.IsWindows() && Environment.Is64BitProcess, "windows_x64_required");
        _path = ControlWindows.ConfiguredPath(_options.ExecutablePath);
        _gameHandle = ControlWindows.Open(checked((uint)Environment.ProcessId));
        _game = ControlWindows.Observe(_gameHandle);
        var sha = typeof(IdentityControlSession).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(item => item.Key == "CompanionControlBuildGitSha")?.Value ?? "unrecorded";
        Log("session_started", sha.Length == 40 && sha.All(Uri.IsHexDigit) ? sha.ToLowerInvariant() : "unrecorded", null);
    }
    private async Task<bool> RegisterAsync(uint inputThread, bool mayStart)
    {
        var pid = ControlWindows.ListenerPid();
        if (pid == 0 && !mayStart) return false;
        if (pid == 0)
        {
            ControlFrame.Require(_startedProcess == null, "launch_already_attempted");
            var start = new ProcessStartInfo { FileName = _path, WorkingDirectory = Path.GetDirectoryName(_path)!, UseShellExecute = false };
            start.ArgumentList.Add($"--api={_options.ApiEndpoint}");
            start.ArgumentList.Add($"--game-pid={_game!.Pid}");
            if (!string.IsNullOrWhiteSpace(_options.ApiToken)) start.ArgumentList.Add($"--token={_options.ApiToken}");
            lock (_sync)
            {
                if (_lifecycle.Stopping) return false;
                _cancel.Token.ThrowIfCancellationRequested();
                _startedProcess = Process.Start(start) ?? throw new ControlFailure("launch_failed");
            }
            var started = ControlWindows.Observe(_startedProcess.SafeHandle);
            ControlWindows.CheckClient(_game!, started, _path);
            var deadline = Stopwatch.StartNew();
            while (pid == 0 && deadline.Elapsed < TimeSpan.FromSeconds(8))
            {
                lock (_sync) { if (_lifecycle.Stopping) return false; }
                ControlFrame.Require(ControlWindows.Observe(_startedProcess.SafeHandle) == started, "launched_identity_changed");
                await Task.Delay(40, _cancel.Token).ConfigureAwait(false);
                pid = ControlWindows.ListenerPid();
            }
            ControlFrame.Require(pid == started.Pid, "launched_listener_mismatch");
        }
        _clientHandle = ControlWindows.Open(pid);
        _client = ControlWindows.Observe(_clientHandle);
        ControlWindows.CheckClient(_game!, _client, _path);
        ControlExitDiagnostic.BindClient(_diagnosticSession, _client.Pid, _client.Creation);
        if (_startedProcess != null)
            ControlFrame.Require(ControlWindows.Observe(_startedProcess.SafeHandle) == _client, "launched_identity_mismatch");
        var gameWindow = ControlWindows.UniqueWindow(_game!.Pid, game: true);
        ControlFrame.Require(inputThread != 0 && gameWindow.Thread == inputThread, "registration_update_thread");
        var handle = ControlWindows.OpenPipe(ControlFrame.PipeName(_client.Pid, _client.Creation));
        try
        {
            ControlFrame.Require(ControlWindows.PipeServer(handle) == _client.Pid, "pipe_server_mismatch");
            _pipe = new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
        }
        catch { handle.Dispose(); throw; }
        VerifyProcesses(requireListener: true);
        var request = new ControlFrame(ControlKind.Register).With(ControlKind.Register,
            (ControlField.GamePid, _game.Pid), (ControlField.GameCreation, _game.Creation),
            (ControlField.ClientPid, _client.Pid), (ControlField.ClientCreation, _client.Creation),
            (ControlField.GameHwnd, gameWindow.Hwnd), (ControlField.GameThread, gameWindow.Thread),
            (ControlField.InputThread, inputThread));
        Task<ControlFrame> exchange;
        lock (_sync)
        {
            if (_lifecycle.Stopping) return false;
            exchange = ExchangeAsync(request);
        }
        var reply = await exchange.ConfigureAwait(false);
        ControlFrame.ValidateRegistered(request, reply);
        _registration = reply.With(ControlKind.Registered, (ControlField.Status, 0));
        VerifyWindows();
        Log("registered", "acknowledged", new ControlInput(ControlInputSource.AutoLaunch, 0, inputThread));

        return true;
    }
    private void VerifyProcesses(bool requireListener)
    {
        ControlFrame.Require(ControlWindows.Observe(_gameHandle!) == _game && ControlWindows.Observe(_clientHandle!) == _client,
            "retained_identity_changed");
        ControlFrame.Require(ControlWindows.PipeServer(_pipe!.SafePipeHandle) == _client!.Pid, "pipe_peer_changed");
        ControlWindows.RequireQuietPipe(_pipe.SafePipeHandle);
        if (requireListener) ControlFrame.Require(ControlWindows.ListenerPid() == _client.Pid, "listener_changed");
    }
    private void VerifyWindows()
    {
        VerifyProcesses(requireListener: true);
        var game = ControlWindows.UniqueWindow(_game!.Pid, true);
        var client = ControlWindows.UniqueWindow(_client!.Pid, false);
        ControlFrame.Require(game.Hwnd == _registration![ControlField.GameHwnd] && game.Thread == _registration[ControlField.GameThread] &&
            client.Hwnd == _registration[ControlField.ClientHwnd] && client.Thread == _registration[ControlField.ClientThread], "bound_window_changed");
    }
    private async Task ActivateAsync(ControlInput input)
    {
        ControlFrame request;
        Task<ControlFrame> exchange;
        ulong requestId;
        lock (_sync)
        {
            if (_lifecycle.Stopping) return;
            VerifyWindows();
            var foreground = ControlWindows.Foreground();
            if (foreground.Hwnd != _registration![ControlField.GameHwnd] || foreground.Pid != _game!.Pid ||
                (input.Source != ControlInputSource.AutoLaunch &&
                    !input.IsCurrent(foreground.Hwnd, foreground.Pid, Handoff)))
            {
                Log("input_rejected", "foreground_changed", input);
                return;
            }
            requestId = _sequence.Next(input, checked((uint)_registration[ControlField.GameThread]));
            _cancel.Token.ThrowIfCancellationRequested();
            var grant = ControlWindows.Allow(_client!.Pid);
            Log("foreground_grant", grant.Succeeded ? "success" : "rejected", input, requestId, grant.Error);
            ControlFrame.Require(grant.Succeeded, "foreground_grant_failed");
            request = _registration.With(ControlKind.Activate,
                (ControlField.RequestId, requestId), (ControlField.Source, (ulong)input.Source),
                (ControlField.ForegroundBeforeHwnd, foreground.Hwnd), (ControlField.ForegroundBeforePid, foreground.Pid),
                (ControlField.AllowAttempted, 1), (ControlField.AllowSucceeded, 1), (ControlField.AllowError, grant.Error),
                (ControlField.InputSequence, input.Sequence), (ControlField.InputThread, input.ThreadId));
            _lifecycle.BeginActivation(request);
            // WriteAsync starts under the same gate as NotifyExit. Once committed, only observe its ACK; never resend.
            exchange = ExchangeAsync(request);
        }
        var reply = await exchange.ConfigureAwait(false);
        ControlFrame observed;
        lock (_sync) observed = _lifecycle.ObserveActivation(reply);
        VerifyWindows();
        ControlWindows.VerifyActivation(reply);
        Interlocked.Increment(ref _handoff);
        // This is completion of the already committed activation, including when NotifyExit arrived meanwhile.
        // It carries the exact observed ACK, makes no new foreground request and expects no additional ACK.
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await _pipe!.WriteAsync(observed.Encode(), timeout.Token).ConfigureAwait(false);
            await _pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        lock (_sync) _lifecycle.CompleteObservation();
        Log("activated", "acknowledged", input, requestId);
    }
    private async Task ExitAsync()
    {
        lock (_sync) ControlFrame.Require(_lifecycle.CanExit, "exit_pending_activation");
        VerifyProcesses(requireListener: false);
        var request = _registration!.With(ControlKind.Exit,
            (ControlField.RequestId, _sequence.Exit()), (ControlField.Source, (ulong)ControlInputSource.Exit));
        var reply = await ExchangeAsync(request).ConfigureAwait(false);
        ControlFrame.ValidateAcknowledgement(request, reply);
        ControlExitDiagnostic.Session(_diagnosticSession, ExitSessionStage.AckValidated);
        try { Log("exit_notification", "acknowledged", null, request[ControlField.RequestId]); }
        finally { _exit.Complete(ControlExitOutcome.Acknowledged); }
    }
    private async Task<ControlFrame> ExchangeAsync(ControlFrame frame)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(frame.Kind == ControlKind.Activate ? 6 : 3));
        if (frame.Kind == ControlKind.Exit) ControlExitDiagnostic.Session(_diagnosticSession, ExitSessionStage.WriteStarted);
        await _pipe!.WriteAsync(frame.Encode(), timeout.Token).ConfigureAwait(false);
        await _pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        if (frame.Kind == ControlKind.Exit) ControlExitDiagnostic.Session(_diagnosticSession, ExitSessionStage.WriteCompleted);
        return await ControlFrame.ReadAsync(_pipe, timeout.Token).ConfigureAwait(false);
    }
    private void Log(string action, string outcome, ControlInput? input, ulong request = 0, uint error = 0)
    {
#if COMPANION_CONTROL_EXIT_DIAGNOSTIC
        try
        {
#endif
        _options.Log($"companion_control protocol=IdentityPipeV1 event={action} outcome={outcome} gamePid={_game?.Pid ?? 0} clientPid={_client?.Pid ?? 0} requestId={request} source={input?.Source.ToString() ?? "none"} inputSequence={input?.Sequence ?? 0} inputThread={input?.ThreadId ?? 0} keyboardHeld={input?.KeyboardHeld ?? false} legacyHeld={input?.LegacyHeld ?? false} legacyPressed={input?.LegacyPressed ?? false} inputSystemHeld={input?.InputSystemHeld ?? false} inputSystemPressed={input?.InputSystemPressed ?? false} win32Error={error}");
#if COMPANION_CONTROL_EXIT_DIAGNOSTIC
        }
        catch { ControlExitDiagnostic.Session(_diagnosticSession, ExitSessionStage.LogThrew); throw; }
#endif
    }
}
