using System.IO;

namespace DiskTree;

/// <summary>Size and file count of one file-type category inside a folder.</summary>
public readonly record struct TypeStat(int Cat, long Size, long Count);

/// <summary>Maps file extensions to document categories (Word, MSSQL backup, images, ...).</summary>
public static class FileTypes
{
    public sealed record Category(string Name, uint Color, string[] Extensions);

    // Order matters only for the colour/legend identity; "Sonstige" must stay last.
    public static readonly Category[] All =
    {
        new("Word-Dokumente", 0x2B579A, new[] { "doc", "docx", "docm", "dot", "dotx", "rtf", "odt" }),
        new("Excel-Tabellen", 0x217346, new[] { "xls", "xlsx", "xlsm", "xlsb", "xlt", "xltx", "ods" }),
        new("PowerPoint", 0xD24726, new[] { "ppt", "pptx", "pptm", "pps", "ppsx", "odp" }),
        new("PDF", 0xE5252A, new[] { "pdf" }),
        new("E-Mail (Outlook)", 0x1490A8, new[] { "pst", "ost", "msg", "eml", "nst" }),
        new("MSSQL-Datenbank", 0x6F42C1, new[] { "mdf", "ndf", "ldf" }),
        new("MSSQL-Datenbankbackup", 0xB07CD8, new[] { "bak", "trn", "bacpac", "dacpac" }),
        new("Weitere Datenbanken", 0x8E86A8, new[] { "sqlite", "sqlite3", "db", "mdb", "accdb", "sdf", "dbf", "edb", "ldb", "mdx" }),
        new("Bilder", 0xE8A317, new[] { "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "svg", "raw", "cr2", "nef", "arw", "dng", "psd", "ico" }),
        new("Videos", 0xD1457A, new[] { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "vob" }),
        new("Audio", 0xF0786A, new[] { "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus" }),
        new("Archive", 0x8B5E34, new[] { "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "cab", "zst", "lz" }),
        new("Datenträger-Abbilder", 0x5B6C7D, new[] { "iso", "img", "vhd", "vhdx", "vmdk", "vdi", "wim", "esd", "ova", "qcow2" }),
        new("Programme & Bibliotheken", 0x3F4A57, new[] { "exe", "dll", "sys", "msi", "msix", "msp", "msu", "appx", "ocx", "com", "scr", "drv", "cpl", "mui" }),
        new("Entwicklung & Quellcode", 0x2EA89B, new[] { "cs", "csproj", "sln", "js", "mjs", "ts", "tsx", "jsx", "py", "pyc", "java", "class", "jar", "c", "cpp", "h", "hpp", "go", "rs", "php", "rb", "json", "xml", "html", "htm", "css", "scss", "sql", "ps1", "bat", "cmd", "pdb", "obj", "o", "nupkg", "map", "resx", "xaml" }),
        new("Logs, Temp & Dumps", 0xA0A7B0, new[] { "log", "tmp", "temp", "etl", "dmp", "mdmp", "hdmp", "evtx", "old", "cache", "chk", "dat~" }),
        new("Text & Daten", 0x7FB069, new[] { "txt", "csv", "md", "ini", "cfg", "conf", "yaml", "yml", "toml", "tsv", "dat", "config", "properties" }),
        new("Sonstige", 0xC9CDD3, Array.Empty<string>()),
    };

    public static readonly int Count = All.Length;
    public static readonly int Other = Count - 1;

    private static readonly Dictionary<string, int> Map = Build();
    private static readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> Lookup =
        Map.GetAlternateLookup<ReadOnlySpan<char>>();

    private static Dictionary<string, int> Build()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < All.Length; i++)
            foreach (var ext in All[i].Extensions)
                map[ext] = i;
        return map;
    }

    /// <summary>Category index for a file name, decided by its extension (case-insensitive, no allocation).</summary>
    public static int CategoryOf(ReadOnlySpan<char> fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Length < 2) return Other;
        return Lookup.TryGetValue(ext[1..], out var cat) ? cat : Other;
    }

    /// <summary>Compact snapshot of the non-empty categories (for storing per folder).</summary>
    public static TypeStat[]? Pack(ReadOnlySpan<long> sizes, ReadOnlySpan<long> counts)
    {
        int n = 0;
        for (int i = 0; i < sizes.Length; i++)
            if (counts[i] != 0) n++;
        if (n == 0) return null;

        var result = new TypeStat[n];
        n = 0;
        for (int i = 0; i < sizes.Length; i++)
            if (counts[i] != 0)
                result[n++] = new TypeStat(i, sizes[i], counts[i]);
        return result;
    }

    /// <summary>Returns a copy of the stats with one file removed (after a deletion).</summary>
    public static TypeStat[]? Without(TypeStat[]? stats, int cat, long size)
    {
        if (stats == null) return null;
        var list = new List<TypeStat>(stats.Length);
        foreach (var s in stats)
        {
            if (s.Cat != cat) { list.Add(s); continue; }
            if (s.Count > 1) list.Add(s with { Size = Math.Max(0, s.Size - size), Count = s.Count - 1 });
        }
        return list.Count == 0 ? null : list.ToArray();
    }
}
