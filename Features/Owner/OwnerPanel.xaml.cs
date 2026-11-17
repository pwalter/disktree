using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Ui;
using DiskTree.Infrastructure;

namespace DiskTree.Features.Owner;

public sealed class OwnerRowVM
{
    public Brush Brush { get; init; } = Brushes.Gray;
    public string Name { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string PercentText { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>
/// Right-panel tab: owner of the selected folder, its rights, and (on demand) storage per owner. The source of the scan
/// answers (<see cref="IOwnerSource"/>): Windows owners and ACLs on the disk, uid / gid and POSIX mode bits on an SFTP
/// server (there also per group).
/// </summary>
public partial class OwnerPanel : UserControl
{
    private const long SampleMin = 1024 * 1024;
    private static readonly Brush[] Palette = new[]
    {
        0x2F6FED, 0xE0A820, 0x3BA55D, 0xD9534F, 0x8E6BD8, 0x1FA8B8, 0xE8743B, 0x7A8794, 0xC24F9B, 0x5E9C2E,
    }.Select(BrushFactory.Frozen).ToArray();

    private string? _path;
    private IStorageProvider _provider = DiskProvider.Instance;
    private int _version;
    private CancellationTokenSource? _cts;
    private OwnerResult? _shown;
    private bool _shownSampled;

    public OwnerPanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) ShowOwnerAsync().Forget("ShowOwnerAsync"); };
    }

    private IOwnerSource? Source => OwnerSources.For(_provider);

    /// <summary>Called when the tree selection changes. Cancels a running analysis (it belongs to the old folder).</summary>
    public void SetFolder(string? path) => SetFolder(path, DiskProvider.Instance);

    /// <summary>Like <see cref="SetFolder(string?)"/> for a folder of <paramref name="provider"/> (its path rules, its owners).</summary>
    public void SetFolder(string? path, IStorageProvider provider)
    {
        bool same = provider == _provider && (path == null ? _path == null : _path != null && provider.Paths.NameEquals(path, _path));
        if (same) return;
        _cts?.Cancel();
        _path = path;
        _provider = provider;
        _shown = null;
        OwnerList.ItemsSource = null;
        Pie.Visibility = Visibility.Collapsed;
        SummaryText.Text = "";
        ProgressText.Text = "";
        AclText.Text = "";
        AclExpander.IsExpanded = false;
        var source = Source;
        AclExpander.Header = source?.RightsTitle ?? "Rechte (ACL)";
        SampleBox.Visibility = source is { SamplingHelps: false } ? Visibility.Collapsed : Visibility.Visible;
        GroupingPanel.Visibility = source is { HasGroups: true } ? Visibility.Visible : Visibility.Collapsed;
        bool costs = (provider.Caps & StorageCaps.CostsPerRequest) != 0;
        RunButton.ToolTip = source is { SamplingHelps: false }
            ? "Listet alle Ordner unter dem markierten Ordner erneut (mit " + (source.HasGroups ? "Besitzer und Gruppe" : "dem Besitzer") +
              " jeder Datei) und summiert." + (costs ? " Kostet eine Anfrage je 1.000 Objekte." : "")
            : "Liest den Besitzer jeder Datei unter dem markierten Ordner. Kann auf Netzfreigaben dauern.";
        ShowOwnerAsync().Forget("ShowOwnerAsync");
    }

    private async Task ShowOwnerAsync()
    {
        // Started fire-and-forget from SetFolder: a vanished folder must end up as a message, never as an unobserved fault.
        try { await ShowOwnerCoreAsync(); }
        catch { OwnerText.Text = "Besitzer: nicht lesbar"; }
    }

    private async Task ShowOwnerCoreAsync()
    {
        int v = ++_version;
        string? path = _path;
        var source = Source;
        FolderText.Text = path == null ? "Besitzer" : _provider.Paths.NameOf(path) is { Length: > 0 } n ? n : path;
        RunButton.IsEnabled = _cts == null && path != null && source != null;
        AclExpander.Visibility = path == null || source == null ? Visibility.Collapsed : Visibility.Visible;
        if (path == null) { OwnerText.Text = "Ordner im Baum markieren."; return; }
        if (source == null) { OwnerText.Text = StorageFeatures.WhyNot(_provider, StorageFeature.Owner) ?? "Besitzer: nicht verfügbar"; return; }
        if (!IsVisible) return; // lazy: only look it up while this tab is shown

        OwnerText.Text = "Besitzer wird ermittelt …";
        OwnerDetails? owner = null;
        string? error = null;
        try
        {
            // Off the UI thread: a remote source asks its server (and may resolve names first).
            owner = await Task.Run(() => source.GetOwnerAsync(path, CancellationToken.None));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = ex.Message;
        }
        catch { }
        if (v != _version) return;
        OwnerText.Text = owner == null
            ? "Besitzer: nicht lesbar" + (error != null ? " – " + error : "")
            : "Besitzer: " + owner.Owner + (owner.Group != null ? " · Gruppe: " + owner.Group : "") +
              (owner.Permissions != null ? "\n" + owner.Permissions : "");
    }

