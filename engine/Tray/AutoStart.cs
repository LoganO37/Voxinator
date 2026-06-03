using System.Diagnostics;
using Microsoft.Win32;

namespace Ducker.Tray;

/// <summary>
/// Manages "launch when Windows starts" via the per-user Run key. Points at Velopack's stable
/// stub launcher (so it survives updates) with --startup, which boots Voxinator straight to the
/// tray with no window. The registry value is the source of truth for the UI toggle.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Voxinator";

    public static bool IsEnabled()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(ValueName) != null; }
        catch { return false; }
    }

    public static void Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;
            if (on) key.SetValue(ValueName, $"\"{LauncherPath()}\" --startup");
            else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* registry access denied / locked-down machine */ }
    }

    /// <summary>
    /// Deletes the per-user Run value if present. Velopack's uninstaller knows nothing about this
    /// key, so the uninstall hook must call this — otherwise it lingers and points at a deleted exe.
    /// </summary>
    public static void Remove()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* registry access denied / locked-down machine */ }
    }

    // Velopack installs a stable stub named after the packId at the install root (the parent of
    // the versioned "current" folder). Fall back to the running exe when not installed (dev).
    private static string LauncherPath()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            if (string.Equals(System.IO.Path.GetFileName(baseDir), "current", StringComparison.OrdinalIgnoreCase))
            {
                var root = System.IO.Path.GetDirectoryName(baseDir);
                var stub = System.IO.Path.Combine(root!, "Voxinator.exe");
                if (System.IO.File.Exists(stub)) return stub;
            }
        }
        catch { }
        try { return Process.GetCurrentProcess().MainModule?.FileName ?? Fallback(); } catch { return Fallback(); }
    }

    private static string Fallback() => System.IO.Path.Combine(AppContext.BaseDirectory, "voxinator.exe");
}
