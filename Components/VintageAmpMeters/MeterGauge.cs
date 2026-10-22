using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SonicTrack.VintageAmpMeters;

public sealed class MeterGauge : FrameworkElement
{
    private const double MinimumDb = -24;
    private const double MaximumDb = 3;
    private static readonly Typeface Typeface = new(new FontFamily("Segoe UI"), FontStyles.Normal,
        FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface SmallTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal,
        FontWeights.Medium, FontStretches.Normal);
    private static readonly Brush HousingBrush = FrozenBrush(new LinearGradientBrush(
        Color.FromRgb(29, 28, 26), Color.FromRgb(88, 77, 54), 45));
    private static readonly Brush InnerBezelBrush = FrozenBrush(new LinearGradientBrush(
        Color.FromRgb(163, 145, 103), Color.FromRgb(61, 54, 41), 90));
    private static readonly Brush FaceBrush = FrozenBrush(new LinearGradientBrush(
        Color.FromRgb(255, 246, 218), Color.FromRgb(211, 192, 145), 90));
    private static readonly Brush ScaleBrush = FrozenBrush(new SolidColorBrush(Color.FromRgb(63, 54, 40)));
    private static readonly Brush NeedleBrush = FrozenBrush(new SolidColorBrush(Color.FromRgb(39, 37, 33)));
    private static readonly Brush PeakBrush = FrozenBrush(new SolidColorBrush(Color.FromRgb(161, 47, 32)));
    private static readonly Brush RedScaleBrush = FrozenBrush(new SolidColorBrush(Color.FromRgb(157, 49, 34)));
    private static readonly Brush LabelBrush = FrozenBrush(new SolidColorBrush(Color.FromRgb(72, 60, 42)));
    private static readonly Brush GlowBrush = CreateGlowBrush();
    private double _currentDb = MinimumDb;
    private double _peakDb = MinimumDb;
    private double _peakDeadline;
    private bool _hasInput;

    internal string Title { get; set; } = "";
    public bool OverlayOnly { get; set; }
    internal bool ShowScaleNumbers { get; set; } = true;
    internal double ShadowStrength { get; set; } = 65;

