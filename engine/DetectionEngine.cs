using System.Diagnostics;
using System.Text.Json;
using Ducker.Audio;
using Ducker.Capture;
using Ducker.Config;
using Ducker.Interop;
using Ducker.Ipc;
using Ducker.Vad;

namespace Ducker;

/// <summary>
/// The product pipeline as a controllable service. Monitors one OR MORE source processes,
/// each with its own capture → chunker → VAD → debouncer chain (Silero is stateful, so each
/// stream needs its own instance). Media is ducked while ANY source has speech, and restored
/// only after all sources have been silent past their end buffer. Owns the WebSocket server
/// and an internal watchdog that re-resolves source PIDs by name when processes restart.
/// Used by both the tray app and the headless `service` command.
/// </summary>
public sealed class DetectionEngine : IDisposable
{
    private sealed class SourceMonitor : IDisposable
    {
        public string ProcessName;
        public uint Pid;
        public bool Active;       // debounced speech state for this source
        public bool Capturing;
        public float MaxProb;     // running max probability since last Probe() (diagnostics)
        public ProcessLoopbackCapture Capture;
        public Mono16kChunker Chunker;
        public SileroVad Vad;
        public Debouncer Debouncer;

        public void Dispose()
        {
            try { Capture?.Stop(); } catch { }
            try { Capture?.Dispose(); } catch { }
            try { Vad?.Dispose(); } catch { }
        }
    }

    private readonly string _modelPath;
    private readonly object _lock = new();
    private readonly List<SourceMonitor> _monitors = new();
    private readonly System.Threading.Timer _watchdog;
    private DialogWebSocketServer _ws;
    private int _wsPort;
    private string _wsToken;
    private EngineSettings _settings;
    private readonly GameLibrary _library;
    private readonly List<GameSource> _autoSources = new(); // engine-detected games (transient)
    private readonly NativeDucker _native = new();          // ducks other apps via the Windows mixer

    public bool Ducking { get; private set; }
    public int ExtensionClients => _ws?.ClientCount ?? 0; // browser extensions connected to the WS
    public event Action<bool> DuckChanged;
    public event Action StateChanged;
    public Action<string> Log { get; set; }

    public DetectionEngine(string modelPath, EngineSettings settings)
    {
        _modelPath = modelPath;
        _settings = settings;
        _library = GameLibrary.Load(Path.Combine(AppContext.BaseDirectory, "games.json"));
        StartWs(settings.Port, settings.Token);
        _watchdog = new System.Threading.Timer(_ => Watchdog(), null, 5000, 5000);
    }

    public EngineSettings Settings => _settings;

    /// <summary>Resolve source PIDs by name and start capturing. Call once after construction.</summary>
    public void Start()
    {
        lock (_lock) { RefreshAutoSourcesLocked(); RestartCaptureLocked(); }
    }

    /// <summary>For UI: (name, capturing, speaking) per source.</summary>
    public IReadOnlyList<(string name, bool capturing, bool active)> SourceStates()
    {
        lock (_lock)
            return _monitors.Select(m => (m.ProcessName, m.Capturing, m.Active)).ToList();
    }

    /// <summary>Diagnostics: max speech probability per source since the last call (then reset).</summary>
    public IReadOnlyList<(string name, float maxProb, bool active, bool capturing)> Probe()
    {
        lock (_lock)
        {
            var r = _monitors.Select(m => (m.ProcessName, m.MaxProb, m.Active, m.Capturing)).ToList();
            foreach (var m in _monitors) m.MaxProb = 0;
            return r;
        }
    }

    public string StatusSummary()
    {
        lock (_lock)
        {
            if (!_settings.Enabled) return "Disabled";
            var eff = EffectiveSourcesLocked().ToList();
            if (eff.Count == 0) return _settings.AutoDetectGames ? "Watching for games…" : "No sources selected";
            int capturing = _monitors.Count(m => m.Capturing);
            string names = string.Join(", ", eff.Select(s => s.ProcessName));
            if (Ducking) return $"DUCKING — {names}";
            return $"Listening ({capturing}/{eff.Count}) — {names}";
        }
    }

