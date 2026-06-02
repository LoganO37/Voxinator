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

/// <summary>How the browser extension reacts on a given site: "pause" (hard cut) or "duck" (fade).</summary>
public sealed class SiteRule
{
    public string Host { get; set; }
    public string Action { get; set; } = "pause"; // "pause" | "duck"
}

/// <summary>
/// Persistent engine settings (JSON at %APPDATA%\Voxinator\settings.json).
/// Loading falls back to defaults on any error and migrates legacy single-game files.
/// </summary>
public sealed class EngineSettings
{
    public int Port { get; set; } = 8730;
    public string Token { get; set; } = "changeme";
    public float Threshold { get; set; } = 0.35f;
    public int MinSpeechMs { get; set; } = 1;
    public int EndBufferMs { get; set; } = 2000;
    public bool Enabled { get; set; } = true;
    public bool AutoDetectGames { get; set; } = true;
    /// <summary>Set once the first-run "Get Extension" wizard has been shown.</summary>
    public bool ExtensionWizardSeen { get; set; } = false;

    /// <summary>Processes monitored for speech. Speech in ANY of them ducks media
    /// (e.g. a game's dialog AND a Discord call).</summary>
    public List<GameSource> Sources { get; set; } = new();

    // ---- Browser behavior (pushed to the extension over the WebSocket as a CONFIG message) ----
    /// <summary>Action for sites without a specific rule: "pause" or "duck".</summary>
    public string DefaultAction { get; set; } = "pause";
    /// <summary>Target volume (0..1) when ducking.</summary>
    public float DuckVolume { get; set; } = 0.2f;
    /// <summary>Fade-back-in duration in ms when restoring after a duck. The duck-down is always
    /// instant; this only controls how gradually volume returns once dialog ends (0 = instant).</summary>
    public int RampMs { get; set; } = 300;
    /// <summary>Per-site overrides. The most specific (longest) matching host wins in the extension.</summary>
    public List<SiteRule> Sites { get; set; } = DefaultSites();

    /// <summary>Native ducking model ("all apps except these"): process names (with or without
    /// .exe) that are never ducked/paused even though they aren't the monitored game — e.g. voice
    /// chat. The monitored game(s) and Voxinator itself are always excluded automatically.</summary>
    public List<string> IgnoredApps { get; set; } = new();

    // Only sites we've actually verified ship as defaults. Users can add others (Spotify,
    // Bandcamp, etc.) themselves in the Websites tab — the extension supports more than this.
    private static List<SiteRule> DefaultSites() => new()
    {
        new() { Host = "youtube.com",       Action = "pause" },
        new() { Host = "music.youtube.com", Action = "duck" },
        new() { Host = "music.amazon.com",  Action = "duck" },
        new() { Host = "soundcloud.com",    Action = "duck" },
        new() { Host = "audible.com",       Action = "pause" },
    };

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
        Sites ??= DefaultSites();
        IgnoredApps ??= new();
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
        c.Sites = Sites.Select(x => new SiteRule { Host = x.Host, Action = x.Action }).ToList();
        c.IgnoredApps = IgnoredApps?.ToList() ?? new();
        return c;
    }
}
