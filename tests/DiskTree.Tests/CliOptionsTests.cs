using DiskTree.Core;
using DiskTree.Infrastructure;
using Xunit;

namespace DiskTree.Tests;

public class CliOptionsTests : IDisposable
{
    public CliOptionsTests() => Portable.ForcedByFlag = false;
    public void Dispose() => Portable.ForcedByFlag = false;

    private static CliOptions P(params string[] args) => CliOptions.Parse(args);

    [Fact]
    public void NoArguments_AllDefaults()
    {
        var o = P();
        Assert.Empty(o.Paths);
        Assert.Null(o.Path);
        Assert.Null(o.Threads);
        Assert.Empty(o.Excludes);
        Assert.False(o.Admin);
        Assert.False(o.NoAdminPrompt);
        Assert.False(o.NoUpdateCheck);
        Assert.False(o.Portable);
        Assert.False(o.Help);
        Assert.Null(o.Error);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("/?")]
    [InlineData("-?")]
    [InlineData("/h")]
    [InlineData("--HELP")]
    public void Help_Variants(string flag) => Assert.True(P(flag).Help);

    [Theory]
    [InlineData("--admin")]
    [InlineData("/admin")]
    [InlineData("--ADMIN")]
    public void Admin_Variants(string flag) => Assert.True(P(flag).Admin);

    [Fact]
    public void Flags()
    {
        var o = P("--no-admin-prompt", "--no-update-check", "--portable");
        Assert.True(o.NoAdminPrompt);
        Assert.True(o.NoUpdateCheck);
        Assert.True(o.Portable);
        Assert.False(o.Admin);
        Assert.Null(o.Error);
    }

    [Theory]
    [InlineData("-t", "8", 8)]
    [InlineData("--threads", "16", 16)]
    [InlineData("/t", "1", 1)]
    [InlineData("--THREADS", "256", 256)]
    public void Threads_SeparateValue(string flag, string value, int expected) => Assert.Equal(expected, P(flag, value).Threads);

    [Theory]
    [InlineData("--threads=32", 32)]
    [InlineData("--Threads=2", 2)]
    public void Threads_EqualsForm(string arg, int expected) => Assert.Equal(expected, P(arg).Threads);

    [Theory]
    [InlineData("--threads")]
    [InlineData("--threads", "0")]
    [InlineData("--threads", "257")]
    [InlineData("--threads", "-3")]
    [InlineData("--threads", "abc")]
    [InlineData("--threads=")]
    [InlineData("--threads=0")]
    [InlineData("--threads=999")]
    [InlineData("-t")]
    public void Threads_Errors(params string[] args)
    {
        var o = P(args);
        Assert.NotNull(o.Error);
        Assert.Contains("--threads", o.Error);
        Assert.Null(o.Threads);
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("--exclude")]
    [InlineData("--exclude", "")]
    [InlineData("--exclude", "   ")]
    [InlineData("--exclude", "\"\"")]
    [InlineData("--exclude=")]
    [InlineData("/x")]
    public void Exclude_Errors(params string[] args)
    {
        var o = P(args);
        Assert.NotNull(o.Error);
        Assert.Contains("--exclude", o.Error);
        Assert.Empty(o.Excludes);
    }

    [Fact]
    public void Exclude_MultipleAndAllForms()
    {
        var o = P("-x", "node_modules", "--exclude", "*.tmp", "/x", @"C:\Windows\Temp", "--exclude=cache?");
        Assert.Equal(new[] { "node_modules", "*.tmp", @"C:\Windows\Temp", "cache?" }, o.Excludes);
        Assert.Null(o.Error);
        Assert.Empty(o.Paths);
    }

