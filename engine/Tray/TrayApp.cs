using System.Windows.Forms;
using Ducker.Config;

namespace Ducker.Tray;

/// <summary>
/// System-tray front end for the DetectionEngine. The "Sources" submenu lets you check
/// multiple processes (a game, Discord, etc.) to monitor at once — media ducks when any of
/// them has speech. A hidden anchor form marshals engine events onto the UI thread; the
/// engine owns PID re-resolution, so the tray has no watchdog of its own.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly DetectionEngine _engine;
    private readonly NotifyIcon _icon;
    private readonly Form _anchor;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _sourcesItem;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _autoDetectItem;

    public TrayApp(string modelPath)
    {
        _anchor = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Visible = false };
        _ = _anchor.Handle; // force handle creation for cross-thread marshaling

        var settings = EngineSettings.Load();
        _engine = new DetectionEngine(modelPath, settings) { Log = TrayLogger.Log };
        _engine.StateChanged += () => OnUi(UpdateUi);

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _sourcesItem = new ToolStripMenuItem("Sources (games / apps)");
        _enabledItem = new ToolStripMenuItem("Enabled") { CheckOnClick = true, Checked = settings.Enabled };
        _enabledItem.Click += (_, __) => { _engine.SetEnabled(_enabledItem.Checked); Persist(); };
        _autoDetectItem = new ToolStripMenuItem("Auto-detect games") { CheckOnClick = true, Checked = settings.AutoDetectGames };
        _autoDetectItem.Click += (_, __) => { _engine.SetAutoDetect(_autoDetectItem.Checked); Persist(); };

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, __) => OpenSettings();
        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, __) => Quit();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_sourcesItem);
        menu.Items.Add(_autoDetectItem);
        menu.Items.Add(_enabledItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);
        menu.Opening += (_, __) => PopulateSourcesMenu();

        _icon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "Voxinator",
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) menu.Show(Cursor.Position); };

        _engine.Start(); // resolve saved sources by name and begin capturing
        UpdateUi();
        TrayLogger.Log("tray started");
    }

    private void OnUi(Action a)
    {
        try { if (_anchor.IsHandleCreated && !_anchor.IsDisposed) _anchor.BeginInvoke(a); }
        catch { /* shutting down */ }
    }

    private void PopulateSourcesMenu()
    {
        _sourcesItem.DropDownItems.Clear();
        var sessions = ProcessList.AudioSessions().ToList();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in sessions)
        {
            listed.Add(s.ProcessName);
            AddSourceMenuItem(s.ProcessName, (uint)s.Pid, $"{s.ProcessName} (pid {s.Pid}) — {s.State}");
        }

        // Show already-selected sources that aren't currently playing audio, so they can be unchecked.
        foreach (var src in _engine.Settings.Sources)
            if (!listed.Contains(src.ProcessName))
                AddSourceMenuItem(src.ProcessName, src.Pid ?? 0, $"{src.ProcessName} — (selected, not playing audio)");

        if (_sourcesItem.DropDownItems.Count == 0)
            _sourcesItem.DropDownItems.Add(new ToolStripMenuItem("(no apps playing audio)") { Enabled = false });

        _sourcesItem.DropDownItems.Add(new ToolStripSeparator());
        var clear = new ToolStripMenuItem("Clear all sources");
        clear.Click += (_, __) => { _engine.ClearSources(); Persist(); };
        _sourcesItem.DropDownItems.Add(clear);
    }

    private void AddSourceMenuItem(string name, uint pid, string label)
    {
        bool selected = _engine.HasSource(name);
        if (!selected && _engine.IsAutoSource(name)) label += "  •auto";
        var item = new ToolStripMenuItem(label) { Checked = selected };
        item.Click += (_, __) =>
        {
            if (_engine.HasSource(name)) _engine.RemoveSource(name);
            else _engine.AddSource(pid, name);
            Persist();
        };
        _sourcesItem.DropDownItems.Add(item);
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_engine.Settings.Clone());
        if (form.ShowDialog() == DialogResult.OK && form.Result != null)
        {
            _engine.ApplySettings(form.Result); // Result keeps current Sources + Enabled
            try { form.Result.Save(); } catch (Exception ex) { TrayLogger.Log($"save failed: {ex.Message}"); }
        }
    }

    private void UpdateUi()
    {
        var status = _engine.StatusSummary();
        _statusItem.Text = $"Status: {status}";
        _enabledItem.Checked = _engine.Settings.Enabled;
        _autoDetectItem.Checked = _engine.Settings.AutoDetectGames;
        var text = $"Voxinator — {status}";
        _icon.Text = text.Length > 63 ? text.Substring(0, 63) : text; // NotifyIcon.Text caps at 63 chars
    }

    private void Persist()
    {
        try { _engine.Settings.Save(); } catch (Exception ex) { TrayLogger.Log($"save failed: {ex.Message}"); }
    }

    private void Quit()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _engine.Dispose();
        _anchor.Dispose();
        ExitThread();
    }
}
