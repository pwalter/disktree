using System.Windows.Controls;

namespace DiskTree.Ui.Controllers;

/// <summary>
/// The tabs of the side panel. Their headers are shown as wrapping chips (eight tabs do not fit in one row, and a
/// TabControl would reshuffle its rows); the chips and the TabControl stay in step.
/// </summary>
public sealed class SideTabsController
{
    private readonly TabControl _tabs;
    private readonly ListBox _chips;
    private bool _chipSync;

    public SideTabsController(TabControl tabs, ListBox chips)
    {
        _tabs = tabs;
        _chips = chips;

        _chips.SelectionChanged += (_, _) =>
        {
            if (_chipSync || _chips.SelectedIndex < 0) return;
            _chipSync = true;
            _tabs.SelectedIndex = _chips.SelectedIndex;
            _chipSync = false;
        };

        foreach (TabItem t in _tabs.Items) _chips.Items.Add(t.Header);
        _tabs.SelectedIndex = 0;
        _chips.SelectedIndex = 0;
        // Programmatic selection (Ctrl+F, treemap ...) and clicks on the chips stay in step.
        _tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, _tabs) || _chipSync) return;
            _chipSync = true;
            _chips.SelectedIndex = _tabs.SelectedIndex;
            _chipSync = false;
        };
    }

    /// <summary>Brings a tab to the front.</summary>
    public void Select(TabItem tab) => _tabs.SelectedItem = tab;
}