    public void ApplySettings(EngineSettings s)
    {
        lock (_lock)
        {
            bool wsChanged = s.Port != _wsPort || s.Token != _wsToken;
            _settings = s;
            if (wsChanged) { _ws?.Dispose(); StartWs(s.Port, s.Token); }
            // Apply threshold (read live) + timing in place — do NOT tear down captures.
            foreach (var m in _monitors) m.Debouncer?.Configure(s.MinSpeechMs, s.EndBufferMs);
            RestartCaptureLocked(); // incremental: only reacts to source changes; captures preserved
        }
    }

    public void AddSource(uint pid, string name)
    {
        lock (_lock)
        {
            name ??= "";
            var existing = _settings.Sources.FirstOrDefault(x => NameEq(x.ProcessName, name));
            if (existing != null) existing.Pid = pid;
            else _settings.Sources.Add(new GameSource { ProcessName = name, Pid = pid });
            RestartCaptureLocked();
        }
    }

    public void RemoveSource(string name)
    {
        lock (_lock)
        {
            _settings.Sources.RemoveAll(x => NameEq(x.ProcessName, name));
            RestartCaptureLocked();
        }
    }

    public void ClearSources() { lock (_lock) { _settings.Sources.Clear(); RestartCaptureLocked(); } }

    public void SetEnabled(bool enabled) { lock (_lock) { _settings.Enabled = enabled; RestartCaptureLocked(); } }

    public void SetAutoDetect(bool on) { lock (_lock) { _settings.AutoDetectGames = on; RefreshAutoSourcesLocked(); RestartCaptureLocked(); } }

    public int GameLibrarySize => _library.Count;

    public IReadOnlyList<(string process, string title)> LibraryGames()
        => _library.All().Select(e => (e.Process, e.Title)).ToList();

