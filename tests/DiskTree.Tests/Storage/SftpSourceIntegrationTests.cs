using System.IO;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.Storage.Sftp;
using Xunit;
using Xunit.Abstractions;

namespace DiskTree.Tests.Storage;

/// <summary>
/// SFTP as a saved source against the throw-away atmoz/sftp container (<see cref="SftpTestServer"/>): connection test,
/// host-key flow (unknown -> trusted -> stored; changed -> refused without asking), password and key file, "~", a scan
/// through the path box resolution and cancelling it. Only with DISKTREE_SFTP_TESTS=1.
/// </summary>
[Collection(SourceStateCollection.Name)]
public sealed class SftpSourceIntegrationTests : IDisposable
{
    private readonly SftpTestServer _server;
    private readonly ITestOutputHelper _output;
    private readonly ISecretStore _oldStore = SourceSecrets.Store;
    private readonly MemorySecretStore _store = new();
    private readonly List<string> _asked = new();
    private readonly List<(string Id, string Key)> _stored = new();

    public SftpSourceIntegrationTests(SftpTestServer server, ITestOutputHelper output)
    {
        _server = server;
        _output = output;
        SourceSecrets.Store = _store;
        SourceProfiles.PreferredId = null;
        SourceProfiles.Set([]);
        SftpStorage.Register();
        SftpStorage.AskHostKey = (_, fp) => { lock (_asked) _asked.Add(fp); return true; };
        SftpStorage.HostKeyTrusted = (p, fp) => { lock (_stored) _stored.Add((p.Id, fp)); };
    }

    public void Dispose()
    {
        foreach (var p in SourceProfiles.All) SftpSource.Forget(p.Id);
        SftpStorage.AskHostKey = null;
        SftpStorage.HostKeyTrusted = null;
        SourceProfiles.Set([]);
        SourceSecrets.Store = _oldStore;
    }

    private SourceProfile Profile(string? hostKey = null, string path = "/data")
    {
        var p = new SourceProfile
        {
            Kind = "sftp", Name = "Container", Endpoint = SftpTestServer.Host, Port = _server.Port,
            UserName = SftpTestServer.User, Auth = SourceAuth.Password, Path = path, HostKeySha256 = hostKey,
        };
        SourceSecrets.Write(p, SecretSlot.Secret, _server.Password);
        SourceProfiles.Set([p]);
        return SourceProfiles.All[0];
    }

    private SftpSource Bound(string path)
    {
        Assert.True(StorageProviders.TryResolve(path, out var loc));
        return Assert.IsType<SftpSource>(loc.Provider);
    }

    [SftpFact]
    public async Task Connection_test_asks_for_the_host_key_and_reports_the_folder()
    {
        var p = Profile();
        p.Path = "/data";
        var text = await SftpSourceKind.Instance.TestAsync(p, secret: null, passphrase: null, CancellationToken.None);
        _output.WriteLine(text);
        Assert.StartsWith("Verbindung OK", text);
        Assert.Contains(_server.TrustedHostKey, text);
        Assert.Contains("/data:", text);
        Assert.Contains("Ordner", text);
        Assert.Contains("frei", text);
        Assert.DoesNotContain(_server.Password, text);
        Assert.Equal([_server.TrustedHostKey], _asked);
        Assert.Equal(_server.TrustedHostKey, p.HostKeySha256); // the dialog stores it on "Speichern"
        Assert.Empty(_stored);                                 // the test itself stores nothing

        // A typed (wrong) password wins over the stored one.
        var ex = await Assert.ThrowsAsync<SftpLoginException>(() => SftpSourceKind.Instance.TestAsync(p, "Getippt-987", null, CancellationToken.None));
        Assert.DoesNotContain("Getippt-987", ex.Message);
    }

    [SftpFact]
    public void Unknown_host_key_is_confirmed_once_and_stored_with_the_source()
    {
        var p = Profile();
        var source = Bound(SftpSourceKind.Instance.PathOf(p));
        Assert.Same(p.Id, source.Profile!.Id);
        source.Connect(CancellationToken.None);
        Assert.True(source.IsConnected);
        Assert.Equal([_server.TrustedHostKey], _asked);
        Assert.Equal([(p.Id, _server.TrustedHostKey)], _stored);

        // Stored in the profile (as the window does it): the same provider stays, nobody is asked again.
        p.HostKeySha256 = _server.TrustedHostKey;
        SourceProfiles.Set([p]);
        Assert.Same(source, Bound(SftpSourceKind.Instance.PathOf(p)));
        source.Disconnect();
        source.Connect(CancellationToken.None);
        Assert.Single(_asked);
    }

