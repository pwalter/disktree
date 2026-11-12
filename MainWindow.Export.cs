using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace DiskTree;

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
            MessageBox.Show(this, "Es gibt noch nichts zu exportieren. Bitte zuerst einen Ordner scannen.", "Exportieren",
                MessageBoxButton.OK, MessageBoxImage.Information);
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
                var open = MessageBox.Show(this,
                    $"Bericht gespeichert:\n{file}" + (scanning ? "\n\nHinweis: Der Scan lief noch, die Zahlen sind ein Zwischenstand." : "") + "\n\nJetzt öffnen?",
                    "Exportieren", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (open == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
                return;
            }

            int rows = await Task.Run(() => top != null ? Exporter.WriteTopFiles(file, top) : Exporter.WriteTree(file, node!));
            Mouse.OverrideCursor = null;
            MessageBox.Show(this,
                $"{rows:N0} Zeilen exportiert nach:\n{file}" + (scanning ? "\n\nHinweis: Der Scan lief noch, die Zahlen sind ein Zwischenstand." : ""),
                "Exportieren", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            MessageBox.Show(this, "Der Export ist fehlgeschlagen:\n" + ex.Message, "Exportieren", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _exporting = false;
            ExportButton.IsEnabled = true;
        }
    }
}

/// <summary>Small dialog: what to export. Built in code, colours only from the theme brushes.</summary>
internal static class ExportDialog
{
    public static ExportKind? Ask(Window owner, Node? selected, bool scanning)
    {
        var rbSub = new RadioButton
        {
            Content = selected == null ? "Baum ab markiertem Ordner (zuerst einen Ordner markieren)" : $"Baum ab markiertem Ordner: {selected.Name}",
            IsEnabled = selected != null,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var rbAll = new RadioButton { Content = "Gesamter Scan", Margin = new Thickness(0, 0, 0, 8) };
        var rbTop = new RadioButton { Content = "Liste der größten Dateien", Margin = new Thickness(0, 0, 0, 8) };
        var rbHtml = new RadioButton { Content = "Bericht (HTML) – Speicherplatz-Bericht für den gesamten Scan", Margin = new Thickness(0, 0, 0, 8) };
        var rbPdf = new RadioButton { Content = "Bericht (PDF) – Speicherplatz-Bericht für den gesamten Scan", Margin = new Thickness(0, 0, 0, 8) };
        (selected != null ? rbSub : rbAll).IsChecked = true;

        var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        panel.Children.Add(new TextBlock { Text = "Was soll exportiert werden?", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(rbSub);
        panel.Children.Add(rbAll);
        panel.Children.Add(rbTop);
        panel.Children.Add(rbHtml);
        panel.Children.Add(rbPdf);
        if (scanning)
        {
            var hint = new TextBlock { Text = "Der Scan läuft noch. Exportiert wird der aktuelle Zwischenstand.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            panel.Children.Add(hint);
        }

        var ok = new Button { Content = "Weiter…", MinWidth = 90, Height = 30, IsDefault = true };
        var cancel = new Button { Content = "Abbrechen", MinWidth = 90, Height = 30, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        if (owner.TryFindResource("ToolButton") is Style tb)
        {
            ok.Style = tb;
            cancel.Style = tb;
            cancel.SetResourceReference(Control.BackgroundProperty, "ToolGray");
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = "Exportieren", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 380, MaxWidth = 560, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Content = panel, FontFamily = owner.FontFamily, FontSize = owner.FontSize,
        };
        win.SetResourceReference(Control.BackgroundProperty, "WindowBg");
        win.SetResourceReference(Control.ForegroundProperty, "Ink");
        ok.Click += (_, _) => win.DialogResult = true;

        if (win.ShowDialog() != true) return null;
        return rbSub.IsChecked == true ? ExportKind.Subtree
            : rbTop.IsChecked == true ? ExportKind.TopFiles
            : rbHtml.IsChecked == true ? ExportKind.ReportHtml
            : rbPdf.IsChecked == true ? ExportKind.ReportPdf
            : ExportKind.WholeScan;
    }
}
