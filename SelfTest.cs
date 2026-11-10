using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using pxdStandard;
using pxdAudioSwitcher.Audio;
using pxdAudioSwitcher.Platform;
using pxdAudioSwitcher.Services;
using pxdAudioSwitcher.Views;

namespace pxdAudioSwitcher;

/// <summary>
/// --self-test DIR: checks the parts that need real Windows audio without changing anything the user would notice
/// (the current defaults are re-applied, not switched) and renders the settings window to a PNG without showing it.
/// It also checks the pxd App Standard parts (window placement, the pxd Toolkit tray link) and draws the README
/// pictures from made-up devices. Nothing is saved to the real settings, windows.json or toolkit.json.
/// </summary>
internal static class SelfTest
{
    public static string? Dir { get; set; }

    public static void Audio(StringBuilder r)
    {
        using var sw = new Switcher(interactive: false);
        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
        {
            var current = sw.Audio.GetDefault(flow);
            r.AppendLine($"{flow} default: {current?.FullName ?? "none"}");
            foreach (var d in sw.Audio.List(flow))
                r.AppendLine($"  - {d.FullName}  [{d.Id}]");
            r.AppendLine($"  cycle list: {sw.CycleList(flow).Count} device(s)");
            if (current is not null)
            {
                var comms = sw.Audio.GetDefault(flow, ERole.Communications)?.Id == current.Id;
                r.AppendLine($"  re-apply current default (IPolicyConfig): {(sw.Audio.SetDefault(current.Id, comms) ? "OK" : "FAILED")}");
                r.AppendLine($"  default unchanged afterwards: {sw.Audio.GetDefault(flow)?.Id == current.Id}");
            }
        }

        r.AppendLine($"Mic muted: {sw.Audio.IsMicMuted()?.ToString() ?? "no mic"}");

        // Apps given their own device in the Volume mixer (read only: nothing is changed here).
        var pinned = new SortedSet<string>();
        var scan = System.Diagnostics.Stopwatch.StartNew();
        foreach (var proc in System.Diagnostics.Process.GetProcesses())
        {
            using (proc)
            {
                if (AppRouting.OwnDevice((uint)proc.Id, EDataFlow.Render) is { } own)
                    pinned.Add($"{proc.ProcessName} -> {own}");
            }
        }

        r.AppendLine($"Running apps with their own output device: {pinned.Count} (scan took {scan.ElapsedMilliseconds} ms)");
        foreach (var p in pinned)
            r.AppendLine("  - " + p);

        using var hk = new HotkeyManager();
        var probe = new Hotkey(Hotkey.Ctrl | Hotkey.Alt | Hotkey.Shift | Hotkey.Win, 0x87);   // F24: nobody uses it
        r.AppendLine($"Hotkey register (Ctrl+Alt+Shift+Win+F24): {(hk.Register(probe, () => { }) ? "OK" : "FAILED")}");
        hk.UnregisterAll();
        foreach (var (name, key) in new[] { ("playback", sw.Settings.PlaybackHotkey), ("recording", sw.Settings.RecordingHotkey), ("mute", sw.Settings.MuteHotkey) })
            if (key is not null)
                r.AppendLine($"Hotkey {name} {key}: {(hk.Register(key, () => { }) ? "free" : "TAKEN by another program")}");
        hk.UnregisterAll();
    }

