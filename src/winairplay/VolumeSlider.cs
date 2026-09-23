using System.Drawing.Drawing2D;

namespace WinAirPlay;

internal sealed class VolumeSlider : Control
{
    private int value;
    public event EventHandler? ValueChanged;
    public int Value
    {
        get => value;
        set
        {
            var next = Math.Clamp(value, 0, 100);
            if (this.value == next) return;
            this.value = next;
            AccessibleDescription = $"{next} %";
            Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public VolumeSlider()
    {
        DoubleBuffered = true; TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
        AccessibleRole = AccessibleRole.Slider; AccessibleName = "Speaker volume";
        Cursor = Cursors.Hand; BackColor = Theme.Background;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var padding = 10f * scale;
        var center = Height / 2f;
        var x = padding + Math.Max(0, Width - 2 * padding) * Value / 100f;
        using var track = new Pen(Theme.Border, 4 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new Pen(Theme.Accent, 4 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var thumb = new SolidBrush(Theme.Text);
        e.Graphics.DrawLine(track, padding, center, Math.Max(padding, Width - padding), center);
        e.Graphics.DrawLine(fill, padding, center, x, center);
        e.Graphics.FillEllipse(thumb, x - 6 * scale, center - 6 * scale, 12 * scale, 12 * scale);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Theme.Muted, BackColor);
    }
    private void SelectValue(int x)
    {
        var padding = 10f * DeviceDpi / 96;
        Value = (int)Math.Round((x - padding) * 100 / Math.Max(1, Width - padding * 2));
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus(); Capture = true; SelectValue(e.X);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (Capture) SelectValue(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Capture = false; }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.Down: Value -= 1; break;
            case Keys.Right: case Keys.Up: Value += 1; break;
            case Keys.Home: Value = 0; break;
            case Keys.End: Value = 100; break;
            default: return;
        }
        e.Handled = true;
    }
}
