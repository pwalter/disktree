using System.Windows;

namespace DiskTree;

// Explorer context menu and portable mode switches (see ExplorerIntegration.cs, Portable.cs).
public partial class MainWindow
{
    private void UpdateMenu_Opened(object sender, RoutedEventArgs e)
    {
        ExplorerItem.IsEnabled = Updater.CanSelfReplace;
        ExplorerItem.IsChecked = ExplorerIntegration.IsEnabled;
        PortableItem.IsEnabled = Updater.CanSelfReplace;
        PortableItem.IsChecked = Portable.IsPortable;
    }

    private void ExplorerItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ExplorerItem.IsChecked) ExplorerIntegration.Enable();
            else ExplorerIntegration.Disable();
        }
        catch (Exception ex)
        {
            ExplorerItem.IsChecked = ExplorerIntegration.IsEnabled;
            MessageBox.Show(this, "Der Explorer-Eintrag konnte nicht geändert werden:\n" + ex.Message, "DiskTree",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PortableItem_Click(object sender, RoutedEventArgs e)
    {
        bool enable = PortableItem.IsChecked;
        try
        {
            Portable.SetEnabled(enable, _settings);
        }
        catch (Exception ex)
        {
            PortableItem.IsChecked = Portable.IsPortable;
            MessageBox.Show(this, "Der Portable-Modus konnte nicht umgeschaltet werden:\n" + ex.Message, "DiskTree",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MessageBox.Show(this, enable
            ? "Portable-Modus ist aktiv: Die Einstellungen liegen jetzt neben DiskTree.exe."
            : "Portable-Modus ist aus: Die Einstellungen liegen wieder in %AppData%\\DiskTree.",
            "DiskTree", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
