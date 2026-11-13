using System.IO;
using System.Windows;
using System.Windows.Controls;
using DiskTree.Core;
using DiskTree.Infrastructure;

namespace DiskTree.Ui.Controllers;

/// <summary>What the shell menu code needs from the window: the scan of the active tab and a way to bring the views up to date.</summary>
public interface IShellMenuHost
{
    /// <summary>The scanner of the active tab.</summary>
    Scanner? Scanner { get; }
    /// <summary>
    /// A scan runs in another tab: tells the user and returns true (only one scan at a time, nothing else meanwhile).
    /// </summary>
    bool BlockedByOtherScan();
    /// <summary>Tree, file types, age data and the largest files after something was deleted behind the program's back.</summary>
    void RefreshAfterDelete();
    /// <summary>Scans a folder of the existing tree again, in place.</summary>
    void Rescan(Node? node);
    /// <summary>Move / ZIP / rename / delete entries for the menu (empty in a stored scan).</summary>
    IReadOnlyList<(string Text, Action Run)> OperationExtras(string path, bool isDir);
}

/// <summary>
/// The Windows shell context menu for the rows of the window (tree, largest files, search hits, duplicates): our own
/// entries on top of it, the small menu of a stored scan, and keeping the tree in step when the menu removed the item.
/// </summary>
public sealed class ShellMenuCoordinator
{
    private readonly Window _window;
    private readonly IShellMenuHost _host;
    private readonly SnapshotController _snapshots;

    public ShellMenuCoordinator(Window window, IShellMenuHost host, SnapshotController snapshots)
    {
        _window = window;
        _host = host;
        _snapshots = snapshots;
    }

    // The "Dateien" row stands for a whole folder's files, so it gets no menu: "Löschen" there would hit the folder.
    // The artificial "Mehrere Pfade" root has no path either.
    public static bool CanUseShell(NodeVM vm) => vm.Kind is Kind.Dir or Kind.File && vm.Path.Length > 0;

    // ---------------------------------------------------------------- tree rows

    public void ShowTreeMenu(NodeVM vm, Point screenPos)
    {
        if (!CanUseShell(vm)) return;
        if (_snapshots.IsSnapshotMode) { ShowSnapshotMenu(vm.Path, vm.Kind == Kind.Dir); return; }
        RunShell(vm.Path, vm.Kind == Kind.File, vm.Size, vm,
            () => ShellContextMenu.Show(_window, vm.Path, screenPos, Extras(vm.Path, vm.Kind == Kind.Dir)));
    }

    public void DeleteNode(NodeVM vm) =>
        RunShell(vm.Path, vm.Kind == Kind.File, vm.Size, vm, () => ShellContextMenu.InvokeVerb(_window, vm.Path, "delete"));

    // ---------------------------------------------------------------- file rows (no tree row behind them)

    /// <summary>Menu of a file in a list; a stored scan gets its small menu instead of the Windows one.</summary>
    public void ShowFileMenu(string path, long size, Point screenPos)
    {
        if (_snapshots.IsSnapshotMode) { ShowSnapshotMenu(path, isDir: false); return; }
        ShowFileShellMenu(path, size, screenPos);
    }

    /// <summary>The Windows menu of a file (a stored scan only shows a hint).</summary>
    public void ShowFileShellMenu(string path, long size, Point screenPos) =>
        RunShell(path, isFile: true, size, null,
            () => ShellContextMenu.Show(_window, path, screenPos, Extras(path, isDir: false)));

    public void DeleteFile(string path, long size) =>
        RunShell(path, isFile: true, size, null, () => ShellContextMenu.InvokeVerb(_window, path, "delete"));

    // ---------------------------------------------------------------- shared

    /// <summary>Our own entries on top of the Windows menu.</summary>
    public IReadOnlyList<(string Text, Action Run)> Extras(string path, bool isDir)
    {
        var list = new List<(string, Action)>();

        // Re-evaluate from here: for a folder the folder itself, for a file the folder it lies in.
        // Afterwards the folder (and everything above it) has the numbers of what is really on disk.
        if (isDir)
            list.Add(("Ab hier neu scannen", () => _host.Rescan(_host.Scanner?.FindNode(path))));
        else
            list.Add(("Ordner dieser Datei neu scannen", () => _host.Rescan(_host.Scanner?.FindNode(Path.GetDirectoryName(path) ?? ""))));

        list.Add(("Übergeordneten Ordner öffnen", () => OpenInExplorer(path)));
        list.Add(("Pfad kopieren", () => Clipboard.SetText(path)));
        list.AddRange(_host.OperationExtras(path, isDir)); // move / ZIP / rename / delete (FileOpsController)
        return list;
    }

    /// <summary>Opens the folder that contains the item and selects the item there.</summary>
    public static void OpenInExplorer(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return; // e.g. a stored scan of another machine
        ShellOpen.InExplorer(path);
    }

    /// <summary>
    /// Runs a shell action on an item and, if the item is gone afterwards (recycle bin or permanently), takes it out of
    /// the tree and the other views.
    /// </summary>
    public void RunShell(string path, bool isFile, long size, NodeVM? vm, Action action)
    {
        if (_snapshots.IsSnapshotMode) { _snapshots.ShowHint(); return; }
        if (_host.BlockedByOtherScan()) return;
        int ageBucket = isFile ? _host.Scanner?.Ages.OfFile(path) ?? -1 : -1; // read before the file is gone
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Dialogs.Warn(_window, ex.Message);
            return;
        }

        // The menu may have deleted the item (recycle bin or permanently): if it is gone, take it out of the tree.
        if (File.Exists(path) || Directory.Exists(path)) return;

        var scanner = _host.Scanner;
        if (vm != null)
            vm.RemoveFromTree(ageBucket);
        else if (isFile && scanner?.FindNode(Path.GetDirectoryName(path) ?? "") is { } dir)
            TreeEditor.RemoveFile(dir, Path.GetFileName(path), size, ageBucket);

        scanner?.RemoveTop(path);
        if (scanner?.Root is { } drive) Scanner.ReadDrive(drive);

        _host.RefreshAfterDelete();
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    /// <summary>In a snapshot the Windows menu (with "Löschen") is replaced by a small menu with our own entries.</summary>
    private void ShowSnapshotMenu(string path, bool isDir)
    {
        var menu = new ContextMenu();
        foreach (var (text, run) in Extras(path, isDir))
        {
            // Rescan and "open in Explorer" need the path to exist on this machine.
            bool needsDisk = !text.StartsWith("Pfad");
            var item = new MenuItem { Header = text, IsEnabled = !needsDisk || Exists(isDir ? path : Path.GetDirectoryName(path) ?? "") };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Gespeicherter Scan: Löschen nicht möglich", IsEnabled = false });
        menu.IsOpen = true;
    }
}
