using System.Runtime.InteropServices;

namespace pxdAudioSwitcher.Platform;

/// <summary>
/// Reports the program (its exe name, e.g. "discord.exe") whenever a different window comes to the front. Uses an
/// out-of-context WinEvent hook, so nothing is injected into other programs and callbacks arrive on the UI thread.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 3;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly WinEventProc _proc;
    private readonly Action<string, int> _onForeground;
    private IntPtr _hook;

    public ForegroundWatcher(Action<string, int> onForeground)
    {
        _onForeground = onForeground;
        _proc = OnEvent;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _proc, 0, 0, 0);
    }

    private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero)
            return;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (ExeName(pid) is { } exe)
            _onForeground(exe, (int)pid);
    }

    /// <summary>The lower-case file name of a process's exe, or null if Windows won't say (it ended, or is protected).</summary>
    public static string? ExeName(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? Path.GetFileName(new string(buffer, 0, size)).ToLowerInvariant()
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
            UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, char[] name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
