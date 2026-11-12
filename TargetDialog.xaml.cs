using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace DiskTree;

public sealed record TargetChoice(string Target, ConflictPolicy Policy, bool RecycleOriginals);

/// <summary>Asks for the destination folder of "Verschieben" or the ZIP file of "In ZIP archivieren", and shows what is about to happen.</summary>
public partial class TargetDialog : Window
{
    private readonly bool _zip;
    public TargetChoice? Result { get; private set; }

    private sealed record PolicyOption(string Text, ConflictPolicy Policy)
    {
        public override string ToString() => Text;
    }

    public TargetDialog(bool zip, IReadOnlyList<OpItem> items, string initial)
    {
        InitializeComponent();
        _zip = zip;

        long size = items.Sum(i => i.Size);
        int dirs = items.Count(i => i.IsDir);
        string what = items.Count == 1 ? (items[0].IsDir ? "1 Ordner" : "1 Datei")
            : dirs == 0 ? $"{items.Count:N0} Dateien" : $"{items.Count:N0} Elemente ({dirs:N0} Ordner)";
        Summary.Text = $"{what} · {NodeVM.FormatSize(size)}";
        Names.Text = string.Join("\n", items.Take(40).Select(i => i.Path)) + (items.Count > 40 ? $"\n… und {items.Count - 40:N0} weitere" : "");
        TargetBox.Text = initial;

        if (zip)
        {
            Title = "In ZIP archivieren";
            TargetLabel.Text = "ZIP-Datei";
            OkButton.Content = "Archivieren";
            MovePanel.Visibility = Visibility.Collapsed;
            RecycleBox.Visibility = Visibility.Visible;
            Hint.Text = "Die Originale bleiben erhalten, sofern der Haken nicht gesetzt ist. Sie wandern nur in den Papierkorb, wenn sie vollständig archiviert wurden.";
        }
        else
        {
            Title = "Verschieben nach …";
            foreach (var o in new[]
                     {
                         new PolicyOption("Nachfragen", ConflictPolicy.Ask),
                         new PolicyOption("Überspringen", ConflictPolicy.Skip),
                         new PolicyOption("Ersetzen (alte Datei in den Papierkorb)", ConflictPolicy.Replace),
                         new PolicyOption("Beide behalten (neuen Namen vergeben)", ConflictPolicy.Rename),
                     })
                PolicyBox.Items.Add(o);
            PolicyBox.SelectedIndex = 0;
            Hint.Text = "Ein Zielordner, den es noch nicht gibt, wird angelegt. Im Baum werden die verschobenen Elemente entfernt; liegt das Ziel im gescannten Bereich, wird es neu eingelesen.";
        }
        Loaded += (_, _) => { TargetBox.Focus(); TargetBox.CaretIndex = TargetBox.Text.Length; };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_zip)
        {
            var dir = Path.GetDirectoryName(TargetBox.Text.Trim());
            var dlg = new SaveFileDialog
            {
                Title = "ZIP-Datei",
                Filter = "ZIP-Archiv (*.zip)|*.zip",
                DefaultExt = ".zip",
                FileName = Path.GetFileName(TargetBox.Text.Trim()),
                InitialDirectory = dir != null && Directory.Exists(dir) ? dir : null,
            };
            if (dlg.ShowDialog(this) == true) TargetBox.Text = dlg.FileName;
        }
        else
        {
            var dlg = new OpenFolderDialog { Title = "Zielordner", InitialDirectory = Directory.Exists(TargetBox.Text.Trim()) ? TargetBox.Text.Trim() : null };
            if (dlg.ShowDialog(this) == true) TargetBox.Text = dlg.FolderName;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var text = TargetBox.Text.Trim().Trim('"');
        if (text.Length == 0 || !Path.IsPathFullyQualified(text))
        {
            Dialogs.Info(this, _zip ? "Bitte einen vollständigen Pfad für die ZIP-Datei angeben." : "Bitte einen vollständigen Zielpfad angeben.");
            return;
        }
        if (_zip)
        {
            if (!text.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) text += ".zip";
            if (Directory.Exists(text) ||
                (File.Exists(text) && !Dialogs.Confirm(this, $"{Path.GetFileName(text)} existiert schon und würde überschrieben.\n\nTrotzdem fortfahren?")))
                return;
        }
        Result = new TargetChoice(text, (PolicyBox.SelectedItem as PolicyOption)?.Policy ?? ConflictPolicy.Ask, _zip && RecycleBox.IsChecked == true);
        DialogResult = true;
    }
}
