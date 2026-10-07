namespace SonicTrack.V61;

/// <summary>Bounded rolling measurements; allocations never occur on the frame path.</summary>
internal sealed class FrameTiming
{
    private readonly double[] _intervals = new double[256], _costs = new double[256], _scratch = new double[256];
    private int _head, _count;
    private double _last;
    public void Reset() { _last = 0; _count = _head = 0; }
    public void Record(double seconds, double costMs)
    {
        if (_last != 0 && seconds > _last)
        {
            _intervals[_head] = (seconds - _last) * 1000;
            _costs[_head] = costMs;
            _head = (_head + 1) % _intervals.Length;
            _count = Math.Min(_count + 1, _intervals.Length);
        }
        _last = seconds;
    }
    public (double IntervalP95, double CostP95) Read()
    {
        if (_count == 0) return (0, 0);
        return (Percentile(_intervals), Percentile(_costs));
    }
    private double Percentile(double[] source)
    {
        Array.Copy(source, _scratch, _count);
        Array.Sort(_scratch, 0, _count);
        return _scratch[(int)Math.Ceiling(_count * .95) - 1];
    }
}
