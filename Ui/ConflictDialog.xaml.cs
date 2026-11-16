using System.Windows;
using DiskTree.Features.FileOperations;

namespace DiskTree.Ui;

/// <summary>Per-file question while moving: skip, replace or keep both under a new name.</summary>
public partial class ConflictDialog : Window
{
    public ConflictPolicy Choice { get; private set; } = ConflictPolicy.Skip;
    public bool ForAll => AllBox.IsChecked == true;

    /// <param name="permanent">The source has no recycle bin (SFTP): "Ersetzen" deletes the existing entry for good.</param>
    public ConflictDialog(OpItem item, string target, bool permanent = false)
    {
        InitializeComponent();
        Message.Text = $"„{item.Name}“ gibt es im Ziel schon.";
        Detail.Text = permanent
            ? $"Ziel: {target}\n„Ersetzen“ löscht das vorhandene Element endgültig (die Quelle hat keinen Papierkorb)."
            : $"Ziel: {target}\n„Ersetzen“ legt die vorhandene Datei in den Papierkorb.";
    }

    private void Finish(ConflictPolicy c) { Choice = c; DialogResult = true; }
    private void Skip_Click(object sender, RoutedEventArgs e) => Finish(ConflictPolicy.Skip);
    private void Replace_Click(object sender, RoutedEventArgs e) => Finish(ConflictPolicy.Replace);
    private void Rename_Click(object sender, RoutedEventArgs e) => Finish(ConflictPolicy.Rename);
}