    private void AclExpander_Expanded(object sender, RoutedEventArgs e)
    {
        string? path = _path;
        var source = Source;
        if (path == null || source == null || AclText.Text.Length > 0) return;
        AclText.Text = "Wird gelesen …";
        _ = Task.Run(() =>
        {
            try { return source.GetRights(path, CancellationToken.None); }
            catch (Exception ex) { return new[] { "Rechte nicht lesbar: " + ex.Message }; }
        }).ContinueWith(t =>
        {
            if (path == _path) AclText.Text = string.Join(Environment.NewLine, t.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Grouping_Changed(object sender, RoutedEventArgs e)
    {
        if (_shown != null) Show(_shown, _shownSampled);
    }

    private OwnerGrouping Grouping => ByGroupRadio?.IsChecked == true ? OwnerGrouping.Group : OwnerGrouping.User;

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        string? path = _path;
        var source = Source;
        if (path == null || source == null) return;

        var cts = _cts = new CancellationTokenSource();
        long min = source.SamplingHelps && SampleBox.IsChecked == true ? SampleMin : 0;
        RunButton.Content = "Abbrechen";
        Bar.Visibility = Visibility.Visible;
        OwnerList.ItemsSource = null;
        Pie.Visibility = Visibility.Collapsed;
        SummaryText.Text = "";
        _shown = null;
        var ui = Dispatcher;
        long lastTick = 0;
        bool folders = !source.SamplingHelps;

        OwnerResult? result = null;
        string? failure = null;
        try
        {
            result = await Task.Run(() => source.Aggregate(path, min,
                (seen, failed) =>
                {
                    long now = Environment.TickCount64;
                    if (now - lastTick < 200) return;
                    lastTick = now;
                    ui.BeginInvoke(() =>
                    {
                        if (_cts == cts)
                            ProgressText.Text = $"{seen:N0} Dateien geprüft · {failed:N0} " + (folders ? "Ordner nicht lesbar" : "ohne Zugriff");
                    });
                }, cts.Token));
        }
        catch (Exception ex)
        {
            // async void: nothing may escape here (a deleted folder, a lost share, ... must not end the process)
            failure = ex.Message;
        }
        finally
        {
            Bar.Visibility = Visibility.Collapsed;
            RunButton.Content = "Analysieren";
            _cts = null;
            cts.Dispose();
            RunButton.IsEnabled = _path != null;
        }

        if (path != _path) return; // another folder was selected meanwhile
        failure ??= result?.Error;
        if (failure != null)
        {
            ProgressText.Text = "Analyse nicht möglich: " + failure;
            if (result == null || result.Rows.Count == 0) return;
        }
        else ProgressText.Text = result!.Cancelled ? "Abgebrochen." : "Fertig.";
        Show(result!, min > 0);
    }

    private void Show(OwnerResult r, bool sampled)
    {
        _shown = r;
        _shownSampled = sampled;
        var shown = r.RowsBy(Grouping);
        long total = shown.Sum(x => x.Bytes);
        var rows = new List<OwnerRowVM>();
        var slices = new List<PieSlice>();
        for (int i = 0; i < shown.Count; i++)
        {
            var row = shown[i];
            var brush = Palette[i % Palette.Length];
            double share = total > 0 ? (double)row.Bytes / total : 0;
            string detail = $"{SizeFormat.Format(row.Bytes)} · {row.Files:N0} Dateien · {share:P1}";
            rows.Add(new OwnerRowVM { Brush = brush, Name = row.Owner, SizeText = SizeFormat.Format(row.Bytes), PercentText = share.ToString("P1"), Detail = detail });
            slices.Add(new PieSlice(row.Owner, row.Bytes, brush, detail));
        }
        OwnerList.ItemsSource = rows;
        Pie.SetData(slices, SizeFormat.Format(total), $"{shown.Sum(x => x.Files):N0} Dateien");
        Pie.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = $"{r.Seen:N0} Dateien gesehen" +
            (sampled ? $", {r.Skipped:N0} kleinere übersprungen (Stichprobe)" : "") +
            (r.FailedAreFolders ? $", {r.Failed:N0} Ordner nicht lesbar (nicht gezählt)." : $", {r.Failed:N0} ohne Zugriff (nicht gezählt).") +
            (r.Cancelled ? " Teilergebnis." : "");
    }
}
