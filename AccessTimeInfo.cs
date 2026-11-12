using Microsoft.Win32;

namespace DiskTree;

/// <summary>
/// Is the NTFS "last access" timestamp maintained on this machine? Windows 10/11 usually manages it itself and
/// switches it off (fsutil behavior query disablelastaccess). Read from the registry (readable without admin):
/// HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\NtfsDisableLastAccessUpdate, bit 0 = disabled,
/// bit 31 = managed by the system.
/// </summary>
public static class AccessTimeInfo
{
    public enum State { Unknown, Enabled, Disabled }

    private static State? _state;

    public static State Current => _state ??= Read();

    private static State Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            if (key?.GetValue("NtfsDisableLastAccessUpdate") is int v)
                return (v & 1) != 0 ? State.Disabled : State.Enabled;
        }
        catch
        {
            // not readable: unknown
        }
        return State.Unknown;
    }

    /// <summary>Warning text for "last opened", shown whenever that timestamp is selected.</summary>
    public static string Warning => Current switch
    {
        State.Disabled =>
            "Achtung: Windows aktualisiert das Zugriffsdatum auf diesem Computer nicht (NTFS „disablelastaccess“). " +
            "Die Werte sind veraltet und unzuverlässig, „seit Jahren nicht geöffnet“ sagt dann nichts aus.",
        State.Enabled =>
            "Hinweis: Das Zugriffsdatum wird von Windows nur verzögert (bis zu etwa einer Stunde) geschrieben, und Virenscanner, " +
            "Indexer oder Backups können es ändern. Nur als grober Anhaltspunkt verwenden.",
        _ =>
            "Achtung: Ob Windows das Zugriffsdatum aktualisiert, ließ sich nicht feststellen (fsutil behavior query disablelastaccess). " +
            "Die Werte können unzuverlässig sein.",
    };

    public static bool IsSevere => Current != State.Enabled;
}