    [Fact]
    public void Paths_SimpleAndSeveral()
    {
        var o = P(@"C:\", @"D:\Daten", @"\\server\share\Ordner");
        Assert.Equal(new[] { @"C:\", @"D:\Daten", @"\\server\share\Ordner" }, o.Paths);
        Assert.Equal(@"C:\;D:\Daten;\\server\share\Ordner", o.Path);
    }

    [Fact]
    public void Paths_WithSpacesAndQuotes()
    {
        var o = P("\"D:\\Mein Ordner\"", "  E:\\Mit Leerzeichen  ");
        Assert.Equal(new[] { @"D:\Mein Ordner", @"E:\Mit Leerzeichen" }, o.Paths);
    }

    [Fact]
    public void Paths_DriveRootWithQuoteFromEscapedBackslash()
    {
        // `DiskTree.exe "C:\"` arrives as  C:"  because \" is an escaped quote
        var o = P("C:\"");
        Assert.Equal(new[] { "C:" }, o.Paths);
        // ...and the path box turns it back into a drive root
        Assert.Equal(new[] { @"C:\" }, PathList.Parse(o.Path));
    }

    [Fact]
    public void Paths_SlashAtStartIsAPathWhenLong()
    {
        Assert.Equal(new[] { "//server/share" }, P("//server/share").Paths);
        Assert.Equal(new[] { @"/ab\c" }, P(@"/ab\c").Paths);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-z")]
    [InlineData("/z")]
    [InlineData("/ab")]
    [InlineData("--")]
    public void UnknownOption_IsAnError(string arg)
    {
        var o = P(arg);
        Assert.NotNull(o.Error);
        Assert.Contains("Unbekannte Option", o.Error);
        Assert.Contains(arg, o.Error);
    }

    [Fact]
    public void FirstErrorWins_ParsingStops()
    {
        var o = P("--threads", "x", @"C:\data", "--bogus");
        Assert.Contains("--threads", o.Error);
        Assert.Empty(o.Paths);
    }

    [Fact]
    public void Everything_Combined()
    {
        var o = P(@"D:\Daten", "\"E:\\Back up\"", "--threads", "32", "-x", "node_modules", "--admin", "--no-admin-prompt", "--no-update-check", "--portable");
        Assert.Equal(new[] { @"D:\Daten", @"E:\Back up" }, o.Paths);
        Assert.Equal(32, o.Threads);
        Assert.Equal(new[] { "node_modules" }, o.Excludes);
        Assert.True(o.Admin && o.NoAdminPrompt && o.NoUpdateCheck && o.Portable);
        Assert.Null(o.Error);
        Assert.False(o.Help);
    }

    [Fact]
    public void ForwardArgs_PassesEverythingExceptAdmin()
    {
        var o = P(@"D:\Daten", "--threads", "8", "-x", "a", "-x", "b", "--admin", "--no-admin-prompt");
        Assert.Equal(new[] { @"D:\Daten", "--threads", "8", "--exclude", "a", "--exclude", "b" }, o.ForwardArgs().ToArray());
    }

    [Fact]
    public void ForwardArgs_PathOverrideAndPortable()
    {
        var o = P(@"D:\Daten", @"E:\x");
        Portable.ForcedByFlag = true;
        Assert.Equal(new[] { @"F:\y", "--portable" }, o.ForwardArgs(@"F:\y").ToArray());
        Assert.Equal(new[] { @"D:\Daten", @"E:\x", "--portable" }, o.ForwardArgs().ToArray());
    }

    [Fact]
    public void ForwardArgs_RoundTripsThroughParse()
    {
        var o = P(@"D:\Mein Ordner", @"E:\x", "--threads", "4", "--exclude", "*.tmp");
        var again = P(o.ForwardArgs().ToArray());
        Assert.Equal(o.Paths, again.Paths);
        Assert.Equal(o.Threads, again.Threads);
        Assert.Equal(o.Excludes, again.Excludes);
    }

    [Fact]
    public void Usage_MentionsEveryOption()
    {
        foreach (var opt in new[] { "--threads", "--exclude", "--admin", "--no-admin-prompt", "--no-update-check", "--portable", "--help" })
            Assert.Contains(opt, CliOptions.Usage);
    }
}
