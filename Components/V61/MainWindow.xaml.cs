using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SonicTrack.V61;

public partial class MainWindow : Window
{
    // Integrated app: own file names next to the shared exe; first launch reads the standalone V6.1 files.
    private static readonly string ParametersPath = Path.Combine(AppContext.BaseDirectory, "v61-parameters.json");
    private static readonly string WindowSettingsPath = Path.Combine(AppContext.BaseDirectory, "v61-window.json");
    private static readonly string StandaloneDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../v6.1-waterfall-3d/bin/Release/net8.0-windows"));
    private static string ReadPath(string path, string standaloneName)
        => File.Exists(path) ? path : Path.Combine(StandaloneDirectory, standaloneName);
    private static readonly string[] ParameterSliders = {
        "SlDensity", "SlRows", "SlGain", "SlRate", "SlContrast", "SlStability",
        "SlBlackOpacity", "SlYaw", "SlPitch", "SlDist", "SlPanX", "SlPanY", "SlHistoryOffset", "SlHeightCompression"
    };
    private const int FftSize = 8192;
    private const int DefaultRate = 30;
    private const float DefaultContrast = 1.11f;
    private const float VolFloor = 0.05f;
    private const float FftFullScale = FftSize * 0.25f;

    private WasapiCapture? _capture;
    private readonly float[] _ring = new float[FftSize * 4];
    private int _write;
    private int _filled;
    private readonly object _audioGate = new();
    private long _pushed;

    private volatile bool _run;
    private Thread? _worker;
    private volatile int _cols = 120;
    private volatile int _rows = 120;
    private volatile int _historyOffset;
    private bool _uiReady;
    private bool _windowClosed;
    private WindowState _lastNonMinimizedState = WindowState.Normal;

    private int _sr = 48000;
    private int _channels = 2;
    private int _blockAlign = 8;
    private bool _useFloat = true;
    private int _bitsPerSample = 32;
    private long _lastAudioTicks;
    private string? _captureError;
    private string _deviceLabel = "";

    private MMDevice? _renderDevice;
    private volatile float _userGain = 7.33f;
    private volatile int _targetRate = DefaultRate;
    private volatile float _contrast = DefaultContrast;
    private volatile float _stabilityMs = 0f;
    private volatile float _volShown = 1f;
    private volatile float _volScalar = 1f;
    private volatile float _volGain = 1f;
    private long _volPollTicks;
    private int _allocMs;
    private int _requestedMs;
    private bool _timerRes;
    private long _cbStamp;
    private int _cbCount;
    private long _cbTicks;
    private int _cbAvgUs;

    private readonly object _frameGate = new();
    private readonly SpectrumDisplayHistory _history = new();
    private float[] _heightRead = Array.Empty<float>();
    private float[] _histRead = Array.Empty<float>();
    private int _histCols;
    private int _histRows;
    private long _frameGen;
    private int _colCount;
    private long _seenGen = -1;
    private long _statusTicks;
    private int _colMark;
    private readonly SpectrumScrollClock _scrollClock = new();
    private readonly PreviewFramePump _previewPump;
    private TimeSpan _lastCompositionTime = TimeSpan.MinValue;
    private double _lastUiSeconds;
    private long _drawMark;
    private long _desktopDrawMark;
    private int _slowFrames;
    private readonly FrameTiming _frameTiming = new();
    private DesktopOverlayWindow? _desktop;
    private float _blackOpacity = NativeSurfaceView.InteriorBlackAlpha;

    private double _yaw = 0;
    private double _pitch = 85;
    private double _dist = 6;
    private double _panX = -3.2;
    private double _panY = .1;

    // 重置视角的目标：最近一次「保存参数」写入（或启动时从参数文件读到）的视角，
    // 预览平移取打开组件时从窗口文件读到的值（「保存参数」时更新为当时的位置）。
    // 参数文件没有某项时，该项退回原来的默认视角。
    private const double DefaultYaw = 0, DefaultPitch = 85, DefaultDist = 6, DefaultPanX = -3.2, DefaultPanY = .1;
    private double _savedYaw = DefaultYaw, _savedPitch = DefaultPitch, _savedDist = DefaultDist,
        _savedPanX = DefaultPanX, _savedPanY = DefaultPanY;
    private Point _savedPreviewOffset;

