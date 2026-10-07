using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SonicTrack.V61;

/// <summary>
/// Immutable abutted pillars translated by a continuous presentation clock.
/// Recorded height and flat color never change while a pillar moves.
/// </summary>
public sealed class Waterfall3DView : Grid
{
    private const double SpanX = 8.2;
    private const double SpanZ = 4.6;
    private const double HeightScale = 4.2;
    private const float PillarFloor = 0.02f;
    internal const float TransparentBlackCutoff = .05f;
    // Match V6 color stops: black -> dark purple -> blue -> green -> yellow -> red.
    private static readonly (float T, byte R, byte G, byte B)[] RampStops =
    {
        (0.00f, 0x00, 0x00, 0x00),
        (0.14f, 0x2A, 0x08, 0x40),
        (0.32f, 0x1D, 0x4E, 0xD8),
        (0.55f, 0x16, 0xA3, 0x4A),
        (0.78f, 0xF5, 0xD9, 0x0A),
        (1.00f, 0xDC, 0x26, 0x26),
    };

    private static readonly Point3D LookTarget = new(0, 1.8, 0);
    private readonly NativeSurfaceView _gpu = new();

    private int _bars = -1;
    private int _cols = -1;
    private int _meshRows;
    private int _meshCols;
    private int _visibleCols;
    private float[] _source = Array.Empty<float>();
    private float[] _heightSource = Array.Empty<float>();
    private double[] _heights = Array.Empty<double>();
    private Color[] _cellColors = Array.Empty<Color>();
    private float[] _tops = Array.Empty<float>(), _occludingTops = Array.Empty<float>(), _alphas = Array.Empty<float>();
    private long? _sequence;
    private int _shiftColumns;
    public int LastRebuiltColumns { get; private set; }
    private float[] _amplitudes = Array.Empty<float>();
    private double _scrollColumns;
    private bool _hasFrame;
    private bool _geometryDirty;
    private bool _cameraDirty = true;
    private double _sampleMs;
    internal Point PreviewOffset => _gpu.PreviewOffset;
    internal void MovePreview(double x, double y) =>
        _gpu.SetPreviewOffset(new Point(PreviewOffset.X + x, PreviewOffset.Y + y));
    internal void ResetPreviewOffset() => _gpu.SetPreviewOffset(new Point());
    internal void SetPreviewFraming(Size size, Point offset)
    {
        _gpu.SetFramingSize(size);
        _gpu.SetPreviewOffset(offset);
    }

    public double BuildMs { get; private set; }
    public double UploadMs { get; private set; }
    public string Renderer => _gpu.Renderer;
    public int AntialiasingSamples => _gpu.AntialiasingSamples;
    public int VisibleRows => _meshRows;
    public int VisibleColumns => _visibleCols;
    public double FootprintSize => _bars > 0 ? SpanZ / _bars : 0;
    public long FrameCount => _gpu.FrameCount;
    internal System.Windows.Media.Imaging.WriteableBitmap? TransparentFrame => _gpu.TransparentFrame;
    internal float InteriorBlackOpacity
    {
        get => _gpu.InteriorBlackOpacity;
        set => _gpu.InteriorBlackOpacity = value;
    }
    internal bool TransparentOutput
    {
        get => _gpu.TransparentBackground;
        set
        {
            if (_gpu.TransparentBackground == value) return;
            _gpu.TransparentBackground = value;
            _geometryDirty = true;
            _shiftColumns = 0;
            RefreshCellStyle();
        }
    }
    public Waterfall3DView()
    {
        ClipToBounds = true;
        SetViewpoint(0, 55, 16, 0, 0);
        Children.Add(_gpu);
        _gpu.PanRequested += (x, y) => MovePreview(x * ActualWidth, y * ActualWidth);
    }

    public void SetViewpoint(double yawDeg, double pitchDeg, double distance, double panX, double panY)
    {
        double yaw = yawDeg * Math.PI / 180.0;
        double pitch = Math.Clamp(pitchDeg, 5, 85) * Math.PI / 180.0;
        double dist = Math.Clamp(distance, 0, 30);
        if (dist < 0.4)
            dist = 0.4;
        double slideX = Math.Clamp(panX, -5, 5);
        double slideY = Math.Clamp(panY, -5, 5);
        double sp = Math.Sin(pitch), cp = Math.Cos(pitch);
        double sy = Math.Sin(yaw), cy = Math.Cos(yaw);
        var radial = new Vector3D(cp * sy, sp, cp * cy);
        var pos = new Point3D(
            LookTarget.X + dist * radial.X,
            LookTarget.Y + dist * radial.Y,
            LookTarget.Z + dist * radial.Z);
        var look = LookTarget - pos;
        var up = new Vector3D(-sp * sy, cp, -sp * cy);
        look.Normalize();
        up.Normalize();
        var right = Vector3D.CrossProduct(look, up);
        right.Normalize();
        var camUp = Vector3D.CrossProduct(right, look);
        camUp.Normalize();
        var shift = right * -slideX + camUp * -slideY;
        pos = new Point3D(pos.X + shift.X, pos.Y + shift.Y, pos.Z + shift.Z);
        _gpu.SetCamera(pos, look, up);
        _cameraDirty = true;
    }

