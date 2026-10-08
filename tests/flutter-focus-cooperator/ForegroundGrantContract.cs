using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MystiaStewardCompanion.FocusProbe;

internal sealed record SessionDescriptor(
    string RunId, string GitSha, string PipeName, string NonceHex,
    ulong GamePid, ulong GameCreation, ulong ProbePid, ulong ProbeCreation,
    ulong ProbeHwnd, ulong ProbeThreadId, string ProbeExeSha256)
{
    public ulong NonceLo => ulong.Parse(NonceHex[..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    public ulong NonceHi => ulong.Parse(NonceHex[16..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    public static SessionDescriptor Parse(ReadOnlyMemory<byte> bytes, string expectedRunId, string expectedGitSha)
    {
        Guard.That(bytes.Length is > 0 and <= 8192, "Descriptor length is invalid.");
        using var document = JsonDocument.Parse(bytes);
        Guard.That(document.RootElement.ValueKind == JsonValueKind.Object, "Descriptor must be an object.");
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            Guard.That(values.TryAdd(property.Name, property.Value), "Descriptor contains duplicate fields.");
        string[] names = { "schemaVersion", "runId", "gitSha", "pipeName", "nonceHex", "gamePid", "gameCreationHex", "probePid", "probeCreationHex", "probeHwnd", "probeThreadId", "probeExeSha256" };
        Guard.That(values.Count == names.Length && names.All(values.ContainsKey), "Descriptor fields differ from the protocol.");
        Guard.That(values["schemaVersion"].ValueKind == JsonValueKind.Number && values["schemaVersion"].GetInt32() == 1, "Descriptor schema differs.");
        string Text(string key)
        {
            Guard.That(values[key].ValueKind == JsonValueKind.String, "Descriptor identity must be a string.");
            var text = values[key].GetString()!;
            Guard.That(text.Length is > 0 and <= 240 && text.All(ch => ch is >= (char)32 and <= (char)126), "Descriptor string is not bounded ASCII.");
            return text;
        }
        ulong Number(string key, bool hex = false)
        {
            var text = Text(key);
            Guard.That(Regex.IsMatch(text, hex ? "^[0-9a-f]{1,16}$" : "^[1-9][0-9]{0,19}$", RegexOptions.CultureInvariant), "Descriptor number is not canonical.");
            Guard.That(ulong.TryParse(text, hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number != 0, "Descriptor number is invalid.");
            return number;
        }
        var run = Text("runId"); var sha = Text("gitSha"); var pipe = Text("pipeName"); var nonce = Text("nonceHex"); var exeHash = Text("probeExeSha256");
        Guard.That(Regex.IsMatch(run, "^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$", RegexOptions.CultureInvariant) && run == expectedRunId, "Descriptor run differs.");
        Guard.That(Regex.IsMatch(sha, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant) && sha == expectedGitSha, "Descriptor source SHA differs.");
        Guard.That(Regex.IsMatch(nonce, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant) && nonce.Any(ch => ch != '0'), "Descriptor nonce is invalid.");
        Guard.That(Regex.IsMatch(pipe, "^[A-Za-z0-9_-]{1,200}$", RegexOptions.CultureInvariant) && pipe.Contains(run, StringComparison.Ordinal) && pipe.Contains(nonce, StringComparison.Ordinal), "Descriptor pipe identity differs.");
        Guard.That(Regex.IsMatch(exeHash, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant), "Descriptor executable SHA is invalid.");
        var result = new SessionDescriptor(run, sha, pipe, nonce, Number("gamePid"), Number("gameCreationHex", true), Number("probePid"), Number("probeCreationHex", true), Number("probeHwnd"), Number("probeThreadId"), exeHash);
        Guard.That(result.GamePid <= uint.MaxValue && result.ProbePid <= uint.MaxValue && result.ProbeThreadId <= uint.MaxValue && result.GamePid != result.ProbePid && result.ProbeHwnd <= long.MaxValue, "Descriptor process/window range is invalid.");
        return result;
    }
}

internal static class Guard
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

internal sealed class GrantFrame
{
    public const int ByteLength = 176;
    public const uint Magic = 0x4d534647;
    public uint Kind { get; }
    private readonly ulong[] _fields;
    public ulong this[int index] => _fields[index];
    public GrantFrame(uint kind, IEnumerable<ulong> fields)
    {
        Kind = kind; _fields = fields.ToArray();
        Guard.That(kind is >= 1 and <= 3 && _fields.Length == 20 && _fields[19] == 0, "Invalid grant frame kind/fields/reserved value.");
    }
    public static GrantFrame Decode(ReadOnlySpan<byte> bytes)
    {
        Guard.That(bytes.Length == ByteLength, "Grant frame has an invalid byte length.");
        Guard.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic && BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) == ByteLength, "Grant frame header differs.");
        var fields = new ulong[20];
        for (var index = 0; index < fields.Length; index++) fields[index] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(16 + index * 8)..]);
        return new GrantFrame(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), fields);
    }
    public byte[] Encode()
    {
        var bytes = new byte[ByteLength];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), Kind); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), ByteLength);
        for (var index = 0; index < _fields.Length; index++) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16 + index * 8), _fields[index]);
        return bytes;
    }
    public ulong[] CopyFields() => (ulong[])_fields.Clone();
    public static GrantFrame Ready(SessionDescriptor descriptor) => new(1, new ulong[] { descriptor.NonceLo, descriptor.NonceHi, descriptor.GamePid, descriptor.GameCreation, descriptor.ProbePid, descriptor.ProbeCreation, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
    internal static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellation, bool allowEof)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellation);
            if (read == 0)
            {
                if (offset == 0 && allowEof) return false;
                throw new EndOfStreamException("EOF inside a fixed-length frame.");
            }
            offset += read;
        }
        return true;
    }
}

