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
        SearchPane.ScopeProvider = _tree.SearchScope;
        SearchPane.Navigate = hit => _tree.Reveal(hit.Node);
        SearchPane.ShowMenu = ShowSearchMenu;
        SearchPane.DeleteHit = hit => RunSearchShell(hit, () => ShellContextMenu.InvokeVerb(this, hit.Path, "delete"));
    }

    /// <summary>Ctrl+F: switch to the search tab and put the cursor into the name box.</summary>
    private void ShowSearch()
    {
        SideTabs.SelectedItem = SearchTab;
        Dispatcher.BeginInvoke(SearchPane.FocusInput, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ShowSearchMenu(SearchHit hit, Point screenPos)
    {
        if (hit.IsDir)
        {
            if (_tree.Reveal(hit.Node) is { } vm) _shell.ShowTreeMenu(vm, screenPos);
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
            if (_tree.Reveal(hit.Node) is { } vm) _shell.RunShell(hit.Path, false, hit.Size, vm, action);
        }
        else
        {
            _shell.RunShell(hit.Path, isFile: true, hit.Size, null, action);
        }
        SearchPane.PruneMissing();
    }
}
