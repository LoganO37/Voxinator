using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Ducker.Config;
using Ducker.Update;

namespace Ducker.Tray;

/// <summary>
/// The main application window: a WebView2 hosting the local web UI (engine/ui), bridged to the
/// DetectionEngine over postMessage. Closing the window minimizes to the tray; the engine keeps
/// running. Live state is pushed to the UI on every engine StateChanged.
/// </summary>
public sealed class MainWindow : Form
{
    private readonly DetectionEngine _engine;
    private readonly Updater _updater;
    private readonly WebView2 _web;
    private bool _ready;
    private bool _quitting;

    public MainWindow(DetectionEngine engine, Updater updater = null)
    {
        _engine = engine;
        _updater = updater;
        Text = "Voxinator";
        ClientSize = new System.Drawing.Size(1000, 680);
        MinimumSize = new System.Drawing.Size(840, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(15, 17, 21);
        try { Icon = AppIcon.Load(); } catch { }

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = System.Drawing.Color.FromArgb(15, 17, 21) };
        Controls.Add(_web);

        _engine.StateChanged += OnEngineStateChanged;
        if (_updater != null) _updater.StagedChanged += OnEngineStateChanged; // re-push when an update stages
        Load += async (_, __) => await InitAsync();
        FormClosing += OnFormClosing;
    }

