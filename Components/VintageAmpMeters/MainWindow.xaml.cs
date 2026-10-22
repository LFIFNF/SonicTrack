using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SonicTrack.App.Shell;

namespace SonicTrack.VintageAmpMeters;

public partial class MainWindow : Window, IOverlayComponent
{
    private readonly AmpMeterSettings _settings;
    private readonly AudioMeterAnalyzer _analyzer = new();
    private readonly float[] _levels = new float[3];
    private readonly MeterGauge[] _gauges;
    private readonly DispatcherTimer _saveTimer;
    private FloatingOverlayWindow? _overlay;
    private long _lastRenderTimestamp;
    private bool _settingsLoaded;
    private bool _running;
    private bool _forceClose;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AmpMeterSettings.Load(out string? warning);
        _gauges = [BassMeter, MidMeter, TrebleMeter];
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) => SaveNow();
        _analyzer.Stopped += OnAudioStopped;
        Loaded += (_, _) => StartRunning();
        Closing += OnWindowClosing;

        _settingsLoaded = true;
        ApplySettings();
        if (warning != null)
            SaveStatus.Text = warning;
    }

    internal event Action? OverlayStateChanged;
    internal event Action? SettingsHidden;
    internal bool HasOverlay => _overlay != null;
    internal bool IsSettingsHidden { get; private set; }

    private void ApplySettings()
    {
        GainSlider.Value = _settings.Gain;
        ShadowSlider.Value = _settings.ShadowStrength;
        SizeSlider.Value = _settings.OverlayMeterSize;
        SpacingSlider.Value = _settings.OverlaySpacing;
        ScaleNumbersToggle.IsChecked = _settings.ShowScaleNumbers;
        ApplyScaleNumbers();
        UpdateLabels();
    }

    private void ApplyScaleNumbers()
    {
        foreach (MeterGauge gauge in _gauges)
        {
            gauge.ShowScaleNumbers = _settings.ShowScaleNumbers;
            gauge.InvalidateVisual();
        }
        _overlay?.SetScaleNumbers(_settings.ShowScaleNumbers);
    }

    private void StartRunning()
    {
        if (_running || _closing)
            return;
        _running = true;
        CompositionTarget.Rendering += OnRendering;
        ConnectAudio();
    }

    private void ConnectAudio()
    {
        try
        {
            _analyzer.Start();
            AudioStatus.Text = $"正在捕获默认播放设备 · {_analyzer.Description}";
        }
        catch (Exception ex)
        {
            global::SonicTrack.App.App.TryLog(ex);
            AudioStatus.Text = $"无法连接默认播放设备：{ex.Message}";
        }
    }

    private void OnAudioStopped(string message)
    {
        if (_closing || Dispatcher.HasShutdownStarted)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing)
                AudioStatus.Text = message;
        }, DispatcherPriority.Background);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_running || _closing)
            return;

        long timestamp = Stopwatch.GetTimestamp();
        double elapsed = _lastRenderTimestamp == 0
            ? 1.0 / 60
            : Stopwatch.GetElapsedTime(_lastRenderTimestamp, timestamp).TotalSeconds;
        _lastRenderTimestamp = timestamp;
        double nowSeconds = (double)timestamp / Stopwatch.Frequency;
        bool hasInput = _analyzer.ReadLevels(_levels);
        double gainDb = 20 * Math.Log10(_settings.Gain);

        for (int i = 0; i < _gauges.Length; i++)
        {
            double db = _levels[i] + gainDb;
            _gauges[i].UpdateLevel(db, elapsed, nowSeconds, hasInput);
            if (_overlay is { } overlay)
                overlay.Gauges[i].UpdateLevel(db, elapsed, nowSeconds, hasInput);
        }
    }

    private void GainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsLoaded)
            return;
        _settings.Gain = Math.Clamp(GainSlider.Value, 0.1, 10);
        UpdateLabels();
        ScheduleSave();
    }

    private void ShadowChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsLoaded)
            return;
        _settings.ShadowStrength = Math.Clamp(ShadowSlider.Value, 0, 100);
        _overlay?.SetShadowStrength(_settings.ShadowStrength);
        UpdateLabels();
        ScheduleSave();
    }

    private void OverlayLayoutChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsLoaded)
            return;
        _settings.OverlayMeterSize = Math.Clamp(SizeSlider.Value, 120, 400);
        _settings.OverlaySpacing = Math.Clamp(SpacingSlider.Value, 0, 80);
        _overlay?.SetLayout(_settings.OverlayMeterSize, _settings.OverlaySpacing);
        UpdateLabels();
        ScheduleSave();
    }

    private void ScaleNumbersChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded)
            return;
        _settings.ShowScaleNumbers = ScaleNumbersToggle.IsChecked == true;
        ApplyScaleNumbers();
        ScheduleSave();
    }

    private void UpdateLabels()
    {
        if (!_settingsLoaded)
            return;
        GainLabel.Text = $"输入增益 · {_settings.Gain:0.00}×";
        ShadowLabel.Text = $"悬浮指针阴影 · {_settings.ShadowStrength:0}%";
        SizeLabel.Text = $"悬浮仪表大小 · {_settings.OverlayMeterSize:0} px";
        SpacingLabel.Text = $"组间距 · {_settings.OverlaySpacing:0} px";
        OverlayButton.Content = HasOverlay ? "关闭桌面悬浮窗" : "打开桌面悬浮窗";
    }

    private void ScheduleSave()
    {
        SaveStatus.Text = "参数有更改，正在保存…";
        SaveStatus.ToolTip = null;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            _settings.Save();
            SaveStatus.Text = "参数自动保存";
            SaveStatus.ToolTip = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            global::SonicTrack.App.App.TryLog(ex);
            SaveStatus.Text = "参数保存失败";
            SaveStatus.ToolTip = ex.Message;
        }
    }

    private void ToggleOverlay(object sender, RoutedEventArgs e)
    {
        try
        {
            if (HasOverlay)
                CloseOverlay();
            else
                OpenOverlay();
        }
        catch (Exception ex)
        {
            global::SonicTrack.App.App.TryLog(ex);
            MessageBox.Show(this, $"无法打开桌面悬浮窗：{ex.Message}", "SonicTrack",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenOverlay()
    {
        if (_closing || HasOverlay)
            return;
        StartRunning();
        var overlay = new FloatingOverlayWindow(
            _settings.ShadowStrength, _settings.OverlayMeterSize, _settings.OverlaySpacing,
            _settings.ShowScaleNumbers);
        _overlay = overlay;
        overlay.Closed += OnOverlayClosed;
        overlay.LocationChanged += OnOverlayLocationChanged;
        if (_settings.OverlayLeft is double left && _settings.OverlayTop is double top)
        {
            overlay.WindowStartupLocation = WindowStartupLocation.Manual;
            overlay.Left = left;
            overlay.Top = top;
        }
        try
        {
            overlay.Show();
        }
        catch
        {
            overlay.Closed -= OnOverlayClosed;
            overlay.LocationChanged -= OnOverlayLocationChanged;
            _overlay = null;
            overlay.Close();
            throw;
        }
        UpdateLabels();
        OverlayStateChanged?.Invoke();
    }

    private void OnOverlayLocationChanged(object? sender, EventArgs e)
    {
        if (sender is not FloatingOverlayWindow overlay || !ReferenceEquals(overlay, _overlay))
            return;
        _settings.OverlayLeft = overlay.Left;
        _settings.OverlayTop = overlay.Top;
        ScheduleSave();
    }

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        if (sender is FloatingOverlayWindow overlay)
        {
            overlay.Closed -= OnOverlayClosed;
            overlay.LocationChanged -= OnOverlayLocationChanged;
        }
        _overlay = null;
        UpdateLabels();
        SaveNow();
        OverlayStateChanged?.Invoke();
        if (!_closing && IsSettingsHidden)
            Dispatcher.BeginInvoke(() =>
            {
                if (!_closing && !HasOverlay && IsSettingsHidden)
                    Close();
            }, DispatcherPriority.Background);
    }

    private void ReconnectAudio(object sender, RoutedEventArgs e) => ConnectAudio();

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceClose && !_closing && HasOverlay)
        {
            SaveNow();
            IsSettingsHidden = true;
            Hide();
            SettingsHidden?.Invoke();
            e.Cancel = true;
            return;
        }

        _closing = true;
        _saveTimer.Stop();
        CompositionTarget.Rendering -= OnRendering;
        _analyzer.Stopped -= OnAudioStopped;
        SaveNow();
        _overlay?.Close();
        _overlay = null;
        _analyzer.Dispose();
    }

    internal void ShowSettings()
    {
        IsSettingsHidden = false;
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    internal void StartOverlayOnly()
    {
        if (_closing)
            return;
        IsSettingsHidden = true;
        new WindowInteropHelper(this).EnsureHandle();
        StartRunning();
        OpenOverlay();
    }

    internal void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    event Action? IOverlayComponent.OverlayStateChanged
    {
        add => OverlayStateChanged += value;
        remove => OverlayStateChanged -= value;
    }

    event Action? IOverlayComponent.SettingsHidden
    {
        add => SettingsHidden += value;
        remove => SettingsHidden -= value;
    }

    bool IOverlayComponent.HasOverlay => HasOverlay;
    bool IOverlayComponent.IsSettingsHidden => IsSettingsHidden;
    void IOverlayComponent.ShowSettings() => ShowSettings();
    void IOverlayComponent.StartOverlayOnly() => StartOverlayOnly();
    void IOverlayComponent.OpenOverlay() => OpenOverlay();
    void IOverlayComponent.CloseOverlay() => CloseOverlay();
    void IOverlayComponent.ForceClose() => ForceClose();

    private void CloseOverlay() => _overlay?.Close();
}
