using DiskTree.Features.Owner;
using Xunit;

namespace DiskTree.Tests;

public class OwnerFixTests : TestBase
{
    [Fact]
    public void Aggregate_MissingFolder_ReportsErrorInsteadOfThrowing()
    {
        var missing = T.Sub("deleted", "folder");
        var r = OwnerAggregator.Aggregate(OwnerAggregator.EnumerateFiles(missing), _ => "x", 0, 2, null, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(r.Error));
        Assert.False(r.Cancelled);
        Assert.Empty(r.Rows);
    }

    [Fact]
    public void Aggregate_Normal_HasNoError()
    {
        T.File("a.txt", 5);
        var r = OwnerAggregator.Aggregate(OwnerAggregator.EnumerateFiles(T.Path), _ => "x", 0, 2, null, CancellationToken.None);
        Assert.Null(r.Error);
        Assert.Equal(5, r.TotalBytes);
    }
}
