using System.Runtime.InteropServices;

namespace pxdAudioSwitcher.Audio;

/// <summary>One playback or recording device as Windows lists it.</summary>
/// <param name="Name">What the Sound control panel shows, e.g. "Speakers".</param>
/// <param name="Adapter">The hardware behind it, e.g. "Realtek High Definition Audio".</param>
public sealed record AudioDevice(string Id, string Name, string Adapter, EDataFlow Flow)
{
    /// <summary>"Speakers (Realtek High Definition Audio)": the same full name Windows uses.</summary>
    public string FullName => Adapter.Length > 0 && !Name.Contains(Adapter, StringComparison.OrdinalIgnoreCase) ? $"{Name} ({Adapter})" : Name;
}

/// <summary>Made-up devices and defaults, for pictures of the window that show nothing of this PC.</summary>
public sealed record SampleDevices(List<AudioDevice> Devices, string PlaybackId, string RecordingId, bool MicMuted = false);

/// <summary>
/// Lists audio devices, reads and changes the default ones, mutes the microphone and reports when anything about the
/// devices changes. Must be created and used on one STA thread (the UI thread).
/// </summary>
public sealed class AudioDevices : IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly NotificationClient? _client;

    /// <summary>Raised (on a Core Audio thread) when a device is added, removed, enabled, disabled or made default.</summary>
    public event Action? Changed;

    /// <summary>Made-up devices for the README pictures (self-test only); null on every real run.</summary>
    private readonly SampleDevices? _sample;

    public AudioDevices(bool watch = true)
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (watch)
        {
            _client = new NotificationClient(() => Changed?.Invoke());
            _enumerator.RegisterEndpointNotificationCallback(_client);
        }
    }

    /// <summary>Shows <paramref name="sample"/> instead of this PC's devices; changes nothing in Windows.</summary>
    public AudioDevices(SampleDevices sample) : this(watch: false) => _sample = sample;

    /// <summary>The devices that are plugged in and enabled, in Windows' own order.</summary>
    public List<AudioDevice> List(EDataFlow flow)
    {
        if (_sample is not null)
            return _sample.Devices.Where(d => d.Flow == flow).ToList();
        var result = new List<AudioDevice>();
        if (_enumerator.EnumAudioEndpoints(flow, CoreAudioNative.DeviceStateActive, out var collection) != 0)
            return result;
        try
        {
            collection.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0)
                    continue;
                try
                {
                    if (Describe(device, flow) is { } d)
                        result.Add(d);
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(collection);
        }

        return result;
    }

    /// <summary>The device Windows currently plays to (or records from), or null when there is none.</summary>
    public AudioDevice? GetDefault(EDataFlow flow, ERole role = ERole.Multimedia)
    {
        if (_sample is not null)
            return _sample.Devices.FirstOrDefault(d => d.Id == (flow == EDataFlow.Render ? _sample.PlaybackId : _sample.RecordingId));
        if (_enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0)
            return null;
        try
        {
            return Describe(device, flow);
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    /// <summary>Makes a device the default. With <paramref name="communications"/> it also becomes the device calls use.</summary>
    public bool SetDefault(string id, bool communications)
    {
        if (_sample is not null)
            return false;
        var policy = (IPolicyConfig)new PolicyConfigComObject();
        try
        {
            var ok = policy.SetDefaultEndpoint(id, ERole.Console) == 0;
            ok &= policy.SetDefaultEndpoint(id, ERole.Multimedia) == 0;
            if (communications)
                ok &= policy.SetDefaultEndpoint(id, ERole.Communications) == 0;
            return ok;
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    /// <summary>Mutes or unmutes the default recording device; null toggles. Returns the new state, or null without a microphone.</summary>
    public bool? SetMicMute(bool? mute)
    {
        if (_sample is not null)
            return null;
        var volume = DefaultMicVolume();
        if (volume is null)
            return null;
        try
        {
            volume.GetMute(out var current);
            var target = mute ?? !current;
            var context = Guid.Empty;
            volume.SetMute(target, ref context);
            return target;
        }
        finally
        {
            Marshal.ReleaseComObject(volume);
        }
    }

    public bool? IsMicMuted()
    {
        if (_sample is not null)
            return _sample.MicMuted;
        var volume = DefaultMicVolume();
        if (volume is null)
            return null;
        try
        {
            return volume.GetMute(out var muted) == 0 ? muted : null;
        }
        finally
        {
            Marshal.ReleaseComObject(volume);
        }
    }

    private IAudioEndpointVolume? DefaultMicVolume()
    {
        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Communications, out var device) != 0
            && _enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Console, out device) != 0)
            return null;
        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            return device.Activate(ref iid, CoreAudioNative.ClsctxAll, IntPtr.Zero, out var obj) == 0 ? (IAudioEndpointVolume)obj : null;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private static AudioDevice? Describe(IMMDevice device, EDataFlow flow)
    {
        if (device.GetId(out var id) != 0)
            return null;
        string name = "", adapter = "";
        if (device.OpenPropertyStore(0, out var store) == 0)
        {
            try
            {
                name = ReadString(store, CoreAudioNative.DeviceDescription);
                adapter = ReadString(store, CoreAudioNative.InterfaceFriendlyName);
                if (name.Length == 0)
                    name = ReadString(store, CoreAudioNative.DeviceFriendlyName);
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        return new AudioDevice(id, name.Length > 0 ? name : "Unknown device", adapter, flow);
    }

    private static string ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0)
            return "";
        try
        {
            return value.Type == CoreAudioNative.VtLpwstr ? Marshal.PtrToStringUni(value.Value) ?? "" : "";
        }
        finally
        {
            CoreAudioNative.PropVariantClear(ref value);
        }
    }

    public void Dispose()
    {
        if (_client is not null)
            _enumerator.UnregisterEndpointNotificationCallback(_client);
        Marshal.ReleaseComObject(_enumerator);
    }

    /// <summary>Forwards Windows' device notifications. Property changes (volume, names) fire constantly and are ignored.</summary>
    [ComVisible(true)]
    private sealed class NotificationClient(Action changed) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string id, uint newState) { changed(); return 0; }
        public int OnDeviceAdded(string id) { changed(); return 0; }
        public int OnDeviceRemoved(string id) { changed(); return 0; }
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id) { changed(); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }
}
