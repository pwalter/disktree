using System.Windows;

namespace DiskTree;

/// <summary>Per-file question while moving: skip, replace or keep both under a new name.</summary>
public partial class ConflictDialog : Window
{
    public ConflictPolicy Choice { get; private set; } = ConflictPolicy.Skip;
    public bool ForAll => AllBox.IsChecked == true;

    public ConflictDialog(OpItem item, string target)
    {
        InitializeComponent();
        Message.Text = $"„{item.Name}“ gibt es im Ziel schon.";
        Detail.Text = $"Ziel: {target}\n„Ersetzen“ legt die vorhandene Datei in den Papierkorb.";
    }

    private void Finish(ConflictPolicy c) { Choice = c; DialogResult = true; }
    private void Skip_Click(object sender, RoutedEventArgs e) => Finish(ConflictPolicy.Skip);
    private void Replace_Click(object sender, RoutedEventArgs e) => Finish(ConflictPolicy.Replace);
    private void Rename_Click(object sender, RoutedEventArgs e) => Finish(ConflictPolicy.Rename);
}
