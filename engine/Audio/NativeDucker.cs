using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Ducker.Audio;

/// <summary>
/// Ducks other applications' audio natively through the Windows per-app mixer (WASAPI audio
/// session volume — the same sliders as the Volume Mixer). On dialog start, every render session
/// that is currently playing is cut instantly to the duck level, EXCEPT the monitored game(s),
/// Voxinator itself, and any user-ignored apps. On dialog end the volumes fade back to where they
/// were. This is the native replacement for the browser-extension duck path: it reaches any app
/// (browsers including Web Audio sites, the desktop Spotify app, etc.) with no extension.
///
/// Threading: the duck can be toggled from the capture thread, a timer, or the UI thread, so every
/// COM operation enumerates fresh within a single call and holds no Core Audio references across
/// calls. Only plain snapshot data is retained between calls (guarded by <see cref="_lock"/>).
/// </summary>
public sealed class NativeDucker : IDisposable
{
    private readonly object _lock = new();

    // What we lowered, captured at duck-start, keyed by PID: the session's pre-duck volume (to
    // restore) and the level we dropped it to. Pure data — safe to hold and read across threads.
    private sealed class Snap { public float Prev; public float Target; }
    private readonly Dictionary<int, Snap> _snap = new();

    private System.Threading.Timer _fade;
    private Stopwatch _fadeClock;
    private int _fadeMs;
    private bool _active; // we currently hold a duck (fully ducked or fading back)
    private bool _disposed;

    /// <summary>
    /// Drive ducking on/off. <paramref name="excludeNames"/> are process names (with or without a
    /// .exe suffix, case-insensitive) that must never be touched — the monitored game(s),
    /// Voxinator, and the user's ignore list.
    /// </summary>
    public void SetDucked(bool on, float duckVolume, int rampMs, IReadOnlyCollection<string> excludeNames)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (on) DuckOnLocked(Math.Clamp(duckVolume, 0f, 1f), excludeNames);
            else DuckOffLocked(Math.Max(0, rampMs));
        }
    }

    private void DuckOnLocked(float duckVolume, IReadOnlyCollection<string> excludeNames)
    {
        StopFadeLocked();

        // Dialog restarted while we were fading back: snap the same sessions to the duck level
        // again, keeping the original pre-duck volumes for the eventual restore.
        if (_active) { ApplyLevelsLocked(s => s.Target); return; }

        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludeNames != null) foreach (var n in excludeNames) exclude.Add(Norm(n));

        _snap.Clear();
        WithSessions((session, pid, state) =>
        {
            if (state != AudioSessionState.AudioSessionStateActive) return; // only what's actually playing
            if (exclude.Contains(Norm(ProcessName(pid)))) return;
            float prev;
            try { prev = session.SimpleAudioVolume.Volume; } catch { return; }
            float target = Math.Min(prev, duckVolume); // never raise a session that's already quieter
            try { session.SimpleAudioVolume.Volume = target; } catch { return; }
            _snap[pid] = new Snap { Prev = prev, Target = target };
        });
        _active = _snap.Count > 0;
    }

    private void DuckOffLocked(int rampMs)
    {
        if (!_active) return;
        StopFadeLocked();
        if (rampMs <= 0 || _snap.Count == 0) { RestoreFinalLocked(); return; }
        _fadeMs = rampMs;
        _fadeClock = Stopwatch.StartNew();
        _fade = new System.Threading.Timer(_ => FadeTick(), null, 0, 16);
    }

    private void FadeTick()
    {
        lock (_lock)
        {
            if (_fade == null || _fadeClock == null) return; // already stopped/superseded
            double frac = _fadeClock.Elapsed.TotalMilliseconds / _fadeMs;
            if (frac >= 1.0) { RestoreFinalLocked(); return; }
            float f = (float)frac;
            ApplyLevelsLocked(s => s.Target + (s.Prev - s.Target) * f);
        }
    }

    private void RestoreFinalLocked()
    {
        ApplyLevelsLocked(s => s.Prev);
        StopFadeLocked();
        _snap.Clear();
        _active = false;
    }

    // Re-enumerate sessions and set each one we ducked to a freshly computed level.
    private void ApplyLevelsLocked(Func<Snap, float> level)
    {
        if (_snap.Count == 0) return;
        WithSessions((session, pid, _) =>
        {
            if (!_snap.TryGetValue(pid, out var s)) return;
            try { session.SimpleAudioVolume.Volume = Math.Clamp(level(s), 0f, 1f); } catch { }
        });
    }

    private void StopFadeLocked()
    {
        _fade?.Dispose();
        _fade = null;
        _fadeClock = null;
    }

    // Enumerate the default render endpoint's sessions, invoking act for each. Disposes every Core
    // Audio object it creates; swallows a missing/unavailable device so a pass is simply skipped.
    private static void WithSessions(Action<AudioSessionControl, int, AudioSessionState> act)
    {
        MMDeviceEnumerator en = null;
        MMDevice dev = null;
        try
        {
            en = new MMDeviceEnumerator();
            dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = dev.AudioSessionManager.Sessions;
            if (sessions == null) return;
            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl s = null;
                try
                {
                    s = sessions[i];
                    int pid;
                    try { pid = (int)s.GetProcessID; } catch { continue; }
                    if (pid == 0) continue; // system/aggregate session
                    AudioSessionState state;
                    try { state = s.State; } catch { continue; }
                    act(s, pid, state);
                }
                finally { s?.Dispose(); }
            }
        }
        catch { /* default device unavailable or changing — skip this pass */ }
        finally { dev?.Dispose(); en?.Dispose(); }
    }

    private static string ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName ?? ""; }
        catch { return ""; }
    }

    // Normalize a process name for comparison: trim, drop a trailing ".exe", lower-case.
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
            StopFadeLocked();
            // Don't leave apps ducked if we're torn down mid-dialog.
            if (_active) { try { RestoreFinalLocked(); } catch { } }
        }
    }
}
