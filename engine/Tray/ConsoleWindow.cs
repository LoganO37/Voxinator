using System.Runtime.InteropServices;

namespace Ducker.Tray;

/// <summary>Hides the console window for a clean tray experience — but only when this
/// process owns the console by itself.</summary>
internal static class ConsoleWindow
{
    private const int SW_HIDE = 0;

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList(uint[] lpdwProcessList, uint dwProcessCount);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// Hides the console ONLY when this process is the sole owner of it — i.e. it was
    /// launched by double-click / Start-Process and Windows allocated a fresh console.
    /// If we were launched from an existing terminal (PowerShell, cmd), the console is
    /// shared and hiding it would hide the user's shell window, which looks like a crash.
    /// In that case we leave the window visible.
    /// </summary>
    public static void Hide()
    {
        var hwnd = GetConsoleWindow();
        if (hwnd == IntPtr.Zero) return; // no console attached
        var pids = new uint[8];
        uint count = GetConsoleProcessList(pids, (uint)pids.Length);
        if (count == 1) ShowWindow(hwnd, SW_HIDE);
    }
}
