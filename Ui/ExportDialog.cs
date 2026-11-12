using System.Windows;
using System.Windows.Controls;
using DiskTree.Core;
using DiskTree.Features.Reports;

namespace DiskTree.Ui;

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