internal sealed record ProcessObservation(ulong Pid, ulong Creation, string ImagePath, uint Session, bool Alive);
internal sealed record WindowObservation(ulong Hwnd, ulong Pid, ulong ThreadId);
internal sealed record GrantObservation(ProcessObservation Game, ProcessObservation Probe, ulong PeerPid,
    WindowObservation GameWindow, int GameWindowCount, WindowObservation ProbeWindow, WindowObservation Foreground);
internal sealed record GrantCallResult(bool Succeeded, uint LastError);

internal sealed class GrantSession
{
    private readonly SessionDescriptor _descriptor;
    private readonly string _gamePath, _probePath;
    private ulong _sequence, _requestId;
    private bool _failed;
    public ulong SuccessfulGrants { get; private set; }
    public GrantSession(SessionDescriptor descriptor, string gamePath, string probePath)
    { _descriptor = descriptor; _gamePath = gamePath; _probePath = probePath; }

    public void ValidateProcesses(GrantObservation observation)
    {
        Guard.That(observation.PeerPid == _descriptor.ProbePid && observation.Game.Alive && observation.Probe.Alive, "Bound pipe peer or process liveness differs.");
        Guard.That(observation.Game.Pid == _descriptor.GamePid && observation.Game.Creation == _descriptor.GameCreation && string.Equals(observation.Game.ImagePath, _gamePath, StringComparison.OrdinalIgnoreCase), "Retained game identity differs.");
        Guard.That(observation.Probe.Pid == _descriptor.ProbePid && observation.Probe.Creation == _descriptor.ProbeCreation && string.Equals(observation.Probe.ImagePath, _probePath, StringComparison.OrdinalIgnoreCase), "Retained Flutter identity differs.");
        Guard.That(observation.Game.Session == observation.Probe.Session, "Retained process sessions differ.");
        Guard.That(observation.ProbeWindow.Hwnd == _descriptor.ProbeHwnd && observation.ProbeWindow.Pid == _descriptor.ProbePid && observation.ProbeWindow.ThreadId == _descriptor.ProbeThreadId, "Exact Flutter window identity differs.");
    }
    public GrantFrame Grant(GrantFrame request, GrantObservation observation, Func<uint, GrantCallResult> allow, Func<WindowObservation> readForeground)
    {
        try
        {
            Guard.That(!_failed && request.Kind == 2, "Grant session is stopped or message is not a request.");
            var ready = GrantFrame.Ready(_descriptor);
            for (var index = 0; index < 6; index++) Guard.That(request[index] == ready[index], "Request retained identity or nonce differs.");
            Guard.That(request[6] == _sequence + 1 && request[6] <= 3 && request[7] > _requestId && request[7] <= 1000, "Grant sequence/request is repeated, stale or out of range.");
            for (var index = 12; index < 20; index++) Guard.That(request[index] == 0, "Request result fields are nonzero.");
            ValidateProcesses(observation);
            Guard.That(observation.GameWindowCount == 1 && observation.GameWindow.Hwnd != 0 && observation.GameWindow.Pid == _descriptor.GamePid && observation.GameWindow.ThreadId != 0, "Game window is missing or ambiguous.");
            Guard.That(request[8] == observation.GameWindow.Hwnd && request[9] == observation.ProbeWindow.Hwnd && request[10] == observation.GameWindow.ThreadId && request[11] == observation.ProbeWindow.ThreadId, "Request window/thread binding differs.");
            Guard.That(observation.Foreground.Hwnd == request[8] && observation.Foreground.Pid == _descriptor.GamePid, "Exact game window is not foreground before grant.");
            _sequence = request[6]; _requestId = request[7];
            // This is the sole mutation. Invalid/replayed requests cannot reach it.
            var granted = allow(checked((uint)_descriptor.ProbePid));
            var after = readForeground();
            var values = request.CopyFields();
            values[12] = observation.Foreground.Hwnd; values[13] = observation.Foreground.Pid;
            values[14] = after.Hwnd; values[15] = after.Pid; values[16] = 1; values[17] = granted.Succeeded ? 1UL : 0UL; values[18] = granted.LastError;
            if (granted.Succeeded && after.Hwnd == values[12] && after.Pid == values[13]) SuccessfulGrants++;
            else _failed = true;
            return new GrantFrame(3, values);
        }
        catch { _failed = true; throw; }
    }
    public bool IsFailed => _failed;
    public void Complete() => Guard.That(!_failed && SuccessfulGrants == 3, "Pipe ended before three successful, unique grants.");
}
