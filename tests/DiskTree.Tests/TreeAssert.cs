using Xunit;

namespace DiskTree.Tests;

/// <summary>Deep comparison of two node trees (numbers, types, age buckets), used to prove an incremental update equals a fresh scan.</summary>
public static class TreeAssert
{
    private static long[] Ages(long[]? a, int len) => a ?? new long[len];

    public static void Equal(Node expected, Node actual, string where = "")
    {
        var at = where.Length == 0 ? expected.FullPath : where;
        Assert.True(expected.Name == actual.Name, $"{at}: Name {expected.Name} != {actual.Name}");
        Assert.True(expected.Size == actual.Size, $"{at}: Size {expected.Size} != {actual.Size}");
        Assert.True(expected.FileCount == actual.FileCount, $"{at}: FileCount {expected.FileCount} != {actual.FileCount}");
        Assert.True(expected.DirCount == actual.DirCount, $"{at}: DirCount {expected.DirCount} != {actual.DirCount}");
        Assert.True(expected.OwnSize == actual.OwnSize, $"{at}: OwnSize {expected.OwnSize} != {actual.OwnSize}");
        Assert.True(expected.OwnCount == actual.OwnCount, $"{at}: OwnCount {expected.OwnCount} != {actual.OwnCount}");
        Assert.True(expected.Excluded == actual.Excluded, $"{at}: Excluded {expected.Excluded} != {actual.Excluded}");
        Assert.Equal(Ages(expected.AgeSub, AgeBuckets.Count), Ages(actual.AgeSub, AgeBuckets.Count));
        Assert.Equal(Ages(expected.OwnAges, AgeBuckets.Count * 2), Ages(actual.OwnAges, AgeBuckets.Count * 2));
        Assert.Equal((expected.OwnTypes ?? Array.Empty<TypeStat>()).OrderBy(t => t.Cat), (actual.OwnTypes ?? Array.Empty<TypeStat>()).OrderBy(t => t.Cat));

        var e = expected.Snapshot().OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var a = actual.Snapshot().OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(e.Select(n => n.Name).SequenceEqual(a.Select(n => n.Name), StringComparer.OrdinalIgnoreCase),
            $"{at}: Unterordner {string.Join(",", e.Select(n => n.Name))} != {string.Join(",", a.Select(n => n.Name))}");
        for (int i = 0; i < e.Length; i++) Equal(e[i], a[i]);
    }
}
