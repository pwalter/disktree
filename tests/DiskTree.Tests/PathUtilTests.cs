using DiskTree.Core;
using DiskTree.Core.Snapshots;
using Xunit;

namespace DiskTree.Tests;

public class PathUtilTests
{
    [Theory]
    [InlineData(@"C:\a", @"C:\a\")]
    [InlineData(@"C:\a\", @"C:\a\")]
    [InlineData(@"C:\a\\", @"C:\a\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\srv\share", @"\\srv\share\")]
    [InlineData("", @"\")]
    public void EnsureTrailingBackslash(string path, string expected) =>
        Assert.Equal(expected, PathUtil.EnsureTrailingBackslash(path));

    [Theory]
    [InlineData(@"C:\a", @"C:\a\b", true)]
    [InlineData(@"C:\a\", @"C:\a\b\c", true)]
    [InlineData(@"C:\A", @"c:\a\b", true)]
    [InlineData(@"C:\a", @"C:\a", false)]      // the same folder is not inside itself
    [InlineData(@"C:\a", @"C:\a\", false)]
    [InlineData(@"C:\a", @"C:\ab", false)]     // separator boundary
    [InlineData(@"C:\a", @"C:\ab\c", false)]
    [InlineData(@"C:\a\b", @"C:\a", false)]
    [InlineData(@"C:\", @"C:\a", true)]
    [InlineData(@"\\srv\share\a", @"\\srv\share\ab", false)]
    [InlineData(@"\\srv\share\a", @"\\srv\share\a\b", true)]
    public void IsUnder(string parent, string child, bool expected) =>
        Assert.Equal(expected, PathUtil.IsUnder(parent, child));

    [Theory]
    [InlineData(@"C:\a", @"C:\a", true)]
    [InlineData(@"C:\a\", @"C:\a", true)]
    [InlineData(@"C:\a", @"C:\a\b", true)]
    [InlineData(@"C:\a", @"C:\ab", false)]
    [InlineData(@"C:\a\b", @"C:\a", false)]
    public void IsSameOrUnder(string parent, string child, bool expected) =>
        Assert.Equal(expected, PathUtil.IsSameOrUnder(parent, child));
}

public class FindNodeTests : TestBase
{
    [Fact]
    public async Task FindRelative_FollowsNamesCaseInsensitively()
    {
        T.File(@"a\b\c\x.txt", 1);
        var s = await ScanT();
        var a = Scan.Node(s, T.Sub("a"));

        Assert.Same(a, a.FindRelative(""));
        Assert.Same(a, a.FindRelative(@"\"));
        Assert.Equal("c", a.FindRelative(@"B\C")!.Name);
        Assert.Equal("c", a.FindRelative(@"\b\c\")!.Name);
        Assert.Null(a.FindRelative(@"b\nope"));
    }

    private static Scanner NetworkScanner()
    {
        // A tree that starts at a folder of a network share (no drive capacity), like a scan of \\srv\share\a.
        var root = new Node { Name = @"\\srv\share\a", FullPath = @"\\srv\share\a", Scanned = true };
        var b = new Node { Name = "b", FullPath = @"\\srv\share\a\b", Parent = root, Scanned = true };
        root.AddChild(b);
        return Scanner.FromSnapshot(new LoadedSnapshot
        {
            Root = root, Target = root, Top = [], Info = new SnapshotInfo(DateTime.UtcNow),
        });
    }

    [Fact]
    public void FindNode_InsideTheRoot()
    {
        var s = NetworkScanner();
        Assert.Same(s.Root, s.FindNode(@"\\srv\share\a"));
        Assert.Equal("b", s.FindNode(@"\\srv\share\a\b")!.Name);
        Assert.Null(s.FindNode(@"\\srv\share\other"));
    }

    // Known flaw (planned for a later phase): the prefix check in Scanner.FindNode has no separator boundary, so the
    // sibling "\\srv\share\ab" is taken for a path inside "\\srv\share\a" and "...\ab" + "b" even finds the child "b".
    [Fact(Skip = "Bekannter Fehler (wird separat behoben): FindNode prueft den Praefix ohne Trennzeichen-Grenze.")]
    public void FindNode_SiblingWithSamePrefix_IsNotInside()
    {
        var s = NetworkScanner();
        Assert.Null(s.FindNode(@"\\srv\share\ab"));
        Assert.Null(s.FindNode(@"\\srv\share\ab\b"));
    }
}
