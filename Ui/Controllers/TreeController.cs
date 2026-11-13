using System.Windows.Controls;
using System.Windows.Threading;
using DiskTree.Core;
using DiskTree.Features.Scan;
using DiskTree.Features.Treemap;

namespace DiskTree.Ui.Controllers;

/// <summary>
/// The folder tree of the active scan tab: showing a scan, the selection and what is derived from it (the folder the
/// charts describe, the scope of a search, the folder of the duplicate search), and the one way to bring a folder into
/// view (<see cref="Reveal"/>) that the treemap, the search hits, the comparison and a tab switch all use.
/// </summary>
public sealed class TreeController
{
    private readonly TreeView _tree;
    private readonly TreemapPanel _treemap;
    private readonly ScanTabsController _tabs;

    public TreeController(TreeView tree, TreemapPanel treemap, ScanTabsController tabs)
    {
        _tree = tree;
        _treemap = treemap;
        _tabs = tabs;

        tree.SelectedItemChanged += (_, _) =>
        {
            _treemap.SetTreeSelection(CurrentChartTarget().Node);
            SelectionChanged?.Invoke();
        };
        // Treemap click / dive: select that folder in the tree (opening the path down to it). The treemap raises this
        // while it handles the click itself, so the tree follows after that is done; the treemap keeps the focus.
        treemap.NodeClicked += node => RevealLater(node, focus: false);
    }

    /// <summary>The selection changed (after the treemap was told): charts and owner view follow.</summary>
    public event Action? SelectionChanged;

    // ---------------------------------------------------------------- what is shown

    private NodeVM? Root => _tabs.Current.Root;

    public NodeVM? SelectedRow => _tree.SelectedItem as NodeVM;

    /// <summary>The folder selected in the tree, or null (a file, the "Dateien" row or nothing is selected).</summary>
    public Node? SelectedFolder => SelectedRow is { Kind: Kind.Dir, Node: { } sel } ? sel : null;

    /// <summary>Takes the tree away (a scan starts, or another tab is about to be shown).</summary>
    public void Clear() => _tree.ItemsSource = null;

    /// <summary>
    /// Shows the tree of the scan that <paramref name="s"/> now holds (a new scan or a loaded one): the rows start at the
    /// top of the scan, opened down to the scanned folder.
    /// </summary>
    public void ShowScan(ScanSession s)
    {
        var scanner = s.Scanner!;
        s.ScanTarget = scanner.ScanTarget;
        s.Root = new NodeVM(scanner.Root!, Kind.Dir, null, 0) { IsExpanded = true };
        _treemap.Reset(s.ScanTarget ?? scanner.Root);
        _tree.ItemsSource = new[] { s.Root };
        ExpandToScanTarget(s);
    }

    /// <summary>Shows the tree a tab already has (it becomes the active tab again).</summary>
    public void ShowExisting(ScanSession s)
    {
        if (s.Root != null)
        {
            s.Root.Refresh();      // background scans / theme changes did not reach this tree
            s.Root.RefreshTheme();
            _tree.ItemsSource = new[] { s.Root };
        }
        _treemap.Reset(s.ScanTarget ?? s.Scanner?.Root);
    }

    // When a subfolder is scanned the tree starts at the drive: open the chain down to the scanned folder.
    private static void ExpandToScanTarget(ScanSession s)
    {
        var vm = s.Root;
        while (vm != null && vm.Node != s.ScanTarget)
        {
            vm.IsExpanded = true;
            vm = vm.Children.FirstOrDefault(c => c.Kind == Kind.Dir);
        }
        if (vm != null) vm.IsExpanded = true;
    }

    /// <summary>The numbers of all rows (of the active tab) after something changed.</summary>
    public void RefreshRows() => Root?.Refresh();

    /// <summary>The colours of all rows after the theme changed.</summary>
    public void RefreshTheme() => Root?.RefreshTheme();

    // ---------------------------------------------------------------- what is derived from the selection

