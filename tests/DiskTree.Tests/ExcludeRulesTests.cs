using DiskTree.Core;
using Xunit;

namespace DiskTree.Tests;

public class ExcludeRulesTests
{
    [Theory]
    [InlineData("  node_modules  ", "node_modules")]
    [InlineData("\"C:\\Windows\\Temp\"", @"C:\Windows\Temp")]
    [InlineData(@"C:\Windows\Temp\", @"C:\Windows\Temp")]
    [InlineData("C:/Windows/Temp/", @"C:\Windows\Temp")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("  \" \" ", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize(string? input, string expected) => Assert.Equal(expected, ExcludeRules.Normalize(input));

    [Theory]
    [InlineData("*", "", true)]
    [InlineData("*", "anything", true)]
    [InlineData("*.tmp", "a.TMP", true)]
    [InlineData("*.tmp", "a.tmpx", false)]
    [InlineData("cache?", "cache1", true)]
    [InlineData("cache?", "cache", false)]
    [InlineData("cache?", "cache12", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData(@"C:\Users\*\AppData", @"C:\Users\anna\AppData", true)]
    [InlineData(@"C:\Users\*\AppData", @"C:\Users\anna\x\AppData", true)] // '*' also crosses '\'
    [InlineData(@"C:\Users\*\AppData", @"C:\Users\AppData", false)]
    [InlineData("abc", "ABC", true)]
    [InlineData("abc", "abcd", false)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    public void WildcardMatch(string pattern, string text, bool expected) =>
        Assert.Equal(expected, ExcludeRules.WildcardMatch(pattern, text));

    [Fact]
    public void Empty_ExcludesNothing()
    {
        var r = new ExcludeRules(new[] { "", "  ", "\"\"" });
        Assert.True(r.IsEmpty);
        Assert.False(r.IsExcluded("anything", @"C:\"));
        Assert.True(ExcludeRules.None.IsEmpty);
    }

    [Fact]
    public void FolderName_MatchesAnywhere_CaseInsensitive()
    {
        var r = new ExcludeRules(new[] { "Node_Modules" });
        Assert.False(r.IsEmpty);
        Assert.True(r.IsExcluded("node_modules", @"C:\proj"));
        Assert.True(r.IsExcluded("NODE_MODULES", @"D:\x\y\z"));
        Assert.False(r.IsExcluded("node_modules2", @"C:\proj"));
        Assert.False(r.IsExcluded("modules", @"C:\proj"));
    }

    [Fact]
    public void NameWildcards()
    {
        var r = new ExcludeRules(new[] { "*.tmp", "cache?" });
        Assert.True(r.IsExcluded("x.tmp", @"C:\a"));
        Assert.True(r.IsExcluded("cache1", @"C:\a"));
        Assert.False(r.IsExcluded("cache", @"C:\a"));
        Assert.False(r.IsExcluded("tmp", @"C:\a"));
    }

    [Fact]
    public void FullPath_MatchesFolderAndOnlyThatPath()
    {
        var r = new ExcludeRules(new[] { @"C:\Windows\Temp\", "C:/Data/Old" });
        Assert.True(r.IsExcluded("Temp", @"C:\Windows"));
        Assert.True(r.IsExcluded("temp", @"c:\windows"));
        Assert.False(r.IsExcluded("Temp", @"C:\Other"));
        Assert.True(r.IsExcluded("Old", @"C:\Data"));
        Assert.False(r.IsExcluded("Old", @"C:\Data\Sub"));
        // a path pattern never matches by name alone
        Assert.False(r.IsExcluded("Windows", @"C:\"));
    }

    [Fact]
    public void FullPathWithWildcards()
    {
        var r = new ExcludeRules(new[] { @"C:\Users\*\AppData" });
        Assert.True(r.IsExcluded("AppData", @"C:\Users\anna"));
        Assert.False(r.IsExcluded("Documents", @"C:\Users\anna"));
        Assert.False(r.IsExcluded("AppData", @"C:\Other\anna"));
    }

    [Fact]
    public void DriveRootPatternKeepsItsBackslash()
    {
        var r = new ExcludeRules(new[] { @"C:\" });
        Assert.False(r.IsEmpty);
        Assert.False(r.IsExcluded("Windows", @"C:\")); // C:\Windows is not C:\
    }
}
