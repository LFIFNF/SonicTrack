using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SonicTrack.WaveCore;

/// <summary>One coalesced render-priority UI callback per deadline; no timer backlog or busy-spin.</summary>
internal sealed class RenderPump : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _draw;
    private readonly AutoResetEvent _stop = new(false);
    private Thread? _thread;
    private volatile bool _running;
    private int _queued;
    private long _generation;
    internal int Hz { get; private set; }
    internal RenderPump(Dispatcher dispatcher, Action draw) { _dispatcher = dispatcher; _draw = draw; }
    internal void Start(int hz)
    {
        if (_running && Hz == hz) return;
        Stop(); Hz = hz; _stop.Reset(); _running = true;
        long generation = Interlocked.Increment(ref _generation);
        _thread = new Thread(() => Run(generation)) { IsBackground = true, Name = "SonicTrack wave frame clock" };
        _thread.Start();
    }
    private void Run(long generation)
    {
        IntPtr timer = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
        if (timer == IntPtr.Zero) timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x1F0003);
        long step = Math.Max(1, Stopwatch.Frequency / Hz), next = Stopwatch.GetTimestamp();
        try
        {
            while (_running && generation == Interlocked.Read(ref _generation))
            {
                if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) break;
                long now = Stopwatch.GetTimestamp();
                if (now < next)
                {
                    long due = -Math.Max(1, (next - now) * 10_000_000 / Stopwatch.Frequency);
                    if (timer != IntPtr.Zero && SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                        WaitForSingleObject(timer, 20);
                    else _stop.WaitOne(Math.Clamp((int)Math.Ceiling((next - now) * 1000.0 / Stopwatch.Frequency), 1, 20));
                    continue;
                }
                next += step;
                if (now - next > step) next = now + step;
                if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0) continue;
                try { _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    try { if (_running && generation == Interlocked.Read(ref _generation)) _draw(); }
                    finally { Interlocked.Exchange(ref _queued, 0); }
                }); }
                catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                { Interlocked.Exchange(ref _queued, 0); break; }
            }
        }
        finally { if (timer != IntPtr.Zero) CloseHandle(timer); }
    }
    internal void Stop()
    {
        _running = false; Interlocked.Increment(ref _generation); _stop.Set();
        _thread?.Join(1000); _thread = null;
    }
    public void Dispose() { Stop(); _stop.Dispose(); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr routine, IntPtr argument, bool resume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
