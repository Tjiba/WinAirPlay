using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using WinAirPlay.Core;
using Microsoft.Win32;

namespace WinAirPlay;

internal sealed class MainForm : Form
{
    private readonly Settings settings = Settings.Load();
    private readonly Discovery discovery = new();
    private readonly ComboBox receivers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox inputs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown delay = new() { Minimum = 50, Maximum = 2000, Increment = 25, Dock = DockStyle.Left, Width = 120 };
    private readonly VolumeSlider volume = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Text = "Searching for speakers…", AutoSize = true, Dock = DockStyle.Fill };
    private readonly NotifyIcon tray = new() { Text = "WinAirPlay 2", Icon = SystemIcons.Application, Visible = true };
    private readonly List<Receiver> manualReceivers = [];
    private readonly FlowLayoutPanel cards = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
    private readonly TableLayoutPanel volumePanel = new() { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 3 };
    private readonly ToolTip hints = new();
    private Button? stopAll;
    private readonly Label source = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 400 };
    private readonly System.Windows.Forms.Timer recoveryTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer audioTimer = new() { Interval = 3000 };
    private sealed class Recovery(Receiver receiver)
    {
        public Receiver Receiver { get; set; } = receiver;
        public int Attempt { get; set; }
        public long? NextAttempt { get; set; }
    }
    private readonly Dictionary<string, Recovery> desired = [];
    private readonly Dictionary<string, (Receiver Receiver, string Message)> failures = [];
    private IReadOnlyList<Receiver> discovered = [];
    private bool suspended;
    private bool refreshingAudio;
    private string? defaultOutput;
    private int previousVolume = 50;
    private readonly Dictionary<string, int> previousReceiverVolumes = [];
    private EventHandler? volumeDisplayChanged;
    private sealed class Connection(Receiver receiver, AirPlaySession session)
    {
        public Receiver Receiver { get; } = receiver;
        public AirPlaySession Session { get; } = session;
        public bool Connecting { get; set; } = true;
        public bool Streaming { get; set; }
        public bool Disconnecting { get; set; }
        public bool Cancelled { get; set; }
        public bool RestartRequested { get; set; }
        public string? Failure { get; set; }
    }
    private readonly Dictionary<string, Connection> connections = [];
    private bool closing;
    private bool exitRequested;
    private readonly Channel<string> logQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false
    });
    private readonly Task logWriter;

    public MainForm()
    {
        logWriter = Task.Run(WriteLogs);
        Texts.Language = settings.Language;
        manualReceivers.AddRange(settings.ManualReceivers);
        receivers.Items.AddRange(manualReceivers.ToArray());
        saveTimer.Tick += (_, _) => SaveSettings();
        recoveryTimer.Tick += (_, _) => RetryConnections();
        audioTimer.Tick += async (_, _) => await RefreshAudioDevices();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        source.Click += (_, _) => OpenSourceMenu();
        source.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var outline = Theme.Rounded(new RectangleF(0, 0, source.Width - 1, source.Height - 1), 8 * DeviceDpi / 96f);
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawPath(pen, outline);
            var x = source.Width - 14 * DeviceDpi / 96f;
            var y = source.Height / 2f;
            e.Graphics.DrawLines(pen, [new PointF(x - 4, y - 2), new PointF(x, y + 2), new PointF(x + 4, y - 2)]);
        };
        cards.Resize += (_, _) => ResizeCards();
        cards.Layout += (_, _) => ResizeCards();
        Text = "WinAirPlay 2 — native audio";
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(400, 450);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        StartPosition = FormStartPosition.Manual;
        Load += (_, _) => { ResizeDashboard(); PositionPopup(); };
        DpiChanged += (_, _) => BeginInvoke(() => { ResizeDashboard(); ResizeCards(); });
        Font = new Font("Segoe UI", 10);
        DoubleBuffered = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        tray.Icon = Icon;
        BuildDashboard();
        delay.Value = Math.Clamp(settings.TargetMs, 50, 2000);
        volume.Value = Math.Clamp(settings.Volume, 0, 100);
        volume.ValueChanged += (_, _) =>
        {
            if (settings.SharedVolume)
                foreach (var connection in connections.Values) connection.Session.Volume = volume.Value;
            QueueSave();
        };
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), BackColor = Theme.Background, ForeColor = Theme.Text };
        menu.Opening += (_, _) => PopulateMenu(menu);
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPopup(); };
        status.TextChanged += (_, _) => { tray.Text = ("WinAirPlay — " + status.Text)[..Math.Min(63, 13 + status.Text.Length)]; hints.SetToolTip(status, status.Text); RefreshCards(); };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        discovery.Changed += found => Ui(() => UpdateReceivers(found));
        discovery.Error += error => Log("Discovery: " + error);
        Shown += async (_, _) =>
        {
            Log("WinAirPlay 2.0 — C# / WASAPI C++ / AirPlay direct");
            try
            {
                await RefreshAudioDevices();
                if (IsDisposed || closing) return;
                recoveryTimer.Start(); audioTimer.Start();
                discovery.Start();
                if (settings.StartMinimized) BeginInvoke(() => Hide());
            }
            catch (Exception error) { Log(error.Message); status.Text = "Initialization failed: " + error.Message; }
        };
        FormClosing += OnClosing;
        FormClosed += (_, _) =>
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            saveTimer.Dispose(); recoveryTimer.Dispose(); audioTimer.Dispose();
            discovery.Dispose(); tray.Dispose(); menu.Dispose(); hints.Dispose();
            logQueue.Writer.TryComplete(); logWriter.Wait(1000);
        };
    }

    private void BuildDashboard()
    {
        layout.Padding = new Padding(24, 20, 24, 20);
        foreach (var height in new[] { 92, 104, 0, 80, 60 })
            layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
        var header = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        var mark = new Panel();
        mark.Paint += (_, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; StudioButton.DrawSymbol(e.Graphics, mark.ClientRectangle, "airplay", Theme.Text); };
        var heading = new Label { Text = "WinAirPlay", Font = new Font("Segoe UI Semibold", 22), AutoSize = true };
        var subtitle = new Label { Text = Texts.Get("Your speakers, automatically."), Font = new Font("Segoe UI", 10), ForeColor = Theme.Muted, AutoEllipsis = true };
        var preferences = new StudioButton { Symbol = "settings", Borderless = true, AccessibleName = Texts.Get("Settings") };
        preferences.Click += (_, _) => OpenSettings(); hints.SetToolTip(preferences, Texts.Get("Settings"));
        var hide = new StudioButton { Symbol = "close", Borderless = true, AccessibleName = Texts.Get("Close window") };
        hide.Click += (_, _) => Close(); hints.SetToolTip(hide, Texts.Get("Close window; Esc always hides it"));
        header.Controls.AddRange([mark, heading, subtitle, preferences, hide]);
        header.Layout += (_, _) =>
        {
            int Px(int value) => (int)(value * DeviceDpi / 96f);
            mark.SetBounds(0, Px(16), Px(32), Px(32));
            heading.Location = new Point(Px(44), Px(10));
            preferences.SetBounds(header.Width - Px(80), Px(12), Px(36), Px(36));
            hide.SetBounds(header.Width - Px(36), Px(12), Px(36), Px(36));
            subtitle.SetBounds(0, Px(58), header.Width, Px(24));
        };
        header.Paint += (_, e) => { using var pen = new Pen(Theme.Border); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };
        layout.Controls.Add(header, 0, 0);
        status.Font = new Font("Segoe UI", 8); status.AutoEllipsis = true;
        status.AutoSize = false; status.TextAlign = ContentAlignment.MiddleLeft; status.Margin = new Padding(0);
        var summary = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = new Padding(0) };
        summary.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        summary.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        summary.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var sourceRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102 * DeviceDpi / 96f));
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceRow.Controls.Add(new Label { Text = Texts.Get("Audio source"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) }, 0, 0);
        source.Font = new Font("Segoe UI", 9); source.Cursor = Cursors.Hand;
        source.Margin = new Padding(0, 8, 0, 8); source.Padding = new Padding(12, 0, 24, 0);
        sourceRow.Controls.Add(source, 1, 0);
        summary.Controls.Add(sourceRow, 0, 0);
        var section = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40)); section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        section.Controls.Add(new Label { Text = Texts.Get("Speakers"), Font = new Font("Segoe UI Semibold", 13), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) }, 0, 0);
        var discoveryLabel = new Label { Text = Texts.Get("Automatic discovery"), Font = new Font("Segoe UI", 9), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0), AutoEllipsis = true };
        section.Controls.Add(discoveryLabel, 1, 0); summary.Controls.Add(section, 0, 1);
        summary.Controls.Add(status, 0, 2); layout.Controls.Add(summary, 0, 1);
        cards.Margin = new Padding(0, 8, 0, 0); layout.Controls.Add(cards, 0, 2);
        volumePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        volumePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        volumePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        volumePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); volumePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        volumePanel.Controls.Add(new Label { Text = Texts.Get("Shared volume"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) }, 0, 0);
        var mute = new StudioButton { Symbol = "mute", Borderless = true, Dock = DockStyle.Fill, Margin = new Padding(0), AccessibleName = Texts.Get("Mute") };
        mute.Click += (_, _) => { if (volume.Value > 0) { previousVolume = volume.Value; volume.Value = 0; } else volume.Value = previousVolume; };
        volumePanel.Controls.Add(mute, 1, 0);
        var volumeLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0) };
        if (volumeDisplayChanged is not null) volume.ValueChanged -= volumeDisplayChanged;
        volumeDisplayChanged = (_, _) =>
        {
            volumeLabel.Text = $"{volume.Value} %";
            mute.Symbol = volume.Value == 0 ? "sound" : "mute";
            mute.AccessibleName = Texts.Get(volume.Value == 0 ? "Unmute" : "Mute");
            hints.SetToolTip(mute, mute.AccessibleName); mute.Invalidate();
        };
        volume.ValueChanged += volumeDisplayChanged;
        volumePanel.Controls.Add(volumeLabel, 2, 0);
        volumePanel.Margin = new Padding(0, 8, 0, 0); volume.Margin = new Padding(0);
        volume.AccessibleName = Texts.Get("All speakers volume");
        volumePanel.Controls.Add(volume, 0, 1); volumePanel.SetColumnSpan(volume, 3);
        layout.Controls.Add(volumePanel, 0, 3);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 12, 0, 0) };
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var (title, symbol, action) in new (string, string, Action)[] { ("Refresh", "refresh", RefreshDevices), ("Stop all", "stop", async () => await DisconnectAll()) })
        {
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var button = new StudioButton { Text = Texts.Get(title), Symbol = symbol, Filled = title == "Stop all", Dock = DockStyle.Fill, Margin = new Padding(4), Font = new Font("Segoe UI", 10) };
            button.Click += (_, _) => action(); footer.Controls.Add(button);
            if (title == "Stop all") { stopAll = button; stopAll.Enabled = false; }
        }
        layout.Controls.Add(footer, 0, 4); Controls.Add(layout);
        Theme.Apply(layout);
        status.ForeColor = subtitle.ForeColor = Theme.Muted; discoveryLabel.ForeColor = Theme.Accent;
        source.BackColor = Theme.Surface;
        volumeDisplayChanged?.Invoke(this, EventArgs.Empty);
        RebuildCards();
    }
    private void OpenSourceMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), BackColor = Theme.Background, ForeColor = Theme.Text };
        var editable = connections.Count == 0 && desired.Count == 0;
        foreach (var input in inputs.Items.Cast<AudioEndpoint>())
        {
            var item = new ToolStripMenuItem(input.Id.Length == 0 ? Texts.Get("Default Windows output") : input.Name)
            { Checked = (inputs.SelectedItem as AudioEndpoint)?.Id == input.Id, Enabled = editable };
            item.Click += (_, _) => { inputs.SelectedItem = input; SaveSettings(); UpdateSource(); };
            menu.Items.Add(item);
        }
        if (!editable) menu.Items.Add(new ToolStripMenuItem(Texts.Get("Stop all speakers to change source")) { Enabled = false });
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(source, new Point(0, source.Height));
    }

    private void OpenLog()
    {
        if (File.Exists(Settings.LogPath)) Process.Start(new ProcessStartInfo(Settings.LogPath) { UseShellExecute = true });
    }
    private void ShowPopup()
    {
        PositionPopup();
        RestoreWindow();
    }
    private void PositionPopup()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - Width - 12), Math.Max(area.Top, area.Bottom - Height - 12));
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WindowsIntegration.ShowMessage) ShowPopup();
        base.WndProc(ref message);
    }
    private void ResizeCards()
    {
        var scrollWidth = cards.Controls.Cast<Control>().Sum(card => card.Height + card.Margin.Vertical) > cards.Height
            ? SystemInformation.VerticalScrollBarWidth : 0;
        var width = Math.Max(100, cards.Width - scrollWidth);
        foreach (Control card in cards.Controls)
            if (card.Width != width) card.Width = width;
    }
    private void ResizeDashboard()
    {
        var scale = DeviceDpi / 96f;
        layout.Padding = new Padding((int)(24 * scale), (int)(20 * scale), (int)(24 * scale), (int)(20 * scale));
        layout.RowStyles[0].Height = 92 * scale;
        layout.RowStyles[1].Height = 104 * scale;
        layout.RowStyles[4].Height = 60 * scale;
        volumePanel.RowStyles[0].Height = 36 * scale;
        volumePanel.ColumnStyles[1].Width = 40 * scale;
        volumePanel.ColumnStyles[2].Width = 58 * scale;
        if (layout.GetControlFromPosition(0, 1) is TableLayoutPanel summary)
        {
            summary.RowStyles[0].Height = 52 * scale; summary.RowStyles[1].Height = 32 * scale;
            if (summary.GetControlFromPosition(0, 0) is TableLayoutPanel sourceRow) sourceRow.ColumnStyles[0].Width = 102 * scale;
        }
        volumePanel.Visible = settings.SharedVolume;
        layout.RowStyles[3].Height = settings.SharedVolume ? 80 * scale : 0;
        var count = AvailableReceivers().Count;
        var height = (304 + (settings.SharedVolume ? 80 : 0) + (count == 0 ? 120 : count * (settings.SharedVolume ? 116 : 156))) * scale;
        var area = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(Math.Min((int)(460 * scale), area.Width - 24), Math.Min((int)Math.Min(height, 650 * scale), area.Height - 24));
        using var outline = Theme.Rounded(new RectangleF(0, 0, Width, Height), 24 * scale);
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
        if (Visible) Location = new Point(Math.Max(area.Left, area.Right - Width - 12), Math.Max(area.Top, area.Bottom - Height - 12));
    }
    private void RebuildCards()
    {
        cards.SuspendLayout();
        while (cards.Controls.Count > 0) cards.Controls[0].Dispose();
        var items = AvailableReceivers();
        foreach (var receiver in items)
        {
            var card = new ReceiverCard(receiver, !settings.SharedVolume, ReceiverVolume(receiver), previousReceiverVolumes.GetValueOrDefault(receiver.Id, 50));
            card.Selected += async selected => await ToggleConnection(selected);
            card.VolumeChanged += SetReceiverVolume;
            card.DetailsRequested += selected =>
            {
                var message = failures.TryGetValue(selected.Id, out var failure) ? failure.Message : selected.Host + ":" + selected.Port;
                MessageBox.Show(this, message, selected.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            card.EditRequested += EditReceiver;
            card.RemoveRequested += RemoveReceiver;
            card.Manual = manualReceivers.Any(item => item.Id == receiver.Id);
            cards.Controls.Add(card);
        }
        if (items.Count == 0) cards.Controls.Add(new Label { Text = Texts.Get("No speakers found yet.\n\nConnect your PC and speakers to the same network. Manual IP entry is available in Settings."), ForeColor = Theme.Muted, Height = (int)(120 * DeviceDpi / 96f), Margin = new Padding(0), Padding = new Padding(0, 12, 0, 0) });
        ResizeDashboard(); ResizeCards(); RefreshCards(); cards.ResumeLayout(); ResizeCards();
    }
    private int ReceiverVolume(Receiver receiver) => settings.SharedVolume ? volume.Value
        : Math.Clamp(settings.ReceiverVolumes.GetValueOrDefault(receiver.Id, volume.Value), 0, 100);

    private void SetReceiverVolume(Receiver receiver, int value)
    {
        if (settings.SharedVolume) return;
        value = Math.Clamp(value, 0, 100);
        var previous = ReceiverVolume(receiver);
        if (previous > 0) previousReceiverVolumes[receiver.Id] = previous;
        settings.ReceiverVolumes[receiver.Id] = value;
        if (connections.TryGetValue(ReceiverKey(receiver), out var connection)) connection.Session.Volume = value;
        QueueSave();
    }

    private void SetVolumeMode(bool shared)
    {
        if (settings.SharedVolume == shared) return;
        if (!shared)
            foreach (var receiver in AvailableReceivers()) settings.ReceiverVolumes.TryAdd(receiver.Id, volume.Value);
        settings.SharedVolume = shared;
        foreach (var connection in connections.Values) connection.Session.Volume = ReceiverVolume(connection.Receiver);
        RebuildCards();
        SaveSettings();
    }
    private void RefreshCards()
    {
        if (stopAll is not null) stopAll.Enabled = (connections.Count > 0 || desired.Count > 0) && !closing;
        foreach (var card in cards.Controls.OfType<ReceiverCard>())
        {
            var connection = connections.Values.FirstOrDefault(item => item.Receiver.Id == card.Receiver.Id);
            desired.TryGetValue(card.Receiver.Id, out var recovery);
            failures.TryGetValue(card.Receiver.Id, out var failure);
            var seconds = recovery?.NextAttempt is long next ? Math.Max(1, (int)Math.Ceiling((next - Environment.TickCount64) / 1000.0)) : 0;
            card.SetState(connection?.Streaming == true, connection?.Connecting == true,
                closing || connection?.Disconnecting == true || connection?.Cancelled == true,
                failure.Message, seconds, suspended && recovery is not null);
        }
    }

    private void PopulateMenu(ContextMenuStrip menu)
    {
        while (menu.Items.Count > 0) { var item = menu.Items[0]; menu.Items.RemoveAt(0); item.Dispose(); }
        menu.Items.Add(new ToolStripMenuItem("WinAirPlay") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem(status.Text) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        var disconnect = new ToolStripMenuItem(Texts.Get("Disconnect all")) { Enabled = (connections.Count > 0 || desired.Count > 0) && !closing };
        disconnect.Click += async (_, _) => await DisconnectAll();
        menu.Items.Add(disconnect);
        var devices = new ToolStripMenuItem(Texts.Get("Speakers")) { Enabled = !closing };
        foreach (var receiver in AvailableReceivers())
        {
            connections.TryGetValue(ReceiverKey(receiver), out var connection);
            var item = new ToolStripMenuItem(receiver.Name + (connection?.Connecting == true ? Texts.Get(" — Cancel connection") : ""))
            {
                Checked = connection is not null || desired.ContainsKey(receiver.Id), ToolTipText = receiver.Host,
                Enabled = connection?.Disconnecting != true && connection?.Cancelled != true
            };
            item.Click += async (_, _) => await ToggleConnection(receiver);
            devices.DropDownItems.Add(item);
        }
        if (devices.DropDownItems.Count == 0) devices.DropDownItems.Add(new ToolStripMenuItem(Texts.Get("Searching…")) { Enabled = false });
        devices.DropDownItems.Add(new ToolStripSeparator());
        devices.DropDownItems.Add(Texts.Get("Add IP address…"), null, (_, _) => { RestoreWindow(); AddReceiver(); });
        menu.Items.Add(devices);
        var levels = new ToolStripMenuItem(settings.SharedVolume ? $"Volume: {volume.Value} %" : Texts.Get("Individual volumes…"));
        if (!settings.SharedVolume) levels.Click += (_, _) => ShowPopup();
        foreach (var value in new[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 })
        {
            if (!settings.SharedVolume) break;
            var item = new ToolStripMenuItem(value == 0 ? Texts.Get("Mute") : $"{value} %") { Checked = volume.Value == value };
            item.Click += (_, _) => volume.Value = value;
            levels.DropDownItems.Add(item);
        }
        menu.Items.Add(levels);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Texts.Get("Settings…"), null, (_, _) => OpenSettings());
        menu.Items.Add(Texts.Get("Open window"), null, (_, _) => RestoreWindow());
        menu.Items.Add(Texts.Get("Open log"), null, (_, _) =>
        {
            if (File.Exists(Settings.LogPath)) Process.Start(new ProcessStartInfo(Settings.LogPath) { UseShellExecute = true });
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Texts.Get("Quit"), null, (_, _) => { exitRequested = true; Close(); });
    }

    private void QueueSave() { saveTimer.Stop(); saveTimer.Start(); }

    private void SaveSettings()
    {
        saveTimer.Stop();
        settings.ManualReceivers = manualReceivers.ToList();
        settings.EndpointId = (inputs.SelectedItem as AudioEndpoint)?.Id ?? settings.EndpointId;
        settings.ReceiverId = (receivers.SelectedItem as Receiver)?.Id ?? settings.ReceiverId;
        settings.TargetMs = (int)delay.Value;
        settings.Volume = volume.Value;
        try { settings.Save(); } catch (Exception error) { Log("Settings : " + error.Message); }
    }

    private async void OpenSettings()
    {
        await RefreshAudioDevices();
        if (closing || IsDisposed) return;
        using var dialog = new SettingsForm(inputs.Items.Cast<AudioEndpoint>().ToArray(),
            (inputs.SelectedItem as AudioEndpoint)?.Id ?? "", (int)delay.Value,
            settings.StartMinimized, settings.MinimizeOnClose, connections.Count == 0 && desired.Count == 0, settings.SharedVolume)
        { AutoReconnect = settings.AutoReconnect, FollowDefaultOutput = settings.FollowDefaultOutput, Language = settings.Language };
        dialog.AddSpeakerRequested += owner => EditReceiverDialog(null, owner);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (connections.Count == 0 && desired.Count == 0)
        {
            inputs.SelectedItem = inputs.Items.Cast<AudioEndpoint>().FirstOrDefault(input => input.Id == dialog.EndpointId);
            delay.Value = dialog.TargetMs;
        }
        settings.AutoReconnect = dialog.AutoReconnect;
        settings.FollowDefaultOutput = dialog.FollowDefaultOutput;
        if (!settings.AutoReconnect)
            foreach (var id in desired.Where(pair => pair.Value.NextAttempt is not null).Select(pair => pair.Key).ToArray()) desired.Remove(id);
        var languageChanged = settings.Language != dialog.Language;
        settings.Language = dialog.Language;
        Texts.Language = settings.Language;
        settings.StartMinimized = dialog.StartMinimized;
        settings.MinimizeOnClose = dialog.MinimizeOnClose;
        SetVolumeMode(dialog.SharedVolume);
        SaveSettings();
        if (languageChanged)
        {
            status.Parent?.Controls.Remove(status); source.Parent?.Controls.Remove(source);
            while (layout.Controls.Count > 0)
            {
                var control = layout.Controls[0];
                if (control == cards || control == volumePanel) layout.Controls.Remove(control);
                else
                {
                    control.Dispose();
                }
            }
            volume.Parent?.Controls.Remove(volume);
            while (volumePanel.Controls.Count > 0) volumePanel.Controls[0].Dispose();
            volumePanel.RowStyles.Clear(); volumePanel.ColumnStyles.Clear(); layout.RowStyles.Clear();
            BuildDashboard();
        }
        UpdateSource(); UpdateStatus(); RebuildCards();
    }

    private void RetryConnections()
    {
        if (closing || suspended) return;
        foreach (var recovery in desired.Values.ToArray())
        {
            if (recovery.NextAttempt is not long next || next > Environment.TickCount64) continue;
            if (connections.Values.Any(item => item.Receiver.Id == recovery.Receiver.Id)) continue;
            recovery.NextAttempt = null;
            _ = ConnectReceiver(recovery.Receiver);
        }
        RefreshCards();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) => Ui(async () => await HandlePowerMode(e.Mode));

    private async Task HandlePowerMode(PowerModes mode)
    {
        if (mode == PowerModes.Suspend)
        {
            suspended = true;
            await RestartConnections(Texts.Get("Windows suspended playback."));
        }
        if (mode != PowerModes.Resume) return;
        suspended = false;
        discovery.Refresh();
        await RefreshAudioDevices();
        RetryConnections();
    }

    private async Task RestartConnections(string reason, bool force = false)
    {
        foreach (var connection in connections.Values.ToArray())
        {
            connection.Failure = reason;
            connection.RestartRequested = force;
            if (connection.Connecting) connection.Session.Cancel();
            await Disconnect(connection);
        }
    }

    private async void RefreshDevices()
    {
        discovery.Refresh();
        await RefreshAudioDevices();
    }

    private async Task RefreshAudioDevices()
    {
        if (refreshingAudio || closing || suspended || IsDisposed) return;
        refreshingAudio = true;
        try
        {
            var snapshot = await Task.Run(() => (Devices: NativeAudio.Devices(), Default: NativeAudio.DefaultDeviceId()));
            if (closing || suspended || IsDisposed) return;
            var selected = (inputs.SelectedItem as AudioEndpoint)?.Id ?? settings.EndpointId;
            if (!inputs.Items.Cast<AudioEndpoint>().SequenceEqual(snapshot.Devices))
            {
                inputs.Items.Clear(); inputs.Items.AddRange(snapshot.Devices.ToArray());
                if (!snapshot.Devices.Any(item => item.Id == selected)) inputs.Items.Add(new AudioEndpoint(selected, Texts.Get("Unavailable output")));
                inputs.SelectedItem = inputs.Items.Cast<AudioEndpoint>().FirstOrDefault(item => item.Id == selected);
            }
            var changed = defaultOutput is not null && defaultOutput != snapshot.Default;
            defaultOutput = snapshot.Default;
            UpdateSource();
            if (changed && selected.Length == 0 && settings.FollowDefaultOutput)
                await RestartConnections(Texts.Get("Default Windows output changed."), true);
        }
        catch (Exception error) { Log("Audio outputs: " + error.Message); }
        finally { refreshingAudio = false; }
    }

    private void UpdateSource()
    {
        var selected = inputs.SelectedItem as AudioEndpoint;
        var name = selected?.Id == "" ? inputs.Items.Cast<AudioEndpoint>().FirstOrDefault(item => item.Id == defaultOutput)?.Name ?? Texts.Get("Default Windows output") : selected?.Name;
        source.Text = name ?? Texts.Get("No audio output");
        hints.SetToolTip(source, source.Text);
    }

    private void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; BringToFront(); WindowsIntegration.SetForegroundWindow(Handle); Activate(); }
    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }
    private void Log(string message)
    {
        logQueue.Writer.TryWrite($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
    }
    private async Task WriteLogs()
    {
        await foreach (var line in logQueue.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                if (File.Exists(Settings.LogPath) && new FileInfo(Settings.LogPath).Length > 2_000_000)
                    File.Move(Settings.LogPath, Settings.LogPath + ".old", true);
                await File.AppendAllTextAsync(Settings.LogPath, line + Environment.NewLine);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    private void UpdateReceivers(IReadOnlyList<Receiver> found)
    {
        discovered = found;
        foreach (var receiver in found)
            if (desired.TryGetValue(receiver.Id, out var recovery)) recovery.Receiver = receiver;
        var previous = (receivers.SelectedItem as Receiver)?.Id ?? settings.ReceiverId;
        var items = manualReceivers.Select(manual => manual with
        {
            Model = found.FirstOrDefault(item => item.Host == manual.Host && item.Port == manual.Port)?.Model ?? manual.Model
        }).Concat(found).GroupBy(receiver => receiver.Host + ":" + receiver.Port).Select(group => group.First()).ToArray();
        if (receivers.Items.Cast<Receiver>().SequenceEqual(items)) return;
        Log("Speakers found: " + string.Join(" | ", items.Select(item => item.ToString())));
        receivers.BeginUpdate();
        receivers.Items.Clear();
        receivers.Items.AddRange(items);
        receivers.SelectedItem = items.FirstOrDefault(receiver => receiver.Id == previous) ?? items.FirstOrDefault();
        receivers.EndUpdate();
        RebuildCards();
        UpdateStatus();
    }
    private void AddReceiver() => EditReceiver(null);

    private void EditReceiver(Receiver? existing) => EditReceiverDialog(existing, this);

    private void EditReceiverDialog(Receiver? existing, IWin32Window owner)
    {
        if (existing is not null && (connections.Values.Any(item => item.Receiver.Id == existing.Id) || desired.ContainsKey(existing.Id))) return;
        using var dialog = new Form { Text = Texts.Get(existing is null ? "Add an AirPlay speaker" : "Edit speaker"), AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(440, 260), Font = Font, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        foreach (var height in new[] { 26, 40, 26, 40, 34, 54 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var host = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "e.g. 192.168.1.15", AccessibleName = Texts.Get("IP address") };
        var port = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 65535, Value = 7000, AccessibleName = "Port" };
        layout.Controls.Add(new Label { Text = Texts.Get("IP address"), AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = "Port", AutoSize = true }, 1, 0);
        layout.Controls.Add(host, 0, 1); layout.Controls.Add(port, 1, 1);
        var name = new TextBox { Dock = DockStyle.Fill, Text = existing?.Name ?? "AirPlay", AccessibleName = Texts.Get("Name"), MaxLength = 80 };
        host.Text = existing?.Host ?? ""; port.Value = existing?.Port ?? 7000;
        layout.Controls.Add(new Label { Text = Texts.Get("Name"), AutoSize = true }, 0, 2);
        layout.Controls.Add(name, 0, 3); layout.SetColumnSpan(name, 2);
        var error = new Label { Dock = DockStyle.Fill, AutoEllipsis = true };
        layout.Controls.Add(error, 0, 4); layout.SetColumnSpan(error, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
        var button = new Button { Text = Texts.Get(existing is null ? "Add" : "Save"), Size = new Size(120, 38), Margin = new Padding(8, 8, 0, 8) };
        var cancel = new Button { Text = Texts.Get("Cancel"), Size = new Size(120, 38), Margin = new Padding(8, 8, 0, 8), DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(button); buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 5); layout.SetColumnSpan(buttons, 2);
        button.Click += (_, _) =>
        {
            if (!IPAddress.TryParse(host.Text, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            { error.Text = Texts.Get("Enter a valid IPv4 address."); host.Focus(); return; }
            if (AvailableReceivers().Any(receiver => receiver.Id != existing?.Id && receiver.Host == address.ToString() && receiver.Port == (int)port.Value))
            { error.Text = Texts.Get("This speaker is already in the list."); return; }
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = Texts.Get("Enter a speaker name."); name.Focus(); return; }
            var item = new Receiver(existing?.Id ?? Guid.NewGuid().ToString(), name.Text.Trim(), address.ToString(), (int)port.Value);
            if (existing is not null) { manualReceivers.RemoveAll(receiver => receiver.Id == existing.Id); failures.Remove(existing.Id); }
            manualReceivers.Add(item);
            UpdateReceivers(discovered); SaveSettings();
            dialog.DialogResult = DialogResult.OK;
        };
        dialog.Controls.Add(layout); dialog.AcceptButton = button; dialog.CancelButton = cancel; Theme.Apply(dialog);
        error.ForeColor = Theme.Error; button.BackColor = Theme.Accent; button.ForeColor = Theme.Background;
        dialog.ShowDialog(owner);
    }
    private void RemoveReceiver(Receiver receiver)
    {
        if (connections.Values.Any(item => item.Receiver.Id == receiver.Id) || desired.ContainsKey(receiver.Id)) return;
        manualReceivers.RemoveAll(item => item.Id == receiver.Id);
        failures.Remove(receiver.Id); settings.ReceiverVolumes.Remove(receiver.Id);
        UpdateReceivers(discovered); SaveSettings();
    }

    private static string ReceiverKey(Receiver receiver) => receiver.Host + ":" + receiver.Port;
    private List<Receiver> AvailableReceivers() => receivers.Items.Cast<Receiver>()
        .Concat(connections.Values.Select(connection => connection.Receiver))
        .Concat(desired.Values.Select(item => item.Receiver)).Concat(failures.Values.Select(item => item.Receiver))
        .GroupBy(receiver => receiver.Id).Select(group => group.First())
        .GroupBy(ReceiverKey).Select(group => group.First()).ToList();

    private void UpdateStatus(string? error = null)
    {
        var playing = connections.Values.Where(connection => connection.Streaming && !connection.Disconnecting).ToArray();
        var pending = connections.Values.Count(connection => connection.Connecting);
        var retrying = desired.Values.Count(item => item.NextAttempt is not null);
        var text = playing.Length == 1 ? string.Format(Texts.Get("Streaming to {0}"), playing[0].Receiver.Name)
            : playing.Length > 1 ? string.Format(Texts.Get("Streaming to {0} speakers"), playing.Length)
            : retrying > 0 ? "Reconnecting…"
            : pending > 0 ? "Connecting…"
            : connections.Count > 0 ? "Disconnecting…"
            : receivers.Items.Count == 0 ? "Searching for speakers…" : "Ready to connect";
        text = Texts.Get(text);
        if (playing.Length > 0 && pending > 0) text += string.Format(Texts.Get(" · {0} connecting"), pending);
        status.Text = error is null ? text : text + " · " + error;
        inputs.Enabled = delay.Enabled = connections.Count == 0;
        RefreshCards();
    }

    private async Task ToggleConnection(Receiver receiver)
    {
        if (closing) return;
        var key = ReceiverKey(receiver);
        var existing = connections.Values.FirstOrDefault(item => item.Receiver.Id == receiver.Id || ReceiverKey(item.Receiver) == key);
        if (existing is not null || desired.ContainsKey(receiver.Id))
        {
            desired.Remove(receiver.Id);
            if (existing is not null) { desired.Remove(existing.Receiver.Id); await Disconnect(existing); }
            RebuildCards(); UpdateStatus();
            return;
        }
        failures.Remove(receiver.Id);
        desired[receiver.Id] = new Recovery(receiver);
        await ConnectReceiver(receiver);
    }

    private async Task ConnectReceiver(Receiver receiver)
    {
        if (closing || suspended) return;
        var key = ReceiverKey(receiver);
        if (connections.ContainsKey(key) || connections.Values.Any(item => item.Receiver.Id == receiver.Id)) return;
        if (inputs.SelectedItem is not AudioEndpoint input)
        { desired.Remove(receiver.Id); status.Text = Texts.Get("Choose an audio output in Settings."); return; }
        receivers.SelectedItem = receiver;
        SaveSettings();
        var session = new AirPlaySession(receiver, input.Id, (int)delay.Value, ReceiverVolume(receiver),
            message => Log($"[{receiver.Name} · {receiver.Host}] {message}"));
        var current = new Connection(receiver, session);
        connections.Add(key, current);
        UpdateStatus();
        session.Streaming += () => Ui(() =>
        {
            if (!connections.TryGetValue(key, out var active) || active != current || current.Disconnecting || current.Cancelled) return;
            current.Streaming = true;
            failures.Remove(receiver.Id);
            if (desired.TryGetValue(receiver.Id, out var recovery)) recovery.Attempt = 0;
            UpdateStatus();
        });
        session.Failed += error => Ui(async () =>
        {
            if (!connections.TryGetValue(key, out var active) || active != current || current.Disconnecting) return;
            current.Failure = error;
            if (!current.Connecting) await Disconnect(current);
        });
        try
        {
            await Task.Run(() => session.Connect());
        }
        catch (Exception error)
        {
            if (!current.Cancelled)
            {
                current.Failure = error.Message;
                if (error is System.Security.Cryptography.CryptographicException or InvalidDataException || error.Message.Contains("Pairing rejected") || error.Message.Contains("/pair-setup:")) desired.Remove(receiver.Id);
                Log($"[{receiver.Name}] Connection: {error.Message}");
            }
        }
        finally
        {
            current.Connecting = false;
            if (current.Failure is not null || current.Cancelled || closing) await Disconnect(current);
            else UpdateStatus();
        }
    }

    private async Task Disconnect(Connection connection)
    {
        if (connection.Disconnecting) return;
        if (connection.Connecting)
        {
            connection.Cancelled = true;
            connection.Session.Cancel();
            UpdateStatus();
            return;
        }
        connection.Disconnecting = true;
        UpdateStatus();
        try { await Task.Run(connection.Session.Dispose); }
        finally
        {
            connections.Remove(ReceiverKey(connection.Receiver));
            if (connection.Failure is not null) failures[connection.Receiver.Id] = (connection.Receiver, connection.Failure);
            if (desired.TryGetValue(connection.Receiver.Id, out var recovery))
            {
                if (!closing && (settings.AutoReconnect || connection.RestartRequested))
                {
                    recovery.Attempt = Math.Min(recovery.Attempt + 1, 5);
                    recovery.NextAttempt = Environment.TickCount64 + Math.Min(30000, 1000 * (1 << recovery.Attempt));
                }
                else desired.Remove(connection.Receiver.Id);
            }
            RebuildCards();
            UpdateStatus(connection.Failure is null ? null : connection.Receiver.Name + ": " + connection.Failure);
            if (closing && connections.Count == 0) Close();
        }
    }
    private async Task DisconnectAll()
    {
        desired.Clear();
        await Task.WhenAll(connections.Values.ToArray().Select(Disconnect));
        RebuildCards(); UpdateStatus();
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        SaveSettings();
        if (!exitRequested && !closing && settings.MinimizeOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; Hide(); return;
        }
        desired.Clear();
        if (connections.Count == 0) return;
        e.Cancel = true; closing = true;
        await DisconnectAll();
    }
}
