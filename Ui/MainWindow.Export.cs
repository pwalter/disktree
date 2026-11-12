using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskTree.Core;
using DiskTree.Features.Reports;
using Microsoft.Win32;

namespace DiskTree.Ui;

// Export (CSV). Kept in its own file; MainWindow.xaml only has the button.
public partial class MainWindow
{
    private bool _exporting;

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;
        var scanner = _scanner;
        if (scanner?.Root == null)
        {
            Dialogs.Info(this, "Es gibt noch nichts zu exportieren. Bitte zuerst einen Ordner scannen.", "Exportieren");
            return;
        }

        var selected = Tree.SelectedItem is NodeVM { Kind: Kind.Dir, Node: { } sel } ? sel : null;
        var whole = scanner.ScanTarget ?? scanner.Root;
        bool scanning = _cts != null;

        var kind = ExportDialog.Ask(this, selected, scanning);
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
        if (dlg.ShowDialog(this) != true) return;

        var file = dlg.FileName;
        var top = kind == ExportKind.TopFiles ? scanner.TopFiles() : null;
        _exporting = true;
        ExportButton.IsEnabled = false;
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
                var open = Dialogs.Confirm(this,
                    $"Bericht gespeichert:\n{file}" + (scanning ? "\n\nHinweis: Der Scan lief noch, die Zahlen sind ein Zwischenstand." : "") + "\n\nJetzt öffnen?", MessageBoxImage.Information, title: "Exportieren");
                if (open)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
                return;
            }

            int rows = await Task.Run(() => top != null ? Exporter.WriteTopFiles(file, top) : Exporter.WriteTree(file, node!));
            Mouse.OverrideCursor = null;
            Dialogs.Info(this,
                $"{rows:N0} Zeilen exportiert nach:\n{file}" + (scanning ? "\n\nHinweis: Der Scan lief noch, die Zahlen sind ein Zwischenstand." : ""), "Exportieren");
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            Dialogs.Warn(this, "Der Export ist fehlgeschlagen:\n" + ex.Message, "Exportieren");
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _exporting = false;
            ExportButton.IsEnabled = true;
        }
    }
}
