using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SonicTrack.App.Shell;

public partial class LauncherWindow : Window
{
    private readonly ObservableCollection<ComponentEntry> _entries = new();
    private readonly ICollectionView? _view;
    private string _category = "all";
    private ImageBrush? _backdropBrush;
    private int _titleClicks;
    private DateTime _lastTitleClick;
    private const double TitleClickWindowMs = 1500;
    private const int TitleClicksToUnlock = 5;
    private TrayIcon? _tray;
    private bool _exiting, _trayHintShown;
    private bool _interactiveMove, _backdropAlignPending;
    private HwndSource? _source;
    private AudioSessionMonitor? _sessionMonitor;
    private OverlayAutoController? _overlayAuto;
    private bool _overlaySettingsLoading;
    private HashSet<string>? _trayOverlaySnapshot;
    private readonly HashSet<string> _autoOpeningOverlayIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoClosingOverlayIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _trayClosingOverlayIds = new(StringComparer.OrdinalIgnoreCase);

    public LauncherWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        AboutIcon.Source = AppIcon.Image;
        WindowPlacement.FitMaximizedToWorkArea(this);

        _entries.Add(new ComponentEntry(
            "v61", "3D 频谱瀑布",
            "实时频谱柱体瀑布，颜色和高度随响度变化",
            "spectrum", "V6.1", (Geometry)FindResource("IconWaterfall"), PreviewKind.Waterfall,
            "频谱 瀑布 柱 3D spectrum waterfall fft",
            () => new SonicTrack.V61.MainWindow()));
        _entries.Add(new ComponentEntry(
            "v10", "桌面声波",
            "GPU 实时声波，可悬浮在桌面上显示",
            "wave", "V10", (Geometry)FindResource("IconWave"), PreviewKind.Waveform,
            "声波 波形 桌面 悬浮 waveform gpu overlay",
            () => new SonicTrack.WaveCore.MainWindow()));
        _entries.Add(new ComponentEntry(
            "vu", "复古功放表",
            "低音、中音 / 人声与高音的复古双指针模拟仪表",
            "spectrum", "V0.0.1", (Geometry)FindResource("IconVintageMeters"), PreviewKind.VintageMeters,
            "功放 仪表 vu 表头 低音 中音 人声 高音 vintage amplifier meter",
            () => new SonicTrack.VintageAmpMeters.MainWindow()));

        _view = CollectionViewSource.GetDefaultView(_entries);
        _view.Filter = item => item is ComponentEntry entry && Accept(entry);
        Cards.ItemsSource = _view;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersion.Text = $"整合版 {version?.ToString(3)} · 浅色主题";
        AboutBody.Text =
            "3D 频谱瀑布（V6.1）：实时频谱柱体，向左平移，高度和颜色保持记录值；支持视角调节和透明桌面悬浮窗。代码来自 v6.1-waterfall-3d（提交 bec53a7），分析和渲染部分原样复用。\n\n" +
            "桌面声波（V10）：GPU 绘制的实时声波，支持底层声波、保护底板和置顶 / 鼠标穿透的桌面悬浮窗。代码来自 v10-desktop-waveform-gpu，绘制核心原样复用。\n\n" +
            "复古功放表（V0.0.1）：低音、中音 / 人声与高音三组圆形双指针表盘，包含增益、峰值回落、柔和阴影和悬浮布局设置。\n\n" +
            "声音来源：所有组件都捕获 Windows 当前默认播放设备的声音（WASAPI loopback）。\n\n" +
            "参数：分别保存在程序目录的 v61-parameters.json、v10-parameters.json 和 vu-meter-parameters.json；首次打开时读取已有组件参数，不会改动原组件文件。";

