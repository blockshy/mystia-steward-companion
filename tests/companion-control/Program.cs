using System.Buffers.Binary;
using MystiaStewardCompanion.Plugin.CompanionControl;

var passed = 0;
void Check(bool value) { if (!value) throw new Exception("Assertion failed."); }
void Reject(Action action)
{
    try { action(); } catch (ControlFailure) { return; }
    throw new Exception("Malformed control contract was accepted.");
}
void Test(string name, Action action) { action(); passed++; Console.WriteLine($"PASS {name}"); }
async Task TestAsync(string name, Func<Task> action) { await action(); passed++; Console.WriteLine($"PASS {name}"); }

Test("diagnostic page binds exact build/run/process and bounded immutable header", () =>
{
    var page = new byte[4096];
    void Word(int index, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(index * 8, 8), value);
    void Text(int offset, string value) => System.Text.Encoding.ASCII.GetBytes(value).CopyTo(page, offset);
    foreach (var pair in new Dictionary<int, ulong> { [0] = ControlExitDiagnosticContract.Magic, [1] = ControlExitDiagnosticContract.Version, [2] = 4096,
        [3] = 512, [4] = 101, [5] = 1001, [6] = 202, [7] = 2002, [8] = 1, [9] = 123, [10] = 456, [11] = 2 }) Word(pair.Key, pair.Value);
    Text(128, new string('a', 40)); Text(168, "synthetic-only");
    foreach (var offset in new[] { 248, 312, 376, 440 }) Text(offset, new string('b', 64));
    var header = ControlExitDiagnosticContract.ValidateHeader(page, 101, 1001, new string('a', 40), "synthetic-only");
    Check(header.ControllerPid == 202 && header.ControllerCreation == 2002 && header.NonceLow == 123);
    Check(ControlExitDiagnosticContract.MappingName(101, 1001) == @"Local\mystia-steward-companion.exitdiag.v3.101.3e9");
    foreach (var offset in new[] { 0, 8, 16, 24, 32, 40, 88, 96, 128, 168, 248, 504 })
    { var wrong = page.ToArray(); wrong[offset] = 0xff; Reject(() => ControlExitDiagnosticContract.ValidateHeader(wrong, 101, 1001, new string('a', 40), "synthetic-only")); }
    foreach (var offset in new[] { 72, 80 })
    { var wrong = page.ToArray(); Array.Clear(wrong, offset, 8); Reject(() => ControlExitDiagnosticContract.ValidateHeader(wrong, 101, 1001, new string('a', 40), "synthetic-only")); }
    Reject(() => ControlExitDiagnosticContract.ValidateHeader(page[..4095], 101, 1001, new string('a', 40), "synthetic-only"));
    Reject(() => ControlExitDiagnosticContract.ValidateHeader(page, 101, 1001, new string('c', 40), "synthetic-only"));
    Reject(() => ControlExitDiagnosticContract.SessionBase(3));
});
Test("duplicate exit notifications reuse one task and cannot restart the monotonic deadline", () =>
{
    long now = 1000; var exit = new ControlExitCompletion(() => now, 1000);
    var sameTask = exit.Completion;
    Check(exit.Start() && exit.Remaining == TimeSpan.FromSeconds(13));
    now += 12000;
    Check(!exit.Start() && ReferenceEquals(sameTask, exit.Completion) && exit.Remaining == TimeSpan.FromSeconds(1));
    now += 1000;
    Check(exit.Wait() == ControlExitOutcome.TimedOut && !exit.Start());
    Check(!exit.Complete(ControlExitOutcome.Acknowledged) && exit.Wait() == ControlExitOutcome.TimedOut);
});
Test("completion requires a request and rejects an ACK arriving at or beyond its deadline", () =>
{
    long now = 100; var exit = new ControlExitCompletion(() => now, 10);
    Reject(() => exit.Complete(ControlExitOutcome.Acknowledged)); Reject(() => exit.Wait());
    Check(exit.Start()); now += 130;
    Check(exit.Complete(ControlExitOutcome.Acknowledged) && exit.Wait() == ControlExitOutcome.TimedOut);
    var before = new ControlExitCompletion(() => now, 10); Check(before.Start()); now += 129;
    Check(before.Complete(ControlExitOutcome.Acknowledged) && before.Wait() == ControlExitOutcome.Acknowledged);
});
Test("fault, cancellation and absence are terminal without waiting for a future worker", () =>
{
    foreach (var outcome in new[] { ControlExitOutcome.Failed, ControlExitOutcome.Cancelled,
        ControlExitOutcome.NoRegistration, ControlExitOutcome.NoSession, ControlExitOutcome.NotApplicable })
    {
        var exit = new ControlExitCompletion();
        Check(exit.Complete(outcome) && exit.Wait() == outcome && !exit.Start());
        Check(!exit.Complete(ControlExitOutcome.Acknowledged) && exit.Wait() == outcome);
    }
});
Test("a regressed monotonic observation cannot prolong native shutdown", () =>
{
    long now = 1000; var exit = new ControlExitCompletion(() => now, 10);
    Check(exit.Start()); now = 999;
    Check(exit.Remaining == TimeSpan.Zero && exit.Wait() == ControlExitOutcome.TimedOut);
    Reject(() => new ControlExitCompletion(() => 0, 0));
    Reject(() => new ControlExitCompletion(() => -1, 1).Start());
});
Test("entry budget constrains a late session start and duplicate notification cannot extend either deadline", () =>
{
    long now = 0; var entry = new ControlExitBudget(TimeSpan.FromSeconds(3), () => now, 1000);
    now = 2000; var exit = new ControlExitCompletion(() => now, 1000);
    Check(exit.Start(entry) && exit.Remaining == TimeSpan.FromSeconds(1));
    now = 3000; Check(exit.Complete(ControlExitOutcome.Acknowledged));
    Check(exit.Wait() == ControlExitOutcome.TimedOut);
    var prior = new ControlExitCompletion(() => now, 1000); Check(prior.Start());
    now += 12000; var later = new ControlExitBudget(TimeSpan.FromSeconds(13), () => now, 1000);
    Check(!prior.Start(later) && prior.Remaining == TimeSpan.FromSeconds(1));
    now += 1000; Check(prior.Wait(later) == ControlExitOutcome.TimedOut);
});
Test("an expired native dispatch never executes its queued action", () =>
{
    long now = 0; var entry = new ControlExitBudget(TimeSpan.FromSeconds(1), () => now, 1000); now = 1000;
    var called = false;
    Check(ControlExitDispatch.Run(entry, _ => { called = true; return ControlExitOutcome.Acknowledged; }) == ControlExitOutcome.TimedOut && !called);
});
Test("blocked dispatch logging cannot block the native caller beyond its budget or resume a late operation", () =>
{
    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var ended = new ManualResetEventSlim();
    var caller = 0; var logger = 0; var operations = 0;
    var pending = Task.Factory.StartNew(() =>
    {
        caller = Environment.CurrentManagedThreadId;
        return ControlExitDispatch.Run(new ControlExitBudget(TimeSpan.FromMilliseconds(500), System.Diagnostics.Stopwatch.GetTimestamp,
            System.Diagnostics.Stopwatch.Frequency), remaining =>
        {
            try
            {
                logger = Environment.CurrentManagedThreadId; entered.Set(); release.Wait();
                if (remaining.Remaining == TimeSpan.Zero) return ControlExitOutcome.TimedOut;
                Interlocked.Increment(ref operations); return ControlExitOutcome.Acknowledged;
            }
            finally { ended.Set(); }
        });
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    try
    {
        Check(entered.Wait(TimeSpan.FromSeconds(2)) && pending.Wait(TimeSpan.FromSeconds(2)));
        Check(pending.Result == ControlExitOutcome.TimedOut && caller != logger && operations == 0);
    }
    finally { release.Set(); }
    Check(ended.Wait(TimeSpan.FromSeconds(2)) && operations == 0);
});
Test("blocked product lock remains on a worker and cannot start work after native deadline", () =>
{
    var sync = new object(); using var entered = new ManualResetEventSlim(); using var ended = new ManualResetEventSlim();
    var operations = 0;
    Monitor.Enter(sync);
    try
    {
        var pending = Task.Factory.StartNew(() => ControlExitDispatch.Run(
            new ControlExitBudget(TimeSpan.FromMilliseconds(500), System.Diagnostics.Stopwatch.GetTimestamp, System.Diagnostics.Stopwatch.Frequency),
            remaining =>
            {
                try
                {
                    entered.Set();
                    lock (sync)
                    {
                        if (remaining.Remaining == TimeSpan.Zero) return ControlExitOutcome.TimedOut;
                        Interlocked.Increment(ref operations); return ControlExitOutcome.Acknowledged;
                    }
                }
                finally { ended.Set(); }
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Check(entered.Wait(TimeSpan.FromSeconds(2)) && pending.Wait(TimeSpan.FromSeconds(2)));
        Check(pending.Result == ControlExitOutcome.TimedOut && operations == 0);
    }
    finally { Monitor.Exit(sync); }
    Check(ended.Wait(TimeSpan.FromSeconds(2)) && operations == 0);
});
await TestAsync("an exit waiter releases its own gate while the worker completes acknowledgement", async () =>
{
    var exit = new ControlExitCompletion(); Check(exit.Start());
    var waiter = Task.Run(() => exit.Wait());
    Check(exit.Complete(ControlExitOutcome.Acknowledged));
    Check(await waiter.WaitAsync(TimeSpan.FromSeconds(2)) == ControlExitOutcome.Acknowledged);
});
await TestAsync("completion never executes a consumer inline on the pipe worker", async () =>
{
    var exit = new ControlExitCompletion(); Check(exit.Start());
    var producerThread = Environment.CurrentManagedThreadId; var insideCompletion = false; var inline = false;
    var observed = exit.Completion.ContinueWith(_ =>
        inline = insideCompletion && Environment.CurrentManagedThreadId == producerThread,
        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    insideCompletion = true;
    Check(exit.Complete(ControlExitOutcome.Acknowledged));
    insideCompletion = false;
    await observed.WaitAsync(TimeSpan.FromSeconds(2)); Check(!inline);
});
Test("real session exit before Prepare is immediate and shares its result across repeated notification", () =>
{
    var session = new IdentityControlSession(new IdentityControlOptions("unused", "", "", _ => { }));
    var first = session.NotifyExit();
    Check(first.Wait() == ControlExitOutcome.NoRegistration && ReferenceEquals(first, session.NotifyExit()));
    session.Cancel(); Check(first.Wait() == ControlExitOutcome.NoRegistration);
});
Test("real session retirement remains cancelled when a later callback notifies that retired session", () =>
{
    var session = new IdentityControlSession(new IdentityControlOptions("unused", "", "", _ => { }));
    session.Cancel();
    var first = session.NotifyExit();
    Check(first.Wait() == ControlExitOutcome.Cancelled && ReferenceEquals(first, session.NotifyExit()));
});
Test("real session registration failure completes even if its diagnostic logger throws", () =>
{
    var session = new IdentityControlSession(new IdentityControlOptions("unused", "", "", _ => throw new InvalidOperationException("synthetic_log_failure")));
    try { session.Prepare(55); }
    catch (InvalidOperationException ex) when (ex.Message == "synthetic_log_failure") { }
    var first = session.NotifyExit();
    Check(first.Wait() == ControlExitOutcome.Failed && ReferenceEquals(first, session.NotifyExit()));
    session.Cancel(); Check(first.Wait() == ControlExitOutcome.Failed);
});
Test("native exit registers once on its real Update thread and never retries a partial failure", () =>
{
    var gate = new NativeExitGate(); var calls = 0;
    Reject(() => gate.Register(0, () => calls++));
    Check(gate.Register(55, () => calls++) && !gate.Register(55, () => calls++));
    Reject(() => gate.Register(56, () => calls++));
    Check(calls == 1 && gate.RegistrationThread == 55);
    var failed = new NativeExitGate();
    Reject(() => failed.Register(55, () => { calls++; throw new ControlFailure("synthetic_install_failure"); }));
    Reject(() => failed.Register(55, () => calls++)); Check(calls == 2);
});
Test("native export notifies once even if several native threads reach it", () =>
{
    var gate = new NativeExitGate(); var signals = 0;
    Check(gate.Register(55, () => { }));
    Parallel.For(0, 64, _ => { if (gate.TrySignal()) Interlocked.Increment(ref signals); });
    Check(signals == 1 && !gate.TrySignal());
});
Test("native helper identity and exact export bytes reject changed files or an existing hook", () =>
{
    NativeExitContract.ValidateFileIdentity(NativeExitContract.HelperSize, NativeExitContract.HelperSha256);
    Reject(() => NativeExitContract.ValidateFileIdentity(NativeExitContract.HelperSize + 1, NativeExitContract.HelperSha256));
    Reject(() => NativeExitContract.ValidateFileIdentity(NativeExitContract.HelperSize, new string('0', 64)));
    NativeExitContract.ValidateEntry(NativeExitContract.EntryBytes);
    var entry = NativeExitContract.EntryBytes.ToArray(); entry[0] = 0xe9;
    Reject(() => NativeExitContract.ValidateEntry(entry));
    Reject(() => NativeExitContract.ValidateEntry(NativeExitContract.EntryBytes[..^1]));
});
Test("native helper PE bounds, architecture, DLL flag and image size fail closed", () =>
{
    var file = new byte[NativeExitContract.HelperSize]; file[0] = (byte)'M'; file[1] = (byte)'Z';
    const int pe = 0x80;
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x3c), pe);
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(pe), 0x4550);
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(pe + 4), 0x8664);
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(pe + 22), 0x2000);
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(pe + 24), 0x20b);
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(pe + 80), NativeExitContract.ImageSize);
    NativeExitContract.ValidateArchitecture(file);
    foreach (var offset in new[] { 0, pe, pe + 4, pe + 23, pe + 24, pe + 81 })
    { var wrong = file.ToArray(); wrong[offset] ^= 0xff; Reject(() => NativeExitContract.ValidateArchitecture(wrong)); }
    foreach (var invalid in new[] { -1, 0x3f, file.Length - 87, int.MaxValue })
    {
        var wrong = file.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(wrong.AsSpan(0x3c), invalid);
        Reject(() => NativeExitContract.ValidateArchitecture(wrong));
    }
    Reject(() => NativeExitContract.ValidateArchitecture(file[..^1]));
});
Test("diagnostic Interlocked first observations survive contention without mixing two sessions", () =>
{
    var page = System.Runtime.InteropServices.Marshal.AllocHGlobal(4096);
    try
    {
        System.Runtime.InteropServices.Marshal.Copy(new byte[4096], 0, page, 4096);
        Check(ControlExitDiagnostic.NewSession(page, 55) == 1 && ControlExitDiagnostic.NewSession(page, 55) == 2);
        var cancelled = ControlExitDiagnosticContract.SessionBase(1) + 32 + (int)ExitSessionStage.Cancelled * 8;
        var secondNotify = ControlExitDiagnosticContract.SessionBase(2) + 32 + (int)ExitSessionStage.NotifyEntered * 8;
        Parallel.For(0, 64, _ => ControlExitDiagnostic.Mark(page, cancelled));
        var first = System.Runtime.InteropServices.Marshal.ReadInt64(page, cancelled);
        ControlExitDiagnostic.Mark(page, secondNotify);
        Check(first > 0 && System.Runtime.InteropServices.Marshal.ReadInt64(page, cancelled) == first &&
            System.Runtime.InteropServices.Marshal.ReadInt64(page, secondNotify) > first &&
            System.Runtime.InteropServices.Marshal.ReadInt64(page, ControlExitDiagnosticContract.SessionBase(2) + 32 + (int)ExitSessionStage.Cancelled * 8) == 0);
        ControlExitDiagnostic.Mark(IntPtr.Zero, 544); ControlExitDiagnostic.Mark(page, 512); ControlExitDiagnostic.Mark(page, 4096);
        Check(System.Runtime.InteropServices.Marshal.ReadInt64(page, 0) == 0);
    }
    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(page); }
});

