using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace SonicTrack.App.Shell;

/// <summary>
/// Shared size of every silver-rail / coral-thumb slider (SilverCoralSlider) in the app.
/// Slider templates bind to this object, so a change applies immediately to all open windows.
/// </summary>
public sealed class SliderAppearance : INotifyPropertyChanged
{
    public const double DefaultRailHeight = 8, MinRailHeight = 8, MaxRailHeight = 36;
    public const double DefaultThumbSize = 24, MinThumbSize = 24, MaxThumbSize = 64;
    /// <summary>Shadow strength in percent of the original slider shadow (0 = no shadow).</summary>
    public const double DefaultShadowStrength = 40, MinShadowStrength = 0, MaxShadowStrength = 200;
    /// <summary>Thumb size the component windows' fixed slider heights were laid out for.</summary>
    private const double ReferenceThumbSize = 40;

    public static SliderAppearance Current { get; } = new();

    private double _railHeight = DefaultRailHeight;
    private double _thumbSize = DefaultThumbSize;
    private double _shadowStrength = DefaultShadowStrength;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Thickness (height) of the white capsule rail.</summary>
    public double RailHeight
    {
        get => _railHeight;
        set
        {
            double v = Normalize(value, MinRailHeight, MaxRailHeight, DefaultRailHeight);
            if (v == _railHeight) return;
            _railHeight = v;
            Raise();
        }
    }

    /// <summary>Diameter of the coral thumb.</summary>
    public double ThumbSize
    {
        get => _thumbSize;
        set
        {
            double v = Normalize(value, MinThumbSize, MaxThumbSize, DefaultThumbSize);
            if (v == _thumbSize) return;
            _thumbSize = v;
            Raise();
        }
    }

    /// <summary>Strength of the rail and thumb shadows, 0-200 % of the original look.</summary>
    public double ShadowStrength
    {
        get => _shadowStrength;
        set
        {
            double v = Normalize(value, MinShadowStrength, MaxShadowStrength, DefaultShadowStrength);
            if (v == _shadowStrength) return;
            _shadowStrength = v;
            Raise();
        }
    }

    public bool IsDefault => _railHeight == DefaultRailHeight && _thumbSize == DefaultThumbSize
        && _shadowStrength == DefaultShadowStrength;

    // Values derived for the slider template (the original template used rail 22, thumb 40,
    // rail inset 20 = half the thumb, and a 66 px soft shadow offset 8 px downward).
    public CornerRadius RailCornerRadius => new(_railHeight / 2);
    public Thickness RailMargin => new(_thumbSize / 2, 0, _thumbSize / 2, 0);
    public double ShadowSize => _thumbSize * 66.0 / 40.0;
    public Thickness ShadowMargin => new(-(ShadowSize - _thumbSize) / 2);
    public Transform ShadowTransform
    {
        get
        {
            var t = new TranslateTransform(0, _thumbSize * 8.0 / 40.0);
            t.Freeze();
            return t;
        }
    }

    /// <summary>Extra layout height a slider needs compared with the original 40 px thumb.</summary>
    public double LayoutExtra => Math.Max(_thumbSize, _railHeight) - ReferenceThumbSize;

    private double ShadowFactor => _shadowStrength / 100.0;

    // Original shadows (strength 100 %): rail drop shadow 0.22 / blur 16 / depth 9 on light
    // surfaces and 0.20 / 7 / 3 otherwise; thumb soft radial shadow with alpha 95-85-48-13-0.
    public Effect? RailShadowLight => RailShadow(.22, 16, 9);
    public Effect? RailShadowDark => RailShadow(.20, 7, 3);

    public Brush ThumbShadowBrush
    {
        get
        {
            var brush = new RadialGradientBrush
            {
                Center = new Point(.5, .5), GradientOrigin = new Point(.5, .5),
                RadiusX = .5, RadiusY = .5
            };
            foreach (var (alpha, offset) in new[] { (95, 0.0), (85, .40), (48, .62), (13, .82), (0, 1.0) })
            {
                byte a = (byte)Math.Clamp(Math.Round(alpha * ShadowFactor), 0, 255);
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(a, 73, 37, 27), offset));
            }
            brush.Freeze();
            return brush;
        }
    }

    private Effect? RailShadow(double opacity, double blur, double depth)
    {
        if (_shadowStrength <= 0) return null;
        var effect = new DropShadowEffect
        {
            Color = Colors.Black, Opacity = Math.Min(1, opacity * ShadowFactor),
            BlurRadius = blur, ShadowDepth = depth, Direction = 270, RenderingBias = RenderingBias.Quality
        };
        effect.Freeze();
        return effect;
    }

    public void Reset()
    {
        _railHeight = DefaultRailHeight;
        _thumbSize = DefaultThumbSize;
        _shadowStrength = DefaultShadowStrength;
        Raise();
    }

    internal void Set(double railHeight, double thumbSize, double shadowStrength)
    {
        _railHeight = Normalize(railHeight, MinRailHeight, MaxRailHeight, DefaultRailHeight);
        _thumbSize = Normalize(thumbSize, MinThumbSize, MaxThumbSize, DefaultThumbSize);
        _shadowStrength = Normalize(shadowStrength, MinShadowStrength, MaxShadowStrength, DefaultShadowStrength);
        Raise();
    }

    private static double Normalize(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(Math.Round(value), min, max) : fallback;

    // Empty name refreshes every binding to this object (all derived values change together).
    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>Launcher-level preferences, stored in app-settings.json next to the executable.</summary>
public sealed class AppPreferences
{
    public bool DeveloperOptions { get; set; }
    public double SliderRailHeight { get; set; } = SliderAppearance.DefaultRailHeight;
    public double SliderThumbSize { get; set; } = SliderAppearance.DefaultThumbSize;
    public double SliderShadow { get; set; } = SliderAppearance.DefaultShadowStrength;

    public static AppPreferences Current { get; private set; } = new();
    internal static string PathName => Path.Combine(AppContext.BaseDirectory, "app-settings.json");

    private static DispatcherTimer? _saveTimer;

    /// <summary>Loads the file (if any), applies slider sizes and starts saving size changes.</summary>
    public static void Initialize()
    {
        try
        {
            if (File.Exists(PathName))
                Current = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(PathName)) ?? new();
        }
        catch (Exception ex)
        {
            App.TryLog(ex);
            Current = new();
        }
        SliderAppearance.Current.Set(Current.SliderRailHeight, Current.SliderThumbSize, Current.SliderShadow);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        SliderAppearance.Current.PropertyChanged += (_, _) =>
        {
            Current.SliderRailHeight = SliderAppearance.Current.RailHeight;
            Current.SliderThumbSize = SliderAppearance.Current.ThumbSize;
            Current.SliderShadow = SliderAppearance.Current.ShadowStrength;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
    }

    /// <summary>Writes pending changes now (used on exit).</summary>
    public static void Flush()
    {
        if (_saveTimer?.IsEnabled == true)
        {
            _saveTimer.Stop();
            Save();
        }
    }

    public static void Save()
    {
        string temporary = PathName + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, PathName, true);
        }
        catch (Exception ex)
        {
            App.TryLog(ex);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
