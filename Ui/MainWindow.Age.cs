using System.Windows.Controls;
using DiskTree.Core;

namespace DiskTree.Ui;

// Glue between the "Alter" tab (AgeChartPanel), the "Älter" column of the tree and the scan tabs.
public partial class MainWindow
{
    // Order matters: the threshold has to be set (AgeThreshold.Months) before anything is drawn with it,
    // and the tab's timestamp choice comes from the settings before the notices are worked out.
    private void InitAge()
    {
        AgePane.InitThreshold(_settings.AgeThresholdMonths);
        ShowAgeHeader();
        AgePane.ThresholdChanged += () =>
        {
            _settings.AgeThresholdMonths = AgeThreshold.Months;
            ShowAgeHeader();
            _root?.Refresh();
            _ = UpdateAgeAsync(_tree.CurrentChartTarget());
        };

        _cur.AgePreferred = _settings.AgeStamp;
        AgePane.InitStamp(_cur.AgePreferred);
        // The choice belongs to the tab; a new scan of that tab adopts it (Scanner.AgeStamp, read when the scan starts).
        AgePane.StampChosen += stamp =>
        {
            _cur.AgePreferred = stamp;
            _settings.AgeStamp = stamp;
            UpdateAgeNotices();
        };
        AgePane.RescanRequested += () =>
        {
            if (!string.IsNullOrWhiteSpace(PathBox.Text)) _ = _scan.ScanPathAsync(PathBox.Text);
        };
        UpdateAgeNotices();
    }

    private void ShowAgeHeader() =>
        OldHeader.Text = "Älter " + AgeBuckets.ThresholdText(AgeThreshold.Months);

    private void SideTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SideTabs) || !AgeTab.IsSelected) return;
        _ = UpdateAgeAsync(_tree.CurrentChartTarget());
    }

    private async Task UpdateAgeAsync(ChartTarget target)
    {
        if (!AgeTab.IsSelected) return; // only worth the walk while it is visible
        UpdateAgeNotices();
        await AgePane.UpdateAsync(target, _cur.AgeStamp);
    }

    /// <summary>A loaded snapshot knows which timestamp it holds; the choice follows it (the saved preference stays).</summary>
    private void OnSnapshotAgeStamp(AgeStamp stamp)
    {
        _cur.AgePreferred = stamp;
        AgePane.ShowStamp(stamp);
        UpdateAgeNotices();
    }

    private void UpdateAgeNotices()
    {
        var shown = _cur.AgeStamp;
        OldHeader.ToolTip = $"Älter als der Schwellwert, nach dem Datum „{AgeStamps.Names[(int)shown]}“";
        AgePane.UpdateNotices(shown, _cur.AgePreferred, hasTree: _root != null, snapshot: _snapshots.IsSnapshotMode);
    }
}
