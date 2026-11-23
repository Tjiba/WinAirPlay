using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WinAirPlay;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(32, 34, 32);
    public static readonly Color Surface = Color.FromArgb(40, 43, 40);
    public static readonly Color Border = Color.FromArgb(64, 70, 62);
    public static readonly Color Text = Color.FromArgb(241, 240, 233);
    public static readonly Color Muted = Color.FromArgb(168, 172, 163);
    public static readonly Color Accent = Color.FromArgb(168, 188, 139);
    public static readonly Color Green = Accent;
    public static readonly Color Error = Color.FromArgb(245, 165, 115);

    public static void Apply(Control control)
    {
        control.Text = Texts.Get(control.Text);
        control.BackColor = control is TextBox or ComboBox or NumericUpDown ? Surface : Background;
        control.ForeColor = Text;
        if (control is ComboBox combo)
        {
            combo.FlatStyle = FlatStyle.Flat;
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.DrawItem += (_, e) =>
            {
                using var background = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Border : Surface);
                e.Graphics.FillRectangle(background, e.Bounds);
                var text = e.Index >= 0 ? combo.GetItemText(combo.Items[e.Index]) : combo.Text;
                TextRenderer.DrawText(e.Graphics, text, combo.Font, e.Bounds, combo.Enabled ? Text : Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                e.DrawFocusRectangle();
            };
        }
        if (control is ScrollableControl or ComboBox or NumericUpDown)
            control.HandleCreated += (_, _) => SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        if (control is Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = Surface;
            button.Cursor = Cursors.Hand;
        }
        foreach (Control child in control.Controls) Apply(child);
        if (control is Form form) form.HandleCreated += (_, _) =>
        {
            var dark = 1;
            DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
        };
    }

    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string theme, string? subId);
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new Colors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
        base.OnRenderItemText(e);
    }
    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Background;
        public override Color ImageMarginGradientBegin => Theme.Background;
        public override Color ImageMarginGradientMiddle => Theme.Background;
        public override Color ImageMarginGradientEnd => Theme.Background;
        public override Color MenuItemSelected => Theme.Surface;
        public override Color MenuItemSelectedGradientBegin => Theme.Surface;
        public override Color MenuItemSelectedGradientEnd => Theme.Surface;
        public override Color MenuItemPressedGradientBegin => Theme.Surface;
        public override Color MenuItemPressedGradientEnd => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.Border;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color CheckBackground => Theme.Surface;
    }
}