    /// <summary>
    /// newestFirst layout: age * bars + bar, age 0 = newest (right), bar 0 = low freq (bottom).
    /// </summary>
    public void Apply(float[] newestFirst, int bars, int cols, int visibleColumns = 0, long? sequence = null,
        float[]? heightLevels = null)
    {
        var profile = Stopwatch.GetTimestamp();
        if (bars < 1 || cols < 1) return;
        if (heightLevels != null && heightLevels.Length != newestFirst.Length)
            throw new ArgumentException("柱高和颜色记录必须具有相同长度。", nameof(heightLevels));

        int visible = visibleColumns == 0 ? cols : Math.Clamp(visibleColumns, 1, cols);
        bool shapeChanged = bars != _bars || cols != _cols || visible != _visibleCols;
        if ((long)bars * cols > SpectrumDisplayHistory.MaxPillars)
            throw new ArgumentException("显示窗口超过柱子预算，请限制窗口大小；历史数据不能重新分组。");
        if (shapeChanged)
        {
            _bars = bars;
            _cols = cols;
            _visibleCols = visible;
            _source = new float[checked(bars * cols)];
            _heightSource = new float[_source.Length];
        }

        // Reuse old columns only after verifying the overlapping immutable records.
        int shift = 0;
        if (!shapeChanged && !_geometryDirty && sequence.HasValue && _sequence.HasValue)
        {
            long advanced = sequence.Value - _sequence.Value;
            if (advanced > 0 && advanced < cols - 2 && newestFirst.Length == _source.Length)
            {
                shift = (int)advanced;
                for (int i = shift * bars; i < _source.Length; i++)
                    if (newestFirst[i] != _source[i - shift * bars] ||
                        (heightLevels == null ? newestFirst[i] : heightLevels[i]) != _heightSource[i - shift * bars])
                    { shift = 0; break; }
            }
        }
        _sequence = sequence;
        int n = Math.Min(_source.Length, newestFirst.Length);
        bool changed = shapeChanged;
        for (int i = 0; i < n; i++)
        {
            float v = newestFirst[i];
            float amplitude = float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;
            changed |= amplitude != _source[i];
            _source[i] = amplitude;
            float h = heightLevels == null ? amplitude : (i < heightLevels.Length ? heightLevels[i] : 0);
            h = float.IsFinite(h) ? Math.Clamp(h, 0f, 1f) : 0f;
            changed |= h != _heightSource[i];
            _heightSource[i] = h;
        }
        if (n < _source.Length)
        {
            for (int i = n; i < _source.Length; i++) changed |= _source[i] != 0 || _heightSource[i] != 0;
            Array.Clear(_source, n, _source.Length - n);
            Array.Clear(_heightSource, n, _heightSource.Length - n);
        }
        if (!changed && _hasFrame) return;

        EnsureDisplayMesh();
        _gpu.SetPlotBounds(SpanX * .5 - _visibleCols * FootprintSize, SpanX * .5);
        UpdatePresentedHeights(shift);
        _shiftColumns = shift;
        UpdateTranslation();
        _hasFrame = true;
        _sampleMs = Stopwatch.GetElapsedTime(profile).TotalMilliseconds;
    }

    private void EnsureDisplayMesh()
    {
        int columns = _cols;
        if (_meshRows == _bars && _meshCols == columns) return;
        _meshRows = _bars;
        _meshCols = columns;
        _heights = new double[checked(_meshRows * _meshCols)];
        _amplitudes = new float[_heights.Length];
        _tops = new float[_heights.Length];
        _occludingTops = new float[_heights.Length];
        _alphas = new float[_heights.Length];
        _cellColors = new Color[_heights.Length];
        _gpu.EnsureCells(_heights.Length);
        _geometryDirty = true;
    }

    private void UpdatePresentedHeights(int shift)
    {
        for (int i = 0; i < _source.Length; i++)
        {
            _geometryDirty |= _source[i] != _amplitudes[i] || _heights[i] != HeightScale * _heightSource[i];
            _amplitudes[i] = _source[i];
            _heights[i] = HeightScale * _heightSource[i];
        }
        if (shift > 0)
        {
            int first = shift * _meshRows;
            Array.Copy(_cellColors, 0, _cellColors, first, _cellColors.Length - first);
            Array.Copy(_tops, 0, _tops, first, _tops.Length - first);
            Array.Copy(_occludingTops, 0, _occludingTops, first, _tops.Length - first);
            Array.Copy(_alphas, 0, _alphas, first, _tops.Length - first);
            RefreshCellStyle(first);
        }
        else RefreshCellStyle();
    }

