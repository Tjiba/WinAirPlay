using System.Drawing.Drawing2D;

namespace WinAirPlay;

internal sealed class StudioButton : Button
{
    private bool hovered;
    public bool Filled { get; set; }
    public bool Borderless { get; set; }
    public string Symbol { get; set; } = "";

    public StudioButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Padding = new Padding(0);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var ink = !Enabled ? Theme.Muted : Filled ? Theme.Background : ForeColor;
        using var shape = Theme.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), Math.Min(Height / 2f - 1, 20 * scale));
        using var fill = new SolidBrush(Filled && Enabled ? Theme.Accent : hovered && Enabled ? Theme.Border : BackColor);
        e.Graphics.FillPath(fill, shape);
        if (!Borderless)
        {
            using var border = new Pen(Enabled ? Theme.Accent : Theme.Border, scale);
            e.Graphics.DrawPath(border, shape);
        }
        var iconSize = Symbol.Length > 0 ? (int)(18 * scale) : 0;
        var gap = iconSize > 0 && Text.Length > 0 ? (int)(10 * scale) : 0;
        var textWidth = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        var left = (Width - iconSize - gap - textWidth) / 2;
        if (iconSize > 0) DrawSymbol(e.Graphics, new Rectangle(left, (Height - iconSize) / 2, iconSize, iconSize), Symbol, ink);
        var textBounds = new Rectangle(left + iconSize + gap, 0, textWidth, Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, ink, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -6, -6), ink, BackColor);
    }

    public static void DrawSymbol(Graphics graphics, Rectangle bounds, string symbol, Color color)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(bounds.X, bounds.Y);
        graphics.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (symbol == "close") { graphics.DrawLine(pen, 5, 5, 19, 19); graphics.DrawLine(pen, 19, 5, 5, 19); }
        else if (symbol == "stop") graphics.DrawRectangle(pen, 5, 5, 14, 14);
        else if (symbol == "refresh")
        {
            graphics.DrawArc(pen, 3, 3, 18, 18, 45, 280);
            graphics.DrawLines(pen, new Point[] {new Point(15, 3), new Point(21, 3), new Point(21, 9) });
        }
        else if (symbol == "settings")
        {
            var teeth = new PointF[32];
            for (var i = 0; i < teeth.Length; i++)
            {
                var angle = i * Math.PI / 16;
                var radius = i % 4 is 1 or 2 ? 10 : 7.5f;
                teeth[i] = new PointF(12 + (float)Math.Cos(angle) * radius, 12 + (float)Math.Sin(angle) * radius);
            }
            graphics.DrawPolygon(pen, teeth); graphics.DrawEllipse(pen, 9, 9, 6, 6);
        }
        else if (symbol == "airplay")
        {
            graphics.DrawLines(pen, new Point[] {new Point(7, 18), new Point(3, 18), new Point(3, 4), new Point(21, 4), new Point(21, 18), new Point(17, 18) });
            graphics.DrawPolygon(pen, new Point[] {new Point(7, 22), new Point(12, 15), new Point(17, 22) });
        }
        else if (symbol is "mute" or "sound")
        {
            graphics.DrawPolygon(pen, new Point[] {new Point(3, 9), new Point(7, 9), new Point(12, 5), new Point(12, 19), new Point(7, 15), new Point(3, 15) });
            if (symbol == "mute") { graphics.DrawLine(pen, 17, 9, 22, 15); graphics.DrawLine(pen, 22, 9, 17, 15); }
            else graphics.DrawArc(pen, 10, 5, 11, 14, -70, 140);
        }
        graphics.Restore(state);
    }
}
