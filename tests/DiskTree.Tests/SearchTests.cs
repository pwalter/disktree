using System.IO;
using DiskTree.Core;
using DiskTree.Features.Search;
using Xunit;

namespace DiskTree.Tests;

public class SearchTests : TestBase
{
    private async Task<Node> Tree()
    {
        T.File(@"Projekte\Alpha\bericht.docx", 300);
        T.File(@"Projekte\Alpha\daten.xlsx", 5000);
        T.File(@"Projekte\Beta\bericht-final.pdf", 700);
        T.File(@"Projekte\Beta\Archiv\alt.zip", 90_000);
        T.File(@"Fotos\urlaub.JPG", 2000);
        T.File(@"Fotos\klein.jpg", 10);
        T.File("readme.txt", 1);
        return (await ScanT()).ScanTarget!;
    }

    private static SearchResult Run(SearchQuery q) => SearchEngine.Run(q, null, CancellationToken.None);

    // ------------------------------------------------------------------ folders

    [Fact]
    public async Task FolderSearch_ByNamePart_CaseInsensitive_BiggestFirst()
    {
        var scope = await Tree();
        var r = Run(new SearchQuery { Scope = scope, Text = "A" }); // Alpha, Beta, Archiv ("Projekte" and "Fotos" have no a)

        Assert.All(r.Hits, h => Assert.True(h.IsDir));
        Assert.DoesNotContain(r.Hits, h => h.Path.Equals(scope.FullPath, StringComparison.OrdinalIgnoreCase)); // the scope itself is no hit
        Assert.Equal(new[] { "Alpha", "Archiv", "Beta" }, r.Hits.Select(h => h.Name).OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "Beta", "Archiv", "Alpha" }, r.Hits.Select(h => h.Name).ToArray()); // 90700 (incl. Archiv), 90000, 5300
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task FolderSearch_NoText_ListsAllFolders_WithMinSize()
    {
        var scope = await Tree();
        var all = Run(new SearchQuery { Scope = scope });
        Assert.Equal(new[] { "Alpha", "Archiv", "Beta", "Fotos", "Projekte" }, all.Hits.Select(h => h.Name).OrderBy(x => x).ToArray());
        Assert.Equal(6, all.FoldersVisited); // scope + 5

        var big = Run(new SearchQuery { Scope = scope, MinSize = 5000 });
        Assert.Equal(new[] { "Alpha", "Archiv", "Beta", "Projekte" }, big.Hits.Select(h => h.Name).OrderBy(x => x).ToArray());
        Assert.Equal("Projekte", big.Hits[0].Name);
        Assert.Equal(T.Sub("Projekte"), big.Hits[0].Path, ignoreCase: true);
        Assert.Equal(T.Path, big.Hits[0].Folder, ignoreCase: true);
        Assert.Equal(96_000, big.Hits[0].Size);
    }

