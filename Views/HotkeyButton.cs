using Avalonia.Controls;
using Avalonia.Input;
using pxdAudioSwitcher.Platform;

namespace pxdAudioSwitcher.Views;

/// <summary>
/// Shows a hotkey; click it, then press the new combination. Esc cancels, Backspace removes the hotkey. While it is
/// listening the app's own hotkeys are paused, so pressing the current combination doesn't switch anything.
/// </summary>
public sealed class HotkeyButton : Button
{
    private bool _capturing;

    protected override Type StyleKeyOverride => typeof(Button);

    public Hotkey? Value { get; private set; }

    public event Action<Hotkey?>? ValueChanged;
    public event Action? CaptureStarted, CaptureEnded;

    public HotkeyButton(Hotkey? value)
    {
        Value = value;
        MinWidth = 180;
        MinHeight = 34;
        Padding = new Avalonia.Thickness(12, 4);
        ShowValue();
        LostFocus += (_, _) =>
        {
            if (_capturing)
                End();
        };
    }

    protected override void OnClick()
    {
        if (_capturing)
            return;
        _capturing = true;
        Content = "Press keys…  (Esc cancels)";
        Classes.Add("capturing");
        CaptureStarted?.Invoke();
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            End();
            return;
        }

        if (e.Key == Key.Back)
        {
            Set(null);
            return;
        }

        var vk = Hotkey.VkFromKey(e.Key);
        if (vk == 0)
            return;   // a modifier on its own, or a key that can't be a hotkey: keep listening

        var mods = Hotkey.ModsFrom(e.KeyModifiers);
        if (mods == 0 && !Hotkey.StandsAlone(vk))
        {
            Content = "Add Ctrl, Alt, Shift or Win";
            return;
        }

        Set(new Hotkey(mods, vk));
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_capturing)
            e.Handled = true;
        else
            base.OnKeyUp(e);
    }

    private void Set(Hotkey? value)
    {
        Value = value;
        End();
        ValueChanged?.Invoke(value);
    }

    private void End()
    {
        _capturing = false;
        Classes.Remove("capturing");
        ShowValue();
        CaptureEnded?.Invoke();
    }

    private void ShowValue() => Content = Value?.ToString() ?? "No hotkey · click to set";
}
