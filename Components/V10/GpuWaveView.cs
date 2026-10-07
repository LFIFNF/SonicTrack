using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SonicTrack.WaveCore;

internal sealed class GpuWaveView : HwndHost
{
    internal bool RequestedAntialiasing { get; }
    internal bool TransparentOutput { get; set; }
    internal GpuWaveView(bool antialiasing = true) { RequestedAntialiasing = antialiasing; }
    private IntPtr _hwnd, _dc, _context;
    private readonly WaveMesh _mesh = new();
    private readonly uint[] _buffer = new uint[1];
    private GenBuffers? _gen; private BindBuffer? _bind; private BufferData? _upload; private DeleteBuffers? _delete;
    private static readonly WindowProc Proc = DefWindowProc;
    private double _gain = 1, _thickness = 1.5;
    private Color _color = Color.FromRgb(255, 127, 102), _background = Color.FromRgb(16, 18, 23);
    private Color _underlay;
    private bool _showUnderlay;
    private long _lastVersion = long.MinValue;
    private int _lastCount, _lastWidth, _lastHeight;
    private bool _dirty = true;
    internal string Renderer { get; private set; } = "GPU 初始化中";
    internal int Samples { get; private set; }
    internal long FrameCount { get; private set; }
    internal long MeshBuildCount { get; private set; }
    internal long UploadCount { get; private set; }
    internal int LayerDrawCalls { get; private set; }
    internal double BuildMs { get; private set; }
    internal double SubmitMs { get; private set; }
    internal double UploadMs { get; private set; }
    internal double DrawMs { get; private set; }
    internal double SwapMs { get; private set; }
    internal int Points => _mesh.PointCount;
    internal string? Error { get; private set; }