    internal void UpdateLevel(double currentDb, double elapsedSeconds, double nowSeconds, bool hasInput)
    {
        currentDb = Math.Clamp(double.IsFinite(currentDb) ? currentDb : MinimumDb, MinimumDb, MaximumDb);
        elapsedSeconds = Math.Clamp(elapsedSeconds, 0, 0.25);
        _hasInput = hasInput;

        double timeConstant = currentDb > _currentDb ? 0.035 : 0.11;
        double alpha = 1 - Math.Exp(-elapsedSeconds / timeConstant);
        _currentDb += (currentDb - _currentDb) * alpha;

        if (_currentDb > _peakDb + 0.03)
        {
            _peakDb = _currentDb;
            _peakDeadline = nowSeconds + 0.65;
        }
        else if (nowSeconds > _peakDeadline)
        {
            _peakDb = Math.Max(_currentDb, _peakDb - elapsedSeconds * 8.0);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 100)
            return;

        double left = (ActualWidth - size) / 2;
        double top = (ActualHeight - size) / 2;
        double centerX = ActualWidth / 2;
        double centerY = ActualHeight / 2;
        double radius = size * 0.475;
        var center = new Point(centerX, centerY);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        if (OverlayOnly)
        {
            DrawFloatingScale(dc, center, radius);
            dc.Pop();
            return;
        }

        dc.DrawEllipse(GlowBrush, null,
            new Point(centerX, top + size * 0.86), size * 0.48, size * 0.2);
        dc.DrawEllipse(HousingBrush, new Pen(Brushes.Black, 1),
            center, radius, radius);
        dc.DrawEllipse(InnerBezelBrush, new Pen(Brushes.White.CloneCurrentValue(), 0.7),
            center, radius * 0.91, radius * 0.91);
        dc.DrawEllipse(FaceBrush, new Pen(ScaleBrush, 1),
            center, radius * 0.85, radius * 0.85);

        var glassBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(55, 255, 255, 255), 0),
                new(Color.FromArgb(4, 255, 255, 255), 0.48),
                new(Color.FromArgb(0, 255, 255, 255), 0.68)
            }
        };
        glassBrush.Freeze();
        dc.DrawEllipse(glassBrush, null, center, radius * 0.845, radius * 0.845);

        double pivotY = centerY + radius * 0.36;
        var pivot = new Point(centerX, pivotY);
        double scaleRadius = radius * 0.64;
        DrawArc(dc, pivot, scaleRadius, -180, 180, new Pen(ScaleBrush, 1.5));

        for (int db = -24; db <= 3; db += 3)
        {
            double angle = AngleFor(db);
            Brush tickBrush = db >= 0 ? RedScaleBrush : ScaleBrush;
            bool major = db % 6 == 0 || db == 3;
            Point outer = Polar(pivot, scaleRadius, angle);
            Point inner = Polar(pivot, scaleRadius - (major ? 11 : 6), angle);
            dc.DrawLine(new Pen(tickBrush, major ? 1.8 : 1), inner, outer);
            if (major && ShowScaleNumbers)
            {
                Point labelPoint = Polar(pivot, scaleRadius - 23, angle);
                string label = db > 0 ? $"+{db}" : db.ToString(CultureInfo.InvariantCulture);
                DrawText(dc, label, labelPoint, 9, LabelBrush);
            }
        }

        DrawText(dc, "VU", new Point(centerX, centerY - radius * 0.65), 14, LabelBrush, Typeface);
        DrawText(dc, Title.ToUpperInvariant(), new Point(centerX, centerY - radius * 0.47), 9, LabelBrush, SmallTypeface);
        DrawText(dc, _hasInput ? "LIVE  /  PEAK" : "NO SIGNAL", new Point(centerX, top + size * 0.79),
            8, _hasInput ? LabelBrush : RedScaleBrush, SmallTypeface);

        DrawNeedle(dc, pivot, scaleRadius * 0.82, _peakDb, PeakBrush, 2.2);
        DrawNeedle(dc, pivot, scaleRadius * 0.75, _currentDb, NeedleBrush, 2.7);
        dc.DrawEllipse(InnerBezelBrush, new Pen(ScaleBrush, 1), pivot, 7, 7);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(223, 209, 173)), null, pivot, 3, 3);

        DrawScrew(dc, Polar(center, radius * 0.79, -135));
        DrawScrew(dc, Polar(center, radius * 0.79, -45));
        dc.Pop();
    }

    private void DrawFloatingScale(DrawingContext dc, Point center, double radius)
    {
        double pivotY = center.Y + radius * 0.36;
        var pivot = new Point(center.X, pivotY);
        double scaleRadius = radius * 0.64;
        double shadow = Math.Clamp(ShadowStrength, 0, 100) / 100;
        var scaleGlow = new Pen(new SolidColorBrush(Color.FromArgb(
            (byte)(shadow * 52), 255, 220, 166)), 3.5);
        var scalePen = new Pen(ScaleBrush, 1.5);
        DrawArc(dc, pivot, scaleRadius, -180, 180, scaleGlow);
        DrawArc(dc, pivot, scaleRadius, -180, 180, scalePen);

        for (int db = -24; db <= 3; db += 3)
        {
            double angle = AngleFor(db);
            bool major = db % 6 == 0 || db == 3;
            Brush tickBrush = db >= 0 ? RedScaleBrush : ScaleBrush;
            Point outer = Polar(pivot, scaleRadius, angle);
            Point inner = Polar(pivot, scaleRadius - (major ? 11 : 6), angle);
            double thickness = major ? 1.8 : 1;
            DrawOutlinedLine(dc, inner, outer, tickBrush, thickness, shadow);
            if (major && ShowScaleNumbers)
            {
                Point labelPoint = Polar(pivot, scaleRadius - 23, angle);
                string label = db > 0 ? $"+{db}" : db.ToString(CultureInfo.InvariantCulture);
                DrawText(dc, label, labelPoint, 10, LabelBrush);
            }
        }

        DrawNeedle(dc, pivot, scaleRadius * 0.82, _peakDb, PeakBrush, 2.2);
        DrawNeedle(dc, pivot, scaleRadius * 0.75, _currentDb, NeedleBrush, 2.7);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(shadow * 64), 255, 220, 166)),
            null, pivot, 8, 8);
        dc.DrawEllipse(InnerBezelBrush, new Pen(ScaleBrush, 1), pivot, 6, 6);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(223, 209, 173)), null, pivot, 2.5, 2.5);
    }

    private static void DrawScrew(DrawingContext dc, Point point)
    {
        dc.DrawEllipse(InnerBezelBrush, new Pen(ScaleBrush, 0.6), point, 3, 3);
        dc.DrawLine(new Pen(ScaleBrush, 0.7), new Point(point.X - 1.5, point.Y + 1.5),
            new Point(point.X + 1.5, point.Y - 1.5));
    }

    private void DrawText(DrawingContext dc, string value, Point center, double size, Brush brush,
        Typeface? typeface = null)
    {
        var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface ?? SmallTypeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    private void DrawNeedle(DrawingContext dc, Point center, double length, double db, Brush brush,
        double thickness)
    {
        Point tip = Polar(center, length, AngleFor(db));
        double shadow = Math.Clamp(ShadowStrength, 0, 100) / 100;
        if (shadow > 0)
        {
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb((byte)(shadow * 28), 255, 220, 166)),
                thickness + 7), center, tip);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb((byte)(shadow * 76), 255, 220, 166)),
                thickness + 2.5), center, tip);
        }
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb((byte)(70 + shadow * 55), 0, 0, 0)), thickness + 2),
            new Point(center.X + 1.2, center.Y + 1.2), new Point(tip.X + 1.2, tip.Y + 1.2));
        dc.DrawLine(new Pen(brush, thickness), center, tip);
    }

    private void DrawOutlinedLine(DrawingContext dc, Point start, Point end, Brush brush,
        double thickness, double shadow)
    {
        if (shadow > 0)
        {
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb((byte)(shadow * 24), 255, 220, 166)),
                thickness + 5), start, end);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb((byte)(shadow * 55), 255, 220, 166)),
                thickness + 2), start, end);
        }
        dc.DrawLine(new Pen(brush, thickness), start, end);
    }

    private static double AngleFor(double db) =>
        -180 + (Math.Clamp(db, MinimumDb, MaximumDb) - MinimumDb) /
        (MaximumDb - MinimumDb) * 180;

    private static Point Polar(Point center, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static void DrawArc(DrawingContext dc, Point center, double radius, double startDegrees,
        double sweepDegrees, Pen pen)
    {
        Point start = Polar(center, radius, startDegrees);
        Point end = Polar(center, radius, startDegrees + sweepDegrees);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180,
                SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Brush CreateGlowBrush()
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(90, 255, 174, 78), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(32, 255, 139, 55), 0.48));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 139, 55), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush FrozenBrush(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
