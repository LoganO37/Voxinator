using System.Diagnostics;

namespace Ducker;

/// <summary>
/// Common voice-chat / call apps that can be monitored as dialog sources — like games — when
/// "Duck for voice chat" is on. Keyed by process (executable) name. Unlike the game library these
/// run persistently, so they're monitored whenever they're running (VAD only fires on actual
/// speech, so a silent/idle call costs nothing but capture).
/// </summary>
internal static class VoiceApps
{
    public static readonly IReadOnlyDictionary<string, string> Known =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Discord"] = "Discord",
            ["DiscordCanary"] = "Discord Canary",
            ["DiscordPTB"] = "Discord PTB",
            ["ts3client_win64"] = "TeamSpeak 3",
            ["TeamSpeak"] = "TeamSpeak",
            ["mumble"] = "Mumble",
            ["Zoom"] = "Zoom",
            ["ms-teams"] = "Microsoft Teams",
            ["Teams"] = "Microsoft Teams",
            ["slack"] = "Slack",
        };

    /// <summary>Currently-running processes that match a known voice app (by executable name).</summary>
    public static List<(uint pid, string name, string title)> FindRunning()
    {
        var found = new List<(uint, string, string)>();
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return found; }
        foreach (var p in procs)
        {
            try { if (Known.TryGetValue(p.ProcessName, out var title)) found.Add(((uint)p.Id, p.ProcessName, title)); }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
        return found;
    }
}
