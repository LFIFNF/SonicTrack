using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace SonicTrack.App.Shell;

/// <summary>
/// ItemsControl whose automation tree follows the visual tree, so the card buttons stay reachable
/// by screen readers / UI Automation after the card filter is refreshed.
/// </summary>
public sealed class CardHost : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
