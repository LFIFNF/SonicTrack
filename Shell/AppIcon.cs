using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SonicTrack.App.Shell;

/// <summary>Coral sound-bar icon rendered at runtime for the title bars and the taskbar.</summary>
internal static class AppIcon
{
    private static BitmapSource? _image;

    public static BitmapSource Image => _image ??= Render(64);

    /// <summary>Creates a Win32 icon (HICON) of the same artwork for the notification area; caller destroys it.</summary>
    public static IntPtr CreateIconHandle(int size)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(size)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var png = stream.ToArray();
        return CreateIconFromResourceEx(png, png.Length, true, 0x00030000, size, size, 0);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(byte[] data, int size, bool icon, int version, int width, int height, uint flags);

    private static BitmapSource Render(int size)
    {
        var geometry = (Geometry)Application.Current.FindResource("IconAppBars");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(0, 0, size, size), size * .22, size * .22);
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xF2, 0x60, 0x4C)), size * .085)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            double scale = size * .7 / 24, offset = size * .15;
            dc.PushTransform(new TransformGroup { Children = { new ScaleTransform(scale, scale), new TranslateTransform(offset, offset) } });
            dc.DrawGeometry(null, new Pen(pen.Brush, pen.Thickness / scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