    // What the charts describe: the selected folder with everything below it, or just the files of one folder.
    public ChartTarget CurrentChartTarget()
    {
        switch (SelectedRow)
        {
            case { Kind: Kind.Dir } d:
                return new(d.Node, false, d.Node?.Name ?? "");
            case { Kind: Kind.Files } f:
                return new(f.Parent?.Node, true, "Dateien in " + f.Parent?.Node?.Name);
            case { Kind: Kind.File } file:
                var dir = file.Parent?.Parent;
                return new(dir?.Node, true, "Dateien in " + dir?.Node?.Name);
        }
        var s = _tabs.Current;
        var target = s.ScanTarget ?? s.Root?.Node;
        return new(target, false, target?.Name ?? "");
    }

    // Search below the selected folder (for a file or the "Dateien" row: its folder), otherwise below the scanned folder.
    public Node? SearchScope() => SelectedRow switch
    {
        NodeVM { Kind: Kind.Dir, Node: { } n } => n,
        NodeVM { Kind: Kind.Files or Kind.File, Parent.Node: { } n } => n,
        _ => _tabs.Current.ScanTarget ?? Root?.Node,
    };

    // Folder for the duplicate search: the selected folder (or the folder of the selected file), else what was scanned.
    public string? DuplicateRoot()
    {
        var vm = SelectedRow;
        if (vm is { Kind: Kind.File or Kind.Files }) vm = vm.Parent is { Kind: Kind.Dir } p ? p : vm.Parent?.Parent;
        var path = vm is { Kind: Kind.Dir } ? vm.Path : (_tabs.Current.ScanTarget ?? Root?.Node)?.FullPath;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    // ---------------------------------------------------------------- bringing a folder into view

    /// <summary>
    /// Opens the tree down to the folder, selects it and scrolls it into view (and, unless <paramref name="focus"/> is
    /// false, gives it the keyboard focus). Returns its row (null if the folder is not in the tree).
    /// </summary>
    public NodeVM? Reveal(Node target, bool focus = true)
    {
        var root = Root;
        if (root?.Node == null) return null;

        var chain = NodeChain.Between(root.Node, target);
        if (chain == null) return null;

        // Open the folders on the way (this creates the child rows).
        var rows = new List<NodeVM> { root };
        for (int i = 1; i < chain.Count; i++)
        {
            rows[^1].IsExpanded = true;
            if (rows[^1].FindDescendant(chain[i]) is not { } next) return null;
            rows.Add(next);
        }

        // Walk down the containers so that virtualized rows get created, then select the last one.
        ItemsControl host = _tree;
        for (int i = 0; i < rows.Count; i++)
        {
            var c = host.ItemContainerGenerator.ContainerFromItem(rows[i]) as TreeViewItem;
            if (c == null)
            {
                host.UpdateLayout();
                VisualTreeHelpers.FindDescendant<VirtualizingPanel>(host)?.BringIndexIntoViewPublic(host.Items.IndexOf(rows[i]));
                host.UpdateLayout();
                c = host.ItemContainerGenerator.ContainerFromItem(rows[i]) as TreeViewItem;
            }
            if (c == null) break;
            if (i == rows.Count - 1)
            {
                c.IsSelected = true;
                c.BringIntoView();
                if (focus) c.Focus();
            }
            else
            {
                c.IsExpanded = true;
                host = c;
            }
        }
        return rows[^1];
    }

    /// <summary>
    /// <see cref="Reveal"/> once the caller is done and the pending layout has run: for requests that arrive in the middle
    /// of another control's mouse handling (treemap, comparison list).
    /// </summary>
    public void RevealLater(Node target, bool focus) =>
        _tree.Dispatcher.InvokeAsync(() => Reveal(target, focus), DispatcherPriority.Loaded);

    /// <summary>Jump from the comparison: shows the folder with that path in the tree. False if the tree does not have it.</summary>
    public bool TryJumpToPath(string path)
    {
        // PHASE-D: FindNode matches a path prefix without checking the separator ("C:\a" also finds "C:\ab")
        var node = _tabs.Current.Scanner?.FindNode(path);
        if (Root == null || node == null) return false;
        RevealLater(node, focus: true);
        return true;
    }
}
