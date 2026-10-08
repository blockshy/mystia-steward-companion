using System.Buffers.Binary;

namespace MystiaStewardCompanion.Plugin.CompanionControl;

public enum CompanionControlProtocol
{
    LegacyTcp,
    IdentityPipeV1,
}

internal enum ControlInputSource : ulong { AutoLaunch, F8, RightStick, Exit }

// Captured synchronously in Unity Update; contains no Unity or IL2CPP object.
internal sealed record ControlInput(ControlInputSource Source, ulong Sequence, uint ThreadId,
    bool LegacyHeld = false, bool LegacyPressed = false,
    bool InputSystemHeld = false, bool InputSystemPressed = false,
    ulong ForegroundHwnd = 0, uint ForegroundPid = 0, long Handoff = 0, bool KeyboardHeld = false)
{
    internal bool IsCurrent(ulong foregroundHwnd, uint foregroundPid, long handoff) =>
        ForegroundHwnd != 0 && ForegroundHwnd == foregroundHwnd && ForegroundPid == foregroundPid && Handoff == handoff;
}

internal sealed class ControlInputGate
{
    private bool _requiresRelease = true;
    private bool _keyboardRequiresRelease = true;
    private long _handoff;
    internal ControlInputSource? Sample(bool gameForeground, bool keyboardPressed, bool held, bool pressed, long handoff, bool keyboardHeld = false)
    {
        if (_handoff != handoff) { _handoff = handoff; _requiresRelease = true; _keyboardRequiresRelease = true; }
        if (!gameForeground) { _requiresRelease = true; _keyboardRequiresRelease = true; return null; }
        var ready = !_requiresRelease;
        var keyboardReady = !_keyboardRequiresRelease;
        // A held state without an edge also requires a real neutral frame before accepting another edge.
        _requiresRelease = held;
        _keyboardRequiresRelease = keyboardHeld || keyboardPressed;
        if (keyboardPressed && keyboardReady) return ControlInputSource.F8;
        return held && pressed && ready ? ControlInputSource.RightStick : null;
    }
}

internal enum ControlKind : uint { Register = 1, Registered, Activate, ActivationAck, Exit, ExitAck, ActivationObserved }

internal enum ControlField
{
    NonceLow, NonceHigh, GamePid, GameCreation, ClientPid, ClientCreation,
    RequestId, Source, GameHwnd, ClientHwnd, GameThread, ClientThread,
    ForegroundBeforeHwnd, ForegroundBeforePid, ForegroundAfterHwnd, ForegroundAfterPid,
    AllowAttempted, AllowSucceeded, AllowError, InputSequence, InputThread,
    ClientFocusHwnd, ClientFlags, Status,
}

