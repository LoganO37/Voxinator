using System.Diagnostics;
using System.Text.Json;

namespace Ducker;

/// <summary>
/// A bundled library of popular games keyed by process (executable) name, loaded from
/// games.json next to the exe. Auto-detect uses it to find which known games are currently
/// running so they can be monitored automatically. The file is plain JSON and user-editable.
/// </summary>
public sealed class GameLibrary
{
    public sealed record Entry(string Process, string Title);

    private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);
    public int Count => _byName.Count;

    public IReadOnlyList<Entry> All() =>
        _byName.Select(kv => new Entry(kv.Key, kv.Value)).OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The friendly title for a known game's process name, if any.</summary>
    public bool TryGetTitle(string process, out string title) => _byName.TryGetValue(process ?? "", out title);

    public static GameLibrary Load(string path)
    {
        var lib = new GameLibrary();
        try
        {
            if (File.Exists(path))
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), opts);
                if (entries != null)
                    foreach (var e in entries)
                        if (!string.IsNullOrWhiteSpace(e.Process))
                            lib._byName[e.Process.Trim()] = string.IsNullOrWhiteSpace(e.Title) ? e.Process : e.Title;
            }
        }
        catch { /* malformed file -> empty library, auto-detect simply finds nothing */ }
        return lib;
    }

    /// <summary>Currently-running processes that match a known game (by executable name).</summary>
    public List<(uint pid, string name, string title)> FindRunningGames()
    {
        var found = new List<(uint, string, string)>();
        if (_byName.Count == 0) return found;
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return found; }
        foreach (var p in procs)
        {
            try
            {
                if (_byName.TryGetValue(p.ProcessName, out var title))
                    found.Add(((uint)p.Id, p.ProcessName, title));
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
        return found;
    }
}
