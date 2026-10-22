using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace SonicTrack.App.Shell;

/// <summary>
/// Lists Windows audio sessions on the default render device and reports whether selected
/// applications currently produce audible output (peak meter). Used by overlay auto open/close.
/// Visualization capture itself remains device loopback; this monitor does not isolate app audio.
/// </summary>
internal sealed class AudioSessionMonitor : IDisposable
{
    /// <summary>Absolute floor used when EffectiveSoundPeakPercent is 0.</summary>
    public const float PeakThreshold = 0.002f;

    /// <summary>Peak level (0-1) at/above which a session counts as having effective sound.</summary>
    public static float GetEffectivePeakThreshold()
    {
        double pct = AppPreferences.Current.EffectiveSoundPeakPercent;
        if (!double.IsFinite(pct)) pct = 10;
        pct = Math.Clamp(pct, 0, 100);
        if (pct <= 0) return PeakThreshold;
        return (float)(pct / 100.0);
    }

    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public ObservableCollection<AudioAppItem> Apps { get; } = new();

    public AudioSessionMonitor()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        if (_disposed) return;
        Refresh();
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void Refresh()
    {
        if (_disposed) return;
        var prefs = AppPreferences.Current;
        var selected = new HashSet<string>(prefs.SelectedAudioApps ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = new List<AudioAppItem>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioSessionManager.RefreshSessions();
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                try
                {
                    if (session.IsSystemSoundsSession) continue;
                    uint pid = session.GetProcessID;
                    if (pid == 0) continue;
                    string key;
                    string title;
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        key = (proc.ProcessName ?? "").Trim();
                        if (string.IsNullOrEmpty(key)) continue;
                        if (!key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            key += ".exe";
                        title = string.IsNullOrWhiteSpace(session.DisplayName)
                            ? (proc.ProcessName ?? key)
                            : session.DisplayName.Trim();
                        if (string.IsNullOrWhiteSpace(title)) title = key;
                    }
                    catch
                    {
                        continue;
                    }

                    float peak = 0;
                    try { peak = session.AudioMeterInformation.MasterPeakValue; } catch { }
                    // 1.0.3: effective sound = peak at/above configurable percent (not session State alone).
                    float threshold = GetEffectivePeakThreshold();
                    bool active = peak >= threshold;
                    var keyLower = key.ToLowerInvariant();
                    if (!seen.Add(keyLower))
                    {
                        var existing = snapshot.FirstOrDefault(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.Peak = Math.Max(existing.Peak, peak);
                            existing.IsActive = existing.IsActive || active;
                            existing.StatusText = existing.IsActive ? "有声音" : "静音/空闲";
                        }
                        continue;
                    }

                    snapshot.Add(new AudioAppItem
                    {
                        Key = key,
                        Title = title,
                        Peak = peak,
                        IsActive = active,
                        IsSelected = selected.Contains(key),
                        StatusText = active ? "有声音" : "静音/空闲",
                    });
                }
                catch (Exception ex) { App.TryLog(ex); }
            }
        }
        catch (Exception ex)
        {
            App.TryLog(ex);
        }

        foreach (var key in selected)
        {
            if (seen.Contains(key.ToLowerInvariant())) continue;
            snapshot.Add(new AudioAppItem
            {
                Key = key,
                Title = key,
                Peak = 0,
                IsActive = false,
                IsSelected = true,
                StatusText = "未在播放",
                IsStale = true,
            });
        }

        snapshot.Sort((a, b) =>
        {
            int c = b.IsActive.CompareTo(a.IsActive);
            if (c != 0) return c;
            return string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
        });

        SyncCollection(snapshot);
    }

    /// <summary>True when overlay auto-manage should treat the current moment as "有声音".</summary>
    public bool HasRelevantSound()
    {
        var prefs = AppPreferences.Current;
        if (prefs.ListenAllAudioApps) return Apps.Any(a => !a.IsStale && a.IsActive);
        var selected = new HashSet<string>(prefs.SelectedAudioApps ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) return false;
        return Apps.Any(a => a.IsSelected && a.IsActive && selected.Contains(a.Key));
    }

    private void SyncCollection(List<AudioAppItem> snapshot)
    {
        var byKey = Apps.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);
        for (int i = Apps.Count - 1; i >= 0; i--)
        {
            if (!snapshot.Any(s => s.Key.Equals(Apps[i].Key, StringComparison.OrdinalIgnoreCase)))
                Apps.RemoveAt(i);
        }
        foreach (var item in snapshot)
        {
            if (byKey.TryGetValue(item.Key, out var existing))
            {
                existing.Title = item.Title;
                existing.Peak = item.Peak;
                existing.IsActive = item.IsActive;
                existing.StatusText = item.StatusText;
                existing.IsStale = item.IsStale;
                if (!existing.IsSelected && item.IsSelected) existing.IsSelected = true;
            }
            else
            {
                Apps.Add(item);
            }
        }
        for (int i = 0; i < snapshot.Count; i++)
        {
            int j = -1;
            for (int k = 0; k < Apps.Count; k++)
            {
                if (Apps[k].Key.Equals(snapshot[i].Key, StringComparison.OrdinalIgnoreCase)) { j = k; break; }
            }
            if (j >= 0 && j != i && i < Apps.Count) Apps.Move(j, i);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}

internal sealed class AudioAppItem : INotifyPropertyChanged
{
    private string _title = "";
    private float _peak;
    private bool _isActive, _isSelected, _isStale;
    private string _statusText = "";

    public string Key { get; init; } = "";

    public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value; OnPropertyChanged(); }
    }

    public float Peak
    {
        get => _peak;
        set { if (Math.Abs(_peak - value) < 0.0001f) return; _peak = value; OnPropertyChanged(); }
    }

    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
    }

    public bool IsStale
    {
        get => _isStale;
        set { if (_isStale == value) return; _isStale = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText == value) return; _statusText = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
