using WinAirPlay.Core;

namespace WinAirPlay;

internal sealed class SettingsForm : Form
{
    private readonly ComboBox inputs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown delay = new() { Minimum = 50, Maximum = 2000, Increment = 25, Width = 120 };
    private readonly CheckBox startMinimized = new() { Text = "Start minimized to the system tray", AutoSize = true };
    private readonly CheckBox minimizeOnClose = new() { Text = "Keep playing when closing the window", AutoSize = true };
    public string EndpointId => (inputs.SelectedItem as AudioEndpoint)?.Id ?? "";
    public int TargetMs => (int)delay.Value;
    public bool StartMinimized => startMinimized.Checked;
    public bool MinimizeOnClose => minimizeOnClose.Checked;

    public SettingsForm(AudioEndpoint[] devices, string endpointId, int targetMs, bool minimized, bool keepPlaying, bool audioEditable)
    {
        Text = "Settings — WinAirPlay";
        ClientSize = new Size(560, 330);
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 42, 42, 50, 38, 38, 40, 40 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Text = "Audio source", AutoSize = true }, 0, 0);
        layout.Controls.Add(inputs, 1, 0);
        inputs.Items.AddRange(devices);
        inputs.SelectedItem = devices.FirstOrDefault(device => device.Id == endpointId) ?? devices.FirstOrDefault();
        inputs.Enabled = audioEditable;
        layout.Controls.Add(new Label { Text = "Target latency (ms)", AutoSize = true }, 0, 1);
        layout.Controls.Add(delay, 1, 1);
        delay.Value = Math.Clamp(targetMs, 50, 2000); delay.Enabled = audioEditable;
        var hint = new Label { Text = audioEditable ? "Actual latency depends on the speaker." : "Disconnect all speakers to change the audio source or latency.", Dock = DockStyle.Fill };
        layout.Controls.Add(hint, 0, 2); layout.SetColumnSpan(hint, 2);
        startMinimized.Checked = minimized; minimizeOnClose.Checked = keepPlaying;
        layout.Controls.Add(startMinimized, 0, 3); layout.SetColumnSpan(startMinimized, 2);
        layout.Controls.Add(minimizeOnClose, 0, 4); layout.SetColumnSpan(minimizeOnClose, 2);
        var note = new Label { Text = "To exit: right-click the tray icon → Quit.", Dock = DockStyle.Fill };
        layout.Controls.Add(note, 0, 5); layout.SetColumnSpan(note, 2);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 6); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted; note.ForeColor = Theme.Muted;
        save.BackColor = Theme.Accent; save.ForeColor = Color.FromArgb(12, 25, 46);
    }
}
