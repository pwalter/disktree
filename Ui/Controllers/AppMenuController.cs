using System.Windows;
using System.Windows.Controls;
using DiskTree.Features.Explorer;
using DiskTree.Features.Update;
using DiskTree.Infrastructure;

namespace DiskTree.Ui.Controllers;

/// <summary>
/// The Explorer context menu and portable mode switches in the version button's context menu
/// (see ExplorerIntegration.cs, Portable.cs). The update entries of the same menu belong to <see cref="UpdateController"/>.
/// </summary>
public sealed class AppMenuController
{
    private readonly Window _owner;
    private readonly AppSettings _settings;
    private readonly MenuItem _explorerItem;
    private readonly MenuItem _portableItem;

    public AppMenuController(Window owner, AppSettings settings, ContextMenu menu, MenuItem explorerItem, MenuItem portableItem)
    {
        _owner = owner;
        _settings = settings;
        _explorerItem = explorerItem;
        _portableItem = portableItem;

        menu.Opened += (_, _) => Refresh();
        _explorerItem.Click += (_, _) => ToggleExplorer();
        _portableItem.Click += (_, _) => TogglePortable();
    }

    private void Refresh()
    {
        _explorerItem.IsEnabled = Updater.CanSelfReplace;
        _explorerItem.IsChecked = ExplorerIntegration.IsEnabled;
        _portableItem.IsEnabled = Updater.CanSelfReplace;
        _portableItem.IsChecked = Portable.IsPortable;
    }

    private void ToggleExplorer()
    {
        try
        {
            if (_explorerItem.IsChecked) ExplorerIntegration.Enable();
            else ExplorerIntegration.Disable();
        }
        catch (Exception ex)
        {
            _explorerItem.IsChecked = ExplorerIntegration.IsEnabled;
            Dialogs.Warn(_owner, "Der Explorer-Eintrag konnte nicht geändert werden:\n" + ex.Message);
        }
    }

    private void TogglePortable()
    {
        bool enable = _portableItem.IsChecked;
        try
        {
            Portable.SetEnabled(enable, _settings);
        }
        catch (Exception ex)
        {
            _portableItem.IsChecked = Portable.IsPortable;
            Dialogs.Warn(_owner, "Der Portable-Modus konnte nicht umgeschaltet werden:\n" + ex.Message);
            return;
        }
        Dialogs.Info(_owner, enable
            ? "Portable-Modus ist aktiv: Die Einstellungen liegen jetzt neben DiskTree.exe."
            : "Portable-Modus ist aus: Die Einstellungen liegen wieder in %AppData%\\DiskTree.");
    }
}
