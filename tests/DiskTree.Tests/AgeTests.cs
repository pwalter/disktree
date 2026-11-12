using System.IO;
using DiskTree.Core;
using Xunit;

namespace DiskTree.Tests;

public class AgeBucketTests : TestBase
{
    private static readonly DateTime Ref = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Of_BucketBoundaries()
    {
        AgeBuckets.SetReference(Ref);
        long Cut(int months) => Ref.AddMonths(-months).Ticks;

        Assert.Equal(0, AgeBuckets.Of(Ref.Ticks));
        Assert.Equal(0, AgeBuckets.Of(Ref.AddYears(5).Ticks)); // future
        Assert.Equal(0, AgeBuckets.Of(Cut(1)));
        Assert.Equal(1, AgeBuckets.Of(Cut(1) - 1));
        Assert.Equal(1, AgeBuckets.Of(Cut(12)));
        Assert.Equal(2, AgeBuckets.Of(Cut(12) - 1));
        Assert.Equal(2, AgeBuckets.Of(Cut(24)));
        Assert.Equal(3, AgeBuckets.Of(Cut(24) - 1));
        Assert.Equal(3, AgeBuckets.Of(Cut(60)));
        Assert.Equal(4, AgeBuckets.Of(Cut(60) - 1));
        Assert.Equal(4, AgeBuckets.Of(0));
    }

    [Fact]
    public void Of_FollowsTheReferenceTime()
    {
        var file = Ref.AddMonths(-3).Ticks;
        AgeBuckets.SetReference(Ref);
        Assert.Equal(1, AgeBuckets.Of(file));
        AgeBuckets.SetReference(Ref.AddYears(3)); // three years later the same file is much older
        Assert.Equal(3, AgeBuckets.Of(file));
    }

    [Fact]
    public void Names_AndLimits_AreConsistent()
    {
        Assert.Equal(AgeBuckets.Count, AgeBuckets.Names.Length);
        Assert.Equal(AgeBuckets.Count - 1, AgeBuckets.LimitMonths.Length);
        Assert.Equal(new[] { 1, 12, 24, 60 }, AgeBuckets.LimitMonths);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(12, 2)]
    [InlineData(24, 3)]
    [InlineData(60, 4)]
    [InlineData(7, 3)]   // unknown value falls back to 24 months
    [InlineData(0, 3)]
    public void ThresholdMonths_SetsFirstOldBucket(int months, int expectedOldFrom)
    {
        AgeBuckets.ThresholdMonths = months;
        Assert.Equal(expectedOldFrom, AgeBuckets.OldFrom);
        Assert.Equal(AgeBuckets.LimitMonths[expectedOldFrom - 1], AgeBuckets.ThresholdMonths);
    }

    [Theory]
    [InlineData(1, "1 Monat")]
    [InlineData(12, "1 Jahr")]
    [InlineData(24, "2 Jahre")]
    [InlineData(60, "5 Jahre")]
    [InlineData(18, "18 Monate")]
    public void ThresholdText(int months, string expected) => Assert.Equal(expected, AgeBuckets.ThresholdText(months));

    [Fact]
    public void OldSize_UsesThreshold()
    {
        var n = new Node { AgeSub = new long[] { 1, 10, 100, 1000, 10000 }, OwnAges = new long[] { 1, 2, 4, 8, 16, 0, 0, 0, 0, 0 } };
        AgeBuckets.ThresholdMonths = 24;
        Assert.Equal(11000, AgeBuckets.OldSize(n));
        Assert.Equal(24, AgeBuckets.OldOwnSize(n));
        AgeBuckets.ThresholdMonths = 1;
        Assert.Equal(11110, AgeBuckets.OldSize(n));
        AgeBuckets.ThresholdMonths = 60;
        Assert.Equal(10000, AgeBuckets.OldSize(n));
        Assert.Equal(0, AgeBuckets.OldSize(new Node()));
        Assert.Equal(0, AgeBuckets.OldOwnSize(new Node()));
    }

    [Fact]
    public void Pack_NullWhenNoFiles_OtherwiseSizesThenCounts()
    {
        Assert.Null(AgeBuckets.Pack(new long[5], new long[5]));
        var p = AgeBuckets.Pack(new long[] { 10, 0, 0, 0, 50 }, new long[] { 1, 0, 0, 0, 2 });
        Assert.Equal(new long[] { 10, 0, 0, 0, 50, 1, 0, 0, 0, 2 }, p);
    }

    [Fact]
    public void Without_TakesOneFileOut_AndNeverGoesNegative()
    {
        var own = new long[] { 10, 0, 0, 0, 50, 1, 0, 0, 0, 2 };
        var r = AgeBuckets.Without(own, 4, 20);
        Assert.Equal(new long[] { 10, 0, 0, 0, 30, 1, 0, 0, 0, 1 }, r);
        Assert.Equal(10, own[0]); // original untouched (copy)
        Assert.Equal(50, own[4]);
        Assert.Equal(new long[] { 0, 0, 0, 0, 50, 0, 0, 0, 0, 2 }, AgeBuckets.Without(own, 0, 99));
        Assert.Same(own, AgeBuckets.Without(own, -1, 5));
        Assert.Null(AgeBuckets.Without(null, 1, 5));
    }

