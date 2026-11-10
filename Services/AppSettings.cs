using System.Text.Json;
using pxdAudioSwitcher.Platform;

namespace pxdAudioSwitcher.Services;

public enum NotifyMode { Banner, BannerAndSound, Sound, Off }

/// <summary>A saved pair of devices, switched to by its own hotkey or when one of its programs comes to the front.</summary>
public sealed class Profile
{
    public string Name { get; set; } = "New profile";

    /// <summary>Device ids; null leaves that side as it is.</summary>
    public string? PlaybackId { get; set; }
    public string? RecordingId { get; set; }

    public Hotkey? Hotkey { get; set; }

    /// <summary>Exe names (e.g. "discord.exe") that switch to this profile when their window comes to the front.</summary>
    public List<string> Apps { get; set; } = new();

    /// <summary>Put the previous devices back once every one of those programs has closed.</summary>
    public bool RestoreOnClose { get; set; } = true;
}

/// <summary>Everything the user can change, saved as JSON in the per-user app-data folder.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Hotkey? PlaybackHotkey { get; set; } = new(Hotkey.Ctrl | Hotkey.Alt, 0x7A);   // F11
    public Hotkey? RecordingHotkey { get; set; } = new(Hotkey.Ctrl | Hotkey.Alt, 0x76);  // F7
    public Hotkey? MuteHotkey { get; set; } = new(Hotkey.Ctrl | Hotkey.Alt, 0x4D);       // M

    /// <summary>Device ids the hotkeys cycle through. Empty means every device that is plugged in.</summary>
    public List<string> PlaybackCycle { get; set; } = new();
    public List<string> RecordingCycle { get; set; } = new();

    /// <summary>Also make the new device the one calls (Discord, Teams…) use. Off keeps a fixed headset for calls.</summary>
    public bool SetCommunications { get; set; } = true;

    /// <summary>
    /// Take along apps that were given their own device in Windows' Volume mixer (they ignore the default otherwise):
    /// their own device is cleared so they follow the new default.
    /// </summary>
    public bool MoveAppsWithOwnDevice { get; set; } = true;

    public NotifyMode Notify { get; set; } = NotifyMode.Banner;

    /// <summary>Where the banner appears: 0–8 across a 3×3 grid of the screen, left to right, top to bottom (8 = bottom right).</summary>
    public int BannerSpot { get; set; } = 8;

    /// <summary>Banner on the screen the mouse is on (true) or always on the main screen.</summary>
    public bool BannerFollowsMouse { get; set; } = true;

    /// <summary>Start in the tray even when opened by hand, instead of showing this window.</summary>
    public bool StartInTray { get; set; }

    public List<Profile> Profiles { get; set; } = new();

    /// <summary>Last known name of every device seen, so unplugged devices in a profile still have a name.</summary>
    public Dictionary<string, string> KnownNames { get; set; } = new();

    /// <summary>%APPDATA%\pxdAudioSwitcher, or the folder in PXDAS_DATA_DIR (tests).</summary>
    public static string Folder =>
        Environment.GetEnvironmentVariable("PXDAS_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pxdAudioSwitcher");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            /* a damaged file just means defaults */
        }

        return new AppSettings();
    }

    /// <summary>Self-test: the window may be built and drawn, but nothing is ever written back.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ReadOnly { get; set; }

    public void Save()
    {
        if (ReadOnly)
            return;
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            /* best effort */
        }
    }

    public string NameOf(string? id) =>
        id is null ? "Leave as it is" : KnownNames.TryGetValue(id, out var n) ? n : "Unknown device";
}
