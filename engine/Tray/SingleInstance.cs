using System.Threading;

namespace Ducker.Tray;

/// <summary>
/// Ensures only one Voxinator runs at a time. The first instance owns a named mutex and listens
/// on a named event; a second launch (e.g. the desktop shortcut while the app is hidden in the
/// tray) detects the mutex, signals the event so the running instance re-opens its window, then
/// exits. Without this, the second process would collide with the first on the WebSocket port.
/// Names are session-local, which matches the typical "double-launch in one session" case.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = "Voxinator.SingleInstance";
    private const string EventName = "Voxinator.ShowWindow";

    private Mutex _mutex;
    private EventWaitHandle _showEvent;
    private Thread _listener;
    private volatile bool _disposed;

    /// <summary>True if we are the first/primary instance; false if another is already running.</summary>
    public bool TryAcquire()
    {
        try { _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew); return createdNew; }
        catch { return true; } // if the mutex can't be created, fail open and run normally
    }

    /// <summary>Ask the already-running instance to bring its window to the front.</summary>
    public void SignalShowWindow()
    {
        try { if (EventWaitHandle.TryOpenExisting(EventName, out var ev)) using (ev) ev.Set(); }
        catch { }
    }

    /// <summary>Primary instance: run <paramref name="onShow"/> whenever another launch signals us.</summary>
    public void ListenForShow(Action onShow)
    {
        try { _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName); }
        catch { return; }
        _listener = new Thread(() =>
        {
            while (!_disposed)
            {
                try { _showEvent.WaitOne(); } catch { break; }
                if (_disposed) break;
                try { onShow(); } catch { }
            }
        }) { IsBackground = true, Name = "VoxShowListener" };
        _listener.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        try { _showEvent?.Set(); } catch { }   // wake the listener so it exits
        try { _showEvent?.Dispose(); } catch { }
        try { _mutex?.Dispose(); } catch { }    // closing the handle releases the mutex
    }
}