    public MainWindow()
    {
        InitializeComponent();
        _previewPump = new PreviewFramePump(Dispatcher, () => OnUiTick(null, EventArgs.Empty));
        RestoreWindowSize();
        _lastNonMinimizedState = WindowState;
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _lastNonMinimizedState = WindowState;
            UpdatePreviewPump();
        };
        var savedParameters = LoadParameters();
        RememberSavedViewpoint(savedParameters);
        _cols = (int)SlDensity.Value;
        _rows = (int)SlRows.Value;
        _userGain = (float)SlGain.Value;
        _targetRate = (int)Math.Round(SlRate.Value);
        _contrast = (float)SlContrast.Value;
        _stabilityMs = (float)SlStability.Value;
        _blackOpacity = (float)(SlBlackOpacity.Value / 100);
        _yaw = SlYaw.Value;
        _pitch = SlPitch.Value;
        _dist = SlDist.Value;
        _panX = SlPanX.Value;
        _panY = SlPanY.Value;
        _uiReady = true;
        UpdateHeightControls();
        UpdateLabels();
        if (savedParameters?.Sliders.TryGetValue("SlHistoryOffset", out double offset) == true && double.IsFinite(offset))
            SlHistoryOffset.Value = Math.Clamp(offset, SlHistoryOffset.Minimum, SlHistoryOffset.Maximum);
        ApplyViewpoint();
        _history.Configure(_rows, _cols, _targetRate);
        _histCols = _history.Columns;
        _histRows = _history.Rows;
        View.Apply(new float[_histCols * _histRows], _histRows, _histCols, _history.VisibleColumns);
        LocationChanged += (_, _) => UpdatePreviewPump();
        CompositionTarget.Rendering += OnCompositionTick;
        SourceInitialized += (_, _) =>
        {
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)
                ?.AddHook(OnDisplayMessage);
            UpdatePreviewPump();
        };
        Loaded += OnLoaded;
        Loaded += (_, _) => { UpdatePreviewPump(); _previewPump.Start(); };
        Closing += (_, _) => SaveWindowSize();
        Closed += (_, _) =>
        {
            _windowClosed = true;
            _previewPump.Dispose();
            CompositionTarget.Rendering -= OnCompositionTick;
            _desktop?.Close();
            StopAll();
        };
    }

    private void UpdatePreviewPump()
    {
        if (_windowClosed) return;
        _previewPump.UpdateDisplay(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        // Native-only preview needs its own clock. A live WPF bitmap already keeps
        // composition paced to presentation, so retain that clock for the overlay.
        _previewPump.SetActive(WindowState != WindowState.Minimized && _desktop == null);
        if (WindowState == WindowState.Minimized && _desktop == null)
        { _lastUiSeconds = 0; _frameTiming.Reset(); }
    }

    private IntPtr OnDisplayMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x007E || message == 0x02E0) // WM_DISPLAYCHANGE / WM_DPICHANGED
        {
            _previewPump.InvalidateDisplay();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdatePreviewPump));
        }
        return IntPtr.Zero;
    }

    private void RestoreWindowSize()
    {
        try
        {
            var settings = WindowSettings.Load(ReadPath(WindowSettingsPath, "window.json"));
            if (settings == null) return;
            Width = Math.Clamp(settings.Width, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
            Height = Math.Clamp(settings.Height, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
            if (double.IsFinite(settings.PreviewOffsetX) && double.IsFinite(settings.PreviewOffsetY))
            {
                View.MovePreview(settings.PreviewOffsetX, settings.PreviewOffsetY);
                _savedPreviewOffset = View.PreviewOffset;
            }
            if (settings.Maximized) WindowState = WindowState.Maximized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Debug.WriteLine($"Window settings load failed: {ex.Message}"); }
    }

    private void SaveWindowSize()
    {
        if (!IsLoaded) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(0, 0, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        try
        {
            new WindowSettings { Width = bounds.Width, Height = bounds.Height,
                Maximized = _lastNonMinimizedState == WindowState.Maximized,
                PreviewOffsetX = View.PreviewOffset.X, PreviewOffsetY = View.PreviewOffset.Y }.Save(WindowSettingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Debug.WriteLine($"Window settings save failed: {ex.Message}"); }
    }

    private ParameterSettings? LoadParameters()
    {
        try
        {
            var settings = ParameterSettings.Load(ReadPath(ParametersPath, "parameters.json"));
            if (settings == null) return null;
            foreach (string name in ParameterSliders)
            {
                // Restore history position after its range has been recalculated.
                if (name == "SlHistoryOffset") continue;
                var slider = (System.Windows.Controls.Slider)FindName(name);
                if (settings.Sliders.TryGetValue(name, out double value) && double.IsFinite(value))
                    slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
            }
            CbDesktopTopmost.IsChecked = settings.DesktopTopmost;
            CbDesktopClickThrough.IsChecked = settings.DesktopClickThrough;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            BtnSaveParameters.ToolTip = $"参数文件读取失败，已使用默认参数：{ex.Message}";
            return null;
        }
    }

    private void OnSaveParameters(object sender, RoutedEventArgs e)
    {
        var settings = new ParameterSettings
        {
            DesktopTopmost = CbDesktopTopmost.IsChecked == true,
            DesktopClickThrough = CbDesktopClickThrough.IsChecked == true
        };
        foreach (string name in ParameterSliders)
            settings.Sliders[name] = ((System.Windows.Controls.Slider)FindName(name)).Value;
        try
        {
            settings.Save(ParametersPath);
            RememberSavedViewpoint(settings);
            _savedPreviewOffset = View.PreviewOffset;
            BtnSaveParameters.Content = "保存参数";
            MessageBox.Show(this, "参数已保存，下次启动将自动恢复。", "保存参数", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "保存参数", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDesktopToggle(object sender, RoutedEventArgs e)
    {
        if (_desktop != null) { _desktop.Close(); return; }
        OpenOverlay();
    }

    internal void OpenOverlay()
    {
        if (_desktop != null || _windowClosed) return;
        var desktop = new DesktopOverlayWindow(View.ActualWidth, View.ActualHeight);
        _desktop = desktop;
        desktop.Topmost = CbDesktopTopmost.IsChecked == true;
        desktop.ClickThrough = CbDesktopClickThrough.IsChecked == true;
        desktop.RenderView.InteriorBlackOpacity = _blackOpacity;
        desktop.SetPreviewFraming(new Size(View.ActualWidth, View.ActualHeight), View.PreviewOffset);
        desktop.RenderView.SetViewpoint(_yaw, _pitch, _dist, _panX, _panY);
        lock (_frameGate)
        {
            var snapshot = new float[_histRows * _histCols];
            var heights = new float[snapshot.Length];
            _history.CopyNewestFirst(snapshot, _historyOffset);
            MapHeights(snapshot, heights);
            desktop.RenderView.Apply(snapshot, _histRows, _histCols, _history.VisibleColumns, heightLevels: heights);
            desktop.RenderView.SetScrollOffset(Math.Max(0, _history.CommittedColumns - _scrollClock.Position));
            _frameGen++; // Ensure both presentations receive the next snapshot.
        }
        desktop.Closed += (_, _) =>
        {
            if (_desktop == desktop) _desktop = null;
            UpdatePreviewPump();
            BtnDesktop.Content = "打开桌面悬浮窗";
            OnOverlayClosed();
        };
        desktop.Show();
        UpdatePreviewPump();
        desktop.Present();
        _desktopDrawMark = desktop.RenderView.FrameCount;
        BtnDesktop.Content = "关闭桌面悬浮窗";
        OverlayStateChanged?.Invoke();
    }

    internal void CloseOverlay() => _desktop?.Close();

    private void MapHeights(float[] levels, float[] heights)
    {
        float compression = (float)SlHeightCompression.Value;
        for (int i = 0; i < levels.Length; i++) heights[i] = SpectrumHeightMap.Map(levels[i], compression);
    }

    private void OnHeightCompressionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        UpdateHeightControls();
        lock (_frameGate) _frameGen++;
    }

    private void UpdateHeightControls()
    {
        LblHeightCompression.Text = $"柱高压缩 {SlHeightCompression.Value:0.00}";
    }

    private void OnBlackOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _blackOpacity = (float)(Math.Clamp(e.NewValue, 0, 100) / 100);
        if (_desktop != null) _desktop.RenderView.InteriorBlackOpacity = _blackOpacity;
        LblBlackOpacity.Text = $"内部黑柱不透明度：{_blackOpacity * 100:0}%";
    }

    private void OnDesktopOptions(object sender, RoutedEventArgs e)
    {
        if (_desktop == null) return;
        _desktop.Topmost = CbDesktopTopmost.IsChecked == true;
        _desktop.ClickThrough = CbDesktopClickThrough.IsChecked == true;
    }

    private void OnDensityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _cols = Math.Clamp((int)Math.Round(e.NewValue), 120, 1000);
        UpdateLabels();
    }

    private void OnRowsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _rows = Math.Clamp((int)Math.Round(e.NewValue), 10, 120);
        UpdateLabels();
    }

    private void OnHistoryOffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _historyOffset = (int)Math.Round(e.NewValue);
        lock (_frameGate) _frameGen++;
        UpdateHistoryLabel();
    }

    private void UpdateHistoryLabel() => LblHistoryOffset.Text = _historyOffset == 0
        ? "回看位置：实时"
        : $"回看位置：{_historyOffset} 列前（约 {_historyOffset / (double)_targetRate:0.00} s）";

    private void OnGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _userGain = Math.Clamp((float)e.NewValue, 1f, 100f);
        UpdateLabels();
    }

    private void OnRateChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _targetRate = Math.Clamp((int)Math.Round(e.NewValue), 30, 500);
        lock (_frameGate)
        {
            _history.Clear();
            _frameGen++;
        }
        UpdateLabels();
    }

    private void OnStabilityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _stabilityMs = (float)e.NewValue;
        UpdateLabels();
    }

    private void OnResetView(object sender, RoutedEventArgs e)
    {
        View.ResetPreviewOffset();
        View.MovePreview(_savedPreviewOffset.X, _savedPreviewOffset.Y);
        SlYaw.Value = _savedYaw;
        SlPitch.Value = _savedPitch;
        SlDist.Value = _savedDist;
        SlPanX.Value = _savedPanX;
        SlPanY.Value = _savedPanY;
    }

    /// <summary>Remembers the viewpoint stored in the parameter file as the target of 重置视角.</summary>
    private void RememberSavedViewpoint(ParameterSettings? settings)
    {
        double Saved(string name, System.Windows.Controls.Slider slider, double fallback)
            => settings != null && settings.Sliders.TryGetValue(name, out double value) && double.IsFinite(value)
                ? Math.Clamp(value, slider.Minimum, slider.Maximum) : fallback;
        _savedYaw = Saved("SlYaw", SlYaw, DefaultYaw);
        _savedPitch = Saved("SlPitch", SlPitch, DefaultPitch);
        _savedDist = Saved("SlDist", SlDist, DefaultDist);
        _savedPanX = Saved("SlPanX", SlPanX, DefaultPanX);
        _savedPanY = Saved("SlPanY", SlPanY, DefaultPanY);
    }

    private void OnContrastChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _contrast = Math.Clamp((float)e.NewValue, 0.5f, 3f);
        UpdateLabels();
    }

    private void OnYawChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _yaw = e.NewValue;
        ApplyViewpoint();
    }

    private void OnPitchChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _pitch = e.NewValue;
        ApplyViewpoint();
    }

    private void OnDistChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _dist = e.NewValue;
        ApplyViewpoint();
    }

    private void OnPanXChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _panX = e.NewValue;
        ApplyViewpoint();
    }

    private void OnPanYChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _panY = e.NewValue;
        ApplyViewpoint();
    }

    private void ApplyViewpoint()
    {
        View.SetViewpoint(_yaw, _pitch, _dist, _panX, _panY);
        _desktop?.RenderView.SetViewpoint(_yaw, _pitch, _dist, _panX, _panY);
        LblYaw.Text = $"水平 {_yaw:0}°";
        LblPitch.Text = $"俯仰 {_pitch:0}°";
        LblDist.Text = $"距离 {_dist:0}";
        LblPanX.Text = $"左右 {_panX:0.0}";
        LblPanY.Text = $"上下 {_panY:0.0}";
    }

    private void UpdateLabels()
    {
        LblBlackOpacity.Text = $"内部黑柱不透明度：{_blackOpacity * 100:0}%";
        LblDensity.Text = $"历史长度 {_cols} 列（{_cols / (double)_targetRate:0.00} s）";
        SlHistoryOffset.Maximum = Math.Max(0, _cols - SpectrumDisplayHistory.DisplayColumns(_rows, _cols, _targetRate));
        UpdateHistoryLabel();
        LblRows.Text = $"\u9891\u7387\u5206\u8fa8 {_rows} \u884c";
        LblGain.Text = $"\u589e\u76ca\u7cfb\u6570 {_userGain:0.00}";
        LblRate.Text = $"\u91c7\u6837\u7387 {_targetRate} \u5217/\u79d2";
        LblContrast.Text = $"\u5bf9\u6bd4\u5ea6 {_contrast:0.00}";
        LblStability.Text = $"新柱稳定 {_stabilityMs:0} ms（0 = 关闭）";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Integrated app: the component may already be running for its overlay (started hidden).
        if (_captureStarted) return;
        _captureStarted = true;
        try
        {
            BeginTimerRes();
            var device = PickDevice();
            _renderDevice = device;
            RefreshVolumeGain();
            _capture = OpenCapture(device);
            _deviceLabel = $"{device.FriendlyName}  {_sr}Hz {_channels}ch";
            _allocMs = AllocatedBufferMs(_capture, _sr);

            _run = true;
            _worker = new Thread(AnalyzeLoop) { IsBackground = true, Name = "v61-waterfall" };
            _worker.Start();
            Status.Text = $"\u6355\u83b7\u4e2d \u00b7 \u53f3=\u6700\u65b0\uff0c\u5411\u5de6\u6eda \u00b7 {_deviceLabel}";
        }
        catch (Exception ex)
        {
            StopAll();
            _captureError = "无法开始捕获: " + ex.Message;
            Status.Text = _captureError;
        }
    }

    private void ConfigureCapture(WasapiCapture capture)
    {
        var wf = capture.WaveFormat;
        AudioSampleDecoder.Validate(wf);
        _useFloat = AudioSampleDecoder.IsFloat(wf);
        _bitsPerSample = wf.BitsPerSample;
        _channels = Math.Max(1, wf.Channels);
        _blockAlign = wf.BlockAlign;
        _sr = wf.SampleRate;
        Interlocked.Exchange(ref _lastAudioTicks, Stopwatch.GetTimestamp());
    }

    private WasapiCapture OpenCapture(MMDevice device)
    {
        foreach (int ms in new[] { 5, 10 })
        {
            WasapiCapture? cap = TryLoopback(device, ms);
            if (cap == null) continue;
            _requestedMs = ms;
            return cap;
        }

        var fallback = new WasapiLoopbackCapture(device);
        fallback.DataAvailable += OnData;
        fallback.RecordingStopped += OnStopped;
        ConfigureCapture(fallback);
        fallback.StartRecording();
        _requestedMs = 100;
        return fallback;
    }

    private WasapiCapture? TryLoopback(MMDevice device, int bufferMs)
    {
        var cap = new EventLoopback(device, bufferMs);
        cap.DataAvailable += OnData;
        cap.RecordingStopped += OnStopped;
        try
        {
            ConfigureCapture(cap);
            cap.StartRecording();
            return cap;
        }
        catch
        {
            cap.DataAvailable -= OnData;
            cap.RecordingStopped -= OnStopped;
            try { cap.Dispose(); } catch { }
            return null;
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs a)
    {
        if (!_run) return;
        _run = false;
        Dispatcher.BeginInvoke(() =>
        {
            _captureError = a.Exception != null
                ? "\u6355\u83b7\u505c\u6b62: " + a.Exception.Message
                : "\u6355\u83b7\u5df2\u505c\u6b62";
            Status.Text = _captureError;
        });
    }

    private static MMDevice PickDevice()
    {
        using var en = new MMDeviceEnumerator();
        return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0)
        {
            long now = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _lastAudioTicks, now);
            if (_cbStamp != 0)
            {
                _cbTicks += now - _cbStamp;
                _cbCount++;
                if (_cbCount >= 20)
                {
                    _cbAvgUs = (int)(_cbTicks * 1_000_000L / Stopwatch.Frequency / _cbCount);
                    _cbTicks = 0;
                    _cbCount = 0;
                }
            }
            _cbStamp = now;
        }

        int frame = _blockAlign;
        int bytes = e.BytesRecorded;
        if (frame <= 0 || bytes < frame) return;
        int frames = bytes / frame;
        int ch = _channels;
        if (ch < 1) ch = 1;

        lock (_audioGate)
        {
            int sampleBytes = _bitsPerSample / 8;
            if (sampleBytes > 0 && frame >= ch * sampleBytes)
            {
                for (int i = 0; i < frames; i++)
                {
                    int o = i * frame;
                    float s = 0;
                    for (int c = 0; c < ch; c++)
                        s += AudioSampleDecoder.Read(e.Buffer.AsSpan(o + c * sampleBytes, sampleBytes), _bitsPerSample, _useFloat);
                    PushSample(s / ch);
                }
            }
        }
    }

    private void PushSample(float s)
    {
        _ring[_write] = s;
        _write++;
        if (_write == _ring.Length) _write = 0;
        if (_filled < _ring.Length) _filled++;
        _pushed++;
    }

    private void AnalyzeLoop()
    {
        IntPtr timer = CreateWaitableTimerEx(IntPtr.Zero, IntPtr.Zero, CreateWaitableTimerHighResolution, TimerAllAccess);
        try
        {
            var window = new float[FftSize];
            var re = new float[FftSize];
            var im = new float[FftSize];
            var sample = new float[FftSize];
            var column = new float[240];
            var stableColumn = new float[240];
            long lastSpectrumTicks = 0;
            long silencePresent = 0;
            for (int i = 0; i < FftSize; i++)
                window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));

            var b0 = Array.Empty<double>();
            var b1 = Array.Empty<double>();
            int bandRows = -1;
            int bandSr = -1;
            long presentedEnd = -1;
            long nextPresent = 0;

            while (_run)
            {
                long now = Stopwatch.GetTimestamp();
                long volumeNow = Environment.TickCount64;
                if (volumeNow - _volPollTicks >= 200)
                {
                    _volPollTicks = volumeNow;
                    RefreshVolumeGain();
                }
                int cols = Math.Clamp(_cols, 120, 1000);
                int rows = Math.Clamp(_rows, 10, 120);
                int rate = Math.Clamp(_targetRate, 30, 500);
                int hop = Math.Max(1, _sr / rate);
                long period = Stopwatch.Frequency / rate;

                bool have = false;
                long end = 0;
                lock (_audioGate)
                {
                    long pushed = _pushed;
                    int filled = _filled;
                    long oldest = pushed - filled;
                    end = presentedEnd < 0 ? pushed : presentedEnd + hop;
                    // A callback may deliver several hops together. Process them in order;
                    // resync only if the audio ring has actually overwritten needed samples.
                    bool behind = presentedEnd >= 0 && end - FftSize < oldest;
                    if (behind)
                        end = pushed;
                    have = filled >= FftSize && end <= pushed && end - FftSize >= oldest;
                    if (have && presentedEnd >= 0 && now < nextPresent)
                        have = false;
                    if (have)
                    {
                        int lag = (int)(pushed - end);
                        int start = _write - lag - FftSize;
                        int ring = _ring.Length;
                        while (start < 0) start += ring;
                        int first = Math.Min(FftSize, ring - start);
                        Array.Copy(_ring, start, sample, 0, first);
                        if (first < FftSize)
                            Array.Copy(_ring, 0, sample, first, FftSize - first);
                    }
                }

                if (have)
                {
                    if (bandRows != rows || bandSr != _sr)
                    {
                        b0 = new double[rows];
                        b1 = new double[rows];
                        BuildBands(rows, _sr, b0, b1);
                        bandRows = rows;
                        bandSr = _sr;
                        Array.Clear(stableColumn);
                    }

                    float rms = 0;
                    for (int i = 0; i < FftSize; i++)
                    {
                        float v = sample[i] * window[i];
                        re[i] = v;
                        im[i] = 0;
                        rms += sample[i] * sample[i];
                    }
                    rms = MathF.Sqrt(rms / FftSize);
                    Fft(re, im);

                    float gate = 0.00045f * _volScalar;
                    bool voiced = rms >= gate;
                    Array.Clear(column, 0, rows);
                    if (voiced)
                    {
                        float gain = _volGain * _userGain;
                        float scale = gain / FftFullScale;
                        int fftBins = FftSize / 2;
                        for (int b = 0; b < rows; b++)
                        {
                            float mag = BandMagnitude(re, im, b0[b], b1[b], fftBins);
                            column[b] = SpectrumLevelMap.ToDisplayLevel(mag * scale, _contrast);
                        }
                    }

                    // Stabilize only incoming spectra. Recorded history never morphs in place.
                    double elapsed = lastSpectrumTicks == 0 ? 1.0 / rate : (now - lastSpectrumTicks) / (double)Stopwatch.Frequency;
                    SpectrumStabilizer.Apply(column, stableColumn, rows, elapsed, _stabilityMs);
                    PushColumn(stableColumn, cols, rows);
                    lastSpectrumTicks = now;
                    silencePresent = now;
                    presentedEnd = end;
                    long current = Stopwatch.GetTimestamp();
                    if (nextPresent == 0 || current - nextPresent > period * 2)
                        nextPresent = current + period;
                    else
                        nextPresent += period;
                }

                else if (now - Interlocked.Read(ref _lastAudioTicks) > Stopwatch.Frequency / 5 &&
                         now - silencePresent >= period)
                {
                    // WASAPI may stop issuing callbacks when no application is playing.
                    Array.Clear(stableColumn);
                    PushColumn(stableColumn, cols, rows);
                    silencePresent = now;
                    lastSpectrumTicks = 0;
                    presentedEnd = -1;
                    nextPresent = 0;
                }

                long wake = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000;
                if (nextPresent > Stopwatch.GetTimestamp())
                    wake = nextPresent;
                else if (now - Interlocked.Read(ref _lastAudioTicks) > Stopwatch.Frequency / 5)
                    wake = silencePresent + period;
                WaitUntil(timer, wake);
            }
        }
        catch (Exception ex)
        {
            _run = false;
            Dispatcher.BeginInvoke(() => { _captureError = "分析中断: " + ex.Message; Status.Text = _captureError; });
        }
        finally
        {
            if (timer != IntPtr.Zero)
                CloseHandle(timer);
        }
    }

    private static void BuildBands(int rows, int sr, double[] b0, double[] b1)
    {
        double nyquist = Math.Max(1000, sr * 0.5);
        const double fLo = 40;
        double fHi = Math.Min(12000, nyquist);
        if (fHi < fLo * 2)
            fHi = nyquist;
        int fftBins = FftSize / 2;
        for (int b = 0; b < rows; b++)
        {
            double f0 = Warp(b / (double)rows, fLo, fHi);
            double f1 = Warp((b + 1) / (double)rows, fLo, fHi);
            double x0 = f0 / nyquist * fftBins;
            double x1 = f1 / nyquist * fftBins;
            b0[b] = Math.Clamp(x0, 1, fftBins - 1);
            b1[b] = Math.Clamp(x1, b0[b] + 1e-9, fftBins);
        }
    }

    private static float BandMagnitude(float[] re, float[] im, double binStart, double binEnd, int fftBins)
    {
        binStart = Math.Clamp(binStart, 1, fftBins - 1);
        binEnd = Math.Clamp(binEnd, binStart + 1e-9, fftBins);
        if (binEnd - binStart < 1.0)
            return MagLerp(re, im, 0.5 * (binStart + binEnd), fftBins);

        double sum = 0;
        double weight = 0;
        int i0 = (int)Math.Floor(binStart);
        int i1 = (int)Math.Ceiling(binEnd) - 1;
        if (i1 >= fftBins) i1 = fftBins - 1;
        for (int k = i0; k <= i1; k++)
        {
            double lo = Math.Max(binStart, k);
            double hi = Math.Min(binEnd, k + 1.0);
            double w = hi - lo;
            if (w <= 0) continue;
            float m = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
            sum += m * w;
            weight += w;
        }
        if (weight <= 0)
            return MagLerp(re, im, 0.5 * (binStart + binEnd), fftBins);
        return (float)(sum / weight);
    }

    private static float MagLerp(float[] re, float[] im, double bin, int fftBins)
    {
        int i = (int)Math.Floor(bin);
        if (i < 1) i = 1;
        if (i > fftBins - 2) i = fftBins - 2;
        float t = (float)(bin - i);
        float m0 = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]);
        float m1 = MathF.Sqrt(re[i + 1] * re[i + 1] + im[i + 1] * im[i + 1]);
        return m0 + (m1 - m0) * t;
    }

    private static double Warp(double t, double fLo, double fHi)
    {
        t = Math.Clamp(t, 0, 1);
        return fLo * Math.Pow(fHi / fLo, t);
    }

    private void RefreshVolumeGain()
    {
        float raw = float.NaN;
        var dev = _renderDevice;
        if (dev != null)
        {
            try { raw = dev.AudioEndpointVolume.MasterVolumeLevelScalar; }
            catch { raw = float.NaN; }
        }
        if (float.IsNaN(raw))
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                raw = def.AudioEndpointVolume.MasterVolumeLevelScalar;
            }
            catch
            {
                raw = 1f;
            }
        }
        if (float.IsNaN(raw)) raw = 1f;
        if (raw < 0f) raw = 0f;
        else if (raw > 1f) raw = 1f;
        _volShown = raw;
        float used = raw < VolFloor ? VolFloor : raw;
        _volScalar = used;
        _volGain = 1f / used;
    }

    /// <summary>
    /// History is age-major, age 0 = newest. Bar 0 = low frequency.
    /// </summary>
    private void PushColumn(float[] column, int cols, int rows)
    {
        lock (_frameGate)
        {
            bool shapeChanged = _history.Configure(rows, cols, _targetRate);
            bool committed = _history.Push(column);
            _histCols = _history.Columns;
            _histRows = _history.Rows;
            if (shapeChanged || committed) _frameGen++;
            _colCount++;
        }
    }

    private void OnCompositionTick(object? sender, EventArgs e)
    {
        if (_desktop == null) return;
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastCompositionTime) return;
            _lastCompositionTime = rendering.RenderingTime;
        }
        OnUiTick(sender, e);
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _desktop == null)
        {
            _lastUiSeconds = 0; _frameTiming.Reset(); return;
        }
        long frameStart = Stopwatch.GetTimestamp();
        double arrivalSeconds = frameStart / (double)Stopwatch.Frequency;
        // One monotonic time domain: independent pump must not mix WPF presentation timestamps.
        double seconds = arrivalSeconds;
        if (_lastUiSeconds != 0 && seconds - _lastUiSeconds > .04) _slowFrames++;
        _lastUiSeconds = seconds;

        int cols = 0, rows = 0;
        long gen = 0;
        int colsDone = 0;
        int visibleCols, stride, padding;
        long inputCols, committedCols, epoch;
        lock (_frameGate)
        {
            visibleCols = _history.VisibleColumns;
            stride = _history.SamplesPerColumn;
            padding = _history.GuardColumns;
            inputCols = _history.InputColumns;
            committedCols = _history.CommittedColumns;
            epoch = _history.Epoch;
            gen = _frameGen;
            colsDone = _colCount;
            if (gen != _seenGen && _histCols > 0 && _histRows > 0)
            {
                cols = _histCols;
                rows = _histRows;
                int need = cols * rows;
                if (_histRead.Length != need) _histRead = new float[need];
                if (_heightRead.Length != need) _heightRead = new float[need];
                _history.CopyNewestFirst(_histRead, _historyOffset);
                _seenGen = gen;
            }
        }
        if (cols > 0)
        {
            MapHeights(_histRead, _heightRead);
            View.Apply(_histRead, rows, cols, visibleCols, committedCols - _historyOffset, _heightRead);
            _desktop?.RenderView.Apply(_histRead, rows, cols, visibleCols, committedCols - _historyOffset, _heightRead);
        }
        double offset = _scrollClock.Advance(seconds, inputCols / (double)stride,
            committedCols, epoch, stride / (double)_targetRate, padding);
        View.SetScrollOffset(offset);
        if (WindowState != WindowState.Minimized && IsVisible) View.RenderFrame();
        if (_desktop != null)
        {
            _desktop.SetPreviewFraming(new Size(View.ActualWidth, View.ActualHeight), View.PreviewOffset);
            _desktop.RenderView.SetScrollOffset(offset);
            _desktop.Present();
        }
        _frameTiming.Record(arrivalSeconds, Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds);
        if (_captureError != null) return;

        long now = Environment.TickCount64;
        if (_statusTicks == 0)
        {
            _statusTicks = now;
            _colMark = colsDone;
            _drawMark = View.FrameCount;
            _desktopDrawMark = _desktop?.RenderView.FrameCount ?? 0;
        }
        else if (now - _statusTicks > 500)
        {
            double fps = (colsDone - _colMark) * 1000.0 / (now - _statusTicks);
            double desktopFps = _desktop != null
                ? Math.Max(0, _desktop.RenderView.FrameCount - _desktopDrawMark) * 1000.0 / (now - _statusTicks) : 0;
            double drawFps = (View.FrameCount - _drawMark) * 1000.0 / (now - _statusTicks);
            _statusTicks = now;
            _colMark = colsDone;
            _drawMark = View.FrameCount;
            _desktopDrawMark = _desktop?.RenderView.FrameCount ?? 0;
            int cbUs = _cbAvgUs;
            string pace = cbUs > 0 ? $"{cbUs / 1000.0:0.0}ms" : "--";
            int w = _cols;
            int h = rows > 0 ? rows : _histRows;
            var timing = _frameTiming.Read();
            Status.Text = $"捕获中 · 分析 {fps:0} 列/s · 预览 {drawFps:0} fps{(_desktop != null ? $" / 悬浮 {desktopFps:0} fps" : "")} · 目标 {_previewPump.TargetRate} fps · 长间隔 {_slowFrames} 次" +
                $" · 抗锯齿 {(View.AntialiasingSamples > 1 ? $"MSAA {View.AntialiasingSamples}×" : "未支持")} · 显示 {View.VisibleColumns}×{View.VisibleRows} · 历史约 {w / (double)_targetRate:0.00}s" +
                $"\n刷新间隔/处理 P95 {timing.IntervalP95:0.0}/{timing.CostP95:0.0} ms{(_desktop != null ? $" · 悬浮提交 {_desktop.RenderView.UploadMs:0.0} ms" : "")} · 构建/提交 {View.BuildMs:0.0}/{View.UploadMs:0.0} ms · 回调 {pace} · 音量 {_volShown * 100:0}% · {_deviceLabel}";
        }
    }

    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wlenRe = MathF.Cos((float)ang);
            float wlenIm = MathF.Sin((float)ang);
            for (int i = 0; i < n; i += len)
            {
                float wRe = 1, wIm = 0;
                int half = len >> 1;
                for (int k = 0; k < half; k++)
                {
                    int a = i + k;
                    int b = a + half;
                    float vRe = re[b] * wRe - im[b] * wIm;
                    float vIm = re[b] * wIm + im[b] * wRe;
                    re[b] = re[a] - vRe;
                    im[b] = im[a] - vIm;
                    re[a] += vRe;
                    im[a] += vIm;
                    float nwRe = wRe * wlenRe - wIm * wlenIm;
                    wIm = wRe * wlenIm + wIm * wlenRe;
                    wRe = nwRe;
                }
            }
        }
    }

    private void BeginTimerRes()
    {
        if (_timerRes) return;
        timeBeginPeriod(1);
        _timerRes = true;
    }

    private void EndTimerRes()
    {
        if (!_timerRes) return;
        timeEndPeriod(1);
        _timerRes = false;
    }

    private static int AllocatedBufferMs(WasapiCapture cap, int sampleRate)
    {
        try
        {
            var field = typeof(WasapiCapture).GetField("audioClient", BindingFlags.Instance | BindingFlags.NonPublic);
            var client = field?.GetValue(cap);
            if (client == null || sampleRate <= 0) return 0;
            int frames = (int)(client.GetType().GetProperty("BufferSize")?.GetValue(client) ?? 0);
            if (frames <= 0) return 0;
            return (int)Math.Round(frames * 1000.0 / sampleRate);
        }
        catch
        {
            return 0;
        }
    }

    private void WaitUntil(IntPtr timer, long deadline)
    {
        long now = Stopwatch.GetTimestamp();
        long remain = deadline - now;
        if (remain <= 0 || !_run) return;

        long remain100 = remain * 10_000_000L / Stopwatch.Frequency;
        if (timer != IntPtr.Zero && remain100 > 0)
        {
            long due = -remain100;
            if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(timer, 30);
                return;
            }
        }
        Thread.Sleep(Math.Clamp((int)Math.Ceiling(remain * 1000.0 / Stopwatch.Frequency), 1, 30));
    }

    private void StopAll()
    {
        _run = false;
        if (_worker != null && _worker != Thread.CurrentThread) _worker.Join(1000);
        try
        {
            if (_capture != null)
            {
                _capture.DataAvailable -= OnData;
                _capture.RecordingStopped -= OnStopped;
                _capture.StopRecording();
            }
            _capture?.Dispose();
        }
        catch { }
        _capture = null;
        _renderDevice?.Dispose();
        _renderDevice = null;
        EndTimerRes();
    }

    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerEx(IntPtr lpTimerAttributes, IntPtr lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr hTimer, ref long pDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uMilliseconds);

    private sealed class EventLoopback : WasapiCapture
    {
        public EventLoopback(MMDevice device, int bufferMs) : base(device, true, bufferMs)
        {
        }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags()
            => AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }
}