    private void RefreshCellStyle(int count = -1)
    {
        int end = count < 0 ? _amplitudes.Length : count;
        // Classify independently within each immutable time column. Peripheral dark
        // bins stay invisible; only bins bracketed by colored frequencies get a bridge.
        for (int start = 0; start < end; start += _meshRows)
        {
            int firstColor = _meshRows, lastColor = -1;
            if (TransparentOutput)
            {
                for (int row = 0; row < _meshRows; row++)
                    if (_amplitudes[start + row] > TransparentBlackCutoff)
                    {
                        firstColor = Math.Min(firstColor, row);
                        lastColor = row;
                    }
            }
            for (int row = 0; row < _meshRows; row++)
            {
                int i = start + row;
                bool dark = TransparentOutput && _amplitudes[i] <= TransparentBlackCutoff;
                float alpha = dark ? (row > firstColor && row < lastColor ? NativeSurfaceView.InteriorBlackAlpha : 0) : 1;
                _alphas[i] = alpha;
                _cellColors[i] = dark ? Colors.Black : RampColor(_amplitudes[i]);
                _tops[i] = alpha == 0 ? 0 : PillarTop(_heights[i]);
                // A translucent bridge cannot hide the lower part of an opaque neighbor.
                _occludingTops[i] = alpha == 1 ? _tops[i] : 0;
            }
        }
    }

    private void UpdateTranslation() => _gpu.SetScrollOffset(_scrollColumns * FootprintSize);

    public void RenderFrame()
    {
        if (!_hasFrame || _bars < 1 || _meshRows < 1) return;
        if (_geometryDirty || _cameraDirty || _gpu.NeedsRedraw)
            DrawPillars();
    }

    public void SetScrollOffset(double columns)
    {
        if (!_hasFrame || !double.IsFinite(columns)) return;
        columns = Math.Clamp(columns, 0, Math.Max(0, _cols - 1));
        if (columns == _scrollColumns) return;
        _scrollColumns = columns;
        UpdateTranslation();
    }

    private void DrawPillars()
    {
        var profile = Stopwatch.GetTimestamp();
        int rows = _meshRows, columns = _meshCols;
        int count = rows * columns;
        if (count < 1) return;

        if (_geometryDirty)
        {
            if (_shiftColumns > 0) _gpu.ShiftCells(_shiftColumns * rows);
            _gpu.BeginCells();
            LastRebuiltColumns = 0;
            double cellX = FootprintSize;
            double cellZ = SpanZ / rows;
            for (int age = 0; age < columns; age++)
            {
                if (_shiftColumns > 0 && age > _shiftColumns && age != columns - 1)
                {
                    for (int b = 0; b < rows; b++) _gpu.ReuseCell(age * rows + b);
                    continue;
                }
                LastRebuiltColumns++;
                float xRight = (float)(SpanX * 0.5 - age * cellX);
                float xLeft = (float)(xRight - cellX);
                int ageBase = age * rows;
                for (int b = 0; b < rows; b++)
                {
                    int i = ageBase + b;
                    float y = _tops[i];
                    float yNewer = age == 0 ? 0f : _occludingTops[ageBase - rows + b];
                    float yOlder = age == columns - 1 ? 0f : _occludingTops[ageBase + rows + b];
                    float yLowerFreq = b == 0 ? 0f : _occludingTops[i - 1];
                    float yHigherFreq = b == rows - 1 ? 0f : _occludingTops[i + 1];
                    float zHigh = (float)(SpanZ * 0.5 - b * cellZ);
                    float zLow = (float)(zHigh - cellZ);
                    var color = _cellColors[i];
                    _gpu.SetCell(i, xLeft, xRight, zLow, zHigh, y,
                        yNewer, yOlder, yLowerFreq, yHigherFreq,
                        color.R / 255f, color.G / 255f, color.B / 255f, _alphas[i]);
                }
            }
        }
        BuildMs = _geometryDirty ? _sampleMs + Stopwatch.GetElapsedTime(profile).TotalMilliseconds : 0;
        profile = Stopwatch.GetTimestamp();
        _gpu.Draw();
        UploadMs = Stopwatch.GetElapsedTime(profile).TotalMilliseconds;
        _geometryDirty = _cameraDirty = false;
        _shiftColumns = 0;
    }


    private static float PillarTop(double h)
    {
        float y = (float)h;
        return y < PillarFloor ? PillarFloor : y;
    }

    public static Color RampColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        int i = 0;
        while (i < RampStops.Length - 2 && t > RampStops[i + 1].T) i++;
        var a = RampStops[i];
        var b = RampStops[i + 1];
        float u = (t - a.T) / Math.Max(0.0001f, b.T - a.T);
        if (u < 0) u = 0;
        else if (u > 1) u = 1;
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * u),
            (byte)(a.G + (b.G - a.G) * u),
            (byte)(a.B + (b.B - a.B) * u));
    }
}
