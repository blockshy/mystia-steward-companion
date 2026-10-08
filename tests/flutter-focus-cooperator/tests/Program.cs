using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MystiaStewardCompanion.FocusProbe;

var passed = 0;
try
{
    Run("descriptor strict shape, types, identity and local pipe", DescriptorCases);
    Run("176-byte little-endian frame and zero ready windows", FrameCases);
    Run("three unique grants echo identity and grant once", SuccessfulSequence);
    Run("every identity/window/foreground mismatch stops before ASFW", IdentityRejectionCases);
    Run("nonce, request, sequence, reserved and result-field rejection", RequestRejectionCases);
    Run("replayed request permanently stops the session", ReplayStops);
    Run("ASFW false cannot be followed by another grant", NativeFailureStops);
    Run("foreground changes after ASFW remain a failure", ForegroundChangeStops);
    Run("identity change immediately before ASFW permanently stops", LateFailureStops);
    Run("incomplete session cannot claim completion", IncompleteSession);
    await RunAsync("fragmented frame transport and boundary EOF", FragmentedFrames);
    await RunAsync("partial frame EOF and cancellation fail closed", PartialFrameAndCancellation);
    await RunAsync("ready, three grants and EOF require later Unity heartbeats", HeartbeatProgression);
    await RunAsync("missing or stalled Unity heartbeat times out", HeartbeatTimeouts);
    Run("wrong heartbeat thread or nonadvancing clock stops publication", HeartbeatIdentityFailures);
    Run("ASFW heartbeat guard binds the exact game window thread", HeartbeatFinalGuard);
    await RunAsync("heartbeat cancellation and component destruction fail closed", HeartbeatCancellation);
    Console.WriteLine($"PASS: {passed} foreground grant contract groups; pure managed model and bytes only, no Win32/game evidence.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }

void Run(string name, Action test) { test(); passed++; Console.WriteLine("PASS: " + name); }
async Task RunAsync(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS: " + name); }

static SessionDescriptor Descriptor() => SessionDescriptor.Parse(DescriptorBytes(), "focus_run-1", new string('a', 40));
static byte[] DescriptorBytes(Action<Dictionary<string, object>>? mutate = null)
{
    var values = new Dictionary<string, object>
    {
        ["schemaVersion"] = 1, ["runId"] = "focus_run-1", ["gitSha"] = new string('a', 40),
        ["pipeName"] = "mystia-focus-focus_run-1-0123456789abcdef1123456789abcdef", ["nonceHex"] = "0123456789abcdef1123456789abcdef",
        ["gamePid"] = "101", ["gameCreationHex"] = "1dd123456789abc", ["probePid"] = "202", ["probeCreationHex"] = "1dd1123456789ab",
        ["probeHwnd"] = "3001", ["probeThreadId"] = "404", ["probeExeSha256"] = new string('b', 64),
    };
    mutate?.Invoke(values); return JsonSerializer.SerializeToUtf8Bytes(values);
}
static GrantObservation Observation()
{
    var d = Descriptor();
    return new GrantObservation(new ProcessObservation(d.GamePid, d.GameCreation, @"D:\run\game.exe", 1, true),
        new ProcessObservation(d.ProbePid, d.ProbeCreation, @"D:\run\probe.exe", 1, true), d.ProbePid,
        new WindowObservation(5001, d.GamePid, 505), 1,
        new WindowObservation(d.ProbeHwnd, d.ProbePid, d.ProbeThreadId), new WindowObservation(5001, d.GamePid, 505));
}
static GrantSession Session() => new(Descriptor(), @"D:\run\game.exe", @"D:\run\probe.exe");
static GrantFrame Request(ulong sequence = 1, ulong requestId = 2, Action<ulong[]>? mutate = null)
{
    var fields = GrantFrame.Ready(Descriptor()).CopyFields(); var observation = Observation();
    fields[6] = sequence; fields[7] = requestId; fields[8] = observation.GameWindow.Hwnd;
    fields[9] = observation.ProbeWindow.Hwnd; fields[10] = observation.GameWindow.ThreadId; fields[11] = observation.ProbeWindow.ThreadId;
    mutate?.Invoke(fields); return new GrantFrame(2, fields);
}
static GrantFrame Grant(GrantSession session, GrantFrame request, ref int calls, GrantObservation? observation = null)
{
    var localCalls = 0;
    try { return session.Grant(request, observation ?? Observation(), pid => { Equal(202U, pid, "Only exact Flutter PID may receive ASFW."); localCalls++; return new GrantCallResult(true, 0); }, () => Observation().Foreground); }
    finally { calls += localCalls; }
}
static void DescriptorCases()
{
    var descriptor = Descriptor(); Equal(0x0123456789abcdefUL, descriptor.NonceLo, "nonceLo text order"); Equal(0x1123456789abcdefUL, descriptor.NonceHi, "nonceHi text order");
    var edits = new Action<Dictionary<string, object>>[] {
        v => v["extra"] = "ignored?", v => v.Remove("gamePid"), v => v["schemaVersion"] = "1", v => v["gamePid"] = 101,
        v => v["gamePid"] = "0101", v => v["gamePid"] = "0", v => v["gamePid"] = "4294967296", v => v["probePid"] = "101",
        v => v["runId"] = "another-run", v => v["gitSha"] = new string('c', 40), v => v["nonceHex"] = new string('0', 32),
        v => v["pipeName"] = @"\\evil\pipe\focus_run-1", v => v["pipeName"] = "mystia-focus-focus_run-1",
        v => v["pipeName"] = "mystia-focus-别的机器", v => v["gameCreationHex"] = "ABC", v => v["probeHwnd"] = "18446744073709551615",
        v => v["probeExeSha256"] = new string('B', 64),
    };
    foreach (var edit in edits) Throws(() => SessionDescriptor.Parse(DescriptorBytes(edit), "focus_run-1", new string('a', 40)), "Invalid descriptor accepted.");
    var duplicate = Encoding.UTF8.GetString(DescriptorBytes()).Replace("{", "{\"runId\":\"focus_run-1\",", StringComparison.Ordinal);
    Throws(() => SessionDescriptor.Parse(Encoding.UTF8.GetBytes(duplicate), "focus_run-1", new string('a', 40)), "Duplicate property accepted.");
    Throws(() => SessionDescriptor.Parse(new byte[8193], "focus_run-1", new string('a', 40)), "Oversized descriptor accepted.");
}
static void FrameCases()
{
    var ready = GrantFrame.Ready(Descriptor()); var bytes = ready.Encode();
    Equal(176, bytes.Length, "fixed byte count"); Equal("4746534D0100000001000000B0000000", Convert.ToHexString(bytes.AsSpan(0, 16)), "little-endian header");
    Equal("EFCDAB8967452301", Convert.ToHexString(bytes.AsSpan(16, 8)), "nonce little endian bytes");
    for (var i = 6; i < 20; i++) Equal(0UL, GrantFrame.Decode(bytes)[i], "ready nonidentity fields zero");
    foreach (var offset in new[] { 0, 4, 8, 12, 175 })
    {
        var damaged = (byte[])bytes.Clone(); damaged[offset] ^= 0x80;
        Throws(() => GrantFrame.Decode(damaged), "Damaged header/reserved accepted.");
    }
    Throws(() => GrantFrame.Decode(bytes.AsSpan(0, 175)), "Short frame accepted.");
    Throws(() => GrantFrame.Decode(bytes.Concat(new byte[] { 0 }).ToArray()), "Extra frame byte accepted.");
}
static void SuccessfulSequence()
{
    var session = Session(); var calls = 0;
    for (ulong index = 1; index <= 3; index++)
    {
        var request = Request(index, index * 4); var reply = Grant(session, request, ref calls);
        Equal(3U, reply.Kind, "reply kind"); for (var field = 0; field < 12; field++) Equal(request[field], reply[field], "echoed identity/request");
        Equal(1UL, reply[16], "ASFW attempted"); Equal(1UL, reply[17], "ASFW succeeded"); Equal(0UL, reply[18], "ASFW last error");
        Equal(5001UL, reply[12], "foreground before HWND"); Equal(reply[12], reply[14], "foreground unchanged across grant");
    }
    session.Complete(); Equal(3, calls, "one ASFW per grant"); Equal(3UL, session.SuccessfulGrants, "grant count");
    Throws(() => Grant(session, Request(4, 20), ref calls), "Fourth grant accepted."); Equal(3, calls, "Fourth request must not call ASFW.");
}
static void IdentityRejectionCases()
{
    var original = Observation();
    var observations = new[] {
        original with { PeerPid = 909 }, original with { Game = original.Game with { Pid = 909 } }, original with { Game = original.Game with { Creation = 1 } },
        original with { Game = original.Game with { ImagePath = @"D:\source\game.exe" } }, original with { Game = original.Game with { Alive = false } },
        original with { Probe = original.Probe with { Pid = 909 } }, original with { Probe = original.Probe with { Creation = 1 } },
        original with { Probe = original.Probe with { ImagePath = @"D:\other\probe.exe" } }, original with { Probe = original.Probe with { Alive = false } },
        original with { Probe = original.Probe with { Session = 2 } }, original with { ProbeWindow = original.ProbeWindow with { Hwnd = 11 } },
        original with { ProbeWindow = original.ProbeWindow with { Pid = 11 } }, original with { ProbeWindow = original.ProbeWindow with { ThreadId = 11 } },
        original with { GameWindowCount = 0 }, original with { GameWindowCount = 2 }, original with { GameWindow = original.GameWindow with { Hwnd = 0 } },
        original with { GameWindow = original.GameWindow with { Pid = 11 } }, original with { GameWindow = original.GameWindow with { ThreadId = 11 } },
        original with { Foreground = original.Foreground with { Hwnd = 999 } }, original with { Foreground = original.Foreground with { Pid = 999 } },
    };
    foreach (var observation in observations)
    {
        var session = Session(); var calls = 0; Throws(() => Grant(session, Request(), ref calls, observation), "Invalid OS identity accepted.");
        Equal(0, calls, "Invalid identity reached ASFW."); Equal(true, session.IsFailed, "Failure must poison the session.");
    }
}
static void RequestRejectionCases()
{
    for (var index = 0; index < 19; index++)
    {
        var field = index; var session = Session(); var calls = 0;
        // A later requestId is legal; use zero to test its range instead.
        var bad = Request(mutate: values => values[field] = field == 7 ? 0 : values[field] + 1);
        Throws(() => Grant(session, bad, ref calls), "Changed request identity/result field accepted."); Equal(0, calls, "Invalid frame reached ASFW.");
    }
    var wrongKind = new GrantFrame(1, Request().CopyFields()); var kindSession = Session(); var count = 0;
    Throws(() => Grant(kindSession, wrongKind, ref count), "Ready message used as request.");
    Equal(0, count, "Wrong kind reached ASFW.");
}
static void ReplayStops()
{
    var session = Session(); var calls = 0; Grant(session, Request(), ref calls);
    Throws(() => Grant(session, Request(), ref calls), "Replay accepted.");
    Throws(() => Grant(session, Request(2, 3), ref calls), "Session recovered after replay."); Equal(1, calls, "Replay/follow-up reached ASFW.");
    var stale = Session(); calls = 0; Grant(stale, Request(), ref calls);
    Throws(() => Grant(stale, Request(2, 2), ref calls), "Stale requestId accepted with new sequence."); Equal(1, calls, "Stale ID reached ASFW.");
}
static void NativeFailureStops()
{
    var session = Session(); var calls = 0;
    var reply = session.Grant(Request(), Observation(), _ => { calls++; return new GrantCallResult(false, 5); }, () => Observation().Foreground);
    Equal(1UL, reply[16], "attempt preserved"); Equal(0UL, reply[17], "native failure preserved"); Equal(5UL, reply[18], "native error preserved");
    Throws(() => Grant(session, Request(2, 3), ref calls), "Retry after native denial accepted."); Equal(1, calls, "Denial caused another call."); Throws(session.Complete, "Denied session completed.");
}
static void ForegroundChangeStops()
{
    var session = Session(); var calls = 0;
    var reply = session.Grant(Request(), Observation(), _ => { calls++; return new GrantCallResult(true, 0); }, () => new WindowObservation(777, 888, 999));
    Equal(1UL, reply[17], "Actual ASFW return preserved."); Equal(777UL, reply[14], "Changed foreground preserved."); Equal(0UL, session.SuccessfulGrants, "Changed foreground counted as valid grant.");
    Throws(() => Grant(session, Request(2, 3), ref calls), "Foreground loss was retried."); Equal(1, calls, "Unexpected grant after foreground loss.");
}
static void LateFailureStops()
{
    var session = Session(); var calls = 0;
    Throws(() => session.Grant(Request(), Observation(), _ => throw new InvalidDataException("Peer exited in final native check."), () => Observation().Foreground), "Late failure ignored.");
    Throws(() => Grant(session, Request(2, 3), ref calls), "Late failure was retried."); Equal(0, calls, "Late guard failure reached later ASFW.");
}
static void IncompleteSession()
{
    for (var count = 0; count < 3; count++)
    {
        var session = Session(); var calls = 0;
        for (ulong i = 1; i <= (ulong)count; i++) Grant(session, Request(i, i + 1), ref calls);
        Throws(session.Complete, "Incomplete session claimed PASS.");
    }
}
static async Task FragmentedFrames()
{
    var bytes = Request().Encode(); using var stream = new FragmentStream(bytes);
    var buffer = new byte[176]; Equal(true, await GrantFrame.ReadExactlyAsync(stream, buffer, CancellationToken.None, true), "Fragmented frame read");
    Equal(Convert.ToHexString(bytes), Convert.ToHexString(buffer), "Fragmented payload differs.");
    Equal(false, await GrantFrame.ReadExactlyAsync(stream, buffer, CancellationToken.None, true), "Boundary EOF expected.");
}
static async Task PartialFrameAndCancellation()
{
    using var stream = new FragmentStream(new byte[175]);
    await ThrowsAsync(() => GrantFrame.ReadExactlyAsync(stream, new byte[176], CancellationToken.None, true), "Partial EOF accepted.");
    using var empty = new MemoryStream(); await ThrowsAsync(() => GrantFrame.ReadExactlyAsync(empty, new byte[176], CancellationToken.None, false), "Required frame missing.");
    using var data = new MemoryStream(new byte[176]); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    await ThrowsAsync(() => GrantFrame.ReadExactlyAsync(data, new byte[176], cancellation.Token, true), "Cancellation ignored.");
}
static async Task HeartbeatProgression()
{
    var state = new HeartbeatState(505, 1000);
    var zero = state.Snapshot;
    var readyWait = state.WaitForAdvanceAsync(zero, TimeSpan.FromSeconds(1), CancellationToken.None);
    Equal(false, readyWait.IsCompleted, "No first Update must mean no ready.");
    state.Publish(505, 100);
    var ready = await readyWait;
    Equal(0L, zero.Sequence, "Published snapshots must remain immutable.");
    Equal(1L, ready.Sequence, "First Update sequence");
    var previous = ready;
    for (var stage = 0; stage < 4; stage++) // Three request receipts, then EOF.
    {
        var baseline = state.Snapshot;
        Equal(previous, baseline, "Each stage retains its actual receipt baseline.");
        var waiting = state.WaitForAdvanceAsync(baseline, TimeSpan.FromSeconds(1), CancellationToken.None);
        Equal(false, waiting.IsCompleted, "A previous heartbeat cannot satisfy a later request.");
        state.Publish(505, 200 + stage * 100);
        var observed = await waiting;
        Equal(baseline.Sequence + 1, observed.Sequence, "Each stage requires a new Update.");
        Equal(true, observed.Ticks > baseline.Ticks, "Each stage requires new monotonic ticks.");
        Equal(observed, state.RequireCurrent(observed, 505), "Exact game thread accepts retained progress.");
        previous = observed;
    }
}
static async Task HeartbeatTimeouts()
{
    var state = new HeartbeatState(505, 1000);
    await ThrowsAsync(() => state.WaitForAdvanceAsync(state.Snapshot, TimeSpan.FromMilliseconds(30), CancellationToken.None), "No Update was accepted as ready.");
    state.Publish(505, 100);
    await ThrowsAsync(() => state.WaitForAdvanceAsync(state.Snapshot, TimeSpan.FromMilliseconds(30), CancellationToken.None), "Stale Update was accepted for another stage.");
}
static void HeartbeatIdentityFailures()
{
    Throws(() => new HeartbeatState(0, 1000), "Zero thread accepted.");
    Throws(() => new HeartbeatState(505, 0), "Zero frequency accepted.");
    foreach (var invalid in new[] { (Thread: 506U, Ticks: 200L), (Thread: 505U, Ticks: 100L), (Thread: 505U, Ticks: 99L) })
    {
        var state = new HeartbeatState(505, 1000); state.Publish(505, 100); var before = state.Snapshot;
        state.Publish(invalid.Thread, invalid.Ticks);
        Throws(() => state.RequireCurrent(before, 505), "Invalid callback identity/clock did not stop the state.");
        var stopped = state.Snapshot; state.Publish(505, 300);
        Equal(stopped, state.Snapshot, "A later callback must not revive a stopped heartbeat.");
    }
}
static void HeartbeatFinalGuard()
{
    var state = new HeartbeatState(505, 1000); state.Publish(505, 100); var retained = state.Snapshot;
    Throws(() => state.RequireCurrent(retained, 506), "Another game window thread reached ASFW.");
    Throws(() => state.RequireCurrent(retained with { Sequence = 2, Ticks = 200 }, 505), "Future heartbeat was accepted.");
    Throws(() => state.RequireCurrent(retained with { ThreadId = 506 }, 505), "Other-thread baseline accepted.");
    Throws(() => state.RequireCurrent(retained with { Sequence = 0 }, 505), "Mismatched zero baseline accepted.");
    state.Publish(505, 200); Equal(2L, state.RequireCurrent(retained, 505).Sequence, "Newer valid progress is permitted before the one ASFW.");
    state.Stop("Component destroyed before ASFW.");
    Throws(() => state.RequireCurrent(retained, 505), "Stopped component reached ASFW.");
}
static async Task HeartbeatCancellation()
{
    var state = new HeartbeatState(505, 1000);
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    await ThrowsAsync(() => state.WaitForAdvanceAsync(state.Snapshot, TimeSpan.FromSeconds(1), cancelled.Token), "Pre-cancellation ignored.");
    using var pendingCancellation = new CancellationTokenSource();
    var pending = state.WaitForAdvanceAsync(state.Snapshot, TimeSpan.FromSeconds(1), pendingCancellation.Token);
    pendingCancellation.Cancel(); await ThrowsAsync(() => pending, "In-flight cancellation ignored.");
    var destroyed = state.WaitForAdvanceAsync(state.Snapshot, TimeSpan.FromSeconds(1), CancellationToken.None);
    state.Stop("Unity heartbeat component was destroyed.");
    await ThrowsAsync(() => destroyed, "Component destruction was accepted as progress.");
}
static void Equal<T>(T expected, T actual, string message) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: expected {expected}, actual {actual}."); }
static void Throws(Action test, string message) { try { test(); } catch (Exception ex) when (ex is InvalidDataException or JsonException or OverflowException or EndOfStreamException) { return; } throw new Exception(message); }
static async Task ThrowsAsync(Func<Task> test, string message) { try { await test(); } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OperationCanceledException) { return; } throw new Exception(message); }

sealed class FragmentStream : MemoryStream
{
    public FragmentStream(byte[] bytes) : base(bytes) { }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
}
