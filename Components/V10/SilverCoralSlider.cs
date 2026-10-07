using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using SonicTrack.App.Shell;

namespace SonicTrack.WaveCore;

/// <summary>Horizontal-only silver rail and coral thumb, with native Slider semantics.</summary>
public sealed class SilverCoralSlider : Slider
{
    public static readonly DependencyProperty LightSurfaceProperty = DependencyProperty.Register(
        nameof(LightSurface), typeof(bool), typeof(SilverCoralSlider),
        new FrameworkPropertyMetadata(false, OnSurfaceChanged));

    public static readonly DependencyProperty GlassSurfaceProperty = DependencyProperty.Register(
        nameof(GlassSurface), typeof(bool), typeof(SilverCoralSlider),
        new FrameworkPropertyMetadata(false, OnSurfaceChanged));

    public bool GlassSurface
    {
        get => (bool)GetValue(GlassSurfaceProperty);
        set => SetValue(GlassSurfaceProperty, value);
    }

    public bool LightSurface
    {
        get => (bool)GetValue(LightSurfaceProperty);
        set => SetValue(LightSurfaceProperty, value);
    }

    static SilverCoralSlider()
    {
        // Integrated app: rail and thumb size come from the developer options (SliderAppearance).
        // A fixed Height set by a window keeps its spacing; it grows or shrinks with the thumb.
        HeightProperty.OverrideMetadata(typeof(SilverCoralSlider), new FrameworkPropertyMetadata(
            double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceHeight));
    }

    private static object CoerceHeight(DependencyObject d, object value)
    {
        if (value is not double height || double.IsNaN(height)) return value;
        var look = SliderAppearance.Current;
        return Math.Max(Math.Max(look.ThumbSize, look.RailHeight), height + look.LayoutExtra);
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e) => CoerceValue(HeightProperty);

