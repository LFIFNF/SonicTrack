using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SonicTrack.V61;

/// <summary>Independent display-paced wakeups; all GL/WPF work stays on its owning UI thread.</summary>
internal sealed class PreviewFramePump : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _draw;
    private readonly Action _dispatch;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _running, _active;
    private bool _disposed;
    private int _pending, _rate = 60;
    private IntPtr _displayMonitor;
    internal long DisplayQueryCount { get; private set; }
    internal int TargetRate => Volatile.Read(ref _rate);
    internal long DroppedWakeups { get; private set; }

    internal PreviewFramePump(Dispatcher dispatcher, Action draw)
    {
        _dispatcher = dispatcher; _draw = draw;
        _dispatch = () =>
        {
            try { if (_running && _active) _draw(); }
            finally { Volatile.Write(ref _pending, 0); }
        };
    }

    internal void Start()
    {
        if (_disposed || _thread != null) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "v61-preview-clock" };
        _thread.Start();
    }

    internal void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        _wake.Set();
    }

    internal void UpdateDisplay(IntPtr window)
    {
        if (_disposed || window == IntPtr.Zero) return;
        var displayMonitor = MonitorFromWindow(window, 2);
        if (displayMonitor == IntPtr.Zero || displayMonitor == _displayMonitor) return;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(displayMonitor, ref monitor)) return;
        var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>() };
        DisplayQueryCount++;
        int rate = EnumDisplaySettings(monitor.Device, -1, ref mode) && mode.Frequency > 1 ? mode.Frequency : 60;
        _displayMonitor = displayMonitor;
        rate = Math.Clamp(rate, 30, 500);
        if (Interlocked.Exchange(ref _rate, rate) != rate) _wake.Set();
    }
    internal void InvalidateDisplay() => _displayMonitor = IntPtr.Zero;

    private void Run()
    {
        IntPtr timer = CreateWaitableTimerEx(IntPtr.Zero, IntPtr.Zero, 2, 0x1F0003);
        if (timer == IntPtr.Zero) timer = CreateWaitableTimerEx(IntPtr.Zero, IntPtr.Zero, 0, 0x1F0003);
        long next = Stopwatch.GetTimestamp();
        try
        {
            while (_running)
            {
                if (!_active) { _wake.WaitOne(); next = Stopwatch.GetTimestamp(); continue; }
                long period = Stopwatch.Frequency / TargetRate;
                long now = Stopwatch.GetTimestamp();
                if (now < next)
                {
                    long due = -(next - now) * 10_000_000L / Stopwatch.Frequency;
                    if (timer != IntPtr.Zero && SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                        WaitForSingleObject(timer, 50);
                    else _wake.WaitOne(Math.Clamp((int)Math.Ceiling((next - now) * 1000.0 / Stopwatch.Frequency), 1, 50));
                    continue;
                }
                // Skip missed deadlines rather than enqueue catch-up frames.
                next += ((now - next) / period + 1) * period;
                if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) { DroppedWakeups++; continue; }
                try { _dispatcher.BeginInvoke(DispatcherPriority.Render, _dispatch); }
                catch (InvalidOperationException) { Volatile.Write(ref _pending, 0); break; }
            }
        }
        finally { if (timer != IntPtr.Zero) CloseHandle(timer); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _wake.Set();
        _thread?.Join();
        _wake.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public int Left, Top, Right, Bottom, WorkLeft, WorkTop, WorkRight, WorkBottom, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    // DEVMODEW is 220 bytes; only dmSize and dmDisplayFrequency are needed.
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DisplayMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(184)] public int Frequency;
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int index, ref DisplayMode mode);
    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW")]
    private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, IntPtr name, uint flags, uint access);
    [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr timer, ref long due,
        int period, IntPtr callback, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
