using System.Windows.Controls;
using DiskTree.Core;
using DiskTree.Features.Age;
using DiskTree.Features.Owner;
using DiskTree.Features.TopFiles;
using DiskTree.Features.Treemap;
using DiskTree.Features.TypeChart;
using DiskTree.Infrastructure;

namespace DiskTree.Ui.Controllers;

/// <summary>
/// The views of the side panel that describe the selected folder: file types, age, treemap, the largest files and the
/// owner. Brings them up to date after the selection, a scan or a change; knows the "Älter" column header and the
/// notices of the age tab (the timestamp the numbers are built from is a choice of the active tab).
/// </summary>
public sealed class ChartsController : IScanViews
{
    private readonly AppSettings _settings;
    private readonly ScanTabsController _tabs;
    private readonly TreeController _tree;
    private readonly SnapshotController _snapshots;
    private readonly TypeChartPanel _types;
    private readonly AgeChartPanel _age;
    private readonly TabItem _ageTab;
    private readonly TextBlock _oldHeader;
    private readonly TreemapPanel _treemap;
    private readonly TopFilesPanel _top;
    private readonly OwnerPanel _owner;

    // Order matters: the threshold has to be set (AgeThreshold.Months) before anything is drawn with it,
    // and the tab's timestamp choice comes from the settings before the notices are worked out.
    public ChartsController(AppSettings settings, ScanTabsController tabs, TreeController tree, SnapshotController snapshots,
        TypeChartPanel types, AgeChartPanel age, TabItem ageTab, TabControl sideTabs, TextBlock oldHeader,
        TreemapPanel treemap, TopFilesPanel top, OwnerPanel owner)
    {
        _settings = settings;
        _tabs = tabs;
        _tree = tree;
        _snapshots = snapshots;
        _types = types;
        _age = age;
        _ageTab = ageTab;
        _oldHeader = oldHeader;
        _treemap = treemap;
        _top = top;
        _owner = owner;

        // The age chart is only worth the walk while its tab is visible: draw it when it is selected.
        sideTabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, sideTabs) || !_ageTab.IsSelected) return;
            _ = UpdateAgeAsync(_tree.CurrentChartTarget());
        };

        _age.InitThreshold(_settings.AgeThresholdMonths);
        ShowAgeHeader();
        _age.ThresholdChanged += () =>
        {
            _settings.AgeThresholdMonths = AgeThreshold.Months;
            ShowAgeHeader();
            _tree.RefreshRows();
            _ = UpdateAgeAsync(_tree.CurrentChartTarget());
        };

        _tabs.Current.AgePreferred = _settings.AgeStamp;
        _age.InitStamp(_tabs.Current.AgePreferred);
        // The choice belongs to the tab; a new scan of that tab adopts it (Scanner.AgeStamp, read when the scan starts).
        _age.StampChosen += stamp =>
        {
            _tabs.Current.AgePreferred = stamp;
            _settings.AgeStamp = stamp;
            UpdateAgeNotices();
        };
        UpdateAgeNotices();
    }

    // ---------------------------------------------------------------- bringing the views up to date

    /// <summary>Brings treemap, age chart and file type chart up to date for the selected folder.</summary>
    public async Task RefreshAsync()
    {
        _ = _treemap.RefreshAsync();
        var target = _tree.CurrentChartTarget();
        _ = UpdateAgeAsync(target);
        await _types.UpdateAsync(target);
    }

    /// <summary>The selection in the tree changed: charts and owner view describe the new folder.</summary>
    public void OnSelectionChanged()
    {
        _ = RefreshAsync();
        ShowOwnerFolder();
    }

    /// <summary>The owner view for the folder the charts describe.</summary>
    public void ShowOwnerFolder() => _owner.SetFolder(_tree.CurrentChartTarget().Node?.FullPath);

    // ---- IScanViews: what a running scan (or a change of the tree) asks of the views

    public void ClearTopFiles() => _top.Clear();

    public void AgeNoticesChanged() => UpdateAgeNotices();

    /// <summary>Charts and the largest files for the scan of the active tab.</summary>
    public void RefreshViews()
    {
        _ = RefreshAsync();
        _top.Refresh(_tabs.Current.Scanner);
    }

    public void RefreshLive()
    {
        long now = Environment.TickCount64;
        if (!_types.IsBusy && now - _types.LastUpdateTick > 1000)
            _ = RefreshAsync();
        if (now - _top.LastRefreshTick > 1000)
            _top.Refresh(_tabs.Current.Scanner);
    }

    // ---------------------------------------------------------------- age

    public void ShowAgeHeader() =>
        _oldHeader.Text = "Älter " + AgeBuckets.ThresholdText(AgeThreshold.Months);

    /// <summary>The age tab shows the active tab's choice of timestamp.</summary>
    public void ShowAgeStamp() => _age.ShowStamp(_tabs.Current.AgePreferred);

    private async Task UpdateAgeAsync(ChartTarget target)
    {
        if (!_ageTab.IsSelected) return; // only worth the walk while it is visible
        UpdateAgeNotices();
        await _age.UpdateAsync(target, _tabs.Current.AgeStamp);
    }

    /// <summary>A loaded snapshot knows which timestamp it holds; the choice follows it (the saved preference stays).</summary>
    public void OnSnapshotAgeStamp(AgeStamp stamp)
    {
        _tabs.Current.AgePreferred = stamp;
        _age.ShowStamp(stamp);
        UpdateAgeNotices();
    }

    private void UpdateAgeNotices()
    {
        var cur = _tabs.Current;
        var shown = cur.AgeStamp;
        _oldHeader.ToolTip = $"Älter als der Schwellwert, nach dem Datum „{AgeStamps.Names[(int)shown]}“";
        _age.UpdateNotices(shown, cur.AgePreferred, hasTree: cur.Root != null, snapshot: _snapshots.IsSnapshotMode);
    }
}
