using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace SonicTrack.App.Shell;

/// <summary>
/// Notification-area (system tray) icon built on Shell_NotifyIcon, with a WPF context menu in the
/// launcher's light theme. Left click or double click raises <see cref="Activated"/>.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 0x5354; // WM_APP + n
    private const int NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const int NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    private const int WmLButtonUp = 0x202, WmLButtonDblClk = 0x203, WmRButtonUp = 0x205, WmContextMenu = 0x7B;

    private readonly HwndSource _window;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly string _tip;
    private IntPtr _icon;
    private bool _added, _disposed;

    public event Action? Activated;
    public ContextMenu Menu { get; }

    public TrayIcon(string tip, ContextMenu menu)
    {
        _tip = tip;
        Menu = menu;
        // Invisible tool window that receives the tray callbacks and owns the menu's foreground state.
        _window = new HwndSource(new HwndSourceParameters("SonicTrack tray")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80, Width = 0, Height = 0,
        });
        _window.AddHook(WndProc);
        _icon = AppIcon.CreateIconHandle(Math.Max(16, GetSystemMetrics(49)));
    }

    public bool IsVisible => _added;

    public void Show()
    {
        if (_disposed || _added) return;
        var data = Data(NifMessage | NifIcon | NifTip);
        _added = ShellNotifyIcon(NimAdd, ref data);
    }

    public void Hide()
    {
        if (!_added) return;
        var data = Data(0);
        ShellNotifyIcon(NimDelete, ref data);
        _added = false;
    }

    /// <summary>Shows a one-line notification next to the icon (Windows turns it into a toast).</summary>
    public void Notify(string title, string text)
    {
        if (!_added) return;
        var data = Data(NifInfo);
        data.InfoTitle = title;
        data.Info = text;
        data.InfoFlags = 0x4 | 0x20; // NIIF_USER (our icon) | NIIF_LARGE_ICON
        data.BalloonIcon = _icon;
        ShellNotifyIcon(NimModify, ref data);
    }

    private NotifyIconData Data(int flags) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(), Window = _window.Handle, Id = 1, Flags = flags,
        CallbackMessage = CallbackMessage, Icon = _icon, Tip = _tip, Info = "", InfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WmLButtonUp:
                case WmLButtonDblClk:
                    Activated?.Invoke();
                    break;
                case WmRButtonUp:
                case WmContextMenu:
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated && _added)
        {
            // Explorer restarted: the icon has to be added again.
            _added = false;
            Show();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        GetCursorPos(out var point);
        var fromDevice = _window.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var position = fromDevice.Transform(new Point(point.X, point.Y));
        SetForegroundWindow(_window.Handle); // so the menu closes when clicking elsewhere
        Menu.Placement = PlacementMode.AbsolutePoint;
        Menu.HorizontalOffset = position.X;
        Menu.VerticalOffset = position.Y;
        Menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Hide();
        _disposed = true;
        Menu.IsOpen = false;
        _window.RemoveHook(WndProc);
        _window.Dispose();
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(int message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointI point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
