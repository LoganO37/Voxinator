using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ducker.Config;

/// <summary>One monitored audio source (a process whose output is scanned for speech).</summary>
public sealed class GameSource
{
    public string ProcessName { get; set; }
    public uint? Pid { get; set; }
}

/// <summary>
/// Persistent engine settings (JSON at %APPDATA%\Voxinator\settings.json).
/// Loading falls back to defaults on any error and migrates legacy single-game files.
/// </summary>
public sealed class EngineSettings
{
    public int Port { get; set; } = 8730;
    public string Token { get; set; } = "changeme";
    public float Threshold { get; set; } = 0.5f;
    public int MinSpeechMs { get; set; } = 250;
    public int EndBufferMs { get; set; } = 2000;
    public bool Enabled { get; set; } = true;

    /// <summary>Processes monitored for speech. Speech in ANY of them ducks media
    /// (e.g. a game's dialog AND a Discord call).</summary>
    public List<GameSource> Sources { get; set; } = new();

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
        return c;
    }
}
