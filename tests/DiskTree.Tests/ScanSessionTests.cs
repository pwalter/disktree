using DiskTree.Features.Scan;
using Xunit;

namespace DiskTree.Tests;

public class ScanSessionTests
{
    [Fact]
    public void Pending_CountsAsRunning_WhileNoScanHasACts()
    {
        var s = new ScanSession();
        Assert.False(s.IsRunningOrPending);

        s.Pending = true; // the old scan stopped, the new one has not started yet (Cts is null in this gap)
        Assert.Null(s.Cts);
        Assert.True(s.IsRunningOrPending);
        Assert.False(s.IsBusy);

        s.Pending = false;
        s.Cts = new CancellationTokenSource();
        Assert.True(s.IsRunningOrPending);
        s.Cts.Dispose();
        s.Cts = null;
        Assert.False(s.IsRunningOrPending);
    }
}
