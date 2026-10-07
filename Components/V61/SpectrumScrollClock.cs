namespace SonicTrack.V61;

/// <summary>Continuous presentation position, independent of analysis/publication cadence.</summary>
internal sealed class SpectrumScrollClock
{
    internal const double MinimumDelaySeconds = .04;
    private long _epoch = -1;
    private double _lastSeconds;
    private bool _started;
    private double _phaseError;
    public double Position { get; private set; }
    public int ResyncCount { get; private set; }

    public double Advance(double seconds, double inputPosition, long committedColumns,
        long epoch, double periodSeconds, int guardColumns)
    {
        double delay = Math.Max(1, MinimumDelaySeconds / periodSeconds);
        if (epoch != _epoch)
        {
            _epoch = epoch;
            _started = false;
            _phaseError = 0;
            Position = 0;
            _lastSeconds = seconds;
        }
        double elapsed = Math.Max(0, seconds - _lastSeconds);
        _lastSeconds = seconds;
        if (!_started)
        {
            if (inputPosition >= delay)
            {
                Position = Math.Max(0, inputPosition - delay);
                _started = true;
            }
        }
        else
        {
            // Correct small producer-clock drift through speed, never by snapping columns.
            double target = inputPosition - delay;
            double predicted = Position + elapsed / periodSeconds;
            // Column counts are quantized. Filter phase error before changing speed,
            // otherwise every publication becomes a small visible acceleration.
            double blend = 1 - Math.Exp(-elapsed / .8);
            _phaseError += ((target - predicted) - _phaseError) * blend;
            double speed = Math.Clamp(1 + _phaseError * .04 / delay, .97, 1.03);
            Position = Math.Min(committedColumns, Position + elapsed / periodSeconds * speed);
        }
        if (committedColumns - Position > guardColumns - 1)
        {
            // A long UI suspension can outlive the retained padding; resume at buffered time.
            Position = Math.Max(0, committedColumns - delay);
            _phaseError = 0;
            ResyncCount++;
        }
        return committedColumns - Position;
    }
}
