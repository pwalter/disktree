using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DiskTree;

// Self update: version / update button in the status bar (see Updater.cs).
public partial class MainWindow
{
    private UpdateInfo? _update;
    private bool _updating;

    private void InitUpdate()
    {
        AutoUpdateItem.IsChecked = _settings.AutoCheckUpdates;
        ShowVersion();

        // Look quietly in the background; a failure (offline, rate limit) is not worth a message.
        Loaded += async (_, _) =>
        {
            if (!_settings.AutoCheckUpdates || App.Options.NoUpdateCheck || Updater.Current == null || !Updater.CanSelfReplace) return;
            await CheckForUpdateAsync(manual: false);
        };
    }

    private void ShowVersion()
    {
        if (_update != null)
        {
            UpdateButton.Content = $"⬆ Update {_update.Version} verfügbar";
            UpdateButton.Background = (Brush)FindResource("Accent");
            UpdateButton.Foreground = Brushes.White;
            UpdateButton.ToolTip = "Klicken zum Herunterladen und Installieren";
        }
        else
        {
            UpdateButton.Content = $"Version {Updater.CurrentText}";
            UpdateButton.Background = Brushes.Transparent;
            UpdateButton.SetResourceReference(Control.ForegroundProperty, "Muted");
            UpdateButton.ToolTip = "Klicken: nach Updates suchen (Rechtsklick: Einstellungen)";
        }
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updating) return;
        UpdateInfo? latest;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            latest = await Updater.GetLatestAsync(cts.Token);
        }
        catch (Exception ex)
        {
            if (manual) Dialogs.Warn(this, "Die Suche nach Updates ist fehlgeschlagen:\n" + ex.Message);
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
            Dialogs.Info(this, $"DiskTree ist aktuell (Version {Updater.CurrentText}).");
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_update is not { } info || _updating) return;

        if (!Updater.CanSelfReplace)
        {
            OpenReleasePage(info);
            return;
        }
        if (Busy != null)
        {
            Dialogs.Info(this, "Bitte zuerst den laufenden Scan beenden oder abwarten.");
            return;
        }

        string size = info.AssetSize > 0 ? $" ({SizeFormat.Format(info.AssetSize)})" : "";
        var answer = Dialogs.Confirm(this,
            $"Version {info.Version} herunterladen{size} und installieren?\n\nDiskTree startet danach neu und scannt den aktuellen Pfad wieder.", title: "Update");
        if (!answer) return;

        _updating = true;
        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => UpdateButton.Content = $"Lade Update … {p * 100:0} %");
            UpdateButton.Content = "Lade Update …";
            var file = await Updater.DownloadAsync(info, progress, CancellationToken.None);

            UpdateButton.Content = "Installiere …";
            Updater.Install(file);
            _settings.Save();

            var path = NormalizePath(PathBox.Text);
            // No second question about administrator rights: the new process inherits the current one's rights anyway.
            var args = (Directory.Exists(path) ? App.Options.ForwardArgs(path) : App.Options.ForwardArgs()).Append("--no-admin-prompt");
            Updater.Restart(args);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            var open = Dialogs.Confirm(this,
                "Das Update konnte nicht installiert werden:\n" + ex.Message +
                "\n\nSoll die Download-Seite im Browser geöffnet werden?", MessageBoxImage.Warning);
            if (open) OpenReleasePage(info);
            UpdateButton.IsEnabled = true;
            _updating = false;
            ShowVersion();
        }
    }

    private static void OpenReleasePage(UpdateInfo? info) =>
        Process.Start(new ProcessStartInfo(info?.PageUrl ?? Updater.ReleasesPage) { UseShellExecute = true });

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_update != null) await InstallUpdateAsync();
        else await CheckForUpdateAsync(manual: true);
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(manual: true);

    private void AutoUpdate_Click(object sender, RoutedEventArgs e) => _settings.AutoCheckUpdates = AutoUpdateItem.IsChecked;

    private void OpenReleases_Click(object sender, RoutedEventArgs e) => OpenReleasePage(null);
}
