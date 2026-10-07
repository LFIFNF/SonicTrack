using System.IO;
using System.Text.Json;

namespace SonicTrack.WaveCore;

public sealed class Settings
{
    public double Gain { get; set; } = 1;
    public double DurationMs { get; set; } = 85;
    public double Thickness { get; set; } = 1.5;
    public string Color { get; set; } = "#FF7F66";
    public bool Antialiasing { get; set; } = true;
    public int TargetHz { get; set; } = 240;
    public string UnderlayColor { get; set; } = "#222222";
    public bool ShowUnderlay { get; set; }
    public double OverlayWidth { get; set; } = 800;
    public double OverlayHeight { get; set; } = 220;
    public bool Topmost { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool Backplate { get; set; } = true;
    public bool OverlayOpen { get; set; } = true;
    public bool PreviewEnabled { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }
    internal static string PathName => Path.Combine(AppContext.BaseDirectory, "v10-parameters.json");
    internal static Settings Load(string? path = null)
    {
        // Parameters live next to the executable; when the file is missing the defaults are used.
        path ??= PathName;
        try
        {
            var value = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new() : new Settings();
            value.Gain = double.IsFinite(value.Gain) ? Math.Clamp(value.Gain, .2, 12) : 1;
            value.DurationMs = double.IsFinite(value.DurationMs) ? Math.Clamp(value.DurationMs, 20, 200) : 85;
            value.Thickness = double.IsFinite(value.Thickness) ? Math.Clamp(value.Thickness, 1, 8) : 1.5;
            if (value.Color is not ("#FF7F66" or "#51D6E8" or "#FFFFFF" or "#222222")) value.Color = "#FF7F66";
            if (value.TargetHz is not (60 or 120 or 144 or 240)) value.TargetHz = 240;
            if (value.UnderlayColor is not ("#FF7F66" or "#51D6E8" or "#FFFFFF" or "#222222")) value.UnderlayColor = "#222222";
            value.OverlayWidth = double.IsFinite(value.OverlayWidth) ? Math.Clamp(value.OverlayWidth, 320, 1600) : 800;
            value.OverlayHeight = double.IsFinite(value.OverlayHeight) ? Math.Clamp(value.OverlayHeight, 80, 600) : 220;
            if (value.Left.HasValue && !double.IsFinite(value.Left.Value)) value.Left = null;
            if (value.Top.HasValue && !double.IsFinite(value.Top.Value)) value.Top = null;
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save(string? path = null)
    {
        path ??= PathName;
        var payload = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path + ".tmp", payload); File.Move(path + ".tmp", path, true);
    }
}
