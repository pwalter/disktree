using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace DiskTree.Tests;

public class ReportTests : TestBase
{
    private const int MB = 1 << 20;

    private async Task<Scanner> SampleScan()
    {
        T.File(@"Daten\Berichte\q1.docx", 300_000, Ago(years: 3));
        T.File(@"Daten\Berichte\q1.pdf", 2 * MB, Ago(days: 3));
        T.File(@"Daten\Medien\film.mp4", 8 * MB, Ago(months: 5));
        T.File(@"Daten\Medien\song.mp3", 3 * MB, Ago(years: 7));
        T.File(@"Daten\log.txt", 1000);
        T.Dir(@"Daten\leer");
        return await Scan.Run(T.Sub("Daten"));
    }

    // ------------------------------------------------------------------ ReportBuilder

    [Fact]
    public async Task Build_CollectsTotalsTypesAgesFoldersAndTopFiles()
    {
        var s = await SampleScan();
        var d = ReportBuilder.Build(s, interim: false);

        long total = 300_000 + 2L * MB + 8L * MB + 3L * MB + 1000;
        Assert.Equal(T.Sub("Daten"), d.Title, ignoreCase: true);
        Assert.False(d.Interim);
        Assert.Equal(total, d.TotalSize);
        Assert.Equal(5, d.Files);
        Assert.Equal(3, d.Dirs);
        Assert.Equal(0, d.Excluded);
        Assert.True(d.PartialDrive);
        Assert.True(d.DriveTotal > 0 && d.DriveFree >= 0);
        Assert.Equal(s.Root!.Name, d.DriveName);

        // types sorted by size, only non-empty ones
        Assert.Equal(new[] { "Videos", "Audio", "PDF", "Word-Dokumente", "Text & Daten" }, d.Types.Select(t => t.Name).ToArray());
        Assert.Equal(8L * MB, d.Types[0].Size);
        Assert.Equal(total, d.Types.Sum(t => t.Size));
        Assert.Equal(5, d.Types.Sum(t => t.Count));

        // all five buckets present, in order
        Assert.Equal(AgeBuckets.Names, d.Ages.Select(a => a.Name).ToArray());
        Assert.Equal(total, d.Ages.Sum(a => a.Size));
        Assert.Equal(2L * MB + 1000, d.Ages[0].Size);
        Assert.Equal(8L * MB, d.Ages[1].Size);
        Assert.Equal(300_000, d.Ages[3].Size);
        Assert.Equal(3L * MB, d.Ages[4].Size);

        // folders: biggest first, never the start folder itself, empty ones left out
        Assert.Equal(new[] { "Medien", "Berichte" }, d.Folders.Select(f => Path.GetFileName(f.Path)).ToArray());
        Assert.Equal(11L * MB, d.Folders[0].Size);
        Assert.Equal(2, d.Folders[0].Files);

        Assert.Equal(new[] { 8L * MB, 3L * MB, 2L * MB }, d.TopFiles.Select(f => f.Size).ToArray());
    }

    [Fact]
    public async Task Build_InterimFlagAndMultiTitle()
    {
        T.File(@"one\a.txt", 1);
        T.File(@"two\b.txt", 2);
        var s = new Scanner();
        await s.ScanManyAsync(new[] { T.Sub("one"), T.Sub("two") }, 2, CancellationToken.None);

        var d = ReportBuilder.Build(s, interim: true);
        Assert.True(d.Interim);
        Assert.Equal(T.Sub("one") + "; " + T.Sub("two"), d.Title, ignoreCase: true);
        Assert.Equal(3, d.TotalSize);
        Assert.Equal(0, d.DriveTotal); // multi scan: no drive information
        Assert.Equal("", d.DriveName);
    }

    [Fact]
    public async Task Build_LimitsFolderList()
    {
        for (int i = 0; i < ReportData.FolderCount + 10; i++) T.File($"d{i:D2}\\f.bin", 100 + i);
        var s = await ScanT();
        var d = ReportBuilder.Build(s, false);
        Assert.Equal(ReportData.FolderCount, d.Folders.Count);
        Assert.Equal(100 + ReportData.FolderCount + 9, d.Folders[0].Size);
        Assert.Equal(d.Folders.Select(f => f.Size).OrderByDescending(x => x), d.Folders.Select(f => f.Size));
    }

