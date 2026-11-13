using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskTree.Core;
using DiskTree.Core.Snapshots;
using Microsoft.Win32;

namespace DiskTree.Ui;

/// <summary>Display row of the comparison list.</summary>
public sealed class DiffItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required string DeltaText { get; init; }
    public required string Glyph { get; init; }
    public required Brush Brush { get; init; }
}

/// <summary>
/// "Vergleich" tab: compares a stored scan with the current one (or two stored scans) and lists the folders
/// that grew, shrank, are new or were removed, biggest change first.
/// </summary>
public partial class ComparePanel : UserControl
{
    private const int MaxRows = 3000;

    /// <summary>Returns the scan currently shown, or null when there is none (or it is still running).</summary>
    public Func<Scanner?>? CurrentScanner { get; set; }

    /// <summary>Double click on a row: the main window selects that folder in the tree.</summary>
    public event Action<string>? JumpRequested;

    private CompareResult? _result;
    private string _oldLabel = "", _newLabel = "";

    public ComparePanel()
    {
        InitializeComponent();
        Loaded += (_, _) => { Theme.Changed -= OnTheme; Theme.Changed += OnTheme; };
        Unloaded += (_, _) => Theme.Changed -= OnTheme;
    }

    private void OnTheme() => ShowRows();

    // ---------------------------------------------------------------- actions

    private async void Current_Click(object sender, RoutedEventArgs e)
    {
        var scanner = CurrentScanner?.Invoke();
        if (scanner?.ScanTarget == null)
        {
            Info("Es gibt keinen fertigen Scan im Baum. Erst scannen (und warten) oder einen gespeicherten Scan öffnen.");
            return;
        }

        var file = PickFile("Älteren Scan zum Vergleichen wählen");
        if (file == null) return;

        var newTarget = scanner.ScanTarget;
        var newLabel = (scanner.IsSnapshot ? "Geladener Scan " : "Aktueller Scan ") + Stamp(scanner.ScannedAt);
        await CompareAsync(file, null, newTarget, newLabel);
    }

    private async void Two_Click(object sender, RoutedEventArgs e)
    {
        var older = PickFile("Älteren Scan wählen");
        if (older == null) return;
        var newer = PickFile("Neueren Scan wählen");
        if (newer == null) return;
        await CompareAsync(older, newer, null, "");
    }

    private async Task CompareAsync(string oldFile, string? newFile, Node? newTarget, string newLabel)
    {
        SetBusy(true);
        try
        {
            var (result, oldLabel, label) = await Task.Run(() =>
            {
                var old = SnapshotStore.Load(oldFile);
                var oldLbl = $"{System.IO.Path.GetFileName(oldFile)} ({Stamp(old.Info.ScannedAt)})";
                var target = newTarget;
                var lbl = newLabel;
                if (target == null)
                {
                    var nw = SnapshotStore.Load(newFile!);
                    target = nw.Target;
                    lbl = $"{System.IO.Path.GetFileName(newFile!)} ({Stamp(nw.Info.ScannedAt)})";
                }
                return (SnapshotCompare.Compare(old.Target, target), oldLbl, lbl);
            });

            _result = result;
            _oldLabel = oldLabel;
            _newLabel = label;
            FilterBox.IsEnabled = true;
            ShowSummary();
            ShowRows();
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Info(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        CurrentButton.IsEnabled = TwoButton.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : null;
        if (busy) SummaryText.Text = "Vergleiche …";
    }

    private static void Show(string text) => Dialogs.Info(Application.Current.MainWindow, text);

    private void Info(string text)
    {
        SummaryText.Text = text;
        Show(text);
    }

    private string? PickFile(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "DiskTree-Scan (*.disktree)|*.disktree|Alle Dateien|*.*",
        };
        return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
    }

    private static string Stamp(DateTime? t) =>
        t is { } v && v != default ? v.ToString("g", System.Globalization.CultureInfo.CurrentCulture) : "ohne Datum";

    // ---------------------------------------------------------------- display

    private void ShowSummary()
    {
        var r = _result!;
        int Count(DiffKind k) => r.Rows.Count(x => x.Kind == k);
        long delta = r.NewTotal - r.OldTotal;

        SummaryText.Text =
            $"Alt: {_oldLabel}\nNeu: {_newLabel}\nVerglichen: {r.CommonPath}\n" +
            $"Gesamt: {SizeFormat.Format(r.OldTotal)} → {SizeFormat.Format(r.NewTotal)} ({FormatDelta(delta)})\n" +
            $"{Count(DiffKind.Grown):N0} gewachsen · {Count(DiffKind.Shrunk):N0} geschrumpft · " +
            $"{Count(DiffKind.New):N0} neu · {Count(DiffKind.Removed):N0} entfernt";
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ShowRows();

    private void ShowRows()
    {
        if (_result == null || List == null) return;

        DiffKind? only = FilterBox.SelectedIndex switch
        {
            1 => DiffKind.Grown,
            2 => DiffKind.Shrunk,
            3 => DiffKind.New,
            4 => DiffKind.Removed,
            _ => null,
        };

        bool dark = Theme.IsDark;
        var rows = _result.Rows.Where(r => only == null || r.Kind == only).Take(MaxRows).Select(r =>
        {
            var parent = System.IO.Path.GetDirectoryName(r.Path.TrimEnd('\\')) ?? "";
            var sizes = r.Kind switch
            {
                DiffKind.New => $"neu · {SizeFormat.Format(r.NewSize)}",
                DiffKind.Removed => $"entfernt · {SizeFormat.Format(r.OldSize)}",
                _ => $"{SizeFormat.Format(r.OldSize)} → {SizeFormat.Format(r.NewSize)}",
            };
            return new DiffItem
            {
                Path = r.Path,
                Name = r.Name.Length > 0 ? r.Name : r.Path,
                Detail = parent.Length > 0 ? $"{sizes} · {parent}" : sizes,
                DeltaText = FormatDelta(r.Delta),
                Glyph = r.Kind switch { DiffKind.Grown => "▲", DiffKind.Shrunk => "▼", DiffKind.New => "＋", _ => "✕" },
                Brush = BrushFor(r.Kind, dark),
            };
        }).ToList();

        List.ItemsSource = rows;
    }

    public static string FormatDelta(long delta) =>
        delta == 0 ? "±0" : (delta > 0 ? "+" : "−") + SizeFormat.Format(Math.Abs(delta));

    // Readable on both the light and the dark panel background.
    private static readonly Brush[] LightBrushes =
        { BrushFactory.Frozen(0xC6, 0x28, 0x28), BrushFactory.Frozen(0x2E, 0x7D, 0x32), BrushFactory.Frozen(0xB2, 0x5E, 0x00), BrushFactory.Frozen(0x54, 0x6E, 0x7A) };
    private static readonly Brush[] DarkBrushes =
        { BrushFactory.Frozen(0xFF, 0x7B, 0x72), BrushFactory.Frozen(0x6F, 0xCF, 0x8B), BrushFactory.Frozen(0xF0, 0xB3, 0x5A), BrushFactory.Frozen(0x9F, 0xB3, 0xC0) };

    private static Brush BrushFor(DiffKind kind, bool dark) => (dark ? DarkBrushes : LightBrushes)[(int)kind];

    // ---------------------------------------------------------------- jump to folder

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VisualTreeHelpers.FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is DiffItem item)
            JumpRequested?.Invoke(item.Path);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is DiffItem item)
        {
            e.Handled = true;
            JumpRequested?.Invoke(item.Path);
        }
    }
}
