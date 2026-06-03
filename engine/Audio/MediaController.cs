using Ducker.Config;

namespace Ducker.Audio;

/// <summary>
/// Coordinates the two native control surfaces when dialog is detected. For each app currently
/// producing audio it resolves an action — from the global default plus per-app overrides — and
/// then PAUSES the "pause" apps (via SMTC) and DUCKS everything else (via the per-app mixer). The
/// monitored game(s) and Voxinator itself are always left alone. Apps marked "pause" that have no
/// usable media session fall back to ducking.
/// </summary>
public sealed class MediaController : IDisposable
{
    private readonly NativeDucker _ducker = new();
    private readonly MediaSessionController _smtc = new();

    public Action<string> Log { get => _smtc.Log; set => _smtc.Log = value; }

    /// <param name="on">true on dialog start, false on dialog end.</param>
    /// <param name="excludeNames">process names never touched (the monitored game(s) + Voxinator).</param>
    public void SetActive(bool on, EngineSettings s, IReadOnlyCollection<string> excludeNames)
    {
        if (on) On(s, excludeNames);
        else Off(s);
    }

    private void On(EngineSettings s, IReadOnlyCollection<string> excludeNames)
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in s.Apps ?? new List<AppRule>())
            if (!string.IsNullOrWhiteSpace(a.Name)) overrides[Norm(a.Name)] = NormAction(a.Action);

        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in excludeNames ?? Array.Empty<string>()) exclude.Add(Norm(n));

        string def = NormAction(s.DefaultAction);
        string Resolve(string name)
        {
            name = Norm(name);
            if (exclude.Contains(name)) return "ignore";
            return overrides.TryGetValue(name, out var a) ? a : def;
        }

        // 1. Pause the "pause" apps via SMTC; it returns the ones it actually paused.
        var paused = _smtc.SetPaused(true, Resolve);

        // 2. Duck everything else. Exclude: game(s)/self, "ignore" apps, and the apps we just paused.
        //    A "pause" app with no SMTC session isn't in `paused`, so it falls through to ducking.
        var duckExclude = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in overrides) if (kv.Value == "ignore") duckExclude.Add(kv.Key);
        foreach (var n in paused) duckExclude.Add(Norm(n));
        _ducker.SetDucked(true, s.DuckVolume, s.RampMs, duckExclude);
    }

    private void Off(EngineSettings s)
    {
        _ducker.SetDucked(false, s.DuckVolume, s.RampMs, Array.Empty<string>());
        _smtc.SetPaused(false, _ => "ignore");
    }

    private static string Norm(string s)
    {
        s = (s ?? "").Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.ToLowerInvariant();
    }

    private static string NormAction(string a)
    {
        if (string.Equals(a, "pause", StringComparison.OrdinalIgnoreCase)) return "pause";
        if (string.Equals(a, "ignore", StringComparison.OrdinalIgnoreCase)) return "ignore";
        return "duck";
    }

    public void Dispose()
    {
        try { _ducker.Dispose(); } catch { }
        try { _smtc.Dispose(); } catch { }
    }
}
