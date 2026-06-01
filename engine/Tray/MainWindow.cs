using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Ducker.Config;

namespace Ducker.Tray;

/// <summary>
/// The main application window: a WebView2 hosting the local web UI (engine/ui), bridged to the
/// DetectionEngine over postMessage. Closing the window minimizes to the tray; the engine keeps
/// running. Live state is pushed to the UI on every engine StateChanged.
/// </summary>
public sealed class MainWindow : Form
{
    private readonly DetectionEngine _engine;
    private readonly WebView2 _web;
    private bool _ready;
    private bool _quitting;

    public MainWindow(DetectionEngine engine)
    {
        _engine = engine;
        Text = "Voxinator";
        ClientSize = new System.Drawing.Size(1000, 680);
        MinimumSize = new System.Drawing.Size(840, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(15, 17, 21);
        try { Icon = System.Drawing.SystemIcons.Application; } catch { }

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = System.Drawing.Color.FromArgb(15, 17, 21) };
        Controls.Add(_web);

        _engine.StateChanged += OnEngineStateChanged;
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
