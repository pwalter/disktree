using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace DiskTree.Ui;

/// <summary>
/// The TabControl of the side panel. Its headers are drawn as chips elsewhere (<see cref="Controllers.SideTabsController"/>),
/// so its template has no tab strip; the standard automation peer would then expose nothing at all. This one exposes the
/// content of the selected tab, so screen readers and UI Automation reach the panels (buttons, lists, texts).
/// </summary>
public sealed class SideTabControl : TabControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(SideTabControl owner) : TabControlAutomationPeer(owner)
    {
        protected override List<AutomationPeer>? GetChildrenCore() =>
            // The visual content (the ContentPresenter of the selected tab), as any other element would expose it.
            new FrameworkElementAutomationPeer(owner).GetChildren();
    }
}