    private async System.Threading.Tasks.Task InitAsync()
    {
        try
        {
            var dataDir = Path.Combine(EngineSettings.Dir, "WebView2");
            Directory.CreateDirectory(dataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir, null);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("voxinator.local",
                Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true; // useful while building the UI
            core.Navigate("https://voxinator.local/index.html");
            _ready = true;
        }
        catch (Exception ex)
        {
            TrayLogger.Log("WebView2 init failed: " + ex);
            MessageBox.Show(
                "Voxinator's window needs the Microsoft Edge WebView2 Runtime, which couldn't be loaded.\n\n" +
                "It ships with Windows 11 and recent Windows 10; if it's missing, install \"Evergreen WebView2 Runtime\" from Microsoft.\n\n" + ex.Message,
                "Voxinator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---- JS -> C# ----
    private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.TryGetWebMessageAsString(); } catch { return; }
        if (string.IsNullOrEmpty(json)) return;

        BridgeRequest req;
        try { req = JsonSerializer.Deserialize<BridgeRequest>(json); } catch { return; }
        if (req == null) return;

        object result = null;
        try { result = HandleCommand(req.cmd, req.args); }
        catch (Exception ex) { TrayLogger.Log($"bridge '{req.cmd}' error: {ex.Message}"); }

        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id = req.id, result })); }
        catch { }
    }

    private object HandleCommand(string cmd, JsonElement args)
    {
        switch (cmd)
        {
            case "snapshot": return Snapshot();
            case "games": return GamesInfo();
            case "setEnabled": _engine.SetEnabled(args.GetProperty("on").GetBoolean()); Persist(); return Snapshot();
            case "setAutoDetect": _engine.SetAutoDetect(args.GetProperty("on").GetBoolean()); Persist(); return Snapshot();
            case "saveSettings": return SaveSettings(args);
            case "addSource": AddSourceCmd(args); Persist(); return GamesInfo();
            case "removeSource": _engine.RemoveSource(args.GetProperty("name").GetString()); Persist(); return GamesInfo();
            case "websites": return WebsitesInfo();
            case "setDefaultAction": _engine.SetDefaultAction(args.GetProperty("action").GetString()); return WebsitesInfo();
            case "setDuckVolume": _engine.SetDuckVolume((float)args.GetProperty("value").GetDouble()); return WebsitesInfo();
            case "setRampMs": _engine.SetRampMs(args.GetProperty("value").GetInt32()); return WebsitesInfo();
            case "setSite": _engine.SetSite(args.GetProperty("host").GetString(), args.GetProperty("action").GetString()); return WebsitesInfo();
            case "removeSite": _engine.RemoveSite(args.GetProperty("host").GetString()); return WebsitesInfo();
            case "extensionInfo": return ExtensionInfo();
            case "checkUpdate": StartUpdateCheck(); return null;
            case "getExtension": return GetExtension();
            case "downloadZip": return DownloadZip();
            case "copyText": CopyText(args.GetProperty("text").GetString()); return null;
            case "openUrl": OpenUrl(args.GetProperty("url").GetString()); return null;
            case "seenWizard": _engine.Settings.ExtensionWizardSeen = true; Persist(); return null;
            case "setStartup": AutoStart.Set(args.GetProperty("on").GetBoolean()); return Snapshot();
            case "restartToUpdate": _updater?.RestartToApply(); return null;
            default: return null;
        }
    }

    private void AddSourceCmd(JsonElement a)
    {
        var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return;
        if (a.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number)
            _engine.AddSource((uint)p.GetInt64(), name);
        else
            _engine.AddSourceByName(name);
    }

    private object GamesInfo()
    {
        var monitored = _engine.SourceStates()
            .Select(x => new { name = x.name, capturing = x.capturing, active = x.active, auto = _engine.IsAutoSource(x.name) })
            .ToList();
        var monitoredNames = new HashSet<string>(monitored.Select(m => m.name), StringComparer.OrdinalIgnoreCase);
        var audioApps = ProcessList.AudioSessions().Select(a => new { pid = a.Pid, name = a.ProcessName }).ToList();
        var library = _engine.LibraryGames()
            .Select(g => new { process = g.process, title = g.title, monitored = monitoredNames.Contains(g.process) })
            .ToList();
        return new { autoDetect = _engine.Settings.AutoDetectGames, monitored, audioApps, library };
    }

    private object WebsitesInfo()
    {
        var s = _engine.Settings;
        var sites = (s.Sites ?? new List<SiteRule>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Host))
            .Select(r => new { host = r.Host, action = r.Action })
            .OrderBy(r => r.host, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new
        {
            defaultAction = s.DefaultAction,
            duckVolume = s.DuckVolume,
            rampMs = s.RampMs,
            extensionClients = _engine.ExtensionClients,
            sites,
        };
    }

    // ---- Get Extension wizard ----
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private const string RepoUrl = "https://github.com/LoganO37/Voxinator";
    private const string RemoteManifestUrl =
        "https://raw.githubusercontent.com/LoganO37/Voxinator/main/extension/manifest.json";

    private static string BundledExtensionDir => Path.Combine(AppContext.BaseDirectory, "extension");
    private static string InstalledExtensionDir => Path.Combine(EngineSettings.Dir, "extension");

    private object ExtensionInfo() => new
    {
        bundledVersion = ReadManifestVersion(Path.Combine(BundledExtensionDir, "manifest.json")) ?? "?",
        folderPath = InstalledExtensionDir,
        repoUrl = RepoUrl,
    };

    private static string ReadManifestVersion(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    // Best-effort GitHub check: compare the bundled extension version to the one on main, and
    // push the result to the UI as an event (the bridge call returns immediately).
    private void StartUpdateCheck()
    {
        var bundled = ReadManifestVersion(Path.Combine(BundledExtensionDir, "manifest.json"));
        _ = Task.Run(async () =>
        {
            string latest = null; bool available = false;
            try
            {
                var json = await Http.GetStringAsync(RemoteManifestUrl);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out var v)) latest = v.GetString();
                available = Version.TryParse(bundled, out var b) && Version.TryParse(latest, out var l) && l > b;
            }
            catch (Exception ex) { TrayLogger.Log("update check failed: " + ex.Message); }
            PostEvent("extUpdate", new { bundledVersion = bundled, latestVersion = latest, updateAvailable = available });
        });
    }

    // Copy the bundled extension into a stable per-user folder (survives app updates and is a
    // good fixed path for Chrome's "Load unpacked"), then reveal it in Explorer.
    private object GetExtension()
    {
        try
        {
            CopyDir(BundledExtensionDir, InstalledExtensionDir);
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{InstalledExtensionDir}\"") { UseShellExecute = true }); } catch { }
            return new { path = InstalledExtensionDir };
        }
        catch (Exception ex) { TrayLogger.Log("getExtension failed: " + ex.Message); return new { error = ex.Message }; }
    }

    private object DownloadZip()
    {
        try
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Save Voxinator extension",
                FileName = "voxinator-extension.zip",
                Filter = "Zip archive (*.zip)|*.zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return null;
            if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
            ZipFile.CreateFromDirectory(BundledExtensionDir, dlg.FileName);
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dlg.FileName}\"") { UseShellExecute = true }); } catch { }
            return new { path = dlg.FileName };
        }
        catch (Exception ex) { TrayLogger.Log("downloadZip failed: " + ex.Message); return new { error = ex.Message }; }
    }

    private void CopyText(string text)
    {
        try { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); } catch { }
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return; // only external https
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target, overwrite: true);
        }
    }

    private void PostEvent(string name, object data)
    {
        if (!_ready || IsDisposed) return;
        try { BeginInvoke(new Action(() => { try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = name, data })); } catch { } })); }
        catch { }
    }

    private void Persist()
    {
        try { _engine.Settings.Save(); } catch (Exception ex) { TrayLogger.Log("save failed: " + ex.Message); }
    }

    private object Snapshot()
    {
        var s = _engine.Settings;
        var sources = _engine.SourceStates()
            .Select(x => new { name = x.name, capturing = x.capturing, active = x.active, auto = _engine.IsAutoSource(x.name) })
            .ToList();
        return new
        {
            enabled = s.Enabled,
            autoDetect = s.AutoDetectGames,
            ducking = _engine.Ducking,
            status = _engine.StatusSummary(),
            extensionClients = _engine.ExtensionClients,
            firstRun = !s.ExtensionWizardSeen,
            appVersion = _updater?.CurrentVersion ?? "",
            updateStaged = _updater?.UpdateStaged ?? false,
            updateVersion = _updater?.StagedVersion ?? "",
            launchOnStartup = AutoStart.IsEnabled(),
            sources,
            settings = new { threshold = s.Threshold, minSpeechMs = s.MinSpeechMs, endBufferMs = s.EndBufferMs, port = s.Port, token = s.Token },
        };
    }

    private object SaveSettings(JsonElement a)
    {
        var s = _engine.Settings.Clone();
        if (a.TryGetProperty("threshold", out var t)) s.Threshold = (float)t.GetDouble();
        if (a.TryGetProperty("minSpeechMs", out var m)) s.MinSpeechMs = m.GetInt32();
        if (a.TryGetProperty("endBufferMs", out var e)) s.EndBufferMs = e.GetInt32();
        if (a.TryGetProperty("port", out var p)) s.Port = p.GetInt32();
        if (a.TryGetProperty("token", out var tk)) s.Token = tk.GetString();
        _engine.ApplySettings(s);
        try { s.Save(); } catch (Exception ex) { TrayLogger.Log("save failed: " + ex.Message); }
        return Snapshot();
    }

    // ---- C# -> JS ----
    private void OnEngineStateChanged()
    {
        if (!_ready || IsDisposed) return;
        try { BeginInvoke(new Action(PushState)); } catch { }
    }

    private void PushState()
    {
        if (!_ready) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = "state", data = Snapshot() })); }
        catch { }
    }

    // ---- window lifecycle ----
    public void ShowWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    public void QuitApp() { _quitting = true; Close(); }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (!_quitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // minimize to tray instead of exiting
            Hide();
        }
    }

    private sealed class BridgeRequest
    {
        public int id { get; set; }
        public string cmd { get; set; }
        public JsonElement args { get; set; }
    }
}
