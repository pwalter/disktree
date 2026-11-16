using System.IO;
using System.IO.Enumeration;
using DiskTree.Core;
using DiskTree.Core.Storage;
using Xunit;

namespace DiskTree.Tests.Storage;

public class PathOpsTests
{
    private static readonly PathOps Slash = new('/', StringComparison.Ordinal);

    [Theory]
    [InlineData("/", "a", "/a")]
    [InlineData("/data", "a", "/data/a")]
    [InlineData("/data/", "a", "/data/a")]
    [InlineData("", "a", "a")]
    [InlineData("/data", "", "/data")]
    public void Join_PutsExactlyOneSeparator(string parent, string name, string expected) =>
        Assert.Equal(expected, Slash.Join(parent, name));

    [Theory]
    [InlineData("/data/a/b.txt", "b.txt", "/data/a")]
    [InlineData("/data/a/", "a", "/data")]
    [InlineData("/data", "data", "/")]
    [InlineData("/", "", null)]
    [InlineData("name", "name", null)]
    public void NameAndParent(string path, string name, string? parent)
    {
        Assert.Equal(name, Slash.NameOf(path));
        Assert.Equal(parent, Slash.ParentOf(path));
    }

    [Fact]
    public void Under_StopsAtSeparator_AndRespectsCase()
    {
        Assert.True(Slash.IsUnder("/data", "/data/a"));
        Assert.False(Slash.IsUnder("/data", "/data"));
        Assert.True(Slash.IsSameOrUnder("/data/", "/data"));
        Assert.False(Slash.IsUnder("/data/a", "/data/ab"));
        Assert.False(Slash.IsUnder("/data", "/DATA/a"));
        Assert.True(Slash.IsSameOrUnder("/", "/data"));
        Assert.Equal("/a/b", Slash.RelativeTo("/data/", "/data/a/b"));
        Assert.Equal(new[] { "a", "b" }, Slash.Split("/a//b/"));
    }

    [Fact]
    public void Disk_IsCaseInsensitive_LikePathUtil()
    {
        var d = DiskPathOps.Instance;
        Assert.Equal('\\', d.Separator);
        Assert.True(d.IsUnder(@"C:\A", @"c:\a\b"));
        Assert.False(d.IsUnder(@"C:\a", @"C:\ab"));
        Assert.Equal(PathUtil.EnsureTrailingBackslash(@"C:\a\\"), d.EnsureTrailingSeparator(@"C:\a\\"));
        Assert.True(d.Comparer.Equals("Windows", "WINDOWS"));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\a")]
    [InlineData(@"C:\a\b.txt")]
    [InlineData(@"C:\a\")]
    [InlineData(@"\\srv\share")]
    [InlineData(@"\\srv\share\x\y")]
    [InlineData(@"C:/a/b")]
    [InlineData(@"rel\x")]
    [InlineData("")]
    public void Disk_GivesWhatSystemIOPathGives(string path)
    {
        var d = DiskPathOps.Instance;
        Assert.Equal(Path.GetFileName(path.TrimEnd('\\')), d.NameOf(path));
        Assert.Equal(Path.GetDirectoryName(path.TrimEnd('\\')), d.ParentOf(path));
        Assert.Equal(Path.Join(path, "n"), d.Join(path, "n"));
    }
}

public class DiskProviderTests : TestBase
{
    private static readonly DiskProvider Disk = DiskProvider.Instance;

    [Fact]
    public void Enumerate_GivesWhatFileSystemEnumerableGives()
    {
        T.File(@"d\a.txt", 5, Ago(days: 3));
        T.File(@"d\B.bin", 7);
        T.File(@"d\sub\x.txt", 1);
        File.SetAttributes(T.Sub("d", "a.txt"), FileAttributes.Hidden);
        var dir = T.Sub("d");

        var mine = Disk.Enumerate(dir, (in StorageEntry e) => (e.Name.ToString(), e.IsDirectory, e.Length, e.Attributes,
            e.ModifiedTicks, e.CreatedTicks, e.AccessedTicks), ListOptions.Strict).OrderBy(x => x.Item1).ToList();

        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        var theirs = new FileSystemEnumerable<(string, bool, long, FileAttributes, long, long, long)>(dir,
            (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes,
                e.LastWriteTimeUtc.UtcTicks, e.CreationTimeUtc.UtcTicks, e.LastAccessTimeUtc.UtcTicks), options)
            .OrderBy(x => x.Item1).ToList();

        Assert.Equal(theirs, mine);
        Assert.Equal(3, mine.Count); // hidden file included, no "." / ".."
    }

    [Fact]
    public void Enumerate_FillsOnlyTheAskedTimestamps()
    {
        T.File(@"d\a.txt", 5);
        var e = Disk.Enumerate(T.Sub("d"), (in StorageEntry x) => (x.ModifiedTicks, x.CreatedTicks, x.AccessedTicks),
            ListOptions.Strict with { Stamps = StampSet.Created }).Single();

        Assert.Equal(0, e.ModifiedTicks);
        Assert.NotEqual(0, e.CreatedTicks);
        Assert.Equal(0, e.AccessedTicks);
    }

    [Fact]
    public void Enumerate_IsLazy_AndThrowsForMissingFolder()
    {
        var listing = Disk.Enumerate(T.Sub("gone"), (in StorageEntry x) => 0, ListOptions.Strict); // no exception yet
        Assert.Throws<DirectoryNotFoundException>(() => listing.ToList());
    }

    [Fact]
    public void ResolveRoot_KnowsDriveAndCapacity()
    {
        var r = Disk.ResolveRoot(T.Path + @"\.\x\..");
        Assert.Equal(Path.GetFullPath(T.Path), r.Target);
        Assert.Equal(Path.GetPathRoot(T.Path), r.Top);
        Assert.True(r.HasCapacity);
        Assert.InRange(r.Free, 0, r.Total);
    }

    [Fact]
    public void TryParse_NormalizesAndNeverThrows()
    {
        Assert.True(Disk.TryParse(T.Path + @"\a\..", out var loc));
        Assert.Same(Disk, loc.Provider);
        Assert.Equal(Path.GetFullPath(T.Path), loc.Path);
        Assert.False(Disk.TryParse("  ", out _));
        Assert.False(Disk.TryParse("C:\\a\0b", out _));
    }

    [Fact]
    public void Registry_KnowsTheDisk_AndAddedProviders()
    {
        Assert.Same(Disk, StorageProviders.Get(null));
        Assert.Same(Disk, StorageProviders.Get("disk"));
        Assert.Null(StorageProviders.Get("nope"));

        var fake = new FakeProvider("fake-registry");
        StorageProviders.Register(fake);
        try
        {
            Assert.Same(fake, StorageProviders.Get("fake-registry"));
            Assert.True(StorageProviders.TryResolve("/bucket/x", out var loc));
            Assert.Same(fake, loc.Provider);
            Assert.True(StorageProviders.TryResolve(T.Path, out loc));
            Assert.Same(Disk, loc.Provider);
        }
        finally
        {
            StorageProviders.Unregister("fake-registry");
        }
        Assert.Null(StorageProviders.Get("fake-registry"));
        StorageProviders.Unregister("disk"); // the disk always stays
        Assert.Same(Disk, StorageProviders.Get("disk"));
    }

    [Fact]
    public async Task Scanner_DefaultsToTheDisk()
    {
        T.File(@"a\f.txt", 3);
        var s = await ScanT();
        Assert.Same(Disk, s.Provider);
        Assert.Same(DiskPathOps.Instance, s.Paths);
    }
}
