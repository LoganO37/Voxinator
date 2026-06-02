using Velopack;
using Velopack.Sources;

namespace Ducker.Update;

/// <summary>
/// Thin wrapper over Velopack's UpdateManager for silent background updates from GitHub Releases.
/// On startup we check + download in the background and stage the update; it is applied when the
/// app next exits (WaitExitThenApplyUpdates) so the user is never interrupted. A "Restart now"
/// action applies immediately. All operations are no-ops in a dev / un-packaged run.
/// </summary>
public sealed class Updater
{
    private const string Repo = "https://github.com/LoganO37/Voxinator";

    private readonly UpdateManager _mgr;
    private UpdateInfo _staged;

    public Updater()
    {
        UpdateManager mgr = null;
        try { mgr = new UpdateManager(new GithubSource(Repo, null, false)); } catch { }
        _mgr = mgr;
    }

    /// <summary>True only when running from a Velopack install (not dev/F5).</summary>
    public bool IsInstalled { get { try { return _mgr?.IsInstalled == true; } catch { return false; } } }

    public bool UpdateStaged { get; private set; }
    public string StagedVersion { get; private set; }

    /// <summary>Raised (off the UI thread) once an update has finished downloading and is staged.</summary>
    public event Action StagedChanged;

    public string CurrentVersion
    {
        get
        {
            try { if (IsInstalled && _mgr.CurrentVersion != null) return _mgr.CurrentVersion.ToString(); } catch { }
            var v = typeof(Updater).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Background check + download. Safe to call fire-and-forget.</summary>
    public async Task CheckAndStageAsync()
    {
        if (!IsInstalled || UpdateStaged) return;
        try
        {
            var info = await _mgr.CheckForUpdatesAsync();
            if (info == null) return; // already up to date
            await _mgr.DownloadUpdatesAsync(info);
            _staged = info;
            StagedVersion = info.TargetFullRelease?.Version?.ToString();
            UpdateStaged = true;
            StagedChanged?.Invoke();
        }
        catch { /* offline / transient; try again next launch */ }
    }

    /// <summary>Apply the staged update after this process exits (no restart). Call on quit.</summary>
    public void ApplyOnExit()
    {
        if (_staged == null) return;
        try { _mgr.WaitExitThenApplyUpdates(_staged); } catch { }
    }

    /// <summary>Apply the staged update now and relaunch.</summary>
    public void RestartToApply()
    {
        if (_staged == null) return;
        try { _mgr.ApplyUpdatesAndRestart(_staged); } catch { }
    }
}
