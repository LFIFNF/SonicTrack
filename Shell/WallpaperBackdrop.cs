using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SonicTrack.App.Shell;

/// <summary>
/// Windows 10 has no system backdrop blur for this kind of window, so the launcher imitates
/// frosted glass with a small, blurred copy of the desktop wallpaper aligned to the screen.
/// </summary>
internal static class WallpaperBackdrop
{
    public static ImageSource? Load()
    {
        try
        {
            using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string? path = desktop?.GetValue("WallPaper") as string;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Themes\TranscodedWallpaper");
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            using (var stream = File.OpenRead(path))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 480;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            return BlurOnce(image);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or InvalidOperationException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // Blur at decoded wallpaper resolution once, rather than running a full-window
    // Gaussian effect every time the screen-aligned brush moves.
    private static BitmapSource BlurOnce(BitmapSource image)
    {
        int width = image.PixelWidth, height = image.PixelHeight;
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        var input = new byte[width * height * 4]; var temp = new byte[input.Length]; var output = new byte[input.Length];
        converted.CopyPixels(input, width * 4, 0);
        int radius = Math.Clamp((int)Math.Ceiling(70 * width / SystemParameters.PrimaryScreenWidth), 1, 48);
        double sigma = radius / 3.0, sum = 0;
        var kernel = new double[radius * 2 + 1];
        for (int k = -radius; k <= radius; k++) { double weight = Math.Exp(-k * k / (2 * sigma * sigma)); kernel[k + radius] = weight; sum += weight; }
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        for (int channel = 0; channel < 4; channel++)
        {
            double value = 0;
            for (int k = -radius; k <= radius; k++) value += input[(y * width + Math.Clamp(x + k, 0, width - 1)) * 4 + channel] * kernel[k + radius];
            temp[(y * width + x) * 4 + channel] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
        }
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        for (int channel = 0; channel < 4; channel++)
        {
            double value = 0;
            for (int k = -radius; k <= radius; k++) value += temp[(Math.Clamp(y + k, 0, height - 1) * width + x) * 4 + channel] * kernel[k + radius];
            output[(y * width + x) * 4 + channel] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, output, width * 4);
        bitmap.Freeze(); return bitmap;
    }

    /// <summary>Aligns a wallpaper brush so that it lines up with the primary screen behind <paramref name="target"/>.</summary>
    public static void Align(FrameworkElement target, ImageBrush brush)
    {
        var source = PresentationSource.FromVisual(target);
        if (source?.CompositionTarget == null) return;
        Point origin = source.CompositionTarget.TransformFromDevice.Transform(target.PointToScreen(new Point(0, 0)));
        brush.Viewport = new Rect(-origin.X, -origin.Y, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    }
}
