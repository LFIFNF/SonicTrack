using System.IO;
using System.Text.Json;

namespace SonicTrack.V61;

internal sealed class ParameterSettings
{
    public ParameterSettings() { }
    public Dictionary<string, double> Sliders { get; set; } = new();
    public bool DesktopTopmost { get; set; }
    public bool DesktopClickThrough { get; set; }

    internal static ParameterSettings? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var settings = JsonSerializer.Deserialize<ParameterSettings>(File.ReadAllText(path));
        if (settings?.Sliders == null) throw new InvalidDataException("参数文件格式不正确。");
        return settings;
    }

    internal void Save(string path)
    {
        // Replace only after the complete new file has been written.
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
