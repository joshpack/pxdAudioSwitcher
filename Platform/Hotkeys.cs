using System.Runtime.InteropServices;
using Avalonia.Input;

namespace pxdAudioSwitcher.Platform;

/// <summary>A key combination: Windows modifier flags (Alt 1, Ctrl 2, Shift 4, Win 8) plus a virtual-key code.</summary>
public sealed record Hotkey(int Mods, int Vk)
{
    public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Mods & Ctrl) != 0) parts.Add("Ctrl");
        if ((Mods & Alt) != 0) parts.Add("Alt");
        if ((Mods & Shift) != 0) parts.Add("Shift");
        if ((Mods & Win) != 0) parts.Add("Win");
        parts.Add(KeyName(Vk));
        return string.Join(" + ", parts);
    }

    /// <summary>Keys that are fine on their own, without a modifier (nobody types them by accident).</summary>
    public static bool StandsAlone(int vk) => vk is >= 0x7C and <= 0x87 or >= 0xAD and <= 0xB3 or 0x13 or 0x91;

    public static string KeyName(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x20 => "Space", 0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x2D => "Insert", 0x2E => "Delete",
        0x13 => "Pause", 0x91 => "Scroll Lock", 0x0D => "Enter", 0x09 => "Tab",
        0x6A => "Num *", 0x6B => "Num +", 0x6D => "Num -", 0x6E => "Num .", 0x6F => "Num /",
        0xAD => "Mute", 0xAE => "Volume Down", 0xAF => "Volume Up", 0xB0 => "Next Track", 0xB1 => "Previous Track",
        0xB2 => "Stop", 0xB3 => "Play/Pause",
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
        0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        _ => $"Key {vk}",
    };

    /// <summary>The Windows virtual-key code for an Avalonia key, or 0 for keys that can't be part of a hotkey.</summary>
    public static int VkFromKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 0x41 + (key - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30 + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => 0x70 + (key - Key.F1),
        Key.Space => 0x20, Key.PageUp => 0x21, Key.PageDown => 0x22, Key.End => 0x23, Key.Home => 0x24,
        Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28, Key.Insert => 0x2D, Key.Delete => 0x2E,
        Key.Pause => 0x13, Key.Scroll => 0x91, Key.Enter => 0x0D,
        Key.Multiply => 0x6A, Key.Add => 0x6B, Key.Subtract => 0x6D, Key.Decimal => 0x6E, Key.Divide => 0x6F,
        Key.VolumeMute => 0xAD, Key.VolumeDown => 0xAE, Key.VolumeUp => 0xAF,
        Key.MediaNextTrack => 0xB0, Key.MediaPreviousTrack => 0xB1, Key.MediaStop => 0xB2, Key.MediaPlayPause => 0xB3,
        Key.OemSemicolon => 0xBA, Key.OemPlus => 0xBB, Key.OemComma => 0xBC, Key.OemMinus => 0xBD, Key.OemPeriod => 0xBE,
        Key.OemQuestion => 0xBF, Key.OemTilde => 0xC0, Key.OemOpenBrackets => 0xDB, Key.OemPipe => 0xDC,
        Key.OemCloseBrackets => 0xDD, Key.OemQuotes => 0xDE,
        _ => 0,
    };

    public static int ModsFrom(KeyModifiers m) =>
        (m.HasFlag(KeyModifiers.Control) ? Ctrl : 0) | (m.HasFlag(KeyModifiers.Alt) ? Alt : 0)
        | (m.HasFlag(KeyModifiers.Shift) ? Shift : 0) | (m.HasFlag(KeyModifiers.Meta) ? Win : 0);
}

