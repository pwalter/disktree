using System.Windows;
using DiskTree.Features.Explorer;
using DiskTree.Features.Update;
using DiskTree.Infrastructure;
using DiskTree.Ui;

namespace DiskTree;

public partial class App : Application
{
    public static CliOptions Options { get; private set; } = CliOptions.Parse(Array.Empty<string>());

    private bool _showingError;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Installed first so that errors during startup are logged too.
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write("DispatcherUnhandledException", args.Exception);
            ShowError(args.Exception, canContinue: true);
            args.Handled = true; // keep working; the failed action is lost, the rest of the program is intact
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString());
            CrashLog.Write(args.IsTerminating ? "UnhandledException (Prozess endet)" : "UnhandledException", ex);
            if (args.IsTerminating) ShowError(ex, canContinue: false);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        Options = CliOptions.Parse(e.Args);

        if (Options.Help || Options.Error != null)
        {
            CliOptions.ShowHelp(Options.Error);
            Shutdown(Options.Error != null ? 2 : 0);
            return;
        }

        Portable.ForcedByFlag = Options.Portable;
        Updater.CleanupOld();
        ExplorerIntegration.RefreshIfStale();
        Theme.Init(AppSettings.Load().Theme);
        base.OnStartup(e);
    }

    /// <summary>A readable error box (German) pointing at crash.log. Never throws; never stacks up several boxes.</summary>
    private void ShowError(Exception ex, bool canContinue)
    {
        if (_showingError) return;
        _showingError = true;
        try
        {
            string text = "Es ist ein unerwarteter Fehler aufgetreten:\n\n" + ex.Message + "\n\n" +
                          (canContinue
                              ? "DiskTree arbeitet weiter; die zuletzt ausgeführte Aktion wurde aber möglicherweise nicht abgeschlossen."
                              : "DiskTree muss beendet werden.") +
                          "\n\nEinzelheiten stehen in der Datei:\n" + CrashLog.FilePath;
            MessageBox.Show(text, "DiskTree – Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* no window to show it in */ }
        finally { _showingError = false; }
    }
}
