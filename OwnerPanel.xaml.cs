using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DiskTree;

public sealed class OwnerRowVM
{
    public Brush Brush { get; init; } = Brushes.Gray;
    public string Name { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string PercentText { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>Right-panel tab: owner of the selected folder, its ACL, and (on demand) storage per owner.</summary>
public partial class OwnerPanel : UserControl
{
    private const long SampleMin = 1024 * 1024;
    private static readonly Brush[] Palette = new[]
    {
        0x2F6FED, 0xE0A820, 0x3BA55D, 0xD9534F, 0x8E6BD8, 0x1FA8B8, 0xE8743B, 0x7A8794, 0xC24F9B, 0x5E9C2E,
    }.Select(c =>
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
        b.Freeze();
        return (Brush)b;
    }).ToArray();

    private string? _path;
    private int _version;
    private CancellationTokenSource? _cts;

    public OwnerPanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) _ = ShowOwnerAsync(); };
    }

    /// <summary>Called when the tree selection changes. Cancels a running analysis (it belongs to the old folder).</summary>
    public void SetFolder(string? path)
    {
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return;
        _cts?.Cancel();
        _path = path;
        OwnerList.ItemsSource = null;
        Pie.Visibility = Visibility.Collapsed;
        SummaryText.Text = "";
        ProgressText.Text = "";
        AclText.Text = "";
        AclExpander.IsExpanded = false;
        _ = ShowOwnerAsync();
    }

    private async Task ShowOwnerAsync()
    {
        int v = ++_version;
        string? path = _path;
        FolderText.Text = path == null ? "Besitzer" : System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;
        RunButton.IsEnabled = _cts == null && path != null;
        AclExpander.Visibility = path == null ? Visibility.Collapsed : Visibility.Visible;
        if (path == null) { OwnerText.Text = "Ordner im Baum markieren."; return; }
        if (!IsVisible) return; // lazy: only look it up while this tab is shown

        OwnerText.Text = "Besitzer wird ermittelt …";
        string? owner = null;
        try { owner = await OwnerInfo.GetOwnerNameAsync(path); } catch { }
        if (v != _version) return;
        OwnerText.Text = owner == null ? "Besitzer: nicht lesbar" : "Besitzer: " + owner;
    }

    private void AclExpander_Expanded(object sender, RoutedEventArgs e)
    {
        string? path = _path;
        if (path == null || AclText.Text.Length > 0) return;
        AclText.Text = "Wird gelesen …";
        _ = Task.Run(() => OwnerInfo.GetAclLines(path)).ContinueWith(t =>
        {
            if (path == _path) AclText.Text = string.Join(Environment.NewLine, t.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        string? path = _path;
        if (path == null) return;

        var cts = _cts = new CancellationTokenSource();
        long min = SampleBox.IsChecked == true ? SampleMin : 0;
        RunButton.Content = "Abbrechen";
        Bar.Visibility = Visibility.Visible;
        OwnerList.ItemsSource = null;
        Pie.Visibility = Visibility.Collapsed;
        SummaryText.Text = "";
        var ui = Dispatcher;
        long lastTick = 0;

        OwnerResult result;
        try
        {
            // Few threads: owner lookups on a network share are latency-bound, and the file server should not be flooded.
            bool unc = path.StartsWith(@"\\");
            result = await Task.Run(() => OwnerAggregator.Aggregate(
                OwnerAggregator.EnumerateFiles(path),
                p => OwnerInfo.GetOwnerSid(p) is { } sid ? OwnerInfo.ResolveName(sid) : null,
                min, unc ? 4 : 8,
                (seen, failed) =>
                {
                    long now = Environment.TickCount64;
                    if (now - lastTick < 200) return;
                    lastTick = now;
                    ui.BeginInvoke(() => { if (_cts == cts) ProgressText.Text = $"{seen:N0} Dateien geprüft · {failed:N0} ohne Zugriff"; });
                }, cts.Token));
        }
        finally
        {
            Bar.Visibility = Visibility.Collapsed;
            RunButton.Content = "Analysieren";
            _cts = null;
            RunButton.IsEnabled = _path != null;
        }

        if (path != _path) return; // another folder was selected meanwhile
        ProgressText.Text = result.Cancelled ? "Abgebrochen." : "Fertig.";
        Show(result, min > 0);
    }

    private void Show(OwnerResult r, bool sampled)
    {
        long total = r.TotalBytes;
        var rows = new List<OwnerRowVM>();
        var slices = new List<PieSlice>();
        for (int i = 0; i < r.Rows.Count; i++)
        {
            var row = r.Rows[i];
            var brush = Palette[i % Palette.Length];
            double share = total > 0 ? (double)row.Bytes / total : 0;
            string detail = $"{NodeVM.FormatSize(row.Bytes)} · {row.Files:N0} Dateien · {share:P1}";
            rows.Add(new OwnerRowVM { Brush = brush, Name = row.Owner, SizeText = NodeVM.FormatSize(row.Bytes), PercentText = share.ToString("P1"), Detail = detail });
            slices.Add(new PieSlice(row.Owner, row.Bytes, brush, detail));
        }
        OwnerList.ItemsSource = rows;
        Pie.SetData(slices, NodeVM.FormatSize(total), $"{r.Rows.Sum(x => x.Files):N0} Dateien");
        Pie.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = $"{r.Seen:N0} Dateien gesehen" +
            (sampled ? $", {r.Skipped:N0} kleinere übersprungen (Stichprobe)" : "") +
            $", {r.Failed:N0} ohne Zugriff (nicht gezählt)." + (r.Cancelled ? " Teilergebnis." : "");
    }
}
