using System.Windows.Threading;

namespace SonicTrack.App.Shell;

/// <summary>
/// Manages overlays that the user has opened: silence hides them and resumed audio restores
/// overlays hidden by silence. Tray-hidden overlays wait for a silence-to-sound transition.
/// </summary>
internal sealed class OverlayAutoController : IDisposable
{
    private static readonly string[] OverlayIds = ["v61", "v10", "vu"];
    private readonly AudioSessionMonitor _monitor;
    private readonly Func<string, bool> _isOverlayOpen;
    private readonly Func<string, bool> _ensureOverlayOpen;
    private readonly Action<string> _ensureOverlayClosed;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, DateTime> _silentSinceUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _userOpenedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoHiddenIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _awaitingSilenceBeforeResume = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private static readonly TimeSpan SilenceDelay = TimeSpan.FromSeconds(2.5);

    public OverlayAutoController(
        AudioSessionMonitor monitor,
        Func<string, bool> isOverlayOpen,
        Func<string, bool> ensureOverlayOpen,
        Action<string> ensureOverlayClosed)
    {
        _monitor = monitor;
        _isOverlayOpen = isOverlayOpen;
        _ensureOverlayOpen = ensureOverlayOpen;
        _ensureOverlayClosed = ensureOverlayClosed;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        if (_disposed) return;
        _monitor.Start();
        foreach (string id in OverlayIds)
            if (_isOverlayOpen(id))
                UserOpened(id);
        if (!_timer.IsEnabled) _timer.Start();
        Tick();
    }

    public void Stop()
    {
        _timer.Stop();
        _monitor.Stop();
        _silentSinceUtc.Clear();
        _userOpenedIds.Clear();
        _autoHiddenIds.Clear();
        _awaitingSilenceBeforeResume.Clear();
    }

    public void UserOpened(string id)
    {
        _userOpenedIds.Add(id);
        _autoHiddenIds.Remove(id);
        _awaitingSilenceBeforeResume.Remove(id);
        _silentSinceUtc.Remove(id);
    }

    public void UserClosed(string id)
    {
        _userOpenedIds.Remove(id);
        _autoHiddenIds.Remove(id);
        _awaitingSilenceBeforeResume.Remove(id);
        _silentSinceUtc.Remove(id);
    }

    public void TemporarilyHiddenByTray(string id)
    {
        _userOpenedIds.Add(id);
        _autoHiddenIds.Add(id);
        _awaitingSilenceBeforeResume.Add(id);
        _silentSinceUtc.Remove(id);
    }

    public void Tick()
    {
        if (_disposed) return;
        var prefs = AppPreferences.Current;
        if (!prefs.OverlayAutoManage)
        {
            _silentSinceUtc.Clear();
            return;
        }

        _monitor.Refresh();
        bool sound = _monitor.HasRelevantSound();
        DateTime now = DateTime.UtcNow;
        foreach (string id in OverlayIds)
        {
            if (!_userOpenedIds.Contains(id))
                continue;

            bool isOpen = _isOverlayOpen(id);
            if (sound)
            {
                _silentSinceUtc.Remove(id);
                if (!isOpen && _autoHiddenIds.Contains(id) &&
                    !_awaitingSilenceBeforeResume.Contains(id))
                {
                    if (_ensureOverlayOpen(id))
                        _autoHiddenIds.Remove(id);
                    else
                        UserClosed(id);
                }
                continue;
            }

            if (_awaitingSilenceBeforeResume.Remove(id))
                _silentSinceUtc.Remove(id);

            if (!isOpen)
            {
                _silentSinceUtc.Remove(id);
                continue;
            }

            if (!_silentSinceUtc.TryGetValue(id, out DateTime silentSince))
            {
                _silentSinceUtc[id] = now;
                continue;
            }

            if (now - silentSince < SilenceDelay)
                continue;

            _silentSinceUtc.Remove(id);
            _autoHiddenIds.Add(id);
            _ensureOverlayClosed(id);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}
