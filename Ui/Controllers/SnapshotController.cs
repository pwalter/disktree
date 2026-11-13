using System.IO;
using System.Windows;
using System.Windows.Controls;
using DiskTree.Core;
using DiskTree.Core.Snapshots;
using DiskTree.Features.Scan;
using DiskTree.Infrastructure;
using Microsoft.Win32;

namespace DiskTree.Ui.Controllers;

/// <summary>What the snapshot controller needs from the window (scan tabs, tree, status line).</summary>
public interface ISnapshotHost
{
    /// <summary>The active scan tab.</summary>
    ScanSession Current { get; }
    /// <summary>A file operation or the updater works on the current tab: nothing may change it.</summary>
    bool TabsLocked { get; }
    /// <summary>Adds an empty tab and shows it.</summary>
    ScanSession NewTab();
    /// <summary>Sets the status text of a tab; the window shows it if that tab is the active one.</summary>
    void SetStatus(ScanSession session, string text);
    /// <summary>
    /// Puts a loaded scan into the active tab <paramref name="session"/>: age timestamp, scanner, tree, treemap, path box.
    /// </summary>
    void ShowSnapshot(ScanSession session, Scanner scanner, AgeStamp stamp);
    /// <summary>The status line of a freshly loaded scan: no tooltip, the given text.</summary>
    void ShowSnapshotStatus(ScanSession session, string text);
    /// <summary>File types, largest files etc. for the scan that was just shown.</summary>
    void RefreshViews();
}

/// <summary>
/// Stored scans (.disktree): save, open, drop onto the window, the window title that says "gespeicherter Scan",
/// and the checks that depend on whether the current scan is a stored one.
/// </summary>
public sealed class SnapshotController
{
    private readonly Window _window;
    private readonly ISnapshotHost _host;
    private readonly Button _saveButton;

    public SnapshotController(Window window, ISnapshotHost host, Button saveButton, Button openButton)
    {
        _window = window;
        _host = host;
        _saveButton = saveButton;
        _saveButton.Click += async (_, _) => await SaveAsync();
        openButton.Click += async (_, _) => await OpenAsync();
    }

    private Scanner? CurrentScanner => _host.Current.Scanner;

    public bool IsSnapshotMode => CurrentScanner is { IsSnapshot: true };

    /// <summary>A fresh scan starts: remember when, and drop the "gespeicherter Scan" marker from the title.</summary>
    public void OnNewScanner(Scanner scanner)
    {
        scanner.ScannedAt = DateTime.Now;
        UpdateTitle();
    }

    public void UpdateTitle()
    {
        var title = AdminHelper.IsAdmin ? "DiskTree (Administrator)" : "DiskTree";
        if (CurrentScanner is { IsSnapshot: true } s)
            title += " – gespeicherter Scan" + (s.ScannedAt is { } t ? " vom " + t.ToString("g") : "");
        _window.Title = title;
    }

    private static string StampText(DateTime? t) => t is { } v ? v.ToString("g") : "unbekannter Zeit";

    // ---------------------------------------------------------------- save

