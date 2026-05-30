using Ducker.Config;

namespace Ducker.Tray;

/// <summary>Minimal append-only file log (console is hidden in tray mode).
/// Writes to %APPDATA%\Voxinator\log.txt.</summary>
internal static class TrayLogger
{
    private static readonly object Gate = new();
    private static string Path => System.IO.Path.Combine(EngineSettings.Dir, "log.txt");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(EngineSettings.Dir);
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
