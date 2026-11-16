using System.Diagnostics;
using System.IO;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.Storage.Sftp;
using Xunit;
using Xunit.Abstractions;

namespace DiskTree.Tests.Storage;

/// <summary>
/// The SFTP provider against a real OpenSSH server (atmoz/sftp in Docker, see <see cref="SftpTestServer"/>): the real
/// <see cref="Scanner"/> with <c>Provider = SftpProvider</c>, logins, host keys, cancel, broken connections.
/// Only with DISKTREE_SFTP_TESTS=1.
/// </summary>
[Collection(SftpCollection.Name)]
public class SftpIntegrationTests(SftpTestServer server, ITestOutputHelper output)
{
    private static async Task<Scanner> ScanAsync(SftpProvider p, string path, int threads = 8, CancellationToken ct = default)
    {
        var s = new Scanner { Provider = p };
        await s.ScanAsync(path, threads, ct);
        return s;
    }

    private static string[] Names(Node n) => n.Snapshot().Select(c => c.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static Node Child(Node n, string name) => n.Snapshot().Single(c => c.Name == name);

    // ------------------------------------------------------------------ scan

    [SftpFact]
    public async Task Scan_MatchesWhatTheServerCounts()
    {
        using var p = new SftpProvider(server.Options());
        var sw = Stopwatch.StartNew();
        var s = await ScanAsync(p, server.DataUri);
        output.WriteLine($"Scan /data: {sw.ElapsedMilliseconds} ms, {s.Files} Dateien, {s.Dirs} Ordner, {s.Bytes} Bytes, {s.Errors} Fehler");

        // statvfs: the tree starts at the root "/" with the capacity, /data hangs below it.
        var top = s.Root!;
        Assert.Equal(server.RootUri, top.FullPath);
        Assert.True(top.DriveTotal > 0);
        Assert.InRange(top.DriveTotal / 1024, server.DfTotalKb - 1, server.DfTotalKb + 1);
        Assert.True(top.PartialDrive);
        var data = s.ScanTarget!;
        Assert.Equal(server.DataUri, data.FullPath);
        Assert.Equal("data", data.Name);

        // Sizes and counts as find/stat in the container see them; links are 0-byte files, "geheim" is unreadable.
        Assert.Equal(server.ExpectedBytes, data.Size);
        Assert.Equal(server.ExpectedBytes, s.Bytes);
        Assert.Equal(server.ExpectedFiles + server.ExpectedLinks, data.FileCount);
        Assert.Equal(server.ExpectedDirs + 1, s.Dirs);   // + "geheim", which was tried
        Assert.Equal(1, s.Errors);
        Assert.True(data.IsDone);

        Assert.Equal(new[] { ".versteckt", "CaseTest", "Ordner mit Leerzeichen", "ebene1", "geheim", "leer", "masse", "Übergröße äöü ß" },
            Names(data));
        Assert.Equal(new[] { "A", "a" }, Names(Child(data, "CaseTest")));   // case-sensitive
        Assert.Equal(3_000_777, Child(data, "Übergröße äöü ß").Size);
        Assert.Equal(1000 + 2500 + 123456 + 4, Child(data, "ebene1").Size);
        Assert.Equal(0, Child(data, "geheim").Size);
        Assert.Equal(server.DataUri + "/ebene1/ebene2/ebene3", Scan.Node(s, server.DataUri + "/ebene1/ebene2/ebene3").FullPath);
        Assert.Equal(5000, Child(data, "masse").FileCount);
    }

    [SftpFact]
    public void Enumerate_ReportsLinksTimesAndHiddenEntries()
    {
        using var p = new SftpProvider(server.Options());
        var all = p.Enumerate(server.DataUri, (in StorageEntry e) => (Name: e.Name.ToString(), e.IsDirectory, e.IsReparsePoint, e.Length,
            e.Attributes, e.ModifiedTicks, e.CreatedTicks, e.AccessedTicks), ListOptions.Strict).ToDictionary(e => e.Name);

        Assert.DoesNotContain(".", all.Keys);
        Assert.DoesNotContain("..", all.Keys);
        foreach (var link in new[] { "link_ordner", "link_datei", "kaputter_link" })
        {
            Assert.True(all[link].IsReparsePoint, link);
            Assert.False(all[link].IsDirectory, link);
            Assert.Equal(0, all[link].Length);
            Assert.Equal(FileAttributes.ReparsePoint, all[link].Attributes);
        }
        Assert.True(all["ebene1"].IsDirectory);
        Assert.Equal(FileAttributes.Directory, all["ebene1"].Attributes);
        Assert.Equal(FileAttributes.Directory | FileAttributes.Hidden, all[".versteckt"].Attributes);
        Assert.Equal(0, all["ebene1"].CreatedTicks);
        Assert.True(all["ebene1"].AccessedTicks > 0);

        var files = p.Enumerate(server.DataUri + "/ebene1", (in StorageEntry e) => (Name: e.Name.ToString(), e.Length, e.ModifiedTicks, e.AccessedTicks),
            new ListOptions { Stamps = StampSet.Modified }).ToDictionary(e => e.Name);
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks, files["alt.txt"].ModifiedTicks);
        Assert.Equal(0, files["alt.txt"].AccessedTicks);   // not asked for
        Assert.Equal(1000, files["a.bin"].Length);
    }