    internal void Apply(Settings settings, Color background)
    {
        _gain = settings.Gain; _thickness = settings.Thickness;
        _color = (Color)ColorConverter.ConvertFromString(settings.Color); _background = background; _dirty = true;
        _underlay = (Color)ColorConverter.ConvertFromString(settings.UnderlayColor); _showUnderlay = settings.ShowUnderlay;
    }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        var instance = GetModuleHandle(null);
        var cls = new WindowClass { Style = 0x23, WindowProc = Marshal.GetFunctionPointerForDelegate(Proc), Instance = instance, ClassName = "SonicTrack.WaveCore.Gpu" };
        if (RegisterClass(ref cls) == 0 && Marshal.GetLastWin32Error() != 1410) throw new Win32Exception();
        _hwnd = CreateWindowEx(0, cls.ClassName, "Wave GPU", 0x56000000, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception();
        try
        {
            _dc = GetDC(_hwnd);
            var pfd = new PixelFormatDescriptor { Size = (ushort)Marshal.SizeOf<PixelFormatDescriptor>(), Version = 1, Flags = 0x25, ColorBits = 32, AlphaBits = 8 };
            int format = RequestedAntialiasing ? ChooseMsaa(_dc, cls.ClassName, instance, pfd) : 0;
            if (format == 0) format = ChoosePixelFormat(_dc, ref pfd);
            if (format == 0) throw new Win32Exception();
            DescribePixelFormat(_dc, format, (uint)Marshal.SizeOf<PixelFormatDescriptor>(), ref pfd);
            if (!SetPixelFormat(_dc, format, ref pfd)) throw new Win32Exception();
            _context = wglCreateContext(_dc);
            if (_context == IntPtr.Zero || !wglMakeCurrent(_dc, _context)) throw new Win32Exception();
            Renderer = Marshal.PtrToStringAnsi(glGetString(0x1F01)) ?? "OpenGL";
            glGetIntegerv(0x80A9, out int samples); Samples = samples;
            if (samples > 1) glEnable(0x809D);
            _gen = Load<GenBuffers>("glGenBuffers"); _bind = Load<BindBuffer>("glBindBuffer");
            _upload = Load<BufferData>("glBufferData"); _delete = Load<DeleteBuffers>("glDeleteBuffers");
            if (_gen != null && _bind != null && _upload != null && _delete != null) _gen(1, _buffer);
            Load<SwapInterval>("wglSwapIntervalEXT")?.Invoke(0);
            glDisable(0x0B71); glDisable(0x0BE2); glDisable(0x0B44);
        }
        catch (Exception ex) { Error = ex.Message; Renderer = "GPU 不可用：" + ex.Message; }
        return new HandleRef(this, _hwnd);
    }
    private static int ChooseMsaa(IntPtr target, string name, IntPtr instance, PixelFormatDescriptor pfd)
    {
        var oldDc = wglGetCurrentDC(); var oldContext = wglGetCurrentContext();
        var window = CreateWindowEx(0, name, "bootstrap", unchecked((int)0x80000000), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (window == IntPtr.Zero) return 0;
        var dc = GetDC(window); IntPtr context = IntPtr.Zero;
        try
        {
            int basic = ChoosePixelFormat(dc, ref pfd);
            if (basic == 0 || !SetPixelFormat(dc, basic, ref pfd)) return 0;
            context = wglCreateContext(dc); if (context == IntPtr.Zero || !wglMakeCurrent(dc, context)) return 0;
            var choose = Load<ChooseFormat>("wglChoosePixelFormatARB"); if (choose == null) return 0;
            foreach (int samples in new[] { 4, 2 })
            {
                int[] attributes = { 0x2001, 1, 0x2010, 1, 0x2011, 1, 0x2013, 0x202B, 0x2014, 32, 0x201B, 8, 0x2041, 1, 0x2042, samples, 0 };
                if (choose(target, attributes, IntPtr.Zero, 1, out int format, out uint count) && count > 0) return format;
            }
            return 0;
        }
        finally
        {
            wglMakeCurrent(oldDc, oldContext); if (context != IntPtr.Zero) wglDeleteContext(context);
            ReleaseDC(window, dc); DestroyWindow(window);
        }
    }
    internal unsafe bool Update(float[] samples, int count, long version, bool present = true)
    {
        if (Error != null || _context == IntPtr.Zero || _hwnd == IntPtr.Zero) return false;
        GetClientRect(_hwnd, out var rect); int width = rect.Right, height = rect.Bottom;
        if (width < 2 || height < 2) return false;
        if (!_dirty && version == _lastVersion && count == _lastCount && width == _lastWidth && height == _lastHeight) return false;
        var dpi = VisualTreeHelper.GetDpi(this);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        _mesh.Build(samples, count, width, height, _gain, _thickness * dpi.DpiScaleX);
        MeshBuildCount++;
        BuildMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        wglMakeCurrent(_dc, _context); glViewport(0, 0, width, height);
        float alpha = TransparentOutput ? _background.A / 255f : 1;
        glClearColor(_background.R / 255f * alpha, _background.G / 255f * alpha, _background.B / 255f * alpha, alpha); glClear(0x4000);
        glMatrixMode(0x1701); glLoadIdentity(); glOrtho(0, width, height, 0, -1, 1);
        glMatrixMode(0x1700); glLoadIdentity(); glColor3f(_color.R / 255f, _color.G / 255f, _color.B / 255f);
        glEnableClientState(0x8074);
        fixed (float* vertices = _mesh.Vertices)
        {
            if (_buffer[0] != 0)
            {
                _bind!(0x8892, _buffer[0]);
                _upload!(0x8892, (IntPtr)(_mesh.VertexCount * 8), (IntPtr)vertices, 0x88E0);
                UploadCount++;
                glVertexPointer(2, 0x1406, 0, IntPtr.Zero);
            }
            else glVertexPointer(2, 0x1406, 0, (IntPtr)vertices);
            UploadMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long drawStart = System.Diagnostics.Stopwatch.GetTimestamp();
            LayerDrawCalls = 1;
            if (_showUnderlay)
            {
                // Same uploaded vertices. The offset is one physical pixel, irrespective of DPI.
                glPushMatrix(); glTranslatef(1, 1, 0);
                glColor3f(_underlay.R / 255f, _underlay.G / 255f, _underlay.B / 255f);
                glDrawArrays(0x0004, 0, _mesh.VertexCount);
                glPopMatrix(); glColor3f(_color.R / 255f, _color.G / 255f, _color.B / 255f);
                LayerDrawCalls++;
            }
            glDrawArrays(0x0004, 0, _mesh.VertexCount);
            DrawMs = System.Diagnostics.Stopwatch.GetElapsedTime(drawStart).TotalMilliseconds;
            if (_buffer[0] != 0) _bind!(0x8892, 0);
        }
        glDisableClientState(0x8074);
        long swapStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (present) SwapBuffers(_dc);
        SwapMs = System.Diagnostics.Stopwatch.GetElapsedTime(swapStart).TotalMilliseconds;
        SubmitMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _lastVersion = version; _lastCount = count; _lastWidth = width; _lastHeight = height; _dirty = false; FrameCount++;
        return true;
    }
    internal void FinishGpu() { wglMakeCurrent(_dc, _context); glFinish(); }
    internal void ReadBackTo(IntPtr pixels, int width, int height)
    {
        wglMakeCurrent(_dc, _context); GetClientRect(_hwnd, out var rect);
        if (rect.Right != width || rect.Bottom != height) throw new InvalidOperationException("Layered surface and GPU viewport size mismatch");
        glReadBuffer(0x0405);
        glReadPixels(0, 0, width, height, 0x80E1, 0x1401, pixels);
    }
    internal BitmapSource Capture()
    {
        wglMakeCurrent(_dc, _context); GetClientRect(_hwnd, out var rect);
        int width = rect.Right, height = rect.Bottom;
        var pixels = new byte[width * height * 4]; var flipped = new byte[pixels.Length];
        glReadBuffer(0x0404); glReadPixels(0, 0, width, height, 0x80E1, 0x1401, pixels);
        for (int y = 0; y < height; y++) Array.Copy(pixels, y * width * 4, flipped, (height - y - 1) * width * 4, width * 4);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, flipped, width * 4); bitmap.Freeze(); return bitmap;
    }
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_context != IntPtr.Zero)
        {
            wglMakeCurrent(_dc, _context);
            if (_buffer[0] != 0) { _delete?.Invoke(1, _buffer); _buffer[0] = 0; }
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); wglDeleteContext(_context);
        }
        if (_dc != IntPtr.Zero) ReleaseDC(_hwnd, _dc);
        DestroyWindow(hwnd.Handle); _hwnd = _dc = _context = IntPtr.Zero;
    }
    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == 0x0F) _dirty = true;
        if (msg == 0x14) { handled = true; return (IntPtr)1; }
        return base.WndProc(hwnd, msg, wp, lp, ref handled);
    }
    private static T? Load<T>(string name) where T : Delegate
    {
        var p = wglGetProcAddress(name);
        return p == IntPtr.Zero || p.ToInt64() is 1 or 2 or 3 or -1 ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Style; public IntPtr WindowProc; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PixelFormatDescriptor
    {
        public ushort Size, Version; public uint Flags;
        public byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift, AlphaBits, AlphaShift,
            AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits, DepthBits, StencilBits, AuxBuffers, LayerType, Reserved;
        public uint LayerMask, VisibleMask, DamageMask;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool ChooseFormat(IntPtr dc, int[] attrs, IntPtr floats, uint max, out int format, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SwapInterval(int interval);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GenBuffers(int count, [Out] uint[] buffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BindBuffer(uint target, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BufferData(uint target, IntPtr size, IntPtr data, uint usage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteBuffers(int count, uint[] buffers);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Rect r);
    [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr dc, ref PixelFormatDescriptor pfd);
    [DllImport("gdi32.dll")] private static extern int DescribePixelFormat(IntPtr dc, int format, uint size, ref PixelFormatDescriptor pfd);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr dc, int format, ref PixelFormatDescriptor pfd);
    [DllImport("gdi32.dll")] private static extern bool SwapBuffers(IntPtr dc);
    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr dc, IntPtr context);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr context);
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentDC();
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr wglGetProcAddress(string name);
    [DllImport("opengl32.dll")] private static extern IntPtr glGetString(uint name);
    [DllImport("opengl32.dll")] private static extern void glGetIntegerv(uint name, out int value);
    [DllImport("opengl32.dll")] private static extern void glEnable(uint state);
    [DllImport("opengl32.dll")] private static extern void glDisable(uint state);
    [DllImport("opengl32.dll")] private static extern void glViewport(int x, int y, int w, int h);
    [DllImport("opengl32.dll")] private static extern void glClearColor(float r, float g, float b, float a);
    [DllImport("opengl32.dll")] private static extern void glClear(uint mask);
    [DllImport("opengl32.dll")] private static extern void glMatrixMode(uint mode);
    [DllImport("opengl32.dll")] private static extern void glLoadIdentity();
    [DllImport("opengl32.dll")] private static extern void glOrtho(double l, double r, double b, double t, double n, double f);
    [DllImport("opengl32.dll")] private static extern void glColor3f(float r, float g, float b);
    [DllImport("opengl32.dll")] private static extern void glEnableClientState(uint state);
    [DllImport("opengl32.dll")] private static extern void glDisableClientState(uint state);
    [DllImport("opengl32.dll")] private static extern void glVertexPointer(int size, uint type, int stride, IntPtr ptr);
    [DllImport("opengl32.dll")] private static extern void glDrawArrays(uint mode, int first, int count);
    [DllImport("opengl32.dll")] private static extern void glPushMatrix();
    [DllImport("opengl32.dll")] private static extern void glPopMatrix();
    [DllImport("opengl32.dll")] private static extern void glTranslatef(float x, float y, float z);
    [DllImport("opengl32.dll")] private static extern void glFinish();
    [DllImport("opengl32.dll")] private static extern void glReadBuffer(uint mode);
    [DllImport("opengl32.dll")] private static extern void glReadPixels(int x, int y, int w, int h, uint format, uint type, byte[] pixels);
    [DllImport("opengl32.dll")] private static extern void glReadPixels(int x, int y, int w, int h, uint format, uint type, IntPtr pixels);
}
