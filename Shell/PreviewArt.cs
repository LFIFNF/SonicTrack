using System.Windows;
using System.Windows.Media;

namespace SonicTrack.App.Shell;

/// <summary>
/// Static vector thumbnail for a component card. Purely decorative: it does not capture audio,
/// so the launcher costs nothing while idle.
/// </summary>
public sealed class PreviewArt : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(PreviewKind), typeof(PreviewArt),
        new FrameworkPropertyMetadata(PreviewKind.Waterfall, FrameworkPropertyMetadataOptions.AffectsRender));

    public PreviewKind Kind
    {
        get => (PreviewKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    // Same colour ramp as the V6.1 legend (weak -> strong).
    private static readonly (double t, Color c)[] Ramp =
    {
        (0, Color.FromRgb(0x00, 0x00, 0x00)), (.14, Color.FromRgb(0x2A, 0x08, 0x40)),
        (.32, Color.FromRgb(0x1D, 0x4E, 0xD8)), (.55, Color.FromRgb(0x16, 0xA3, 0x4A)),
        (.78, Color.FromRgb(0xF5, 0xD9, 0x0A)), (1, Color.FromRgb(0xDC, 0x26, 0x26)),
    };

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;
        if (Kind == PreviewKind.Waterfall) DrawWaterfall(dc, w, h);
        else DrawWaveform(dc, w, h);
    }

    private static Color Sample(double t)
    {
        t = Math.Clamp(t, 0, 1);
        for (int i = 1; i < Ramp.Length; i++)
        {
            if (t > Ramp[i].t) continue;
            var (t0, a) = Ramp[i - 1]; var (t1, b) = Ramp[i];
            double f = (t - t0) / (t1 - t0);
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));
        }
        return Ramp[^1].c;
    }

    private static Color Shade(Color c, double k)
        => k >= 1
            ? Color.FromRgb((byte)(c.R + (255 - c.R) * (k - 1)), (byte)(c.G + (255 - c.G) * (k - 1)), (byte)(c.B + (255 - c.B) * (k - 1)))
            : Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));

    private static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static double Level(int col, int row, int cols)
    {
        // Deterministic spectrum-like field: strong lows, a mid formant, decaying highs, gentle time variation.
        double x = col / (double)(cols - 1);
        double env = .92 * Math.Exp(-x * 2.1) + .55 * Math.Exp(-Math.Pow((x - .42) / .12, 2)) + .12;
        double ripple = .72 + .28 * Math.Sin(col * 1.7 + row * 2.3) * Math.Cos(col * .53 - row * 1.1);
        return Math.Clamp(env * ripple, .04, 1);
    }

    private static void DrawWaterfall(DrawingContext dc, double w, double h)
    {
        const int cols = 26, rows = 5;
        double depthX = 7, depthY = 6;
        double usableW = w - depthX * (rows - 1) - 8;
        double pitch = usableW / cols, bar = pitch;
        double baseFront = h - 6, maxH = h - depthY * (rows - 1) - 14;
        for (int r = rows - 1; r >= 0; r--)
        {
            double ox = 4 + r * depthX, baseY = baseFront - r * depthY;
            double fade = 1 - r * .1;
            for (int c = 0; c < cols; c++)
            {
                double level = Level(c, r, cols);
                double bh = Math.Max(2, level * maxH * fade);
                var color = Sample(level);
                double x = ox + c * pitch, top = baseY - bh;
                double dx = depthX * .55, dy = depthY * .55;
                dc.DrawRectangle(Brush(color), null, new Rect(x, top, bar, bh));
                var topFace = new StreamGeometry();
                using (var g = topFace.Open())
                {
                    g.BeginFigure(new Point(x, top), true, true);
                    g.LineTo(new Point(x + dx, top - dy), false, false);
                    g.LineTo(new Point(x + bar + dx, top - dy), false, false);
                    g.LineTo(new Point(x + bar, top), false, false);
                }
                topFace.Freeze();
                dc.DrawGeometry(Brush(Shade(color, 1.35)), null, topFace);
                if (c == cols - 1)
                {
                    var side = new StreamGeometry();
                    using (var g = side.Open())
                    {
                        g.BeginFigure(new Point(x + bar, top), true, true);
                        g.LineTo(new Point(x + bar + dx, top - dy), false, false);
                        g.LineTo(new Point(x + bar + dx, baseY - dy), false, false);
                        g.LineTo(new Point(x + bar, baseY), false, false);
                    }
                    side.Freeze();
                    dc.DrawGeometry(Brush(Shade(color, .6)), null, side);
                }
            }
        }
    }

    private static void DrawWaveform(DrawingContext dc, double w, double h)
    {
        double mid = h / 2, amp = h * .4;
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            bool first = true;
            for (double x = 6; x <= w - 6; x += 1.2)
            {
                double t = x / w;
                double env = .25 + .75 * Math.Pow(Math.Sin(Math.PI * t), .8) * (.6 + .4 * Math.Sin(t * 11.0));
                double v = (.62 * Math.Sin(x * .23) + .28 * Math.Sin(x * .61 + 1.3) + .1 * Math.Sin(x * 1.37)) * env;
                var p = new Point(x, mid - v * amp);
                if (first) { g.BeginFigure(p, false, false); first = false; }
                else g.LineTo(p, true, true);
            }
        }
        line.Freeze();
        var under = new Pen(Brush(Color.FromArgb(70, 0x22, 0x22, 0x22)), 1.6) { LineJoin = PenLineJoin.Round };
        var coral = new Pen(Brush(Color.FromRgb(0xFF, 0x7F, 0x66)), 1.6) { LineJoin = PenLineJoin.Round };
        under.Freeze(); coral.Freeze();
        dc.DrawLine(new Pen(Brush(Color.FromArgb(40, 0x8A, 0x8F, 0x98)), 1), new Point(6, mid), new Point(w - 6, mid));
        dc.PushTransform(new TranslateTransform(1, 1));
        dc.DrawGeometry(null, under, line);
        dc.Pop();
        dc.DrawGeometry(null, coral, line);
    }
}
