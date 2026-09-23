using WinAirPlay.Core;

namespace WinAirPlay;

internal sealed class ReceiverCard : UserControl
{
    private readonly Button action = new() { FlatStyle = FlatStyle.Flat, Width = 96, Height = 34, Cursor = Cursors.Hand };
    private readonly Label name = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label detail = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly VolumeSlider volume = new();
    private readonly Label volumeLabel = new() { TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Muted };
    private readonly ToolTip hints = new();
    public Receiver Receiver { get; }
    public event Action<Receiver>? Selected;
    public event Action<Receiver, int>? VolumeChanged;

    public ReceiverCard(Receiver receiver, bool individualVolume, int initialVolume)
    {
        Receiver = receiver;
        DoubleBuffered = true;
        Height = individualVolume ? 108 : 72;
        Margin = new Padding(0);
        BackColor = Theme.Background;
        name.Text = receiver.Name;
        hints.SetToolTip(name, receiver.Name + " · " + receiver.Host);
        name.Font = new Font("Segoe UI Semibold", 11);
        detail.Font = new Font("Segoe UI", 9);
        action.Font = new Font("Segoe UI", 9);
        action.FlatAppearance.BorderSize = 0;
        action.Click += (_, _) => Selected?.Invoke(Receiver);
        action.AccessibleName = receiver.Name + " connection";
        volume.Visible = volumeLabel.Visible = individualVolume;
        volume.AccessibleName = receiver.Name + " volume";
        volume.Value = initialVolume;
        volumeLabel.Text = $"{volume.Value} %";
        volume.ValueChanged += (_, _) =>
        {
            volumeLabel.Text = $"{volume.Value} %";
            VolumeChanged?.Invoke(Receiver, volume.Value);
        };
        Controls.AddRange([name, detail, action, volume, volumeLabel]);
        void LayoutCard()
        {
            var scale = DeviceDpi / 96f;
            int Px(int value) => (int)(value * scale);
            name.SetBounds(0, Px(10), Math.Max(20, Width - Px(110)), Px(26));
            detail.SetBounds(0, Px(36), Math.Max(20, Width - Px(110)), Px(22));
            action.SetBounds(Width - Px(96), Px(19), Px(96), Px(34));
            volume.SetBounds(0, Px(70), Math.Max(20, Width - Px(58)), Px(28));
            volumeLabel.SetBounds(Width - Px(54), Px(70), Px(54), Px(28));
            using var outline = Theme.Rounded(new RectangleF(0, 0, action.Width, action.Height), Px(17));
            var previous = action.Region;
            action.Region = new Region(outline);
            previous?.Dispose();
        }
        Load += (_, _) => { Height = (int)((individualVolume ? 108 : 72) * DeviceDpi / 96f); LayoutCard(); };
        Resize += (_, _) => LayoutCard();
        SetState(false, false, false);
    }

    public void SetState(bool connected, bool pending, bool locked)
    {
        name.ForeColor = connected ? Theme.Accent : Theme.Text;
        detail.Text = locked ? "Stopping…" : pending ? "Connecting…" : connected ? "Playing" : "Available";
        detail.ForeColor = Theme.Muted;
        action.Text = pending ? "Cancel" : connected ? "Stop" : "Listen";
        action.Enabled = !locked;
        action.BackColor = connected ? Color.FromArgb(41, 57, 81) : Theme.Surface;
        action.FlatAppearance.BorderColor = action.BackColor;
        action.ForeColor = connected ? Theme.Accent : Theme.Text;
        action.FlatAppearance.MouseOverBackColor = connected ? Color.FromArgb(58, 79, 102) : Theme.Border;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) hints.Dispose();
        base.Dispose(disposing);
    }
}
