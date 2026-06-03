using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ducker.Config;

/// <summary>One monitored audio source (a process whose output is scanned for speech).</summary>
public sealed class GameSource
{
    public string ProcessName { get; set; }
    public uint? Pid { get; set; }
    [JsonIgnore] public bool Auto { get; set; } // engine-detected source; transient, not persisted
}

/// <summary>A source the user has monitored before (manual or auto-detected), remembered so the
/// dashboard can offer it for quick re-adding even when it isn't currently running.</summary>
public sealed class RecentSource
{
    public string Name { get; set; }
    public string Title { get; set; }
}

/// <summary>Per-app override of the global action, keyed by process name. Action is "duck" (lower
/// the volume, then fade back), "pause" (stop playback via the app's media controls), or "ignore"
/// (never touch this app — e.g. voice chat).</summary>
public sealed class AppRule
{
    public string Name { get; set; }             // process name, e.g. "spotify" (with or without .exe)
    public string Action { get; set; } = "duck"; // "duck" | "pause" | "ignore"
}

/// <summary>
/// Persistent engine settings (JSON at %APPDATA%\Voxinator\settings.json).
/// Loading falls back to defaults on any error and migrates legacy single-game files.
/// </summary>
public sealed class EngineSettings
{
    public float Threshold { get; set; } = 0.35f;
    public int MinSpeechMs { get; set; } = 1;
    public int EndBufferMs { get; set; } = 2000;
    public bool Enabled { get; set; } = true;
    public bool AutoDetectGames { get; set; } = true;
    /// <summary>When on, common voice-chat / call apps (Discord, TeamSpeak, Zoom, …) are monitored
    /// as dialog sources while running — so your media ducks when someone is talking, just like a
    /// game's dialog. Independent of <see cref="AutoDetectGames"/>.</summary>
    public bool DuckForVoiceChat { get; set; } = false;

    /// <summary>Processes monitored for speech. Speech in ANY of them ducks media
    /// (e.g. a game's dialog AND a Discord call).</summary>
    public List<GameSource> Sources { get; set; } = new();

    /// <summary>Sources monitored at some point (most-recent first), for the dashboard's
    /// "used before" quick-add list. Capped; titles cached for display.</summary>
    public List<RecentSource> RecentSources { get; set; } = new();

    // ---- Ducking behavior (how other apps react to detected dialog) ----
    /// <summary>Global action for any audio app without its own rule: "duck" or "pause". The
    /// monitored game(s) and Voxinator itself are always left alone.</summary>
    public string DefaultAction { get; set; } = "duck";
    /// <summary>Target volume (0..1) when ducking.</summary>
    public float DuckVolume { get; set; } = 0.2f;
    /// <summary>Fade-back-in duration in ms when restoring after a duck. The duck-down is always
    /// instant; this only controls how gradually volume returns once dialog ends (0 = instant).</summary>
    public int RampMs { get; set; } = 300;
    /// <summary>Per-app overrides of the global action, keyed by process name. "ignore" spares an
    /// app entirely (e.g. voice chat).</summary>
    public List<AppRule> Apps { get; set; } = new();

    // Legacy single-source fields (pre multi-source); migrated to Sources on load.
    public uint? GamePid { get; set; }
    public string GameProcessName { get; set; }

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Voxinator");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static EngineSettings Load()
    {
        EngineSettings s;
        try { s = File.Exists(FilePath) ? JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { s = new(); }
        s.Migrate();
        return s;
    }

    private void Migrate()
    {
        Sources ??= new();
        Apps ??= new();
        RecentSources ??= new();
        if (Sources.Count == 0 && !string.IsNullOrEmpty(GameProcessName))
            Sources.Add(new GameSource { ProcessName = GameProcessName, Pid = GamePid });
        GamePid = null;
        GameProcessName = null;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public EngineSettings Clone()
    {
        var c = (EngineSettings)MemberwiseClone();
        c.Sources = Sources.Select(x => new GameSource { ProcessName = x.ProcessName, Pid = x.Pid }).ToList();
        c.Apps = Apps.Select(x => new AppRule { Name = x.Name, Action = x.Action }).ToList();
        c.RecentSources = RecentSources.Select(x => new RecentSource { Name = x.Name, Title = x.Title }).ToList();
        return c;
    }
}