    public static void Window(StringBuilder r, Switcher sw)
    {
        // A throw-away profile so the profile card gets built too; never saved.
        sw.Settings.ReadOnly = true;
        sw.Settings.Profiles.Add(new Profile { Name = "Self-test", PlaybackId = sw.Audio.GetDefault(EDataFlow.Render)?.Id, Apps = { "game.exe" } });
        var window = new SettingsWindow(sw, () => { })
        {
            // Far off every screen, never activated, not on the taskbar: nothing appears for the user.
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.Manual,
            Position = new PixelPoint(-30000, -30000),
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        foreach (var tab in new[] { 0, 1, 2 })
        {
            window.FindControl<TabControl>("Tabs")!.SelectedIndex = tab;
            var size = new Size(560, 740);
            window.Width = size.Width;
            window.Height = size.Height;
            window.UpdateLayout();
            using var bmp = new RenderTargetBitmap(new PixelSize(560, 740));
            bmp.Render(window);
            bmp.Save(Path.Combine(Dir!, $"tab{tab}.png"));
        }

        var bar = window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("pxd-caption"));
        r.AppendLine($"Settings window: built and rendered with the pxd title bar ({bar} caption buttons{(window.CanResize ? ", resizable" : "")}; platform window: {window.PlatformImpl?.GetType().Name})");
        window.Close();
        sw.Settings.Profiles.RemoveAt(sw.Settings.Profiles.Count - 1);
    }

    // ---- pxd App Standard ------------------------------------------------------------------------------------------

    private static string PlacementDir => Path.Combine(Dir!, "placement");

    /// <summary>
    /// Keeps the self-test away from the user's own files: window placement goes to DIR\placement and, unless
    /// PXD_SHARED_DIR is set already, the pxd Toolkit file to DIR\shared.
    /// </summary>
    public static void Prepare(StringBuilder r)
    {
        if (Directory.Exists(PlacementDir))
            Directory.Delete(PlacementDir, recursive: true);
        pxdChrome.Configure(TrayLink.AppId, PlacementDir);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PXD_SHARED_DIR")))
            Environment.SetEnvironmentVariable("PXD_SHARED_DIR", Path.Combine(Dir!, "shared"));
        r.AppendLine("Test folders: placement and toolkit.json kept inside the self-test folder");
    }

    /// <summary>
    /// Updating before start (pxdUpdate): skipped for --self-test and bin\ builds; a made-up newer release on a local
    /// server updates a copy in DIR\update (never the real install).
    /// </summary>
    public static void Update(StringBuilder r)
    {
        Check(r, "update: skipped for --self-test", !pxdUpdate.BeforeStart(Program.UpdateRepo, Program.UpdateAsset, ["--self-test", Dir!]));
        if (Path.GetDirectoryName(Environment.ProcessPath ?? "")?.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) == true)
            Check(r, "update: skipped for a bin\\ build", !pxdUpdate.BeforeStart(Program.UpdateRepo, Program.UpdateAsset, []));

