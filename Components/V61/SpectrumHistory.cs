namespace SonicTrack.V61;

/// <summary>Circular history. The caller synchronizes access between audio and UI threads.</summary>
internal sealed class SpectrumHistory
{
    private float[] _data = Array.Empty<float>();
    private int _head;
    private int _count;
    public int Rows { get; private set; }
    public int Columns { get; private set; }

    public void Clear()
    {
        Array.Clear(_data);
        _head = _count = 0;
    }

    public void Configure(int rows, int columns)
    {
        if (Rows == rows && Columns == columns) return;
        var data = new float[checked(rows * columns)];
        int keep = Rows == rows ? Math.Min(_count, columns) : 0;
        for (int age = 0; age < keep; age++)
            Array.Copy(_data, ((_head + age) % Columns) * Rows, data, age * rows, rows);
        _data = data;
        Rows = rows;
        Columns = columns;
        _head = 0;
        _count = keep;
    }

    public void Push(float[] column)
    {
        _head = (_head + Columns - 1) % Columns;
        Array.Copy(column, 0, _data, _head * Rows, Rows);
        _count = Math.Min(_count + 1, Columns);
    }

    public void CopyNewestFirst(float[] destination, int ageOffset = 0)
    {
        int columns = destination.Length / Rows;
        if (ageOffset < 0 || ageOffset + columns > Columns || destination.Length % Rows != 0)
            throw new ArgumentOutOfRangeException(nameof(ageOffset));
        int start = (_head + ageOffset) % Columns;
        int first = Math.Min(columns, Columns - start) * Rows;
        Array.Copy(_data, start * Rows, destination, 0, first);
        Array.Copy(_data, 0, destination, first, destination.Length - first);
    }
}