var register = new ControlFrame(ControlKind.Register).With(ControlKind.Register,
    (ControlField.GamePid, 101), (ControlField.GameCreation, 0x1122334455667788),
    (ControlField.ClientPid, 202), (ControlField.ClientCreation, 0x0102030405060708),
    (ControlField.GameHwnd, 303), (ControlField.GameThread, 404), (ControlField.InputThread, 404));
var registered = register.With(ControlKind.Registered, (ControlField.NonceLow, 505), (ControlField.NonceHigh, 606),
    (ControlField.ClientHwnd, 707), (ControlField.ClientThread, 808), (ControlField.Status, 1));
var activate = registered.With(ControlKind.Activate, (ControlField.Status, 0), (ControlField.Source, 1),
    (ControlField.RequestId, 1), (ControlField.ForegroundBeforeHwnd, 303), (ControlField.ForegroundBeforePid, 101),
    (ControlField.AllowAttempted, 1), (ControlField.AllowSucceeded, 1), (ControlField.InputSequence, 1));
var ack = activate.With(ControlKind.ActivationAck, (ControlField.Status, 1),
    (ControlField.ForegroundAfterHwnd, 707), (ControlField.ForegroundAfterPid, 202),
    (ControlField.ClientFocusHwnd, 909), (ControlField.ClientFlags, 3));

