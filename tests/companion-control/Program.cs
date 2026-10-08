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
