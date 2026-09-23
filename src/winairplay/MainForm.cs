using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using WinAirPlay.Core;

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
    private readonly TableLayoutPanel volumePanel = new() { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2 };
    private readonly ToolTip hints = new();
    private Button? stopAll;
    private sealed class Connection(Receiver receiver, AirPlaySession session)
    {
        public Receiver Receiver { get; } = receiver;
        public AirPlaySession Session { get; } = session;
        public bool Connecting { get; set; } = true;
        public bool Streaming { get; set; }
        public bool Disconnecting { get; set; }
        public bool Cancelled { get; set; }
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
        Text = "WinAirPlay 2 — native audio";
        ClientSize = new Size(370, 450);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        StartPosition = FormStartPosition.Manual;
        Load += (_, _) => { ResizeDashboard(); PositionPopup(); };
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
            SaveSettings();
        };
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), BackColor = Theme.Background, ForeColor = Theme.Text };
        menu.Opening += (_, _) => PopulateMenu(menu);
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPopup(); };
        status.TextChanged += (_, _) => { tray.Text = ("WinAirPlay — " + status.Text)[..Math.Min(63, 13 + status.Text.Length)]; hints.SetToolTip(status, status.Text); RefreshCards(); };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        discovery.Changed += found => Ui(() => UpdateReceivers(found));
        discovery.Error += error => Log("Discovery: " + error);
        Shown += (_, _) =>
        {
            Log("WinAirPlay 2.0 — C# / WASAPI C++ / AirPlay direct");
            try
            {
                foreach (var input in NativeAudio.Devices()) inputs.Items.Add(input);
                inputs.SelectedItem = inputs.Items.Cast<AudioEndpoint>().FirstOrDefault(input => input.Id == settings.EndpointId) ?? inputs.Items.Cast<AudioEndpoint>().FirstOrDefault();
                discovery.Start();
                if (settings.StartMinimized) BeginInvoke(() => Hide());
            }
            catch (Exception error) { Log(error.Message); status.Text = "Initialization failed: " + error.Message; }
        };
        FormClosing += OnClosing;
        FormClosed += (_, _) =>
        {
            discovery.Dispose(); tray.Dispose(); menu.Dispose(); hints.Dispose();
            logQueue.Writer.TryComplete(); logWriter.Wait(1000);
        };
    }

    private void BuildDashboard()
    {
        layout.Padding = new Padding(22, 16, 22, 12);
        foreach (var height in new[] { 58, 28, 0, 70, 44 })
            layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
        var header = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        var heading = new Label { Text = "WinAirPlay", Font = new Font("Segoe UI Semibold", 18), AutoSize = true, Location = new Point(0, 7) };
        header.Controls.Add(heading);
        var hide = new Button { Text = "×", Width = 32, Height = 32, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Muted, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 14) };
        hide.FlatAppearance.BorderSize = 0; hide.Click += (_, _) => Hide(); header.Controls.Add(hide);
        hide.AccessibleName = "Hide window";
        hints.SetToolTip(hide, "Hide window (Esc) — audio keeps playing");
        header.Resize += (_, _) => hide.Location = new Point(header.ClientSize.Width - hide.Width, 7);
        layout.Controls.Add(header, 0, 0);
        status.ForeColor = Theme.Muted; status.Font = new Font("Segoe UI", 9); status.AutoEllipsis = true;
        status.AutoSize = false; status.TextAlign = ContentAlignment.MiddleLeft;
        status.Margin = new Padding(0);
        layout.Controls.Add(status, 0, 1);
        cards.Margin = new Padding(0);
        cards.Resize += (_, _) => ResizeCards();
        cards.Layout += (_, _) => ResizeCards();
        layout.Controls.Add(cards, 0, 2);
        volumePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); volumePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        volumePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); volumePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        volumePanel.Controls.Add(new Label { Text = "Volume", AutoSize = true, ForeColor = Theme.Muted }, 0, 0);
        var volumeLabel = new Label { Text = $"{settings.Volume} %", Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopRight, ForeColor = Theme.Muted, Margin = new Padding(0) };
        volume.ValueChanged += (_, _) => volumeLabel.Text = $"{volume.Value} %";
        volumePanel.Controls.Add(volumeLabel, 1, 0);
        volumePanel.Margin = new Padding(0, 8, 0, 0);
        volume.Margin = new Padding(0);
        volume.AccessibleName = "All speakers volume";
        volumePanel.Controls.Add(volume, 0, 1); volumePanel.SetColumnSpan(volume, 2);
        layout.Controls.Add(volumePanel, 0, 3);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 8, 0, 0) };
        foreach (var (title, action) in new (string, Action)[] { ("Settings", OpenSettings), ("Add", AddReceiver), ("Stop all", async () => await DisconnectAll()) })
        {
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            var button = new Button { Text = title, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Muted, Cursor = Cursors.Hand, Margin = new Padding(0), Font = new Font("Segoe UI", 9) };
            button.FlatAppearance.BorderSize = 0; button.FlatAppearance.MouseOverBackColor = Theme.Surface;
            button.Click += (_, _) => action(); footer.Controls.Add(button);
            if (title == "Stop all")
            {
                stopAll = button; stopAll.Enabled = false;
                stopAll.Paint += (_, e) =>
                {
                    if (button.Enabled) return;
                    e.Graphics.Clear(Theme.Background);
                    TextRenderer.DrawText(e.Graphics, button.Text, button.Font, button.ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                };
            }
        }
        layout.Controls.Add(footer, 0, 4); Controls.Add(layout);
        RebuildCards();
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
        volumePanel.Visible = settings.SharedVolume;
        layout.RowStyles[3].Height = settings.SharedVolume ? 70 * scale : 0;
        var count = AvailableReceivers().Count;
        var height = (158 + (settings.SharedVolume ? 70 : 0) + (count == 0 ? 120 : count * (settings.SharedVolume ? 72 : 108))) * scale;
        var area = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(Math.Min((int)(370 * scale), area.Width - 24), Math.Min((int)height, area.Height - 24));
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
            var card = new ReceiverCard(receiver, !settings.SharedVolume, ReceiverVolume(receiver));
            card.Selected += async selected => await ToggleConnection(selected);
            card.VolumeChanged += SetReceiverVolume;
            cards.Controls.Add(card);
        }
        if (items.Count == 0) cards.Controls.Add(new Label { Text = "No speakers found yet.\n\nConnect your PC and HomePods to the same network, or add an IP address below.", ForeColor = Theme.Muted, Height = (int)(120 * DeviceDpi / 96f), Margin = new Padding(0), Padding = new Padding(0, 12, 0, 0) });
        ResizeDashboard(); ResizeCards(); RefreshCards(); cards.ResumeLayout(); ResizeCards();
    }
    private int ReceiverVolume(Receiver receiver) => settings.SharedVolume ? volume.Value
        : Math.Clamp(settings.ReceiverVolumes.GetValueOrDefault(receiver.Id, volume.Value), 0, 100);

    private void SetReceiverVolume(Receiver receiver, int value)
    {
        if (settings.SharedVolume) return;
        value = Math.Clamp(value, 0, 100);
        settings.ReceiverVolumes[receiver.Id] = value;
        if (connections.TryGetValue(ReceiverKey(receiver), out var connection)) connection.Session.Volume = value;
        SaveSettings();
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
        if (stopAll is not null) stopAll.Enabled = connections.Count > 0 && !closing;
        foreach (var card in cards.Controls.OfType<ReceiverCard>())
        {
            connections.TryGetValue(ReceiverKey(card.Receiver), out var connection);
            card.SetState(connection?.Streaming == true, connection?.Connecting == true,
                closing || connection?.Disconnecting == true || connection?.Cancelled == true);
        }
    }

    private void PopulateMenu(ContextMenuStrip menu)
    {
        while (menu.Items.Count > 0) { var item = menu.Items[0]; menu.Items.RemoveAt(0); item.Dispose(); }
        menu.Items.Add(new ToolStripMenuItem("WinAirPlay") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem(status.Text) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        var disconnect = new ToolStripMenuItem("Disconnect all") { Enabled = connections.Count > 0 && !closing };
        disconnect.Click += async (_, _) => await DisconnectAll();
        menu.Items.Add(disconnect);
        var devices = new ToolStripMenuItem("Speakers") { Enabled = !closing };
        foreach (var receiver in AvailableReceivers())
        {
            connections.TryGetValue(ReceiverKey(receiver), out var connection);
            var item = new ToolStripMenuItem(receiver.Name + (connection?.Connecting == true ? " — Cancel connection" : ""))
            {
                Checked = connection is not null, ToolTipText = receiver.Host,
                Enabled = connection?.Disconnecting != true && connection?.Cancelled != true
            };
            item.Click += async (_, _) => await ToggleConnection(receiver);
            devices.DropDownItems.Add(item);
        }
        if (devices.DropDownItems.Count == 0) devices.DropDownItems.Add(new ToolStripMenuItem("Searching…") { Enabled = false });
        devices.DropDownItems.Add(new ToolStripSeparator());
        devices.DropDownItems.Add("Add IP address…", null, (_, _) => { RestoreWindow(); AddReceiver(); });
        menu.Items.Add(devices);
        var levels = new ToolStripMenuItem(settings.SharedVolume ? $"Volume: {volume.Value} %" : "Individual volumes…");
        if (!settings.SharedVolume) levels.Click += (_, _) => ShowPopup();
        foreach (var value in new[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 })
        {
            if (!settings.SharedVolume) break;
            var item = new ToolStripMenuItem(value == 0 ? "Mute" : $"{value} %") { Checked = volume.Value == value };
            item.Click += (_, _) => volume.Value = value;
            levels.DropDownItems.Add(item);
        }
        menu.Items.Add(levels);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Open window", null, (_, _) => RestoreWindow());
        menu.Items.Add("Open log", null, (_, _) =>
        {
            if (File.Exists(Settings.LogPath)) Process.Start(new ProcessStartInfo(Settings.LogPath) { UseShellExecute = true });
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { exitRequested = true; Close(); });
    }

    private void SaveSettings()
    {
        settings.EndpointId = (inputs.SelectedItem as AudioEndpoint)?.Id ?? settings.EndpointId;
        settings.ReceiverId = (receivers.SelectedItem as Receiver)?.Id ?? settings.ReceiverId;
        settings.TargetMs = (int)delay.Value;
        settings.Volume = volume.Value;
        try { settings.Save(); } catch (Exception error) { Log("Settings : " + error.Message); }
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(inputs.Items.Cast<AudioEndpoint>().ToArray(),
            (inputs.SelectedItem as AudioEndpoint)?.Id ?? "", (int)delay.Value,
            settings.StartMinimized, settings.MinimizeOnClose, connections.Count == 0, settings.SharedVolume);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (connections.Count == 0)
        {
            inputs.SelectedItem = inputs.Items.Cast<AudioEndpoint>().FirstOrDefault(input => input.Id == dialog.EndpointId);
            delay.Value = dialog.TargetMs;
        }
        settings.StartMinimized = dialog.StartMinimized;
        settings.MinimizeOnClose = dialog.MinimizeOnClose;
        SetVolumeMode(dialog.SharedVolume);
        SaveSettings();
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
        var previous = (receivers.SelectedItem as Receiver)?.Id ?? settings.ReceiverId;
        var items = found.Concat(manualReceivers).GroupBy(receiver => receiver.Host + ":" + receiver.Port).Select(group => group.First()).ToArray();
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
    private void AddReceiver()
    {
        using var dialog = new Form { Text = "Add an AirPlay speaker", ClientSize = new Size(400, 180), Font = Font, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        foreach (var height in new[] { 26, 40, 30, 44 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var host = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "e.g. 192.168.1.15", AccessibleName = "IP address" };
        var port = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 65535, Value = 7000, AccessibleName = "Port" };
        layout.Controls.Add(new Label { Text = "IP address", AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = "Port", AutoSize = true }, 1, 0);
        layout.Controls.Add(host, 0, 1); layout.Controls.Add(port, 1, 1);
        var error = new Label { Dock = DockStyle.Fill, AutoEllipsis = true };
        layout.Controls.Add(error, 0, 2); layout.SetColumnSpan(error, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0) };
        var button = new Button { Text = "Add", Size = new Size(92, 34) };
        var cancel = new Button { Text = "Cancel", Size = new Size(92, 34), DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(button); buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 3); layout.SetColumnSpan(buttons, 2);
        button.Click += (_, _) =>
        {
            if (!IPAddress.TryParse(host.Text, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            { error.Text = "Enter a valid IPv4 address."; host.Focus(); return; }
            if (AvailableReceivers().Any(receiver => receiver.Host == address.ToString() && receiver.Port == (int)port.Value))
            { error.Text = "This speaker is already in the list."; return; }
            var item = new Receiver(address + ":" + port.Value, "AirPlay", address.ToString(), (int)port.Value);
            manualReceivers.Add(item);
            receivers.Items.Add(item); receivers.SelectedItem = item; RebuildCards();
            dialog.DialogResult = DialogResult.OK;
        };
        dialog.Controls.Add(layout); dialog.AcceptButton = button; dialog.CancelButton = cancel; Theme.Apply(dialog);
        error.ForeColor = Theme.Muted; button.BackColor = Theme.Accent; button.ForeColor = Theme.Background;
        dialog.ShowDialog(this);
    }
    private static string ReceiverKey(Receiver receiver) => receiver.Host + ":" + receiver.Port;
    private List<Receiver> AvailableReceivers() => receivers.Items.Cast<Receiver>()
        .Concat(connections.Values.Select(connection => connection.Receiver))
        .GroupBy(ReceiverKey).Select(group => group.First()).ToList();

    private void UpdateStatus(string? error = null)
    {
        var playing = connections.Values.Where(connection => connection.Streaming && !connection.Disconnecting).ToArray();
        var pending = connections.Values.Count(connection => connection.Connecting);
        var text = playing.Length == 1 ? "Streaming to " + playing[0].Receiver.Name
            : playing.Length > 1 ? $"Streaming to {playing.Length} speakers"
            : pending > 0 ? "Connecting…"
            : connections.Count > 0 ? "Disconnecting…"
            : receivers.Items.Count == 0 ? "Searching for speakers…" : "Ready to connect";
        if (playing.Length > 0 && pending > 0) text += $" · {pending} connecting";
        status.Text = error is null ? text : text + " · " + error;
        inputs.Enabled = delay.Enabled = connections.Count == 0;
        RefreshCards();
    }

    private async Task ToggleConnection(Receiver receiver)
    {
        if (closing) return;
        var key = ReceiverKey(receiver);
        if (connections.TryGetValue(key, out var existing))
        {
            await Disconnect(existing);
            return;
        }
        if (inputs.SelectedItem is not AudioEndpoint input)
        { status.Text = "Choose an audio output in Settings."; return; }
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
            RebuildCards();
            UpdateStatus(connection.Failure is null ? null : connection.Receiver.Name + ": " + connection.Failure);
            if (closing && connections.Count == 0) Close();
        }
    }
    private async Task DisconnectAll()
    {
        await Task.WhenAll(connections.Values.ToArray().Select(Disconnect));
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        SaveSettings();
        if (!exitRequested && !closing && settings.MinimizeOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; Hide(); return;
        }
        if (connections.Count == 0) return;
        e.Cancel = true; closing = true;
        await DisconnectAll();
    }
}
