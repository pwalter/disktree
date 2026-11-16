using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using DiskTree.Ui;

namespace DiskTree.Infrastructure;

public static class AdminHelper
{
    private static string SuppressFile => Path.Combine(Portable.DataDir, "no-admin-prompt");

    // The rights of a process do not change while it runs: ask Windows once.
    private static readonly Lazy<bool> IsAdminCached = new(() =>
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    });

    public static bool IsAdmin => IsAdminCached.Value;

    public static bool PromptSuppressed => File.Exists(SuppressFile);

    public static void SuppressPrompt()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SuppressFile)!);
            File.WriteAllText(SuppressFile, "");
        }
        catch { /* not being able to remember the choice is harmless */ }
    }

    /// <summary>
    /// Starts this program again with administrator rights (UAC prompt). Returns false when the user
    /// declined the UAC prompt, in which case the current instance simply keeps running.
    /// </summary>
    public static bool RestartElevated(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }

    /// <summary>Asks whether to restart as administrator. <paramref name="dontAskAgain"/> is only meaningful for "No".</summary>
    public static bool Ask(Window owner, out bool dontAskAgain) =>
        Dialogs.AskAtStart(owner,
            "Als Administrator neu starten?",
            "Ohne Administratorrechte kann DiskTree geschützte Ordner (z. B. „System Volume Information“ oder " +
            "Ordner anderer Benutzer) nicht lesen. Die Größen auf einem Laufwerk sind dann etwas zu klein.\n\n" +
            "Du kannst auch ohne Administratorrechte weiterarbeiten.",
            "Als Administrator neu starten", "Ohne Administratorrechte fortfahren", out dontAskAgain);
}
