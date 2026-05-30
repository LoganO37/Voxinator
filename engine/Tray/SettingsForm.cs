using System.Windows.Forms;
using Ducker.Config;

namespace Ducker.Tray;

/// <summary>Compact settings dialog for the engine's detection + connection parameters.
/// Per-site action / ramp settings live in the browser extension, not here.</summary>
public sealed class SettingsForm : Form
{
    private readonly NumericUpDown _port = Num(1, 65535, 1, 0);
    private readonly TextBox _token = new() { Width = 160 };
    private readonly NumericUpDown _threshold = Num(0, 1, 0.05m, 2);
    private readonly NumericUpDown _minSpeech = Num(0, 5000, 50, 0);
    private readonly NumericUpDown _endBuffer = Num(0, 10000, 100, 0);

    public EngineSettings Result { get; private set; }

    public SettingsForm(EngineSettings s)
    {
        Text = "Voxinator — Engine Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _port.Value = Clamp(s.Port, 1, 65535);
        _token.Text = s.Token ?? "";
        _threshold.Value = (decimal)Math.Clamp(s.Threshold, 0f, 1f);
        _minSpeech.Value = Clamp(s.MinSpeechMs, 0, 5000);
        _endBuffer.Value = Clamp(s.EndBufferMs, 0, 10000);

        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        AddRow(table, "WebSocket port", _port);
        AddRow(table, "Token", _token);
        AddRow(table, "Detection threshold (0–1)", _threshold);
        AddRow(table, "Attack / min speech (ms)", _minSpeech);
        AddRow(table, "End buffer (ms)", _endBuffer);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
        ok.Click += (_, __) =>
        {
            Result = s.Clone();
            Result.Port = (int)_port.Value;
            Result.Token = string.IsNullOrWhiteSpace(_token.Text) ? "changeme" : _token.Text.Trim();
            Result.Threshold = (float)_threshold.Value;
            Result.MinSpeechMs = (int)_minSpeech.Value;
            Result.EndBufferMs = (int)_endBuffer.Value;
        };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        var root = new TableLayoutPanel { RowCount = 2, ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(table, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static NumericUpDown Num(decimal min, decimal max, decimal inc, int decimals) =>
        new() { Minimum = min, Maximum = max, Increment = inc, DecimalPlaces = decimals, Width = 100 };

    private static decimal Clamp(int v, int lo, int hi) => Math.Clamp(v, lo, hi);

    private static void AddRow(TableLayoutPanel t, string label, Control control)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 6) });
        t.Controls.Add(control);
    }
}
