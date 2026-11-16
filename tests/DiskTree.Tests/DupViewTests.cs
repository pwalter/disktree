using System.Windows;
using DiskTree.Core.Storage;
using DiskTree.Features.Duplicates;
using DiskTree.Features.Storage.S3;
using DiskTree.Tests.Storage;
using Xunit;

namespace DiskTree.Tests;

/// <summary>What the duplicate tab shows: certainty badges, path rules of the source, the texts of the estimate dialog.</summary>
public class DupViewTests : TestBase
{
    [Fact]
    public void Disk_groups_look_as_before_remote_groups_carry_a_badge_and_no_check_boxes()
    {
        var disk = new DupGroupVM(new DuplicateGroup(1000, [@"C:\a\x.bin", @"C:\b\x.bin"]));
        Assert.Equal(Visibility.Collapsed, disk.BadgeVisibility);
        Assert.All(disk.Files, f => Assert.Equal(Visibility.Visible, f.CheckVisibility));
        Assert.Equal("x.bin", disk.Files[0].Name);
        Assert.Equal(@"C:\a", disk.Files[0].Folder);
        Assert.StartsWith("2 identische Dateien", disk.Detail);

        var sure = new DupGroupVM(new DuplicateGroup(1000, ["s3://bk/a/x.bin", "s3://bk/x.bin"]) { Basis = DupBasis.ContentTag },
            S3PathOps.Instance, remote: true);
        Assert.Equal(Visibility.Visible, sure.BadgeVisibility);
        Assert.Equal("sicher", sure.BadgeText);
        Assert.Contains("ETag", sure.BadgeTip);
        Assert.All(sure.Files, f => Assert.Equal(Visibility.Collapsed, f.CheckVisibility));
        Assert.Equal("s3://bk/a", sure.Files[0].Folder);
        Assert.Equal("s3://bk", sure.Files[1].Folder);

        var probable = new DupGroupVM(new DuplicateGroup(1000, ["/d/a", "/d/b"]) { Basis = DupBasis.PartialHash },
            new FakeProvider().Paths, remote: true);
        Assert.Equal("wahrscheinlich", probable.BadgeText);
        Assert.False(probable.IsCertain);
        Assert.Contains("wahrscheinlich", probable.Detail);
        Assert.Contains("Gründlich", probable.BadgeTip);
    }

    [Theory]
    [InlineData("2", 2L << 30)]
    [InlineData("0,5", 1L << 29)]
    [InlineData("0.5", 1L << 29)]
    [InlineData("0", 0L)]
    [InlineData("", 0L)]
    [InlineData("abc", null)]
    [InlineData("-1", null)]
    public void Download_limit_in_GB(string text, long? bytes) => Assert.Equal(bytes, DupEstimateDialog.ParseGb(text));

    [Fact]
    public async Task Estimate_texts_name_downloads_and_requests_only_for_sources_with_costs()
    {
        var p = new FakeDeepProvider();
        var x = new byte[300_000];
        p.File("/d/a.bin", x).File("/d/b.bin", x).File("/d/c.bin", new byte[300_000]);
        p.Tag("/d/a.bin", "0123456789abcdef0123456789abcdef");
        var c = await DuplicateFinder.CollectAsync(p, "/d", 1, new ListRun(), null);
        var e = DuplicateFinder.Estimate(c);

        var fast = DupEstimateDialog.FastDescription(e, tags: true, costs: true);
        Assert.Contains("HEAD-Anfragen: 1", fast);
        Assert.Contains("Anfragen insgesamt", fast);
        Assert.Contains("wahrscheinlich", DupEstimateDialog.FastDescription(e, tags: false, costs: false));
        Assert.DoesNotContain("Anfragen", DupEstimateDialog.FastDescription(e, tags: false, costs: false));

        var thorough = DupEstimateDialog.ThoroughDescription(e, costs: true);
        Assert.Contains("SHA-256", thorough);
        Assert.Contains("GET-Anfragen: bis zu", thorough);
        Assert.DoesNotContain("GET", DupEstimateDialog.ThoroughDescription(e, costs: false));

        var listed = DupEstimateDialog.ListedText(c, costs: true);
        Assert.Contains("3 Dateien", listed);
        Assert.Contains("(1 LIST-Anfrage)", listed);
    }
}
