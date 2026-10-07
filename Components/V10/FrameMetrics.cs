using System.Diagnostics;

namespace SonicTrack.WaveCore;

internal sealed class FrameMetrics
{
    private readonly double[] _intervals = new double[512], _costs = new double[512], _scratch = new double[512];
    private int _count, _index;
    private long _last;
    public long Calls { get; private set; }
    public void Reset() { _count = _index = 0; _last = 0; }
    public void Record(long timestamp, double costMs)
    {
        Calls++;
        if (_last != 0)
        {
            _intervals[_index] = Stopwatch.GetElapsedTime(_last, timestamp).TotalMilliseconds;
            _costs[_index] = costMs;
            _index = (_index + 1) % _intervals.Length; _count = Math.Min(_count + 1, _intervals.Length);
        }
        _last = timestamp;
    }
    public (double Fps, double IntervalP95, double CostP95) Read()
    {
        if (_count == 0) return (0, 0, 0);
        double sum = 0; for (int i = 0; i < _count; i++) sum += _intervals[i];
        double P95(double[] values)
        {
            Array.Copy(values, _scratch, _count); Array.Sort(_scratch, 0, _count);
            return _scratch[(int)Math.Ceiling(_count * .95) - 1];
        }
        return (sum > 0 ? 1000 * _count / sum : 0, P95(_intervals), P95(_costs));
    }
}

