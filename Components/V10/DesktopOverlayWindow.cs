using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SonicTrack.WaveCore;

/// <summary>Native layered presentation. WPF supplies only an invisible GL host, never the overlay image.</summary>
internal sealed class DesktopOverlayWindow : IDisposable
{
    private static readonly Dictionary<IntPtr, DesktopOverlayWindow> Windows = new();
    private static readonly WindowProc Proc = Dispatch;
    private readonly HwndSource _host;
    private IntPtr _hwnd, _memoryDc, _bitmap, _originalBitmap, _pixels;
    private int _width, _height;
    private bool _disposed, _shown, _clickThrough;
    private double _left, _top;
    internal GpuWaveView View { get; private set; }
    internal long PresentCount { get; private set; }
    internal double ReadbackMs { get; private set; }
    internal double PresentMs { get; private set; }
    internal double Left { get { if (!_disposed && GetWindowRect(_hwnd, out var r)) _left = r.Left; return _left; } }
    internal double Top { get { if (!_disposed && GetWindowRect(_hwnd, out var r)) _top = r.Top; return _top; } }
    internal IntPtr Handle => _hwnd;
    internal event Action? PositionSettled;
    internal event Action? Closed;

    internal DesktopOverlayWindow(Settings settings)
    {
        var instance = GetModuleHandle(null);
        var cls = new WindowClass { Instance = instance, ClassName = "SonicTrack.GpuDesktopWave", WindowProc = Marshal.GetFunctionPointerForDelegate(Proc) };
        if (RegisterClass(ref cls) == 0 && Marshal.GetLastWin32Error() != 1410) throw new Win32Exception();
        SystemParametersInfo(0x30, 0, out Rect work, 0);
        int width = (int)settings.OverlayWidth, height = (int)settings.OverlayHeight;
        int x = (int)(settings.Left ?? work.Left + (work.Right - work.Left - width) / 2.0);
        int y = (int)(settings.Top ?? work.Top + (work.Bottom - work.Top) * .7);
        var requested = new Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(MonitorFromRect(ref requested, 2), ref monitor))
        {
            x = Math.Clamp(x, monitor.Work.Left, Math.Max(monitor.Work.Left, monitor.Work.Right - width));
            y = Math.Clamp(y, monitor.Work.Top, Math.Max(monitor.Work.Top, monitor.Work.Bottom - height));
        }
        _hwnd = CreateWindowEx(0x08080080, cls.ClassName, "SonicTrack 桌面声波", unchecked((int)0x80000000),
            x, y, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception();
        Windows.Add(_hwnd, this);
        _left = x; _top = y;
        _host = new HwndSource(new HwndSourceParameters("SonicTrack waveform GPU host")
        { WindowStyle = unchecked((int)0x80000000), Width = width, Height = height });
        View = new GpuWaveView(settings.Antialiasing) { TransparentOutput = true };
        _host.RootVisual = View;
        _memoryDc = CreateCompatibleDC(IntPtr.Zero);
        try { Apply(settings); }
        catch { Dispose(); throw; }
    }
    internal static Color PlateColor(Settings settings)
    {
        if (!settings.Backplate) return Colors.Transparent;
        var foreground = (Color)ColorConverter.ConvertFromString(settings.Color);
        double luminance = (.2126 * foreground.R + .7152 * foreground.G + .0722 * foreground.B) / 255;
        // A dark waveform gets a light protective surface; a light waveform gets a dark one.
        return luminance < .4 ? Color.FromArgb(160, 245, 245, 245) : Color.FromArgb(160, 16, 18, 23);
    }
    internal void Apply(Settings settings)
    {
        if (_disposed) return;
        if (_memoryDc == IntPtr.Zero) throw new Win32Exception();
        if (View.RequestedAntialiasing != settings.Antialiasing)
        {
            _host.RootVisual = null; View.Dispose();
            View = new GpuWaveView(settings.Antialiasing) { TransparentOutput = true };
            _host.RootVisual = View;
            _width = _height = 0;
        }
        int width = (int)settings.OverlayWidth, height = (int)settings.OverlayHeight;
        if (_width != width || _height != height)
        {
            SetWindowPos(_host.Handle, IntPtr.Zero, 0, 0, width, height, 0x16);
            var dpi = VisualTreeHelper.GetDpi(View);
            View.Width = width / dpi.DpiScaleX; View.Height = height / dpi.DpiScaleY;
            View.Measure(new Size(View.Width, View.Height)); View.Arrange(new System.Windows.Rect(0, 0, View.Width, View.Height)); View.UpdateLayout();
            CreateSurface(width, height);
        }
        if (View.Error != null) throw new InvalidOperationException(View.Error);
        _clickThrough = settings.ClickThrough;
        int style = 0x08080080 | (_clickThrough ? 0x20 : 0);
        SetWindowLong(_hwnd, -20, style);
        SetWindowPos(_hwnd, settings.Topmost ? (IntPtr)(-1) : (IntPtr)(-2), 0, 0, width, height, 0x12);
        View.Apply(settings, PlateColor(settings));
    }
    private void CreateSurface(int width, int height)
    {
        if (_bitmap != IntPtr.Zero) { SelectObject(_memoryDc, _originalBitmap); DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
        var info = new BitmapInfo { Size = 40, Width = width, Height = height, Planes = 1, Bits = 32 };
        _bitmap = CreateDIBSection(_memoryDc, ref info, 0, out _pixels, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero || _pixels == IntPtr.Zero) throw new Win32Exception();
        _originalBitmap = SelectObject(_memoryDc, _bitmap);
        _width = width; _height = height;
    }
    internal bool Update(float[] samples, int count, long version)
    {
        if (_disposed || !View.Update(samples, count, version, present: false)) return false;
        long start = Stopwatch.GetTimestamp();
        // OpenGL and this DIB both use bottom-up BGRA: direct write, no managed copy/flip/bitmap creation.
        // Resolved MSAA coverage and the clear color already provide premultiplied alpha.
        View.ReadBackTo(_pixels, _width, _height);
        ReadbackMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = Stopwatch.GetTimestamp();
        GetWindowRect(_hwnd, out var rect);
        var destination = new PointI { X = rect.Left, Y = rect.Top };
        var source = new PointI(); var size = new SizeI { Width = _width, Height = _height };
        var blend = new Blend { SourceConstantAlpha = 255, AlphaFormat = 1 };
        if (!UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref destination, ref size, _memoryDc, ref source, 0, ref blend, 2)) throw new Win32Exception();
        PresentMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        PresentCount++;
        if (!_shown) { ShowWindow(_hwnd, 8); _shown = true; }
        return true;
    }
    internal BitmapSource Capture()
    {
        var bytes = new byte[_width * _height * 4];
        for (int y = 0; y < _height; y++) Marshal.Copy(_pixels + (_height - y - 1) * _width * 4, bytes, y * _width * 4, _width * 4);
        var image = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Pbgra32, null, bytes, _width * 4); image.Freeze(); return image;
    }
    private static IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        if (Windows.TryGetValue(hwnd, out var window))
        {
            if (msg == 0x84) return window._clickThrough ? (IntPtr)(-1) : (IntPtr)2; // transparent or drag caption
            if (msg == 0x232) { window.PositionSettled?.Invoke(); return IntPtr.Zero; }
            if (msg is 0xA5 or 0x205)
            {
                GetCursorPos(out var position); var menu = CreatePopupMenu();
                try
                {
                    AppendMenu(menu, 0, (UIntPtr)1, "关闭桌面声波");
                    if (TrackPopupMenu(menu, 0x180, position.X, position.Y, 0, hwnd, IntPtr.Zero) == 1) window.Dispose();
                }
                finally { DestroyMenu(menu); }
                return IntPtr.Zero;
            }
            if (msg == 0x10) { window.Dispose(); return IntPtr.Zero; }
        }
        return DefWindowProc(hwnd, msg, wp, lp);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _left = Left; _top = Top;
        _disposed = true;
        View.Dispose(); _host.RootVisual = null; _host.Dispose();
        if (_bitmap != IntPtr.Zero) { SelectObject(_memoryDc, _originalBitmap); DeleteObject(_bitmap); }
        if (_memoryDc != IntPtr.Zero) DeleteDC(_memoryDc);
        Windows.Remove(_hwnd); DestroyWindow(_hwnd);
        _pixels = _bitmap = _memoryDc = IntPtr.Zero;
        // Cached screen position remains available to the close/save handler after destroying the HWND.
        Closed?.Invoke(); _hwnd = IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SizeI { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Op, Flags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Style; public IntPtr WindowProc; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screenDc, ref PointI dest, ref SizeI size, IntPtr sourceDc, ref PointI src, uint key, ref Blend blend, uint flags);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, out Rect value, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointI position);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
