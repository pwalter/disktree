using System.IO;
using System.Windows;
using System.Windows.Controls;
using DiskTree.Core;
using DiskTree.Infrastructure;

namespace DiskTree.Ui.Controllers;

/// <summary>
/// Administrator rights: the "Als Administrator neu starten" button and the question / automatic restart when the
/// window has loaded. Without admin rights protected folders are skipped; declining is fine, the scan still works.
/// </summary>
public sealed class AdminStartup
{
    private readonly Window _window;
    private readonly Func<string> _pathText;

    /// <param name="pathText">Text of the path box: handed over so the elevated instance scans it right away.</param>
    public AdminStartup(Window window, Button button, Func<string> pathText)
    {
        _window = window;
        _pathText = pathText;

        if (AdminHelper.IsAdmin)
        {
            _window.Title = "DiskTree (Administrator)";
            button.Visibility = Visibility.Collapsed;
        }
        button.Click += (_, _) => Restart();
    }

    private void Restart()
    {
        var args = PathList.Parse(_pathText()).Where(Directory.Exists).ToArray();

        if (AdminHelper.RestartElevated(args))
            Application.Current.Shutdown();
    }

    /// <summary>
    /// Called once after the window loaded. Returns true if the program is being replaced by an elevated instance
    /// (the caller must not start anything then).
    /// </summary>
    public bool ElevateIfWanted()
    {
        var cli = App.Options;
        if (AdminHelper.IsAdmin) return false;

        if (cli.Admin)
        {
            // --admin: restart right away, no question. If UAC is declined the program just carries on.
            if (AdminHelper.RestartElevated(cli.ForwardArgs()))
            {
                Application.Current.Shutdown();
                return true;
            }
        }
        else if (!cli.NoAdminPrompt && !AdminHelper.PromptSuppressed)
        {
            if (AdminHelper.Ask(_window, out var never))
            {
                if (AdminHelper.RestartElevated(cli.ForwardArgs()))
                {
                    Application.Current.Shutdown();
                    return true;
                }
            }
            else if (never)
            {
                AdminHelper.SuppressPrompt();
            }
        }
        return false;
    }
}
