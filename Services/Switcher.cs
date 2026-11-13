using pxdAudioSwitcher.Audio;
using pxdAudioSwitcher.Platform;

namespace pxdAudioSwitcher.Services;

/// <summary>What just happened, for the banner: "PLAYBACK" / "Speakers (Realtek…)".</summary>
public sealed record SwitchNotice(string Label, string Text, bool Warning = false);

/// <summary>
/// The app's brain: cycles devices, applies profiles, mutes the microphone, owns the hotkeys and watches for profile
/// programs. Lives on the UI thread. The command-line mode uses the same class without hotkeys or watching.
/// </summary>
public sealed class Switcher : IDisposable
{
    public AppSettings Settings { get; }
    public AudioDevices Audio { get; }

    /// <summary>Something switched (or failed to); the app shows it as a banner and/or sound.</summary>
    public event Action<SwitchNotice>? Notice;

    /// <summary>Devices or defaults changed; menus and windows should refresh. Always raised on the UI thread.</summary>
    public event Action? DevicesChanged;

    /// <summary>Hotkeys that couldn't be registered because another program owns them, e.g. "Cycle playback".</summary>
    public List<string> HotkeyConflicts { get; } = new();

    private HotkeyManager? _hotkeys;
    private ForegroundWatcher? _foreground;
    private string? _lastForeground;
    private Action<Action>? _post;

    // A profile switched in by a program, waiting to be undone when that program closes.
    private (Profile Profile, string? PrevPlayback, string? PrevRecording)? _pendingRestore;
    private Timer? _restoreTimer;

    public Switcher(bool interactive)
    {
        Settings = AppSettings.Load();
        Audio = new AudioDevices(watch: interactive);
    }

    /// <summary>Self-test pictures: the given settings and made-up devices; no hotkeys, nothing watched or saved by itself.</summary>
    public Switcher(AppSettings settings, SampleDevices sample)
    {
        Settings = settings;
        Audio = new AudioDevices(sample);
    }

    /// <summary>Starts hotkeys and watching. <paramref name="post"/> runs an action on the UI thread.</summary>
    public void Start(Action<Action> post)
    {
        _post = post;
        _hotkeys = new HotkeyManager();
        Audio.Changed += OnAudioChanged;
        RegisterHotkeys();
        UpdateForegroundWatch();
        RememberNames();
    }

    // ---- Devices ---------------------------------------------------------------------------------------------------

    private int _changePending;

    private void OnAudioChanged()
    {
        // Windows sends a burst of notifications per switch (one per role); handle them as one.
        if (Interlocked.Exchange(ref _changePending, 1) == 1)
            return;
        Task.Delay(150).ContinueWith(_ => _post?.Invoke(() =>
        {
            Interlocked.Exchange(ref _changePending, 0);
            RememberNames();
            DevicesChanged?.Invoke();
        }));
    }

    private void RememberNames()
    {
        var changed = false;
        foreach (var d in Audio.List(EDataFlow.Render).Concat(Audio.List(EDataFlow.Capture)))
        {
            if (Settings.KnownNames.TryGetValue(d.Id, out var n) && n == d.FullName)
                continue;
            Settings.KnownNames[d.Id] = d.FullName;
            changed = true;
        }

        if (changed)
            Settings.Save();
    }

    /// <summary>The devices the hotkey cycles through right now: the chosen ones that are plugged in, or all of them.</summary>
    public List<AudioDevice> CycleList(EDataFlow flow)
    {
        var all = Audio.List(flow);
        var chosen = flow == EDataFlow.Render ? Settings.PlaybackCycle : Settings.RecordingCycle;
        if (chosen.Count == 0)
            return all;
        var picked = all.Where(d => chosen.Contains(d.Id)).ToList();
        return picked.Count > 0 ? picked : all;
    }

