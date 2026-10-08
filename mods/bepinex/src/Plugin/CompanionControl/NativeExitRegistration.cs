using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Unity.IL2CPP.Hook;
using Microsoft.Win32.SafeHandles;

namespace MystiaStewardCompanion.Plugin.CompanionControl;

internal static class NativeExitRegistration
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeExit();
    private static readonly NativeExitGate Gate = new();
    private static readonly NativeExit Callback = OnNativeExit;
    private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("CompanionControlNativeExit");
    // Process lifetime roots: never release code, trampoline, callback or the
    // no-write/no-delete file handle while any native caller can reach the hook.
    private static NativeExit? _original;
    private static INativeDetour? _detour;
    private static FileStream? _file;
    private static IntPtr _module;

    internal static void EnsureRegistered(uint updateThread)
    {
        try
        {
            ControlFrame.Require(OperatingSystem.IsWindows() && Environment.Is64BitProcess &&
                RuntimeInformation.ProcessArchitecture == Architecture.X64 && updateThread == ControlWindows.CurrentThread,
                "native_exit_registration_platform_thread");
            if (Gate.Register(updateThread, Install))
                ControlExitDiagnostic.Global(ExitGlobalStage.NativeRegistered);
        }
        catch
        {
            ControlExitDiagnostic.Global(ExitGlobalStage.NativeRegistrationFailed);
            throw;
        }
    }

    private static void Install()
    {
        using var process = ControlWindows.Open((uint)Environment.ProcessId);
        var game = ControlWindows.Observe(process);
        var directory = Path.GetDirectoryName(ControlWindows.ConfiguredPath(game.Path))!;
        var path = ControlWindows.ConfiguredPath(Path.Combine(directory, Path.GetFileNameWithoutExtension(game.Path) + "_Data",
            "Plugins", "x86_64", NativeExitContract.HelperName));
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            CheckFilePath(file.SafeFileHandle, path);
            ControlFrame.Require(file.Length == NativeExitContract.HelperSize, "native_exit_helper_size");
            var bytes = new byte[NativeExitContract.HelperSize]; var offset = 0;
            while (offset < bytes.Length)
            {
                var count = file.Read(bytes, offset, bytes.Length - offset);
                ControlFrame.Require(count > 0, "native_exit_helper_eof"); offset += count;
            }
            NativeExitContract.ValidateFileIdentity(file.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            NativeExitContract.ValidateArchitecture(bytes);
            var existing = FindModule(path);
            var loadedHere = existing == IntPtr.Zero;
            IntPtr module;
            if (loadedHere)
            {
                // The exact helper has only kernel32/ucrtbase imports. Its
                // DllMain returns TRUE; CRT initialization never calls the exit
                // export. Do not add the plugin directory to a global search path.
                module = LoadLibraryExW(path, IntPtr.Zero, 0x800); // LOAD_LIBRARY_SEARCH_SYSTEM32
                ControlFrame.Require(module != IntPtr.Zero, "native_exit_helper_load");
            }
            else
            {
                ControlFrame.Require(GetModuleHandleExW(0, path, out module) && module == existing, "native_exit_module_reference");
            }
            // Keep the loader reference even if a later validation refuses the
            // hook. Calling FreeLibrary in an exit registration failure is unsafe.
            _module = module;
            ControlFrame.Require(FindModule(path) == module && string.Equals(ModulePath(module), path, StringComparison.OrdinalIgnoreCase),
                "native_exit_loaded_module_identity");
            ControlWindows.ConfiguredPath(path); CheckFilePath(file.SafeFileHandle, path);
            var export = GetProcAddress(module, NativeExitContract.ExportName);
            ControlFrame.Require(export == IntPtr.Add(module, NativeExitContract.ExportRva), "native_exit_export_identity");
            var entry = new byte[NativeExitContract.EntryBytes.Length]; Marshal.Copy(export, entry, 0, entry.Length);
            NativeExitContract.ValidateEntry(entry);

            // CreateAndApply would expose the callback before _original is
            // published. Prepare and root everything first, then apply once.
            var detour = INativeDetour.Create(export, Callback);
            var original = detour.GenerateTrampoline<NativeExit>();
            ControlFrame.Require(detour.TrampolinePtr != IntPtr.Zero && detour.TrampolinePtr != export, "native_exit_trampoline");
            _file = file; file = null!; _detour = detour;
            Volatile.Write(ref _original, original);
            detour.Apply();
            ControlFrame.Require(detour.IsValid && detour.IsApplied && detour.OriginalMethodPtr == export, "native_exit_detour_apply");
            WriteLog("native_exit_registered", loadedHere ? "loaded_exact_helper" : "retained_loaded_helper", ControlWindows.CurrentThread, Gate.RegistrationThread);
        }
        finally { file?.Dispose(); }
    }

    private static void OnNativeExit()
    {
        try
        {
            var budget = new ControlExitBudget();
            if (Gate.TrySignal())
            {
                var thread = ControlWindows.CurrentThread;
                ControlExitDiagnostic.NativeThread(thread);
                ControlExitDiagnostic.Global(ExitGlobalStage.NativeEntered);
                // Only the dispatched worker may take lifecycle/session locks
                // or log. This native thread observes its bounded completion.
                var outcome = CompanionProcessLauncher.NotifyNativeExitAndWait(budget, thread, Gate.RegistrationThread);
                ControlExitDiagnostic.Global(outcome == ControlExitOutcome.Acknowledged ?
                    ExitGlobalStage.NativeAcknowledged : ExitGlobalStage.NativeUnconfirmed);
            }
        }
        catch
        {
            try { ControlExitDiagnostic.Global(ExitGlobalStage.NativeFailed); }
            catch { /* A diagnostic failure cannot alter the game's exit. */ }
        }
        finally
        {
            try { ControlExitDiagnostic.Global(ExitGlobalStage.NativeOriginalInvoked); }
            catch { /* Always continue through the exact original trampoline. */ }
            Volatile.Read(ref _original)!();
        }
    }

    // Called only by the managed dispatch worker. This proves its phase, not
    // that the native caller observed its result before the deadline.
    internal static void LogWorker(string action, string outcome, uint nativeThread, uint registrationThread) =>
        WriteLog(action, outcome, nativeThread, registrationThread);

    private static void WriteLog(string action, string outcome, uint thread, uint registrationThread)
    {
        try
        {
            Log.LogInfo($"companion_control protocol=IdentityPipeV1 event={action} outcome={outcome} gamePid={Environment.ProcessId} nativeThread={thread} registrationThread={registrationThread} helperSha256={NativeExitContract.HelperSha256} exportRva=0x15a0");
        }
        catch { /* Logging cannot prevent the original native termination. */ }
    }

    private static IntPtr FindModule(string expected)
    {
        using var process = Process.GetCurrentProcess();
        var candidates = process.Modules.Cast<ProcessModule>().Where(module =>
            string.Equals(module.ModuleName, NativeExitContract.HelperName, StringComparison.OrdinalIgnoreCase)).ToArray();
        ControlFrame.Require(candidates.Length <= 1, "native_exit_module_ambiguous");
        if (candidates.Length == 0) return IntPtr.Zero;
        var candidate = candidates[0];
        var fileName = candidate.FileName ?? throw new ControlFailure("native_exit_module_path_missing");
        ControlFrame.Require(string.Equals(ControlWindows.ConfiguredPath(fileName), expected, StringComparison.OrdinalIgnoreCase),
            "native_exit_foreign_module");
        return candidate.BaseAddress;
    }
    private static string ModulePath(IntPtr module)
    {
        var path = new StringBuilder(32768); var count = GetModuleFileNameW(module, path, path.Capacity);
        ControlFrame.Require(count > 0 && count < path.Capacity, "native_exit_module_path");
        return ControlWindows.ConfiguredPath(path.ToString());
    }
    private static void CheckFilePath(SafeFileHandle file, string expected)
    {
        var path = new StringBuilder(32768); var count = GetFinalPathNameByHandleW(file, path, (uint)path.Capacity, 0);
        ControlFrame.Require(count > 0 && count < path.Capacity, "native_exit_file_final_path");
        var actual = path.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        ControlFrame.Require(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase), "native_exit_file_path_changed");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleExW(uint flags, string name, out IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