    public void AddSourceByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            name = name.Trim();
            if (!_settings.Sources.Any(x => NameEq(x.ProcessName, name)))
                _settings.Sources.Add(new GameSource { ProcessName = name, Pid = null });
            RestartCaptureLocked();
        }
    }

    public bool HasSource(string name)
    {
        lock (_lock) return _settings.Sources.Any(x => NameEq(x.ProcessName, name));
    }

    public bool IsAutoSource(string name)
    {
        lock (_lock) return _autoSources.Any(a => NameEq(a.ProcessName, name));
    }

    private void StartWs(int port, string token)
    {
        _ws = new DialogWebSocketServer(port, token);
        _ws.Start();
        _wsPort = port;
        _wsToken = token;
        try { _ws.SetConfig(BuildConfigJson()); } catch { } // seed the new server with current behavior config
        Log?.Invoke($"WebSocket listening on ws://127.0.0.1:{port}/");
    }

    // ---- browser behavior config (pushed to the extension) ----

    // Serialize the current site rules + duck/ramp/default-action into the CONFIG message the
    // extension consumes. Safe to call single-threaded (ctor) or under _lock.
    private string BuildConfigJson()
    {
        var s = _settings;
        var sites = (s.Sites ?? new List<SiteRule>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Host))
            .Select(r => new { host = r.Host.Trim().ToLowerInvariant(), action = NormAction(r.Action) });
        return JsonSerializer.Serialize(new
        {
            type = "CONFIG",
            defaultAction = NormAction(s.DefaultAction),
            duckVolume = s.DuckVolume,
            rampMs = s.RampMs,
            sites,
        });
    }

    public void BroadcastConfig()
    {
        string json;
        lock (_lock) json = BuildConfigJson();
        try { _ws?.SetConfig(json); } catch { }
    }

    public void SetDefaultAction(string action)
    {
        lock (_lock) _settings.DefaultAction = NormAction(action);
        PersistAndPushConfig();
    }

    public void SetDuckVolume(float v)
    {
        lock (_lock) _settings.DuckVolume = Math.Clamp(v, 0f, 1f);
        PersistAndPushConfig();
    }

    public void SetRampMs(int ms)
    {
        lock (_lock) _settings.RampMs = Math.Clamp(ms, 0, 10000);
        PersistAndPushConfig();
    }

    public void SetSite(string host, string action)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        lock (_lock)
        {
            host = host.Trim().ToLowerInvariant();
            var r = _settings.Sites.FirstOrDefault(x => NameEq(x.Host, host));
            if (r != null) r.Action = NormAction(action);
            else _settings.Sites.Add(new SiteRule { Host = host, Action = NormAction(action) });
        }
        PersistAndPushConfig();
    }

    public void RemoveSite(string host)
    {
        lock (_lock) _settings.Sites.RemoveAll(x => NameEq(x.Host, host));
        PersistAndPushConfig();
    }

    private static string NormAction(string a) =>
        string.Equals(a, "duck", StringComparison.OrdinalIgnoreCase) ? "duck" : "pause";

    private void PersistAndPushConfig()
    {
        try { _settings.Save(); } catch (Exception ex) { Log?.Invoke("save failed: " + ex.Message); }
        BroadcastConfig();
        RaiseState();
    }

    // Manual sources (from settings) plus auto-detected running games, de-duped by name
    // (a manual entry wins over an auto one for the same process).
    private IEnumerable<GameSource> EffectiveSourcesLocked()
    {
        foreach (var m in _settings.Sources) yield return m;
        foreach (var a in _autoSources)
            if (!_settings.Sources.Any(m => NameEq(m.ProcessName, a.ProcessName))) yield return a;
    }

    // Auto-detect: add running known games (from games.json) as sources; drop ones that closed
    // or that the user has since added manually. Called each watchdog tick.
    private void RefreshAutoSourcesLocked()
    {
        if (!_settings.Enabled || !_settings.AutoDetectGames)
        {
            if (_autoSources.Count > 0) _autoSources.Clear();
            return;
        }
        var detected = _library.FindRunningGames();
        _autoSources.RemoveAll(a =>
            !detected.Any(d => NameEq(d.name, a.ProcessName)) ||
            _settings.Sources.Any(m => NameEq(m.ProcessName, a.ProcessName)));
        foreach (var d in detected)
        {
            if (_settings.Sources.Any(m => NameEq(m.ProcessName, d.name))) continue;
            var existing = _autoSources.FirstOrDefault(a => NameEq(a.ProcessName, d.name));
            if (existing != null) existing.Pid = d.pid;
            else
            {
                _autoSources.Add(new GameSource { ProcessName = d.name, Pid = d.pid, Auto = true });
                Log?.Invoke($"auto-detected game: {d.title} ({d.name}, pid {d.pid})");
            }
        }
    }

    // Make running monitors match the enabled sources. Incremental: preserves healthy
    // captures, drops removed/dead ones, and (re)starts any missing — so a transient
    // capture-start failure is retried on the next watchdog tick instead of sticking at 0/N.
    // Only successfully-started monitors are kept, so _monitors.Count == sources capturing.
    private void RestartCaptureLocked()
    {
        // Resolve any dead/missing source PIDs by process name.
        foreach (var src in _settings.Sources)
            if (!(src.Pid is uint ap && IsAlive(ap))) src.Pid = ResolvePid(src.ProcessName);

        if (!_settings.Enabled)
        {
            foreach (var m in _monitors) m.Dispose();
            _monitors.Clear();
            RecomputeDuckLocked();
            RaiseState();
            return;
        }

        var sources = EffectiveSourcesLocked().ToList();
        var wanted = sources.Where(s => s.Pid is uint).Select(s => (uint)s.Pid).ToHashSet();

        // Drop monitors whose source was removed or whose process died.
        for (int i = _monitors.Count - 1; i >= 0; i--)
        {
            var m = _monitors[i];
            if (!wanted.Contains(m.Pid) || !IsAlive(m.Pid)) { m.Dispose(); _monitors.RemoveAt(i); }
        }

        // Start a monitor for any wanted source not already capturing. On failure, don't keep
        // a dead monitor — the next watchdog tick retries.
        foreach (var src in sources)
        {
            if (src.Pid is not uint pid) continue;
            if (_monitors.Any(m => m.Pid == pid)) continue;
            var mon = new SourceMonitor { ProcessName = src.ProcessName ?? "", Pid = pid };
            try { StartMonitor(mon, pid); _monitors.Add(mon); }
            catch (Exception ex) { Log?.Invoke($"capture failed for {mon.ProcessName} (pid {pid}): {ex.Message}; will retry"); mon.Dispose(); }
        }

        RecomputeDuckLocked();
        RaiseState();
    }

    private void StartMonitor(SourceMonitor mon, uint pid)
    {
        mon.Vad = new SileroVad(_modelPath);
        mon.Debouncer = new Debouncer(new DebounceParams
        {
            ChunkMs = SileroVad.ChunkSeconds * 1000,
            MinSpeechMs = _settings.MinSpeechMs,
            EndBufferMs = _settings.EndBufferMs,
        });
        mon.Debouncer.DialogStart += (_, __) => { mon.Active = true; RecomputeDuck(); };
        mon.Debouncer.DialogEnd += (_, __) => { mon.Active = false; RecomputeDuck(); };

        var vad = mon.Vad;
        var deb = mon.Debouncer;
        mon.Capture = new ProcessLoopbackCapture(pid, ProcessLoopbackMode.IncludeTargetProcessTree);
        mon.Chunker = new Mono16kChunker(mon.Capture.WaveFormat);
        mon.Chunker.ChunkReady += chunk =>
        {
            float p = vad.Process(chunk);
            if (p > mon.MaxProb) mon.MaxProb = p;
            deb.Push(p >= _settings.Threshold, p); // read threshold live so settings apply without rebuild
        };
        mon.Capture.DataAvailable += (_, e) => mon.Chunker.Add(e.Buffer, e.BytesRecorded);
        mon.Capture.Start();
        mon.Capturing = true;
        Log?.Invoke($"capturing {mon.ProcessName} (pid {pid})");
    }

    private void RecomputeDuck() { lock (_lock) RecomputeDuckLocked(); }

    private void RecomputeDuckLocked() => SetDuckLocked(_monitors.Any(m => m.Active));

    private void SetDuckLocked(bool on)
    {
        if (Ducking == on) return;
        Ducking = on;
        Log?.Invoke(on ? ">>> DUCK (native + DIALOG_START)" : "<<< UNDUCK (native + DIALOG_END)");
        // Native per-app ducking via the Windows mixer (the new default path).
        try { _native.SetDucked(on, _settings.DuckVolume, _settings.RampMs, ExcludeNamesLocked()); }
        catch (Exception ex) { Log?.Invoke("native duck failed: " + ex.Message); }
        // Browser extension over the WebSocket (kept in parallel until the native path is proven;
        // removed in Phase 4). If the extension is connected this double-ducks the browser — test
        // native with the extension disconnected.
        _ws?.Broadcast(on
            ? $"{{\"type\":\"DIALOG_START\",\"ts\":{Now()}}}"
            : $"{{\"type\":\"DIALOG_END\",\"ts\":{Now()}}}");
        DuckChanged?.Invoke(on);
        RaiseState();
    }

    // Process names the native ducker must never touch: every monitored source (you never duck the
    // thing you're detecting dialog on), Voxinator itself, and the user's ignore list.
    private HashSet<string> ExcludeNamesLocked()
    {
        var ex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in EffectiveSourcesLocked())
            if (!string.IsNullOrWhiteSpace(s.ProcessName)) ex.Add(s.ProcessName);
        foreach (var m in _monitors)
            if (!string.IsNullOrWhiteSpace(m.ProcessName)) ex.Add(m.ProcessName);
        if (_settings.IgnoredApps != null)
            foreach (var n in _settings.IgnoredApps)
                if (!string.IsNullOrWhiteSpace(n)) ex.Add(n);
        try { ex.Add(Process.GetCurrentProcess().ProcessName); } catch { }
        return ex;
    }

    // Every tick: re-resolve PIDs, drop dead captures, and retry any source that isn't
    // currently capturing (e.g. a transient start failure, or the game relaunched).
    private void Watchdog()
    {
        lock (_lock)
        {
            RefreshAutoSourcesLocked();
            RestartCaptureLocked();
        }
    }

    private static bool NameEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsAlive(uint pid)
    {
        try { using var _ = Process.GetProcessById((int)pid); return true; }
        catch { return false; }
    }

    private static uint? ResolvePid(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        try
        {
            var ps = Process.GetProcessesByName(name);
            if (ps.Length == 0) return null;
            uint pid = (uint)ps[0].Id;
            foreach (var p in ps) p.Dispose();
            return pid;
        }
        catch { return null; }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private void RaiseState() => StateChanged?.Invoke();

    public void Dispose()
    {
        try { _watchdog?.Dispose(); } catch { }
        try { _native.Dispose(); } catch { }
        lock (_lock)
        {
            foreach (var m in _monitors) m.Dispose();
            _monitors.Clear();
            _ws?.Dispose();
        }
    }
}
