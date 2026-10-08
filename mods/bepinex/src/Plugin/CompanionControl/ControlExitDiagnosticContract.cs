using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace MystiaStewardCompanion.Plugin.CompanionControl;

// P0 observation memory only. This is not MSC1 and authorizes no game action.
internal enum ExitGlobalStage
{
    Attached, QuitEntered, QuitReturned, DestroyEntered, DestroyReturned,
    DisposeEntered, DisposeAlreadyDisposed, DisposeFirst, DisposeReturned,
    LauncherEntered, LauncherStopping, LauncherSessionMissing,
    NativeRegistered, NativeRegistrationFailed, NativeEntered, NativeAcknowledged,
    NativeUnconfirmed, NativeFailed, NativeOriginalInvoked,
}
internal enum ExitSessionStage
{
    Prepared, ClientBound, NotifyEntered, NotifyStopping, NotifyFaulted, StopSet,
    Queued, QueueRejected, WorkerDequeued, NoRegistration, WriteStarted,
    WriteCompleted, AckValidated, Failed, Cancelled, LogThrew,
}
internal sealed record ExitDiagnosticHeader(uint ControllerPid, ulong ControllerCreation, uint Session,
    ulong NonceLow, ulong NonceHigh, string ClientSha256, string ModSha256, string ManifestSha256, string GameSha256);
internal static class ControlExitDiagnosticContract
{
    internal const int PageSize = 4096, HeaderSize = 512, CounterOffset = 512, SessionCountOffset = 520,
        MainThreadOffset = 528, NativeThreadOffset = 536, GlobalOffset = 544, SessionOffset = 768, SessionSize = 512, SessionStagesOffset = 32;
    internal const ulong Magic = 0x333030445845434d; // MCEXD003, little endian.
    internal const ulong Version = 3;
    internal static string MappingName(uint pid, ulong creation) => $@"Local\mystia-steward-companion.exitdiag.v3.{pid}.{creation:x}";
    internal static int SessionBase(int session)
    {
        ControlFrame.Require(session is 1 or 2, "diagnostic_session_range");
        return SessionOffset + (session - 1) * SessionSize;
    }
    internal static ExitDiagnosticHeader ValidateHeader(byte[] page, uint pid, ulong creation, string sha, string run)
    {
        ControlFrame.Require(page.Length == PageSize, "diagnostic_page_size");
        ulong Word(int index) => BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(index * 8, 8));
        ControlFrame.Require(Word(0) == Magic && Word(1) == Version && Word(2) == PageSize && Word(3) == HeaderSize &&
            Word(4) == pid && pid != 0 && Word(5) == creation && creation != 0 && Word(6) is > 0 and <= uint.MaxValue &&
            Word(6) != pid && Word(7) is > 0 and <= long.MaxValue && Word(8) is > 0 and <= uint.MaxValue &&
            Word(9) is > 0 and <= long.MaxValue && Word(10) is > 0 and <= long.MaxValue && Word(11) == 2 &&
            Enumerable.Range(12, 4).All(index => Word(index) == 0) && page.AsSpan(504, 8).ToArray().All(value => value == 0),
            "diagnostic_header_identity");
        string Text(int offset, int length)
        {
            var bytes = page.AsSpan(offset, length); var end = bytes.IndexOf((byte)0);
            if (end < 0) end = length;
            ControlFrame.Require(bytes[..end].ToArray().All(value => value is >= 33 and <= 126) &&
                bytes[end..].ToArray().All(value => value == 0), "diagnostic_header_text");
            return Encoding.ASCII.GetString(bytes[..end]);
        }
        ControlFrame.Require(Regex.IsMatch(sha, "\\A[a-f0-9]{40}\\z") && Regex.IsMatch(run, "\\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\\z") &&
            Text(128, 40) == sha && Text(168, 80) == run, "diagnostic_build_run");
        var hashes = new[] { Text(248, 64), Text(312, 64), Text(376, 64), Text(440, 64) };
        ControlFrame.Require(hashes.All(hash => Regex.IsMatch(hash, "\\A[a-f0-9]{64}\\z")), "diagnostic_hashes");
        return new ExitDiagnosticHeader((uint)Word(6), Word(7), (uint)Word(8), Word(9), Word(10), hashes[0], hashes[1], hashes[2], hashes[3]);
    }
}
