using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SonicTrack.App.Shell;

/// <summary>
/// Light title bar used by the component windows (WindowChrome caption with self-drawn buttons).
/// Only window chrome lives here; the component code itself is untouched.
/// </summary>
public partial class ComponentTitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ComponentTitleBar),
        new PropertyMetadata("", (d, e) => ((ComponentTitleBar)d).TitleText.Text = (string)e.NewValue));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(ComponentTitleBar),
        new PropertyMetadata("", (d, e) => ((ComponentTitleBar)d).UpdateCaption()));

    private Window? _window;

    public ComponentTitleBar()
    {
        InitializeComponent();
        UpdateCaption();
        Loaded += (_, _) => Attach();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    private void UpdateCaption()
    {
        CaptionText.Text = Caption;
        CaptionChip.Visibility = string.IsNullOrEmpty(Caption) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Attach()
    {
        var window = Window.GetWindow(this);
        if (window == null || ReferenceEquals(window, _window)) return;
        _window = window;
        window.StateChanged += (_, _) => Sync();
        Sync();
    }

    private void Sync()
    {
        if (_window == null) return;
        bool max = _window.WindowState == WindowState.Maximized;
        MaxButton.Content = (Geometry)FindResource(max ? "GlyphRestore" : "GlyphMaximize");
        MaxButton.ToolTip = max ? "还原" : "最大化";
        // A maximized WindowChrome window overhangs the screen by the resize frame; pad it back in.
        var frame = SystemParameters.WindowResizeBorderThickness;
        _window.BorderThickness = max ? new Thickness(frame.Left + 1, frame.Top + 1, frame.Right + 1, frame.Bottom + 1) : new Thickness(0);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) { if (_window != null) SystemCommands.MinimizeWindow(_window); }
    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (_window == null) return;
        if (_window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(_window);
        else SystemCommands.MaximizeWindow(_window);
    }
    private void OnClose(object sender, RoutedEventArgs e) => _window?.Close();
}