    [SftpFact]
    public void Enumerate_UnreadableFolder_ThrowsOrIsSkipped()
    {
        using var p = new SftpProvider(server.Options());
        Assert.Throws<UnauthorizedAccessException>(() => p.Enumerate(server.DataUri + "/geheim", (in StorageEntry e) => 0, ListOptions.Strict).ToList());
        Assert.Empty(p.Enumerate(server.DataUri + "/geheim", (in StorageEntry e) => 0, ListOptions.Lenient));
        Assert.Throws<DirectoryNotFoundException>(() => p.Enumerate(server.DataUri + "/gibt-es-nicht", (in StorageEntry e) => 0, ListOptions.Strict).ToList());
        Assert.True(p.FolderExists(server.DataUri + "/ebene1"));
        Assert.True(p.FolderExists(server.DataUri + "/link_ordner"));   // like the disk: a link to a folder is a folder
        Assert.False(p.FolderExists(server.DataUri + "/ebene1/a.bin"));
        Assert.False(p.FolderExists(server.DataUri + "/gibt-es-nicht"));
    }

    [SftpFact]
    public void OpenRead_ReadsTheContent()
    {
        using var p = new SftpProvider(server.Options());
        using (var s = p.OpenRead(server.DataUri + "/CaseTest/A/x.txt"))
        using (var r = new StreamReader(s))
            Assert.Equal("GROSSBUCHSTABEN\n", r.ReadToEnd());
        using (var s = p.OpenRead(server.DataUri + "/Übergröße äöü ß/groß.bin"))
        {
            long total = 0;
            var buf = new byte[81920];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) total += n;
            Assert.Equal(3_000_000, total);
        }
        Assert.ThrowsAny<IOException>(() => p.OpenRead(server.DataUri + "/nicht-da.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => p.OpenRead(server.DataUri + "/geheim/secret.bin"));
    }

    [SftpFact]
    public void Capacity_OnlyForTheRoot()
    {
        using var p = new SftpProvider(server.Options());
        Assert.True(p.TryGetCapacity(server.RootUri, out long total, out long free));
        Assert.True(total > 0 && free > 0 && free <= total);
        Assert.False(p.TryGetCapacity(server.DataUri, out _, out _));

        var root = p.ResolveRoot(server.DataUri + "/ebene1/");
        Assert.Equal(server.DataUri + "/ebene1", root.Target);
        Assert.Equal(server.RootUri, root.Top);
        Assert.True(root.HasCapacity);
    }

    [SftpFact]
    public async Task Scan_OfTheRoot_UsesTheWholeFileSystem()
    {
        using var p = new SftpProvider(server.Options());
        var s = await ScanAsync(p, server.RootUri);
        Assert.Same(s.Root, s.ScanTarget);
        Assert.False(s.Root!.PartialDrive);
        Assert.True(s.ExpectedBytes > 0);
        Assert.Contains("data", Names(s.Root));
    }

    // ------------------------------------------------------------------ login

    [SftpFact]
    public void Login_WithEd25519Key()
    {
        using var p = new SftpProvider(server.Options(SftpAuth.KeyText(server.KeyPlain)));
        p.Connect(CancellationToken.None);
        Assert.True(p.IsConnected);
        Assert.StartsWith("SSH-2.0-OpenSSH", p.ServerVersion);
        Assert.Contains("data", p.Enumerate(server.RootUri, (in StorageEntry e) => e.Name.ToString(), ListOptions.Strict));
    }

    [SftpFact]
    public void Login_WithEncryptedKey_NeedsTheRightPassphrase()
    {
        using (var p = new SftpProvider(server.Options(SftpAuth.KeyText(server.KeyEncrypted, SftpTestServer.KeyPassphrase))))
        {
            p.Connect(CancellationToken.None);
            Assert.True(p.IsConnected);
        }

        var missing = Assert.Throws<SftpLoginException>(() => new SftpProvider(server.Options(SftpAuth.KeyText(server.KeyEncrypted))).Connect(CancellationToken.None));
        Assert.Contains("Passphrase", missing.Message);
        var wrong = Assert.Throws<SftpLoginException>(() => new SftpProvider(server.Options(SftpAuth.KeyText(server.KeyEncrypted, "Nicht-die-Passphrase"))).Connect(CancellationToken.None));
        Assert.DoesNotContain("Nicht-die-Passphrase", wrong.ToString());
    }

    [SftpFact]
    public void Login_WithKeyFile()
    {
        var file = Path.Combine(Path.GetTempPath(), "disktree-sftp-it-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, server.KeyPlain);
        try
        {
            using var p = new SftpProvider(server.Options(SftpAuth.KeyFile(file)));
            p.Connect(CancellationToken.None);
            Assert.True(p.IsConnected);
        }
        finally
        {
            File.Delete(file);
        }
        Assert.Throws<SftpLoginException>(() => new SftpProvider(server.Options(SftpAuth.KeyFile(file))).Connect(CancellationToken.None));
    }

    [SftpFact]
    public async Task Login_WrongPassword_IsALoginError_WithoutTheSecret()
    {
        using var p = new SftpProvider(server.Options(SftpAuth.Password("falsches-Passwort")));
        var ex = Assert.Throws<SftpLoginException>(() => p.Connect(CancellationToken.None));
        Assert.IsAssignableFrom<UnauthorizedAccessException>(ex);
        Assert.DoesNotContain("falsches-Passwort", ex.ToString());
        Assert.DoesNotContain(server.Password, ex.ToString());

        // In a scan the login already fails while the tree is set up.
        await Assert.ThrowsAsync<SftpLoginException>(() => ScanAsync(p, server.DataUri));
    }

    // ------------------------------------------------------------------ host keys

    [SftpFact]
    public void HostKey_Unknown_WithoutCallback_CarriesTheFingerprint()
    {
        using var p = new SftpProvider(server.Options(hostKey: null));
        var ex = Assert.Throws<SftpHostKeyException>(() => p.Connect(CancellationToken.None));
        Assert.Equal(SftpHostKeyProblem.Unknown, ex.Problem);
        Assert.Contains(ex.Fingerprint, server.HostKeys);
        Assert.False(p.IsConnected);
        output.WriteLine("Host-Key: " + ex.Fingerprint);
    }

    [SftpFact]
    public void HostKey_Unknown_AskedOnce_AndKeptForReconnects()
    {
        var asked = new List<string>();
        using var p = new SftpProvider(server.Options(hostKey: null, confirm: f => { asked.Add(f); return true; }));
        p.Connect(CancellationToken.None);
        Assert.Equal(new[] { server.TrustedHostKey }, asked);
        Assert.Equal(server.TrustedHostKey, p.HostKeySha256);

        p.Disconnect();
        p.Connect(CancellationToken.None);
        Assert.Single(asked);   // the accepted key is known now
    }

    [SftpFact]
    public void HostKey_Rejected_ByTheUser()
    {
        using var p = new SftpProvider(server.Options(hostKey: null, confirm: _ => false));
        var ex = Assert.Throws<SftpHostKeyException>(() => p.Connect(CancellationToken.None));
        Assert.Equal(SftpHostKeyProblem.Unknown, ex.Problem);
        Assert.Null(p.HostKeySha256);
    }

    [SftpFact]
    public void HostKey_Changed_IsAHardError_NeverAsked()
    {
        bool asked = false;
        const string old = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        using var p = new SftpProvider(server.Options(hostKey: old, confirm: _ => asked = true));
        var ex = Assert.Throws<SftpHostKeyException>(() => p.Connect(CancellationToken.None));
        Assert.Equal(SftpHostKeyProblem.Changed, ex.Problem);
        Assert.Equal(server.TrustedHostKey, ex.Fingerprint);
        Assert.Equal(old, ex.Expected);
        Assert.False(asked);

        // A listing fails the same way (an IOException: "nicht lesbar" in a scan).
        Assert.Throws<SftpHostKeyException>(() => p.Enumerate(server.DataUri, (in StorageEntry e) => 0, ListOptions.Strict).ToList());
    }

    [SftpFact]
    public void TestConnection_ReportsKeyAndRoot()
    {
        var r = SftpProvider.TestConnection(server.Options(hostKey: null, confirm: _ => true));
        Assert.Equal(server.TrustedHostKey, r.HostKeySha256);
        Assert.Equal(server.RootUri, r.Folder);
        Assert.Contains("data/", r.Sample);
        Assert.True(r.Total > 0);
        output.WriteLine($"{r.ServerVersion}, verbunden in {r.ConnectTime.TotalMilliseconds:0} ms");

        var sub = SftpProvider.TestConnection(server.Options(), "/data/ebene1");
        Assert.Equal(server.DataUri + "/ebene1", sub.Folder);
        Assert.Equal(new[] { "ebene2/", "a.bin", "alt.txt" }, sub.Sample);
        Assert.Throws<UnauthorizedAccessException>(() => SftpProvider.TestConnection(server.Options(), "/data/geheim"));
    }

    // ------------------------------------------------------------------ parallel, latency, cancel, broken connections

    [SftpFact]
    public async Task ManyScannerThreads_UseAtMostTheConfiguredRequests()
    {
        using var proxy = new LatencyProxy(server.Port, TimeSpan.FromMilliseconds(5));
        using var p = new SftpProvider(server.Options(port: proxy.Port, parallel: 3));
        p.Connect(CancellationToken.None);
        var conn = (SftpConnection)typeof(SftpProvider).GetField("_connection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(p)!;

        int max = 0;
        using var stop = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                max = Math.Max(max, conn.Busy);
                await Task.Delay(1);
            }
        });
        var s = await ScanAsync(p, p.RootUri + "data/masse", threads: 16);
        stop.Cancel();
        await watch;

        Assert.Equal(5000, s.ScanTarget!.FileCount);
        Assert.InRange(max, 1, 3);
        Assert.Equal(1, proxy.Connections);   // one connection for everything
    }

