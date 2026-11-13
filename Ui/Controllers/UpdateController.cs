using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskTree.Core;
using DiskTree.Features.Update;
using DiskTree.Infrastructure;

namespace DiskTree.Ui.Controllers;

/// <summary>
/// Self update: the version / update button in the status bar and its context menu (see Updater.cs).
/// Wires its own events; the window only has to create it.
/// </summary>
public sealed class UpdateController
{
    private readonly Window _owner;
    private readonly AppSettings _settings;
    private readonly Button _button;
    private readonly MenuItem _autoItem;
    private readonly Func<string> _pathText;
    private readonly Func<bool> _scanRunning;
    private readonly Action _saveSettings;

    private UpdateInfo? _update;

    /// <summary>True while a download / installation runs: no tab changes then.</summary>
    public bool IsUpdating { get; private set; }

    /// <param name="pathText">Text of the path box: the new process scans it again after the restart.</param>
    /// <param name="scanRunning">Whether any scan runs (the update has to wait for it).</param>
    /// <param name="saveSettings">Writes the settings (window size, last path ...): the new process reads them before this one closes.</param>
    public UpdateController(Window owner, AppSettings settings, Button button, MenuItem autoItem,
        MenuItem checkItem, MenuItem releasesItem, Func<string> pathText, Func<bool> scanRunning, Action saveSettings)
    {
        _owner = owner;
        _settings = settings;
        _button = button;
        _autoItem = autoItem;
        _pathText = pathText;
        _scanRunning = scanRunning;
        _saveSettings = saveSettings;

        _autoItem.IsChecked = _settings.AutoCheckUpdates;
        ShowVersion();

        _button.Click += async (_, _) =>
        {
            if (_update != null) await InstallUpdateAsync();
            else await CheckForUpdateAsync(manual: true);
        };
        checkItem.Click += async (_, _) => await CheckForUpdateAsync(manual: true);
        _autoItem.Click += (_, _) => _settings.AutoCheckUpdates = _autoItem.IsChecked;
        releasesItem.Click += (_, _) => OpenReleasePage(null);

        // Look quietly in the background; a failure (offline, rate limit) is not worth a message.
        _owner.Loaded += async (_, _) =>
        {
            if (!_settings.AutoCheckUpdates || App.Options.NoUpdateCheck || Updater.Current == null || !Updater.CanSelfReplace) return;
            await CheckForUpdateAsync(manual: false);
        };
    }

    private void ShowVersion()
    {
        if (_update != null)
        {
            _button.Content = $"⬆ Update {_update.Version} verfügbar";
            _button.Background = (Brush)_owner.FindResource("Accent");
            _button.Foreground = Brushes.White;
            _button.ToolTip = "Klicken zum Herunterladen und Installieren";
        }
        else
        {
            _button.Content = $"Version {Updater.CurrentText}";
            _button.Background = Brushes.Transparent;
            _button.SetResourceReference(Control.ForegroundProperty, "Muted");
            _button.ToolTip = "Klicken: nach Updates suchen (Rechtsklick: Einstellungen)";
        }
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (IsUpdating) return;
        UpdateInfo? latest;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            latest = await Updater.GetLatestAsync(cts.Token);
        }
        catch (Exception ex)
        {
            if (manual) Dialogs.Warn(_owner, "Die Suche nach Updates ist fehlgeschlagen:\n" + ex.Message);
            return;
        }

        if (latest != null && Updater.IsNewer(latest))
        {
            _update = latest;
            ShowVersion();
            if (manual) await InstallUpdateAsync();
        }
        else if (manual)
        {
            Dialogs.Info(_owner, $"DiskTree ist aktuell (Version {Updater.CurrentText}).");
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_update is not { } info || IsUpdating) return;

        if (!Updater.CanSelfReplace)
        {
            OpenReleasePage(info);
            return;
        }
        if (_scanRunning())
        {
            Dialogs.Info(_owner, "Bitte zuerst den laufenden Scan beenden oder abwarten.");
            return;
        }

        string size = info.AssetSize > 0 ? $" ({SizeFormat.Format(info.AssetSize)})" : "";
        var answer = Dialogs.Confirm(_owner,
            $"Version {info.Version} herunterladen{size} und installieren?\n\nDiskTree startet danach neu und scannt den aktuellen Pfad wieder.", title: "Update");
        if (!answer) return;

        IsUpdating = true;
        _button.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => _button.Content = $"Lade Update … {p * 100:0} %");
            _button.Content = "Lade Update …";
            var file = await Updater.DownloadAsync(info, progress, CancellationToken.None);

            _button.Content = "Installiere …";
            Updater.Install(file);
            _saveSettings();

            var path = PathList.Normalize(_pathText());
            // No second question about administrator rights: the new process inherits the current one's rights anyway.
            var args = (Directory.Exists(path) ? App.Options.ForwardArgs(path) : App.Options.ForwardArgs()).Append("--no-admin-prompt");
            Updater.Restart(args);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            var open = Dialogs.Confirm(_owner,
                "Das Update konnte nicht installiert werden:\n" + ex.Message +
                "\n\nSoll die Download-Seite im Browser geöffnet werden?", MessageBoxImage.Warning);
            if (open) OpenReleasePage(info);
            _button.IsEnabled = true;
            IsUpdating = false;
            ShowVersion();
        }
    }

    private static void OpenReleasePage(UpdateInfo? info) =>
        Process.Start(new ProcessStartInfo(info?.PageUrl ?? Updater.ReleasesPage) { UseShellExecute = true });
}