    private async Task SaveAsync()
    {
        var session = _host.Current;
        var scanner = session.Scanner;
        if (scanner?.Root == null)
        {
            Dialogs.Info(_window, "Es gibt noch keinen Scan, den man speichern könnte.");
            return;
        }
        if (session.Cts != null)
        {
            Dialogs.Info(_window, "Der Scan läuft noch. Bitte warten oder abbrechen, dann speichern.");
            return;
        }

        var target = scanner.ScanTarget ?? scanner.Root;
        var name = new string((string.IsNullOrEmpty(target.Name) ? "Scan" : target.Name)
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim('_', ' ');
        var dlg = new SaveFileDialog
        {
            Title = "Scan speichern",
            Filter = "DiskTree-Scan (*.disktree)|*.disktree",
            DefaultExt = SnapshotStore.Extension,
            FileName = $"{(name.Length > 0 ? name : "Scan")}_{(scanner.ScannedAt ?? DateTime.Now):yyyy-MM-dd}",
        };
        if (dlg.ShowDialog(_window) != true) return;

        var file = dlg.FileName;
        var at = scanner.ScannedAt ?? DateTime.Now;
        _saveButton.IsEnabled = false;
        var before = session.Status;
        _host.SetStatus(session, "Scan wird gespeichert …");
        try
        {
            await Task.Run(() => SnapshotStore.Save(file, scanner, at));
            _host.SetStatus(session, $"Scan gespeichert: {file} ({SizeFormat.Format(new FileInfo(file).Length)})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.SetStatus(session, before);
            Dialogs.Info(_window, "Speichern fehlgeschlagen:\n" + ex.Message);
        }
        finally
        {
            _saveButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- open

    private async Task OpenAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Gespeicherten Scan öffnen",
            Filter = "DiskTree-Scan (*.disktree)|*.disktree|Alle Dateien|*.*",
        };
        if (dlg.ShowDialog(_window) == true) await LoadAsync(dlg.FileName);
    }

    /// <summary>A file dropped on the window: if it is a stored scan it is opened. Returns true if it was one.</summary>
    public async Task<bool> TryHandleDropAsync(string firstFile)
    {
        if (!firstFile.EndsWith(SnapshotStore.Extension, StringComparison.OrdinalIgnoreCase) || !File.Exists(firstFile))
            return false;
        await LoadAsync(firstFile);
        return true;
    }

    // A stored scan opens in a new tab (in the active one if that is still empty), so the scan being looked at
    // stays; a scan running in another tab is not disturbed either.
    private async Task LoadAsync(string file)
    {
        if (_host.TabsLocked) return;
        var origin = _host.Current;
        var before = origin.Status;
        _host.SetStatus(origin, "Scan wird geladen …");
        LoadedSnapshot snap;
        try
        {
            snap = await Task.Run(() => SnapshotStore.Load(file));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _host.SetStatus(origin, before);
            Dialogs.Info(_window, $"Die Datei konnte nicht geladen werden:\n{file}\n\n{ex.Message}");
            return;
        }

        _host.SetStatus(origin, before);
        if (_host.TabsLocked) return;
        var s = _host.Current.IsEmpty ? _host.Current : _host.NewTab();
        var scanner = Scanner.FromSnapshot(snap);
        _host.ShowSnapshot(s, scanner, snap.AgeStamp);

        var t = scanner.ScanTarget!;
        string where = string.IsNullOrEmpty(t.FullPath) ? t.Name : t.FullPath;
        s.SetTitle("💾 " + where, $"Gespeicherter Scan vom {StampText(scanner.ScannedAt)}: {where}");
        _host.ShowSnapshotStatus(s,
            $"Gespeicherter Scan vom {StampText(scanner.ScannedAt)}: {t.DirCount + 1:N0} Ordner · {t.FileCount:N0} Dateien · " +
            $"{SizeFormat.Format(t.Size)} · {t.FullPath}");
        UpdateTitle();
        _host.RefreshViews();
    }

    // ---------------------------------------------------------------- checks that depend on the stored scan

    public void ShowHint() =>
        Dialogs.Info(_window,
            $"Das ist ein gespeicherter Scan vom {StampText(CurrentScanner?.ScannedAt)}, nicht der aktuelle Stand der Platte.\n\n" +
            "Löschen und das Windows-Kontextmenü gibt es nur in einem aktuellen Scan (Scannen). " +
            "Mit „Ab hier neu scannen“ lässt sich ein Ordner, der auf diesem Rechner existiert, neu einlesen.");

    /// <summary>Rescan below a loaded snapshot only makes sense when the folder exists here.</summary>
    public bool CanRescan(Node node)
    {
        if (!IsSnapshotMode || Directory.Exists(node.FullPath)) return true;
        Dialogs.Info(_window, $"Der Ordner existiert auf diesem Rechner nicht:\n{node.FullPath}\n\nEr kann nicht neu gescannt werden.");
        return false;
    }
}
