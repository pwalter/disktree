using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.Storage.S3;
using Xunit;

namespace DiskTree.Tests.Storage;

/// <summary>What the path box / command line / a drop asks to scan: one provider, several paths of it, mixtures refused.</summary>
public sealed class ScanInputTests : IDisposable
{
    public ScanInputTests()
    {
        S3Storage.Register();
        SourceProfiles.Set([]);
    }

    public void Dispose()
    {
        SourceProfiles.PreferredId = null;
        SourceProfiles.Set([]);
    }

    [Fact]
    public void Local_text_is_read_exactly_like_before()
    {
        using var dir = new TempDir();
        var a = dir.Dir("a");
        var b = dir.Dir("b");
        foreach (var text in new[] { a, $"{a}; {b}", "C:", "", "  ", $"\"{a}\"", @"\\server\share\x" })
        {
            var input = ScanInput.Parse(text);
            Assert.Null(input.Error);
            Assert.True(input.IsDisk);
            Assert.Same(StorageProviders.Disk, input.Provider);
            Assert.Equal(PathList.Parse(text), input.Paths);
        }
    }

    [Fact]
    public void An_s3_uri_gives_the_s3_provider()
    {
        var input = ScanInput.Parse("  s3://bucket/logs/2024/  ");
        Assert.Null(input.Error);
        Assert.False(input.IsDisk);
        Assert.Equal("s3", input.Provider.Id);
        Assert.Equal(["s3://bucket/logs/2024"], input.Paths);
    }

    [Fact]
    public void Several_paths_of_one_source_are_reduced_by_its_rules()
    {
        var input = ScanInput.Parse("s3://b/x; s3://b/x/sub; s3://b/X; s3://b/x/");
        Assert.Null(input.Error);
        // case-sensitive: X is another prefix; x/sub lies inside x; x/ is x
        Assert.Equal(["s3://b/x", "s3://b/X"], input.Paths);
    }

    [Fact]
    public void A_semicolon_inside_a_key_stays_part_of_it()
    {
        var input = ScanInput.Parse("s3://b/a;b");
        Assert.Null(input.Error);
        Assert.Equal(["s3://b/a;b"], input.Paths);
    }

    [Fact]
    public void Mixing_a_source_and_a_drive_is_refused()
    {
        var input = ScanInput.Parse(@"s3://bucket/a; C:\Daten");
        Assert.NotNull(input.Error);
        Assert.Contains("einer einzigen Quelle", input.Error);
        Assert.Empty(input.Paths);

        Assert.NotNull(ScanInput.Parse(@"C:\Daten; s3://bucket/a").Error);
    }

    [Fact]
    public void Two_saved_profiles_are_two_sources()
    {
        var one = new SourceProfile { Kind = "s3", Name = "eins", Path = "s3://b1" };
        var two = new SourceProfile { Kind = "s3", Name = "zwei", Path = "s3://b2" };
        SourceProfiles.Set([one, two]);

        var same = ScanInput.Parse("s3://b1/a; s3://b1/b");
        Assert.Null(same.Error);
        Assert.Equal("eins", same.Provider.Profile?.Name);

        var mixed = ScanInput.Parse("s3://b1/a; s3://b2/b");
        Assert.NotNull(mixed.Error);
        Assert.Contains("eins", mixed.Error);
        Assert.Contains("zwei", mixed.Error);
    }

    [Fact]
    public void Remote_part_is_detected_only_for_registered_schemes()
    {
        Assert.True(ScanInput.HasRemotePart("S3://bucket"));
        Assert.True(ScanInput.HasRemotePart(@"C:\a; s3://b"));
        Assert.False(ScanInput.HasRemotePart(@"C:\a; D:\b"));
        Assert.False(ScanInput.HasRemotePart("ftp://host/x"));
    }
}