    public SilverCoralSlider()
    {
        Loaded += (_, _) =>
        {
            SliderAppearance.Current.PropertyChanged -= OnAppearanceChanged;
            SliderAppearance.Current.PropertyChanged += OnAppearanceChanged;
            CoerceValue(HeightProperty);
        };
        Unloaded += (_, _) => SliderAppearance.Current.PropertyChanged -= OnAppearanceChanged;
        Minimum = 0;
        Maximum = 100;
        SmallChange = 1;
        LargeChange = 10;
        IsMoveToPointEnabled = true;
        FocusVisualStyle = null;
        Orientation = Orientation.Horizontal;
        Template = BuildTemplate(false, false);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        Focus();
        // Capture the native Thumb even for a rail click, allowing click-then-drag.
        if (e.OriginalSource is DependencyObject source && IsInsideThumb(source)) return;
        var thumbSize = SliderAppearance.Current.ThumbSize;
        var width = ActualWidth - thumbSize;
        if (width <= 0) return;
        var fraction = Math.Clamp((e.GetPosition(this).X - thumbSize / 2) / width, 0, 1);
        SetCurrentValue(ValueProperty, Minimum + fraction * (Maximum - Minimum));
        if (Template.FindName("PART_Track", this) is Track track)
        {
            track.Thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
            {
                RoutedEvent = Mouse.MouseDownEvent,
                Source = track.Thumb
            });
        }
        e.Handled = true;
    }

    private static bool IsInsideThumb(DependencyObject source)
    {
        for (DependencyObject? current = source; current != null; current = VisualTreeHelper.GetParent(current))
            if (current is Thumb) return true;
        return false;
    }

    private static void OnSurfaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (SilverCoralSlider)d;
        slider.Template = BuildTemplate(slider.LightSurface, slider.GlassSurface);
    }

    private static Brush Gradient(params (string color, double offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (color, offset) in stops)
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(color), offset));
        brush.Freeze();
        return brush;
    }


    private static void Bind(FrameworkElementFactory element, DependencyProperty property, string path)
        => element.SetBinding(property, new Binding(path) { Source = SliderAppearance.Current, Mode = BindingMode.OneWay });

    private static ControlTemplate BuildTemplate(bool light, bool glass)
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(BackgroundProperty, Brushes.Transparent);
        var rail = new FrameworkElementFactory(typeof(Border));
        Bind(rail, HeightProperty, nameof(SliderAppearance.RailHeight));
        Bind(rail, MarginProperty, nameof(SliderAppearance.RailMargin));
        rail.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        Bind(rail, Border.CornerRadiusProperty, nameof(SliderAppearance.RailCornerRadius));
        rail.SetValue(Border.BorderThicknessProperty, new Thickness(light ? 1 : 1.8));
        rail.SetValue(Border.BorderBrushProperty, light ? Gradient(("#ABABAB", 0), ("#D0D0D0", 1)) : Brushes.White);
        rail.SetValue(Border.BackgroundProperty, light
            ? Gradient(("#E8E8E8", 0), ("#FCFCFC", .52), ("#F5F5F5", 1))
            : glass ? Gradient(("#AAC5C6C6", 0), ("#B0D0D1D1", .48), ("#B8DADADA", 1))
            : Gradient(("#929393", 0), ("#A0A1A1", .48), ("#B3B4B4", 1)));
        // Shadow strength comes from the developer options (100 % = the original shadow).
        Bind(rail, EffectProperty, light ? nameof(SliderAppearance.RailShadowLight) : nameof(SliderAppearance.RailShadowDark));
        root.AppendChild(rail);

        var track = new FrameworkElementFactory(typeof(SliderTrack), "PART_Track");
        Bind(track, HeightProperty, nameof(SliderAppearance.ThumbSize));
        track.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        track.SetValue(Track.OrientationProperty, Orientation.Horizontal);
        // Track automatically binds Value, Minimum and Maximum to its Slider parent.
        var thumb = new FrameworkElementFactory(typeof(Thumb));
        Bind(thumb, WidthProperty, nameof(SliderAppearance.ThumbSize));
        Bind(thumb, HeightProperty, nameof(SliderAppearance.ThumbSize));
        thumb.SetValue(CursorProperty, Cursors.Hand);
        var thumbVisual = new FrameworkElementFactory(typeof(Grid));
        // A shadow-only gradient has no opaque silhouette under the antialiased disc edge.
        var silhouette = new FrameworkElementFactory(typeof(Ellipse));
        Bind(silhouette, Shape.FillProperty, nameof(SliderAppearance.ThumbShadowBrush));
        Bind(silhouette, WidthProperty, nameof(SliderAppearance.ShadowSize));
        Bind(silhouette, HeightProperty, nameof(SliderAppearance.ShadowSize));
        Bind(silhouette, MarginProperty, nameof(SliderAppearance.ShadowMargin));
        Bind(silhouette, RenderTransformProperty, nameof(SliderAppearance.ShadowTransform));
        silhouette.SetValue(IsHitTestVisibleProperty, false);
        thumbVisual.AppendChild(silhouette);
        var disc = new FrameworkElementFactory(typeof(Ellipse), "Disc");
        disc.SetValue(Shape.FillProperty, Gradient(("#FF9A82", 0), ("#FF8A72", .42), ("#FF856C", 1)));
        disc.SetValue(Shape.StrokeProperty, Gradient(("#FFC0AF", 0), ("#EA7661", 1)));
        disc.SetValue(Shape.StrokeThicknessProperty, 1.0);
        thumbVisual.AppendChild(disc);
        thumb.SetValue(TemplateProperty, new ControlTemplate(typeof(Thumb)) { VisualTree = thumbVisual });
        track.AppendChild(thumb);
        root.AppendChild(track);

        var template = new ControlTemplate(typeof(SilverCoralSlider)) { VisualTree = root };
        return template;
    }
}

// FrameworkElementFactory requires IAddChild for a property-backed child.
public sealed class SliderTrack : Track, IAddChild
{
    public void AddChild(object value) => Thumb = (Thumb)value;
    public void AddText(string text) { }
}


