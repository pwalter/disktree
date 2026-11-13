using System.Security.Principal;
using DiskTree.Infrastructure;
using Xunit;

namespace DiskTree.Tests;

public class AdminHelperTests
{
    [Fact]
    public void IsAdmin_MatchesWindowsAndIsStable()
    {
        using var id = WindowsIdentity.GetCurrent();
        bool expected = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        Assert.Equal(expected, AdminHelper.IsAdmin);
        Assert.Equal(expected, AdminHelper.IsAdmin); // cached: the same answer every time
    }
}