/// <summary>
/// System-wide hotkeys through RegisterHotKey on a hidden message-only window. The window lives on the UI thread, whose
/// message loop (Avalonia's) delivers WM_HOTKEY to it, so the callbacks run on the UI thread too.
/// <para>
/// Many games ask Windows to ignore RegisterHotKey hotkeys while they are in front (raw input with RIDEV_NOHOTKEYS),
/// so a low-level keyboard hook also watches for exactly the registered combinations. It only compares each key with
/// the registered hotkeys, holds back just those combinations (so the game doesn't act on them as well) and posts the
/// action to the window, returning at once. Everything else passes straight through untouched.
/// </para>
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WmFire = 0x8001;        // WM_APP + 1: the hook saw a hotkey; wParam is its id
    private const int WmRehook = 0x8002;      // WM_APP + 2: put the hook back (Windows drops slow hooks silently)
    private const int ModNoRepeat = 0x4000;
    private const int WhKeyboardLl = 13;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly WndProc _proc;   // kept in a field so the delegate is never collected while Windows holds it
    private readonly HookProc _hookProc;
    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly Dictionary<int, Hotkey> _keys = new();
    private readonly HashSet<int> _held = new();   // keys whose press was held back; their repeats and release are too
    private readonly Timer _rehook;
    private IntPtr _hook;
    private int _nextId = 1;

    public HotkeyManager()
    {
        _proc = OnMessage;
        _hookProc = OnKey;
        var cls = new WndClassEx
        {
            Size = Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            Instance = GetModuleHandle(null),
            ClassName = "pxdAudioSwitcherHotkeys",
        };
        RegisterClassEx(ref cls);
        _hwnd = CreateWindowEx(0, cls.ClassName, "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, cls.Instance, IntPtr.Zero);
        Hook();
        // Windows quietly removes a low-level hook whose thread was ever too slow to answer; re-hooking every 30 s
        // (on the UI thread, through the window) means a dropped hook comes back on its own.
        _rehook = new Timer(_ => PostMessage(_hwnd, WmRehook, IntPtr.Zero, IntPtr.Zero), null, 30_000, 30_000);
    }

    /// <summary>Registers a hotkey. Returns false when another program already owns that combination.</summary>
    public bool Register(Hotkey key, Action action)
    {
        var id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, key.Mods | ModNoRepeat, key.Vk))
            return false;
        _actions[id] = action;
        _keys[id] = key;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys)
            UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        _keys.Clear();
        _held.Clear();
    }

    private void Hook()
    {
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
    }

    /// <summary>
    /// Runs for every key on the system, so it does as little as possible: compare with the registered hotkeys, post the
    /// match to the window, and hold back only that key. It never runs the action itself (switching takes too long for
    /// a keyboard hook).
    /// </summary>
    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && _keys.Count > 0)
        {
            var msg = message.ToInt32();
            var vk = Marshal.ReadInt32(data);
            // Keys typed by programs (our own game key press included) never count as a hotkey: only real presses do.
            if ((Marshal.ReadInt32(data, 8) & 0x10) != 0)   // KBDLLHOOKSTRUCT.flags & LLKHF_INJECTED
                return CallNextHookEx(IntPtr.Zero, code, message, data);
            var down = msg is 0x0100 or 0x0104;   // WM_KEYDOWN, WM_SYSKEYDOWN
            var up = msg is 0x0101 or 0x0105;     // WM_KEYUP, WM_SYSKEYUP

            if (_held.Contains(vk))
            {
                if (up)
                    _held.Remove(vk);
                return new IntPtr(1);   // the repeats and the release of a hotkey we took
            }

            if (down)
            {
                var mods = CurrentMods();
                foreach (var (id, key) in _keys)
                {
                    if (key.Vk != vk || key.Mods != mods)
                        continue;
                    _held.Add(vk);
                    PostMessage(_hwnd, WmFire, new IntPtr(id), IntPtr.Zero);
                    if ((mods & (Hotkey.Alt | Hotkey.Win)) != 0)
                        // Alt or Win released with nothing in between opens a menu or Start; a harmless unassigned
                        // key in between stops that, the same way Windows does it for its own hotkeys.
                        TapMaskKey();
                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private static int CurrentMods() =>
        (Pressed(0x11) ? Hotkey.Ctrl : 0) | (Pressed(0x12) ? Hotkey.Alt : 0) | (Pressed(0x10) ? Hotkey.Shift : 0)
        | (Pressed(0x5B) || Pressed(0x5C) ? Hotkey.Win : 0);

    private static bool Pressed(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void TapMaskKey()
    {
        var inputs = new Input[2];
        inputs[0] = new Input { Type = 1, Key = new KeybdInput { Vk = 0xE8 } };
        inputs[1] = new Input { Type = 1, Key = new KeybdInput { Vk = 0xE8, Flags = 0x0002 } };   // KEYEVENTF_KEYUP
        SendInput(2, inputs, Marshal.SizeOf<Input>());
    }

    private IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmRehook)
        {
            Hook();
            return IntPtr.Zero;
        }

        if (msg is WmHotkey or WmFire && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            try
            {
                action();
            }
            catch
            {
                /* one failed switch must not take down the message loop */
            }

            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _rehook.Dispose();
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        UnregisterAll();
        DestroyWindow(_hwnd);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

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
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSmall;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, int mods, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
