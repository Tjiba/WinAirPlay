using WinAirPlay.Core;

namespace WinAirPlay;

internal sealed class ReceiverCard : UserControl
{
    private readonly StudioButton action = new() { Width = 96, Height = 36 };
    private readonly Label name = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label detail = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly VolumeSlider volume = new();
    private readonly Label volumeLabel = new() { TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Muted };
    private readonly ToolTip hints = new();
    private readonly StudioButton details = new() { Text = "…", Borderless = true };
    private readonly StudioButton mute = new() { Symbol = "mute", Borderless = true };
    private readonly ContextMenuStrip menu = new() { Renderer = new DarkMenuRenderer() };
    private int previousVolume;
    public bool Manual { get; set; }
    public event Action<Receiver>? DetailsRequested;
    public event Action<Receiver>? EditRequested;
    public event Action<Receiver>? RemoveRequested;
    public Receiver Receiver { get; }
    public event Action<Receiver>? Selected;
    public event Action<Receiver, int>? VolumeChanged;

    public ReceiverCard(Receiver receiver, bool individualVolume, int initialVolume, int restoreVolume)
    {
        Receiver = receiver;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        Height = individualVolume ? 144 : 104;
        Margin = new Padding(0, 0, 0, (int)(12 * DeviceDpi / 96f));
        BackColor = Theme.Background;
        name.Text = receiver.Name;
        hints.SetToolTip(name, receiver.Name + " · " + receiver.Host + (receiver.Model.Length == 0 ? "" : " · " + receiver.Model));
        name.Font = new Font("Segoe UI Semibold", 13);
        detail.Font = new Font("Segoe UI", 9);
        action.Font = new Font("Segoe UI", 9);
        action.FlatAppearance.BorderSize = 0;
        action.Click += (_, _) => Selected?.Invoke(Receiver);
        action.AccessibleName = receiver.Name + " connection";
        volume.Visible = volumeLabel.Visible = individualVolume;
        volume.AccessibleName = receiver.Name + " volume";
        volume.Value = initialVolume;
        previousVolume = initialVolume > 0 ? initialVolume : restoreVolume;
        mute.Symbol = initialVolume == 0 ? "sound" : "mute";
        mute.AccessibleName = receiver.Name + " " + Texts.Get("Mute");
        mute.FlatAppearance.BorderSize = 0;
        mute.Font = new Font("Segoe UI", 8);
        mute.Visible = individualVolume;
        mute.Click += (_, _) => { if (volume.Value > 0) { previousVolume = volume.Value; volume.Value = 0; } else volume.Value = previousVolume; };
        details.FlatAppearance.BorderSize = 0;
        details.AccessibleName = receiver.Name + " " + Texts.Get("Details");
        details.Click += (_, _) => menu.Show(details, new Point(0, details.Height));
        menu.Opening += (_, _) =>
        {
            while (menu.Items.Count > 0) { var item = menu.Items[0]; menu.Items.RemoveAt(0); item.Dispose(); }
            menu.Items.Add(Texts.Get("Details"), null, (_, _) => DetailsRequested?.Invoke(Receiver));
            if (!Manual) return;
            menu.Items.Add(new ToolStripMenuItem(Texts.Get("Edit speaker"), null, (_, _) => EditRequested?.Invoke(Receiver)) { Enabled = action.Text != Texts.Get("Stop") && action.Text != Texts.Get("Cancel") && action.Enabled });
            menu.Items.Add(new ToolStripMenuItem(Texts.Get("Remove speaker"), null, (_, _) => RemoveRequested?.Invoke(Receiver)) { Enabled = action.Text != Texts.Get("Stop") && action.Text != Texts.Get("Cancel") && action.Enabled });
        };
        volumeLabel.Text = $"{volume.Value} %";
        volume.ValueChanged += (_, _) =>
        {
            volumeLabel.Text = $"{volume.Value} %";
            mute.Symbol = volume.Value == 0 ? "sound" : "mute";
            mute.AccessibleName = receiver.Name + " " + Texts.Get(volume.Value == 0 ? "Unmute" : "Mute");
            hints.SetToolTip(mute, Texts.Get(volume.Value == 0 ? "Unmute" : "Mute")); mute.Invalidate();
            VolumeChanged?.Invoke(Receiver, volume.Value);
        };
        var illustration = new SpeakerIllustration(receiver.Kind) { AccessibleName = receiver.Kind == SpeakerKind.HomePodMini ? "HomePod mini" : receiver.Kind == SpeakerKind.HomePod ? "HomePod" : "AirPlay" };
        Controls.AddRange([name, detail, action, volume, volumeLabel, mute, details, illustration]);
        foreach (Control control in Controls) control.BackColor = Theme.Surface;
        mute.ForeColor = details.ForeColor = Theme.Text;
        void LayoutCard()
        {
            var scale = DeviceDpi / 96f;
            int Px(int value) => (int)(value * scale);
            illustration.SetBounds(Px(10), Px(10), Px(76), Px(76));
            name.SetBounds(Px(98), Px(23), Math.Max(20, Width - Px(238)), Px(28));
            detail.SetBounds(Px(98), Px(52), Math.Max(20, Width - Px(238)), Px(22));
            action.SetBounds(Width - Px(132), Px(30), Px(96), Px(36));
            details.SetBounds(Width - Px(32), Px(30), Px(24), Px(36));
            mute.SetBounds(Px(16), Px(98), Px(32), Px(32));
            volume.SetBounds(Px(60), Px(98), Math.Max(20, Width - Px(136)), Px(32));
            volumeLabel.SetBounds(Width - Px(68), Px(98), Px(50), Px(32));
            Invalidate();
        }
        Load += (_, _) => { Height = (int)((individualVolume ? 144 : 104) * DeviceDpi / 96f); LayoutCard(); };
        Resize += (_, _) => LayoutCard();
        DpiChangedAfterParent += (_, _) => { Height = (int)((individualVolume ? 144 : 104) * DeviceDpi / 96f); Margin = new Padding(0, 0, 0, (int)(12 * DeviceDpi / 96f)); LayoutCard(); };
        SetState(false, false, false);
    }

    public void SetState(bool connected, bool pending, bool locked, string? error = null, int retrySeconds = 0, bool suspended = false)
    {
        name.ForeColor = connected ? Theme.Accent : Theme.Text;
        detail.Text = suspended ? Texts.Get("Waiting for Windows…") : locked ? Texts.Get("Stopping…")
            : pending ? Texts.Get("Connecting…") : connected ? Texts.Get("Playing")
            : retrySeconds > 0 ? string.Format(Texts.Get("Retry in {0}s"), retrySeconds)
            : error is not null ? Texts.Get("Connection failed") : Texts.Get("Available");
        hints.SetToolTip(detail, error ?? detail.Text);
        details.Visible = error is not null || Manual;
        detail.ForeColor = connected ? Theme.Accent : error is not null ? Theme.Error : Theme.Muted;
        action.Text = Texts.Get(pending || retrySeconds > 0 || suspended ? "Cancel" : connected ? "Stop" : error is not null ? "Retry" : "Stream");
        action.Enabled = !locked;
        action.Filled = connected;
        action.ForeColor = Theme.Accent;
        action.Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        using var shape = Theme.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 12 * scale);
        using var fill = new SolidBrush(Theme.Surface);
        using var border = new Pen(Theme.Border, scale);
        e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(border, shape);
        e.Graphics.DrawLine(border, 90 * scale, 22 * scale, 90 * scale, 76 * scale);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { hints.Dispose(); menu.Dispose(); }
        base.Dispose(disposing);
    }
}
