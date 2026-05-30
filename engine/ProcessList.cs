using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace Ducker;

/// <summary>Enumerates processes that currently have an audio session on the default
/// render device — i.e. apps that are (or recently were) playing sound. This is the
/// friendliest way to find a game's PID for the manual picker.</summary>
internal static class ProcessList
{
    public record SessionInfo(int Pid, string ProcessName, string State, string DisplayName);

    public static IEnumerable<SessionInfo> AudioSessions()
    {
        var results = new List<SessionInfo>();
        using var en = new MMDeviceEnumerator();
        var device = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            int pid;
            try { pid = (int)s.GetProcessID; } catch { continue; }
            if (pid == 0) continue;

            string name = "(unknown)";
            try { name = Process.GetProcessById(pid).ProcessName; } catch { }

            string display = "";
            try { display = s.DisplayName ?? ""; } catch { }
            if (string.IsNullOrWhiteSpace(display)) display = name;

            results.Add(new SessionInfo(pid, name, s.State.ToString().Replace("AudioSessionState", ""), display));
        }
        return results
            .GroupBy(r => r.Pid)
            .Select(g => g.First())
            .OrderByDescending(r => r.State == "Active")
            .ThenBy(r => r.ProcessName);
    }
}