    /// <summary>Moves to the next device in the cycle.</summary>
    public AudioDevice? CycleNext(EDataFlow flow)
    {
        var list = CycleList(flow);
        var label = flow == EDataFlow.Render ? "PLAYBACK" : "RECORDING";
        if (list.Count == 0)
        {
            Notice?.Invoke(new SwitchNotice(label, "No devices plugged in", Warning: true));
            return null;
        }

        var current = Audio.GetDefault(flow)?.Id;
        var index = list.FindIndex(d => d.Id == current);
        var next = list[(index + 1) % list.Count];
        if (list.Count == 1 && next.Id == current)
        {
            Notice?.Invoke(new SwitchNotice(label, next.FullName + " is the only one", Warning: true));
            return null;
        }

        return SwitchTo(next) ? next : null;
    }

    public bool SwitchTo(AudioDevice device)
    {
        var ok = SetDefault(device.Id, device.Flow, out var moved);
        var label = device.Flow == EDataFlow.Render ? "PLAYBACK" : "RECORDING";
        Notice?.Invoke(ok
            ? new SwitchNotice(label, device.FullName + MovedNote(moved))
            : new SwitchNotice(label, "Windows refused to switch to " + device.FullName, Warning: true));
        return ok;
    }

    /// <summary>
    /// Makes a device the default and, when the setting is on, takes along the running apps that were given their own
    /// device in Windows' Volume mixer (they ignore the default otherwise). <paramref name="moved"/> counts those apps.
    /// </summary>
    private bool SetDefault(string id, EDataFlow flow, out int moved)
    {
        moved = 0;
        if (!Audio.SetDefault(id, Settings.SetCommunications))
            return false;
        if (Settings.MoveAppsWithOwnDevice)
            moved = Math.Max(0, AppRouting.ReleaseRunningApps(flow));
        if (flow == EDataFlow.Render)
            NudgeGame();
        return true;
    }

