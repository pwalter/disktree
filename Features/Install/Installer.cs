using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiskTree.Features.Explorer;
using DiskTree.Features.Update;
using Microsoft.Win32;

namespace DiskTree.Features.Install;

/// <summary>Where an installation lives. Tests pass their own temp folders and registry key.</summary>
public sealed record InstallLayout(string InstallDir, string StartMenuDir, string UninstallKey)
{
    public const string ExeName = "DiskTree.exe";

    public static InstallLayout Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DiskTree"),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskTree");

    public string ExePath => Path.Combine(InstallDir, ExeName);
    public string ShortcutPath => Path.Combine(StartMenuDir, "DiskTree.lnk");
}

/// <summary>
/// "DiskTree.exe --install": copies the exe to %LocalAppData%\Programs\DiskTree, adds a start menu shortcut, an entry
/// in Settings &gt; Apps (with uninstall) and, unless --no-context-menu, the Explorer context menu.
/// Everything is per user (HKCU, own profile), so no administrator rights are needed; the self updater keeps working
/// because the folder is writable. "--uninstall" removes all of it again; the settings in %AppData%\DiskTree stay.
/// </summary>
public static class Installer
{
    public static bool IsInstalled(InstallLayout? layout = null) => File.Exists((layout ?? InstallLayout.Default).ExePath);

    /// <summary>True when the running exe is the installed copy.</summary>
    public static bool IsRunningInstalled(InstallLayout? layout = null) =>
        Environment.ProcessPath is { } p && SamePath(p, (layout ?? InstallLayout.Default).ExePath);

    /// <summary>Installs <paramref name="sourceExe"/> and returns a short report for the user. Throws on failure.</summary>
    public static string Install(string sourceExe, bool contextMenu, InstallLayout? layout = null)
    {
        var l = layout ?? InstallLayout.Default;

        if (!SamePath(sourceExe, l.ExePath))
        {
            Directory.CreateDirectory(l.InstallDir);
            try
            {
                File.Copy(sourceExe, l.ExePath, overwrite: true);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"{l.ExePath} kann nicht überschrieben werden – läuft DiskTree von dort noch? Bitte schließen und erneut versuchen.\n({ex.Message})", ex);
            }
        }

        Directory.CreateDirectory(l.StartMenuDir);
        CreateShortcut(l.ShortcutPath, l.ExePath, "Speicherplatz-Analyse");
        WriteUninstallEntry(l);
        if (contextMenu) ExplorerIntegration.Enable(l.ExePath);

        return "DiskTree wurde installiert:\n" +
               $"  Programm:        {l.ExePath}\n" +
               "  Startmenü:       DiskTree\n" +
               (contextMenu
                   ? "  Kontextmenü:     „Mit DiskTree analysieren“ bei Ordnern und Laufwerken\n" +
                     "                   (Windows 11: unter „Weitere Optionen anzeigen“)\n"
                   : "") +
               "  Deinstallieren:  Einstellungen > Apps oder DiskTree.exe --uninstall";
    }

    /// <summary>
    /// Removes shortcut, Apps entry, context menu (when it belongs to the installed exe) and the program files.
    /// When <paramref name="runningExe"/> is the installed exe itself, the files are deleted by a helper shortly after
    /// this process has ended. Returns a short report.
    /// </summary>
    public static string Uninstall(string? runningExe, InstallLayout? layout = null)
    {
        var l = layout ?? InstallLayout.Default;
        bool wasInstalled = File.Exists(l.ExePath);
        using (var k = Registry.CurrentUser.OpenSubKey(l.UninstallKey)) wasInstalled |= k != null;

        if (ExplorerIntegration.RegisteredExe() is { } reg && SamePath(reg, l.ExePath))
            ExplorerIntegration.Disable();

        TryDelete(l.ShortcutPath);
        Registry.CurrentUser.DeleteSubKeyTree(l.UninstallKey, throwOnMissingSubKey: false);

        var files = new[] { l.ExePath, l.ExePath + ".old" };
        if (runningExe != null && SamePath(runningExe, l.ExePath))
            DeleteAfterExit(files, l.InstallDir);
        else
        {
            foreach (var f in files) TryDelete(f);
            try { if (Directory.Exists(l.InstallDir) && !Directory.EnumerateFileSystemEntries(l.InstallDir).Any()) Directory.Delete(l.InstallDir); }
            catch { /* an empty folder left behind is harmless */ }
        }

        return wasInstalled
            ? "DiskTree wurde deinstalliert.\nDie Einstellungen in %AppData%\\DiskTree bleiben erhalten (bei Bedarf von Hand löschen)."
            : "DiskTree war nicht installiert; eventuelle Reste wurden entfernt.";
    }

    /// <summary>At start of the installed copy: keep the version shown in Settings &gt; Apps current after a self update.</summary>
    public static void RefreshIfInstalled()
    {
        try
        {
            if (!IsRunningInstalled()) return;
            using var k = Registry.CurrentUser.OpenSubKey(InstallLayout.Default.UninstallKey);
            if (k == null) return; // copied there by hand, not installed: leave it alone
            if (k.GetValue("DisplayVersion") as string != Updater.CurrentText) WriteUninstallEntry(InstallLayout.Default);
        }
        catch { /* the Apps entry is cosmetic */ }
    }

    private static void WriteUninstallEntry(InstallLayout l)
    {
        using var k = Registry.CurrentUser.CreateSubKey(l.UninstallKey);
        k.SetValue("DisplayName", "DiskTree");
        k.SetValue("DisplayVersion", Updater.CurrentText);
        k.SetValue("DisplayIcon", $"\"{l.ExePath}\"");
        k.SetValue("Publisher", "DiskTree");
        k.SetValue("URLInfoAbout", "https://github.com/pwalter/disktree");
        k.SetValue("InstallLocation", l.InstallDir);
        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        k.SetValue("UninstallString", $"\"{l.ExePath}\" --uninstall");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try { k.SetValue("EstimatedSize", (int)(new FileInfo(l.ExePath).Length / 1024), RegistryValueKind.DWord); }
        catch { /* size is optional */ }
    }

    /// <summary>A running exe cannot delete itself: a hidden cmd waits a moment, then removes the files and the folder.</summary>
    private static void DeleteAfterExit(IEnumerable<string> files, string dir)
    {
        var del = string.Join(" ", files.Select(f => $"\"{f}\""));
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c ping -n 3 127.0.0.1 >nul & del /f /q {del} 2>nul & rd \"{dir}\" 2>nul",
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(), // the folder to delete must not be the helper's current directory
        };
        Process.Start(psi);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch { /* best effort */ }
    }

    // ---- Start menu shortcut (IShellLink, no extra dependency) ----

    private static void CreateShortcut(string lnkPath, string target, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
