using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MystiaStewardCompanion.FocusProbe;

internal static class NativeMethods
{
    private const uint QueryAndSynchronize = 0x00101000;
    internal static uint CurrentThreadId() => GetCurrentThreadId();
    internal static SafeProcessHandle OpenRetained(ulong pid)
    {
        var result = OpenProcess(QueryAndSynchronize, false, checked((uint)pid));
        if (result.IsInvalid) { result.Dispose(); throw Error("OpenProcess"); }
        return result;
    }
    internal static ProcessObservation ObserveProcess(SafeProcessHandle process)
    {
        var pid = GetProcessId(process); Check(pid != 0, "GetProcessId");
        Check(GetProcessTimes(process, out var creation, out _, out _, out _), "GetProcessTimes");
        var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
        Check(QueryFullProcessImageNameW(process, 0, path, ref length), "QueryFullProcessImageName");
        Check(ProcessIdToSessionId(pid, out var session), "ProcessIdToSessionId");
        var wait = WaitForSingleObject(process, 0);
        Check(wait is 0 or 258, "WaitForSingleObject");
        return new ProcessObservation(pid, unchecked((ulong)creation), Path.GetFullPath(path.ToString()), session, wait == 258);
    }
    internal static SafePipeHandle OpenPipe(string name)
    {
        // Avoid GENERIC_WRITE: FILE_CREATE_PIPE_INSTANCE aliases a generic-write bit.
        var handle = CreateFileW(@"\\.\pipe\" + name, 0x80000002, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw Error("CreateFile(named pipe)"); }
        return handle;
    }
    internal static uint PipeServerPid(SafePipeHandle pipe)
    { Check(GetNamedPipeServerProcessId(pipe, out var pid), "GetNamedPipeServerProcessId"); Guard.That(pid != 0, "Pipe server PID is zero."); return pid; }
    internal static WindowObservation Window(ulong hwnd)
    {
        var window = new IntPtr(checked((long)hwnd));
        Check(IsWindow(window), "IsWindow");
        var thread = GetWindowThreadProcessId(window, out var pid);
        Guard.That(thread != 0 && pid != 0, "Window owner is unavailable.");
        return new WindowObservation(hwnd, pid, thread);
    }
    internal static WindowObservation Foreground()
    {
        var hwnd = GetForegroundWindow();
        return hwnd == IntPtr.Zero ? new WindowObservation(0, 0, 0) : Window(unchecked((ulong)hwnd.ToInt64()));
    }
    internal static WindowObservation ProbeWindow(SessionDescriptor descriptor)
    {
        var observation = Window(descriptor.ProbeHwnd);
        var hwnd = new IntPtr(checked((long)descriptor.ProbeHwnd));
        Guard.That(ClassName(hwnd) == "FLUTTER_RUNNER_WIN32_WINDOW" && GetWindow(hwnd, 4) == IntPtr.Zero && GetAncestor(hwnd, 2) == hwnd, "Flutter HWND is not its exact unowned top-level window.");
        return observation;
    }
    internal static IReadOnlyList<WindowObservation> GameWindows(ulong gamePid)
    {
        var windows = new List<WindowObservation>(); Exception? callbackError = null;
        EnumWindowsCallback callback = (hwnd, _) =>
        {
            try
            {
                var thread = GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == gamePid && IsWindowVisible(hwnd) && GetWindow(hwnd, 4) == IntPtr.Zero && ClassName(hwnd) == "UnityWndClass")
                {
                    Check(GetClientRect(hwnd, out var area), "GetClientRect");
                    if (area.Right > area.Left && area.Bottom > area.Top)
                        windows.Add(new WindowObservation(unchecked((ulong)hwnd.ToInt64()), pid, thread));
                }
                return true;
            }
            catch (Exception ex) { callbackError = ex; return false; }
        };
        var ok = EnumWindows(callback, IntPtr.Zero);
        if (callbackError != null) throw callbackError;
        Check(ok, "EnumWindows");
        return windows;
    }
    internal static GrantCallResult Allow(uint targetPid)
    {
        SetLastError(0);
        var result = AllowSetForegroundWindow(targetPid);
        return new GrantCallResult(result, unchecked((uint)Marshal.GetLastWin32Error()));
    }
    private static string ClassName(IntPtr hwnd)
    { var text = new StringBuilder(256); Check(GetClassNameW(hwnd, text, text.Capacity) > 0, "GetClassName"); return text.ToString(); }
    private static void Check(bool condition, string operation) { if (!condition) throw Error(operation); }
    private static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation + " failed.");

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr parameter);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateFileW(string name, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr hwnd, out Rect area);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint pid);
}
