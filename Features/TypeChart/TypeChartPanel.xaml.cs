using System.Windows.Controls;
using System.Windows.Media;
using DiskTree.Core;
using DiskTree.Ui;

namespace DiskTree.Features.TypeChart;

/// <summary>The "Dateitypen" tab: donut and legend of the file types below the chart target.</summary>
public partial class TypeChartPanel : UserControl
{
    private int _version;

    private static readonly Brush[] CategoryBrushesLight = MakeCategoryBrushes(false);
    private static readonly Brush[] CategoryBrushesDark = MakeCategoryBrushes(true);
    private static Brush[] CategoryBrushes => Theme.IsDark ? CategoryBrushesDark : CategoryBrushesLight;

    /// <summary>True while the types are being summed up (the host does not start another run meanwhile).</summary>
    public bool IsBusy { get; private set; }

    /// <summary>When the last update started (Environment.TickCount64): the host refreshes less often than the tree.</summary>
    public long LastUpdateTick { get; private set; }

    public TypeChartPanel()
    {
        InitializeComponent();
    }

    // On the dark panel the darkest categories (e.g. programs, #3F4A57) would vanish: lighten them a little.
    private static Brush[] MakeCategoryBrushes(bool dark) => FileTypes.All.Select(c =>
    {
        byte r = (byte)(c.Color >> 16), g = (byte)(c.Color >> 8), bl = (byte)c.Color;
        if (dark && 0.299 * r + 0.587 * g + 0.114 * bl < 110)
        {
            r = (byte)(r + (255 - r) * 0.45); g = (byte)(g + (255 - g) * 0.45); bl = (byte)(bl + (255 - bl) * 0.45);
        }
        return BrushFactory.Frozen(r, g, bl);
    }).ToArray();

    public async Task UpdateAsync(ChartTarget target)
    {
        var (node, ownOnly, title) = target;
        int version = ++_version;
        LastUpdateTick = Environment.TickCount64;

        if (node == null)
        {
            Pie.SetData(Array.Empty<PieSlice>(), "", "");
            Legend.ItemsSource = null;
            PieTitle.Text = "Dateitypen";
            return;
        }

        IsBusy = true;
        long[] sizes, counts;
        try
        {
            (sizes, counts) = await Task.Run(() => SumTypes(node, ownOnly));
        }
        finally
        {
            IsBusy = false;
        }
        if (version != _version) return; // a newer request is on its way

        long total = sizes.Sum(), files = counts.Sum();
        var order = Enumerable.Range(0, FileTypes.Count)
            .Where(i => counts[i] > 0)
            .OrderByDescending(i => sizes[i])
            .ToList();

        string Detail(int i) =>
            $"{SizeFormat.Format(sizes[i])} · {counts[i]:N0} Dateien · {(total > 0 ? (double)sizes[i] / total : 0):P1}";

        Pie.SetData(
            order.Select(i => new PieSlice(FileTypes.All[i].Name, sizes[i], CategoryBrushes[i], Detail(i))).ToList(),
            SizeFormat.Format(total), $"{files:N0} Dateien");
        Legend.ItemsSource = order.Select(i => new LegendRow
        {
            Brush = CategoryBrushes[i],
            Name = FileTypes.All[i].Name,
            SizeText = SizeFormat.Format(sizes[i]),
            PercentText = (total > 0 ? (double)sizes[i] / total : 0).ToString("P1"),
            Detail = Detail(i),
        }).ToList();
        PieTitle.Text = "Dateitypen – " + title;
    }

    private static (long[] Sizes, long[] Counts) SumTypes(Node start, bool ownOnly)
    {
        var sizes = new long[FileTypes.Count];
        var counts = new long[FileTypes.Count];
        foreach (var n in ownOnly ? [start] : start.Walk())
        {
            if (n.OwnTypes is { } types)
                foreach (var t in types) { sizes[t.Cat] += t.Size; counts[t.Cat] += t.Count; }
        }
        return (sizes, counts);
    }
}
