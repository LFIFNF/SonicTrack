using System.IO;
using System.Text.Json;

namespace SonicTrack.V61;

internal sealed class WindowSettings
{
    public WindowSettings() { }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
    public double PreviewOffsetX { get; set; }
    public double PreviewOffsetY { get; set; }

    internal static WindowSettings? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var settings = JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(path));
        return settings != null && double.IsFinite(settings.Width) && settings.Width > 0 &&
            double.IsFinite(settings.Height) && settings.Height > 0 ? settings : null;
    }

    internal void Save(string path)
    {
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
