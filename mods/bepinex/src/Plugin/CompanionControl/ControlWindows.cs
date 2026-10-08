using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MystiaStewardCompanion.Plugin.CompanionControl;

internal sealed record ControlProcess(uint Pid, ulong Creation, string Path, uint Session, string User);
internal readonly record struct ControlWindow(ulong Hwnd, uint Pid, uint Thread);

internal static class ControlWindows
{
    internal static bool RetainedExited(SafeProcessHandle handle, ControlProcess expected)
    {
        if (handle.IsClosed || handle.IsInvalid) return false;
        var wait = WaitForSingleObject(handle, 0);
        Check(wait is 0 or 258, "retained_exit_wait");
        if (wait != 0) return false;
        Check(GetProcessId(handle) == expected.Pid && GetProcessTimes(handle, out var creation, out _, out _, out _) &&
            checked((ulong)creation) == expected.Creation, "retained_exit_identity");
        return true;
    }
    internal static uint CurrentThread => OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0;
    internal static SafeProcessHandle Open(uint pid)
    {
        var handle = OpenProcess(0x00101000, false, pid);
        if (handle.IsInvalid) { handle.Dispose(); throw Failure("process_open"); }
        return handle;
    }
    internal static ControlProcess Observe(SafeProcessHandle handle)
    {
        Check(WaitForSingleObject(handle, 0) == 258, "process_not_alive");
        var pid = GetProcessId(handle); Check(pid != 0, "process_pid");
        Check(GetProcessTimes(handle, out var creation, out _, out _, out _) && creation > 0, "process_creation");
        var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
        Check(QueryFullProcessImageNameW(handle, 0, path, ref length), "process_path");
        Check(ProcessIdToSessionId(pid, out var session), "process_session");
        Check(OpenProcessToken(handle, 8, out var token), "process_token");
        using (token)
        {
            GetTokenInformation(token, 1, IntPtr.Zero, 0, out var size);
            Check(size is > 0 and < 65536, "token_size");
            var buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                Check(GetTokenInformation(token, 1, buffer, size, out _), "token_user");
                var sid = Marshal.ReadIntPtr(buffer);
                Check(IsValidSid(sid), "token_sid");
                var bytes = new byte[checked((int)GetLengthSid(sid))];
                Check(bytes.Length is > 0 and < 256, "token_sid_size");
                Marshal.Copy(sid, bytes, 0, bytes.Length);
                return new ControlProcess(pid, checked((ulong)creation), System.IO.Path.GetFullPath(path.ToString()), session, Convert.ToBase64String(bytes));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    internal static void CheckClient(ControlProcess game, ControlProcess client, string configuredPath)
    {
        ControlFrame.Require(client.Pid != game.Pid && client.Session == game.Session && client.User == game.User &&
            string.Equals(client.Path, configuredPath, StringComparison.OrdinalIgnoreCase), "client_identity");
    }
    internal static string ConfiguredPath(string configured)
    {
        ControlFrame.Require(!string.IsNullOrWhiteSpace(configured), "configured_path_required");
        var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
        ControlFrame.Require(System.IO.Path.IsPathFullyQualified(expanded) && !expanded.StartsWith(@"\\", StringComparison.Ordinal), "configured_path_absolute");
        var path = System.IO.Path.GetFullPath(expanded);
        ControlFrame.Require(File.Exists(path), "configured_path_missing");
        for (var item = path; !string.IsNullOrEmpty(item); item = System.IO.Path.GetDirectoryName(item))
            ControlFrame.Require((File.GetAttributes(item) & FileAttributes.ReparsePoint) == 0, "configured_path_reparse");
        return path;
    }
    // Read-only discovery. A wildcard/IPv6/ambiguous occupant is not permission to start another process.
    internal static uint ListenerPid()
    {
        return ControlListenerIdentity.Resolve(ReadTable(2), ReadTable(23));
    }
    private static byte[] ReadTable(uint family)
    {
        uint size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
        ControlFrame.Require(status is 0 or 122 && size is >= 4 and <= 1048576, "tcp_table_size");
        // Table growth is an observation failure, not an invitation to redispatch a control request.
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            status = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
            ControlFrame.Require(status == 0, "tcp_table_read");
            var data = new byte[size]; Marshal.Copy(buffer, data, 0, data.Length);
            return data;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static SafePipeHandle OpenPipe(string name)
    {
        var handle = CreateFileW(@"\\.\pipe\" + name, 0x80000002, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw Failure("pipe_open"); }
        return handle;
    }
    internal static uint PipeServer(SafePipeHandle pipe)
    { Check(GetNamedPipeServerProcessId(pipe, out var pid) && pid != 0, "pipe_peer"); return pid; }
    internal static void RequireQuietPipe(SafePipeHandle pipe)
    {
        Check(PeekNamedPipe(pipe, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero), "pipe_peek");
        ControlFrame.Require(available == 0, "pipe_unsolicited_data");
    }
    internal static ControlWindow Window(ulong hwnd)
    {
        ControlFrame.Require(hwnd is > 0 and <= long.MaxValue, "window_range");
        var native = new IntPtr(checked((long)hwnd)); Check(IsWindow(native), "window_missing");
        var thread = GetWindowThreadProcessId(native, out var pid);
        Check(thread != 0 && pid != 0, "window_owner");
        return new ControlWindow(hwnd, pid, thread);
    }
    internal static ControlWindow Foreground()
    {
        var window = GetForegroundWindow();
        return window == IntPtr.Zero ? default : Window(checked((ulong)window.ToInt64()));
    }
    internal static ControlWindow UniqueWindow(uint process, bool game)
    {
        var windows = new List<ControlWindow>(); Exception? error = null;
        EnumWindowsCallback callback = (hwnd, _) =>
        {
            try
            {
                var thread = GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != process || GetWindow(hwnd, 4) != IntPtr.Zero || GetAncestor(hwnd, 2) != hwnd) return true;
                var name = new StringBuilder(256);
                Check(GetClassNameW(hwnd, name, name.Capacity) > 0, "window_class");
                if (name.ToString() != (game ? "UnityWndClass" : "FLUTTER_RUNNER_WIN32_WINDOW")) return true;
                if (game)
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    Check(GetClientRect(hwnd, out var area), "window_rect");
                    if (area.Right <= area.Left || area.Bottom <= area.Top) return true;
                }
                windows.Add(new ControlWindow(checked((ulong)hwnd.ToInt64()), pid, thread));
                return true;
            }
            catch (Exception ex) { error = ex; return false; }
        };
        var ok = EnumWindows(callback, IntPtr.Zero);
        if (error != null) throw error;
        Check(ok, "window_enumeration");
        ControlFrame.Require(windows.Count == 1, game ? "game_window_ambiguous" : "client_window_ambiguous");
        return windows[0];
    }
    internal static (bool Succeeded, uint Error) Allow(uint target)
    {
        SetLastError(0);
        var success = AllowSetForegroundWindow(target);
        return (success, unchecked((uint)Marshal.GetLastWin32Error()));
    }
    internal static void VerifyActivation(ControlFrame ack)
    {
        var client = new IntPtr(checked((long)ack[ControlField.ClientHwnd]));
        var focus = Window(ack[ControlField.ClientFocusHwnd]);
        ControlFrame.Require(focus.Pid == ack[ControlField.ClientPid] && focus.Thread == ack[ControlField.ClientThread] &&
            GetAncestor(new IntPtr(checked((long)focus.Hwnd)), 2) == client, "client_focus_owner");
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        Check(GetGUIThreadInfo(checked((uint)ack[ControlField.ClientThread]), ref info), "client_focus_query");
        ControlFrame.Require(info.Focus == new IntPtr(checked((long)focus.Hwnd)) && IsWindowVisible(client) && IsWindowEnabled(client) &&
            (GetWindowLongPtrW(client, -20).ToInt64() & (0x20L | 0x08000000L)) == 0, "client_interaction");
        var foreground = Foreground();
        ControlFrame.Require(foreground.Hwnd == ack[ControlField.ClientHwnd] && foreground.Pid == ack[ControlField.ClientPid], "client_foreground_changed");
    }
    private static ControlFailure Failure(string code) => new(code + "_" + Marshal.GetLastWin32Error());
    private static void Check(bool condition, string code) { if (!condition) throw Failure(code); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    { public uint Size, Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(SafeProcessHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsValidSid(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern uint GetLengthSid(IntPtr sid);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr left);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr hwnd, out Rect area);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint pid);
}
