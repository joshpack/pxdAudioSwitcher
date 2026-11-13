using System.Runtime.InteropServices;

namespace pxdAudioSwitcher.Platform;

/// <summary>
/// Presses one key combination in the program in front, for games that only move to a new sound device when told to
/// restart their sound. Used once per switch the user made, never on its own.
/// </summary>
public static class KeySender
{
    /// <summary>
    /// In the background: waits until the user has let go of every modifier (their own hotkey is usually still held),
    /// gives the device switch a moment to settle, checks that <paramref name="exe"/> is still in front, then presses
    /// <paramref name="key"/> there. Returns at once.
    /// </summary>
    public static void PressInGame(Hotkey key, string exe) => Task.Run(async () =>
    {
        for (var waited = 0; waited < 2000 && AnyModifierDown(); waited += 25)
            await Task.Delay(25);
        await Task.Delay(250);
        if (!string.Equals(ForegroundExe(), exe, StringComparison.OrdinalIgnoreCase))
            return;

        var down = Modifiers(key.Mods).Append(key.Vk).ToList();
        var inputs = down.Select(vk => Make(vk, up: false))
            .Concat(Enumerable.Reverse(down).Select(vk => Make(vk, up: true)))
            .ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    });

    /// <summary>Lower-case exe name of the window in front, or null.</summary>
    public static string? ForegroundExe()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        return ForegroundWatcher.ExeName(pid);
    }

    private static IEnumerable<int> Modifiers(int mods)
    {
        if ((mods & Hotkey.Ctrl) != 0) yield return 0x11;
        if ((mods & Hotkey.Alt) != 0) yield return 0x12;
        if ((mods & Hotkey.Shift) != 0) yield return 0x10;
        if ((mods & Hotkey.Win) != 0) yield return 0x5B;
    }

    private static bool AnyModifierDown() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    /// <summary>
    /// A key event with both the virtual key and its scan code: games that read raw keyboard input go by the scan code.
    /// </summary>
    private static Input Make(int vk, bool up)
    {
        var flags = up ? 0x0002u : 0u;                       // KEYEVENTF_KEYUP
        if (vk is >= 0x21 and <= 0x2E or 0x5B or 0x5C or 0x6F)   // Page Up…Delete, arrows, Win, Num /: extended keys
            flags |= 0x0001;                                     // KEYEVENTF_EXTENDEDKEY
        return new Input
        {
            Type = 1,
            Key = new KeybdInput { Vk = (ushort)vk, Scan = (ushort)MapVirtualKey((uint)vk, 0), Flags = flags },
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>INPUT with the keyboard member; padded to the size of the largest member (MOUSEINPUT).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeybdInput Key;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
