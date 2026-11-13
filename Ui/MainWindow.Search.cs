using System.Windows;
using System.Windows.Controls;
using DiskTree.Core;
using DiskTree.Features.Search;
using DiskTree.Infrastructure;
using DiskTree.Ui.Controllers;

namespace DiskTree.Ui;

// Glue between the "Suche" tab (SearchPanel) and the tree / shell menu of the main window.
public partial class MainWindow
{
    private void InitSearch()
    {
        SearchPane.ScopeProvider = SearchScope;
        SearchPane.Navigate = hit => RevealNode(hit.Node);
        SearchPane.ShowMenu = ShowSearchMenu;
        SearchPane.DeleteHit = hit => RunSearchShell(hit, () => ShellContextMenu.InvokeVerb(this, hit.Path, "delete"));
    }

    /// <summary>Ctrl+F: switch to the search tab and put the cursor into the name box.</summary>
    private void ShowSearch()
    {
        SideTabs.SelectedItem = SearchTab;
        Dispatcher.BeginInvoke(SearchPane.FocusInput, System.Windows.Threading.DispatcherPriority.Input);
    }

    // Search below the selected folder (for a file or the "Dateien" row: its folder), otherwise below the scanned folder.
    private Node? SearchScope() => Tree.SelectedItem switch
    {
        NodeVM { Kind: Kind.Dir, Node: { } n } => n,
        NodeVM { Kind: Kind.Files or Kind.File, Parent.Node: { } n } => n,
        _ => _scanTarget ?? _root?.Node,
    };

    private void ShowSearchMenu(SearchHit hit, Point screenPos)
    {
        if (hit.IsDir)
        {
            if (RevealNode(hit.Node) is { } vm) _shell.ShowTreeMenu(vm, screenPos);
            SearchPane.PruneMissing();
        }
        else
        {
            RunSearchShell(hit, () => ShellContextMenu.Show(this, hit.Path, screenPos, _shell.Extras(hit.Path, isDir: false)));
        }
    }

    private void RunSearchShell(SearchHit hit, Action action)
    {
        if (hit.IsDir)
        {
            if (RevealNode(hit.Node) is { } vm) _shell.RunShell(hit.Path, false, hit.Size, vm, action);
        }
        else
        {
            _shell.RunShell(hit.Path, isFile: true, hit.Size, null, action);
        }
        SearchPane.PruneMissing();
    }

    /// <summary>Opens the tree down to the folder, selects it and scrolls it into view. Returns its row (null if not in the tree).</summary>
    private NodeVM? RevealNode(Node target)
    {
        if (_root?.Node == null) return null;

        var chain = new List<Node>();
        for (var n = target; n != null; n = n.Parent)
        {
            chain.Add(n);
            if (n == _root.Node) break;
        }
        if (chain[^1] != _root.Node) return null;
        chain.Reverse();

        var rows = new List<NodeVM> { _root };
        for (int i = 1; i < chain.Count; i++)
        {
            rows[^1].IsExpanded = true;
            if (rows[^1].FindDescendant(chain[i]) is not { } next) return null;
            rows.Add(next);
        }

        // Walk down the containers so that virtualized rows get created, then select the last one.
        ItemsControl host = Tree;
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
                c.Focus();
            }
            else
            {
                c.IsExpanded = true;
                host = c;
            }
        }
        return rows[^1];
    }
}