internal sealed class ControlFrame
{
    internal const int Size = 208;
    internal const uint Magic = 0x3143534d;
    internal const uint Version = 1;
    private readonly ulong[] _fields;
    internal ControlKind Kind { get; }
    internal ulong this[ControlField field] => _fields[(int)field];
    internal ControlFrame(ControlKind kind) : this(kind, new ulong[24]) { }
    private ControlFrame(ControlKind kind, ulong[] fields) { Kind = kind; _fields = fields; }
    internal ControlFrame With(ControlKind kind, params (ControlField Field, ulong Value)[] values)
    {
        var fields = (ulong[])_fields.Clone();
        foreach (var item in values) fields[(int)item.Field] = item.Value;
        return new ControlFrame(kind, fields);
    }
    internal byte[] Encode()
    {
        var bytes = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), Size);
        for (var i = 0; i < 24; ++i) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16 + i * 8), _fields[i]);
        return bytes;
    }
    internal static ControlFrame Decode(ReadOnlySpan<byte> bytes)
    {
        Require(bytes.Length == Size, "frame_length");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == Version &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) == Size, "frame_version");
        var kind = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        Require(kind is >= 1 and <= 7, "frame_kind");
        var fields = new ulong[24];
        for (var i = 0; i < fields.Length; ++i) fields[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(16 + i * 8)..]);
        return new ControlFrame((ControlKind)kind, fields);
    }
    internal static async Task<ControlFrame> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        var bytes = new byte[Size];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(offset), cancellation).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("control_frame_eof");
            offset += count;
        }
        return Decode(bytes);
    }
    internal static void ValidateRegistered(ControlFrame request, ControlFrame reply)
    {
        Require(request.Kind == ControlKind.Register && reply.Kind == ControlKind.Registered, "registration_kind");
        Require(reply[ControlField.NonceLow] != 0 && reply[ControlField.NonceHigh] != 0 &&
            reply[ControlField.ClientHwnd] is > 0 and <= long.MaxValue &&
            reply[ControlField.ClientThread] is > 0 and <= uint.MaxValue &&
            reply[ControlField.Status] == 1, "registration_fields");
        for (var i = 0; i < 24; ++i)
        {
            var field = (ControlField)i;
            if (field is ControlField.NonceLow or ControlField.NonceHigh or ControlField.ClientHwnd or ControlField.ClientThread or ControlField.Status) continue;
            Require(reply[field] == request[field], "registration_echo");
        }
    }
    internal static void ValidateAcknowledgement(ControlFrame request, ControlFrame reply)
    {
        var exit = request.Kind == ControlKind.Exit;
        Require((exit || request.Kind == ControlKind.Activate) &&
            reply.Kind == (exit ? ControlKind.ExitAck : ControlKind.ActivationAck), "ack_kind");
        for (var i = 0; i < 24; ++i)
        {
            var field = (ControlField)i;
            if (field == ControlField.Status || (!exit && field is ControlField.ForegroundAfterHwnd or ControlField.ForegroundAfterPid or ControlField.ClientFocusHwnd or ControlField.ClientFlags)) continue;
            Require(reply[field] == request[field], "ack_echo");
        }
        Require(reply[ControlField.Status] is 1 or 2, "ack_status");
        Require(!exit || reply[ControlField.Status] == 1, "exit_rejected");
        if (!exit)
        {
            Require(reply[ControlField.ClientFlags] <= 3 && reply[ControlField.ForegroundAfterHwnd] <= long.MaxValue &&
                reply[ControlField.ForegroundAfterPid] <= uint.MaxValue && reply[ControlField.ClientFocusHwnd] <= long.MaxValue, "ack_bounds");
            if (reply[ControlField.Status] == 1)
                Require(reply[ControlField.ForegroundAfterHwnd] == request[ControlField.ClientHwnd] &&
                    reply[ControlField.ForegroundAfterPid] == request[ControlField.ClientPid] &&
                    reply[ControlField.ClientFocusHwnd] > 0 && reply[ControlField.ClientFlags] == 3, "activation_unproven");
        }
    }
    internal static string PipeName(uint pid, ulong creation)
    {
        Require(pid != 0 && creation != 0, "pipe_identity");
        return $"mystia-steward-companion.control.v1.{pid}.{creation:x}";
    }
    internal static void Require(bool condition, string code)
    { if (!condition) throw new ControlFailure(code); }
}

// Access is serialized by the session's stop/submission lock. Stop rejects new
// activation, but an already submitted activation must finish its observation.
internal sealed class ControlLifecycle
{
    private ControlFrame? _pendingActivation;
    private bool _observed;
    internal bool Stopping { get; private set; }
    internal bool CanExit => Stopping && _pendingActivation == null;
    internal void Stop() => Stopping = true;
    internal void BeginActivation(ControlFrame request)
    {
        ControlFrame.Require(!Stopping && _pendingActivation == null && request.Kind == ControlKind.Activate, "activation_submission_state");
        _pendingActivation = request;
        _observed = false;
    }
    internal ControlFrame ObserveActivation(ControlFrame reply)
    {
        ControlFrame.Require(_pendingActivation != null && !_observed, "activation_observation_state");
        ControlFrame.ValidateAcknowledgement(_pendingActivation!, reply);
        ControlFrame.Require(reply[ControlField.Status] == 1, "activation_rejected");
        _observed = true;
        return reply.With(ControlKind.ActivationObserved);
    }
    internal void CompleteObservation()
    {
        ControlFrame.Require(_pendingActivation != null && _observed, "activation_completion_state");
        _pendingActivation = null;
    }
}

internal sealed class ControlFailure : Exception
{
    internal ControlFailure(string code) : base(code) { }
}

// Pure sequencing rules, shared by the worker and the .NET 6 smoke.
internal sealed class ControlRequestSequence
{
    private ulong _request;
    private ulong _input;
    internal ulong Next(ControlInput input, uint gameThread)
    {
        ControlFrame.Require(input.ThreadId == gameThread && gameThread != 0, "input_thread");
        ControlFrame.Require(input.Source is ControlInputSource.AutoLaunch or ControlInputSource.F8 or ControlInputSource.RightStick, "input_source");
        if (input.Source == ControlInputSource.AutoLaunch)
            ControlFrame.Require(_request == 0 && input.Sequence == 0, "auto_sequence");
        else
        {
            ControlFrame.Require(input.Sequence > _input && input.Sequence <= long.MaxValue, "input_sequence");
            _input = input.Sequence;
        }
        return checked(++_request);
    }
    internal ulong Exit() => checked(++_request);
}
