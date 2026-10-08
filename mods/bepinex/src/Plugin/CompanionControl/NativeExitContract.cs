using System.Buffers.Binary;

namespace MystiaStewardCompanion.Plugin.CompanionControl;

// The current game's final native exit export, not a cancellable Unity request.
internal static class NativeExitContract
{
    internal const string HelperName = "ApplicationExitHelper.dll";
    internal const string HelperSha256 = "6c424e3ed02d2767494e37352ffa3c32eb7999b00d5c4771c43073d675e724ae";
    internal const int HelperSize = 49237, ExportRva = 0x15a0, ImageSize = 0x14000;
    internal const string ExportName = "ApplicationExit";
    // push rbp; mov rbp,rsp; sub rsp,32; mov ecx,0; call exit. No relocated absolute addresses.
    internal static ReadOnlySpan<byte> EntryBytes => new byte[]
        { 0x55, 0x48, 0x89, 0xe5, 0x48, 0x83, 0xec, 0x20, 0xb9, 0, 0, 0, 0, 0xe8, 0x3e, 0x16, 0, 0 };

    internal static void ValidateFileIdentity(long length, string sha256) =>
        ControlFrame.Require(length == HelperSize && sha256 == HelperSha256, "native_exit_helper_identity");

    internal static void ValidateArchitecture(ReadOnlySpan<byte> file)
    {
        ControlFrame.Require(file.Length == HelperSize && file[0] == 'M' && file[1] == 'Z', "native_exit_pe_header");
        var pe = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(0x3c, 4));
        ControlFrame.Require(pe >= 0x40 && pe <= file.Length - 88, "native_exit_pe_offset");
        ControlFrame.Require(BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(pe, 4)) == 0x4550 &&
            BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(pe + 4, 2)) == 0x8664 &&
            (BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(pe + 22, 2)) & 0x2000) != 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(pe + 24, 2)) == 0x20b &&
            BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(pe + 80, 4)) == ImageSize,
            "native_exit_pe_architecture");
    }

    internal static void ValidateEntry(ReadOnlySpan<byte> entry) =>
        ControlFrame.Require(entry.SequenceEqual(EntryBytes), "native_exit_export_already_modified");
}

internal sealed class NativeExitGate
{
    private uint _thread;
    private int _registration, _signalled;
    internal uint RegistrationThread => _thread;
    internal bool Register(uint thread, Action install)
    {
        ControlFrame.Require(thread != 0, "native_exit_registration_thread");
        if (Interlocked.CompareExchange(ref _registration, 1, 0) != 0)
        {
            ControlFrame.Require(thread == _thread && Volatile.Read(ref _registration) == 2, "native_exit_registration_unavailable");
            return false;
        }
        _thread = thread;
        try { install(); Volatile.Write(ref _registration, 2); return true; }
        catch { Volatile.Write(ref _registration, 3); throw; }
    }
    // The export can be called from a different native thread. It must still
    // continue to the original; only the notification is process-wide once.
    internal bool TrySignal() => Interlocked.CompareExchange(ref _signalled, 1, 0) == 0;
}
