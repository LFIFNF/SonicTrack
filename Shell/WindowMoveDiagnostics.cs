using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SonicTrack.App.Shell;

/// <summary>Controlled movement of our own diagnostic window; no input injection or user-setting changes.</summary>
internal static class WindowMoveDiagnostics
{
    internal static void Attach(LauncherWindow window, string variant)
    {
        if (variant == "verify") { AttachVerification(window); return; }
        if (variant == "no-blur") ((System.Windows.Shapes.Rectangle)window.FindName("Backdrop")).Effect = null;
        var clock = Stopwatch.StartNew(); var process = Process.GetCurrentProcess();
        var costs = new List<double>(); var intervals = new List<double>();
        long last = 0, start = 0, calls = 0, composition = 0; TimeSpan cpu = default;
        var handle = IntPtr.Zero; NativeRect origin = default;
        EventHandler onRendering = (_, _) => { if (start != 0) composition++; };
        SonicTrack.WaveCore.RenderPump? pump = null;
        window.Loaded += (_, _) =>
        {
            handle = new WindowInteropHelper(window).Handle; GetWindowRect(handle, out origin);
            SendMessage(handle, 0x231, IntPtr.Zero, IntPtr.Zero); // WM_ENTERSIZEMOVE
            CompositionTarget.Rendering += onRendering;
            pump = new SonicTrack.WaveCore.RenderPump(window.Dispatcher, () =>
            {
                long before = Stopwatch.GetTimestamp();
                if (clock.Elapsed.TotalSeconds >= 1 && start == 0) { start = before; cpu = process.TotalProcessorTime; }
                double phase = clock.Elapsed.TotalSeconds;
                SetWindowPos(handle, IntPtr.Zero, origin.Left + (int)(110 * Math.Sin(phase * 2)), origin.Top + (int)(40 * Math.Sin(phase * 3)), 0, 0, 0x15);
                if (start != 0)
                {
                    costs.Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds); calls++;
                    if (last != 0) intervals.Add(Stopwatch.GetElapsedTime(last, before).TotalMilliseconds);
                    last = before;
                    if (Stopwatch.GetElapsedTime(start).TotalSeconds >= 6)
                    {
                        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                        static double P95(List<double> values) { values.Sort(); return values.Count == 0 ? 0 : values[(int)((values.Count - 1) * .95)]; }
                        string report = $"variant={variant}\nwindow_DIP={window.ActualWidth}x{window.ActualHeight}\nwallpaper_loaded={((System.Windows.Controls.Grid)window.FindName("BackdropHost")).Visibility == Visibility.Visible}\nseconds={seconds:F2}\nmove_callbacks_Hz={calls / seconds:F1}\nWPF_render_callbacks_Hz={composition / seconds:F1}\nmove_call_p95_ms={P95(costs):F3}\ncallback_interval_p95_ms={P95(intervals):F3}\nprocess_CPU_one_core_percent={(process.TotalProcessorTime - cpu).TotalSeconds / seconds * 100:F1}\nNote: controlled SetWindowPos at target 240 Hz, interactive move notifications; not mouse input latency or screen presentation FPS.\n";
                        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "window-move-" + variant + ".txt"), report);
                        SendMessage(handle, 0x232, IntPtr.Zero, IntPtr.Zero); // WM_EXITSIZEMOVE
                        window.Close();
                    }
                }
            });
            pump.Start(240);
        };
        window.Closed += (_, _) => { pump?.Dispose(); CompositionTarget.Rendering -= onRendering; process.Dispose(); };
    }
    private static void AttachVerification(LauncherWindow window)
    {
        window.Loaded += async (_, _) =>
        {
            try
            {
                static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
                var backdrop = (System.Windows.Shapes.Rectangle)window.FindName("Backdrop");
                Assert(backdrop.Effect == null, "No per-move live blur effect");
                var brush = backdrop.Fill as ImageBrush;
                Assert(brush?.ImageSource.IsFrozen == true, "Loaded blurred wallpaper is immutable");
                var handle = new WindowInteropHelper(window).Handle; GetWindowRect(handle, out var origin);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Rect before = brush!.Viewport;
                SendMessage(handle, 0x231, IntPtr.Zero, IntPtr.Zero);
                SetWindowPos(handle, IntPtr.Zero, origin.Left + 70, origin.Top + 35, 0, 0, 0x15);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(brush.Viewport == before, "Interactive movement reuses background");
                SendMessage(handle, 0x232, IntPtr.Zero, IntPtr.Zero);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(brush.Viewport != before, "Background realigns after movement");
                WallpaperBackdrop.Align(backdrop, brush);
                Rect aligned = brush.Viewport;
                SetWindowPos(handle, IntPtr.Zero, origin.Left, origin.Top, 0, 0, 0x15);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(brush.Viewport != aligned, "Programmatic movement realigns too");
                window.WindowState = WindowState.Maximized;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(((System.Windows.Controls.Border)window.FindName("Frame")).CornerRadius.TopLeft == 0, "Maximized frame state");
                window.WindowState = WindowState.Normal;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(((System.Windows.Controls.Border)window.FindName("Frame")).CornerRadius.TopLeft == 16, "Restored frame state");
                using var displayPump = new SonicTrack.V61.PreviewFramePump(window.Dispatcher, () => { });
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 200; i++) { displayPump.InvalidateDisplay(); displayPump.UpdateDisplay(handle); }
                double uncached = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long queries = displayPump.DisplayQueryCount;
                start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 200; i++) displayPump.UpdateDisplay(handle);
                double cached = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Assert(displayPump.DisplayQueryCount == queries, "Same-display moves avoid refresh-mode queries");
                int rate = displayPump.TargetRate;
                displayPump.InvalidateDisplay(); displayPump.UpdateDisplay(handle);
                Assert(displayPump.DisplayQueryCount == queries + 1 && displayPump.TargetRate == rate, "Display configuration invalidation forces a fresh query");
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "window-move-verification.txt"),
                    $"PASS: frozen preblurred wallpaper; no live blur; move background reuse and final alignment; noninteractive move alignment; maximize/restore; display mode cache and invalidation.\n200 uncached display queries ms={uncached:F3}\n200 cached updates ms={cached:F3}\nrefresh_Hz={rate}\n");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "window-move-verification.txt"), ex.ToString()); }
            finally { window.Close(); }
        };
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wp, IntPtr lp);
}
