namespace SonicTrack.V61;

/// <summary>
/// Stores each analysis column unchanged. Retention length is independent of the
/// bounded display window; old columns can be viewed without temporal aggregation.
/// The caller synchronizes audio/UI access.
/// </summary>
internal sealed class SpectrumDisplayHistory
{
    internal const int MaxPillars = 8192;
    private readonly SpectrumHistory _history = new();
    private float[] _column = Array.Empty<float>();
    private int _recordColumns;
    private int _rate;
    public int Rows => _history.Rows;
    public int Columns { get; private set; }
    public int SamplesPerColumn => 1;
    public int VisibleColumns { get; private set; }
    public int GuardColumns => Columns - VisibleColumns;
    public long InputColumns { get; private set; }
    public long CommittedColumns { get; private set; }
    public long Epoch { get; private set; }
    public int MaximumHistoryOffset => Math.Max(0, _recordColumns - VisibleColumns);

    // Retain extra raw columns for callback jitter beyond the presentation delay.
    private static int PaddingColumns(int rate) =>
        (int)Math.Ceiling(Math.Max(1, SpectrumScrollClock.MinimumDelaySeconds * rate)) +
        (int)Math.Ceiling(.01 * rate) + 2;

    public static int DisplayColumns(int rows, int recordColumns, int rate)
    {
        int padding = PaddingColumns(rate);
        int spatial = Math.Max(1, (int)Math.Floor(8.2 / (4.6 / rows)));
        return Math.Max(1, Math.Min(recordColumns, Math.Min(spatial, MaxPillars / rows - padding)));
    }

    public bool Configure(int rows, int recordColumns, int rate = 120)
    {
        if (rows == Rows && recordColumns == _recordColumns && rate == _rate) return false;
        if (rows < 1 || rows > MaxPillars / 4 || recordColumns < 1 || rate < 1)
            throw new ArgumentOutOfRangeException(nameof(rows));
        int padding = PaddingColumns(rate);
        int visible = DisplayColumns(rows, recordColumns, rate);
        if ((long)(visible + padding) * rows > MaxPillars)
            throw new ArgumentOutOfRangeException(nameof(rate));
        bool timebaseChanged = rows != Rows || rate != _rate;
        _history.Configure(rows, recordColumns + padding);
        _recordColumns = recordColumns;
        Columns = visible + padding;
        VisibleColumns = visible;
        _rate = rate;
        if (_column.Length != rows) _column = new float[rows];
        if (timebaseChanged) Clear();
        return true;
    }

    public void Clear()
    {
        _history.Clear();
        InputColumns = CommittedColumns = 0;
        Epoch++;
    }

    public bool Push(float[] column)
    {
        InputColumns++;
        for (int b = 0; b < Rows; b++)
        {
            float value = column[b];
            if (!float.IsFinite(value)) value = 0;
            _column[b] = Math.Clamp(value, 0, 1);
        }
        _history.Push(_column);
        CommittedColumns++;
        return true;
    }

    public void CopyNewestFirst(float[] destination, int historyOffset = 0) =>
        _history.CopyNewestFirst(destination, Math.Clamp(historyOffset, 0, MaximumHistoryOffset));
}
