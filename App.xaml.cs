using System.Windows;

namespace DiskTree;

public partial class App : Application
{
    public static CliOptions Options { get; private set; } = CliOptions.Parse(Array.Empty<string>());

    protected override void OnStartup(StartupEventArgs e)
    {
        Options = CliOptions.Parse(e.Args);

        if (Options.Help || Options.Error != null)
        {
            CliOptions.ShowHelp(Options.Error);
            Shutdown(Options.Error != null ? 2 : 0);
            return;
        }

        Updater.CleanupOld();
        Theme.Init(AppSettings.Load().Theme);
        base.OnStartup(e);
    }
}
