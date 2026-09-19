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
    private readonly Label deviceCount = new() { AutoSize = true, ForeColor = Theme.Muted };
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
        ClientSize = new Size(480, 660);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        tray.Icon = Icon;
        BuildDashboard();
        delay.Value = Math.Clamp(settings.TargetMs, 50, 2000);
        volume.Value = Math.Clamp(settings.Volume, 0, 100);
        volume.ValueChanged += (_, _) =>
        {
            foreach (var connection in connections.Values) connection.Session.Volume = volume.Value;
            SaveSettings();
        };
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), BackColor = Theme.Background, ForeColor = Theme.Text };
        menu.Opening += (_, _) => PopulateMenu(menu);
        tray.ContextMenuStrip = menu;
        // MouseClick suppresses the second click of a double-click; MouseDown does not.
        tray.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { if (Visible) Hide(); else ShowPopup(); } };
        status.TextChanged += (_, _) => { tray.Text = ("WinAirPlay — " + status.Text)[..Math.Min(63, 13 + status.Text.Length)]; RefreshCards(); };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        discovery.Changed += found => Ui(() => UpdateReceivers(found));
        discovery.Error += error => Log("Discovery: " + error);
        Shown += (_, _) =>
        {
            Log("WinAirPlay 2.0 — C# / WASAPI C++ / AirPlay direct");
            try
            {
                foreach (var input in NativeAudio.Devices()) inputs.Items.Add(input);
                inputs.SelectedItem = inputs.Items.Cast<AudioEndpoint>().FirstOrDefault(input => input.Id == settings.EndpointId) ?? inputs.Items[0];
                discovery.Start();
                if (settings.StartMinimized) BeginInvoke(() => Hide());
            }
            catch (Exception error) { Log(error.Message); status.Text = "Initialization failed: " + error.Message; }
        };
        FormClosing += OnClosing;
        FormClosed += (_, _) =>
        {
            discovery.Dispose(); tray.Dispose(); menu.Dispose();
            logQueue.Writer.TryComplete(); logWriter.Wait(1000);
        };
    }

    private void BuildDashboard()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 7 };
        foreach (var height in new[] { 84, 42, 32, 0, 30, 76, 42 })
            layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
        var header = new Panel { Dock = DockStyle.Fill };
        var heading = new Label { Text = "WinAirPlay", Font = new Font("Segoe UI Semibold", 21), AutoSize = true, Location = new Point(0, 0) };
        var subtitle = new Label { Text = "YOUR PC AUDIO, EVERY ROOM", ForeColor = Theme.Muted, Font = new Font("Segoe UI", 8), AutoSize = true };
        header.Controls.Add(heading); header.Controls.Add(subtitle);
        header.Layout += (_, _) => subtitle.Location = new Point(2, heading.Bottom + 4);
        var hide = new Button { Text = "×", Width = 34, Height = 34, Location = new Point(390, 2), Anchor = AnchorStyles.Top | AnchorStyles.Right, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Muted, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 15) };
        hide.FlatAppearance.BorderSize = 0; hide.Click += (_, _) => Hide(); header.Controls.Add(hide);
        header.Resize += (_, _) => hide.Location = new Point(header.ClientSize.Width - hide.Width, 2);
        layout.Controls.Add(header, 0, 0);
        status.ForeColor = Theme.Muted; status.Font = new Font("Segoe UI", 9); status.AutoEllipsis = true;
        status.AutoSize = false; status.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(status, 0, 1);
        deviceCount.Text = "YOUR SPEAKERS"; deviceCount.Font = new Font("Segoe UI Semibold", 9);
        layout.Controls.Add(deviceCount, 0, 2);
        cards.Resize += (_, _) => ResizeCards();
        layout.Controls.Add(cards, 0, 3);
        var add = new LinkLabel { Text = "+ Add IP address", AutoSize = true, LinkColor = Theme.Muted, ActiveLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 5, 0, 0) };
        add.LinkClicked += (_, _) => AddReceiver(); layout.Controls.Add(add, 0, 4);
        var volumePanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2 };
        volumePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); volumePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        volumePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); volumePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        volumePanel.Controls.Add(new Label { Text = "All speakers volume", AutoSize = true, ForeColor = Theme.Muted }, 0, 0);
        var volumeLabel = new Label { Text = "50 %", AutoSize = true, ForeColor = Theme.Text };
        volume.ValueChanged += (_, _) => volumeLabel.Text = $"{volume.Value} %";
        volumePanel.Controls.Add(volumeLabel, 1, 0);
        volume.BackColor = Theme.Background;
        volumePanel.Controls.Add(volume, 0, 1); volumePanel.SetColumnSpan(volume, 2);
        layout.Controls.Add(volumePanel, 0, 5);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        foreach (var (title, action) in new (string, Action)[] { ("Settings", OpenSettings), ("Logs", OpenLog), ("Quit", () => { exitRequested = true; Close(); }) })
        {
            var button = new Button { Text = title, Width = 128, Height = 34, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Muted, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 14, 0) };
            button.FlatAppearance.BorderColor = Theme.Border; button.FlatAppearance.MouseOverBackColor = Theme.Surface;
            button.Click += (_, _) => action(); footer.Controls.Add(button);
        }
        layout.Controls.Add(footer, 0, 6); Controls.Add(layout);
        RebuildCards();
    }

    private void OpenLog()
    {
        if (File.Exists(Settings.LogPath)) Process.Start(new ProcessStartInfo(Settings.LogPath) { UseShellExecute = true });
    }
    private void ShowPopup()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - Width - 12), Math.Max(area.Top, area.Bottom - Height - 12));
        RestoreWindow();
    }
    private void ResizeCards()
    {
        foreach (Control card in cards.Controls) card.Width = Math.Max(100, cards.ClientSize.Width - 22);
    }
    private void RebuildCards()
    {
        cards.SuspendLayout();
        while (cards.Controls.Count > 0) cards.Controls[0].Dispose();
        var items = AvailableReceivers();
        deviceCount.Text = items.Count == 0 ? "YOUR SPEAKERS" : $"YOUR SPEAKERS  ·  {items.Count}";
        foreach (var receiver in items)
        {
            var card = new ReceiverCard(receiver);
            card.Selected += async selected => await ToggleConnection(selected);
            cards.Controls.Add(card);
        }
        if (items.Count == 0) cards.Controls.Add(new Label { Text = "Searching for AirPlay speakers…\n\nConnect your PC and speakers to the same network.", ForeColor = Theme.Muted, Height = 110, Padding = new Padding(14) });
        ResizeCards(); RefreshCards(); cards.ResumeLayout();
    }
    private void RefreshCards()
    {
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
        var levels = new ToolStripMenuItem($"Volume: {volume.Value} %");
        foreach (var value in new[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 })
        {
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
            settings.StartMinimized, settings.MinimizeOnClose, connections.Count == 0);
        if (dialog.ShowDialog() != DialogResult.OK) return;
        if (connections.Count == 0)
        {
            inputs.SelectedItem = inputs.Items.Cast<AudioEndpoint>().FirstOrDefault(input => input.Id == dialog.EndpointId);
            delay.Value = dialog.TargetMs;
        }
        settings.StartMinimized = dialog.StartMinimized;
        settings.MinimizeOnClose = dialog.MinimizeOnClose;
        SaveSettings();
    }

    private void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
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
        using var dialog = new Form { Text = "AirPlay speaker", ClientSize = new Size(360, 125), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var host = new TextBox { Left = 15, Top = 15, Width = 230, PlaceholderText = "IPv4 address" };
        var port = new NumericUpDown { Left = 255, Top = 15, Width = 85, Minimum = 1, Maximum = 65535, Value = 7000 };
        var button = new Button { Text = "Add", Left = 240, Top = 65, Width = 100 };
        button.Click += (_, _) =>
        {
            if (!IPAddress.TryParse(host.Text, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            { MessageBox.Show(dialog, "Enter a valid IPv4 address."); return; }
            var item = new Receiver(address + ":" + port.Value, "AirPlay", address.ToString(), (int)port.Value);
            manualReceivers.Add(item);
            receivers.Items.Add(item); receivers.SelectedItem = item; RebuildCards();
            dialog.DialogResult = DialogResult.OK;
        };
        dialog.Controls.AddRange([host, port, button]); dialog.AcceptButton = button; Theme.Apply(dialog); dialog.ShowDialog(this);
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
        var session = new AirPlaySession(receiver, input.Id, (int)delay.Value, volume.Value,
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
