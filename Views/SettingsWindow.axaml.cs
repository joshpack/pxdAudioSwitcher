using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using pxdAudioSwitcher.Audio;
using pxdAudioSwitcher.Platform;
using pxdAudioSwitcher.Services;
using pxdStandard;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace pxdAudioSwitcher.Views;

/// <summary>
/// The whole interface: one window, created when opened and thrown away when closed, so while the app sits in the tray
/// none of it exists.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Switcher _sw;
    private readonly Action _menuChanged;
    private bool _loading;
    private string _deviceSignature = "";

    private AppSettings S => _sw.Settings;

    public SettingsWindow()
    {
        InitializeComponent();
        _sw = null!;
        _menuChanged = null!;
    }

    /// <param name="tray">The tray icon's pxd Toolkit link, for the "Show tray icon" switch (null in the self-test).</param>
    /// <param name="quit">Quits the whole app (the tray icon may be hidden, so the window needs its own way out).</param>
    public SettingsWindow(Switcher sw, Action menuChanged, TrayLink? tray = null, Action? quit = null)
    {
        InitializeComponent();
        _sw = sw;
        _menuChanged = menuChanged;
        Icon = AppIcon.Create(128);

        // The pxd title bar: icon, name, the hotkey status, then minimize / maximize / close.
        var content = (Control)Content!;
        Content = null;
        Content = pxdChrome.Wrap(this, content, "pxd Audio Switcher", BrandIcon.Render(36), resizable: true, extras: BuildStatusPill());
        pxdChrome.RememberPlacement(this, PlacementKey);

        Ctl<ContentControl>("PlaybackHotkeySlot").Content = HotkeyEditor(S.PlaybackHotkey, v => S.PlaybackHotkey = v);
        Ctl<ContentControl>("RecordingHotkeySlot").Content = HotkeyEditor(S.RecordingHotkey, v => S.RecordingHotkey = v);
        Ctl<ContentControl>("MuteHotkeySlot").Content = HotkeyEditor(S.MuteHotkey, v => S.MuteHotkey = v);
        Ctl<Button>("MuteButton").Click += (_, _) => _sw.ToggleMute();
        Ctl<Button>("AddProfileButton").Click += (_, _) => AddProfile();
        Ctl<Button>("QuitButton").Click += (_, _) => quit?.Invoke();

        var trayIcon = Ctl<ToggleSwitch>("TrayIconSwitch");
        trayIcon.IsChecked = tray?.Visible ?? pxdLink.TrayIconVisible(TrayLink.AppId);
        trayIcon.IsCheckedChanged += (_, _) =>
        {
            if (_loading)
                return;
            if (tray is not null)
                tray.Set(trayIcon.IsChecked == true);
            else
                pxdLink.SetTrayIconVisible(TrayLink.AppId, trayIcon.IsChecked == true);
        };
        if (tray is not null)
        {
            // Turned on or off from pxd Toolkit while this window is open: keep the switch in step.
            void OnTrayChanged(bool visible)
            {
                _loading = true;
                trayIcon.IsChecked = visible;
                _loading = false;
            }

            tray.Changed += OnTrayChanged;
            Closed += (_, _) => tray.Changed -= OnTrayChanged;
        }

        LoadSettingsTab();
        Refresh();
    }

    /// <summary>Key of this window's size and position in windows.json.</summary>
    public const string PlacementKey = "settings";

    private Border _statusPill = null!;
    private Ellipse _statusDot = null!;
    private TextBlock _statusText = null!;

    /// <summary>"Hotkeys ready" / "1 hotkey blocked", shown in the title bar left of the caption buttons.</summary>
    private Border BuildStatusPill()
    {
        _statusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Classes = { "pulse" } };
        _statusText = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { _statusDot, _statusText } };
        _statusPill = new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Child = row,
        };
        return _statusPill;
    }

    private T Ctl<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void Save()
    {
        S.Save();
        _menuChanged();
    }

    /// <summary>Called whenever devices, defaults or mute change.</summary>
    public void Refresh()
    {
        _loading = true;
        var playback = _sw.Audio.List(EDataFlow.Render);
        var recording = _sw.Audio.List(EDataFlow.Capture);
        BuildDeviceList(Ctl<StackPanel>("PlaybackList"), playback, _sw.Audio.GetDefault(EDataFlow.Render)?.Id, S.PlaybackCycle, v => S.PlaybackCycle = v);
        BuildDeviceList(Ctl<StackPanel>("RecordingList"), recording, _sw.Audio.GetDefault(EDataFlow.Capture)?.Id, S.RecordingCycle, v => S.RecordingCycle = v);

        var muted = _sw.Audio.IsMicMuted();
        Ctl<TextBlock>("MuteState").Text = muted switch { true => "Muted right now", false => "Live right now", null => "No microphone" };
        Ctl<Button>("MuteButton").Content = muted == true ? "Unmute" : "Mute";
        Ctl<Button>("MuteButton").IsEnabled = muted is not null;

        // Profile cards hold half-typed text: rebuild them only when the set of devices they offer changes.
        var signature = string.Join("|", playback.Concat(recording).Select(d => d.Id + d.FullName));
        if (signature != _deviceSignature)
        {
            _deviceSignature = signature;
            BuildProfiles();
        }

        UpdateStatus();
        _loading = false;
    }

    private void UpdateStatus()
    {
        var conflicts = _sw.HotkeyConflicts;
        Ctl<Border>("ConflictCard").IsVisible = conflicts.Count > 0;
        Ctl<TextBlock>("ConflictText").Text = "Another program already uses " + string.Join(", ", conflicts) + ". Pick a different combination for "
                                              + (conflicts.Count == 1 ? "it." : "them.");

        var bad = conflicts.Count > 0;
        _statusText.Text = bad ? $"{conflicts.Count} hotkey{(conflicts.Count == 1 ? "" : "s")} blocked" : "Hotkeys ready";
        _statusPill.Background = new SolidColorBrush(Color.Parse(bad ? "#33260F" : "#0C2A1D"));
        _statusPill.BorderBrush = new SolidColorBrush(Color.Parse(bad ? "#5C4515" : "#146B44"));
        _statusDot.Fill = new SolidColorBrush(Color.Parse(bad ? "#F5B14C" : "#19F58C"));
    }

    // ---- Hotkeys ---------------------------------------------------------------------------------------------------

    private HotkeyButton HotkeyEditor(Hotkey? value, Action<Hotkey?> apply)
    {
        var button = new HotkeyButton(value);
        button.CaptureStarted += _sw.PauseHotkeys;
        button.CaptureEnded += () =>
        {
            _sw.RegisterHotkeys();
            UpdateStatus();
        };
        button.ValueChanged += v =>
        {
            apply(v);
            S.Save();
            _sw.RegisterHotkeys();
            UpdateStatus();
        };
        return button;
    }

    // ---- Devices ---------------------------------------------------------------------------------------------------

    private void BuildDeviceList(StackPanel list, List<AudioDevice> devices, string? currentId, List<string> cycle, Action<List<string>> setCycle)
    {
        list.Children.Clear();
        if (devices.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "Nothing plugged in.", Classes = { "muted" }, FontSize = 12 });
            return;
        }

        var boxes = new List<(CheckBox Box, string Id)>();
        foreach (var d in devices)
        {
            var device = d;
            var isDefault = d.Id == currentId;
            var box = new CheckBox { IsChecked = cycle.Count == 0 || cycle.Contains(d.Id), VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
            ToolTip.SetTip(box, "Include in the hotkey's cycle");
            boxes.Add((box, d.Id));

            var names = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = d.Name, FontWeight = isDefault ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
            if (d.Adapter.Length > 0)
                names.Children.Add(new TextBlock { Text = d.Adapter, Classes = { "muted" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });

            Control action;
            if (isDefault)
            {
                action = new Border
                {
                    CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3), VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.Parse("#0C2A1D")), BorderBrush = new SolidColorBrush(Color.Parse("#146B44")), BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = "IN USE", FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#19F58C")), LetterSpacing = 0.8 },
                };
            }
            else
            {
                var use = new Button { Content = "Use", MinHeight = 30, Padding = new Thickness(14, 2), VerticalAlignment = VerticalAlignment.Center };
                use.Click += (_, _) => _sw.SwitchTo(device);
                action = use;
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6, MinHeight = 40 };
            row.Children.Add(box);
            Grid.SetColumn(names, 1);
            row.Children.Add(names);
            Grid.SetColumn(action, 2);
            row.Children.Add(action);
            list.Children.Add(new Border { Classes = { "tile" }, Padding = new Thickness(8, 4), Child = row });
        }

        foreach (var (box, _) in boxes)
        {
            box.IsCheckedChanged += (_, _) =>
            {
                if (_loading)
                    return;
                var ticked = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToList();
                var shown = boxes.Select(b => b.Id).ToHashSet();
                // Keep choices for devices that are unplugged right now, so plugging them back in keeps them in the cycle.
                var away = (cycle.Count == 0 ? new List<string>() : cycle).Where(id => !shown.Contains(id));
                // Everything ticked means "all devices", which also covers devices plugged in later.
                setCycle(ticked.Count == boxes.Count && !away.Any() ? new List<string>() : ticked.Concat(away).ToList());
                S.Save();
            };
        }
    }

    // ---- Profiles --------------------------------------------------------------------------------------------------

    private void AddProfile()
    {
        var profile = new Profile
        {
            Name = $"Profile {S.Profiles.Count + 1}",
            PlaybackId = _sw.Audio.GetDefault(EDataFlow.Render)?.Id,
            RecordingId = _sw.Audio.GetDefault(EDataFlow.Capture)?.Id,
        };
        S.Profiles.Add(profile);
        Save();
        BuildProfiles();
    }

    private void BuildProfiles()
    {
        var list = Ctl<StackPanel>("ProfileList");
        list.Children.Clear();
        if (S.Profiles.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "No profiles yet. Make one for gaming (headset + headset mic), one for movies (TV), one for calls…",
                Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0),
            });
            return;
        }

        foreach (var p in S.Profiles)
            list.Children.Add(ProfileCard(p));
    }

    private Control ProfileCard(Profile p)
    {
        var name = new TextBox { Text = p.Name, FontWeight = FontWeight.SemiBold, FontSize = 14 };
        name.TextChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
                return;
            p.Name = name.Text.Trim();
            S.Save();
        };
        name.LostFocus += (_, _) =>
        {
            _menuChanged();
            _sw.RegisterHotkeys();
        };

        var playback = DeviceChoice(EDataFlow.Render, p.PlaybackId, id => p.PlaybackId = id);
        var recording = DeviceChoice(EDataFlow.Capture, p.RecordingId, id => p.RecordingId = id);
        var hotkey = HotkeyEditor(p.Hotkey, v => p.Hotkey = v);

        // Programs
        var apps = new WrapPanel { Orientation = Orientation.Horizontal };
        var restore = new CheckBox { Content = "Switch back when it closes", IsChecked = p.RestoreOnClose, IsVisible = p.Apps.Count > 0 };
        restore.IsCheckedChanged += (_, _) =>
        {
            p.RestoreOnClose = restore.IsChecked == true;
            S.Save();
        };
        void ShowApps()
        {
            apps.Children.Clear();
            foreach (var exe in p.Apps.ToList())
            {
                var remove = new Button { Content = exe + "  ✕", Classes = { "ghost" }, MinHeight = 28, Padding = new Thickness(10, 2), Margin = new Thickness(0, 0, 6, 6) };
                ToolTip.SetTip(remove, "Remove");
                remove.Click += (_, _) =>
                {
                    p.Apps.Remove(exe);
                    AppsChanged();
                };
                apps.Children.Add(remove);
            }

            restore.IsVisible = p.Apps.Count > 0;
        }

        void AppsChanged()
        {
            S.Save();
            _sw.UpdateForegroundWatch();
            ShowApps();
        }

        ShowApps();

        var pick = new ComboBox { PlaceholderText = "Add a running program…", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 34 };
        pick.DropDownOpened += (_, _) =>
        {
            pick.Items.Clear();
            foreach (var exe in RunningPrograms().Where(e => !p.Apps.Contains(e)))
                pick.Items.Add(new ComboBoxItem { Content = exe, Tag = exe });
        };
        pick.SelectionChanged += (_, _) =>
        {
            if (pick.SelectedItem is not ComboBoxItem { Tag: string exe })
                return;
            pick.SelectedItem = null;
            if (!p.Apps.Contains(exe))
            {
                p.Apps.Add(exe);
                AppsChanged();
            }
        };

        var typed = new TextBox { PlaceholderText = "or type one: game.exe", MinHeight = 34 };
        typed.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter || string.IsNullOrWhiteSpace(typed.Text))
                return;
            var exe = typed.Text.Trim().ToLowerInvariant();
            if (!exe.EndsWith(".exe", StringComparison.Ordinal))
                exe += ".exe";
            if (!p.Apps.Contains(exe))
            {
                p.Apps.Add(exe);
                AppsChanged();
            }

            typed.Text = "";
        };

        var apply = new Button { Content = "Switch now", Classes = { "primary" }, MinHeight = 34, Padding = new Thickness(14, 4) };
        apply.Click += (_, _) => _sw.ApplyProfile(p);
        var delete = new Button { Content = "Delete", Classes = { "ghost" }, MinHeight = 34 };
        delete.Click += (_, _) =>
        {
            S.Profiles.Remove(p);
            Save();
            _sw.RegisterHotkeys();
            _sw.UpdateForegroundWatch();
            BuildProfiles();
            UpdateStatus();
        };

        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 8,
        };
        AddRow(0, "PLAYBACK", playback);
        AddRow(1, "RECORDING", recording);
        AddRow(2, "HOTKEY", hotkey);
        var pickers = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        pickers.Children.Add(pick);
        Grid.SetColumn(typed, 1);
        pickers.Children.Add(typed);
        AddRow(3, "PROGRAMS", pickers);
        Place(apps, 4);
        Place(restore, 5);

        void AddRow(int row, string label, Control control)
        {
            var text = new TextBlock { Text = label, Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center, MinWidth = 78 };
            Grid.SetRow(text, row);
            form.Children.Add(text);
            Place(control, row);
            if (control is HotkeyButton)
                control.HorizontalAlignment = HorizontalAlignment.Left;
        }

        void Place(Control control, int row)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            form.Children.Add(control);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(delete);
        buttons.Children.Add(apply);

        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(name);
        stack.Children.Add(form);
        stack.Children.Add(new TextBlock
        {
            Text = "When a listed program's window comes to the front, this profile switches in by itself.",
            Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(buttons);
        return new Border { Classes = { "card" }, Padding = new Thickness(16), Child = stack };
    }

    /// <summary>A drop-down of the devices plugged in, plus "Leave as it is", plus the saved device if it is unplugged.</summary>
    private ComboBox DeviceChoice(EDataFlow flow, string? selected, Action<string?> apply)
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 34 };
        box.Items.Add(new ComboBoxItem { Content = "Leave as it is", Tag = null });
        var devices = _sw.Audio.List(flow);
        foreach (var d in devices)
            box.Items.Add(new ComboBoxItem { Content = d.FullName, Tag = d.Id });
        if (selected is not null && devices.All(d => d.Id != selected))
            box.Items.Add(new ComboBoxItem { Content = S.NameOf(selected) + "  (not plugged in)", Tag = selected });

        box.SelectedIndex = Math.Max(0, box.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string?)i.Tag == selected));
        box.SelectionChanged += (_, _) =>
        {
            if (_loading || box.SelectedItem is not ComboBoxItem item)
                return;
            apply(item.Tag as string);
            S.Save();
        };
        return box;
    }

    /// <summary>Exe names of programs that have a window open, for the "add a program" list.</summary>
    private static List<string> RunningPrograms()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero && ForegroundWatcher.ExeName((uint)proc.Id) is { } exe && exe != "pxdaudioswitcher.exe")
                        names.Add(exe);
                }
                catch
                {
                    /* gone, or not ours to look at */
                }
            }
        }

        return names.ToList();
    }

    // ---- Settings tab ----------------------------------------------------------------------------------------------

    private void LoadSettingsTab()
    {
        _loading = true;
        var notify = Ctl<ComboBox>("NotifyBox");
        notify.SelectedIndex = (int)S.Notify;
        notify.SelectionChanged += (_, _) =>
        {
            if (_loading || notify.SelectedIndex < 0)
                return;
            S.Notify = (NotifyMode)notify.SelectedIndex;
            S.Save();
        };

        var comms = Ctl<ToggleSwitch>("CommsSwitch");
        comms.IsChecked = S.SetCommunications;
        comms.IsCheckedChanged += (_, _) =>
        {
            S.SetCommunications = comms.IsChecked == true;
            S.Save();
        };

        var auto = Ctl<ToggleSwitch>("AutostartSwitch");
        auto.IsChecked = Autostart.IsEnabled();
        auto.IsCheckedChanged += (_, _) =>
        {
            if (_loading)
                return;
            try
            {
                Autostart.Set(auto.IsChecked == true);
            }
            catch (Exception ex)
            {
                Ctl<TextBlock>("AutostartNote").Text = "Couldn't change that: " + ex.Message;
            }
        };

        var moveApps = Ctl<ToggleSwitch>("MoveAppsSwitch");
        moveApps.IsChecked = S.MoveAppsWithOwnDevice;
        moveApps.IsCheckedChanged += (_, _) =>
        {
            S.MoveAppsWithOwnDevice = moveApps.IsChecked == true;
            S.Save();
        };
        Ctl<Button>("ReleaseAllButton").Click += (_, _) =>
            Ctl<TextBlock>("ReleaseAllNote").Text = AppRouting.ReleaseAllApps()
                ? "Done: every app now follows the default devices"
                : "This version of Windows doesn't allow that; use Reset in Settings → System → Sound → Volume mixer";

        var tray = Ctl<ToggleSwitch>("TrayStartSwitch");
        tray.IsChecked = S.StartInTray;
        tray.IsCheckedChanged += (_, _) =>
        {
            S.StartInTray = tray.IsChecked == true;
            S.Save();
        };

        var screen = Ctl<ComboBox>("ScreenBox");
        screen.SelectedIndex = S.BannerFollowsMouse ? 0 : 1;
        screen.SelectionChanged += (_, _) =>
        {
            if (_loading || screen.SelectedIndex < 0)
                return;
            S.BannerFollowsMouse = screen.SelectedIndex == 0;
            S.Save();
            PreviewBanner();
        };
        BuildSpots();

        var exe = Environment.ProcessPath ?? "pxdAudioSwitcher.exe";
        Ctl<TextBox>("CliText").Text =
            $"\"{exe}\" --next-playback\n" +
            "  --next-recording   --toggle-mute   --mute   --unmute\n" +
            "  --profile \"Gaming\"\n" +
            "  --playback \"Headphones\"   --recording \"Yeti\"   (part of a name)";
        _loading = false;
    }

    private static readonly string[] SpotNames =
        ["Top left", "Top middle", "Top right", "Middle left", "Center", "Middle right", "Bottom left", "Bottom middle", "Bottom right"];

    /// <summary>The nine spots on the little monitor; the chosen one is lit like a tiny banner.</summary>
    private void BuildSpots()
    {
        var grid = Ctl<Avalonia.Controls.Primitives.UniformGrid>("SpotGrid");
        grid.Children.Clear();
        for (var i = 0; i < 9; i++)
        {
            var spot = i;
            var on = spot == S.BannerSpot;
            var button = new Button
            {
                Width = 40, Height = 20, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
                HorizontalAlignment = (spot % 3) switch { 0 => HorizontalAlignment.Left, 1 => HorizontalAlignment.Center, _ => HorizontalAlignment.Right },
                VerticalAlignment = (spot / 3) switch { 0 => VerticalAlignment.Top, 1 => VerticalAlignment.Center, _ => VerticalAlignment.Bottom },
                Background = on ? new SolidColorBrush(Color.Parse("#19F58C")) : new SolidColorBrush(Color.Parse("#1D212C")),
                BorderBrush = on ? Brushes.Transparent : new SolidColorBrush(Color.Parse("#2B3242")),
            };
            ToolTip.SetTip(button, SpotNames[spot]);
            button.Click += (_, _) =>
            {
                S.BannerSpot = spot;
                S.Save();
                BuildSpots();
                PreviewBanner();
            };
            grid.Children.Add(button);
        }
    }

    private void PreviewBanner() =>
        Banner.Show(new SwitchNotice("PREVIEW · " + SpotNames[Math.Clamp(S.BannerSpot, 0, 8)].ToUpperInvariant(), "Banners will show up here"), S, force: true);
}
