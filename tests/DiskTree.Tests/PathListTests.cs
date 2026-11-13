using System.IO;
using DiskTree.Core;
using Xunit;

namespace DiskTree.Tests;

public class PathListTests : TestBase
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ; ;; ")]
    public void Parse_EmptyInput_GivesNoPaths(string? text) => Assert.Empty(PathList.Parse(text));

    [Fact]
    public void Parse_SeveralPaths_TrimQuotesAndDuplicates()
    {
        var r = PathList.Parse(@" C:\a ; ""D:\b c"" ;; c:\A ; \\server\share\x ");
        Assert.Equal(new[] { @"C:\a", @"D:\b c", @"\\server\share\x" }, r);
    }

    [Fact]
    public void Parse_DriveLetterWithoutBackslashGetsOne()
    {
        Assert.Equal(new[] { @"C:\", @"D:\" }, PathList.Parse("C: ; D:"));
        Assert.Equal(@"C:\", PathList.Normalize("C:"));
        Assert.Equal(@"C:\", PathList.Normalize("\"C:\""));
        Assert.Equal(@"C:\x", PathList.Normalize(@" C:\x "));
    }

    [Fact]
    public void Parse_ExistingFolderWithSemicolonInItsName_StaysOnePath()
    {
        var dir = T.Dir("odd;name");
        var r = PathList.Parse(dir);
        Assert.Equal(new[] { dir }, r);
    }

    [Fact]
    public void Join_UsesSemicolonAndBlank()
    {
        Assert.Equal(@"C:\a; D:\b", PathList.Join(new[] { @"C:\a", @"D:\b" }));
        Assert.Equal("", PathList.Join(Array.Empty<string>()));
    }

    [Fact]
    public void Reduce_DropsDuplicatesAndNestedPaths()
    {
        var r = PathList.Reduce(new[] { @"C:\data\sub", @"C:\data", @"C:\DATA", @"C:\data\sub\deep", @"D:\other" });
        Assert.Equal(new[] { @"C:\data", @"D:\other" }, r.Select(p => p.TrimEnd('\\')).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\data", @"C:\DATA\")]
    [InlineData(@"C:\data\", @"C:\data")]
    [InlineData(@"C:\data", @"c:\Data\\")]
    [InlineData(@"\\srv\share\a", @"\\SRV\share\A\")]
    public void Reduce_SameFolderWithOtherCaseOrTrailingBackslash_IsOneEntry(string a, string b)
    {
        var r = PathList.Reduce(new[] { a, b });
        Assert.Single(r);
        Assert.Equal(Path.GetFullPath(a), r[0]); // the first spelling stays

        var withOther = PathList.Reduce(new[] { a, @"D:\other", b });
        Assert.Equal(2, withOther.Count);
    }

    [Fact]
    public void Reduce_SimilarPrefixIsNotNesting()
    {
        var r = PathList.Reduce(new[] { @"C:\foo", @"C:\foobar", @"C:\foo2\x" });
        Assert.Equal(3, r.Count);
    }

    [Fact]
    public void Reduce_KeepsIndependentPathsInOrder()
    {
        var r = PathList.Reduce(new[] { @"E:\z", @"C:\a", @"D:\m" });
        Assert.Equal(new[] { @"E:\z", @"C:\a", @"D:\m" }, r);
    }

    [Fact]
    public void Reduce_UncPaths()
    {
        var r = PathList.Reduce(new[] { @"\\srv\share\a", @"\\srv\share\a\b", @"\\srv\share\c" });
        Assert.Equal(new[] { @"\\srv\share\a", @"\\srv\share\c" }, r);
    }

    [Fact]
    public void Reduce_DriveRootSwallowsEverythingOnThatDrive()
    {
        var r = PathList.Reduce(new[] { @"C:\a", @"C:\" });
        Assert.Equal(new[] { @"C:\" }, r);
    }
}
