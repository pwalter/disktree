using System.Windows;
using System.Windows.Controls;
using DiskTree.Core;
using DiskTree.Features.Explorer;
using DiskTree.Features.Install;
using DiskTree.Features.Update;
using DiskTree.Infrastructure;

namespace DiskTree.Ui.Controllers;

/// <summary>
/// Install / uninstall, the Explorer context menu and portable mode switches in the version button's context menu
/// (see ExplorerIntegration.cs, Portable.cs). The update entries of the same menu belong to <see cref="UpdateController"/>.
/// </summary>
public sealed class AppMenuController
{
    private readonly Window _owner;
    private readonly AppSettings _settings;
    private readonly MenuItem _explorerItem;
    private readonly MenuItem _portableItem;
    private readonly MenuItem _installItem;
    private readonly Func<string> _pathText;
    private readonly Action _saveSettings;

    public AppMenuController(Window owner, AppSettings settings, ContextMenu menu, MenuItem installItem, MenuItem explorerItem,
        MenuItem portableItem, Func<string> pathText, Action saveSettings)
    {
        _owner = owner;
        _settings = settings;
        _installItem = installItem;
        _explorerItem = explorerItem;
        _portableItem = portableItem;
        _pathText = pathText;
        _saveSettings = saveSettings;

        menu.Opened += (_, _) => Refresh();
        _explorerItem.Click += (_, _) => ToggleExplorer();
        _portableItem.Click += (_, _) => TogglePortable();
        _installItem.Click += (_, _) => InstallOrUninstall();
    }

    private void Refresh()
    {
        bool installed = Installer.IsRunningInstalled();
        _installItem.Header = installed ? "DiskTree deinstallieren …" : "DiskTree installieren …";
        _installItem.Visibility = installed || InstallStartup.CanInstall ? Visibility.Visible : Visibility.Collapsed;
        _explorerItem.IsEnabled = Updater.CanSelfReplace;
        _explorerItem.IsChecked = ExplorerIntegration.IsEnabled;
        _portableItem.IsEnabled = Updater.CanSelfReplace;
        _portableItem.IsChecked = Portable.IsPortable;
    }

    private void InstallOrUninstall()
    {
        if (Installer.IsRunningInstalled())
        {
            InstallStartup.Uninstall(_owner);
            return;
        }
        if (!Dialogs.Confirm(_owner,
                "DiskTree installieren?\n\nDiskTree wird nach %LocalAppData%\\Programs\\DiskTree kopiert, bekommt einen " +
                "Startmenü-Eintrag, das Explorer-Kontextmenü „Mit DiskTree analysieren“ und einen Eintrag unter " +
                "Einstellungen > Apps. Danach startet die installierte Version, ein laufender Scan wird dabei abgebrochen."))
            return;
        var args = PathList.Parse(_pathText()).Where(System.IO.Directory.Exists).ToArray();
        InstallStartup.Install(_owner, _saveSettings, args);
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
