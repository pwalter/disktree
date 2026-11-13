using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskTree.Core;
using DiskTree.Ui;
using DiskTree.Infrastructure;

namespace DiskTree.Features.Search;

public partial class SearchPanel : UserControl
{
    private CancellationTokenSource? _cts;

    /// <summary>Folder to search below (selected folder, else the scan root); null if nothing was scanned.</summary>
    public Func<Node?>? ScopeProvider { get; set; }
    /// <summary>Jump to the hit in the tree.</summary>
    public Action<SearchHit>? Navigate { get; set; }
    /// <summary>Show the Windows shell menu for the hit at a screen position.</summary>
    public Action<SearchHit, Point>? ShowMenu { get; set; }
    public Action<SearchHit>? DeleteHit { get; set; }
    /// <summary>Keys the host handles for the selection (F2, Delete on several hits); returns true if it used the key.</summary>
    public Func<Key, bool>? ExtraKeyDown { get; set; }

    public IReadOnlyList<SearchHit> SelectedHits => Results.SelectedItems.OfType<SearchHitRow>().Select(r => r.Hit).ToList();

    public SearchPanel()
    {
        InitializeComponent();
        Mode_Changed(null, null!);

        // Context menu, Delete, Enter and the keys of the host: the same behaviour as in every other list (ListInteraction).
        ListInteraction.Attach(Results, new ListBehavior<SearchHitRow>
        {
            Selected = () => Results.SelectedItem as SearchHitRow,
            ExtraKey = (key, _) => ExtraKeyDown?.Invoke(key) == true,
            Enter = row => Navigate?.Invoke(row.Hit),
            Delete = row => DeleteHit?.Invoke(row.Hit),
            ShowMenu = (row, pos) => ShowMenu?.Invoke(row.Hit, pos),
        });
    }

    /// <summary>The host switched to another scan: running search and hits belonged to the old tree.</summary>
    public void Reset()
    {
        _cts?.Cancel();
        Results.ItemsSource = null;
        StatusText.Text = "";
        ScopeText.Text = "";
    }

    public void FocusInput()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    /// <summary>Removes hits that are gone from disk (after a delete through the shell menu).</summary>
    public void PruneMissing()
    {
        if (Results.ItemsSource is not List<SearchHitRow> list) return;
        var rest = list.Where(r => r.Hit.IsDir ? System.IO.Directory.Exists(r.Hit.Path) : System.IO.File.Exists(r.Hit.Path)).ToList();
        if (rest.Count != list.Count) Results.ItemsSource = rest;
    }

    private void Mode_Changed(object? sender, SelectionChangedEventArgs? e)
    {
        if (ExtBox == null) return;
        bool files = ModeBox.SelectedIndex == 1;
        ExtBox.IsEnabled = files;
        ExtLabel.Opacity = files ? 1 : 0.5;
        MoreFilters.IsEnabled = files; // date / attribute filters need files
    }

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        StartAsync().Forget("StartAsync");
    }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) _cts.Cancel();
        else StartAsync().Forget("StartAsync");
    }

    private async Task StartAsync()
    {
        if (_cts != null) return;

        var scope = ScopeProvider?.Invoke();
        if (scope == null)
        {
            StatusText.Text = "Zuerst einen Ordner scannen.";
            return;
        }

        long minSize = 0;
        var sizeText = SizeBox.Text.Trim();
        if (sizeText.Length > 0)
        {
            if ((!double.TryParse(sizeText, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) &&
                 !double.TryParse(sizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) || v < 0)
            {
                StatusText.Text = "Ungültige Größe.";
                return;
            }
            minSize = (long)(v * (UnitBox.SelectedIndex == 1 ? 1024.0 * 1024 * 1024 : 1024.0 * 1024));
        }

        bool files = ModeBox.SelectedIndex == 1;
        var exts = files
            ? ExtBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => "." + x.TrimStart('*', '.').ToLowerInvariant()).ToArray()
            : [];
        var text = NameBox.Text.Trim();
        string? filterError = null;
        var filter = files ? BuildFilter(out filterError) : null;
        if (filterError != null)
        {
            StatusText.Text = filterError;
            MoreFilters.IsExpanded = true;
            return;
        }

        if (text.Length == 0 && minSize == 0 && exts.Length == 0 && filter == null)
        {
            StatusText.Text = "Name, Mindestgröße, Endung oder einen weiteren Filter angeben.";
            return;
        }

        var query = new SearchQuery { Scope = scope, Text = text, MinSize = minSize, Extensions = exts, Files = files, Filter = filter };
        ScopeText.Text = "In: " + scope.FullPath;

        MoreFilters.IsExpanded = false; // make room for the hits; the header says how many filters are active
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        GoButton.Content = "Abbrechen";
        Busy.Visibility = Visibility.Visible;
        string what = files ? "Suche auf der Platte" : "Suche";
        StatusText.Text = what + " …";
        var progress = new Progress<int>(n => StatusText.Text = $"{what} … {n:N0} Ordner geprüft");

        try
        {
            var result = await Task.Run(() => SearchEngine.Run(query, progress, ct), ct);
            Results.ItemsSource = result.Hits.Select(h => new SearchHitRow(h)).ToList();
            StatusText.Text = result.Hits.Count == 0
                ? $"Keine Treffer ({result.FoldersVisited:N0} Ordner geprüft)."
                : $"{result.Hits.Count:N0} Treffer" + (result.Truncated ? $" (auf {SearchEngine.MaxHits:N0} begrenzt, Suche eingrenzen)" : "")
                  + $" · {result.FoldersVisited:N0} Ordner geprüft";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Suche abgebrochen.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Fehler: " + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            GoButton.Content = "Suchen";
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VisualTreeHelpers.FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is SearchHitRow row) Navigate?.Invoke(row.Hit);
    }
}
