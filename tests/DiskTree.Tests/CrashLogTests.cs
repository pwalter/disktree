using System.IO;
using DiskTree.Infrastructure;
using Xunit;

namespace DiskTree.Tests;

[Collection("CrashLog")]
public class CrashLogTests : TestBase
{
    public CrashLogTests() => CrashLog.FileOverride = T.Sub("data", CrashLog.FileName);

    public override void Dispose()
    {
        CrashLog.FileOverride = null;
        base.Dispose();
    }

    [Fact]
    public void Write_CreatesFolderAndAppendsEntries()
    {
        Assert.True(CrashLog.Write("quelle-1", new InvalidOperationException("boom1")));
        Assert.True(CrashLog.Write("quelle-2", new InvalidOperationException("boom2")));
        var text = File.ReadAllText(CrashLog.FilePath);
        Assert.Contains("quelle-1", text);
        Assert.Contains("boom1", text);
        Assert.Contains("boom2", text);
    }

    [Fact]
    public void Write_UnwritableTarget_IsIgnored()
    {
        File.WriteAllText(T.Sub("blocker"), "x");
        // a file where the folder should be: CreateDirectory fails, Write must not throw
        Assert.False(CrashLog.WriteTo(Path.Combine(T.Sub("blocker"), "crash.log"), "q", new Exception("e")));
    }

    [Fact]
    public async Task Forget_LogsFaultAndCallsHandler_ButIgnoresCancellation()
    {
        var seen = new TaskCompletionSource<Exception>();
        static async Task Fail() { await Task.Yield(); throw new InvalidOperationException("kaputt-xyz"); }
        Fail().Forget("TestQuelle", ex => seen.TrySetResult(ex));
        var ex = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("kaputt-xyz", ex.Message);
        var text = File.ReadAllText(CrashLog.FilePath);
        Assert.Contains("TestQuelle", text);
        Assert.Contains("kaputt-xyz", text);

        bool called = false;
        static async Task Cancel() { await Task.Yield(); throw new OperationCanceledException(); }
        var t = Cancel();
        t.Forget("Abbruch", _ => called = true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
        await Task.Delay(100);
        Assert.False(called);
    }
}
