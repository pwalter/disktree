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

        foreach (TabItem t in _tabs.Items) _chips.Items.Add(new ListBoxItem { Content = t.Header });
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

    /// <summary>Brings a tab to the front (not a tab that is switched off for the current source).</summary>
    public void Select(TabItem tab)
    {
        if (tab.IsEnabled) _tabs.SelectedItem = tab;
    }

    /// <summary>
    /// Switches a tab off for the scan of the active tab (null = on): its chip stays visible, greyed out, with the reason
    /// as tooltip. If it was the open tab, the first tab opens instead.
    /// </summary>
    public void SetAvailable(TabItem tab, string? whyNot)
    {
        int i = _tabs.Items.IndexOf(tab);
        if (i < 0 || _chips.Items[i] is not ListBoxItem chip) return;
        bool on = whyNot == null;
        tab.IsEnabled = on;
        chip.IsEnabled = on;
        chip.Opacity = on ? 1 : 0.45;
        chip.ToolTip = whyNot;
        ToolTipService.SetShowOnDisabled(chip, true);
        if (!on && _tabs.SelectedIndex == i) _tabs.SelectedIndex = 0;
    }
}