    [Fact]
    public async Task FolderSearch_ScopeLimitsTheSearch()
    {
        var scope = (await Tree()).Snapshot().First(c => c.Name == "Projekte");
        var r = Run(new SearchQuery { Scope = scope });
        Assert.Equal(new[] { "Alpha", "Archiv", "Beta" }, r.Hits.Select(h => h.Name).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task FolderSearch_ExtensionsAndFilterAreIgnored()
    {
        var scope = await Tree();
        var r = Run(new SearchQuery { Scope = scope, Extensions = new[] { ".xyz" } });
        Assert.Equal(5, r.Hits.Count);
    }

    // ------------------------------------------------------------------ files

    [Fact]
    public async Task FileSearch_ByNamePart()
    {
        var scope = await Tree();
        var r = Run(new SearchQuery { Scope = scope, Files = true, Text = "BERICHT" });

        Assert.Equal(new[] { "bericht-final.pdf", "bericht.docx" }, r.Hits.Select(h => h.Name).OrderBy(x => x).ToArray());
        Assert.All(r.Hits, h => Assert.False(h.IsDir));
        Assert.Equal(700, r.Hits[0].Size); // biggest first
        Assert.Equal(T.Sub("Projekte", "Beta", "bericht-final.pdf"), r.Hits[0].Path, ignoreCase: true);
        Assert.Equal(T.Sub("Projekte", "Beta"), r.Hits[0].Folder, ignoreCase: true);
        Assert.True(r.Hits[0].Modified > 0);
    }

    [Fact]
    public async Task FileSearch_ByExtension_AndMinSize()
    {
        var scope = await Tree();
        var jpg = Run(new SearchQuery { Scope = scope, Files = true, Extensions = new[] { ".jpg" } });
        Assert.Equal(new[] { "klein.jpg", "urlaub.JPG" }, jpg.Hits.Select(h => h.Name).OrderBy(x => x).ToArray());

        var several = Run(new SearchQuery { Scope = scope, Files = true, Extensions = new[] { ".jpg", ".zip", ".txt" }, MinSize = 100 });
        Assert.Equal(new[] { "alt.zip", "urlaub.JPG" }, several.Hits.Select(h => h.Name).OrderBy(x => x).ToArray());

        var combined = Run(new SearchQuery { Scope = scope, Files = true, Text = "klein", Extensions = new[] { ".jpg" } });
        Assert.Single(combined.Hits);
    }

    [Fact]
    public async Task FileSearch_NoCriteria_ListsAllFilesOnDisk()
    {
        var scope = await Tree();
        var r = Run(new SearchQuery { Scope = scope, Files = true });
        Assert.Equal(7, r.Hits.Count);
        Assert.Equal(90_000, r.Hits[0].Size);
        Assert.Equal(1, r.Hits[^1].Size);
    }

    [Fact]
    public async Task FileSearch_SeesFilesAddedAfterTheScan_ButOnlyInScannedFolders()
    {
        var scope = await Tree();
        T.File(@"Fotos\neu.jpg", 5);
        T.File(@"NeuerOrdner\x.jpg", 5); // folder was not scanned: not part of the tree
        var r = Run(new SearchQuery { Scope = scope, Files = true, Extensions = new[] { ".jpg" } });
        Assert.Equal(3, r.Hits.Count);
    }

    [Fact]
    public async Task FileSearch_TruncatesAtMaxHits()
    {
        var many = T.Dir("many");
        Parallel.For(0, SearchEngine.MaxHits + 5, i => File.Create(Path.Combine(many, $"f{i}.txt")).Dispose());
        var scope = (await ScanT()).ScanTarget!;
        var r = Run(new SearchQuery { Scope = scope, Files = true });
        Assert.True(r.Truncated);
        Assert.Equal(SearchEngine.MaxHits, r.Hits.Count);
    }

    [Fact]
    public async Task Search_CanBeCancelled()
    {
        var scope = await Tree();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => SearchEngine.Run(new SearchQuery { Scope = scope }, null, cts.Token));
    }

    // ------------------------------------------------------------------ date / attribute filters

    [Fact]
    public async Task FileSearch_DateFilter_OlderThan()
    {
        T.File("new.txt", 1, Ago(days: 5));
        T.File("year.txt", 1, Ago(months: 14));
        T.File("old.txt", 1, Ago(years: 6));
        var scope = (await ScanT()).ScanTarget!;

        Names(scope, new FileFilter { Stamp = AgeStamp.Modified, Amount = 1, Unit = DateUnit.Years }, "old.txt", "year.txt");
        Names(scope, new FileFilter { Stamp = AgeStamp.Modified, Amount = 5, Unit = DateUnit.Years }, "old.txt");
        Names(scope, new FileFilter { Stamp = AgeStamp.Modified, Amount = 30, Unit = DateUnit.Days }, "old.txt", "year.txt");
        Names(scope, new FileFilter { Stamp = AgeStamp.Modified, Amount = 3, Unit = DateUnit.Days }, "new.txt", "old.txt", "year.txt");
        Names(scope, new FileFilter { Stamp = AgeStamp.Modified, Amount = 13, Unit = DateUnit.Months }, "old.txt", "year.txt");
        Names(scope, new FileFilter { Stamp = AgeStamp.Modified, Amount = 15, Unit = DateUnit.Months }, "old.txt");
    }

    [Fact]
    public async Task FileSearch_DateFilter_UsesTheChosenTimestamp()
    {
        var f = T.File("a.txt", 1, Ago(days: 1));
        File.SetCreationTimeUtc(f, Ago(years: 4));
        File.SetLastAccessTimeUtc(f, Ago(years: 2));
        var scope = (await ScanT()).ScanTarget!;

        Names(scope, new FileFilter { Stamp = AgeStamp.Modified, Amount = 1, Unit = DateUnit.Years });
        Names(scope, new FileFilter { Stamp = AgeStamp.Created, Amount = 3, Unit = DateUnit.Years }, "a.txt");
        Names(scope, new FileFilter { Stamp = AgeStamp.Accessed, Amount = 3, Unit = DateUnit.Years });
        Names(scope, new FileFilter { Stamp = AgeStamp.Accessed, Amount = 1, Unit = DateUnit.Years }, "a.txt");
    }

    [Fact]
    public async Task FileSearch_AttributeFilter_AllChosenMustHold_AnyFlagOfAChoiceIsEnough()
    {
        File.SetAttributes(T.File("hidden.txt", 1), FileAttributes.Hidden);
        File.SetAttributes(T.File("ro.txt", 1), FileAttributes.ReadOnly);
        File.SetAttributes(T.File("both.txt", 1), FileAttributes.Hidden | FileAttributes.ReadOnly);
        T.File("plain.txt", 1);
        var scope = (await ScanT()).ScanTarget!;
        var hidden = AttributeChoice.All.First(a => a.Name == "Versteckt");
        var readOnly = AttributeChoice.All.First(a => a.Name == "Schreibgeschützt");

        Names(scope, new FileFilter { Attributes = new[] { hidden } }, "both.txt", "hidden.txt");
        Names(scope, new FileFilter { Attributes = new[] { readOnly } }, "both.txt", "ro.txt");
        Names(scope, new FileFilter { Attributes = new[] { hidden, readOnly } }, "both.txt");

        var r = Run(new SearchQuery { Scope = scope, Files = true, Filter = new FileFilter { Attributes = new[] { hidden } } });
        Assert.All(r.Hits, h => Assert.True(h.ShowAttributes));
        Assert.All(r.Hits, h => Assert.Contains("Versteckt", new SearchHitRow(h).DetailText));
    }

    [Fact]
    public async Task FileSearch_EmptyFilter_IsIgnored()
    {
        T.File("a.txt", 1);
        var scope = (await ScanT()).ScanTarget!;
        Names(scope, new FileFilter(), "a.txt");
        Assert.True(new FileFilter().IsEmpty);
    }

    private void Names(Node scope, FileFilter filter, params string[] expected)
    {
        var r = Run(new SearchQuery { Scope = scope, Files = true, Filter = filter });
        Assert.Equal(expected.OrderBy(x => x), r.Hits.Select(h => h.Name).OrderBy(x => x));
    }

    // ------------------------------------------------------------------ FileFilter / SearchHit

    [Fact]
    public void FileFilter_Matches_Logic()
    {
        var f = new FileFilter { Stamp = AgeStamp.Created, Amount = 10, Unit = DateUnit.Days };
        long old = DateTime.UtcNow.AddDays(-20).Ticks, fresh = DateTime.UtcNow.AddDays(-1).Ticks;
        Assert.True(f.Matches(0, modified: fresh, created: old, accessed: fresh));
        Assert.False(f.Matches(0, modified: old, created: fresh, accessed: old));
        Assert.True(f.CutoffTicks < DateTime.UtcNow.Ticks);
        Assert.False(f.IsEmpty);
    }

    [Fact]
    public void FileFilter_AbsurdAmount_MatchesNothing()
    {
        var f = new FileFilter { Stamp = AgeStamp.Modified, Amount = 100_000, Unit = DateUnit.Years };
        Assert.False(f.Matches(0, DateTime.UtcNow.AddYears(-50).Ticks, 0, 0));
        Assert.Equal(DateTime.MinValue.Ticks, f.CutoffTicks);
    }

    [Fact]
    public void FileFilter_OfflineChoice_CoversRecallFlags()
    {
        var offline = AttributeChoice.All.First(a => a.Name == "Offline / Cloud");
        var f = new FileFilter { Attributes = new[] { offline } };
        Assert.True(f.Matches(FileAttributes.Offline, 0, 0, 0));
        Assert.True(f.Matches((FileAttributes)0x400000, 0, 0, 0));
        Assert.True(f.Matches((FileAttributes)0x40000, 0, 0, 0));
        Assert.False(f.Matches(FileAttributes.Normal, 0, 0, 0));
    }

    [Theory]
    [InlineData(DateUnit.Days, "Tagen")]
    [InlineData(DateUnit.Months, "Monaten")]
    [InlineData(DateUnit.Years, "Jahren")]
    public void UnitText(DateUnit u, string text) => Assert.Equal(text, FileFilter.UnitText(u));

    [Fact]
    public void SearchHitRow_TextHelpers()
    {
        var node = new Node();
        var file = new SearchHit(@"C:\a\f.txt", "f.txt", @"C:\a", 1536, false, node)
        {
            Modified = new DateTime(2020, 3, 4, 12, 0, 0, DateTimeKind.Utc).Ticks,
            Attributes = FileAttributes.Hidden | FileAttributes.ReadOnly,
            ShownStamp = AgeStamp.Modified,
            ShowAttributes = true,
        };
        var fileRow = new SearchHitRow(file);
        Assert.Equal("1,5 KB", fileRow.SizeText);
        Assert.Equal("📄", fileRow.Icon);
        Assert.StartsWith("Zuletzt geändert: ", fileRow.DetailText);
        Assert.Contains("Versteckt, Schreibgeschützt", fileRow.DetailText);
        Assert.Contains("Geändert:", fileRow.ToolTipText);
        Assert.Contains("Erstellt: unbekannt", fileRow.ToolTipText);

        var dir = new SearchHitRow(new SearchHit(@"C:\a", "a", @"C:\", 5, true, node));
        Assert.Equal("📁", dir.Icon);
        Assert.Equal("", dir.DetailText);
        Assert.Equal(@"C:\a", dir.ToolTipText);
    }
}
