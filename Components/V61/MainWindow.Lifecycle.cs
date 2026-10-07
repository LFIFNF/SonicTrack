using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SonicTrack.App.Shell;

namespace SonicTrack.V61;

/// <summary>
/// Window lifecycle for the integrated app, the same as the desktop waveform component: closing the
/// settings window only hides it while the transparent desktop overlay keeps rendering (capture,
/// analysis and the overlay all live in this object). The window is really closed once the overlay
/// is gone, or when the application exits (<see cref="ForceClose"/>). The overlay can also be started
/// from the launcher without ever showing the settings window.
/// </summary>
public partial class MainWindow : IOverlayComponent
{
    private bool _forceClose, _captureStarted;

    /// <summary>Raised when the desktop overlay is opened or closed.</summary>
    internal event Action? OverlayStateChanged;

    /// <summary>Raised when the settings window is hidden but the component keeps running for its overlay.</summary>
    internal event Action? SettingsHidden;

    internal bool HasOverlay => _desktop != null;

    /// <summary>True while the settings window is hidden (or was never shown) and only the overlay runs.</summary>
    internal bool IsSettingsHidden { get; private set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose && !_windowClosed && _desktop != null)
        {
            e.Cancel = true;
            HideKeepingOverlay();
            return;
        }
        base.OnClosing(e);
    }

    private void HideKeepingOverlay()
    {
        SaveWindowSize();
        IsSettingsHidden = true;
        Hide();
        SettingsHidden?.Invoke();
    }

    /// <summary>Starts capture and the overlay without showing the settings window.</summary>
    internal void StartOverlayOnly()
    {
        IsSettingsHidden = true;
        // A real (hidden) window handle: display-rate lookup and the normal close path need it.
        new WindowInteropHelper(this).EnsureHandle();
        UpdateLayout();
        if (View.ActualWidth <= 0 && Content is UIElement root)
        {
            // Lay the (hidden) content out at the saved window size so the overlay gets the same
            // framing as the preview would have.
            var size = new Size(double.IsFinite(Width) ? Width : 1100, double.IsFinite(Height) ? Height : 760);
            root.Measure(size);
            root.Arrange(new Rect(size));
        }
        OnLoaded(this, new RoutedEventArgs());
        UpdatePreviewPump();
        _previewPump.Start();
        OpenOverlay();
    }

    /// <summary>Shows the settings window (it may be hidden, or never shown yet).</summary>
    internal void ShowSettings()
    {
        IsSettingsHidden = false;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = _lastNonMinimizedState;
        Activate();
    }

    /// <summary>Closes the settings window and its overlay (application exit).</summary>
    internal void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void OnOverlayClosed()
    {
        OverlayStateChanged?.Invoke();
        // Settings window already closed by the user and the overlay is gone: finish closing now.
        if (!_windowClosed && IsSettingsHidden)
            Dispatcher.BeginInvoke(() => { if (!_windowClosed && _desktop == null && IsSettingsHidden) Close(); }, DispatcherPriority.Background);
    }

    event Action? IOverlayComponent.OverlayStateChanged { add => OverlayStateChanged += value; remove => OverlayStateChanged -= value; }
    event Action? IOverlayComponent.SettingsHidden { add => SettingsHidden += value; remove => SettingsHidden -= value; }
    bool IOverlayComponent.HasOverlay => HasOverlay;
    bool IOverlayComponent.IsSettingsHidden => IsSettingsHidden;
    void IOverlayComponent.ShowSettings() => ShowSettings();
    void IOverlayComponent.StartOverlayOnly() => StartOverlayOnly();
    void IOverlayComponent.OpenOverlay() => OpenOverlay();
    void IOverlayComponent.CloseOverlay() => CloseOverlay();
    void IOverlayComponent.ForceClose() => ForceClose();
}
