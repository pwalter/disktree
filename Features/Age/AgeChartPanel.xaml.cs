using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskTree.Core;
using DiskTree.Ui;

namespace DiskTree.Features.Age;

/// <summary>
/// The "Alter" tab: donut and legend per age bucket, the threshold for "old" (also used by the "Älter" tree column),
/// and the "Zeitstempel" switch. The scan only builds one set of buckets (see AgeStamps), so a different timestamp than
/// the one the current tree was built with needs a new scan; the tab says so and offers it.
/// The panel keeps no scan state: the host passes the numbers it needs and handles the events.
/// </summary>
public partial class AgeChartPanel : UserControl
{
    // Green (recent) -> red (old); readable on the light and the dark panel alike.
    private static readonly Brush[] AgeBrushes = new[] { 0x3FA66B, 0x9CC24A, 0xE8B923, 0xE8832A, 0xD0473E }
        .Select(BrushFactory.Frozen).ToArray();

    private int _version;
    private bool _settingStampBox;

    /// <summary>The user picked another threshold (<see cref="AgeThreshold.Months"/> is already set).</summary>
    public event Action? ThresholdChanged;
    /// <summary>The user picked a timestamp for the next scan of the tab.</summary>
    public event Action<AgeStamp>? StampChosen;
    /// <summary>"Jetzt neu scannen" was clicked.</summary>
    public event Action? RescanRequested;

    public AgeChartPanel()
    {
        InitializeComponent();
    }

    /// <summary>Fills the threshold box; <paramref name="months"/> is the saved choice. Call before <see cref="InitStamp"/>.</summary>
    public void InitThreshold(int months)
    {
        AgeThreshold.Months = months;
        foreach (var m in AgeBuckets.LimitMonths)
            AgeThresholdBox.Items.Add(AgeBuckets.ThresholdText(m));
        AgeThresholdBox.SelectedIndex = AgeThreshold.OldFrom - 1;
        AgeThresholdBox.SelectionChanged += (_, _) =>
        {
            if (AgeThresholdBox.SelectedIndex < 0) return;
            AgeThreshold.Months = AgeBuckets.LimitMonths[AgeThresholdBox.SelectedIndex];
            ThresholdChanged?.Invoke();
        };
    }

    /// <summary>Fills the timestamp box with the tab's current choice.</summary>
    public void InitStamp(AgeStamp preferred)
    {
        foreach (var n in AgeStamps.Names) AgeStampBox.Items.Add(n);
        AgeStampBox.SelectedIndex = (int)preferred;
        AgeStampBox.SelectionChanged += (_, _) =>
        {
            if (_settingStampBox || AgeStampBox.SelectedIndex < 0) return;
            StampChosen?.Invoke((AgeStamp)AgeStampBox.SelectedIndex);
        };
    }

    /// <summary>Shows a tab's choice in the box without raising <see cref="StampChosen"/>.</summary>
    public void ShowStamp(AgeStamp stamp)
    {
        _settingStampBox = true;
        AgeStampBox.SelectedIndex = (int)stamp;
        _settingStampBox = false;
    }

    private void AgeRescan_Click(object sender, RoutedEventArgs e) => RescanRequested?.Invoke();

    /// <summary>
    /// The notices under the boxes. <paramref name="shown"/> is the timestamp the tab's numbers were built from,
    /// <paramref name="wanted"/> the user's choice, <paramref name="hasTree"/> whether the tab has a scan at all.
    /// </summary>
    public void UpdateNotices(AgeStamp shown, AgeStamp wanted, bool hasTree, bool snapshot)
    {
        bool mismatch = hasTree && shown != wanted;
        AgeSwitchNotice.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
        if (mismatch)
        {
            AgeSwitchText.Text = $"Die Zahlen stammen vom Zeitstempel „{AgeStamps.Names[(int)shown]}“. " +
                $"Für „{AgeStamps.Names[(int)wanted]}“ ist ein Neuscan nötig" +
                (snapshot ? " (ein gespeicherter Scan enthält nur einen Zeitstempel)." : ".");
            AgeRescanButton.Visibility = snapshot ? Visibility.Collapsed : Visibility.Visible;
        }

        bool access = wanted == AgeStamp.Accessed || (hasTree && shown == AgeStamp.Accessed);
        AgeAccessNotice.Visibility = access ? Visibility.Visible : Visibility.Collapsed;
        if (access)
        {
            AgeAccessText.Text = AccessTimeInfo.Warning;
            AgeAccessNotice.BorderBrush = AccessTimeInfo.IsSevere ? Brushes.OrangeRed : Brushes.Goldenrod;
        }
    }

    /// <summary>Redraws chart, legend and summary for the target; <paramref name="shown"/> is the tab's timestamp.</summary>
    public async Task UpdateAsync(ChartTarget target, AgeStamp shown)
    {
        var (node, ownOnly, title) = target;
        int version = ++_version;
        var lower = AgeStamps.Lower[(int)shown];

        if (node == null)
        {
            AgePie.SetData(Array.Empty<PieSlice>(), "", "");
            AgeLegend.ItemsSource = null;
            AgeSummary.Text = "";
            AgeTitle.Text = $"Alter ({lower})";
            return;
        }

        var (sizes, counts) = await Task.Run(() => SumAges(node, ownOnly));
        if (version != _version) return;

        long total = sizes.Sum(), files = counts.Sum();
        double Share(long v) => total > 0 ? (double)v / total : 0;

        string Detail(int i) => $"{SizeFormat.Format(sizes[i])} · {counts[i]:N0} Dateien · {Share(sizes[i]):P1}";

        var rows = Enumerable.Range(0, AgeBuckets.Count).Where(i => counts[i] > 0).ToList();
        AgePie.SetData(
            rows.Select(i => new PieSlice(AgeBuckets.Names[i], sizes[i], AgeBrushes[i], Detail(i))).ToList(),
            SizeFormat.Format(total), $"{files:N0} Dateien");
        AgeLegend.ItemsSource = Enumerable.Range(0, AgeBuckets.Count).Select(i => new LegendRow
        {
            Brush = AgeBrushes[i],
            Name = AgeBuckets.Names[i],
            SizeText = SizeFormat.Format(sizes[i]),
            PercentText = Share(sizes[i]).ToString("P1"),
            Detail = Detail(i),
        }).ToList();

        long old = 0, oldFiles = 0;
        for (int i = AgeThreshold.OldFrom; i < AgeBuckets.Count; i++) { old += sizes[i]; oldFiles += counts[i]; }
        AgeSummary.Text = total > 0
            ? $"Älter als {AgeBuckets.ThresholdText(AgeThreshold.Months)} ({lower}): {SizeFormat.Format(old)} ({Share(old):P0}) in {oldFiles:N0} Dateien"
            : "";
        AgeTitle.Text = $"Alter ({lower}) – " + title;
        AgeTitle.ToolTip = AgeTitle.Text;
    }

    private static (long[] Sizes, long[] Counts) SumAges(Node start, bool ownOnly)
    {
        var sizes = new long[AgeBuckets.Count];
        var counts = new long[AgeBuckets.Count];
        foreach (var n in ownOnly ? [start] : start.Walk())
        {
            if (n.OwnAges is { } a)
                for (int i = 0; i < AgeBuckets.Count; i++) { sizes[i] += a[i]; counts[i] += a[AgeBuckets.Count + i]; }
        }
        return (sizes, counts);
    }
}
