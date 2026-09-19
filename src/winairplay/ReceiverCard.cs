using System.Drawing.Drawing2D;
using WinAirPlay.Core;

namespace WinAirPlay;

internal sealed class ReceiverCard : UserControl
{
    private readonly Button action = new() { FlatStyle = FlatStyle.Flat, Width = 112, Height = 34, Cursor = Cursors.Hand };
    private readonly Label name = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label detail = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private bool active;
    public Receiver Receiver { get; }
    public event Action<Receiver>? Selected;

    public ReceiverCard(Receiver receiver)
    {
        Receiver = receiver;
        DoubleBuffered = true;
        Height = 88;
        Margin = new Padding(0, 0, 0, 10);
        Padding = new Padding(12);
        BackColor = Theme.Background;
        name.Text = receiver.Name;
        name.Font = new Font("Segoe UI Semibold", 11);
        name.ForeColor = Theme.Text;
        detail.Font = new Font("Segoe UI", 9);
        detail.ForeColor = Theme.Muted;
        action.Font = new Font("Segoe UI Semibold", 9);
        action.FlatAppearance.BorderSize = 0;
        action.Click += (_, _) => Selected?.Invoke(Receiver);
        Controls.AddRange([name, detail, action]);
        Resize += (_, _) =>
        {
            name.SetBounds(70, 17, Math.Max(20, Width - 208), 26);
            detail.SetBounds(70, 45, Math.Max(20, Width - 208), 22);
            action.Location = new Point(Width - 126, 27);
        };
        SetState(false, false, false);
    }

    public void SetState(bool connected, bool pending, bool locked)
    {
        active = connected;
        var fill = connected ? Color.FromArgb(32, 48, 64) : Theme.Surface;
        name.BackColor = fill; detail.BackColor = fill;
        detail.Text = pending ? "Connecting…" : connected ? "●  Streaming" : "AirPlay  ·  " + Receiver.Host;
        detail.ForeColor = connected ? Theme.Green : Theme.Muted;
        action.Text = pending ? "Cancel" : connected ? "Disconnect" : "Listen";
        action.Enabled = !locked;
        action.BackColor = connected ? Color.FromArgb(47, 66, 84) : Theme.Accent;
        action.ForeColor = connected ? Theme.Text : Color.FromArgb(12, 25, 46);
        action.FlatAppearance.MouseOverBackColor = connected ? Color.FromArgb(58, 79, 102) : Color.FromArgb(126, 175, 255);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = Theme.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), 13);
        using var fill = new SolidBrush(active ? Color.FromArgb(32, 48, 64) : Theme.Surface);
        using var border = new Pen(active ? Color.FromArgb(66, 103, 143) : Theme.Border);
        graphics.FillPath(fill, outline); graphics.DrawPath(border, outline);
        using var speaker = Theme.Rounded(new RectangleF(24, 26, 26, 38), 10);
        using var speakerPen = new Pen(active ? Theme.Accent : Theme.Muted, 1.6f);
        graphics.DrawPath(speakerPen, speaker);
        graphics.DrawEllipse(speakerPen, 29, 30, 16, 5);
        using var dot = new SolidBrush(active ? Theme.Green : Theme.Muted);
        graphics.FillEllipse(dot, 35, 55, 4, 4);
    }
}
