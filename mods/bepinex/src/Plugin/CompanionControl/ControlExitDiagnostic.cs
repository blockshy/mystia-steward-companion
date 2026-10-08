using System.Diagnostics;
#if COMPANION_CONTROL_EXIT_DIAGNOSTIC
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
#endif

namespace MystiaStewardCompanion.Plugin.CompanionControl;

internal static class ControlExitDiagnostic
{
    // Conditional removes these calls from ordinary builds, including argument
    // evaluation. Diagnostic code never authorizes/retries an MSC1 operation.
    [Conditional("COMPANION_CONTROL_EXIT_DIAGNOSTIC")]
    internal static void Prepare(string configuredClient, uint thread, ref int session)
    {
#if COMPANION_CONTROL_EXIT_DIAGNOSTIC
        try
        {
            if (!_attempted) { _attempted = true; Open(configuredClient, thread); }
            if (_page == IntPtr.Zero) return;
            session = NewSession(_page, thread);
            Session(session, ExitSessionStage.Prepared);
        }
        catch { /* Missing Attached/session evidence fails the external diagnostic. */ }
#endif
    }
    [Conditional("COMPANION_CONTROL_EXIT_DIAGNOSTIC")]
    internal static void Global(ExitGlobalStage stage)
    {
#if COMPANION_CONTROL_EXIT_DIAGNOSTIC
        Mark(_page, ControlExitDiagnosticContract.GlobalOffset + (int)stage * 8);
#endif
    }
    [Conditional("COMPANION_CONTROL_EXIT_DIAGNOSTIC")]
    internal static void NativeThread(uint thread)
    {
#if COMPANION_CONTROL_EXIT_DIAGNOSTIC
        SetNativeThread(_page, thread);
#endif
    }
    [Conditional("COMPANION_CONTROL_EXIT_DIAGNOSTIC")]
    internal static void Session(int session, ExitSessionStage stage)
    {
#if COMPANION_CONTROL_EXIT_DIAGNOSTIC
        if (session is not (1 or 2)) return;
        Mark(_page, ControlExitDiagnosticContract.SessionOffset + (session - 1) * ControlExitDiagnosticContract.SessionSize +
            ControlExitDiagnosticContract.SessionStagesOffset + (int)stage * 8);
#endif
    }
    [Conditional("COMPANION_CONTROL_EXIT_DIAGNOSTIC")]
    internal static void BindClient(int session, uint pid, ulong creation)
    {
#if COMPANION_CONTROL_EXIT_DIAGNOSTIC
        if (session is not (1 or 2) || _page == IntPtr.Zero) return;
        Bind(_page, session, pid, creation);
        Session(session, ExitSessionStage.ClientBound);
#endif
    }
#if COMPANION_CONTROL_EXIT_DIAGNOSTIC
    private static bool _attempted;
    private static IntPtr _page;
    // Retain the view/handle for the entire native process. Never unmap it from
    // a callback or finalizer while another callback/worker could publish.
    private static SafeFileHandle? _mapping;
    internal static unsafe void Mark(IntPtr page, int offset)
    {
        if (page == IntPtr.Zero || offset < 544 || offset > 4096 - 8 || (offset & 7) != 0) return;
        ref var slot = ref *(long*)((byte*)page + offset);
        if (Interlocked.Read(ref slot) != 0) return;
        var sequence = Interlocked.Increment(ref *(long*)((byte*)page + ControlExitDiagnosticContract.CounterOffset));
        Interlocked.CompareExchange(ref slot, sequence, 0);
    }
    internal static unsafe int NewSession(IntPtr page, uint thread)
    {
        var session = Interlocked.Increment(ref *(long*)((byte*)page + ControlExitDiagnosticContract.SessionCountOffset));
        ControlFrame.Require(session is 1 or 2, "diagnostic_session_overflow");
        var record = (long*)((byte*)page + ControlExitDiagnosticContract.SessionBase((int)session));
        Interlocked.Exchange(ref record[3], thread);
        Interlocked.Exchange(ref record[0], session);
        return (int)session;
    }
    private static unsafe void Bind(IntPtr page, int session, uint pid, ulong creation)
    {
        var record = (long*)((byte*)page + ControlExitDiagnosticContract.SessionBase(session));
        Interlocked.Exchange(ref record[1], pid);
        Interlocked.Exchange(ref record[2], checked((long)creation));
    }
    private static void Open(string configuredClient, uint thread)
    {
        ControlFrame.Require(OperatingSystem.IsWindows() && Environment.Is64BitProcess && thread == ControlWindows.CurrentThread && thread != 0,
            "diagnostic_platform_thread");
        var assembly = typeof(ControlExitDiagnostic).Assembly;
        string Metadata(string key) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == key).Value ?? "";
        ControlFrame.Require(Metadata("CompanionControlExitDiagnostic") == "true", "diagnostic_compiled_flag");
        var sha = Metadata("CompanionControlBuildGitSha");
        var plugin = ControlWindows.ConfiguredPath(assembly.Location);
        var match = Regex.Match(plugin, @"\AD:\\dev\\mystia-node\\runs\\([A-Za-z0-9][A-Za-z0-9_-]{0,79})\\workspace\\game\\BepInEx\\plugins\\mystia-steward-companion\\MystiaStewardCompanion\.BepInEx\.dll\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        ControlFrame.Require(match.Success, "diagnostic_fixed_assembly");
        var run = match.Groups[1].Value; var root = @"D:\dev\mystia-node\runs\" + run;
        var clientPath = ControlWindows.ConfiguredPath(configuredClient);
        ControlFrame.Require(string.Equals(clientPath, root + @"\payload\mystia-steward-companion-window-probe.exe", StringComparison.OrdinalIgnoreCase), "diagnostic_client_path");
        using var own = ControlWindows.Open((uint)Environment.ProcessId);
        var game = ControlWindows.Observe(own);
        ControlFrame.Require(string.Equals(game.Path, root + @"\workspace\game\Touhou Mystia Izakaya.exe", StringComparison.OrdinalIgnoreCase), "diagnostic_game_path");
        byte[] Read(string path, int limit)
        {
            ControlWindows.ConfiguredPath(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ControlFrame.Require(file.Length is > 0 && file.Length <= limit, "diagnostic_file_bound");
            var bytes = new byte[checked((int)file.Length)]; var offset = 0;
            while (offset < bytes.Length)
            { var count = file.Read(bytes, offset, bytes.Length - offset); ControlFrame.Require(count > 0, "diagnostic_file_eof"); offset += count; }
            return bytes;
        }
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var auth = JsonDocument.Parse(Read(root + @"\control-exit-diagnostic.json", 32768));
        var properties = auth.RootElement.EnumerateObject().ToArray();
        ControlFrame.Require(properties.Length == 7 && properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() == 7,
            "diagnostic_authorization_schema");
        var value = auth.RootElement;
        ControlFrame.Require(value.GetProperty("schemaVersion").GetInt32() == 1 && value.GetProperty("kind").GetString() == "control-exit-diagnostic-authorization" &&
            value.GetProperty("runId").GetString() == run && value.GetProperty("gitSha").GetString() == sha && value.GetProperty("diagnosticOnly").GetBoolean(),
            "diagnostic_authorization");
        var manifestBytes = Read(root + @"\workspace\mod-build-evidence.json", 1048576);
        var manifestHash = Hash(manifestBytes);
        ControlFrame.Require(value.GetProperty("modBuildEvidenceSha256").GetString() == manifestHash &&
            value.GetProperty("preparedEvidenceSha256").GetString() == Hash(Read(root + @"\workspace\prepared-evidence.json", 1048576)),
            "diagnostic_authorization_hash");
        using var manifest = JsonDocument.Parse(manifestBytes);
        ControlFrame.Require(manifest.RootElement.GetProperty("exitDiagnostic").GetBoolean() &&
            manifest.RootElement.GetProperty("compiledGitSha").GetString() == sha, "diagnostic_manifest_flag");
        var mapping = OpenFileMappingW(6, false, ControlExitDiagnosticContract.MappingName(game.Pid, game.Creation));
        ControlFrame.Require(!mapping.IsInvalid, "diagnostic_mapping_open");
        var page = MapViewOfFile(mapping, 6, 0, 0, (UIntPtr)ControlExitDiagnosticContract.PageSize);
        if (page == IntPtr.Zero) { mapping.Dispose(); throw new ControlFailure("diagnostic_mapping_view"); }
        try
        {
            var bytes = new byte[ControlExitDiagnosticContract.PageSize]; Marshal.Copy(page, bytes, 0, bytes.Length);
            var header = ControlExitDiagnosticContract.ValidateHeader(bytes, game.Pid, game.Creation, sha, run);
            using var controllerHandle = ControlWindows.Open(header.ControllerPid);
            var controller = ControlWindows.Observe(controllerHandle);
            ControlWindows.CheckClient(game, controller, clientPath);
            ControlFrame.Require(ActualParent(game.Pid) == controller.Pid && controller.Creation == header.ControllerCreation && controller.Session == header.Session &&
                header.ManifestSha256 == manifestHash && header.ModSha256 == Hash(Read(plugin, 16777216)) &&
                header.ClientSha256 == Hash(Read(clientPath, 67108864)) && header.GameSha256 == Hash(Read(game.Path, 67108864)), "diagnostic_mapping_identity");
            _mapping = mapping; _page = page;
            SetMainThread(page, thread);
            Global(ExitGlobalStage.Attached);
        }
        catch { UnmapViewOfFile(page); mapping.Dispose(); throw; }
    }
    private static unsafe void SetMainThread(IntPtr page, uint thread) =>
        Interlocked.Exchange(ref *(long*)((byte*)page + ControlExitDiagnosticContract.MainThreadOffset), thread);
    private static unsafe void SetNativeThread(IntPtr page, uint thread)
    {
        if (page != IntPtr.Zero)
            Interlocked.CompareExchange(ref *(long*)((byte*)page + ControlExitDiagnosticContract.NativeThreadOffset), thread, 0);
    }
    private static uint ActualParent(uint pid)
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        ControlFrame.Require(!snapshot.IsInvalid, "diagnostic_parent_snapshot");
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (Process32FirstW(snapshot, ref entry))
            do { if (entry.ProcessId == pid) return entry.ParentProcessId; } while (Process32NextW(snapshot, ref entry));
        throw new ControlFailure("diagnostic_parent_missing");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId; public IntPtr DefaultHeap; public uint ModuleId, Threads, ParentProcessId;
        public int Priority; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle OpenFileMappingW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr MapViewOfFile(SafeFileHandle mapping, uint access, uint high, uint low, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnmapViewOfFile(IntPtr page);
#endif
}
