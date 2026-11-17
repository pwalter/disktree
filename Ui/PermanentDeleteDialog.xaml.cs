using System.Windows;
using DiskTree.Features.FileOperations;

namespace DiskTree.Ui;

/// <summary>
/// "Endgültig löschen – kann nicht rückgängig gemacht werden": the confirmation before deleting on a source without
/// recycle bin (SFTP). Shows what goes (count, size, examples, folder contents), where (server, folder). The default
/// button is "Abbrechen" (Enter and Esc cancel); only the red button confirms the plan (<see cref="PermanentDeletePlan.Confirm"/>).
/// </summary>
public partial class PermanentDeleteDialog : Window
{
    private readonly PermanentDeletePlan _plan;

    /// <param name="warning">An extra warning (e.g. all copies of a duplicate group are selected), or null.</param>
    public PermanentDeleteDialog(PermanentDeletePlan plan, string? warning = null)
    {
        InitializeComponent();
        _plan = plan;
        Headline.Text = PermanentDeletePlan.Title;
        Details.Text = plan.Describe();
        if (warning != null)
        {
            Warning.Text = warning;
            Warning.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        _plan.Confirm();
        DialogResult = true;
    }
}
