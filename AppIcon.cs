using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace pxdAudioSwitcher;

/// <summary>The app's speaker mark, drawn from vector geometry so it stays crisp at every size.</summary>
public static class AppIcon
{
    /// <summary>The speaker body, filled, in a 100x100 box.</summary>
    public static readonly Geometry Speaker = Geometry.Parse("M8,36 L28,36 L52,14 L52,86 L28,64 L8,64 Z");

    /// <summary>The sound waves, stroked.</summary>
    public static readonly Geometry Waves = Geometry.Parse("M64,34 A20,20 0 0 1 64,66 M74,20 A36,36 0 0 1 74,80");

    /// <summary>A slash across the waves, for a muted microphone.</summary>
    public static readonly Geometry Slash = Geometry.Parse("M60,30 L92,70");

    /// <summary>The app icon for the tray and windows (see <see cref="BrandIcon"/>).</summary>
    public static WindowIcon Create(int size = 64, bool muted = false) => BrandIcon.Window(size, muted);
}
