using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskTree.Features.Search;
using DiskTree.Infrastructure;

namespace DiskTree.Ui.Controllers;

/// <summary>Glue between the "Suche" tab (SearchPanel) and the tree / shell menu of the main window.</summary>
public sealed class SearchController
{
    private readonly Window _window;
    private readonly SearchPanel _search;
    private readonly SideTabsController _sideTabs;
    private readonly TabItem _searchTab;
    private readonly TreeController _tree;
    private readonly ShellMenuCoordinator _shell;

    public SearchController(Window window, SearchPanel search, SideTabsController sideTabs, TabItem searchTab,
        TreeController tree, ShellMenuCoordinator shell)
    {
        _window = window;
        _search = search;
        _sideTabs = sideTabs;
        _searchTab = searchTab;
        _tree = tree;
        _shell = shell;

        _search.ScopeProvider = _tree.SearchScope;
        _search.StorageProvider = () => _tree.Provider;
        _search.Navigate = hit => _tree.Reveal(hit.Node);
        _search.ShowMenu = ShowMenu;
        _search.DeleteHit = hit => RunShell(hit, () => ShellContextMenu.InvokeVerb(_window, hit.Path, "delete"));
    }

    /// <summary>Ctrl+F: switch to the search tab and put the cursor into the name box.</summary>
    public void Show()
    {
        _sideTabs.Select(_searchTab);
        _window.Dispatcher.BeginInvoke(_search.FocusInput, DispatcherPriority.Input);
    }

    private void ShowMenu(SearchHit hit, Point screenPos)
    {
        if (hit.IsDir)
        {
            if (_tree.Reveal(hit.Node) is { } vm) _shell.ShowTreeMenu(vm, screenPos);
            _search.PruneMissing();
        }
        else if (_shell.NotLocal != null)
        {
            _shell.ShowFileMenu(hit.Path, hit.Size, screenPos); // remote source: our own small menu
        }
        else
        {
            RunShell(hit, () => ShellContextMenu.Show(_window, hit.Path, screenPos, _shell.Extras(hit.Path, isDir: false)));
        }
    }

    private void RunShell(SearchHit hit, Action action)
    {
        if (hit.IsDir)
        {
            if (_tree.Reveal(hit.Node) is { } vm) _shell.RunShell(hit.Path, false, hit.Size, vm, action);
        }
        else
        {
            _shell.RunShell(hit.Path, isFile: true, hit.Size, null, action);
        }
        _search.PruneMissing();
    }
}
