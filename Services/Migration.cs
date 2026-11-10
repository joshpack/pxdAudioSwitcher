using pxdStandard;

namespace pxdAudioSwitcher.Services;

/// <summary>
/// One-time moves from the app's former name (see pxdMigrate), run at start-up before anything reads settings. Safe to
/// repeat (nothing happens once the new name is in place or the old one is gone) and never throws.
/// </summary>
public static class Migration
{
    /// <summary>The settings folder and the "Start when I sign in" entry. Skipped when PXDAS_DATA_DIR is set (tests).</summary>
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("PXDAS_DATA_DIR") is { Length: > 0 })
            return; // settings live somewhere else on purpose: leave the real folders and the Run key alone
        DataFolder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

        // The Run value moves to the new name and this exe, keeping its arguments (--minimized) and Task Manager's
        // enabled/disabled state. Never from a development run through dotnet.
        if (Environment.ProcessPath is { } exe && !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            pxdMigrate.RunEntry(pxdMigrate.FormerName(TrayLink.AppId), TrayLink.AppId, exe);
    }

    /// <summary>Moves &lt;root&gt;\(former name) to &lt;root&gt;\pxdAudioSwitcher when only the former one exists. True if it moved.</summary>
    public static bool DataFolder(string root) =>
        pxdMigrate.Folder(Path.Combine(root, pxdMigrate.FormerName(TrayLink.AppId)), Path.Combine(root, TrayLink.AppId));
}
