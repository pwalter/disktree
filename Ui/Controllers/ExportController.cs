using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskTree.Core;
using DiskTree.Features.Reports;
using Microsoft.Win32;

namespace DiskTree.Ui.Controllers;

/// <summary>What the export needs to know about the window: the scan of the active tab and the selected folder.</summary>
public interface IExportSource
{
    Scanner? Scanner { get; }
    /// <summary>The folder selected in the tree, or null.</summary>
    Node? SelectedFolder { get; }
    bool ScanRunning { get; }
}

/// <summary>Export (CSV, HTML / PDF report) behind the "Exportieren…" button.</summary>
public sealed class ExportController
{
    private readonly Window _owner;
    private readonly Button _button;
    private readonly IExportSource _source;
    private bool _exporting;

    public ExportController(Window owner, Button button, IExportSource source)
    {
        _owner = owner;
        _button = button;
        _source = source;
        _button.Click += async (_, _) => await ExportAsync();
    }

    private async Task ExportAsync()
    {
        if (_exporting) return;
        var scanner = _source.Scanner;
        if (scanner?.Root == null)
        {
            Dialogs.Info(_owner, "Es gibt noch nichts zu exportieren. Bitte zuerst einen Ordner scannen.", "Exportieren");
            return;
        }

        var selected = _source.SelectedFolder;
        var whole = scanner.ScanTarget ?? scanner.Root;
        bool scanning = _source.ScanRunning;

        var kind = ExportDialog.Ask(_owner, selected, scanning);
        if (kind == null) return;

        bool report = kind is ExportKind.ReportHtml or ExportKind.ReportPdf;
        bool pdf = kind == ExportKind.ReportPdf;
        Node? node = kind == ExportKind.Subtree ? selected : kind == ExportKind.WholeScan ? whole : null;
        var dlg = new SaveFileDialog
        {
            Title = "Exportieren",
            Filter = pdf ? "PDF-Datei (*.pdf)|*.pdf" : report ? "HTML-Datei (*.html)|*.html" : "CSV-Datei (*.csv)|*.csv",
            DefaultExt = pdf ? ".pdf" : report ? ".html" : ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = report ? ReportBuilder.DefaultFileName(pdf ? "pdf" : "html") : Exporter.DefaultFileName(kind.Value, node),
        };
        if (dlg.ShowDialog(_owner) != true) return;

        var file = dlg.FileName;
        var top = kind == ExportKind.TopFiles ? scanner.TopFiles() : null;
        _exporting = true;
        _button.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            if (report)
            {
                await Task.Run(() =>
                {
                    var data = ReportBuilder.Build(scanner, scanning);
                    if (pdf) ReportPdf.Write(file, data); else ReportHtml.Write(file, data);
                });
                Mouse.OverrideCursor = null;
                var open = Dialogs.Confirm(_owner,
                    $"Bericht gespeichert:\n{file}" + (scanning ? "\n\nHinweis: Der Scan lief noch, die Zahlen sind ein Zwischenstand." : "") + "\n\nJetzt öffnen?", MessageBoxImage.Information, title: "Exportieren");
                if (open)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
                return;
            }

            int rows = await Task.Run(() => top != null ? Exporter.WriteTopFiles(file, top) : Exporter.WriteTree(file, node!));
            Mouse.OverrideCursor = null;
            Dialogs.Info(_owner,
                $"{rows:N0} Zeilen exportiert nach:\n{file}" + (scanning ? "\n\nHinweis: Der Scan lief noch, die Zahlen sind ein Zwischenstand." : ""), "Exportieren");
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            Dialogs.Warn(_owner, "Der Export ist fehlgeschlagen:\n" + ex.Message, "Exportieren");
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _exporting = false;
            _button.IsEnabled = true;
        }
    }
}
