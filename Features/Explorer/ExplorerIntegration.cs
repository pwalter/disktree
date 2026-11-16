using DiskTree.Features.Update;
using Microsoft.Win32;

namespace DiskTree.Features.Explorer;

/// <summary>
/// Explorer context menu "Mit DiskTree analysieren" for folders, drives and the background of an open folder.
/// Lives in HKCU\Software\Classes, so no administrator rights are needed. On Windows 11 the classic entries
/// appear under "Weitere Optionen anzeigen".
/// </summary>
public static class ExplorerIntegration
{
    private const string Verb = "DiskTree";
    private const string Title = "Mit DiskTree analysieren";

    // (key below HKCU\Software\Classes, placeholder for the path)
    private static readonly (string Key, string Arg)[] Targets =
    {
        (@"Directory\shell", "%1"),
        (@"Directory\Background\shell", "%V"),
        (@"Drive\shell", "%1"),
    };

    private static string? Exe => Environment.ProcessPath;

    private static string Command(string exe, string arg) => $"\"{exe}\" \"{arg}\"";

    private static string KeyPath(string target) => $@"Software\Classes\{target}\{Verb}";

    private static string? CurrentCommand()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath(Targets[0].Key) + @"\command");
        return k?.GetValue(null) as string;
    }

    public static bool IsEnabled
    {
        get
        {
            try { return CurrentCommand() != null; }
            catch { return false; }
        }
    }

    /// <summary>The exe the entry currently starts, or null when there is no entry.</summary>
    public static string? RegisteredExe()
    {
        try
        {
            // Written by Command(): "C:\…\DiskTree.exe" "%1"
            if (CurrentCommand() is not { } cmd || !cmd.StartsWith('"')) return null;
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
        catch { return null; }
    }

    /// <summary>Registers the entry for <paramref name="exePath"/> (default: the running exe).</summary>
    public static void Enable(string? exePath = null)
    {
        var exe = exePath ?? Exe ?? throw new InvalidOperationException("Pfad der Programmdatei unbekannt.");
        foreach (var (key, arg) in Targets)
        {
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath(key));
            k.SetValue(null, Title);
            k.SetValue("Icon", $"\"{exe}\"");
            using var c = k.CreateSubKey("command");
            c.SetValue(null, Command(exe, arg));
        }
    }

    public static void Disable()
    {
        foreach (var (key, _) in Targets)
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath(key), throwOnMissingSubKey: false);
    }

    /// <summary>
    /// At start: when the entry is active but its exe no longer exists (moved or deleted), point it here. An entry for
    /// another exe that still exists (e.g. the installed copy while a downloaded one is started) is left alone.
    /// </summary>
    public static void RefreshIfStale()
    {
        try
        {
            if (Exe is not { } exe || !Updater.CanSelfReplace) return;
            if (CurrentCommand() is { } cmd && cmd != Command(exe, Targets[0].Arg)
                && (RegisteredExe() is not { } reg || !System.IO.File.Exists(reg)))
                Enable();
        }
        catch { /* the entry is a convenience only */ }
    }
}
