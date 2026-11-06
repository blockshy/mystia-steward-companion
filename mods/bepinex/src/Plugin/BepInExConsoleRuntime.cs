using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Logging;

namespace MystiaStewardCompanion.Plugin;

internal static class BepInExConsoleWindow
{
    public static BepInExConsoleWindowService Instance { get; } =
        new(new WindowsBepInExConsoleRuntime());
}

/// <summary>封装 BepInEx 控制台与 Win32 窗口操作，供可测试的可见性服务调用。</summary>
internal sealed class WindowsBepInExConsoleRuntime : IBepInExConsoleRuntime
{
    private const int ShowWindowHide = 0;
    private const int ShowWindowWithoutActivation = 4;
    private const uint MenuByCommand = 0;
    private const uint SystemCommandClose = 0xf060;
    private const uint MenuItemNotFound = uint.MaxValue;
    private static readonly object ListenerGate = new();

    public bool IsSupported => OperatingSystem.IsWindows();
    public bool IsActive => IsSupported && ConsoleManager.ConsoleActive;
    public nint WindowHandle => IsSupported ? GetConsoleWindow() : IntPtr.Zero;

    public bool IsVisible
    {
        get
        {
            if (!IsSupported) return false;
            var window = GetConsoleWindow();
            return window != IntPtr.Zero && IsWindowVisible(window);
        }
    }

    public void Create()
    {
        EnsureSupported();
        ConsoleManager.CreateConsole();
    }

    /// <summary>移除控制台系统菜单的关闭命令，避免将隐藏日志窗口误操作为关闭宿主进程。</summary>
    public void DisableCloseCommand()
    {
        EnsureSupported();
        var window = GetConsoleWindow();
        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException("The BepInEx console window handle is unavailable.");
        }

        var menu = GetSystemMenu(window, revert: false);
        if (menu == IntPtr.Zero)
        {
            throw new InvalidOperationException("The BepInEx console system menu is unavailable.");
        }

        if (GetMenuState(menu, SystemCommandClose, MenuByCommand) == MenuItemNotFound)
        {
            return;
        }

        if (!DeleteMenu(menu, SystemCommandClose, MenuByCommand))
        {
            throw new InvalidOperationException(
                $"Could not disable the BepInEx console close command (Win32 {Marshal.GetLastWin32Error()}).");
        }

        DrawMenuBar(window);
    }

    /// <summary>在全局监听器锁内按类型去重，防止重复显示控制台时重复输出同一日志。</summary>
    public void EnsureLogListener()
    {
        EnsureSupported();
        lock (ListenerGate)
        {
            if (Logger.Listeners.Any(listener => listener is ConsoleLogListener)) return;
            Logger.Listeners.Add(new ConsoleLogListener());
        }
    }

    public void ShowWithoutActivation()
    {
        ShowConsoleWindow(ShowWindowWithoutActivation);
    }

    public void Hide()
    {
        ShowConsoleWindow(ShowWindowHide);
    }

    private void ShowConsoleWindow(int command)
    {
        EnsureSupported();
        var window = GetConsoleWindow();
        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException("The BepInEx console window handle is unavailable.");
        }

        ShowWindow(window, command);
    }

    private void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("BepInEx console window control is only available on Windows.");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(
        IntPtr window,
        [MarshalAs(UnmanagedType.Bool)] bool revert);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawMenuBar(IntPtr window);
}
