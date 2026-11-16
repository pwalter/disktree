namespace DiskTree.Core.Storage;

/// <summary>
/// POSIX mode bits (st_mode) as people read them: "drwxr-x---" like <c>ls -l</c> and octal "0750" like <c>chmod</c>.
/// </summary>
public static class PosixMode
{
    public const int TypeMask = 0xF000;     // S_IFMT (0170000)
    public const int Directory = 0x4000;    // S_IFDIR (0040000)
    public const int Regular = 0x8000;      // S_IFREG (0100000)
    public const int Link = 0xA000;         // S_IFLNK (0120000)
    public const int SetUid = 0x800;        // 04000
    public const int SetGid = 0x400;        // 02000
    public const int Sticky = 0x200;        // 01000

    /// <summary>"drwxr-x---" (type letter and nine permission letters, s/S/t/T for the special bits).</summary>
    public static string Format(int mode)
    {
        Span<char> c = stackalloc char[10];
        c[0] = (mode & TypeMask) switch
        {
            Directory => 'd',
            Link => 'l',
            0x2000 => 'c',
            0x6000 => 'b',
            0x1000 => 'p',
            0xC000 => 's',
            _ => '-',
        };
        Triple(c[1..4], mode >> 6, (mode & SetUid) != 0, 's');
        Triple(c[4..7], mode >> 3, (mode & SetGid) != 0, 's');
        Triple(c[7..10], mode, (mode & Sticky) != 0, 't');
        return new string(c);
    }

    private static void Triple(Span<char> c, int bits, bool special, char specialChar)
    {
        c[0] = (bits & 4) != 0 ? 'r' : '-';
        c[1] = (bits & 2) != 0 ? 'w' : '-';
        bool x = (bits & 1) != 0;
        c[2] = special ? (x ? specialChar : char.ToUpperInvariant(specialChar)) : x ? 'x' : '-';
    }

    /// <summary>The permission bits in octal as chmod takes them: "0750", "4755", "1777".</summary>
    public static string Octal(int mode) => Convert.ToString(mode & 0xFFF, 8).PadLeft(4, '0');

    /// <summary>"lesen, schreiben, ausführen" for one of the three groups (bits 4 = r, 2 = w, 1 = x); "keine Rechte" for none.</summary>
    public static string Words(int bits, bool directory)
    {
        var parts = new List<string>(3);
        if ((bits & 4) != 0) parts.Add(directory ? "auflisten" : "lesen");
        if ((bits & 2) != 0) parts.Add(directory ? "anlegen/löschen" : "schreiben");
        if ((bits & 1) != 0) parts.Add(directory ? "betreten" : "ausführen");
        return parts.Count == 0 ? "keine Rechte" : string.Join(", ", parts);
    }

    /// <summary>
    /// The rights of an entry as lines for the "Rechte (POSIX)" section: the <c>ls -l</c> line, then owner, group and
    /// others in words, then the special bits.
    /// </summary>
    public static string[] Describe(int mode, string owner, string group)
    {
        bool dir = (mode & TypeMask) == Directory;
        var lines = new List<string>
        {
            $"{Format(mode)}  {owner}:{group}  ({Octal(mode)})",
            $"Besitzer {owner}: {Words(mode >> 6 & 7, dir)}",
            $"Gruppe {group}: {Words(mode >> 3 & 7, dir)}",
            $"Andere: {Words(mode & 7, dir)}",
        };
        if ((mode & SetUid) != 0) lines.Add("setuid: läuft mit den Rechten des Besitzers");
        if ((mode & SetGid) != 0) lines.Add(dir ? "setgid: neue Einträge erben die Gruppe" : "setgid: läuft mit den Rechten der Gruppe");
        if ((mode & Sticky) != 0) lines.Add(dir ? "Sticky: nur der Besitzer eines Eintrags darf ihn löschen oder umbenennen" : "Sticky-Bit gesetzt");
        return lines.ToArray();
    }
}
