using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace SonicTrack.V61;

/// <summary>Per-pixel alpha window fed by a separate, invisible MSAA render host.</summary>
internal sealed class DesktopOverlayWindow : Window
{
    private readonly HwndSource _renderHost;
    internal Waterfall3DView RenderView { get; }
    private readonly Image _image;
    private bool _clickThrough;
    private bool _disposed;
    public bool ClickThrough
    {
        get => _clickThrough;
        set { _clickThrough = value; UpdateInputStyle(); }
    }

    public DesktopOverlayWindow(double width, double height)
    {
        Title = "SonicTrack 桌面透明频谱";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        width = double.IsFinite(width) && width > 0 ? width : 640;
        height = double.IsFinite(height) && height > 0 ? height : 400;
        double scale = Math.Min(1, Math.Min(1200 / width, 650 / height));
        Width = Math.Max(1, width * scale);
        Height = Math.Max(1, height * scale);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _image = new Image { Stretch = Stretch.Fill };
        Content = _image;
        // Never put HwndHost into the layered WPF window: render in our hidden popup.
        _renderHost = new HwndSource(new HwndSourceParameters("SonicTrack transparent render host")
        {
            WindowStyle = unchecked((int)0x80000000), Width = (int)Width, Height = (int)Height
        });
        RenderView = new Waterfall3DView { Width = Width, Height = Height, TransparentOutput = true };
        _renderHost.RootVisual = RenderView;
        RenderView.Measure(new Size(Width, Height));
        RenderView.Arrange(new Rect(0, 0, Width, Height));
        RenderView.UpdateLayout();
        SourceInitialized += (_, _) => UpdateInputStyle();
        MouseLeftButtonDown += (_, e) =>
        {
            if (!_clickThrough && e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        var menu = new ContextMenu();
        var close = new MenuItem { Header = "关闭桌面悬浮窗" };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);
        _image.ContextMenu = menu;
        Closed += (_, _) => DisposeRenderer();
    }

    public void Present()
    {
        if (_disposed) return;
        RenderView.RenderFrame();
        if (RenderView.TransparentFrame != null && _image.Source != RenderView.TransparentFrame)
            _image.Source = RenderView.TransparentFrame;
    }

    internal void SetPreviewFraming(Size size, Point offset) => RenderView.SetPreviewFraming(size, offset);

    private void UpdateInputStyle()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, -20);
        int next = _clickThrough ? style | 0x20 | 0x08000000 : style & ~(0x20 | 0x08000000);
        SetLastError(0);
        if (SetWindowLong(hwnd, -20, next) == 0 && Marshal.GetLastWin32Error() != 0)
            throw new Win32Exception();
    }

    private void DisposeRenderer()
    {
        if (_disposed) return;
        _disposed = true;
        _renderHost.RootVisual = null;
        foreach (var child in RenderView.Children)
            if (child is IDisposable disposable) disposable.Dispose();
        _renderHost.Dispose();
        _image.Source = null;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
}