    [Fact]
    public void Build_WithoutScan_Throws() =>
        Assert.Throws<InvalidOperationException>(() => ReportBuilder.Build(new Scanner(), false));

    [Fact]
    public void Percent_AndFileName()
    {
        Assert.Equal("50 %", ReportData.Percent(1, 2));
        Assert.Equal("33,3 %", ReportData.Percent(1, 3));
        Assert.Equal("0 %", ReportData.Percent(5, 0));
        Assert.Matches(@"^DiskTree-Bericht-\d{8}-\d{4}\.pdf$", ReportBuilder.DefaultFileName("pdf"));
    }

    // ------------------------------------------------------------------ HTML

    [Fact]
    public async Task Html_ContainsTheNumbers()
    {
        var s = await SampleScan();
        var d = ReportBuilder.Build(s, false);
        var html = ReportHtml.Render(d);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("Speicherplatz-Bericht", html);
        Assert.Contains(NodeVM.FormatSize(d.TotalSize), html);          // total
        Assert.Contains("Dateien", html);
        Assert.Contains("Medien", html);                                  // folder rows
        Assert.Contains(NodeVM.FormatSize(11L * MB), html);
        Assert.Contains("Videos", html);                                  // type rows
        Assert.Contains("Word-Dokumente", html);
        Assert.Contains("Unter 1 Monat", html);                           // age rows
        Assert.Contains(System.Net.WebUtility.HtmlEncode("Über 5 Jahre"), html);
        Assert.Contains(@"film.mp4", html);                               // top files
        Assert.Contains("Laufwerk " + d.DriveName, html);
        Assert.Contains("Gescannt wurde nur ein Teil des Laufwerks", html);
        Assert.DoesNotContain("Zwischenstand", html);
        Assert.DoesNotContain("http://", html.Replace("http://www.w3.org/2000/svg", "")); // self-contained: no external resources
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void Html_EncodesPathsAndShowsInterimNote()
    {
        var d = new ReportData
        {
            Title = @"C:\<b>x</b> & ""q""",
            Created = new DateTime(2026, 1, 2, 3, 4, 0),
            Interim = true,
            TotalSize = 5,
            Folders = new[] { new ReportData.FolderRow(@"C:\<i>evil</i>", 5, 5, 1) },
            TopFiles = new[] { new Scanner.BigFile(@"C:\<u>f</u>.bin", 2 * MB) },
        };
        var html = ReportHtml.Render(d);

        Assert.Contains("Zwischenstand", html);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>x</b>", html);
        Assert.DoesNotContain("<i>evil</i>", html);
        Assert.DoesNotContain("<u>f</u>", html);
        Assert.Contains("02.01.2026 03:04", html);
    }

    [Fact]
    public void Html_EmptyReport_SaysSo()
    {
        var html = ReportHtml.Render(new ReportData { Title = "leer" });
        Assert.Contains("Keine Unterordner gefunden.", html);
        Assert.Contains("Keine Dateien gefunden.", html);
        Assert.Contains("Keine Altersdaten vorhanden.", html);
        Assert.Contains("Keine Dateien ab 1 MB gefunden.", html);
    }

    [Fact]
    public async Task Html_Write_CreatesUtf8FileWithoutBom()
    {
        var s = await SampleScan();
        var file = Path.Combine(Path.GetTempPath(), "disktree-report-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            ReportHtml.Write(file, ReportBuilder.Build(s, false));
            var bytes = File.ReadAllBytes(file);
            Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
            Assert.Contains(System.Net.WebUtility.HtmlEncode("Über 5 Jahre"), Encoding.UTF8.GetString(bytes));
        }
        finally { File.Delete(file); }
    }

    // ------------------------------------------------------------------ PDF

    private static int PageCount(byte[] pdf)
    {
        // PDFsharp writes page dictionaries as "/Type /Page"; the page tree is "/Type /Pages".
        var text = Encoding.Latin1.GetString(pdf);
        return Regex.Matches(text, @"/Type\s*/Page(?![s\w])").Count;
    }

    [Fact]
    public async Task Pdf_Write_CreatesAValidLookingFile()
    {
        var s = await SampleScan();
        using var outDir = new TempDir();
        var file = Path.Combine(outDir.Path, "bericht.pdf");

        ReportPdf.Write(file, ReportBuilder.Build(s, false));

        var bytes = File.ReadAllBytes(file);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.True(bytes.Length > 3000);
        Assert.Contains("%%EOF", Encoding.Latin1.GetString(bytes, Math.Max(0, bytes.Length - 20), Math.Min(20, bytes.Length)));
        Assert.True(PageCount(bytes) >= 1);
    }

    [Fact]
    public void Pdf_LargeReport_NeedsSeveralPages()
    {
        var top = Enumerable.Range(0, 400).Select(i => new Scanner.BigFile($@"C:\Daten\Ordner{i % 7}\Datei-{i}.bin", 50L * MB - i)).ToList();
        var folders = Enumerable.Range(0, 20).Select(i => new ReportData.FolderRow($@"C:\Daten\Ordner{i}", 1000L * MB - i, 5, 10)).ToList();
        var small = new ReportData { Title = @"C:\Daten", Created = DateTime.Now, TotalSize = 1, Folders = folders };
        var big = new ReportData { Title = @"C:\Daten", Created = DateTime.Now, TotalSize = 1, Folders = folders, TopFiles = top };

        using var outDir = new TempDir();
        var f1 = Path.Combine(outDir.Path, "small.pdf");
        var f2 = Path.Combine(outDir.Path, "big.pdf");
        ReportPdf.Write(f1, small);
        ReportPdf.Write(f2, big);

        int pagesSmall = PageCount(File.ReadAllBytes(f1));
        int pagesBig = PageCount(File.ReadAllBytes(f2));
        Assert.True(pagesBig >= 5, $"400 Dateien sollten mehrere Seiten füllen, gezählt: {pagesBig}");
        Assert.True(pagesBig > pagesSmall);
    }

    [Fact]
    public void Pdf_EmptyAndUnusualData_DoesNotThrow()
    {
        using var outDir = new TempDir();
        ReportPdf.Write(Path.Combine(outDir.Path, "empty.pdf"), new ReportData { Title = "" });
        ReportPdf.Write(Path.Combine(outDir.Path, "odd.pdf"), new ReportData
        {
            Title = new string('x', 500) + @" <&> ä ö ü ß 日本",
            Interim = true,
            DriveTotal = 1000, DriveFree = 5000, // free > total must not crash
            Types = new[] { new ReportData.TypeRow("Typ", 0xFF0000, 0, 0) },
            Ages = new[] { new ReportData.AgeRow("A", 0, 0) },
        });
        Assert.True(File.Exists(Path.Combine(outDir.Path, "odd.pdf")));
    }
}

public class ExporterTests : TestBase
{
    private async Task<Scanner> Sample()
    {
        T.File(@"Root\big\a.bin", 3000);
        T.File(@"Root\big\sub\b.bin", 1000);
        T.File(@"Root\small\c.bin", 10);
        T.File(@"Root\=formula\d.bin", 5);
        T.File(@"Root\semi;colon\e.bin", 1);
        T.File(@"Root\top.bin", 2 << 20);
        return await Scan.Run(T.Sub("Root"));
    }

