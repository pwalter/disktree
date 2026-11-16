using System.Diagnostics;
using System.IO;
using System.Windows;
using DiskTree.Features.Install;
using DiskTree.Features.Update;
using DiskTree.Infrastructure;

namespace DiskTree.Ui.Controllers;

/// <summary>
/// Installation from the window: the question at start (before the administrator question) and the
/// "installieren / deinstallieren" entry in the version button's menu. The work itself is done by <see cref="Installer"/>.
/// </summary>
public static class InstallStartup
{
    private static string SuppressFile => Path.Combine(Portable.DataDir, "no-install-prompt");

    /// <summary>Running as a standalone copy that could be installed (not installed, not portable, own exe, not elevated).</summary>
    public static bool CanInstall =>
        Updater.CanSelfReplace
        && !Installer.IsRunningInstalled()
        && !Portable.IsPortable
        && !AdminHelper.IsAdmin; // an elevated instance may belong to another account: its HKCU is the wrong one

    private static bool ShouldAsk =>
        CanInstall && !Installer.IsInstalled() && !App.Options.NoInstallPrompt && !File.Exists(SuppressFile);

    /// <summary>
    /// Called once after the window loaded. Returns true when the program is being replaced by the installed copy
    /// (the caller must not start anything then).
    /// </summary>
    public static bool InstallIfWanted(Window owner, Action saveSettings)
    {
        if (!ShouldAsk) return false;

        bool yes = Dialogs.AskAtStart(owner,
            "DiskTree installieren?",
            "DiskTree läuft gerade ohne Installation. Installiert bekommst du:\n\n" +
            "•  einen Eintrag im Startmenü\n" +
            "•  „Mit DiskTree analysieren“ im Rechtsklick-Menü von Ordnern und Laufwerken\n" +
            "•  einen Eintrag unter Einstellungen > Apps (dort auch wieder deinstallierbar)\n\n" +
            "Die Installation gilt nur für dich und braucht keine Administratorrechte. " +
            "Du kannst DiskTree auch weiter ohne Installation benutzen.",
            "Installieren", "Ohne Installation fortfahren", out var never);

        if (!yes)
        {
            if (never) Suppress();
            return false;
        }
        return Install(owner, saveSettings, App.Options.ForwardArgs());
    }

    /// <summary>Installs, then starts the installed copy with <paramref name="args"/> and closes this one. False on failure.</summary>
    public static bool Install(Window owner, Action saveSettings, IEnumerable<string> args)
    {
        try
        {
            Installer.Install(Environment.ProcessPath!, contextMenu: true);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Install", ex);
            Dialogs.Warn(owner, "Die Installation ist fehlgeschlagen:\n" + ex.Message);
            return false;
        }

        saveSettings(); // the installed copy reads them at its start, before this one closes
        try
        {
            var psi = new ProcessStartInfo(InstallLayout.Default.ExePath) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Dialogs.Info(owner, "DiskTree ist installiert, konnte aber nicht neu gestartet werden:\n" + ex.Message +
                                "\n\nDu findest es im Startmenü.");
            return false;
        }
        Application.Current.Shutdown();
        return true;
    }

    /// <summary>Uninstall from the menu of the installed copy: asks, removes everything and closes the program.</summary>
    public static void Uninstall(Window owner)
    {
        if (!Dialogs.Confirm(owner,
                "DiskTree deinstallieren?\n\nStartmenü-Eintrag, Kontextmenü und Programmdatei werden entfernt, " +
                "DiskTree wird danach beendet. Die Einstellungen bleiben erhalten."))
            return;
        try
        {
            Dialogs.Info(owner, Installer.Uninstall(Environment.ProcessPath));
        }
        catch (Exception ex)
        {
            CrashLog.Write("Uninstall", ex);
            Dialogs.Warn(owner, "Die Deinstallation ist fehlgeschlagen:\n" + ex.Message);
            return;
        }
        Application.Current.Shutdown();
    }

    private static void Suppress()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SuppressFile)!);
            File.WriteAllText(SuppressFile, "");
        }
        catch { /* not being able to remember the choice is harmless */ }
    }
}
