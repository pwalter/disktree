using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskTree.Core;
using DiskTree.Ui;
using DiskTree.Ui.Controllers;

namespace DiskTree.Features.TopFiles;

/// <summary>The "Größte Dateien" tab: the largest files of the scan, with the shell menu and keys of the host.</summary>
public partial class TopFilesPanel : UserControl
{
    /// <summary>Show the Windows shell menu for the row at a screen position (right click, Apps key).</summary>
    public Action<TopRow, Point>? ShowMenu { get; set; }
    /// <summary>Delete on the row (the shell's own delete).</summary>
    public Action<TopRow>? DeleteRow { get; set; }
    /// <summary>Keys the host handles for the selection (F2, Delete on several rows); returns true if it used the key.</summary>
    public Func<Key, bool>? ExtraKeyDown { get; set; }

    /// <summary>When the list was filled last (Environment.TickCount64): the host refreshes it less often than the tree.</summary>
    public long LastRefreshTick { get; private set; }

    /// <summary>The list itself (the host reads the selection of the list the user works in).</summary>
    public ListBox List => TopList;

    public IReadOnlyList<TopRow> SelectedRows => TopList.SelectedItems.Cast<TopRow>().ToList();

    public TopFilesPanel()
    {
        InitializeComponent();
        ListInteraction.Attach(TopList, new ListBehavior<TopRow>
        {
            Selected = () => TopList.SelectedItem as TopRow,
            ExtraKey = (key, _) => ExtraKeyDown?.Invoke(key) == true, // F2, Delete on several rows
            Delete = row => DeleteRow?.Invoke(row),
            ShowMenu = (row, pos) => ShowMenu?.Invoke(row, pos),
        });
    }

    public void Clear() => TopList.ItemsSource = null;

    /// <summary>Shows the largest files of <paramref name="scanner"/> (an empty list without a scan); the selection stays where possible.</summary>
    public void Refresh(Scanner? scanner)
    {
        LastRefreshTick = Environment.TickCount64;
        if (scanner == null)
        {
            TopList.ItemsSource = null;
            return;
        }

        var selected = TopList.SelectedItems.Cast<TopRow>().Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = scanner.TopFiles().Select(f => new TopRow
        {
            Path = f.Path,
            Name = Path.GetFileName(f.Path),
            Folder = Path.GetDirectoryName(f.Path) ?? "",
            Size = f.Size,
            SizeText = SizeFormat.Format(f.Size),
        }).ToList();

        TopList.ItemsSource = rows;
        foreach (var r in rows)
            if (selected.Contains(r.Path)) TopList.SelectedItems.Add(r);
    }

    private void TopList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VisualTreeHelpers.FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is TopRow row)
            ShellMenuCoordinator.OpenInExplorer(row.Path);
    }
}
