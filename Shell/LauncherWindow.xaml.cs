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

        _view = CollectionViewSource.GetDefaultView(_entries);
        _view.Filter = item => item is ComponentEntry entry && Accept(entry);
        Cards.ItemsSource = _view;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersion.Text = $"整合版 {version?.ToString(3)} · 浅色主题";
        AboutBody.Text =
            "3D 频谱瀑布（V6.1）：实时频谱柱体，向左平移，高度和颜色保持记录值；支持视角调节和透明桌面悬浮窗。\n\n" +
            "桌面声波（V10）：GPU 绘制的实时声波，支持底层声波、保护底板和置顶 / 鼠标穿透的桌面悬浮窗。\n\n" +
            "声音来源：两个组件都捕获 Windows 当前默认播放设备的声音（WASAPI loopback）。\n\n" +
            "参数：分别保存在程序目录的 v61-parameters.json 和 v10-parameters.json；没有参数文件时使用默认值。";

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
        bool page = about || dev;
        bool empty = !page && _view.IsEmpty;
        AboutPanel.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
        DevPanel.Visibility = dev ? Visibility.Visible : Visibility.Collapsed;
        CardScroller.Visibility = page ? Visibility.Collapsed : Visibility.Visible;
        SearchArea.Visibility = page ? Visibility.Hidden : Visibility.Visible;
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
            : sender == NavOpen ? "open" : sender == NavAbout ? "about" : sender == NavDev ? "dev" : "all";
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
                entry.HasOverlay = host.HasOverlay;
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
            entry.HasOverlay = false;
            UpdateCardState();
            CheckTrayOnly();
        };
        entry.Window = window;
        return window;
    }

    // ---- Window lifecycle: desktop overlays outlive their settings window and the launcher ----

    private bool AnyOverlay => _entries.Any(e => e.HasOverlay);

    private static void CloseOverlay(ComponentEntry entry)
    {
        if (entry.Window is IOverlayComponent host) host.CloseOverlay();
    }

    private void OnClosingLauncher(object? sender, CancelEventArgs e)
    {
        if (!_exiting && AnyOverlay)
        {
            // A desktop overlay is still running: keep the process alive in the notification area.
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
        _tray?.Dispose();
        _tray = null;
    }

    private void HideToTray()
    {
        // Component windows close as before; a settings window only hides while its overlay runs.
        foreach (var entry in _entries.ToList())
            entry.Window?.Close();
        if (!AnyOverlay)
        {
            // The overlays went away while closing the component windows.
            ExitApplication();
            return;
        }
        EnsureTray().Show();
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.Notify("SonicTrack 仍在运行", "桌面悬浮窗继续显示。单击托盘图标可回到主界面。");
        }
    }

    private void RestoreFromTray()
    {
        if (_exiting) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _tray?.Hide();
    }

    /// <summary>Main window hidden and the last overlay closed: nothing is left on screen, so exit.</summary>
    private void CheckTrayOnly()
    {
        if (_exiting || IsVisible || AnyOverlay) return;
        Dispatcher.BeginInvoke(() => { if (!_exiting && !IsVisible && !AnyOverlay) ExitApplication(); });
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
        _tray.Activated += RestoreFromTray;
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
}