    /// <summary>Not an assertion of speed (machines differ), but the numbers for the PR: 1 vs. 8 parallel listings at ~20 ms RTT.</summary>
    [SftpFact]
    public async Task Latency_ParallelListingsPayOff()
    {
        using var proxy = new LatencyProxy(server.Port, TimeSpan.FromMilliseconds(10));
        var times = new Dictionary<int, TimeSpan>();
        foreach (int parallel in new[] { 1, 8 })
        {
            using var p = new SftpProvider(server.Options(port: proxy.Port, parallel: parallel));
            p.Connect(CancellationToken.None);
            var sw = Stopwatch.StartNew();
            var s = await ScanAsync(p, (p.RootUri + "data"), threads: 8);
            times[parallel] = sw.Elapsed;
            Assert.Equal(server.ExpectedBytes, s.ScanTarget!.Size);
            output.WriteLine($"20 ms RTT, {parallel} parallel: {sw.ElapsedMilliseconds} ms für {s.Dirs} Ordner / {s.Files} Dateien");
        }
        Assert.True(times[8] < times[1], $"8 parallel ({times[8]}) sollte schneller sein als 1 ({times[1]})");
    }

    [SftpFact]
    public async Task Cancel_EndsTheScan_AndAbortsRunningRequests()
    {
        using var proxy = new LatencyProxy(server.Port, TimeSpan.FromMilliseconds(25));
        using var p = new SftpProvider(server.Options(port: proxy.Port));
        p.Connect(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        using var _ = p.CancelWith(cts.Token);
        var s = new Scanner { Provider = p };
        var scan = s.ScanAsync((p.RootUri + "data"), 8, cts.Token);
        await Task.Delay(300);
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        output.WriteLine($"Abbruch nach {sw.ElapsedMilliseconds} ms, {s.Dirs} Ordner gelesen");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(s.Dirs < server.ExpectedDirs);

        // The provider is usable again afterwards (a new connection).
        var again = await ScanAsync(p, (p.RootUri + "data") + "/ebene1");
        Assert.Equal(1000 + 2500 + 123456 + 4, again.ScanTarget!.Size);
    }

    [SftpFact]
    public async Task ConnectionDrop_MidScan_ReconnectsOnce_AndFinishes()
    {
        using var proxy = new LatencyProxy(server.Port, TimeSpan.FromMilliseconds(10));
        using var p = new SftpProvider(server.Options(port: proxy.Port));
        p.Connect(CancellationToken.None);

        var s = new Scanner { Provider = p };
        var scan = s.ScanAsync((p.RootUri + "data"), 8, CancellationToken.None);
        while (s.Dirs < 20 && !scan.IsCompleted) await Task.Delay(5);
        proxy.DropAll();
        await scan;

        output.WriteLine($"Verbindungen: {proxy.Connections}, Fehler: {s.Errors}");
        Assert.Equal(2, proxy.Connections);
        Assert.Equal(1, s.Errors);   // only "geheim": the folders hit by the drop were read again
        Assert.Equal(server.ExpectedBytes, s.ScanTarget!.Size);
        Assert.Equal(server.ExpectedFiles + server.ExpectedLinks, s.ScanTarget.FileCount);
    }

    [SftpFact]
    public async Task ServerSessionsKilled_MidScan_ReconnectsAndFinishes()
    {
        using var p = new SftpProvider(server.Options());
        p.Connect(CancellationToken.None);
        server.KillSessions();
        var s = await ScanAsync(p, server.DataUri);
        Assert.Equal(1, s.Errors);
        Assert.Equal(server.ExpectedBytes, s.ScanTarget!.Size);
    }

    [SftpFact]
    public async Task ServerPaused_MidScan_FoldersCountAsUnreadable_ScanEnds()
    {
        using var proxy = new LatencyProxy(server.Port, TimeSpan.FromMilliseconds(10));
        using var p = new SftpProvider(server.Options(port: proxy.Port,
            operationTimeout: TimeSpan.FromSeconds(2), connectTimeout: TimeSpan.FromSeconds(2)));
        p.Connect(CancellationToken.None);

        var s = new Scanner { Provider = p };
        var sw = Stopwatch.StartNew();
        var scan = s.ScanAsync((p.RootUri + "data"), 8, CancellationToken.None);
        while (s.Dirs < 20 && !scan.IsCompleted) await Task.Delay(5);
        server.Pause();
        try
        {
            await scan.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            server.Unpause();
        }
        output.WriteLine($"Pause: Scan nach {sw.ElapsedMilliseconds} ms beendet, {s.Errors} nicht lesbar, {s.Dirs} Ordner");
        Assert.True(s.Errors > 1);
        Assert.True(s.ScanTarget!.Size < server.ExpectedBytes);
        Assert.True(s.Root!.IsDone);

        // After the server is back, an explicit Connect clears the pause and the next scan is complete again.
        p.Connect(CancellationToken.None);
        var again = await ScanAsync(p, (p.RootUri + "data"));
        Assert.Equal(1, again.Errors);
        Assert.Equal(server.ExpectedBytes, again.ScanTarget!.Size);
    }
}
