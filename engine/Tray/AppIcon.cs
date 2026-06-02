using System.Drawing;

namespace Ducker.Tray;

/// <summary>Loads the bundled Voxinator icon (voxinator.ico), falling back to the system icon.</summary>
internal static class AppIcon
{
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "voxinator.ico");

    /// <summary>Returns a fresh Icon the caller owns (and should dispose), or the shared system icon.</summary>
    public static Icon Load()
    {
        try { if (System.IO.File.Exists(Path)) return new Icon(Path); }
        catch { }
        return SystemIcons.Application;
    }
}