Test("fixed 208-byte little-endian wire offsets and immutable copy", () =>
{
    var bytes = register.Encode();
    Check(bytes.Length == 208 && Convert.ToHexString(bytes.AsSpan(0, 16)) == "4D5343310100000001000000D0000000");
    Check(Convert.ToHexString(bytes.AsSpan(40, 8)) == "8877665544332211");
    Check(Convert.ToHexString(bytes.AsSpan(56, 8)) == "0807060504030201");
    Check(ControlFrame.Decode(bytes)[ControlField.GameCreation] == 0x1122334455667788);
    Check(register[ControlField.NonceLow] == 0 && registered[ControlField.NonceLow] == 505);
});
Test("length, magic, version, kind and declared size fail closed", () =>
{
    Reject(() => ControlFrame.Decode(new byte[207]));
    Reject(() => ControlFrame.Decode(new byte[209]));
    foreach (var offset in new[] { 0, 4, 8, 12 })
    { var bytes = register.Encode(); bytes[offset] = 99; Reject(() => ControlFrame.Decode(bytes)); }
});
Test("registration identity echo, nonzero nonce, windows and reserved zeros", () =>
{
    ControlFrame.ValidateRegistered(register, registered);
    foreach (ControlField field in Enum.GetValues(typeof(ControlField)))
    {
        if (field is ControlField.NonceLow or ControlField.NonceHigh or ControlField.ClientHwnd or ControlField.ClientThread or ControlField.Status) continue;
        Reject(() => ControlFrame.ValidateRegistered(register, registered.With(ControlKind.Registered, (field, registered[field] + 1))));
    }
    foreach (var field in new[] { ControlField.NonceLow, ControlField.NonceHigh, ControlField.ClientHwnd, ControlField.ClientThread, ControlField.Status })
        Reject(() => ControlFrame.ValidateRegistered(register, registered.With(ControlKind.Registered, (field, 0))));
    Reject(() => ControlFrame.ValidateRegistered(register, registered.With(ControlKind.Registered, (ControlField.ClientThread, 0x100000000))));
});
Test("actual activation ACK evidence and exact request echo", () =>
{
    ControlFrame.ValidateAcknowledgement(activate, ack);
    foreach (ControlField field in Enum.GetValues(typeof(ControlField)))
    {
        if (field is ControlField.ForegroundAfterHwnd or ControlField.ForegroundAfterPid or ControlField.ClientFocusHwnd or ControlField.ClientFlags or ControlField.Status) continue;
        Reject(() => ControlFrame.ValidateAcknowledgement(activate, ack.With(ControlKind.ActivationAck, (field, ack[field] + 1))));
    }
    foreach (var field in new[] { ControlField.ForegroundAfterHwnd, ControlField.ForegroundAfterPid, ControlField.ClientFocusHwnd, ControlField.ClientFlags, ControlField.Status })
        Reject(() => ControlFrame.ValidateAcknowledgement(activate, ack.With(ControlKind.ActivationAck, (field, 0))));
    Reject(() => ControlFrame.ValidateAcknowledgement(activate, ack.With(ControlKind.ExitAck)));
});
Test("rejection is explicit, bounded, and never interpreted as applied", () =>
{
    var rejected = activate.With(ControlKind.ActivationAck, (ControlField.Status, 2));
    ControlFrame.ValidateAcknowledgement(activate, rejected);
    Check(rejected[ControlField.Status] != 1);
    Reject(() => ControlFrame.ValidateAcknowledgement(activate, rejected.With(ControlKind.ActivationAck, (ControlField.ClientFlags, 4))));
    Reject(() => ControlFrame.ValidateAcknowledgement(activate, rejected.With(ControlKind.ActivationAck, (ControlField.ForegroundAfterPid, 0x100000000))));
});
Test("exit reuses bound identity and permits no activation fields", () =>
{
    var exit = registered.With(ControlKind.Exit, (ControlField.Status, 0), (ControlField.RequestId, 2), (ControlField.Source, 3));
    var response = exit.With(ControlKind.ExitAck, (ControlField.Status, 1));
    ControlFrame.ValidateAcknowledgement(exit, response);
    Reject(() => ControlFrame.ValidateAcknowledgement(exit, response.With(ControlKind.ExitAck, (ControlField.ClientFlags, 3))));
    Reject(() => ControlFrame.ValidateAcknowledgement(exit, response.With(ControlKind.ExitAck, (ControlField.Status, 2))));
});
Test("request and physical input sequences are distinct and strictly increasing", () =>
{
    var sequence = new ControlRequestSequence();
    Check(sequence.Next(new ControlInput(ControlInputSource.AutoLaunch, 0, 404), 404) == 1);
    Check(sequence.Next(new ControlInput(ControlInputSource.F8, 3, 404), 404) == 2);
    Reject(() => sequence.Next(new ControlInput(ControlInputSource.F8, 3, 404), 404));
    Reject(() => sequence.Next(new ControlInput(ControlInputSource.RightStick, 4, 405), 404));
    Reject(() => sequence.Next(new ControlInput(ControlInputSource.AutoLaunch, 0, 404), 404));
    Reject(() => sequence.Next(new ControlInput(ControlInputSource.Exit, 4, 404), 404));
    Check(sequence.Next(new ControlInput(ControlInputSource.RightStick, 4, 404), 404) == 3);
    Check(sequence.Exit() == 4);
});
Test("pipe name binds decimal PID and lowercase exact creation, not executable name", () =>
{
    Check(ControlFrame.PipeName(1234, 0x1ABCDEF) == "mystia-steward-companion.control.v1.1234.1abcdef");
    Reject(() => ControlFrame.PipeName(0, 1)); Reject(() => ControlFrame.PipeName(1, 0));
});
Test("game/client user, session, PID and exact configured path are independently required", () =>
{
    var game = new ControlProcess(101, 1, @"D:\game\game.exe", 2, "SID-A");
    var client = new ControlProcess(202, 2, @"D:\client\client.exe", 2, "SID-A");
    ControlWindows.CheckClient(game, client, @"d:\CLIENT\client.exe");
    Reject(() => ControlWindows.CheckClient(game, client with { Pid = 101 }, client.Path));
    Reject(() => ControlWindows.CheckClient(game, client with { Session = 3 }, client.Path));
    Reject(() => ControlWindows.CheckClient(game, client with { User = "SID-B" }, client.Path));
    Reject(() => ControlWindows.CheckClient(game, client, @"D:\other\client.exe"));
});
byte[] Table4(params (uint Pid, byte Address, ushort Port)[] entries)
{
    var bytes = new byte[4 + 24 * entries.Length]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)entries.Length);
    for (var i = 0; i < entries.Length; ++i)
    {
        var row = bytes.AsSpan(4 + 24 * i, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(row, 2); row[4] = entries[i].Address; row[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(row[8..], entries[i].Port);
        BinaryPrimitives.WriteUInt32LittleEndian(row[20..], entries[i].Pid);
    }
    return bytes;
}
Test("read-only listener discovery distinguishes absent, exact and ambiguous occupants", () =>
{
    var empty = new byte[4];
    Check(ControlListenerIdentity.Resolve(Table4(), empty) == 0);
    Check(ControlListenerIdentity.Resolve(Table4((22, 127, 32146), (99, 0, 80)), empty) == 22);
    Reject(() => ControlListenerIdentity.Resolve(Table4((22, 0, 32146)), empty));
    Reject(() => ControlListenerIdentity.Resolve(Table4((0, 127, 32146)), empty));
    Reject(() => ControlListenerIdentity.Resolve(Table4((22, 127, 32146), (22, 127, 32146)), empty));
    Reject(() => ControlListenerIdentity.Resolve(Table4((22, 127, 32146), (33, 127, 32146)), empty));
});
Test("IPv6 occupant and malformed OS table never become absent-listener permission", () =>
{
    var ipv6 = new byte[60]; BinaryPrimitives.WriteUInt32LittleEndian(ipv6, 1);
    BinaryPrimitives.WriteUInt16BigEndian(ipv6.AsSpan(24), 32146);
    Reject(() => ControlListenerIdentity.Resolve(Table4(), ipv6));
    Reject(() => ControlListenerIdentity.Resolve(new byte[3], new byte[4]));
    Reject(() => ControlListenerIdentity.Resolve(new byte[] { 1, 0, 0, 0 }, new byte[4]));
    Reject(() => ControlListenerIdentity.Resolve(Table4(), new byte[] { 255, 255, 255, 255 }));
});
Test("RS requires neutral, does not repeat held, and rejects background input", () =>
{
    var gate = new ControlInputGate();
    Check(gate.Sample(true, false, true, true, 0) == null);
    Check(gate.Sample(true, false, false, false, 0) == null);
    Check(gate.Sample(true, false, true, true, 0) == ControlInputSource.RightStick);
    Check(gate.Sample(true, false, true, true, 0) == null);
    Check(gate.Sample(false, true, false, false, 0) == null);
    Check(gate.Sample(true, false, true, true, 0) == null);
    gate.Sample(true, false, false, false, 0);
    Check(gate.Sample(true, true, true, true, 0) == ControlInputSource.F8);
});
Test("ACK handoff invalidates RS readiness even when no Unity frame ran in background", () =>
{
    var gate = new ControlInputGate();
    gate.Sample(true, false, false, false, 0);
    Check(gate.Sample(true, false, true, true, 1) == null);
    Check(gate.Sample(true, false, true, true, 1) == null);
    gate.Sample(true, false, false, false, 1);
    Check(gate.Sample(true, false, true, true, 1) == ControlInputSource.RightStick);
});
Test("F8 held across client return cannot become another game edge before a real neutral sample", () =>
{
    var gate = new ControlInputGate();
    Check(gate.Sample(true, true, false, false, 0, keyboardHeld: true) == null);
    gate.Sample(true, false, false, false, 0);
    Check(gate.Sample(true, true, false, false, 0, keyboardHeld: true) == ControlInputSource.F8);
    Check(gate.Sample(true, true, false, false, 0, keyboardHeld: true) == null);
    Check(gate.Sample(false, false, false, false, 0, keyboardHeld: true) == null);
    Check(gate.Sample(true, true, false, false, 1, keyboardHeld: true) == null);
    Check(gate.Sample(true, false, false, false, 1, keyboardHeld: true) == null);
    Check(gate.Sample(true, true, false, false, 1, keyboardHeld: true) == null);
    gate.Sample(true, false, false, false, 1);
    Check(gate.Sample(true, true, false, false, 1, keyboardHeld: true) == ControlInputSource.F8);
});
Test("queued input expires across handoff even after the same game HWND regains foreground", () =>
{
    var input = new ControlInput(ControlInputSource.F8, 2, 404, ForegroundHwnd: 303, ForegroundPid: 101, Handoff: 3);
    Check(input.IsCurrent(303, 101, 3));
    Check(!input.IsCurrent(303, 101, 4));
    Check(!input.IsCurrent(707, 202, 3));
    Check(!input.IsCurrent(303, 202, 3));
    Check((input with { Handoff = 4 }).IsCurrent(303, 101, 4));
});
Test("activation consumption confirmation preserves every ACK field and permits no replay", () =>
{
    var lifecycle = new ControlLifecycle();
    lifecycle.BeginActivation(activate);
    Reject(() => lifecycle.CompleteObservation());
    Reject(() => lifecycle.ObserveActivation(ack.With(ControlKind.ActivationAck, (ControlField.InputSequence, 99))));
    var observed = lifecycle.ObserveActivation(ack);
    Check(observed.Kind == ControlKind.ActivationObserved);
    Check(observed.Encode().AsSpan(16).SequenceEqual(ack.Encode().AsSpan(16)));
    Check(ControlFrame.Decode(observed.Encode()).Kind == ControlKind.ActivationObserved);
    Reject(() => lifecycle.ObserveActivation(ack));
    lifecycle.CompleteObservation();
    Reject(() => lifecycle.ObserveActivation(ack));
    Reject(() => lifecycle.CompleteObservation());
});
Test("stop rejects new activation while allowing the submitted ACK and observation before Exit", () =>
{
    var lifecycle = new ControlLifecycle();
    lifecycle.BeginActivation(activate);
    lifecycle.Stop();
    Check(lifecycle.Stopping && !lifecycle.CanExit);
    Reject(() => lifecycle.BeginActivation(activate));
    var observed = lifecycle.ObserveActivation(ack);
    Check(observed.Kind == ControlKind.ActivationObserved && !lifecycle.CanExit);
    lifecycle.CompleteObservation();
    Check(lifecycle.CanExit);
    Reject(() => lifecycle.BeginActivation(activate));
    var stoppedBeforeSubmit = new ControlLifecycle();
    stoppedBeforeSubmit.Stop();
    Check(stoppedBeforeSubmit.CanExit);
    Reject(() => stoppedBeforeSubmit.BeginActivation(activate));
    Reject(() => stoppedBeforeSubmit.ObserveActivation(ack));
});
await TestAsync("ActivationObserved tolerates pipe fragmentation and is not another activation or request ID", async () =>
{
    var lifecycle = new ControlLifecycle(); lifecycle.BeginActivation(activate);
    var observed = lifecycle.ObserveActivation(ack);
    using var stream = new FragmentStream(observed.Encode(), 1);
    var decoded = await ControlFrame.ReadAsync(stream, CancellationToken.None);
    Check(decoded.Kind == ControlKind.ActivationObserved && decoded[ControlField.RequestId] == activate[ControlField.RequestId]);
    Check(decoded.Encode().SequenceEqual(observed.Encode()));
    Reject(() => ControlFrame.ValidateAcknowledgement(activate, decoded));
});
await TestAsync("fragmented stream is assembled exactly once without consuming next frame", async () =>
{
    using var stream = new FragmentStream(register.Encode().Concat(ack.Encode()).ToArray(), 3);
    Check((await ControlFrame.ReadAsync(stream, CancellationToken.None)).Kind == ControlKind.Register);
    Check(stream.Position == 208);
    Check((await ControlFrame.ReadAsync(stream, CancellationToken.None)).Kind == ControlKind.ActivationAck);
});
await TestAsync("EOF fails instead of accepting a partial ACK", async () =>
{
    using var stream = new FragmentStream(ack.Encode()[..207], 7);
    try { await ControlFrame.ReadAsync(stream, CancellationToken.None); }
    catch (EndOfStreamException) { return; }
    throw new Exception("Truncated ACK accepted.");
});
await TestAsync("cancellation interrupts an outstanding read without replay", async () =>
{
    using var cancel = new CancellationTokenSource(); using var stream = new WaitingStream();
    var read = ControlFrame.ReadAsync(stream, cancel.Token); cancel.Cancel();
    try { await read; } catch (OperationCanceledException) { Check(stream.Reads == 1); return; }
    throw new Exception("Read ignored cancellation.");
});
Console.WriteLine($"Companion identity control smoke: {passed} groups passed (managed contracts only).");

sealed class FragmentStream : MemoryStream
{
    private readonly int _fragment;
    internal FragmentStream(byte[] bytes, int fragment) : base(bytes) { _fragment = fragment; }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, _fragment)], cancellation);
}
sealed class WaitingStream : MemoryStream
{
    internal int Reads;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
    { Reads++; await Task.Delay(Timeout.Infinite, cancellation); return 0; }
}
