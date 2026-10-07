using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace SonicTrack.V61;

/// <summary>Own child window and OpenGL context; one indexed draw for the surface.</summary>
internal sealed class NativeSurfaceView : HwndHost
{
    private IntPtr _hwnd, _dc, _context;
    private bool _dragging;
    private int _dragX, _dragY;
    internal event Action<double, double>? PanRequested;
    internal System.Windows.Point PreviewOffset { get; private set; }
    private System.Windows.Size _framingSize = System.Windows.Size.Empty;
    internal void SetFramingSize(System.Windows.Size size)
    {
        if (size.IsEmpty || !double.IsFinite(size.Width) || !double.IsFinite(size.Height) ||
            size.Width <= 0 || size.Height <= 0 || _framingSize == size) return;
        _framingSize = size;
        NeedsRedraw = true;
    }
    internal void SetPreviewOffset(System.Windows.Point offset)
    {
        if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y) || PreviewOffset == offset) return;
        PreviewOffset = offset;
        NeedsRedraw = true;
    }
    private float[] _vertices = Array.Empty<float>(), _normals = Array.Empty<float>(), _colors = Array.Empty<float>();
    internal const float InteriorBlackAlpha = .35f;
    private float _interiorBlackOpacity = InteriorBlackAlpha;
    public float InteriorBlackOpacity
    {
        get => _interiorBlackOpacity;
        set
        {
            float opacity = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : InteriorBlackAlpha;
            if (_interiorBlackOpacity == opacity) return;
            _interiorBlackOpacity = opacity;
            NeedsRedraw = true;
        }
    }
    private int[] _indices = Array.Empty<int>(), _gapIndices = Array.Empty<int>();
    private bool[] _gapCells = Array.Empty<bool>();
    private int _gapDrawIndexCount;
    private byte[] _faceMasks = Array.Empty<byte>();
    private float[] _cellX = Array.Empty<float>();
    private Point3D _eye = new(0, 15, 10);
    private readonly double[] _cameraMatrix = new double[16];
    private static readonly float[] LightDirection = { .3f, 1, .2f, 0 };
    private Vector3D _look = new(0, -15, -10), _up = new(0, 1, 0);
    private static readonly WindowProc DefaultProc = DefWindowProc;
    private GenerateBuffers? _generateBuffers;
    private BindBuffer? _bindBuffer;
    private BufferData? _bufferData;
    private DeleteBuffers? _deleteBuffers;
    private readonly uint[] _buffers = new uint[5];
    private bool _buffersDirty = true;
    private bool _normalsDirty = true;
    public string Renderer { get; private set; } = "初始化中";
    public int AntialiasingSamples { get; private set; }
    public bool TransparentBackground { get; set; }
    public WriteableBitmap? TransparentFrame { get; private set; }
    private byte[] _readback = Array.Empty<byte>(), _flipped = Array.Empty<byte>();

    public void SetCamera(Point3D eye, Vector3D look, Vector3D up) { _eye = eye; _look = look; _up = up; }
    public void SetTopology(int[] indices, int count)
    {
        _buffersDirty = _normalsDirty = true;
        _indices = indices;
        _vertices = new float[count * 3]; _normals = new float[count * 3]; _colors = new float[count * 3];
    }
    public void SetVertex(int i, Point3D p, Vector3D n, Color c)
    {
        _buffersDirty = _normalsDirty = true;
        int j = i * 3;
        _vertices[j] = (float)p.X; _vertices[j + 1] = (float)p.Y; _vertices[j + 2] = (float)p.Z;
        _normals[j] = (float)n.X; _normals[j + 1] = (float)n.Y; _normals[j + 2] = (float)n.Z;
        _colors[j] = c.R / 255f; _colors[j + 1] = c.G / 255f; _colors[j + 2] = c.B / 255f;
    }

    private int _cellCount;
    private int _drawIndexCount;
    private double _scrollOffset;
    private readonly double[] LeftClip = { 1, 0, 0, 4.1 };
    private readonly double[] RightClip = { -1, 0, 0, 4.1 };
    public long FrameCount { get; private set; }
    public bool NeedsRedraw { get; private set; } = true;

    public void SetPlotBounds(double left, double right)
    {
        if (LeftClip[3] == -left && RightClip[3] == right) return;
        LeftClip[3] = -left;
        RightClip[3] = right;
        NeedsRedraw = true;
    }

    public void SetScrollOffset(double offset)
    {
        if (Math.Abs(offset - _scrollOffset) < 1e-9) return;
        _scrollOffset = offset;
        NeedsRedraw = true;
    }

    protected override void OnRenderSizeChanged(System.Windows.SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        NeedsRedraw = true;
    }

    public void BeginCells()
    {
        _drawIndexCount = _gapDrawIndexCount = 0;
        _buffersDirty = true;
    }

    private void AddQuad(int cell, int quad, bool gap = false)
    {
        int b = cell * 20 + quad * 4;
        // Counter-clockwise faces viewed from outside the pillar.
        bool reverse = quad < 3;
        var indices = gap ? _gapIndices : _indices;
        int offset = gap ? _gapDrawIndexCount : _drawIndexCount;
        indices[offset++] = b;
        indices[offset++] = b + (reverse ? 2 : 1);
        indices[offset++] = b + (reverse ? 1 : 2);
        indices[offset++] = b;
        indices[offset++] = b + (reverse ? 3 : 2);
        indices[offset++] = b + (reverse ? 2 : 3);
        if (gap) _gapDrawIndexCount = offset; else _drawIndexCount = offset;
    }

    /// <summary>
    /// One flat top plus four side quads per cell. Vertices are not shared,
    /// so neighboring cells never interpolate height or color.
    /// </summary>
    public void EnsureCells(int count)
    {
        if (count < 1) count = 1;
        const int vertsPerCell = 20;
        const int indicesPerCell = 30;
        if (count == _cellCount && _vertices.Length == count * vertsPerCell * 3 && _indices.Length == count * indicesPerCell)
            return;
        _cellCount = count;
        _faceMasks = new byte[count];
        _gapCells = new bool[count];
        _gapIndices = new int[count * indicesPerCell];
        _cellX = new float[count * 20];
        _buffersDirty = _normalsDirty = true;
        _vertices = new float[count * vertsPerCell * 3];
        _normals = new float[count * vertsPerCell * 3];
        _colors = new float[count * vertsPerCell * 3];
        _indices = new int[count * indicesPerCell];
        for (int i = 0; i < count; i++)
        {
            int v = i * vertsPerCell;
            int o = i * indicesPerCell;
            for (int q = 0; q < 5; q++)
            {
                int b = v + q * 4;
                _indices[o++] = b; _indices[o++] = b + 1; _indices[o++] = b + 2;
                _indices[o++] = b; _indices[o++] = b + 2; _indices[o++] = b + 3;
            }
            WriteQuadNormal(i, 0, 0f, 1f, 0f);
            WriteQuadNormal(i, 1, 1f, 0f, 0f);
            WriteQuadNormal(i, 2, -1f, 0f, 0f);
            WriteQuadNormal(i, 3, 0f, 0f, 1f);
            WriteQuadNormal(i, 4, 0f, 0f, -1f);
        }
    }

    /// <summary>
    /// Footprints abut. A side quad is drawn only when this cell is strictly taller
    /// than that neighbor (or the floor, when there is no neighbor); otherwise it is degenerate.
    /// </summary>
    public void SetCell(int i, float x0, float x1, float z0, float z1, float y,
        float yPosX, float yNegX, float yPosZ, float yNegZ, float r, float g, float b, float alpha = 1)
    {
        CacheCellX(i, x0, x1);
        _gapCells[i] = TransparentBackground && alpha > 0 && alpha < 1;
        if (TransparentBackground && alpha <= 0)
        {
            _faceMasks[i] = 0;
            return;
        }
        int j = i * 60;
        WriteFlat(j, r, g, b);
        WritePositions(j, x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1);
        WriteSide(j + 12, y, yPosX, x1, z0, x1, z1, x1, z1, x1, z0);
        WriteSide(j + 24, y, yNegX, x0, z1, x0, z0, x0, z0, x0, z1);
        WriteSide(j + 36, y, yPosZ, x0, z1, x1, z1, x1, z1, x0, z1);
        WriteSide(j + 48, y, yNegZ, x1, z0, x0, z0, x0, z0, x1, z0);
        _faceMasks[i] = (byte)(1 | (y > yPosX ? 2 : 0) | (y > yNegX ? 4 : 0) |
            (y > yPosZ ? 8 : 0) | (y > yNegZ ? 16 : 0));
        ReuseCell(i);
    }

    public void ShiftCells(int cells)
    {
        int remaining = _cellCount - cells;
        Array.Copy(_vertices, 0, _vertices, cells * 60, remaining * 60);
        Array.Copy(_colors, 0, _colors, cells * 60, remaining * 60);
        Array.Copy(_faceMasks, 0, _faceMasks, cells, remaining);
        Array.Copy(_gapCells, 0, _gapCells, cells, remaining);
        // Restore canonical coordinates rather than subtracting repeatedly: no float drift.
        for (int i = cells * 20; i < _cellX.Length; i++) _vertices[i * 3] = _cellX[i];
    }

    public void ReuseCell(int cell)
    {
        int mask = _faceMasks[cell];
        for (int face = 0; face < 5; face++)
            if ((mask & (1 << face)) != 0) AddQuad(cell, face, _gapCells[cell]);
    }

    private void CacheCellX(int cell, float left, float right)
    {
        int j = cell * 20;
        _cellX[j]=left; _cellX[j+1]=right; _cellX[j+2]=right; _cellX[j+3]=left;
        _cellX[j+4]=_cellX[j+5]=_cellX[j+6]=_cellX[j+7]=right;
        _cellX[j+8]=_cellX[j+9]=_cellX[j+10]=_cellX[j+11]=left;
        _cellX[j+12]=left; _cellX[j+13]=right; _cellX[j+14]=right; _cellX[j+15]=left;
        _cellX[j+16]=right; _cellX[j+17]=left; _cellX[j+18]=left; _cellX[j+19]=right;
    }

    private void WriteQuadNormal(int cell, int quad, float nx, float ny, float nz)
    {
        int j = (cell * 20 + quad * 4) * 3;
        for (int k = 0; k < 4; k++)
        {
            _normals[j++] = nx;
            _normals[j++] = ny;
            _normals[j++] = nz;
        }
    }

    private void WriteFlat(int j, float r, float g, float b)
    {
        for (int c = 0; c < 20; c++)
        {
            int p = j + c * 3;
            float shade = c < 4 ? 1f : c < 12 ? .76f : .88f;
            _colors[p] = r * shade;
            _colors[p + 1] = g * shade;
            _colors[p + 2] = b * shade;
        }
    }

    private void WritePositions(int j,
        float x0, float y0, float z0, float x1, float y1, float z1,
        float x2, float y2, float z2, float x3, float y3, float z3)
    {
        _vertices[j] = x0; _vertices[j + 1] = y0; _vertices[j + 2] = z0;
        _vertices[j + 3] = x1; _vertices[j + 4] = y1; _vertices[j + 5] = z1;
        _vertices[j + 6] = x2; _vertices[j + 7] = y2; _vertices[j + 8] = z2;
        _vertices[j + 9] = x3; _vertices[j + 10] = y3; _vertices[j + 11] = z3;
    }

    private void WriteSide(int j, float ySelf, float yNeighbor,
        float x0, float z0, float x1, float z1, float x2, float z2, float x3, float z3)
    {
        float top = ySelf, bot = yNeighbor;
        if (ySelf <= yNeighbor)
            bot = top;
        WritePositions(j, x0, bot, z0, x1, bot, z1, x2, top, z2, x3, top, z3);
    }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        IntPtr instance = GetModuleHandle(null);
        var cls = new WindowClass { Style = 0x23, WindowProc = Marshal.GetFunctionPointerForDelegate(DefaultProc), Instance = instance, ClassName = "SonicTrack.Surface.Gpu" };
        if (RegisterClass(ref cls) == 0 && Marshal.GetLastWin32Error() != 1410) throw new Win32Exception();
        _hwnd = CreateWindowEx(0, cls.ClassName, "SonicTrack surface", 0x50000000 | 0x04000000 | 0x02000000, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception();
        _dc = GetDC(_hwnd);
        var pfd = new PixelFormatDescriptor { Size = (ushort)Marshal.SizeOf<PixelFormatDescriptor>(), Version = 1, Flags = 0x25, ColorBits = 32, DepthBits = 24, AlphaBits = 8 };
        int format = ChooseMultisampleFormat(_dc, cls.ClassName, instance, pfd);
        bool multisampleFormat = format != 0;
        if (format == 0) format = ChoosePixelFormat(_dc, ref pfd);
        if (format != 0) DescribePixelFormat(_dc, format, (uint)Marshal.SizeOf<PixelFormatDescriptor>(), ref pfd);
        if (format == 0 || !SetPixelFormat(_dc, format, ref pfd)) throw new Win32Exception();
        _context = wglCreateContext(_dc);
        if (_context == IntPtr.Zero || !wglMakeCurrent(_dc, _context)) throw new Win32Exception();
        Renderer = Marshal.PtrToStringAnsi(glGetString(0x1F01)) ?? "OpenGL";
        if (multisampleFormat)
        {
            glGetIntegerv(0x80A8, out int sampleBuffers);
            glGetIntegerv(0x80A9, out int samples);
            AntialiasingSamples = sampleBuffers > 0 ? samples : 0;
            if (AntialiasingSamples > 1) glEnable(0x809D);
        }
        _generateBuffers = LoadExtension<GenerateBuffers>("glGenBuffers");
        _bindBuffer = LoadExtension<BindBuffer>("glBindBuffer");
        _bufferData = LoadExtension<BufferData>("glBufferData");
        _deleteBuffers = LoadExtension<DeleteBuffers>("glDeleteBuffers");
        if (_generateBuffers != null && _bindBuffer != null && _bufferData != null && _deleteBuffers != null)
            _generateBuffers(_buffers.Length, _buffers);
        _buffersDirty = _normalsDirty = true;
        var swap = wglGetProcAddress("wglSwapIntervalEXT");
        if (swap != IntPtr.Zero && swap.ToInt64() > 3 && swap.ToInt64() != -1)
            Marshal.GetDelegateForFunctionPointer<SwapInterval>(swap)(0);
        glEnable(0x0B71); glDepthFunc(0x0203); glShadeModel(0x1D01);
        glEnable(0x0B50); glEnable(0x4000); glEnable(0x0B57); glEnable(0x0BA1);
        glColorMaterial(0x0408, 0x1602);
        glLightModelfv(0x0B53, new float[] { .66f, .66f, .66f, 1 });
        glLightfv(0x4000, 0x1201, new float[] { .48f, .48f, .48f, 1 });
        glClearColor(12/255f, 14/255f, 20/255f, 1);
        return new HandleRef(this, _hwnd);
    }
    // WGL extension lookup needs a current context, but a window's pixel format
    // can only be set once. Bootstrap on an invisible temporary window first.
    private static int ChooseMultisampleFormat(IntPtr targetDc, string className,
        IntPtr instance, PixelFormatDescriptor pfd)
    {
        IntPtr previousDc = wglGetCurrentDC(), previousContext = wglGetCurrentContext();
        IntPtr window = CreateWindowEx(0, className, "GL bootstrap", unchecked((int)0x80000000),
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (window == IntPtr.Zero) return 0;
        IntPtr dc = GetDC(window), context = IntPtr.Zero;
        try
        {
            int basic = ChoosePixelFormat(dc, ref pfd);
            if (basic == 0 || !SetPixelFormat(dc, basic, ref pfd)) return 0;
            context = wglCreateContext(dc);
            if (context == IntPtr.Zero || !wglMakeCurrent(dc, context)) return 0;
            var choose = LoadExtension<ChoosePixelFormatArb>("wglChoosePixelFormat");
            if (choose == null) return 0;
            foreach (int samples in new[] { 4, 2 })
            {
                int[] attributes = {
                    0x2001, 1,       // WGL_DRAW_TO_WINDOW_ARB
                    0x2010, 1,       // WGL_SUPPORT_OPENGL_ARB
                    0x2011, 1,       // WGL_DOUBLE_BUFFER_ARB
                    0x2013, 0x202B,  // WGL_PIXEL_TYPE_ARB: WGL_TYPE_RGBA_ARB
                    0x2014, 32, 0x201B, 8, 0x2022, 24, // color, alpha, depth bits
                    0x2041, 1, 0x2042, samples, // sample buffer and sample count
                    0
                };
                if (choose(targetDc, attributes, IntPtr.Zero, 1, out int format, out uint count)
                    && count > 0 && format > 0) return format;
            }
            return 0;
        }
        finally
        {
            wglMakeCurrent(previousDc, previousContext);
            if (context != IntPtr.Zero) wglDeleteContext(context);
            if (dc != IntPtr.Zero) ReleaseDC(window, dc);
            DestroyWindow(window);
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_context != IntPtr.Zero && _buffers[0] != 0 && _deleteBuffers != null)
        {
            wglMakeCurrent(_dc, _context);
            _deleteBuffers(_buffers.Length, _buffers);
            Array.Clear(_buffers);
        }
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        if (_context != IntPtr.Zero) wglDeleteContext(_context);
        if (_dc != IntPtr.Zero) ReleaseDC(_hwnd, _dc);
        DestroyWindow(hwnd.Handle);
        _context = _dc = _hwnd = IntPtr.Zero;
    }
    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // HwndHost owns a native child, so handle its mouse messages directly.
        if (msg == 0x0201 && PanRequested != null) // WM_LBUTTONDOWN
        {
            _dragX = (short)(lParam.ToInt64() & 0xffff);
            _dragY = (short)((lParam.ToInt64() >> 16) & 0xffff);
            _dragging = true;
            SetCapture(hwnd);
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == 0x0200 && _dragging) // WM_MOUSEMOVE, including outside the view while captured
        {
            int x = (short)(lParam.ToInt64() & 0xffff), y = (short)((lParam.ToInt64() >> 16) & 0xffff);
            GetClientRect(hwnd, out var rect);
            int width = Math.Max(1, rect.Right);
            PanRequested?.Invoke((x - _dragX) / (double)width, (y - _dragY) / (double)width);
            _dragX = x; _dragY = y;
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == 0x0202 && _dragging) // WM_LBUTTONUP
        {
            _dragging = false;
            ReleaseCapture();
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == 0x0215) _dragging = false; // WM_CAPTURECHANGED
        if (msg == 0x001F && _dragging) // WM_CANCELMODE
        { _dragging = false; ReleaseCapture(); }
        if (msg == 0x0F) NeedsRedraw = true;
        if (msg == 0x14) { handled = true; return new IntPtr(1); }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }
    public unsafe void Draw()
    {
        if (_context == IntPtr.Zero) return;
        wglMakeCurrent(_dc, _context); GetClientRect(_hwnd, out var rect);
        int width = Math.Max(1, rect.Right), height = Math.Max(1, rect.Bottom);
        glClearColor(TransparentBackground ? 0 : 12/255f, TransparentBackground ? 0 : 14/255f,
            TransparentBackground ? 0 : 20/255f, TransparentBackground ? 0 : 1);
        glViewport(0, 0, width, height); glClear(0x4000 | 0x100);
        double frameWidth = _framingSize.IsEmpty ? Math.Max(1, ActualWidth) : _framingSize.Width;
        double frameHeight = _framingSize.IsEmpty ? Math.Max(1, ActualHeight) : _framingSize.Height;
        if (!_framingSize.IsEmpty)
        {
            double scale = Math.Min(width / frameWidth, height / frameHeight);
            int fittedWidth = Math.Clamp((int)Math.Round(frameWidth * scale), 1, width);
            int fittedHeight = Math.Clamp((int)Math.Round(frameHeight * scale), 1, height);
            glViewport((width - fittedWidth) / 2, (height - fittedHeight) / 2, fittedWidth, fittedHeight);
        }
        // WPF's original FieldOfView is horizontal; preserve the same framing.
        double near = .05, right = near * Math.Tan(46 * Math.PI / 360),
            top = right * (_framingSize.IsEmpty ? height / (double)width : frameHeight / frameWidth);
        glMatrixMode(0x1701); glLoadIdentity();
        // Shift the projected image in screen space; camera and model matrices stay unchanged.
        glTranslated(2 * PreviewOffset.X / frameWidth, -2 * PreviewOffset.Y / frameHeight, 0);
        glFrustum(-right, right, -top, top, near, 200);
        var f = _look; f.Normalize(); var s = Vector3D.CrossProduct(f, _up); s.Normalize(); var u = Vector3D.CrossProduct(s, f);
        var matrix = _cameraMatrix;
        matrix[0]=s.X; matrix[1]=u.X; matrix[2]=-f.X;
        matrix[4]=s.Y; matrix[5]=u.Y; matrix[6]=-f.Y;
        matrix[8]=s.Z; matrix[9]=u.Z; matrix[10]=-f.Z; matrix[15]=1;
        glMatrixMode(0x1700); glLoadMatrixd(matrix); glTranslated(-_eye.X, -_eye.Y, -_eye.Z);
        glLightfv(0x4000, 0x1203, LightDirection);
        if (!TransparentBackground)
        {
            glColor3f(42/255f,51/255f,72/255f); glNormal3f(0,1,0);
            glBegin(0x0007); glVertex3d(-8.25,-.04,-5.5);glVertex3d(-8.25,-.04,5.5);glVertex3d(8.25,-.04,5.5);glVertex3d(8.25,-.04,-5.5);glEnd();
        }
        glDisable(0x0B50); glEnable(0x0B44);
        // Fixed world-space boundaries, then translate the immutable mesh inside them.
        glClipPlane(0x3000, LeftClip); glClipPlane(0x3001, RightClip);
        glEnable(0x3000); glEnable(0x3001);
        glTranslated(_scrollOffset, 0, 0);
        glEnableClientState(0x8074); glEnableClientState(0x8075); glEnableClientState(0x8076);
        fixed(float* v = _vertices, n = _normals, c = _colors)
        fixed(int* indices = _indices, gapIndices = _gapIndices)
        {
            if (_buffers[0] != 0)
            {
                if (_buffersDirty)
                {
                    UploadBuffer(0, 0x8892, _vertices.Length, (IntPtr)v);
                    if (_normalsDirty) UploadBuffer(1, 0x8892, _normals.Length, (IntPtr)n);
                    UploadBuffer(2, 0x8892, _colors.Length, (IntPtr)c);
                    UploadBuffer(3, 0x8893, _drawIndexCount, (IntPtr)indices);
                    UploadBuffer(4, 0x8893, _gapDrawIndexCount, (IntPtr)gapIndices);
                    _buffersDirty = _normalsDirty = false;
                }
                _bindBuffer!(0x8892, _buffers[0]); glVertexPointer(3, 0x1406, 0, IntPtr.Zero);
                _bindBuffer(0x8892, _buffers[1]); glNormalPointer(0x1406, 0, IntPtr.Zero);
                _bindBuffer(0x8892, _buffers[2]); glColorPointer(3, 0x1406, 0, IntPtr.Zero);
                _bindBuffer(0x8893, _buffers[3]); glDrawElements(0x0004, _drawIndexCount, 0x1405, IntPtr.Zero);
                DrawGaps(IntPtr.Zero, true);
                _bindBuffer(0x8892, 0); _bindBuffer(0x8893, 0);
            }
            else
            {
                glVertexPointer(3,0x1406,0,(IntPtr)v);glNormalPointer(0x1406,0,(IntPtr)n);glColorPointer(3,0x1406,0,(IntPtr)c);
                glDrawElements(0x0004,_drawIndexCount,0x1405,(IntPtr)indices);
                DrawGaps((IntPtr)gapIndices, false);
            }
        }
        glDisableClientState(0x8074);glDisableClientState(0x8075);glDisableClientState(0x8076);
        glDisable(0x0B44); glEnable(0x0B50);
        glDisable(0x3000); glDisable(0x3001);
        if (TransparentBackground) ReadTransparentFrame(width, height);
        SwapBuffers(_dc);
        FrameCount++;
        NeedsRedraw = false;
    }

    private void DrawGaps(IntPtr indices, bool buffered)
    {
        if (_gapDrawIndexCount == 0 || InteriorBlackOpacity == 0) return;
        if (buffered) _bindBuffer!(0x8893, _buffers[4]);
        glDisableClientState(0x8076);
        // Black has premultiplied RGB=(0,0,0). ONE / ONE_MINUS_SRC_ALPHA blends
        // its alpha correctly against the transparent desktop and opaque pillars.
        glEnable(0x0BE2); glBlendFunc(1, 0x0303); glDepthMask(0);
        glColor4f(0, 0, 0, InteriorBlackOpacity);
        glDrawElements(0x0004, _gapDrawIndexCount, 0x1405, indices);
        glDepthMask(1); glDisable(0x0BE2);
        glColor4f(1, 1, 1, 1);
        glEnableClientState(0x8076);
    }

    private void ReadTransparentFrame(int width, int height)
    {
        if (TransparentFrame == null || TransparentFrame.PixelWidth != width || TransparentFrame.PixelHeight != height)
        {
            _readback = new byte[checked(width * height * 4)];
            _flipped = new byte[_readback.Length];
            TransparentFrame = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        }
        // Read before SwapBuffers: hidden window front buffers are not reliable.
        // MSAA resolves RGB and alpha against (0,0,0,0), producing premultiplied edge coverage.
        glReadBuffer(0x0405);
        glReadPixels(0, 0, width, height, 0x80E1, 0x1401, _readback);
        int stride = width * 4;
        for (int row = 0; row < height; row++)
            Array.Copy(_readback, row * stride, _flipped, (height - 1 - row) * stride, stride);
        TransparentFrame.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), _flipped, stride, 0);
    }

    private void UploadBuffer(int index, uint target, int count, IntPtr data)
    {
        _bindBuffer!(target, _buffers[index]);
        _bufferData!(target, (IntPtr)checked(count * 4), data, 0x88E8);
    }

    private static T? LoadExtension<T>(string name) where T : Delegate
    {
        IntPtr address = wglGetProcAddress(name);
        if (address == IntPtr.Zero || address.ToInt64() is 1 or 2 or 3 or -1)
            address = wglGetProcAddress(name + "ARB");
        return address == IntPtr.Zero || address.ToInt64() is 1 or 2 or 3 or -1
            ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GenerateBuffers(int count, [Out] uint[] buffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BindBuffer(uint target, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BufferData(uint target, IntPtr size, IntPtr data, uint usage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteBuffers(int count, uint[] buffers);
    public BitmapSource? CaptureFrame()
    {
        if (_context == IntPtr.Zero) return null;
        wglMakeCurrent(_dc,_context);GetClientRect(_hwnd,out var rect);
        int w=rect.Right,h=rect.Bottom;if(w<1||h<1)return null;
        var pixels=new byte[w*h*4];glReadBuffer(0x0404);glReadPixels(0,0,w,h,0x80E1,0x1401,pixels);
        var flipped=new byte[pixels.Length];for(int row=0;row<h;row++)Array.Copy(pixels,row*w*4,flipped,(h-1-row)*w*4,w*4);
        var bitmap=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,flipped,w*4);bitmap.Freeze();return bitmap;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Style; public IntPtr WindowProc; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] private struct PixelFormatDescriptor
    {
        public ushort Size,Version;public uint Flags;public byte PixelType,ColorBits,RedBits,RedShift,GreenBits,GreenShift,BlueBits,BlueShift,AlphaBits,AlphaShift,AccumBits,AccumRedBits,AccumGreenBits,AccumBlueBits,AccumAlphaBits,DepthBits,StencilBits,AuxBuffers,LayerType,Reserved;public uint LayerMask,VisibleMask,DamageMask;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ChoosePixelFormatArb(IntPtr dc, int[] attributes, IntPtr floatAttributes,
        uint maxFormats, out int format, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SwapInterval(int interval);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateWindowEx(int ex,string cls,string title,int style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
    [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr dc,ref PixelFormatDescriptor pfd);
    [DllImport("gdi32.dll")] private static extern int DescribePixelFormat(IntPtr dc,int pixel,uint size,ref PixelFormatDescriptor pfd);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr dc,int pixel,ref PixelFormatDescriptor pfd);
    [DllImport("gdi32.dll")] private static extern bool SwapBuffers(IntPtr dc);
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentDC();
    [DllImport("opengl32.dll")] private static extern void glGetIntegerv(uint name,out int value);
    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr dc,IntPtr rc);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr rc);
    [DllImport("opengl32.dll",CharSet=CharSet.Ansi)] private static extern IntPtr wglGetProcAddress(string name);
    [DllImport("opengl32.dll")] private static extern IntPtr glGetString(uint name);
    [DllImport("opengl32.dll")] private static extern void glViewport(int x,int y,int w,int h);
    [DllImport("opengl32.dll")] private static extern void glEnable(uint state);
    [DllImport("opengl32.dll")] private static extern void glDisable(uint state);
    [DllImport("opengl32.dll")] private static extern void glBlendFunc(uint source,uint destination);
    [DllImport("opengl32.dll")] private static extern void glDepthMask(byte flag);
    [DllImport("opengl32.dll")] private static extern void glColor4f(float r,float g,float b,float a);
    [DllImport("opengl32.dll")] private static extern void glDepthFunc(uint func);
    [DllImport("opengl32.dll")] private static extern void glShadeModel(uint model);
    [DllImport("opengl32.dll")] private static extern void glColorMaterial(uint face,uint mode);
    [DllImport("opengl32.dll")] private static extern void glLightModelfv(uint name,float[] value);
    [DllImport("opengl32.dll")] private static extern void glLightfv(uint light,uint name,float[] value);
    [DllImport("opengl32.dll")] private static extern void glClearColor(float r,float g,float b,float a);
    [DllImport("opengl32.dll")] private static extern void glClear(uint mask);
    [DllImport("opengl32.dll")] private static extern void glMatrixMode(uint mode);
    [DllImport("opengl32.dll")] private static extern void glLoadIdentity();
    [DllImport("opengl32.dll")] private static extern void glLoadMatrixd(double[] matrix);
    [DllImport("opengl32.dll")] private static extern void glTranslated(double x,double y,double z);
    [DllImport("opengl32.dll")] private static extern void glClipPlane(uint plane, double[] equation);
    [DllImport("opengl32.dll")] private static extern void glFrustum(double left,double right,double bottom,double top,double near,double far);
    [DllImport("opengl32.dll")] private static extern void glColor3f(float r,float g,float b);
    [DllImport("opengl32.dll")] private static extern void glNormal3f(float x,float y,float z);
    [DllImport("opengl32.dll")] private static extern void glBegin(uint mode);
    [DllImport("opengl32.dll")] private static extern void glEnd();
    [DllImport("opengl32.dll")] private static extern void glVertex3d(double x,double y,double z);
    [DllImport("opengl32.dll")] private static extern void glEnableClientState(uint state);
    [DllImport("opengl32.dll")] private static extern void glDisableClientState(uint state);
    [DllImport("opengl32.dll")] private static extern void glVertexPointer(int size,uint type,int stride,IntPtr pointer);
    [DllImport("opengl32.dll")] private static extern void glNormalPointer(uint type,int stride,IntPtr pointer);
    [DllImport("opengl32.dll")] private static extern void glColorPointer(int size,uint type,int stride,IntPtr pointer);
    [DllImport("opengl32.dll")] private static extern void glDrawElements(uint mode,int count,uint type,IntPtr indices);
    [DllImport("opengl32.dll")] private static extern void glReadBuffer(uint mode);
    [DllImport("opengl32.dll")] private static extern void glReadPixels(int x,int y,int w,int h,uint format,uint type,byte[] pixels);
}
