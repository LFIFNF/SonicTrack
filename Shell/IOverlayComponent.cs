namespace SonicTrack.App.Shell;

/// <summary>
/// A component whose settings window and desktop overlay have independent lifetimes. The launcher
/// uses this to open the overlay straight from a card, to keep the overlay running when the settings
/// window is closed, and to keep the app in the notification area while any overlay is on screen.
/// </summary>
internal interface IOverlayComponent
{
    /// <summary>Raised when the desktop overlay is opened or closed.</summary>
    event Action? OverlayStateChanged;

    /// <summary>Raised when the settings window is hidden but the component keeps running for its overlay.</summary>
    event Action? SettingsHidden;

    bool HasOverlay { get; }

    /// <summary>True while the settings window is hidden (or was never shown) and only the overlay runs.</summary>
    bool IsSettingsHidden { get; }

    /// <summary>Shows the settings window (it may be hidden, or never shown yet).</summary>
    void ShowSettings();

    /// <summary>Starts a component whose settings window has not been shown, with only its desktop overlay visible.</summary>
    void StartOverlayOnly();

    void OpenOverlay();
    void CloseOverlay();

    /// <summary>Closes the settings window and its overlay (application exit).</summary>
    void ForceClose();
}
