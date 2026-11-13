using DiskTree.Core;
using DiskTree.Ui.Controllers;

namespace DiskTree.Ui;

// What the shell menu code (ShellMenuCoordinator) reads from and asks of the window.
public partial class MainWindow : IShellMenuHost
{
    Scanner? IShellMenuHost.Scanner => _scanner;

    bool IShellMenuHost.BlockedByOtherScan()
    {
        // only one scan at a time, nothing else meanwhile
        if (Busy is not { } other || other == _cur) return false;
        NoticeBusy(other);
        return true;
    }

    void IShellMenuHost.RefreshAfterDelete()
    {
        _root?.Refresh();
        _ = UpdatePieAsync();
        TopPane.Refresh(_scanner);
    }

    private void Notice(string text) =>
        Dialogs.Info(this, text);

    void IShellMenuHost.Rescan(Node? node) => _ = RescanAsync(node);

    IReadOnlyList<(string Text, Action Run)> IShellMenuHost.OperationExtras(string path, bool isDir) => OpExtras(path, isDir);
}