    [SftpFact]
    public void Changed_host_key_is_refused_without_asking()
    {
        var p = Profile(hostKey: "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        var source = Bound(SftpSourceKind.Instance.PathOf(p));
        var ex = Assert.Throws<SftpHostKeyException>(() => source.Connect(CancellationToken.None));
        Assert.Equal(SftpHostKeyProblem.Changed, ex.Problem);
        Assert.Equal(_server.TrustedHostKey, ex.Fingerprint);
        Assert.Empty(_asked);
        Assert.Empty(_stored);
        Assert.False(source.IsConnected);
    }

    [SftpFact]
    public void Declined_host_key_does_not_connect()
    {
        SftpStorage.AskHostKey = (_, _) => false;
        var p = Profile();
        var ex = Assert.Throws<SftpHostKeyException>(() => Bound(SftpSourceKind.Instance.PathOf(p)).Connect(CancellationToken.None));
        Assert.Equal(SftpHostKeyProblem.Unknown, ex.Problem);
        Assert.Empty(_stored);
    }

    [SftpFact]
    public void Key_file_with_passphrase_from_the_store()
    {
        using var dir = new TempDir();
        var keyFile = Path.Combine(dir.Path, "id_test");
        File.WriteAllText(keyFile, _server.KeyEncrypted);
        var p = Profile(hostKey: _server.TrustedHostKey);
        p.Auth = SourceAuth.KeyFile;
        p.KeyFile = keyFile;
        SourceSecrets.Delete(p, SecretSlot.Secret);
        SourceSecrets.Write(p, SecretSlot.Passphrase, SftpTestServer.KeyPassphrase);
        SourceProfiles.Set([p]);

        var source = Bound(SftpSourceKind.Instance.PathOf(p));
        source.Connect(CancellationToken.None);
        Assert.True(source.FolderExists(_server.DataUri));

        // Without the passphrase: a login error that says what is missing.
        SourceSecrets.Delete(p, SecretSlot.Passphrase);
        SftpSource.Forget(p.Id);
        var ex = Assert.Throws<SftpLoginException>(() => Bound(SftpSourceKind.Instance.PathOf(p)).Connect(CancellationToken.None));
        Assert.Contains("Passphrase", ex.Message);
    }

    [SftpFact]
    public void Home_folder_is_resolved_after_connecting()
    {
        var p = Profile(hostKey: _server.TrustedHostKey, path: "");
        var start = SftpSourceKind.Instance.PathOf(p);
        Assert.EndsWith("/~", start);
        var source = Bound(start);
        source.Connect(CancellationToken.None);
        var home = source.ResolvePath(start);
        _output.WriteLine($"{start} -> {home}");
        Assert.DoesNotContain("~", home);
        Assert.True(source.FolderExists(home));
        Assert.Equal(_server.DataUri, source.ResolvePath(start + "/data"));
    }

    [SftpFact]
    public async Task Scan_through_the_saved_source_counts_and_cancels()
    {
        var p = Profile(hostKey: _server.TrustedHostKey);
        var input = ScanInput.Parse(_server.DataUri);
        Assert.Null(input.Error);
        var source = Assert.IsType<SftpSource>(input.Provider);
        Assert.Equal(p.Id, source.Profile!.Id);
        source.Connect(CancellationToken.None);

        var scanner = new Scanner { Provider = source };
        await scanner.ScanAsync(input.Paths[0], source.ScanThreads!.Value, CancellationToken.None);
        Assert.Equal(_server.ExpectedBytes, scanner.ScanTarget!.Size);
        Assert.True(scanner.Requests >= _server.ExpectedDirs, $"{scanner.Requests} Anfragen");
        Assert.True(source.IsConnected); // stays open for rescans and the "Dateien" row

        // Esc: CancelWith ends the listings in flight, the scan ends as cancelled.
        using var cts = new CancellationTokenSource();
        using (source.CancelWith(cts.Token))
        {
            var again = new Scanner { Provider = source };
            var scan = again.ScanAsync(input.Paths[0], source.ScanThreads!.Value, cts.Token);
            cts.CancelAfter(30);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        }

        // A loaded snapshot scans again through the generic source, bound to the same saved source.
        var generic = new Scanner { Provider = SftpSource.Generic };
        await generic.ScanAsync(_server.DataUri + "/ebene1", 2, CancellationToken.None);
        Assert.Equal(1000 + 2500 + 123456 + 4, generic.ScanTarget!.Size);
    }
}
