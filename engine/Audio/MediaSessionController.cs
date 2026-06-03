using Windows.Media.Control;

namespace Ducker.Audio;

/// <summary>
/// Pauses and resumes other apps' media natively through the Windows System Media Transport
/// Controls — the same play/pause surface as the media flyout and the keyboard media keys. Used
/// for apps whose action is "pause". Apps that don't register an SMTC session (e.g. some Web Audio
/// sites) can't be paused this way; the coordinator falls back to ducking those.
///
/// Reads (GetSessions/GetPlaybackInfo) are synchronous and safe from any thread; the only async
/// calls (TryPause/TryPlay) are fire-and-forget, so nothing blocks the engine.
/// </summary>
public sealed class MediaSessionController : IDisposable
{
    private readonly object _lock = new();
    private readonly HashSet<string> _pausedByUs = new(StringComparer.OrdinalIgnoreCase);
    private GlobalSystemMediaTransportControlsSessionManager _mgr;
    private bool _disposed;

    public Action<string> Log { get; set; }

    public MediaSessionController() => _ = InitAsync();

    private async Task InitAsync()
    {
        try { _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().ConfigureAwait(false); }
        catch (Exception ex) { Log?.Invoke("SMTC init failed: " + ex.Message); }
    }

    /// <summary>
    /// On dialog start (<paramref name="on"/> = true): pause every media session whose resolved
    /// action is "pause". On dialog end: resume the ones we paused. <paramref name="resolveAction"/>
    /// maps a process name to "duck" | "pause" | "ignore". Returns the set of app names this
    /// controller actually paused, so the caller can keep the ducker off them (apps that resolve to
    /// "pause" but have no usable SMTC session aren't returned, and fall back to ducking).
    /// </summary>
    public IReadOnlyCollection<string> SetPaused(bool on, Func<string, string> resolveAction)
    {
        var mgr = _mgr;
        if (mgr == null) return Array.Empty<string>();
        lock (_lock)
        {
            if (_disposed) return Array.Empty<string>();
            return on ? PauseOnLocked(mgr, resolveAction) : ResumeLocked(mgr);
        }
    }

    private IReadOnlyCollection<string> PauseOnLocked(
        GlobalSystemMediaTransportControlsSessionManager mgr, Func<string, string> resolveAction)
    {
        var paused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try { sessions = mgr.GetSessions(); } catch { return paused; }
        foreach (var s in sessions)
        {
            string name;
            try { name = Norm(s.SourceAppUserModelId); } catch { continue; }
            if (name.Length == 0) continue;
            if (!string.Equals(resolveAction(name), "pause", StringComparison.OrdinalIgnoreCase)) continue;
            GlobalSystemMediaTransportControlsSessionPlaybackInfo info;
            try { info = s.GetPlaybackInfo(); } catch { continue; }
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing && info.Controls.IsPauseEnabled)
            {
                _pausedByUs.Add(name);
                paused.Add(name);
                try { _ = s.TryPauseAsync(); } catch { }
            }
        }
        return paused;
    }

    private IReadOnlyCollection<string> ResumeLocked(GlobalSystemMediaTransportControlsSessionManager mgr)
    {
        if (_pausedByUs.Count == 0) return Array.Empty<string>();
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = null;
        try { sessions = mgr.GetSessions(); } catch { }
        if (sessions != null)
            foreach (var s in sessions)
            {
                string name;
                try { name = Norm(s.SourceAppUserModelId); } catch { continue; }
                if (!_pausedByUs.Contains(name)) continue;
                GlobalSystemMediaTransportControlsSessionPlaybackInfo info;
                try { info = s.GetPlaybackInfo(); } catch { continue; }
                // Only resume if it's still paused — don't fight the user if they hit play themselves.
                if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
                    && info.Controls.IsPlayEnabled)
                    try { _ = s.TryPlayAsync(); } catch { }
            }
        _pausedByUs.Clear();
        return Array.Empty<string>();
    }

    // Normalize an app id for comparison: drop a trailing ".exe", lower-case. SMTC's
    // SourceAppUserModelId is the exe name for Win32 apps (e.g. "firefox.exe"), matching the
    // process names the ducker and per-app rules use.
    private static string Norm(string s)
    {
        s = (s ?? "").Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.ToLowerInvariant();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            var mgr = _mgr;
            if (mgr != null && _pausedByUs.Count > 0) { try { ResumeLocked(mgr); } catch { } }
        }
    }
}
