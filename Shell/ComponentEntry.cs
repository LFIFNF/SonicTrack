using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace SonicTrack.App.Shell;

public enum PreviewKind { Waterfall, Waveform }

/// <summary>One visualization component shown as a card in the launcher.</summary>
public sealed class ComponentEntry : INotifyPropertyChanged
{
    private bool _isOpen;
    private bool _hasOverlay;

    public ComponentEntry(string id, string name, string description, string category, string version,
        Geometry icon, PreviewKind preview, string keywords, Func<Window> factory)
    {
        Id = id; Name = name; Description = description; Category = category; Version = version;
        Icon = icon; Preview = preview; Keywords = keywords; Factory = factory;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }
    public string Version { get; }
    public Geometry Icon { get; }
    public PreviewKind Preview { get; }
    public string Keywords { get; }
    internal Func<Window> Factory { get; }
    internal Window? Window { get; set; }

    /// <summary>The component's own window is shown.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set { if (_isOpen == value) return; _isOpen = value; OnPropertyChanged(); RaiseState(); }
    }

    /// <summary>The component's desktop overlay is running (it may outlive the component window).</summary>
    public bool HasOverlay
    {
        get => _hasOverlay;
        set { if (_hasOverlay == value) return; _hasOverlay = value; OnPropertyChanged(); RaiseState(); }
    }

    /// <summary>Window or overlay is running.</summary>
    public bool IsRunning => _isOpen || _hasOverlay;

    public string StatusText => (_isOpen, _hasOverlay) switch
    {
        (true, true) => "设置窗口和桌面悬浮窗运行中",
        (false, true) => "桌面悬浮窗运行中",
        (true, false) => "设置窗口已打开",
        _ => "",
    };

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StatusText));
    }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        query = query.Trim();
        return Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Version.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Keywords.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