    [Fact]
    public void AddSub_PropagatesToAllParents_AndRemoveSubtreeUndoesIt()
    {
        var root = new Node { Name = "r" };
        var a = new Node { Name = "a", Parent = root };
        var b = new Node { Name = "b", Parent = a };
        root.AddChild(a);
        a.AddChild(b);

        AgeBuckets.AddSub(b, 2, 100);
        AgeBuckets.AddSub(b, 4, 7);
        AgeBuckets.AddSub(a, 2, 1);
        Assert.Equal(new long[] { 0, 0, 100, 0, 7 }, b.AgeSub);
        Assert.Equal(new long[] { 0, 0, 101, 0, 7 }, a.AgeSub);
        Assert.Equal(new long[] { 0, 0, 101, 0, 7 }, root.AgeSub);

        AgeBuckets.RemoveSubtree(b);
        Assert.Equal(new long[] { 0, 0, 1, 0, 0 }, a.AgeSub);
        Assert.Equal(new long[] { 0, 0, 1, 0, 0 }, root.AgeSub);
        AgeBuckets.RemoveSubtree(root); // no parent: nothing happens
        Assert.Equal(1, root.AgeSub![2]);
    }

    [Fact]
    public void FlushDelta_HandsOverAndClears()
    {
        var root = new Node();
        var d = new Node { Parent = root };
        long[] delta = { 5, 0, 0, 0, 9 };
        AgeBuckets.FlushDelta(d, delta);
        Assert.Equal(new long[5], delta);
        Assert.Equal(new long[] { 5, 0, 0, 0, 9 }, root.AgeSub);
    }

    [Fact]
    public void OfFile_ReadsTheCurrentTimestamp()
    {
        AgeBuckets.SetReference(DateTime.UtcNow);
        Assert.Equal(0, AgeBuckets.OfFile(T.File("new.txt", 1, Ago(days: 2))));
        Assert.Equal(1, AgeBuckets.OfFile(T.File("mid.txt", 1, Ago(months: 6))));
        Assert.Equal(4, AgeBuckets.OfFile(T.File("old.txt", 1, Ago(years: 9))));

        AgeStamps.Current = AgeStamp.Created; // created just now
        Assert.Equal(0, AgeBuckets.OfFile(T.Sub("old.txt")));
    }
}

public class AgeStampTests : TestBase
{
    [Fact]
    public void Keys_RoundTrip()
    {
        foreach (var s in Enum.GetValues<AgeStamp>())
            Assert.Equal(s, AgeStamps.FromKey(AgeStamps.ToKey(s)));
        Assert.Equal("modified", AgeStamps.ToKey(AgeStamp.Modified));
        Assert.Equal("created", AgeStamps.ToKey(AgeStamp.Created));
        Assert.Equal("accessed", AgeStamps.ToKey(AgeStamp.Accessed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("whatever")]
    [InlineData("MODIFIED")]
    public void FromKey_UnknownMeansModified(string? key) => Assert.Equal(AgeStamp.Modified, AgeStamps.FromKey(key));

    [Fact]
    public void Names_CoverAllStamps()
    {
        Assert.Equal(Enum.GetValues<AgeStamp>().Length, AgeStamps.Names.Length);
        Assert.Equal(AgeStamps.Names.Length, AgeStamps.Lower.Length);
        Assert.Equal("Zuletzt geändert", AgeStamps.Names[(int)AgeStamp.Modified]);
    }

    [Fact]
    public void BeginScan_AppliesPreferredAndRaisesEvent()
    {
        int raised = 0;
        void H() => raised++;
        AgeStamps.ScanStarted += H;
        try
        {
            AgeStamps.Preferred = AgeStamp.Accessed;
            AgeStamps.Current = AgeStamp.Modified;
            AgeStamps.BeginScan();
            Assert.Equal(AgeStamp.Accessed, AgeStamps.Current);
            Assert.Equal(1, raised);
        }
        finally { AgeStamps.ScanStarted -= H; }
    }

    [Fact]
    public void TicksOfFile_ReturnsTheRequestedTimestamp()
    {
        var f = T.File("t.txt", 1);
        var written = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var created = new DateTime(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var accessed = new DateTime(2021, 9, 8, 7, 6, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(f, created);
        File.SetLastWriteTimeUtc(f, written);
        File.SetLastAccessTimeUtc(f, accessed);

        Assert.Equal(written.Ticks, AgeStamps.TicksOfFile(f, AgeStamp.Modified));
        Assert.Equal(created.Ticks, AgeStamps.TicksOfFile(f, AgeStamp.Created));
        Assert.Equal(accessed.Ticks, AgeStamps.TicksOfFile(f, AgeStamp.Accessed));
    }

    [Fact]
    public void AccessTimeInfo_HasAWarningText()
    {
        Assert.False(string.IsNullOrWhiteSpace(AccessTimeInfo.Warning));
        Assert.Equal(AccessTimeInfo.Current != AccessTimeInfo.State.Enabled, AccessTimeInfo.IsSevere);
    }
}
