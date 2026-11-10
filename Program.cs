using System.IO.Pipes;
using Avalonia;
using Avalonia.Headless;
using pxdAudioSwitcher.Audio;
using pxdAudioSwitcher.Services;

namespace pxdAudioSwitcher;

internal static class Program
{
    public const string PipeName = "pxdAudioSwitcher.commands";
    private static Mutex? _instance;

    /// <summary>Started by Windows at sign-in: stay in the tray instead of opening the window.</summary>
    public static bool StartMinimized { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // pxd App Standard: window placement lives in windows.json next to settings.json.
        pxdStandard.pxdChrome.Configure(TrayLink.AppId, AppSettings.Folder);
        StartMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        var test = Array.FindIndex(args, a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase));
        if (test >= 0 && test + 1 < args.Length)
        {
            SelfTest.Dir = args[test + 1];
            // Headless platform: no OS window is ever created, so nothing can flash up on screen.
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args);
            return 0;
        }

        _instance = new Mutex(true, @"Local\pxdAudioSwitcher.instance", out var first);
        if (!first)
        {
            // Already running: hand it the command (or just "open your window").
            return Forward(args.Length > 0 ? args : ["--show"]) ? 0 : 1;
        }

        // Settings folder and sign-in entry from the app's former name, before anything reads settings.
        Migration.Run();

        if (Commands.HasAction(args))
        {
            // Not running, but asked to do something (a shortcut or script): do it and leave.
            using var switcher = new Switcher(interactive: false);
            return Commands.Run(switcher, args) ? 0 : 1;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>
    /// Software rendering: windows this small don't need the graphics card, and skipping GPU setup keeps memory use and
    /// start-up time low.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
            .LogToTrace();

    private static bool Forward(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe);
            writer.Write(string.Join("\n", args));
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Command-line control, for shortcuts, Stream Deck buttons and scripts:
/// --next-playback, --next-recording, --toggle-mute, --mute, --unmute, --profile "Name",
/// --playback "part of a name", --recording "part of a name", --show.
/// </summary>
public static class Commands
{
    private static readonly string[] Actions =
        ["--next-playback", "--next-recording", "--toggle-mute", "--mute", "--unmute", "--profile", "--playback", "--recording"];

    public static bool HasAction(string[] args) => args.Any(a => Actions.Contains(a, StringComparer.OrdinalIgnoreCase));

    /// <summary>Carries out every action in <paramref name="args"/>; false if any of them couldn't be done.</summary>
    public static bool Run(Switcher sw, string[] args)
    {
        var ok = true;
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : "";
            switch (args[i].ToLowerInvariant())
            {
                case "--next-playback": ok &= sw.CycleNext(EDataFlow.Render) is not null; break;
                case "--next-recording": ok &= sw.CycleNext(EDataFlow.Capture) is not null; break;
                case "--toggle-mute": ok &= sw.ToggleMute() is not null; break;
                case "--mute": ok &= sw.Audio.SetMicMute(true) is not null; break;
                case "--unmute": ok &= sw.Audio.SetMicMute(false) is not null; break;
                case "--profile":
                    i++;
                    ok &= sw.FindProfile(value) is { } p && sw.ApplyProfile(p);
                    break;
                case "--playback" or "--recording":
                    i++;
                    var flow = args[i - 1].Equals("--playback", StringComparison.OrdinalIgnoreCase) ? EDataFlow.Render : EDataFlow.Capture;
                    var device = sw.Audio.List(flow).FirstOrDefault(d => d.FullName.Contains(value, StringComparison.OrdinalIgnoreCase));
                    ok &= device is not null && sw.SwitchTo(device);
                    break;
            }
        }

        return ok;
    }
}
