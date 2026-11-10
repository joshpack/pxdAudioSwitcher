using System.Diagnostics;
using System.Runtime.InteropServices;

namespace pxdAudioSwitcher.Audio;

/// <summary>
/// The per-app output and input devices from Windows' Volume mixer ("App volume and device preferences"). An app given
/// its own device there ignores the default device, so switching the default doesn't move it. This lets a switch take
/// those apps along by clearing their own device, after which they follow the default again.
/// </summary>
public static class AppRouting
{
    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";

    /// <summary>
    /// Clears the app's own device for <paramref name="flow"/> in every running app that has one, so they all follow the
    /// default. Returns how many apps were moved, or -1 when this version of Windows doesn't offer it.
    /// </summary>
    public static int ReleaseRunningApps(EDataFlow flow)
    {
        var factory = Create();
        if (factory is null)
            return -1;
        var moved = 0;
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    var pid = (uint)proc.Id;
                    if (pid == 0 || !HasOwnDevice(factory, pid, flow))
                        continue;
                    var ok = factory.SetPersistedDefaultAudioEndpoint(pid, flow, ERole.Console, IntPtr.Zero) == 0;
                    ok &= factory.SetPersistedDefaultAudioEndpoint(pid, flow, ERole.Multimedia, IntPtr.Zero) == 0;
                    if (ok)
                        moved++;
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }

        return moved;
    }

    /// <summary>Clears every app's own devices (output and input, running or not), like the mixer's Reset button.</summary>
    public static bool ReleaseAllApps()
    {
        var factory = Create();
        if (factory is null)
            return false;
        try
        {
            return factory.ClearAllPersistedApplicationDefaultEndpoints() == 0;
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>The device an app was given in the Volume mixer, or null when it follows the default. For diagnostics.</summary>
    public static string? OwnDevice(uint pid, EDataFlow flow)
    {
        var factory = Create();
        if (factory is null)
            return null;
        try
        {
            if (factory.GetPersistedDefaultAudioEndpoint(pid, flow, ERole.Multimedia, out var hstring) != 0 || hstring == IntPtr.Zero)
                return null;
            try
            {
                var text = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(hstring, out var length), (int)length);
                return string.IsNullOrEmpty(text) ? null : text;
            }
            finally
            {
                WindowsDeleteString(hstring);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private static bool HasOwnDevice(IAudioPolicyConfigFactory factory, uint pid, EDataFlow flow)
    {
        foreach (var role in new[] { ERole.Multimedia, ERole.Console })
        {
            if (factory.GetPersistedDefaultAudioEndpoint(pid, flow, role, out var hstring) != 0 || hstring == IntPtr.Zero)
                continue;
            WindowsDeleteString(hstring);
            return true;
        }

        return false;
    }

    private static IAudioPolicyConfigFactory? Create()
    {
        try
        {
            if (WindowsCreateString(ClassName, ClassName.Length, out var name) != 0)
                return null;
            try
            {
                var iid = typeof(IAudioPolicyConfigFactory).GUID;
                if (RoGetActivationFactory(name, ref iid, out var ptr) != 0 || ptr == IntPtr.Zero)
                    return null;
                try
                {
                    return (IAudioPolicyConfigFactory)Marshal.GetObjectForIUnknown(ptr);
                }
                finally
                {
                    Marshal.Release(ptr);
                }
            }
            finally
            {
                WindowsDeleteString(name);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string text, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
}

/// <summary>
/// Windows' internal audio policy factory (Windows 10 21H2 and later). Only the vtable order matters: the IInspectable
/// methods and the 19 methods before the per-app endpoint ones are placeholders that are never called.
/// </summary>
[ComImport, Guid("AB3D4648-E242-459F-B02F-541C70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPolicyConfigFactory
{
    [PreserveSig] int GetIids();
    [PreserveSig] int GetRuntimeClassName();
    [PreserveSig] int GetTrustLevel();
    [PreserveSig] int Reserved01();
    [PreserveSig] int Reserved02();
    [PreserveSig] int Reserved03();
    [PreserveSig] int Reserved04();
    [PreserveSig] int Reserved05();
    [PreserveSig] int Reserved06();
    [PreserveSig] int Reserved07();
    [PreserveSig] int Reserved08();
    [PreserveSig] int Reserved09();
    [PreserveSig] int Reserved10();
    [PreserveSig] int Reserved11();
    [PreserveSig] int Reserved12();
    [PreserveSig] int Reserved13();
    [PreserveSig] int Reserved14();
    [PreserveSig] int Reserved15();
    [PreserveSig] int Reserved16();
    [PreserveSig] int Reserved17();
    [PreserveSig] int Reserved18();
    [PreserveSig] int Reserved19();
    [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);
    [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);
    [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
}
