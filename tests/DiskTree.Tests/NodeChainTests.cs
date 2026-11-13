using DiskTree.Core;
using Xunit;

namespace DiskTree.Tests;

/// <summary>NodeChain: the path of folders the tree view opens to show a folder (treemap click, search hit, comparison jump).</summary>
public class NodeChainTests
{
    private static Node Child(Node parent, string name)
    {
        var n = new Node { Name = name, Parent = parent };
        parent.AddChild(n);
        return n;
    }

    [Fact]
    public void Between_ReturnsTheFoldersFromRootToTarget()
    {
        var drive = new Node { Name = @"C:\" };
        var a = Child(drive, "a");
        var b = Child(a, "b");

        Assert.Equal(new[] { drive, a, b }, NodeChain.Between(drive, b));
    }

    [Fact]
    public void Between_TargetIsRoot_IsJustTheRoot()
    {
        var drive = new Node { Name = @"C:\" };

        Assert.Equal(new[] { drive }, NodeChain.Between(drive, drive));
    }

    [Fact]
    public void Between_StartsAtTheGivenRootNotAtTheTopOfTheTree()
    {
        var drive = new Node { Name = @"C:\" };
        var a = Child(drive, "a");
        var b = Child(a, "b");

        Assert.Equal(new[] { a, b }, NodeChain.Between(a, b));
    }

    [Fact]
    public void Between_TargetOutsideTheRoot_IsNull()
    {
        var drive = new Node { Name = @"C:\" };
        var a = Child(drive, "a");
        var other = Child(drive, "other");

        Assert.Null(NodeChain.Between(a, other));
        Assert.Null(NodeChain.Between(a, drive)); // above the root
    }
}
