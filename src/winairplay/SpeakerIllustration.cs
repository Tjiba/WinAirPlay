using System.Drawing.Drawing2D;
using WinAirPlay.Core;

namespace WinAirPlay;

internal sealed class SpeakerIllustration(SpeakerKind kind) : Control
{
    private static readonly Lazy<Image> homePod = new(() => Load("homepod.png"));
    private static readonly Lazy<Image> mini = new(() => Load("homepod-mini.png"));

    private static Image Load(string name)
    {
        using var stream = typeof(SpeakerIllustration).Assembly.GetManifestResourceStream("WinAirPlay." + name)
            ?? throw new InvalidOperationException("Missing speaker illustration: " + name);
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (kind != SpeakerKind.Unknown)
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var size = Math.Min(Width, Height);
            e.Graphics.DrawImage(kind == SpeakerKind.HomePodMini ? mini.Value : homePod.Value,
                new Rectangle((Width - size) / 2, (Height - size) / 2, size, size));
            return;
        }
        var scale = Math.Min(Width, Height) / 64f;
        using var pen = new Pen(Theme.Muted, 1.5f * scale);
        var bounds = new RectangleF(Width / 2f - 18 * scale, Height / 2f - 25 * scale, 36 * scale, 50 * scale);
        using var outline = Theme.Rounded(bounds, 7 * scale);
        e.Graphics.DrawPath(pen, outline);
        e.Graphics.DrawEllipse(pen, Width / 2f - 10 * scale, Height / 2f - 4 * scale, 20 * scale, 20 * scale);
        e.Graphics.DrawEllipse(pen, Width / 2f - 4 * scale, Height / 2f - 17 * scale, 8 * scale, 8 * scale);
    }
}
