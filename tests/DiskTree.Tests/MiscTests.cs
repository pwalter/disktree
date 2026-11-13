using System.Globalization;
using System.IO;
using System.Windows;
using DiskTree.Core;
using DiskTree.Features.Owner;
using DiskTree.Features.Treemap;
using DiskTree.Features.Update;
using DiskTree.Ui;
using Xunit;

namespace DiskTree.Tests;

public class UpdaterTests
{
    [Theory]
    [InlineData("2026.11.12.4", "2026.11.12.4")]
    [InlineData("v2026.11.12.4", "2026.11.12.4")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("  v1.2.3  ", "1.2.3")]
    [InlineData("1.2.3+abcdef", "1.2.3")]
    [InlineData("v1.2", "1.2")]
    [InlineData("10.0.0", "10.0.0")]
    public void ParseVersion_Releases(string text, string expected) => Assert.Equal(Version.Parse(expected), Updater.ParseVersion(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0.0.0-dev")]
    [InlineData("0.0.0-ci.17")]
    [InlineData("1.2.3-beta")]
    [InlineData("0.0.0")]
    [InlineData("0.1.2")]
    [InlineData("abc")]
    [InlineData("v")]
    [InlineData("1.2.3.4.5")]
    public void ParseVersion_NoReleaseNumber_IsNull(string? text) => Assert.Null(Updater.ParseVersion(text));

    [Fact]
    public void ParseVersion_OrdersLikeReleases()
    {
        var a = Updater.ParseVersion("v2026.11.12.4")!;
        var b = Updater.ParseVersion("v2026.11.12.10")!;
        var c = Updater.ParseVersion("v2026.12.1.1")!;
        Assert.True(b > a);
        Assert.True(c > b);
    }

    private static UpdateInfo Info(string version) =>
        new(Version.Parse(version), "v" + version, "https://example.invalid/r", "DiskTree.exe", "https://example.invalid/a", 1, null);

    [Fact]
    public void IsNewer_ComparesWithTheRunningVersion()
    {
        // The test build carries no release number (Current == null): every release counts as newer.
        // With a release build the comparison is a plain version comparison.
        Assert.True(Updater.IsNewer(Info("9999.1.1.1")));
        bool expectedOld = Updater.Current == null || Version.Parse("1.0.0") > Updater.Current;
        Assert.Equal(expectedOld, Updater.IsNewer(Info("1.0.0")));
        if (Updater.Current is { } cur)
        {
            Assert.False(Updater.IsNewer(Info(cur.ToString())));
            Assert.Equal("Entwicklung", "Entwicklung"); // CurrentText is the version here
            Assert.Equal(cur.ToString(), Updater.CurrentText);
        }
        else
        {
            Assert.Equal("Entwicklung", Updater.CurrentText);
        }
    }

    [Fact]
    public void ReleasesPage_PointsAtTheProject() =>
        Assert.Equal("https://github.com/pwalter/disktree/releases", Updater.ReleasesPage);
}

public class TreemapLayoutTests
{
    private static double Area(Rect r) => r.Width * r.Height;

    private static double Overlap(Rect a, Rect b)
    {
        double w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        double h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }

    private static void AssertTiles(double[] sizes, Rect bounds)
    {
        var rects = TreemapLayout.Squarify(sizes, bounds);
        Assert.Equal(sizes.Length, rects.Length);
        double total = sizes.Where(s => s > 0).Sum();
        double sum = 0;
        const double eps = 1e-6;
        for (int i = 0; i < rects.Length; i++)
        {
            if (sizes[i] <= 0)
            {
                Assert.Equal(0, Area(rects[i]));
                continue;
            }
            var r = rects[i];
            Assert.True(r.Left >= bounds.Left - eps && r.Top >= bounds.Top - eps && r.Right <= bounds.Right + eps && r.Bottom <= bounds.Bottom + eps,
                $"Rechteck {i} liegt außerhalb: {r} in {bounds}");
            double expected = sizes[i] / total * Area(bounds);
            Assert.True(Math.Abs(Area(r) - expected) <= Math.Max(1e-6, expected * 1e-6), $"Fläche {i}: {Area(r)} statt {expected}");
            sum += Area(r);
        }
        Assert.True(Math.Abs(sum - Area(bounds)) <= Area(bounds) * 1e-9 + 1e-6, $"Summe {sum} statt {Area(bounds)}");

        for (int i = 0; i < rects.Length; i++)
            for (int j = i + 1; j < rects.Length; j++)
                Assert.True(Overlap(rects[i], rects[j]) <= 1e-6, $"Rechtecke {i} und {j} überlappen: {rects[i]} / {rects[j]}");
    }

    [Fact]
    public void SingleEntry_FillsTheBounds()
    {
        var r = TreemapLayout.Squarify(new[] { 42.0 }, new Rect(10, 20, 300, 200));
        Assert.Equal(new Rect(10, 20, 300, 200), r[0]);
    }

    [Fact]
    public void TwoEqualEntries_SplitInHalf()
    {
        var r = TreemapLayout.Squarify(new[] { 1.0, 1.0 }, new Rect(0, 0, 200, 100));
        Assert.Equal(20000, Area(r[0]) + Area(r[1]), 6);
        Assert.Equal(Area(r[0]), Area(r[1]), 6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(50)]
    [InlineData(500)]
    public void RandomSizes_TileTheBounds(int count)
    {
        var rnd = new Random(count);
        var sizes = Enumerable.Range(0, count).Select(_ => Math.Pow(rnd.NextDouble(), 3) * 1e9 + 1).OrderByDescending(x => x).ToArray();
        AssertTiles(sizes, new Rect(0, 0, 800, 600));
        AssertTiles(sizes, new Rect(5.5, 7.25, 123.4, 987.6));  // tall
        AssertTiles(sizes, new Rect(0, 0, 1000, 30));            // flat
    }

    [Fact]
    public void ExtremeProportions_StillTile()
    {
        AssertTiles(new[] { 1e12, 1.0, 1.0, 1.0, 1.0 }, new Rect(0, 0, 640, 480));
        AssertTiles(new[] { 5.0, 5.0, 5.0, 5.0, 5.0, 5.0, 5.0, 5.0, 5.0 }, new Rect(0, 0, 90, 90));
    }

    [Fact]
    public void ZeroAndNegativeSizes_GetEmptyRectangles_RestStillTiles()
    {
        AssertTiles(new[] { 100.0, 50.0, 0.0, 25.0, -5.0, 0.0 }, new Rect(0, 0, 400, 300));
    }

    [Fact]
    public void DegenerateInput_ReturnsEmptyRectangles()
    {
        Assert.Empty(TreemapLayout.Squarify(Array.Empty<double>(), new Rect(0, 0, 10, 10)));
        Assert.All(TreemapLayout.Squarify(new[] { 0.0, 0.0 }, new Rect(0, 0, 10, 10)), r => Assert.Equal(0, Area(r)));
        Assert.All(TreemapLayout.Squarify(new[] { 3.0, 2.0 }, new Rect(0, 0, 0, 10)), r => Assert.Equal(0, Area(r)));
        Assert.All(TreemapLayout.Squarify(new[] { 3.0, 2.0 }, new Rect(0, 0, 10, 0)), r => Assert.Equal(0, Area(r)));
    }

    [Fact]
    public void BiggestEntryKeepsAReasonableShape()
    {
        var sizes = Enumerable.Range(1, 30).Select(i => 1000.0 / i).ToArray();
        var rects = TreemapLayout.Squarify(sizes, new Rect(0, 0, 800, 600));
        double worst = rects.Max(r => Math.Max(r.Width / r.Height, r.Height / r.Width));
        Assert.True(worst < 6, $"Schlechtestes Seitenverhältnis: {worst:0.0}");
    }
}

public class FormatSizeTests : TestBase
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1.023 B")]
    [InlineData(1024, "1,0 KB")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(10 * 1024, "10,0 KB")]
    [InlineData(100 * 1024, "100 KB")]
    [InlineData(1000 * 1024, "1000 KB")]
    [InlineData(1024L * 1024, "1,0 MB")]
    [InlineData(1536L * 1024, "1,5 MB")]
    [InlineData(1024L * 1024 * 1024, "1,0 GB")]
    [InlineData(5L * 1024 * 1024 * 1024 * 1024, "5,0 TB")]
    [InlineData(1024L * 1024 * 1024 * 1024 * 1024, "1,0 PB")]
    [InlineData(long.MaxValue, "8192 PB")]
    public void FormatSize_German(long bytes, string expected) => Assert.Equal(expected, SizeFormat.Format(bytes));

    [Fact]
    public void FormatSize_FollowsTheCurrentCulture()
    {
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        Assert.Equal("1.5 KB", SizeFormat.Format(1536));
        Assert.Equal("1,023 B", SizeFormat.Format(1023));
    }
}

public class OwnerInfoTests : TestBase
{
    private static readonly List<(string, long)> Files = new()
    {
        (@"a\1", 5_000_000), (@"a\2", 3_000_000), (@"b\3", 2_000_000),
        (@"b\4", 100), (@"c\denied", 9_000_000), (@"c\5", 4_000_000),
    };

    private static string? Fake(string p) => p switch
    {
        @"a\1" or @"a\2" => "DOM\\anna",
        @"b\3" or @"b\4" => "DOM\\bob",
        @"c\denied" => null,
        _ => "DOM\\bob",
    };

    [Fact]
    public void Aggregate_SumsPerOwner_SortedDescending_CountsDenied()
    {
        var all = OwnerAggregator.Aggregate(Files, Fake, 0, 4, null, CancellationToken.None);
        Assert.Equal(2, all.Rows.Count);
        Assert.Equal("DOM\\anna", all.Rows[0].Owner);
        Assert.Equal(8_000_000, all.Rows[0].Bytes);
        Assert.Equal("DOM\\bob", all.Rows[1].Owner);
        Assert.Equal(6_000_100, all.Rows[1].Bytes);
        Assert.Equal(3, all.Rows[1].Files);
        Assert.Equal(1, all.Failed);
        Assert.Equal(6, all.Seen);
        Assert.Equal(0, all.Skipped);
        Assert.Equal(14_000_100, all.TotalBytes);
        Assert.False(all.Cancelled);
    }

    [Fact]
    public void Aggregate_SampleSkipsSmallFiles()
    {
        var sample = OwnerAggregator.Aggregate(Files, Fake, 1_000_000, 2, null, CancellationToken.None);
        Assert.Equal(1, sample.Skipped);
        Assert.Equal(4, sample.Rows.Sum(r => r.Files));
    }

    [Fact]
    public void Aggregate_CancelledGivesPartialResultWithFlag()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(OwnerAggregator.Aggregate(Files, Fake, 0, 2, null, cts.Token).Cancelled);
    }

    [Fact]
    public void Aggregate_ReportsProgress()
    {
        long last = -1;
        OwnerAggregator.Aggregate(Files, Fake, 0, 1, (seen, _) => last = seen, CancellationToken.None);
        Assert.Equal(6, last);
    }

    [Fact]
    public async Task RealFolder_OwnerSidNameAndAcl()
    {
        T.File("x.bin", 2048);
        T.File(@"sub\y.bin", 1024);

        var sid = OwnerInfo.GetOwnerSid(T.Path);
        Assert.NotNull(sid);
        Assert.StartsWith("S-1-", sid);
        Assert.StartsWith("S-1-", OwnerInfo.GetOwnerSid(T.Sub("x.bin")));

        var name = await OwnerInfo.GetOwnerNameAsync(T.Path);
        Assert.False(string.IsNullOrEmpty(name));

        var acl = OwnerInfo.GetAclLines(T.Path);
        Assert.NotEmpty(acl);

        var real = OwnerAggregator.Aggregate(OwnerAggregator.EnumerateFiles(T.Path),
            p => OwnerInfo.GetOwnerSid(p) is { } s ? OwnerInfo.ResolveName(s) : null, 0, 2, null, CancellationToken.None);
        Assert.Equal(2, real.Rows.Sum(r => r.Files));
        Assert.Equal(3072, real.TotalBytes);
        Assert.Single(real.Rows);
    }

    [Fact]
    public void Missing_PathHasNoOwner() => Assert.Null(OwnerInfo.GetOwnerSid(T.Sub("does", "not", "exist")));

    [Fact]
    public async Task ResolveName_WellKnownAndUnknownSids()
    {
        var admins = await OwnerInfo.ResolveNameAsync("S-1-5-32-544");
        Assert.DoesNotContain("S-1-", admins);
        Assert.Contains("\\", admins);
        Assert.Equal(admins, OwnerInfo.ResolveName("S-1-5-32-544")); // cached

        var unknown = await OwnerInfo.ResolveNameAsync("S-1-5-21-1-2-3-99999");
        Assert.StartsWith("S-1-5-21", unknown);
        Assert.Equal("not-a-sid", OwnerInfo.ResolveName("not-a-sid"));
    }

    [Fact]
    public void EnumerateFiles_SkipsNothingReadable_AndFindsNested()
    {
        T.File("a.txt", 1);
        T.File(@"d1\d2\b.txt", 2);
        var files = OwnerAggregator.EnumerateFiles(T.Path).ToList();
        Assert.Equal(new long[] { 1, 2 }, files.Select(f => f.Size).OrderBy(x => x).ToArray());
    }
}