    private static string[][] Parse(string file)
    {
        var text = File.ReadAllText(file, new UTF8Encoding(true));
        return text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(Split).ToArray();
    }

    // Minimal CSV splitter for ';' with quotes
    private static string[] Split(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(c); }
            else if (c == '"') q = true;
            else if (c == ';') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells.ToArray();
    }

    [Fact]
    public async Task WriteTree_HasBomHeaderAndOneRowPerFolder()
    {
        var s = await Sample();
        using var outDir = new TempDir();
        var file = Path.Combine(outDir.Path, "tree.csv");

        int rows = Exporter.WriteTree(file, s.ScanTarget!);

        var bytes = File.ReadAllBytes(file);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var lines = Parse(file);
        Assert.Equal(rows + 1, lines.Length);
        Assert.Equal(6, rows); // Root, big, sub, small, =formula, semi;colon
        Assert.Equal(new[] { "Ebene", "Name", "Pfad", "Größe (Bytes)", "Größe", "Dateien", "Ordner", "Anteil an Startordner (%)", "Scan vollständig" }, lines[0]);
        Assert.All(lines, l => Assert.Equal(9, l.Length));

        var root = lines[1];
        Assert.Equal("0", root[0]);
        Assert.Equal("Root", root[1]);
        Assert.Equal((3000 + 1000 + 10 + 5 + 1 + (2 << 20)).ToString(), root[3]);
        Assert.Equal("6", root[5]);
        Assert.Equal("5", root[6]);
        Assert.Equal("100", root[7]);
        Assert.Equal("ja", root[8]);
        Assert.Equal(NodeVM.FormatSize(3000 + 1000 + 10 + 5 + 1 + (2 << 20)), root[4]);

        // depth first, biggest sibling first: big (level 1) is directly followed by its child sub (level 2)
        Assert.Equal("big", lines[2][1]);
        Assert.Equal("1", lines[2][0]);
        Assert.Equal("sub", lines[3][1]);
        Assert.Equal("2", lines[3][0]);
        Assert.Equal("4000", lines[2][3]);
    }

