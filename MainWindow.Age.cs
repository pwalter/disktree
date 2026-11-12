using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DiskTree;

// Age analysis: "Alter" tab (donut + legend per age bucket) and the threshold for the "Älter" tree column.
public partial class MainWindow
{
    // Green (recent) -> red (old); readable on the light and the dark panel alike.
    private static readonly Brush[] AgeBrushes = new[] { 0x3FA66B, 0x9CC24A, 0xE8B923, 0xE8832A, 0xD0473E }
        .Select(c =>
        {
            var b = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
            b.Freeze();
            return (Brush)b;
        }).ToArray();

    private int _ageVersion;

    private void InitAge()
    {
        AgeBuckets.ThresholdMonths = _settings.AgeThresholdMonths;
        foreach (var m in AgeBuckets.LimitMonths)
            AgeThresholdBox.Items.Add(AgeBuckets.ThresholdText(m));
        AgeThresholdBox.SelectedIndex = AgeBuckets.OldFrom - 1;
        ShowAgeHeader();
        AgeThresholdBox.SelectionChanged += AgeThreshold_Changed;
    }

    private void ShowAgeHeader() =>
        OldHeader.Text = "Älter " + AgeBuckets.ThresholdText(AgeBuckets.ThresholdMonths);

    private void AgeThreshold_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AgeThresholdBox.SelectedIndex < 0) return;
        AgeBuckets.ThresholdMonths = AgeBuckets.LimitMonths[AgeThresholdBox.SelectedIndex];
        _settings.AgeThresholdMonths = AgeBuckets.ThresholdMonths;
        ShowAgeHeader();
        _root?.Refresh();
        var (node, ownOnly, title) = PieTarget();
        _ = UpdateAgeAsync(node, ownOnly, title);
    }

    private void SideTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SideTabs) || !AgeTab.IsSelected) return;
        var (node, ownOnly, title) = PieTarget();
        _ = UpdateAgeAsync(node, ownOnly, title);
    }

    private async Task UpdateAgeAsync(Node? node, bool ownOnly, string title)
    {
        if (!AgeTab.IsSelected) return; // only worth the walk while it is visible
        int version = ++_ageVersion;
        UpdateAgeNotices();

        if (node == null)
        {
            AgePie.SetData(Array.Empty<PieSlice>(), "", "");
            AgeLegend.ItemsSource = null;
            AgeSummary.Text = "";
            AgeTitle.Text = $"Alter ({AgeStamps.Lower[(int)AgeStamps.Current]})";
            return;
        }

        var (sizes, counts) = await Task.Run(() => SumAges(node, ownOnly));
        if (version != _ageVersion) return;

        long total = sizes.Sum(), files = counts.Sum();
        double Share(long v) => total > 0 ? (double)v / total : 0;

        string Detail(int i) => $"{NodeVM.FormatSize(sizes[i])} · {counts[i]:N0} Dateien · {Share(sizes[i]):P1}";

        var rows = Enumerable.Range(0, AgeBuckets.Count).Where(i => counts[i] > 0).ToList();
        AgePie.SetData(
            rows.Select(i => new PieSlice(AgeBuckets.Names[i], sizes[i], AgeBrushes[i], Detail(i))).ToList(),
            NodeVM.FormatSize(total), $"{files:N0} Dateien");
        AgeLegend.ItemsSource = Enumerable.Range(0, AgeBuckets.Count).Select(i => new TypeRow
        {
            Brush = AgeBrushes[i],
            Name = AgeBuckets.Names[i],
            SizeText = NodeVM.FormatSize(sizes[i]),
            PercentText = Share(sizes[i]).ToString("P1"),
            Detail = Detail(i),
        }).ToList();

        long old = 0, oldFiles = 0;
        for (int i = AgeBuckets.OldFrom; i < AgeBuckets.Count; i++) { old += sizes[i]; oldFiles += counts[i]; }
        AgeSummary.Text = total > 0
            ? $"Älter als {AgeBuckets.ThresholdText(AgeBuckets.ThresholdMonths)} ({AgeStamps.Lower[(int)AgeStamps.Current]}): {NodeVM.FormatSize(old)} ({Share(old):P0}) in {oldFiles:N0} Dateien"
            : "";
        AgeTitle.Text = $"Alter ({AgeStamps.Lower[(int)AgeStamps.Current]}) – " + title;
        AgeTitle.ToolTip = AgeTitle.Text;
    }

    private static (long[] Sizes, long[] Counts) SumAges(Node start, bool ownOnly)
    {
        var sizes = new long[AgeBuckets.Count];
        var counts = new long[AgeBuckets.Count];
        var stack = new Stack<Node>();
        stack.Push(start);

        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.OwnAges is { } a)
                for (int i = 0; i < AgeBuckets.Count; i++) { sizes[i] += a[i]; counts[i] += a[AgeBuckets.Count + i]; }
            if (!ownOnly)
                foreach (var c in n.Snapshot()) stack.Push(c);
        }
        return (sizes, counts);
    }
}