        var wallpaper = WallpaperBackdrop.Load();
        if (wallpaper != null)
        {
            _backdropBrush = new ImageBrush(wallpaper)
            {
                Stretch = Stretch.UniformToFill, ViewportUnits = BrushMappingMode.Absolute,
                AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center,
            };
            RenderOptions.SetBitmapScalingMode(_backdropBrush, BitmapScalingMode.LowQuality);
            Backdrop.Fill = _backdropBrush;
            BackdropHost.Visibility = Visibility.Visible;
            Loaded += (_, _) => AlignBackdrop();
            LocationChanged += (_, _) => { if (!_interactiveMove) QueueBackdropAlignment(); };
        }
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(OnMoveMessage);
        };
        Closed += (_, _) => _source?.RemoveHook(OnMoveMessage);
        BackdropHost.SizeChanged += (_, _) => ClipBackdrop();

        NavDev.Visibility = AppPreferences.Current.DeveloperOptions ? Visibility.Visible : Visibility.Collapsed;

        StateChanged += (_, _) => SyncWindowState();
        Closing += OnClosingLauncher;
        Closed += (_, _) => Application.Current.Shutdown();
        UpdateHeader();
        UpdateOpenCount();
        EnsureOverlayServices();
        // 1.0.3: tray icon is always present from startup; restoring the main window does not hide it.
        EnsureTray().Show();
    }

    private bool Accept(ComponentEntry entry)
    {
        bool category = _category switch
        {
            "spectrum" => entry.Category == "spectrum",
            "wave" => entry.Category == "wave",
            "open" => entry.IsRunning,
            _ => true,
        };
        return category && entry.Matches(SearchBox?.Text ?? "");
    }

    private void Refresh()
    {
        if (_view == null) return;
        _view.Refresh();
        bool about = _category == "about";
        bool dev = _category == "dev";
        bool settings = _category == "settings";
        bool page = about || dev || settings;
        bool empty = !page && _view.IsEmpty;
        AboutPanel.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
        DevPanel.Visibility = dev ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        CardScroller.Visibility = page ? Visibility.Collapsed : Visibility.Visible;
        SearchArea.Visibility = page ? Visibility.Hidden : Visibility.Visible;
        if (settings) LoadOverlaySettingsUi();
        if (dev)
            LoadDevPeakThresholdUi();
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _category == "open" && string.IsNullOrWhiteSpace(SearchBox.Text)
            ? "还没有打开的组件" : "没有匹配的组件";
    }

    private void UpdateHeader()
    {
        (PageTitle.Text, PageSubtitle.Text) = _category switch
        {
            "spectrum" => ("频谱组件", "按频率显示声音的强弱变化"),
            "wave" => ("声波组件", "按时间显示声音的波形"),
            "open" => ("已打开", "正在运行的组件窗口，点击可切换到该窗口"),
            "settings" => ("设置", "悬浮窗管理与声音来源"),
            "about" => ("关于", "版本与组件来源"),
            "dev" => ("开发者选项", "调整组件窗口里滑条的外观"),
            _ => ("可视化组件", "选择组件并在独立窗口中打开"),
        };
    }

    private void UpdateOpenCount()
    {
        int count = _entries.Count(e => e.IsRunning);
        OpenCount.Text = count.ToString();
        OpenBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        _category = sender == NavSpectrum ? "spectrum" : sender == NavWave ? "wave"
            : sender == NavOpen ? "open" : sender == NavSettings ? "settings"
            : sender == NavAbout ? "about" : sender == NavDev ? "dev" : "all";
        _titleClicks = 0;
        UpdateHeader();
        Refresh();
    }

    // Clicking the "关于" page title five times in a row reveals the developer options.
    private void OnPageTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_category != "about") return;
        var now = DateTime.UtcNow;
        _titleClicks = (now - _lastTitleClick).TotalMilliseconds <= TitleClickWindowMs ? _titleClicks + 1 : 1;
        _lastTitleClick = now;
        if (_titleClicks < TitleClicksToUnlock) return;
        _titleClicks = 0;
        if (AppPreferences.Current.DeveloperOptions)
        {
            ShowToast("开发者选项已开启");
            return;
        }
        AppPreferences.Current.DeveloperOptions = true;
        AppPreferences.Save();
        NavDev.Visibility = Visibility.Visible;
        ShowToast("已开启开发者选项");
    }

    private void OnResetSliderLook(object sender, RoutedEventArgs e)
    {
        SliderAppearance.Current.Reset();
        ShowToast("已恢复默认滑条外观");
    }

    // ---- Developer: effective sound peak threshold (1.0.3) ----

    private bool _devPeakLoading;

    private void LoadDevPeakThresholdUi()
    {
        if (DevPeakThresholdSlider == null) return;
        _devPeakLoading = true;
        try
        {
            double pct = AppPreferences.Current.EffectiveSoundPeakPercent;
            if (!double.IsFinite(pct)) pct = 10;
            DevPeakThresholdSlider.Value = Math.Clamp(pct, 0, 100);
            UpdateDevPeakThresholdLabel();
        }
        finally
        {
            _devPeakLoading = false;
        }
    }

    private void UpdateDevPeakThresholdLabel()
    {
        if (DevPeakThresholdValue == null || DevPeakThresholdSlider == null) return;
        DevPeakThresholdValue.Text = $"{DevPeakThresholdSlider.Value:0}%";
    }

    private void OnDevPeakThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_devPeakLoading) return;
        double pct = Math.Clamp(Math.Round(DevPeakThresholdSlider.Value), 0, 100);
        AppPreferences.Current.EffectiveSoundPeakPercent = pct;
        AppPreferences.Save();
        UpdateDevPeakThresholdLabel();
        _sessionMonitor?.Refresh();
        _overlayAuto?.Tick();
    }


    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1800))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2150))));
        fade.Completed += (_, _) =>
        {
            if (ToastText.Text == message) Toast.Visibility = Visibility.Collapsed;
        };
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_view != null) Refresh();
    }

    private void OnSearchFocus(object sender, KeyboardFocusChangedEventArgs e)
        => SearchFrame.BorderBrush = SearchBox.IsKeyboardFocused
            ? (Brush)FindResource("CoralBrush") : (Brush)FindResource("DividerBrush");

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in FilterMenu.Items.OfType<MenuItem>())
            item.IsChecked = (string)item.Tag == _category;
        FilterMenu.PlacementTarget = FilterButton;
        FilterMenu.IsOpen = true;
    }

    private void OnFilterMenu(object sender, RoutedEventArgs e)
    {
        var target = (string)((MenuItem)sender).Tag switch
        {
            "spectrum" => NavSpectrum, "wave" => NavWave, "open" => NavOpen, _ => NavAll,
        };
        target.IsChecked = true;
    }

    private void UpdateCardState()
    {
        UpdateOpenCount();
        Refresh();
    }

    /// <summary>"打开设置": opens the component's settings window, or brings it back.</summary>
    private void OnOpenComponent(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ComponentEntry entry) return;
        if (entry.Window is IOverlayComponent { IsSettingsHidden: true } hidden)
        {
            // Only the desktop overlay was running (or it was started from the card): show the settings window.
            hidden.ShowSettings();
            entry.IsOpen = true;
            UpdateCardState();
            return;
        }
        if (entry.Window is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = CreateComponentWindow(entry);
        if (window == null) return;
        entry.IsOpen = true;
        UpdateCardState();
        window.Show();
        window.Activate();
    }

    /// <summary>"打开悬浮窗" / "关闭悬浮窗": toggles the desktop overlay without needing the settings window.</summary>
    private void OnToggleOverlay(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ComponentEntry entry) return;
        if (entry.HasOverlay) { CloseOverlay(entry); return; }
        if (entry.Window is IOverlayComponent running)
        {
            TryOpenOverlay(entry, () => running.OpenOverlay());
            return;
        }
        // Not running yet: create the component with its settings window hidden and start only the overlay.
        if (CreateComponentWindow(entry) is not IOverlayComponent created) return;
        if (!TryOpenOverlay(entry, created.StartOverlayOnly) || !created.HasOverlay)
            created.ForceClose();
    }

    private bool TryOpenOverlay(ComponentEntry entry, Action open)
    {
        string? error = null;
        try { open(); }
        catch (Exception ex) { App.TryLog(ex); error = ex.Message; }
        if (error == null && entry.Window is IOverlayComponent { HasOverlay: true }) return true;
        MessageBox.Show(this, $"无法打开「{entry.Name}」的桌面悬浮窗" + (error != null ? $"：{error}" : "。可以打开设置窗口查看原因。"),
            "SonicTrack", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>Creates the component window (not shown) and wires its lifecycle to the card.</summary>
    private Window? CreateComponentWindow(ComponentEntry entry)
    {
        Window window;
        try
        {
            window = entry.Factory();
        }
        catch (Exception ex)
        {
            App.TryLog(ex);
            MessageBox.Show(this, $"无法打开「{entry.Name}」：{ex.Message}", "SonicTrack", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        window.Icon = AppIcon.Image;
        if (window is IOverlayComponent host)
        {
            host.OverlayStateChanged += () =>
            {
                if (entry.Window != window) return;
                bool wasOpen = entry.HasOverlay;
                entry.HasOverlay = host.HasOverlay;
                if (!_exiting && wasOpen != entry.HasOverlay)
                {
                    if (entry.HasOverlay)
                    {
                        if (!_autoOpeningOverlayIds.Contains(entry.Id))
                            _overlayAuto?.UserOpened(entry.Id);
                    }
                    else if (!_autoClosingOverlayIds.Contains(entry.Id) &&
                             !_trayClosingOverlayIds.Contains(entry.Id))
                    {
                        _overlayAuto?.UserClosed(entry.Id);
                    }
                }
                UpdateCardState();
                CheckTrayOnly();
            };
            host.SettingsHidden += () =>
            {
                if (entry.Window != window) return;
                entry.IsOpen = false;
                UpdateCardState();
            };
        }
        window.Closed += (_, _) =>
        {
            if (entry.Window != window) return;
            entry.Window = null;
            entry.IsOpen = false;
            if (entry.HasOverlay)
                _overlayAuto?.UserClosed(entry.Id);
            entry.HasOverlay = false;
            UpdateCardState();
            CheckTrayOnly();
        };
        entry.Window = window;
        return window;
    }

    // ---- Window lifecycle: desktop overlays outlive their settings window and the launcher ----

    private bool AnyOverlay => _entries.Any(e => e.HasOverlay);

    private void CloseOverlay(ComponentEntry entry)
    {
        if (entry.Window is IOverlayComponent host)
        {
            host.CloseOverlay();
            if (!host.HasOverlay)
                _overlayAuto?.UserClosed(entry.Id);
        }
    }

    private void TemporarilyHideOverlayForTray(ComponentEntry entry)
    {
        if (entry.Window is not IOverlayComponent host || !host.HasOverlay)
            return;

        _trayClosingOverlayIds.Add(entry.Id);
        try
        {
            host.CloseOverlay();
        }
        finally
        {
            _trayClosingOverlayIds.Remove(entry.Id);
            if (!host.HasOverlay)
                _overlayAuto?.TemporarilyHiddenByTray(entry.Id);
        }
    }

    private void ToggleTrayOverlays()
    {
        if (_exiting) return;

        if (_trayOverlaySnapshot is not null)
        {
            HashSet<string> restore = _trayOverlaySnapshot;
            foreach (var entry in _entries.Where(e => e.HasOverlay && !restore.Contains(e.Id)).ToList())
                CloseOverlay(entry);
            var remaining = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in restore)
                if (!EnsureOverlayOpenById(id))
                    remaining.Add(id);

            _trayOverlaySnapshot = remaining.Count == 0 ? null : remaining;
            _tray?.Notify(remaining.Count == 0 ? "悬浮窗已恢复" : "部分悬浮窗未能恢复",
                restore.Count == 0
                    ? "关闭前没有正在运行的悬浮窗。"
                    : remaining.Count == 0
                        ? $"已恢复关闭前的 {restore.Count} 个悬浮窗。"
                        : $"仍有 {remaining.Count} 个悬浮窗未能恢复；再次单击可重试。");
            return;
        }

        HashSet<string> snapshot = _entries.Where(e => e.HasOverlay)
            .Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _trayOverlaySnapshot = snapshot;
        foreach (var entry in _entries.Where(e => e.HasOverlay).ToList())
            TemporarilyHideOverlayForTray(entry);

        string detail = snapshot.Count == 0
            ? "目前没有悬浮窗；自动隐藏只作用于之后手动打开的悬浮窗。"
            : $"已记住 {snapshot.Count} 个悬浮窗；再次单击可恢复。";
        _tray?.Notify("悬浮窗已关闭", detail);
    }

    private void OnClosingLauncher(object? sender, CancelEventArgs e)
    {
        if (!_exiting && AppPreferences.Current.CloseHidesToTray)
        {
            // Preference: close button hides the main UI into the tray (overlays optional).
            e.Cancel = true;
            HideToTray();
            return;
        }
        _exiting = true;
        // Close component windows first so each runs its own shutdown/save path (overlays included).
        foreach (var entry in _entries.ToList())
        {
            if (entry.Window is IOverlayComponent host) host.ForceClose();
            else entry.Window?.Close();
        }
        _overlayAuto?.Dispose();
        _overlayAuto = null;
        _sessionMonitor?.Dispose();
        _sessionMonitor = null;
        _tray?.Dispose();
        _tray = null;
    }

    private void HideToTray()
    {
        // Component windows close as before; a settings window only hides while its overlay runs.
        foreach (var entry in _entries.ToList())
            entry.Window?.Close();
        EnsureTray().Show();
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            string tip = AnyOverlay
                ? "桌面悬浮窗继续显示。单击托盘图标可关闭 / 恢复悬浮窗，双击可打开主界面。"
                : "程序已隐藏到托盘。单击托盘图标可关闭 / 恢复悬浮窗，双击可打开主界面。";
            _tray?.Notify("SonicTrack 仍在运行", tip);
        }
    }

    private void RestoreFromTray()
    {
        if (_exiting) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // 1.0.3: keep tray icon while the main window is visible.
        EnsureTray().Show();
    }

    /// <summary>1.0.2: last overlay closed while main is hidden no longer forces exit; tray keeps the process.</summary>
    private void CheckTrayOnly()
    {
        // Intentionally empty — kept so overlay/window lifecycle hooks stay stable.
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        Close();
    }

    private TrayIcon EnsureTray()
    {
        if (_tray != null) return _tray;
        var menu = new ContextMenu { Style = (Style)FindResource("LightContextMenu") };
        var itemStyle = (Style)FindResource("LightMenuItem");
        var show = new MenuItem { Header = "显示主界面", Style = itemStyle, Tag = FindResource("IconGrid"), FontWeight = FontWeights.SemiBold };
        show.Click += (_, _) => RestoreFromTray();
        var exit = new MenuItem { Header = "退出", Style = itemStyle, Tag = FindResource("GlyphClose") };
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(show);
        menu.Items.Add(new Separator { Style = (Style)FindResource("LightMenuSeparator") });
        menu.Items.Add(exit);
        // One "关闭…悬浮窗" item per running overlay, rebuilt each time the menu opens.
        var overlayItems = new List<MenuItem>();
        menu.Opened += (_, _) =>
        {
            foreach (var item in overlayItems) menu.Items.Remove(item);
            overlayItems.Clear();
            var running = _entries.Where(x => x.HasOverlay).ToList();
            foreach (var entry in running)
            {
                var item = new MenuItem { Header = $"关闭「{entry.Name}」悬浮窗", Style = itemStyle, Tag = entry.Icon };
                item.Click += (_, _) => CloseOverlay(entry);
                overlayItems.Add(item);
            }
            if (running.Count > 1)
            {
                var all = new MenuItem { Header = "关闭全部悬浮窗", Style = itemStyle, Tag = FindResource("GlyphClose") };
                all.Click += (_, _) => { foreach (var entry in running) CloseOverlay(entry); };
                overlayItems.Add(all);
            }
            if (running.Count == 0)
                overlayItems.Add(new MenuItem { Header = "关闭悬浮窗", Style = itemStyle, Tag = FindResource("IconWave"), IsEnabled = false });
            for (int i = 0; i < overlayItems.Count; i++) menu.Items.Insert(1 + i, overlayItems[i]);
        };
        _tray = new TrayIcon("SonicTrack 音乐可视化 · 桌面悬浮窗运行中", menu);
        _tray.SingleClicked += ToggleTrayOverlays;
        _tray.DoubleClicked += RestoreFromTray;
        return _tray;
    }

    /// <summary>
    /// Card footer: the buttons move under the version tag when the card is too narrow for one row,
    /// and drop their icons when even a full row is too narrow (minimum window width).
    /// </summary>
    private void OnCardFooterSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid footer || footer.Children.Count < 2 || footer.Children[1] is not Panel buttons) return;
        var version = footer.Children[0];
        SetFooterIcons(buttons, true);
        buttons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double full = buttons.DesiredSize.Width;
        bool wrap = footer.ActualWidth < version.DesiredSize.Width + 12 + full;
        SetFooterIcons(buttons, footer.ActualWidth >= full);
        Grid.SetRow(buttons, wrap ? 1 : 0);
        Grid.SetColumn(buttons, wrap ? 0 : 2);
        Grid.SetColumnSpan(buttons, wrap ? 3 : 1);
        ((FrameworkElement)buttons).Margin = new Thickness(0, wrap ? 12 : 0, 0, 0);
    }

    private static void SetFooterIcons(Panel buttons, bool show)
    {
        foreach (var button in buttons.Children.OfType<Button>())
        {
            if (button.Content is not StackPanel { Children.Count: 2 } content) continue;
            content.Children[0].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ((FrameworkElement)content.Children[1]).Margin = new Thickness(show ? 7 : 0, 0, 0, 0);
        }
    }

    private void AlignBackdrop()
    {
        if (_backdropBrush != null && Backdrop.IsLoaded) WallpaperBackdrop.Align(Backdrop, _backdropBrush);
    }

    private IntPtr OnMoveMessage(IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (message == 0x231) _interactiveMove = true; // WM_ENTERSIZEMOVE
        else if (message == 0x232) // WM_EXITSIZEMOVE
        {
            _interactiveMove = false;
            QueueBackdropAlignment();
        }
        return IntPtr.Zero;
    }
    private void QueueBackdropAlignment()
    {
        if (_backdropAlignPending || _backdropBrush == null || Dispatcher.HasShutdownStarted) return;
        _backdropAlignPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _backdropAlignPending = false;
            if (!_interactiveMove && IsVisible) AlignBackdrop();
        });
    }

    private void ClipBackdrop()
    {
        double radius = WindowState == WindowState.Maximized ? 0 : 16;
        BackdropHost.Clip = new RectangleGeometry(new Rect(BackdropHost.RenderSize), radius, radius);
    }

    private void SyncWindowState()
    {
        bool max = WindowState == WindowState.Maximized;
        ShadowHost.Margin = max ? new Thickness(0) : new Thickness(20);
        Frame.CornerRadius = new CornerRadius(max ? 0 : 16);
        ClipBackdrop();
        QueueBackdropAlignment();
        MaxButton.Content = FindResource(max ? "GlyphRestore" : "GlyphMaximize");
        MaxButton.ToolTip = max ? "还原" : "最大化";
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false, ResizeBorderThickness = max ? new Thickness(0) : new Thickness(24),
        });
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---- Settings / close behavior + overlay auto-manage (1.0.1+) ----

    private void EnsureOverlayServices()
    {
        if (_sessionMonitor != null) return;
        _sessionMonitor = new AudioSessionMonitor();
        AudioAppList.ItemsSource = _sessionMonitor.Apps;
        _overlayAuto = new OverlayAutoController(
            _sessionMonitor,
            id => _entries.FirstOrDefault(e => e.Id == id)?.HasOverlay == true,
            EnsureOverlayOpenAutomaticallyById,
            EnsureOverlayClosedAutomaticallyById);
        if (AppPreferences.Current.OverlayAutoManage)
            _overlayAuto.Start();
    }

    private void LoadOverlaySettingsUi()
    {
        EnsureOverlayServices();
        _overlaySettingsLoading = true;
        try
        {
            var p = AppPreferences.Current;
            if (p.CloseHidesToTray) RadioCloseToTray.IsChecked = true;
            else RadioCloseExit.IsChecked = true;
            ChkOverlayAuto.IsChecked = p.OverlayAutoManage;
            if (p.ListenAllAudioApps) RadioListenAll.IsChecked = true;
            else RadioListenSelected.IsChecked = true;
            _sessionMonitor!.Refresh();
            UpdateAudioAppEmptyHint();
        }
        finally
        {
            _overlaySettingsLoading = false;
        }
    }

    private void UpdateAudioAppEmptyHint()
    {
        bool empty = _sessionMonitor == null || _sessionMonitor.Apps.Count == 0;
        AudioAppEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PersistOverlaySettingsFromUi()
    {
        if (_overlaySettingsLoading || _sessionMonitor == null) return;
        var p = AppPreferences.Current;
        p.CloseHidesToTray = RadioCloseToTray.IsChecked == true;
        p.OverlayAutoManage = ChkOverlayAuto.IsChecked == true;
        p.ListenAllAudioApps = RadioListenAll.IsChecked == true;
        p.SelectedAudioApps = _sessionMonitor.Apps.Where(a => a.IsSelected).Select(a => a.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AppPreferences.Save();
        if (p.OverlayAutoManage) _overlayAuto?.Start();
        else _overlayAuto?.Stop();
    }

    private void OnOverlaySettingsChanged(object sender, RoutedEventArgs e) => PersistOverlaySettingsFromUi();

    private void OnCloseBehaviorChanged(object sender, RoutedEventArgs e) => PersistOverlaySettingsFromUi();

    private void OnListenModeChanged(object sender, RoutedEventArgs e) => PersistOverlaySettingsFromUi();

    private void OnAudioAppCheckChanged(object sender, RoutedEventArgs e) => PersistOverlaySettingsFromUi();

    private void OnRefreshAudioApps(object sender, RoutedEventArgs e)
    {
        EnsureOverlayServices();
        _sessionMonitor!.Refresh();
        UpdateAudioAppEmptyHint();
        ShowToast("已刷新播放应用列表");
    }

    private bool EnsureOverlayOpenById(string id)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        if (entry == null) return false;
        if (entry.HasOverlay) return true;
        if (entry.Window is IOverlayComponent running)
        {
            if (running.IsSettingsHidden && !running.HasOverlay)
            {
                running.ForceClose();
                if (entry.Window != null)
                    return false;
            }
            else
            {
                return TryOpenOverlay(entry, () => running.OpenOverlay());
            }
        }
        if (CreateComponentWindow(entry) is not IOverlayComponent created) return false;
        if (!TryOpenOverlay(entry, created.StartOverlayOnly) || !created.HasOverlay)
        {
            created.ForceClose();
            return false;
        }
        return true;
    }

    private bool EnsureOverlayOpenAutomaticallyById(string id)
    {
        _autoOpeningOverlayIds.Add(id);
        try
        {
            return EnsureOverlayOpenById(id);
        }
        finally
        {
            _autoOpeningOverlayIds.Remove(id);
        }
    }

    private void EnsureOverlayClosedAutomaticallyById(string id)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        if (entry == null || !entry.HasOverlay) return;
        if (entry.Window is not IOverlayComponent host) return;
        _autoClosingOverlayIds.Add(id);
        try
        {
            host.CloseOverlay();
        }
        finally
        {
            _autoClosingOverlayIds.Remove(id);
        }
    }
}
