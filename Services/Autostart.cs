using Microsoft.Win32;

namespace pxdAudioSwitcher.Services;

/// <summary>Start with Windows through a per-user Run registry entry.</summary>
public static class Autostart
{
    private const string AppName = "pxdAudioSwitcher";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return LaunchCommand() is { } command && key?.GetValue(AppName) is string existing
                   && string.Equals(existing, command + " --minimized", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        var command = LaunchCommand() ?? throw new InvalidOperationException("Can't determine how this app was launched.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(AppName, command + " --minimized");
        else
            key.DeleteValue(AppName, throwOnMissingValue: false);
    }

    private static string? LaunchCommand()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return null;

        // Running through "dotnet pxdAudioSwitcher.dll" (e.g. dotnet run): register the dll under dotnet instead.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Environment.GetCommandLineArgs().FirstOrDefault();
            return string.IsNullOrEmpty(dll) ? null : $"\"{exe}\" \"{dll}\"";
        }

        return $"\"{exe}\"";
    }
}