    [Fact]
    public async Task WriteTree_EscapesFormulasAndSemicolons()
    {
        var s = await Sample();
        using var outDir = new TempDir();
        var file = Path.Combine(outDir.Path, "tree.csv");
        Exporter.WriteTree(file, s.ScanTarget!);

        var raw = File.ReadAllText(file);
        Assert.Contains("'=formula", raw);                 // would be a formula in Excel otherwise
        Assert.Contains("\"semi;colon\"", raw);            // quoted because of the separator
        var lines = Parse(file);
        Assert.Contains(lines, l => l[1] == "'=formula");
        Assert.Contains(lines, l => l[1] == "semi;colon");
    }

    [Fact]
    public async Task WriteTopFiles_RanksAndCountsRows()
    {
        var s = await Sample();
        using var outDir = new TempDir();
        var file = Path.Combine(outDir.Path, "top.csv");

        int n = Exporter.WriteTopFiles(file, s.TopFiles());

        Assert.Equal(1, n);
        var lines = Parse(file);
        Assert.Equal(new[] { "Rang", "Name", "Ordner", "Pfad", "Größe (Bytes)", "Größe" }, lines[0]);
        Assert.Equal("1", lines[1][0]);
        Assert.Equal("top.bin", lines[1][1]);
        Assert.Equal(T.Sub("Root"), lines[1][2], ignoreCase: true);
        Assert.Equal((2 << 20).ToString(), lines[1][4]);
        Assert.Equal("2,0 MB", lines[1][5]);
    }

    [Fact]
    public void WriteTopFiles_Empty_OnlyHeader()
    {
        using var outDir = new TempDir();
        var file = Path.Combine(outDir.Path, "top.csv");
        Assert.Equal(0, Exporter.WriteTopFiles(file, Array.Empty<Scanner.BigFile>()));
        Assert.Single(Parse(file));
    }

    [Fact]
    public void DefaultFileName_ByKind()
    {
        var node = new Node { Name = "My Docs (2024)" };
        Assert.Matches(@"^DiskTree-MyDocs2024-\d{8}-\d{4}\.csv$", Exporter.DefaultFileName(ExportKind.Subtree, node));
        Assert.Matches(@"^DiskTree-Baum-\d{8}-\d{4}\.csv$", Exporter.DefaultFileName(ExportKind.WholeScan, new Node { Name = ":\\" }));
        Assert.Matches(@"^DiskTree-Baum-\d{8}-\d{4}\.csv$", Exporter.DefaultFileName(ExportKind.Subtree, null));
        Assert.Matches(@"^DiskTree-groesste-Dateien-\d{8}-\d{4}\.csv$", Exporter.DefaultFileName(ExportKind.TopFiles, null));
    }
}
