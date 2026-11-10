using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using pxdAudioSwitcher.Services;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace pxdAudioSwitcher.Views;

/// <summary>
/// The little pop-up in the bottom-right corner after a switch. One window is reused, so cycling quickly through
/// devices updates it in place instead of stacking pop-ups; it never takes focus away from the game or app in front.
/// </summary>
public static class Banner
{
    private static Window? _window;
    private static TextBlock _label = null!, _text = null!;
    private static Border _frame = null!, _badge = null!;
    private static ShapePath _waves = null!;
    private static DispatcherTimer? _timer;

    private static int _spot = 8;
    private static bool _followMouse = true;

    /// <summary>
    /// Announces a switch the way the settings say. <paramref name="force"/> shows the banner (silently) whatever the
    /// setting, for messages the user must see and for previewing the position.
    /// </summary>
    public static void Show(SwitchNotice notice, AppSettings settings, bool force = false)
    {
        var mode = force ? NotifyMode.Banner : settings.Notify;
        _spot = Math.Clamp(settings.BannerSpot, 0, 8);
        _followMouse = settings.BannerFollowsMouse;
        if (mode == NotifyMode.Off)
            return;
        if (mode is NotifyMode.Sound or NotifyMode.BannerAndSound)
            // Played through the device that is now the default, so it doubles as "this is where sound goes now".
            PlaySound(notice.Warning ? "SystemExclamation" : "SystemAsterisk", IntPtr.Zero, 0x10000 | 0x0001 | 0x0002);
        if (mode == NotifyMode.Sound)
            return;

        _window ??= Create();
        _timer!.Interval = TimeSpan.FromSeconds(force ? 5 : 2.5);
        _label.Text = notice.Label;
        _text.Text = notice.Text;
        _frame.BorderBrush = new SolidColorBrush(Color.Parse(notice.Warning ? "#7A2A33" : "#1D5A47"));
        _frame.BoxShadow = BoxShadows.Parse(notice.Warning ? "0 0 30 0 #33F2666A" : "0 0 30 0 #3319F58C");
        _badge.Background = GradientFor(notice.Warning);
        _waves.Data = notice.Warning ? AppIcon.Slash : AppIcon.Waves;

        if (!_window.IsVisible)
            _window.Show();
        Place();
        _timer!.Stop();
        _timer.Start();
    }

    private static Window Create()
    {
        _label = new TextBlock { Classes = { "label" } };
        _text = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        var dark = new SolidColorBrush(Color.Parse("#04140C"));
        _waves = new ShapePath { Stroke = dark, StrokeThickness = 10, StrokeLineCap = PenLineCap.Round };
        var art = new Grid { Width = 100, Height = 100 };
        art.Children.Add(new ShapePath { Data = AppIcon.Speaker, Fill = dark });
        art.Children.Add(_waves);
        _badge = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center, Child = new Viewbox { Margin = new Thickness(8), Child = art } };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_label);
        text.Children.Add(_text);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(_badge);
        row.Children.Add(text);

        _frame = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F2161922")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 18, 12),
            Child = row,
        };

        var window = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowDecorations = WindowDecorations.None,
            Topmost = true,
            ShowInTaskbar = false,
            CanResize = false,
            ShowActivated = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Content = new Border { Padding = new Thickness(12), Child = _frame },
        };
        window.PointerPressed += (_, _) => window.Hide();
        window.SizeChanged += (_, _) => Place();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _timer.Tick += (_, _) =>
        {
            if (window.IsPointerOver)
                return;
            _timer.Stop();
            // Closed rather than hidden: the tray app keeps no window alive between switches.
            window.Close();
            _window = null;
        };
        return window;
    }

    private static IBrush GradientFor(bool warning) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse(warning ? "#FF7A6B" : "#00C8FF"), 0),
            new GradientStop(Color.Parse(warning ? "#F2666A" : "#19F58C"), 1),
        },
    };

    /// <summary>Puts the banner in the chosen ninth of the working area (taskbar excluded) of the chosen screen.</summary>
    private static void Place()
    {
        if (_window is null)
            return;
        Avalonia.Platform.Screen? screen = null;
        if (_followMouse)
        {
            GetCursorPos(out var cursor);
            screen = _window.Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y));
        }

        screen ??= _window.Screens.Primary;
        if (screen is null)
            return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var w = (int)((_window.Bounds.Width > 0 ? _window.Bounds.Width : 340) * scale);
        var h = (int)((_window.Bounds.Height > 0 ? _window.Bounds.Height : 90) * scale);
        var gap = (int)(8 * scale);
        var x = (_spot % 3) switch
        {
            0 => area.X + gap,
            1 => area.X + (area.Width - w) / 2,
            _ => area.Right - w - gap,
        };
        var y = (_spot / 3) switch
        {
            0 => area.Y + gap,
            1 => area.Y + (area.Height - h) / 2,
            _ => area.Bottom - h - gap,
        };
        _window.Position = new PixelPoint(x, y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);
}