    /// <summary>
    /// If the window in front is a game on the "doesn't follow" list, press its restart-sound key there so it moves to
    /// the new default too. Games in the background are left alone: a key press would land in the wrong window.
    /// </summary>
    private void NudgeGame()
    {
        if (Settings.GameNudges.Count == 0 || KeySender.ForegroundExe() is not { } exe)
            return;
        var nudge = Settings.GameNudges.FirstOrDefault(g => g.Key is not null && g.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (nudge?.Key is { } key)
            KeySender.PressInGame(key, exe);
    }

    private static string MovedNote(int moved) => moved switch
    {
        0 => "",
        1 => "\n1 app with its own device moved along",
        _ => $"\n{moved} apps with their own device moved along",
    };

    public bool SwitchTo(string id, EDataFlow flow)
    {
        var device = Audio.List(flow).FirstOrDefault(d => d.Id == id);
        if (device is null)
        {
            Notice?.Invoke(new SwitchNotice(flow == EDataFlow.Render ? "PLAYBACK" : "RECORDING", Settings.NameOf(id) + " isn't plugged in", Warning: true));
            return false;
        }

        return SwitchTo(device);
    }

    public bool? ToggleMute()
    {
        var muted = Audio.SetMicMute(null);
        Notice?.Invoke(muted switch
        {
            true => new SwitchNotice("MICROPHONE", "Muted", Warning: true),
            false => new SwitchNotice("MICROPHONE", "Live"),
            null => new SwitchNotice("MICROPHONE", "No microphone found", Warning: true),
        });
        _post?.Invoke(() => DevicesChanged?.Invoke());
        return muted;
    }

    // ---- Profiles --------------------------------------------------------------------------------------------------

    /// <summary>Switches to a profile's devices. Only devices that aren't already default are touched (and announced).</summary>
    public bool ApplyProfile(Profile profile, bool quietIfNothingChanged = false)
    {
        var changed = new List<string>();
        var ok = true;
        foreach (var (id, flow) in new[] { (profile.PlaybackId, EDataFlow.Render), (profile.RecordingId, EDataFlow.Capture) })
        {
            if (id is null || Audio.GetDefault(flow)?.Id == id)
                continue;
            var device = Audio.List(flow).FirstOrDefault(d => d.Id == id);
            if (device is null || !SetDefault(id, flow, out var moved))
            {
                ok = false;
                changed.Add(Settings.NameOf(id) + " (not plugged in)");
                continue;
            }

            changed.Add(device.FullName + MovedNote(moved));
        }

        if (changed.Count > 0 || !quietIfNothingChanged)
            Notice?.Invoke(new SwitchNotice("PROFILE · " + profile.Name.ToUpperInvariant(),
                changed.Count == 0 ? "Already active" : string.Join("\n", changed), Warning: !ok));
        return ok;
    }

    public Profile? FindProfile(string name) =>
        Settings.Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ---- Hotkeys ---------------------------------------------------------------------------------------------------

    /// <summary>(Re)registers every hotkey from the settings; call after any hotkey changes.</summary>
    public void RegisterHotkeys()
    {
        if (_hotkeys is null)
            return;
        _hotkeys.UnregisterAll();
        HotkeyConflicts.Clear();
        Try(Settings.PlaybackHotkey, "Cycle playback", () => CycleNext(EDataFlow.Render));
        Try(Settings.RecordingHotkey, "Cycle recording", () => CycleNext(EDataFlow.Capture));
        Try(Settings.MuteHotkey, "Mute microphone", () => ToggleMute());
        foreach (var p in Settings.Profiles)
        {
            var profile = p;
            Try(p.Hotkey, "Profile " + p.Name, () => ApplyProfile(profile));
        }

        void Try(Hotkey? key, string what, Action action)
        {
            if (key is not null && !_hotkeys.Register(key, action))
                HotkeyConflicts.Add($"{what} ({key})");
        }
    }

    /// <summary>Stops all hotkeys while the user is typing a new one, so pressing it doesn't fire the old action.</summary>
    public void PauseHotkeys() => _hotkeys?.UnregisterAll();

    // ---- Programs that switch profiles -----------------------------------------------------------------------------

    /// <summary>Watches window focus only while some profile actually has programs; otherwise nothing runs.</summary>
    public void UpdateForegroundWatch()
    {
        var needed = Settings.Profiles.Any(p => p.Apps.Count > 0);
        if (needed && _foreground is null)
            _foreground = new ForegroundWatcher(OnForeground);
        else if (!needed && _foreground is not null)
        {
            _foreground.Dispose();
            _foreground = null;
        }
    }

    private void OnForeground(string exe, int pid)
    {
        if (exe == _lastForeground)
            return;
        _lastForeground = exe;
        if (exe == "pxdaudioswitcher.exe")
            return;

        var profile = Settings.Profiles.FirstOrDefault(p => p.Apps.Any(a => a.Equals(exe, StringComparison.OrdinalIgnoreCase)));
        if (profile is null)
            return;

        if (profile.RestoreOnClose && _pendingRestore?.Profile != profile)
        {
            // Remember what to go back to; a restore already waiting keeps its original devices.
            var prev = _pendingRestore;
            _pendingRestore = (profile,
                prev?.PrevPlayback ?? Audio.GetDefault(EDataFlow.Render)?.Id,
                prev?.PrevRecording ?? Audio.GetDefault(EDataFlow.Capture)?.Id);
            _restoreTimer ??= new Timer(_ => _post?.Invoke(CheckRestore), null, 2000, 2000);
        }

        ApplyProfile(profile, quietIfNothingChanged: true);
    }

    /// <summary>Every 2 s while a restore is waiting: once none of the profile's programs is running, put the old devices back.</summary>
    private void CheckRestore()
    {
        if (_pendingRestore is not { } r)
            return;
        var running = r.Profile.Apps.Any(a => System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(a)) is { Length: > 0 } procs && Dispose(procs));
        if (running)
            return;

        _pendingRestore = null;
        _restoreTimer?.Dispose();
        _restoreTimer = null;
        ApplyProfile(new Profile { Name = "Back to before", PlaybackId = r.PrevPlayback, RecordingId = r.PrevRecording }, quietIfNothingChanged: true);

        static bool Dispose(System.Diagnostics.Process[] procs)
        {
            foreach (var p in procs)
                p.Dispose();
            return true;
        }
    }

    public void Dispose()
    {
        _restoreTimer?.Dispose();
        _foreground?.Dispose();
        _hotkeys?.Dispose();
        Audio.Dispose();
    }
}