        const string exe = TrayLink.AppId + ".exe";
        var root = Path.Combine(Dir!, "update");
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        var pkg = Path.Combine(root, "pkg", TrayLink.AppId);
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(pkg);
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(pkg, exe), "new exe");
        File.WriteAllText(Path.Combine(pkg, "app.dll"), "new dll");
        File.WriteAllText(Path.Combine(app, exe), "old exe");
        File.WriteAllText(Path.Combine(app, "app.dll"), "old dll");
        var zipName = TrayLink.AppId + "-99.0.0-win-x64.zip";
        var zip = Path.Combine(root, zipName);
        System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(root, "pkg"), zip);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();

        var prefix = $"http://127.0.0.1:{Random.Shared.Next(41000, 49000)}/";
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                byte[] body = ctx.Request.Url!.AbsolutePath switch
                {
                    "/repos/" + Program.UpdateRepo + "/releases/latest" => Encoding.UTF8.GetBytes(
                        $$"""{"tag_name":"v99.0.0","assets":[{"name":"{{zipName}}","browser_download_url":"{{prefix}}zip"},{"name":"{{zipName}}.sha256","browser_download_url":"{{prefix}}sum"}]}"""),
                    "/zip" => File.ReadAllBytes(zip),
                    "/sum" => Encoding.ASCII.GetBytes(hash + "  " + zipName),
                    _ => [],
                };
                ctx.Response.StatusCode = body.Length == 0 ? 404 : 200;
                try { ctx.Response.OutputStream.Write(body); ctx.Response.Close(); } catch { /* client gone */ }
            }
        });

        var before = Environment.GetEnvironmentVariable("PXD_UPDATE_API");
        Environment.SetEnvironmentVariable("PXD_UPDATE_API", prefix.TrimEnd('/'));
        try
        {
            // Off the UI thread: the self-test runs on it, and awaiting there would deadlock.
            var plan = Task.Run(() => pxdUpdate.FindUpdateAsync(Program.UpdateRepo, Program.UpdateAsset, pxdUpdate.ThisVersion())).GetAwaiter().GetResult();
            Check(r, "update: newer release found (zip + checksum)", plan is { Sum: not null });
            Check(r, "update: nothing when this copy is current",
                Task.Run(() => pxdUpdate.FindUpdateAsync(Program.UpdateRepo, Program.UpdateAsset, new Version(99, 0, 0))).GetAwaiter().GetResult() is null);
            var ok = plan is { } p && Task.Run(() => pxdUpdate.ApplyAsync(p.Zip, p.Sum, app, Path.Combine(app, exe), [], start: false)).GetAwaiter().GetResult();
            pxdUpdate.CleanUp(app);
            Check(r, "update: files replaced in place", ok && File.ReadAllText(Path.Combine(app, exe)) == "new exe"
                && File.ReadAllText(Path.Combine(app, "app.dll")) == "new dll" && !Directory.EnumerateFiles(app, "*.pxdold").Any());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PXD_UPDATE_API", before);
            listener.Stop();
        }
    }

    /// <summary>
    /// The settings folder of the app's former name moves to the new name once (a fake one in DIR\migration; the real
    /// folders and the Run key are never touched here).
    /// </summary>
    public static void Migration(StringBuilder r)
    {
        var root = Path.Combine(Dir!, "migration");
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        var former = Path.Combine(root, pxdMigrate.FormerName(TrayLink.AppId));
        var target = Path.Combine(root, TrayLink.AppId);
        Directory.CreateDirectory(former);
        File.WriteAllText(Path.Combine(former, "settings.json"), """{ "StartInTray": true }""");
        File.WriteAllText(Path.Combine(former, "windows.json"), "{}");

        var moved = Services.Migration.DataFolder(root);
        Check(r, "migration: former settings folder moved to the new name", moved && !Directory.Exists(former)
            && File.ReadAllText(Path.Combine(target, "settings.json")).Contains("StartInTray") && File.Exists(Path.Combine(target, "windows.json")));
        File.WriteAllText(Path.Combine(target, "settings.json"), "{}");
        var again = Services.Migration.DataFolder(root);
        Check(r, "migration: second run is a no-op", !again && File.ReadAllText(Path.Combine(target, "settings.json")) == "{}");
        Directory.CreateDirectory(former);
        File.WriteAllText(Path.Combine(former, "settings.json"), "old");
        Check(r, "migration: an existing new folder is never overwritten", !Services.Migration.DataFolder(root)
            && File.ReadAllText(Path.Combine(target, "settings.json")) == "{}");
        Check(r, "migration: Run entry keeps its arguments", pxdMigrate.ArgsOf("\"C:\\Apps\\x\\a.exe\" --minimized") == "--minimized");
        Check(r, "migration: nothing to move on a fresh install", !Services.Migration.DataFolder(Path.Combine(root, "empty")));
    }

    /// <summary>Saves a size, restores it, restores a spot on screen, and ignores a spot that is on no screen.</summary>
    public static void Placement(StringBuilder r)
    {
        var file = Path.Combine(PlacementDir, "windows.json");
        using var sw = new Switcher(new AppSettings { ReadOnly = true }, Sample());

        // 1. Saving: resize a window that is open far off screen.
        var w = OffScreen(new SettingsWindow(sw, () => { }));
        w.Show();
        w.Width = 600;
        w.Height = 650;
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var saved = ReadPlacement(file);
        w.Close();
        Check(r, "placement saved (600x650)", saved is { } s && Math.Abs(s.Width - 600) < 1 && Math.Abs(s.Height - 650) < 1, saved?.ToString());

        // 2. Restoring size and a position on the main screen (the window is built but never shown).
        var probe = new SettingsWindow(sw, () => { });
        var area = probe.Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1040);
        var x = area.X + 120;
        var y = area.Y + 80;
        WritePlacement(file, x, y, 610, 660);
        var restored = new SettingsWindow(sw, () => { });
        Check(r, "placement restored (size and position)",
            Math.Abs(restored.Width - 610) < 1 && Math.Abs(restored.Height - 660) < 1 && restored.WindowStartupLocation == WindowStartupLocation.Manual
            && restored.Position == new PixelPoint(x, y),
            $"{restored.Width}x{restored.Height} at {restored.Position}, {restored.WindowStartupLocation}");

        // 3. A spot that is on no screen any more: the size is kept, the position is not.
        WritePlacement(file, -30000, -30000, 620, 670);
        var lost = new SettingsWindow(sw, () => { });
        Check(r, "off-screen placement ignored", lost.WindowStartupLocation == WindowStartupLocation.CenterScreen && Math.Abs(lost.Width - 620) < 1,
            $"{lost.WindowStartupLocation}, {lost.Width}x{lost.Height}");
        File.Delete(file);
    }

    /// <summary>Writing toolkit.json (as pxd Toolkit does) shows and hides the tray icon; the app's own switch writes it.</summary>
    public static void TrayIconLink(StringBuilder r)
    {
        var shared = pxdLink.SharedDir;
        Directory.CreateDirectory(shared);
        if (File.Exists(pxdLink.FilePath))
            File.Delete(pxdLink.FilePath);

        // The same class the app uses, with a stand-in for the tray icon (a real one would appear on screen).
        var shown = true;
        using var changed = new AutoResetEvent(false);
        using var link = new TrayLink(v => { shown = v; changed.Set(); }, a => a());
        Check(r, "tray icon shown by default (no toolkit.json)", link.Visible && shown);

        bool ToolkitWrites(bool visible)
        {
            changed.Reset();
            File.WriteAllText(pxdLink.FilePath, $$"""{ "schema": 1, "apps": { "pxdWatermark": { "trayIcon": true }, "pxdAudioSwitcher": { "trayIcon": {{(visible ? "true" : "false")}} } } }""");
            changed.WaitOne(TimeSpan.FromSeconds(5));
            return shown == visible && link.Visible == visible;
        }

        Check(r, "toolkit.json trayIcon=false hides the icon", ToolkitWrites(false));
        Check(r, "toolkit.json trayIcon=true shows it again", ToolkitWrites(true));

        link.Set(false);
        var other = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(pxdLink.FilePath))?["apps"]?["pxdWatermark"]?["trayIcon"]?.GetValue<bool>();
        Check(r, "Show tray icon switch writes toolkit.json (other apps kept)", !shown && !pxdLink.TrayIconVisible(TrayLink.AppId) && other == true);
        link.Set(true);
        Check(r, "Show tray icon switch on again", shown && pxdLink.TrayIconVisible(TrayLink.AppId));
    }

    /// <summary>The README pictures: made-up devices and profiles, nothing from this PC, drawn off screen.</summary>
    public static void SamplePictures(StringBuilder r)
    {
        var settings = new AppSettings
        {
            ReadOnly = true,
            PlaybackCycle = { "spk", "headset" },
            Profiles =
            {
                new Profile { Name = "Gaming", PlaybackId = "headset", RecordingId = "headset-mic", Hotkey = new Hotkey(Hotkey.Ctrl | Hotkey.Alt, 0x47), Apps = { "game.exe", "launcher.exe" } },
                new Profile { Name = "Movie night", PlaybackId = "tv", Hotkey = new Hotkey(Hotkey.Ctrl | Hotkey.Alt, 0x4E) },
            },
        };
        using var sw = new Switcher(settings, Sample());
        var w = OffScreen(new SettingsWindow(sw, () => { }));
        w.Show();
        if (w.FindControl<TextBox>("CliText") is { } cli && Environment.ProcessPath is { } exe)
            cli.Text = cli.Text?.Replace(exe, @"C:\Apps\pxdAudioSwitcher\pxdAudioSwitcher.exe");

        var folder = Path.Combine(Dir!, "readme");
        Directory.CreateDirectory(folder);
        foreach (var (tab, name, height) in new[] { (0, "devices", 740), (1, "profiles", 740), (2, "settings", 880) })
        {
            w.FindControl<TabControl>("Tabs")!.SelectedIndex = tab;
            w.Width = 560;
            w.Height = height;
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            SaveFramed(r, w, 560, height, Path.Combine(folder, name + ".png"));
        }

        w.Close();
        r.AppendLine("README pictures (sample data): readme\\devices.png, profiles.png, settings.png");
    }

    /// <summary>
    /// The window with rounded corners and a soft shadow, the way it looks on the desktop. Drawn at 1x: rendering it at
    /// 192 dpi misplaces nested controls, and a 2x window would be taller than the screen allows.
    /// </summary>
    private static void SaveFramed(StringBuilder r, Window w, int width, int height, string path)
    {
        const int pad = 18;
        var drawn = w.ClientSize;
        Check(r, $"{Path.GetFileName(path)} laid out at {width}x{height}", Math.Abs(drawn.Width - width) < 1 && Math.Abs(drawn.Height - height) < 1, drawn.ToString());
        using var shot = new RenderTargetBitmap(new PixelSize(width, height));
        shot.Render(w);
        var framed = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = w.Background,
            BoxShadow = BoxShadows.Parse("0 6 18 0 #66000000"),
            Child = new Border
            {
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Child = new Panel
                {
                    Children =
                    {
                        new Image { Source = shot, Stretch = Stretch.None },
                        new Border { CornerRadius = new CornerRadius(10), BorderBrush = new SolidColorBrush(Color.Parse("#2A3040")), BorderThickness = new Thickness(1) },
                    },
                },
            },
        };
        var frame = new Border { Padding = new Thickness(pad), Child = framed };
        var size = new Size(width + 2 * pad, height + 2 * pad);
        frame.Measure(size);
        frame.Arrange(new Rect(size));
        using var out_ = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
        out_.Render(frame);
        out_.Save(path);
    }

    private static SampleDevices Sample() => new(
    [
        new AudioDevice("spk", "Speakers", "USB Audio", EDataFlow.Render),
        new AudioDevice("headset", "Headset", "Wireless Headset", EDataFlow.Render),
        new AudioDevice("tv", "TV", "HDMI Audio", EDataFlow.Render),
        new AudioDevice("studio", "Studio Mic", "USB Audio", EDataFlow.Capture),
        new AudioDevice("headset-mic", "Headset Microphone", "Wireless Headset", EDataFlow.Capture),
        new AudioDevice("webcam", "Microphone", "HD Webcam", EDataFlow.Capture),
    ], "spk", "studio");

    /// <summary>Far off every screen, never activated, not on the taskbar: nothing appears for the user.</summary>
    private static Window OffScreen(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Position = new PixelPoint(-30000, -30000);
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        return w;
    }

    private sealed record SavedPlacement(int X, int Y, double Width, double Height, bool Maximized);

    private static SavedPlacement? ReadPlacement(string file)
    {
        try
        {
            var all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, SavedPlacement>>(File.ReadAllText(file));
            return all?.GetValueOrDefault(SettingsWindow.PlacementKey);
        }
        catch
        {
            return null;
        }
    }

    private static void WritePlacement(string file, int x, int y, double width, double height) =>
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, SavedPlacement>
        {
            [SettingsWindow.PlacementKey] = new(x, y, width, height, false),
        }));

    private static void Check(StringBuilder r, string what, bool ok, string? detail = null) =>
        r.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}{(ok || detail is null ? "" : "  (" + detail + ")")}");
}
