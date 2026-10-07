using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SonicTrack.App.Shell;

namespace SonicTrack.WaveCore;

/// <summary>
/// Window lifecycle for the integrated app: the settings window and the desktop overlay are
/// independent. Closing the settings window only hides it while the overlay keeps capturing and
/// drawing (audio, render pump and overlay all live in this object); the window is really closed
/// once the overlay is gone, or when the application exits (<see cref="ForceClose"/>). The overlay
/// can also be started from the launcher without ever showing the settings window.
/// </summary>
public partial class MainWindow : IOverlayComponent
{
    private bool _forceClose;

    /// <summary>Raised when the desktop overlay is opened or closed.</summary>
    internal event Action? OverlayStateChanged;

    /// <summary>Raised when the settings window is hidden but the component keeps running for its overlay.</summary>
    internal event Action? SettingsHidden;

    internal bool HasOverlay => _overlay != null;

    /// <summary>True while the settings window is hidden and only the overlay is running.</summary>
    internal bool IsSettingsHidden { get; private set; }

    private bool KeepOverlayOnClose() => !_forceClose && !_isolated && !_closing && _overlay != null;

    private void HideKeepingOverlay()
    {
        if (_save.IsEnabled) SaveNow();
        IsSettingsHidden = true;
        Hide();
        SettingsHidden?.Invoke();
    }

    /// <summary>Same start-up as the first Loaded event: audio capture, saved overlay, render clock.</summary>
    private void StartRunning()
    {
        if (_loaded) return;
        _loaded = true;
        if (Source.SelectedIndex == 0) Connect();
        if (_settings.OverlayOpen) OpenOverlay();
        ConfigureRefresh(); Describe();
    }

    private void OnWindowLoaded()
    {
        if (_loaded) { ConfigureRefresh(); ResetMetrics(); Describe(); return; }
        StartRunning();
    }

    /// <summary>Starts the component for its desktop overlay only; the settings window stays hidden.</summary>
    internal void StartOverlayOnly()
    {
        IsSettingsHidden = true;
        // A real (hidden) window handle so the normal close path runs later; nothing is shown.
        new WindowInteropHelper(this).EnsureHandle();
        StartRunning();
        if (_overlay == null) { OpenOverlay(); Changed(); }
    }

    /// <summary>Shows the settings window again (it may have been hidden while the overlay runs).</summary>
    internal void ShowSettings()
    {
        IsSettingsHidden = false;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
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
        if (!_closing && IsSettingsHidden)
            Dispatcher.BeginInvoke(() => { if (!_closing && _overlay == null && IsSettingsHidden) Close(); }, DispatcherPriority.Background);
    }

    event Action? IOverlayComponent.OverlayStateChanged { add => OverlayStateChanged += value; remove => OverlayStateChanged -= value; }
    event Action? IOverlayComponent.SettingsHidden { add => SettingsHidden += value; remove => SettingsHidden -= value; }
    bool IOverlayComponent.HasOverlay => HasOverlay;
    bool IOverlayComponent.IsSettingsHidden => IsSettingsHidden;
    void IOverlayComponent.ShowSettings() => ShowSettings();
    void IOverlayComponent.StartOverlayOnly() => StartOverlayOnly();
    void IOverlayComponent.OpenOverlay() { if (_overlay == null && !_closing) { OpenOverlay(); Changed(); } }
    void IOverlayComponent.CloseOverlay() => CloseOverlay();
    void IOverlayComponent.ForceClose() => ForceClose();
}
