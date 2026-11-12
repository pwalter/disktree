using DiskTree.Core;
using Xunit;

namespace DiskTree.Tests;

public class FileTypesTests
{
    private static string NameOf(string file) => FileTypes.All[FileTypes.CategoryOf(file)].Name;

    [Theory]
    [InlineData("report.docx", "Word-Dokumente")]
    [InlineData("REPORT.DOCX", "Word-Dokumente")]
    [InlineData("a.b.c.xlsx", "Excel-Tabellen")]
    [InlineData("deck.pptx", "PowerPoint")]
    [InlineData("scan.pdf", "PDF")]
    [InlineData("mail.pst", "E-Mail (Outlook)")]
    [InlineData("db.mdf", "MSSQL-Datenbank")]
    [InlineData("db.bak", "MSSQL-Datenbankbackup")]
    [InlineData("x.sqlite", "Weitere Datenbanken")]
    [InlineData("photo.JPG", "Bilder")]
    [InlineData("film.mkv", "Videos")]
    [InlineData("song.flac", "Audio")]
    [InlineData("pack.zip", "Archive")]
    [InlineData("disk.vhdx", "Datenträger-Abbilder")]
    [InlineData("setup.exe", "Programme & Bibliotheken")]
    [InlineData("Program.cs", "Entwicklung & Quellcode")]
    [InlineData("app.log", "Logs, Temp & Dumps")]
    [InlineData("notes.txt", "Text & Daten")]
    [InlineData("x.unknownext", "Sonstige")]
    [InlineData("noextension", "Sonstige")]
    [InlineData("trailingdot.", "Sonstige")]
    [InlineData(".gitignore", "Sonstige")]
    [InlineData("", "Sonstige")]
    public void CategoryOf_ByExtension(string file, string category) => Assert.Equal(category, NameOf(file));

    [Fact]
    public void CategoryOf_UsesLastExtensionOnly() => Assert.Equal("Archive", NameOf("backup.docx.zip"));

    [Fact]
    public void EveryExtensionMapsBackToItsOwnCategory()
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < FileTypes.All.Length; i++)
            foreach (var ext in FileTypes.All[i].Extensions)
            {
                Assert.False(seen.TryGetValue(ext, out var other), $".{ext} steht in '{other}' und '{FileTypes.All[i].Name}'");
                seen[ext] = FileTypes.All[i].Name;
                Assert.Equal(i, FileTypes.CategoryOf("file." + ext));
            }
    }

    [Fact]
    public void Layout_OtherIsLast_NamesUnique_ColorsUnique()
    {
        Assert.Equal(FileTypes.All.Length, FileTypes.Count);
        Assert.Equal(FileTypes.Count - 1, FileTypes.Other);
        Assert.Equal("Sonstige", FileTypes.All[FileTypes.Other].Name);
        Assert.Empty(FileTypes.All[FileTypes.Other].Extensions);
        Assert.Equal(FileTypes.Count, FileTypes.All.Select(c => c.Name).Distinct().Count());
        Assert.Equal(FileTypes.Count, FileTypes.All.Select(c => c.Color).Distinct().Count());
        Assert.Equal(18, FileTypes.Count);
    }

    [Fact]
    public void Pack_KeepsOnlyNonEmptyCategories()
    {
        var sizes = new long[FileTypes.Count];
        var counts = new long[FileTypes.Count];
        Assert.Null(FileTypes.Pack(sizes, counts));

        sizes[3] = 100; counts[3] = 2;
        sizes[FileTypes.Other] = 5; counts[FileTypes.Other] = 1;
        Assert.Equal(new[] { new TypeStat(3, 100, 2), new TypeStat(FileTypes.Other, 5, 1) }, FileTypes.Pack(sizes, counts));
    }

    [Fact]
    public void Without_DecrementsOrDropsTheCategory()
    {
        var stats = new[] { new TypeStat(3, 100, 2), new TypeStat(5, 7, 1) };
        Assert.Equal(new[] { new TypeStat(3, 60, 1), new TypeStat(5, 7, 1) }, FileTypes.Without(stats, 3, 40));
        Assert.Equal(new[] { new TypeStat(3, 100, 2) }, FileTypes.Without(stats, 5, 7));
        Assert.Null(FileTypes.Without(new[] { new TypeStat(5, 7, 1) }, 5, 7));
        Assert.Equal(stats, FileTypes.Without(stats, 9, 1)); // category not present: unchanged
        Assert.Null(FileTypes.Without(null, 3, 1));
        Assert.Equal(100, stats[0].Size); // input untouched
    }
}
