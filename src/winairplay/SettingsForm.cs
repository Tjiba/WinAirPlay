using WinAirPlay.Core;

namespace WinAirPlay;

internal sealed class SettingsForm : Form
{
    private readonly ComboBox inputs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown delay = new() { Minimum = 50, Maximum = 2000, Increment = 25, Width = 120 };
    private readonly CheckBox startMinimized = new() { Text = "Start minimized to the system tray", AutoSize = true };
    private readonly CheckBox startAtLogin = new() { Text = "Launch at Windows startup", AutoSize = true };
    private readonly CheckBox sharedVolume = new() { Text = "Use the same volume for all speakers", AutoSize = true };
    private readonly CheckBox minimizeOnClose = new() { Text = "Keep playing when closing the window", AutoSize = true };
    private readonly CheckBox reconnect = new() { Text = "Reconnect after network loss or sleep", AutoSize = true };
    private readonly CheckBox followDefault = new() { Text = "Follow the default Windows output", AutoSize = true };
    private readonly ComboBox language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    public bool AutoReconnect { get => reconnect.Checked; set => reconnect.Checked = value; }
    public bool FollowDefaultOutput { get => followDefault.Checked; set => followDefault.Checked = value; }
    public string Language { get => language.SelectedIndex == 1 ? "fr" : "en"; set => language.SelectedIndex = value == "fr" ? 1 : 0; }
    public string EndpointId => (inputs.SelectedItem as AudioEndpoint)?.Id ?? "";
    public int TargetMs => (int)delay.Value;
    public bool StartMinimized => startMinimized.Checked;
    public bool MinimizeOnClose => minimizeOnClose.Checked;
    public bool SharedVolume => sharedVolume.Checked;
    public event Action<IWin32Window>? AddSpeakerRequested;

    public SettingsForm(AudioEndpoint[] devices, string endpointId, int targetMs, bool minimized, bool keepPlaying, bool audioEditable, bool sameVolume)
    {
        Text = "Settings — WinAirPlay";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(580, 600);
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 13 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 42, 42, 50, 38, 38, 38, 38, 56, 38, 38, 46, 48, 48 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Text = "Audio source", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(inputs, 1, 0);
        inputs.Items.AddRange(devices.Select(device => device.Id.Length == 0 ? device with { Name = Texts.Get("Default Windows output") } : device).ToArray());
        inputs.SelectedItem = inputs.Items.Cast<AudioEndpoint>().FirstOrDefault(device => device.Id == endpointId) ?? inputs.Items.Cast<AudioEndpoint>().FirstOrDefault();
        inputs.Enabled = audioEditable && devices.Length > 0;
        layout.Controls.Add(new Label { Text = "Target latency (ms)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(delay, 1, 1);
        delay.Value = Math.Clamp(targetMs, 50, 2000); delay.Enabled = audioEditable;
        var hint = new Label { Text = devices.Length == 0 ? "No audio output found. Connect one, then reopen Settings." : audioEditable ? "Actual latency depends on the speaker." : "Disconnect all speakers to change the audio source or latency.", Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
        layout.Controls.Add(hint, 0, 2); layout.SetColumnSpan(hint, 2);
        startMinimized.Checked = minimized; minimizeOnClose.Checked = keepPlaying;
        layout.Controls.Add(startMinimized, 0, 3); layout.SetColumnSpan(startMinimized, 2);
        layout.Controls.Add(minimizeOnClose, 0, 4); layout.SetColumnSpan(minimizeOnClose, 2);
        startAtLogin.Checked = WindowsIntegration.StartAtLogin;
        layout.Controls.Add(startAtLogin, 0, 5); layout.SetColumnSpan(startAtLogin, 2);
        var note = new Label { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
        sharedVolume.Checked = sameVolume;
        void UpdateVolumeHint() => note.Text = Texts.Get(sharedVolume.Checked
            ? "One slider controls every speaker.\nIndividual levels are kept for later."
            : "Each speaker has its own slider.\nLevels are saved automatically.");
        sharedVolume.CheckedChanged += (_, _) => UpdateVolumeHint();
        UpdateVolumeHint();
        layout.Controls.Add(sharedVolume, 0, 6); layout.SetColumnSpan(sharedVolume, 2);
        layout.Controls.Add(note, 0, 7); layout.SetColumnSpan(note, 2);
        reconnect.Checked = true; followDefault.Checked = true;
        layout.Controls.Add(reconnect, 0, 8); layout.SetColumnSpan(reconnect, 2);
        layout.Controls.Add(followDefault, 0, 9); layout.SetColumnSpan(followDefault, 2);
        layout.Controls.Add(new Label { Text = "Language", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 10);
        language.Items.AddRange(["English", "Français"]); language.SelectedIndex = Texts.Language == "fr" ? 1 : 0;
        layout.Controls.Add(language, 1, 10);
        inputs.AccessibleName = Texts.Get("Audio source"); delay.AccessibleName = Texts.Get("Target latency (ms)");
        language.AccessibleName = Texts.Get("Language");
        var addSpeaker = new Button { Text = "Add speaker by IP…", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8) };
        addSpeaker.Click += (_, _) => AddSpeakerRequested?.Invoke(this);
        layout.Controls.Add(addSpeaker, 0, 11); layout.SetColumnSpan(addSpeaker, 2);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        var save = new Button { Text = "Save", Size = new Size(120, 38), Margin = new Padding(8, 5, 0, 5), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Size = new Size(120, 38), Margin = new Padding(8, 5, 0, 5), DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            try { WindowsIntegration.StartAtLogin = startAtLogin.Checked; }
            catch (Exception error)
            {
                DialogResult = DialogResult.None;
                MessageBox.Show(this, Texts.Get("Could not update Windows startup: ") + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 12); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
        Theme.Apply(this);
        hint.ForeColor = Theme.Muted; note.ForeColor = Theme.Muted;
        save.BackColor = Theme.Accent; save.ForeColor = Theme.Background;
    }
}
