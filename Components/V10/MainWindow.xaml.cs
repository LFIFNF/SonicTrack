using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SonicTrack.WaveCore;

public partial class MainWindow : Window
{
    private GpuWaveView _gpu;
    private readonly RenderPump _pump;
    private readonly AudioCapture _audio = new();
    private readonly float[] _snapshot = new float[131072];
    private readonly float[] _demo = new float[96000], _stress = new float[96000];
    private readonly FrameMetrics _metrics = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Settings _settings;
    private readonly string? _profileMode;
    private readonly bool _isolated;
    private DesktopOverlayWindow? _overlay;
    private bool _building = true, _loaded, _closing;
    private long _version, _lastStatus, _lastPackets, _lastFrames, _lastCalls, _lastOverlayFrames;
    private Color _background = Color.FromRgb(16, 18, 23);
    private string? _overlayError;
    private long _profileStart, _profileFrames, _profileOverlayFrames, _profileCalls;
    private TimeSpan _profileCpu;
    internal GpuWaveView Preview => _gpu;
    internal DesktopOverlayWindow? Overlay => _overlay;
    internal Settings Parameters => _settings;

    public MainWindow(bool verification = false, string? profileMode = null)
    {
        _profileMode = profileMode; _isolated = verification || profileMode != null;
        _settings = _isolated ? new Settings { OverlayOpen = false } : Settings.Load();
        if (profileMode != null)
        {
            _settings.Gain = 12; _settings.DurationMs = 200; _settings.Thickness = 1.5;
            _settings.OverlayOpen = profileMode != "preview";
            _settings.PreviewEnabled = !profileMode.StartsWith("overlay");
            _settings.ShowUnderlay = profileMode.Contains("underlay");
            _settings.Antialiasing = !profileMode.Contains("no-aa");
            if (profileMode.Contains("large")) { _settings.OverlayWidth = 1600; _settings.OverlayHeight = 600; }
        }
        _gpu = new GpuWaveView(_settings.Antialiasing);
        _pump = new RenderPump(Dispatcher, DrawFrame);
        InitializeComponent();
        ViewHost.Children.Add(_gpu);
        AddSlider("增益", .2, 12, _settings.Gain, .1, v => _settings.Gain = v, "F1");
        AddSlider("显示时长（ms）", 20, 200, _settings.DurationMs, 1, v => _settings.DurationMs = v, "F0");
        AddSlider("线宽（px）", 1, 8, _settings.Thickness, .1, v => _settings.Thickness = v, "F1");
        AddSlider("悬浮窗宽度（px）", 320, 1600, _settings.OverlayWidth, 10, v => _settings.OverlayWidth = v, "F0");
        AddSlider("悬浮窗高度（px）", 80, 600, _settings.OverlayHeight, 10, v => _settings.OverlayHeight = v, "F0");
        WaveColor.SelectedIndex = ColorIndex(_settings.Color); UnderlayColor.SelectedIndex = ColorIndex(_settings.UnderlayColor);
        Antialiasing.IsChecked = _settings.Antialiasing; ShowUnderlay.IsChecked = _settings.ShowUnderlay;
        TopmostControl.IsChecked = _settings.Topmost; ClickThrough.IsChecked = _settings.ClickThrough;
        Backplate.IsChecked = _settings.Backplate; PreviewEnabled.IsChecked = _settings.PreviewEnabled;
        TargetRate.SelectedIndex = Array.IndexOf(new[] { 60, 120, 144, 240 }, _settings.TargetHz);
        for (int i = 0; i < _demo.Length; i++)
        {
            _demo[i] = (float)(.3 * Math.Sin(i * .025) * (.65 + .35 * Math.Sin(i * .003)));
            _stress[i] = (float)(.25 * Math.Sin(i * .91) + .2 * Math.Sin(i * 1.83) + .15 * Math.Sin(i * .031) + (i % 37 == 0 ? .35 : 0));
        }
        if (_isolated) Source.SelectedIndex = 2;
        _building = false; Apply();
        _save.Tick += (_, _) => SaveNow();
        StateChanged += (_, _) => { if (_loaded) { ConfigureRefresh(); ResetMetrics(); } };
        _audio.Stopped += message => Dispatcher.BeginInvoke(() => { if (!_closing) DeviceStatus.Text = message; });
        Loaded += (_, _) => OnWindowLoaded();
        IsVisibleChanged += (_, _) => { if (_loaded && !_closing) { ConfigureRefresh(); ResetMetrics(); } };
        Closing += (_, e) =>
        {
            // Integrated app: closing the settings window keeps a running desktop overlay alive.
            if (KeepOverlayOnClose()) { e.Cancel = true; HideKeepingOverlay(); return; }
            _closing = true; _save.Stop(); _pump.Dispose();
            if (_overlay != null) { RememberPosition(); _overlay.Dispose(); }
            if (!_isolated) SaveNow();
            _audio.Dispose(); _gpu.Dispose();
        };
    }
    private static readonly string[] ColorsList = { "#FF7F66", "#51D6E8", "#FFFFFF", "#222222" };
    private static int ColorIndex(string color) => Array.IndexOf(ColorsList, color);
    private void AddSlider(string name, double min, double max, double value, double tick, Action<double> set, string format)
    {
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = name + "：" + value.ToString(format) };
        var slider = new SilverCoralSlider { Minimum = min, Maximum = max, Value = value, Height = 52,
            SmallChange = tick, LargeChange = tick * 10, TickFrequency = tick, IsSnapToTickEnabled = true };
        System.Windows.Automation.AutomationProperties.SetName(slider, name);
        slider.ValueChanged += (_, _) => { label.Text = name + "：" + slider.Value.ToString(format); if (!_building) { set(slider.Value); Changed(); } };
        ParameterPanel.Children.Add(label); ParameterPanel.Children.Add(slider);
    }
    private void Apply()
    {
        _background = BackgroundChoice.SelectedIndex switch { 1 => Colors.White, 2 => (Color)ColorConverter.ConvertFromString(_settings.Color), _ => Color.FromRgb(16, 18, 23) };
        _gpu.Visibility = _settings.PreviewEnabled ? Visibility.Visible : Visibility.Collapsed;
        PreviewHint.Visibility = _settings.PreviewEnabled ? Visibility.Collapsed : Visibility.Visible;
        PreviewHint.Text = _overlay != null ? "预览已暂停，桌面悬浮窗继续更新" : "预览已暂停。打开悬浮窗或启用预览以继续显示声波。";
        _gpu.Apply(_settings, _background); _overlay?.Apply(_settings);
        UnderlayColor.IsEnabled = _settings.ShowUnderlay;
        ViewHost.Background = new SolidColorBrush(_background);
    }
    private void Changed()
    {
        Apply(); ConfigureRefresh();
        if (_isolated) return;
        SaveStatus.Text = "正在自动保存…"; if (!_save.IsEnabled) _save.Start();
    }
    private void SaveNow()
    {
        _save.Stop();
        try { _settings.Save(); SaveStatus.Text = "参数已自动保存"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SaveStatus.Text = "保存失败：" + ex.Message; }
    }
    private bool PreviewActive => _settings.PreviewEnabled && WindowState != WindowState.Minimized && IsVisible;
    private void ConfigureRefresh()
    {
        if (_loaded && !_closing && (PreviewActive || _overlay != null)) _pump.Start(_settings.TargetHz);
        else _pump.Stop();
    }
    private void ResetMetrics() { _lastStatus = 0; _metrics.Reset(); }
    private void DrawFrame()
    {
        if (_closing || (!PreviewActive && _overlay == null)) return;
        long start = Stopwatch.GetTimestamp(); int count; long version;
        // One audio snapshot per tick, shared by preview and desktop output.
        if (Source.SelectedIndex == 0) count = _audio.Snapshot(_snapshot, _settings.DurationMs, out version);
        else
        {
            count = (int)(48000 * _settings.DurationMs / 1000);
            var signal = Source.SelectedIndex == 1 ? _demo : _stress;
            int offset = (int)(_clock.Elapsed.TotalSeconds * 48000) % signal.Length;
            int first = Math.Min(count, signal.Length - offset);
            Array.Copy(signal, offset, _snapshot, 0, first);
            if (first < count) Array.Copy(signal, 0, _snapshot, first, count - first);
            version = ++_version;
        }
        if (PreviewActive) _gpu.Update(_snapshot, count, version);
        try { _overlay?.Update(_snapshot, count, version); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _overlayError = "悬浮窗输出失败：" + ex.Message; _overlay?.Dispose(); Describe();
        }
        _metrics.Record(start, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        if (_profileMode != null && _clock.Elapsed.TotalSeconds > 2)
        {
            if (_profileStart == 0)
            {
                _profileStart = start; _profileCpu = Process.GetCurrentProcess().TotalProcessorTime;
                _profileFrames = _gpu.FrameCount; _profileOverlayFrames = _overlay?.PresentCount ?? 0; _profileCalls = _metrics.Calls;
            }
            if (Stopwatch.GetElapsedTime(_profileStart).TotalSeconds >= 7) { FinishProfile(); return; }
        }
        if (_lastStatus == 0)
        {
            _lastStatus = start; _lastFrames = _gpu.FrameCount; _lastOverlayFrames = _overlay?.PresentCount ?? 0;
            _lastPackets = _audio.PacketCount; _lastCalls = _metrics.Calls; return;
        }
        double seconds = Stopwatch.GetElapsedTime(_lastStatus, start).TotalSeconds;
        if (seconds < 1) return;
        var timing = _metrics.Read(); long overlayFrames = _overlay?.PresentCount ?? 0;
        PerformanceStatus.Text = $"更新 {(_metrics.Calls - _lastCalls) / seconds:F0} Hz · 预览/悬浮提交 {Math.Max(0, _gpu.FrameCount - _lastFrames) / seconds:F0}/{Math.Max(0, overlayFrames - _lastOverlayFrames) / seconds:F0} Hz · 间隔/处理 P95 {timing.IntervalP95:F1}/{timing.CostP95:F2} ms";
        if (_overlay != null) PerformanceStatus.Text += $" · 悬浮传输/合成 {_overlay.ReadbackMs:F2}/{_overlay.PresentMs:F2} ms";
        if (Source.SelectedIndex == 0) PerformanceStatus.Text += $" · 音频 {(_audio.PacketCount - _lastPackets) / seconds:F0} 次/秒";
        _lastStatus = start; _lastFrames = _gpu.FrameCount; _lastOverlayFrames = overlayFrames;
        _lastCalls = _metrics.Calls; _lastPackets = _audio.PacketCount;
        Describe();
    }
    private void FinishProfile()
    {
        double seconds = Stopwatch.GetElapsedTime(_profileStart).TotalSeconds;
        var timing = _metrics.Read(); var overlay = _overlay;
        string report = $"mode={_profileMode}\nrenderer={_gpu.Renderer}\nMSAA={_gpu.Samples}\nunderlay={_settings.ShowUnderlay}\npreview_DIP={ViewHost.ActualWidth:F0}x{ViewHost.ActualHeight:F0}\noverlay_px={_settings.OverlayWidth}x{_settings.OverlayHeight}\nseconds={seconds:F2}\ncallbacks_Hz={(_metrics.Calls - _profileCalls) / seconds:F1}\npreview_submissions_Hz={(_gpu.FrameCount - _profileFrames) / seconds:F1}\noverlay_present_calls_Hz={((overlay?.PresentCount ?? 0) - _profileOverlayFrames) / seconds:F1}\ninterval_p95_ms={timing.IntervalP95:F2}\nprocessing_p95_ms={timing.CostP95:F3}\nCPU_one_core_percent={(Process.GetCurrentProcess().TotalProcessorTime - _profileCpu).TotalSeconds / seconds * 100:F1}\npreview_mesh_builds={_gpu.MeshBuildCount}\npreview_uploads={_gpu.UploadCount}\noverlay_mesh_builds={overlay?.View.MeshBuildCount}\noverlay_uploads={overlay?.View.UploadCount}\noverlay_readback_ms={overlay?.ReadbackMs:F3}\noverlay_present_ms={overlay?.PresentMs:F3}\nNote: submissions/present calls are not independently measured screen presentation FPS. Shared independent clock; no WPF Rendering event or Image output.\n";
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "performance-" + _profileMode + ".txt"), report);
        if (overlay != null) SavePng(overlay.Capture(), "desktop-wave-preview.png");
        Close();
    }
    internal static void SavePng(BitmapSource bitmap, string name)
    {
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name)); png.Save(file);
    }
    private void Connect()
    {
        try { _audio.Start(); Describe(); }
        catch (Exception ex) { DeviceStatus.Text = "无法捕获音频：" + ex.Message; }
    }
    private void Describe()
    {
        var view = PreviewActive ? _gpu : _overlay?.View ?? _gpu;
        DeviceStatus.Text = $"GPU：{view.Renderer} · " + (view.Samples > 1 ? $"{view.Samples}× 抗锯齿" : "抗锯齿关闭") + $" · 独立 {_settings.TargetHz} Hz · " +
            (Source.SelectedIndex == 0 ? _audio.Description : Source.SelectedIndex == 1 ? "演示波形" : "复杂压力波形");
        if (_gpu.Error != null) DeviceStatus.Text = "GPU 初始化失败：" + _gpu.Error;
        if (_overlayError != null) DeviceStatus.Text += " · " + _overlayError;
    }
    private void RememberPosition()
    {
        if (_overlay == null) return;
        _settings.Left = _overlay.Left; _settings.Top = _overlay.Top;
    }
    internal void OpenOverlay()
    {
        if (_overlay != null) return;
        try
        {
            var overlay = new DesktopOverlayWindow(_settings); _overlay = overlay;
            overlay.PositionSettled += () => { RememberPosition(); Changed(); };
            overlay.Closed += () =>
            {
                _settings.Left = overlay.Left; _settings.Top = overlay.Top;
                _overlay = null; OverlayButton.Content = "打开桌面悬浮窗";
                if (!_closing) { _settings.OverlayOpen = false; Changed(); ResetMetrics(); }
                OnOverlayClosed();
            };
            _settings.OverlayOpen = true; OverlayButton.Content = "关闭桌面悬浮窗";
            _overlayError = null; PreviewHint.Text = "预览已暂停，桌面悬浮窗继续更新";
            ConfigureRefresh(); ResetMetrics();
            OverlayStateChanged?.Invoke();
        }
        catch (Exception ex) { _settings.OverlayOpen = false; _overlayError = "悬浮窗创建失败：" + ex.Message; Describe(); }
    }
    internal void CloseOverlay() { _overlay?.Dispose(); }
    private void ToggleOverlay(object sender, RoutedEventArgs e)
    {
        if (_overlay == null) { OpenOverlay(); Changed(); } else CloseOverlay();
    }
    private void AntialiasingChanged(object sender, RoutedEventArgs e)
    {
        if (_building) return;
        _settings.Antialiasing = Antialiasing.IsChecked == true; _pump.Stop();
        ViewHost.Children.Remove(_gpu); _gpu.Dispose();
        _gpu = new GpuWaveView(_settings.Antialiasing); ViewHost.Children.Add(_gpu); ViewHost.UpdateLayout();
        Changed(); ResetMetrics(); Describe();
    }
    private void TargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || TargetRate.SelectedIndex < 0) return;
        _settings.TargetHz = new[] { 60, 120, 144, 240 }[TargetRate.SelectedIndex]; Changed(); ResetMetrics();
    }
    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_building) return;
        _settings.ShowUnderlay = ShowUnderlay.IsChecked == true; _settings.Topmost = TopmostControl.IsChecked == true;
        _settings.ClickThrough = ClickThrough.IsChecked == true; _settings.Backplate = Backplate.IsChecked == true;
        _settings.PreviewEnabled = PreviewEnabled.IsChecked == true; Changed(); ResetMetrics();
    }
    private void SourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _audio.Dispose(); ResetMetrics(); if (Source.SelectedIndex == 0) Connect(); else Describe();
    }
    private void ColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        _settings.Color = ColorsList[WaveColor.SelectedIndex]; _settings.UnderlayColor = ColorsList[UnderlayColor.SelectedIndex]; Changed();
    }
    private void BackgroundChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        _background = BackgroundChoice.SelectedIndex switch { 1 => Colors.White, 2 => (Color)ColorConverter.ConvertFromString(_settings.Color), _ => Color.FromRgb(16, 18, 23) };
        Apply();
    }
    private void Reconnect(object sender, RoutedEventArgs e) { if (Source.SelectedIndex == 0) Connect(); }
}
