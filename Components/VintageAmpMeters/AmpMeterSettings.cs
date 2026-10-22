using System.IO;
using System.Text.Json;

namespace SonicTrack.VintageAmpMeters;

internal sealed class AmpMeterSettings
{
    public double Gain { get; set; } = 1;
    public double ShadowStrength { get; set; } = 65;
    public double OverlayMeterSize { get; set; } = 250;
    public double OverlaySpacing { get; set; } = 8;
    public bool ShowScaleNumbers { get; set; } = true;
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }

    internal static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "vu-meter-parameters.json");

    private static string LegacySettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SonicTrack", "VintageAmpMeters", "settings.json");

    internal static AmpMeterSettings Load(out string? warning)
    {
        warning = null;
        try
        {
            string? path = File.Exists(SettingsPath) ? SettingsPath
                : File.Exists(LegacySettingsPath) ? LegacySettingsPath : null;
            var settings = path == null
                ? new AmpMeterSettings()
                : JsonSerializer.Deserialize<AmpMeterSettings>(File.ReadAllText(path)) ?? new();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warning = $"参数无法读取，已使用默认值：{ex.Message}";
            global::SonicTrack.App.App.TryLog(ex);
            return new AmpMeterSettings();
        }
    }

    internal void Normalize()
    {
        Gain = Normalize(Gain, 0.1, 10, 1);
        ShadowStrength = Normalize(ShadowStrength, 0, 100, 65);
        OverlayMeterSize = Normalize(OverlayMeterSize, 120, 400, 250);
        OverlaySpacing = Normalize(OverlaySpacing, 0, 80, 8);
        if (OverlayLeft is not double left || !double.IsFinite(left)) OverlayLeft = null;
        if (OverlayTop is not double top || !double.IsFinite(top)) OverlayTop = null;
    }

    internal void Save()
    {
        string tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, SettingsPath, true);
    }

    private static double Normalize(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
