using System.IO;
using System.Net.Sockets;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.Storage.Sftp;
using Renci.SshNet.Common;
using Xunit;

namespace DiskTree.Tests.Storage;

/// <summary>SFTP provider without a server: URIs, path rules, options, error mapping (no network except a closed local port).</summary>
public class SftpProviderTests
{
    private const string Secret = "Geheim-123!";

    private static SftpConnectionOptions Options(string host = "NAS.local", int port = 22, string user = "pascal") => new()
    {
        Host = host,
        Port = port,
        UserName = user,
        Auth = SftpAuth.Password(Secret),
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };

    // ------------------------------------------------------------------ SftpUri

    [Theory]
    [InlineData("sftp://pascal@nas.local/data", "pascal", "nas.local", 22, "/data")]
    [InlineData("SFTP://pascal@NAS.Local:22/data/", "pascal", "nas.local", 22, "/data")]
    [InlineData("sftp://pascal@nas.local:2222", "pascal", "nas.local", 2222, "/")]
    [InlineData("sftp://nas.local//a//b/./c/../d/", "", "nas.local", 22, "/a/b/d")]
    [InlineData("sftp://u@h/../../x", "u", "h", 22, "/x")]
    [InlineData("sftp://me@corp.de@h:23/p", "me@corp.de", "h", 23, "/p")]
    [InlineData("sftp://u@[::1]:2022/a b/Ä", "u", "::1", 2022, "/a b/Ä")]
    [InlineData("  sftp://u@h/Mein Ordner/Übersicht  ", "u", "h", 22, "/Mein Ordner/Übersicht")]
    [InlineData("sftp://u@h/a%20b", "u", "h", 22, "/a%20b")]
    public void SftpUri_Parses_AndNormalizes(string text, string user, string host, int port, string path)
    {
        Assert.True(SftpUri.TryParse(text, out var uri));
        Assert.Equal(new SftpUri(user, host, port, path), uri);

        // Normalizing twice changes nothing.
        Assert.True(SftpUri.TryParse(uri.ToString(), out var again));
        Assert.Equal(uri, again);
    }

    [Theory]
    [InlineData("sftp://pascal@nas.local/data", "sftp://pascal@nas.local/data")]
    [InlineData("sftp://pascal@nas.local:22/", "sftp://pascal@nas.local/")]
    [InlineData("sftp://pascal@nas.local:2222/x/", "sftp://pascal@nas.local:2222/x")]
    [InlineData("sftp://u@[::1]:2022/a", "sftp://u@[::1]:2022/a")]
    public void SftpUri_ToString_IsCanonical(string text, string expected)
    {
        Assert.True(SftpUri.TryParse(text, out var uri));
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:\\data")]
    [InlineData("\\\\server\\share")]
    [InlineData("s3://bucket/key")]
    [InlineData("sftp://")]
    [InlineData("sftp:///data")]
    [InlineData("sftp://u@/data")]
    [InlineData("sftp://host:0/")]
    [InlineData("sftp://host:70000/")]
    [InlineData("sftp://host:abc/")]
    [InlineData("sftp://::1/")]
    [InlineData("sftp://[::1/")]
    [InlineData("sftp://ho st/")]
    public void SftpUri_RejectsOtherText(string? text)
    {
        Assert.False(SftpUri.TryParse(text, out _));
    }

    // ------------------------------------------------------------------ path rules

    [Fact]
    public void PathOps_StopAtTheServer()
    {
        var p = SftpPathOps.Instance;
        Assert.Equal('/', p.Separator);
        Assert.Equal(StringComparison.Ordinal, p.Comparison);

        Assert.Equal("", p.NameOf("sftp://u@h/"));
        Assert.Equal("", p.NameOf("sftp://u@h"));
        Assert.Equal("a", p.NameOf("sftp://u@h/a"));
        Assert.Equal("b c.txt", p.NameOf("sftp://u@h:2222/a/b c.txt"));
        Assert.Equal("b", p.NameOf("sftp://u@h/a/b/"));

        Assert.Null(p.ParentOf("sftp://u@h/"));
        Assert.Null(p.ParentOf("sftp://u@h"));
        Assert.Equal("sftp://u@h/", p.ParentOf("sftp://u@h/a"));
        Assert.Equal("sftp://u@h/a", p.ParentOf("sftp://u@h/a/b"));
        Assert.Equal("sftp://u@h:2222/a/b", p.ParentOf("sftp://u@h:2222/a/b/c/"));

        Assert.Equal("sftp://u@h/a", p.Join("sftp://u@h/", "a"));
        Assert.Equal("sftp://u@h/a/b", p.Join("sftp://u@h/a", "b"));

        Assert.True(p.IsUnder("sftp://u@h/", "sftp://u@h/a"));
        Assert.True(p.IsUnder("sftp://u@h/a", "sftp://u@h/a/b"));
        Assert.False(p.IsUnder("sftp://u@h/a", "sftp://u@h/ab"));
        Assert.False(p.IsUnder("sftp://u@h/a", "sftp://u@h/A/b"));   // case-sensitive
        Assert.True(p.IsSameOrUnder("sftp://u@h/", "sftp://u@h/"));
        Assert.Equal("data/x", p.RelativeTo("sftp://u@h/", "sftp://u@h/data/x").TrimStart('/'));
        Assert.Equal(new[] { "data", "x" }, p.Split("data/x/"));

        // Plain paths fall back to the generic rule.
        Assert.Equal("c", p.NameOf("/a/b/c"));
        Assert.Equal("/a/b", p.ParentOf("/a/b/c"));
    }

    // ------------------------------------------------------------------ provider without connecting

    [Fact]
    public void Provider_TryParse_OnlyItsOwnServer()
    {
        using var p = new SftpProvider(Options());
        Assert.Equal("sftp", p.Id);
        Assert.Equal("SFTP pascal@nas.local", p.DisplayName);
        Assert.Same(SftpPathOps.Instance, p.Paths);
        Assert.Equal("sftp://pascal@nas.local/", p.RootUri);

        Assert.True(p.TryParse("sftp://pascal@NAS.local/data/", out var loc));
        Assert.Same(p, loc.Provider);
        Assert.Equal("sftp://pascal@nas.local/data", loc.Path);

        // Without a user: the configured one.
        Assert.True(p.TryParse("sftp://nas.local:22/data", out loc));
        Assert.Equal("sftp://pascal@nas.local/data", loc.Path);

        Assert.False(p.TryParse("sftp://other@nas.local/data", out _));
        Assert.False(p.TryParse("sftp://pascal@nas.local:2222/data", out _));
        Assert.False(p.TryParse("sftp://pascal@other/data", out _));
        Assert.False(p.TryParse(@"C:\data", out _));
        Assert.False(p.TryParse("", out _));
        Assert.False(p.IsConnected);
    }

    [Fact]
    public void Provider_Normalize_AndRemotePath()
    {
        using var p = new SftpProvider(Options(port: 2222));
        Assert.Equal("sftp://pascal@nas.local:2222/a/c", p.Normalize("sftp://pascal@nas.local:2222/a/b/../c/"));
        Assert.Equal("/a/c", p.RemotePath("sftp://pascal@nas.local:2222/a/c"));
        Assert.Throws<ArgumentException>(() => p.Normalize(@"C:\data"));
        Assert.Throws<ArgumentException>(() => p.Normalize("sftp://pascal@nas.local/a"));   // other port
        Assert.Throws<ArgumentException>(() => p.RemotePath("sftp://x@nas.local:2222/a"));

        // Not ours: no folder, and no connection is attempted for it.
        Assert.False(p.FolderExists("sftp://pascal@elsewhere/a"));
        Assert.False(p.TryGetCapacity("sftp://pascal@nas.local:2222/a", out _, out _));   // only the root has a capacity
        Assert.False(p.IsConnected);
    }

    [Fact]
    public void Provider_Caps_ReadOnly()
    {
        using var p = new SftpProvider(Options());
        var caps = p.Caps;
        foreach (var yes in new[] { StorageCaps.Capacity, StorageCaps.AccessedTime, StorageCaps.Hierarchical, StorageCaps.ReparsePoints,
                     StorageCaps.CaseSensitive, StorageCaps.ParallelListing, StorageCaps.LiveFileList, StorageCaps.ContentHash })
            Assert.True(caps.HasFlag(yes), yes.ToString());
        foreach (var no in new[] { StorageCaps.CreatedTime, StorageCaps.Attributes, StorageCaps.CostsPerRequest, StorageCaps.StorageClasses,
                     StorageCaps.Owner, StorageCaps.Acl, StorageCaps.Delete, StorageCaps.Trash, StorageCaps.Move, StorageCaps.Rename,
                     StorageCaps.ZipCreate, StorageCaps.ShellMenu })
            Assert.False(caps.HasFlag(no), no.ToString());
    }

    [Fact]
    public void Provider_CanBeRegistered_AndResolved()
    {
        using var p = new SftpProvider(Options());
        StorageProviders.Register(p);
        try
        {
            Assert.Same(p, StorageProviders.Get("sftp"));
            Assert.True(StorageProviders.TryResolve("sftp://pascal@nas.local/x", out var loc));
            Assert.Same(p, loc.Provider);
            Assert.True(StorageProviders.TryResolve(@"C:\x", out loc));
            Assert.Same(StorageProviders.Disk, loc.Provider);
        }
        finally
        {
            StorageProviders.Unregister("sftp");
        }
    }

    [Theory]
    [InlineData("", "u")]
    [InlineData("h", "")]
    public void Options_AreChecked(string host, string user)
    {
        Assert.Throws<ArgumentException>(() => new SftpProvider(new SftpConnectionOptions { Host = host, UserName = user, Auth = SftpAuth.Password("x") }));
    }

    // ------------------------------------------------------------------ secrets

    [Fact]
    public void Secrets_StayOutOfTexts()
    {
        var o = Options();
        Assert.Equal("pascal@NAS.local:22 (Passwort)", o.ToString());
        Assert.DoesNotContain(Secret, o.ToString());
        Assert.DoesNotContain(Secret, SftpAuth.KeyFile(@"C:\keys\id_ed25519", Secret).ToString());
        Assert.DoesNotContain(Secret, SftpAuth.KeyText("-----BEGIN OPENSSH PRIVATE KEY-----" + Secret, Secret).ToString());
        Assert.Equal("Passwort", o.With(SftpAuth.Password("anders")).Auth.ToString());
        Assert.Equal("SHA256:abc", o.WithHostKey("SHA256:abc").HostKeySha256);
    }

    [Fact]
    public void Fingerprints_AreNormalized()
    {
        Assert.Equal("SHA256:AbC+/1", SftpFingerprint.Normalize(" sha256:AbC+/1= "));
        Assert.Equal("SHA256:AbC+/1", SftpFingerprint.Normalize("AbC+/1"));
        Assert.Null(SftpFingerprint.Normalize(" "));
        Assert.Null(SftpFingerprint.Normalize("SHA256:"));
        Assert.True(SftpFingerprint.Same("SHA256:AbC", "AbC="));
        Assert.False(SftpFingerprint.Same("SHA256:AbC", "SHA256:abc"));   // base64 is case-sensitive
        Assert.False(SftpFingerprint.Same(null, null));
    }

    // ------------------------------------------------------------------ error mapping

    public static TheoryData<Exception, Type> MappedErrors => new()
    {
        { new SftpPermissionDeniedException("Permission denied"), typeof(UnauthorizedAccessException) },
        { new SftpPathNotFoundException("No such file"), typeof(DirectoryNotFoundException) },
        { new SshAuthenticationException("Permission denied (password)."), typeof(SftpLoginException) },
        { new SshPassPhraseNullOrEmptyException("Private key is encrypted but passphrase is empty."), typeof(SftpLoginException) },
        { new SshOperationTimeoutException("Timeout"), typeof(IOException) },
        { new SshConnectionException("Client not connected."), typeof(IOException) },
        { new SocketException((int)SocketError.ConnectionRefused), typeof(IOException) },
        { new ProxyException("proxy"), typeof(IOException) },
        { new ObjectDisposedException("SftpClient"), typeof(IOException) },
        { new SftpException(Renci.SshNet.Sftp.StatusCode.Failure, "Failure"), typeof(IOException) },
        { new SshException("Something"), typeof(IOException) },
    };

    [Theory]
    [MemberData(nameof(MappedErrors))]
    public void Errors_AreMapped_ToWhatTheScanCounts(Exception ssh, Type expected)
    {
        Assert.True(SftpConnection.Map(ssh, "sftp://pascal@nas/data", out var mapped));
        Assert.IsType(expected, mapped);
        Assert.Same(ssh, mapped.InnerException);
        Assert.True(mapped is IOException or UnauthorizedAccessException);
        Assert.DoesNotContain(Secret, mapped.Message);
    }

    [Fact]
    public void Errors_OursAndUnknown_AreLeftAlone()
    {
        Assert.False(SftpConnection.Map(new OperationCanceledException(), "x", out _));
        Assert.False(SftpConnection.Map(new SftpLoginException("x"), "x", out _));
        Assert.False(SftpConnection.Map(new SftpHostKeyException(SftpHostKeyProblem.Unknown, "h", "SHA256:a", null), "x", out _));
        Assert.False(SftpConnection.Map(new NullReferenceException(), "x", out _));   // bugs are not swallowed
        Assert.False(SftpConnection.Map(new IOException("schon gemappt"), "x", out _));
    }

    [Fact]
    public void ConnectionLoss_IsRecognized()
    {
        Assert.True(SftpConnection.IsConnectionLoss(new SshConnectionException("x")));
        Assert.True(SftpConnection.IsConnectionLoss(new SshOperationTimeoutException("x")));
        Assert.True(SftpConnection.IsConnectionLoss(new ObjectDisposedException("x")));
        Assert.True(SftpConnection.IsConnectionLoss(new SocketException()));
        Assert.False(SftpConnection.IsConnectionLoss(new SftpPermissionDeniedException("x")));
        Assert.False(SftpConnection.IsConnectionLoss(new SftpPathNotFoundException("x")));
        Assert.False(SftpConnection.IsConnectionLoss(new SshAuthenticationException("x")));
    }

    [Fact]
    public void HostKeyException_IsAnIOException_WithTheFingerprint()
    {
        var unknown = new SftpHostKeyException(SftpHostKeyProblem.Unknown, "u@h", "SHA256:neu", null);
        Assert.IsAssignableFrom<IOException>(unknown);
        Assert.Contains("SHA256:neu", unknown.Message);
        var changed = new SftpHostKeyException(SftpHostKeyProblem.Changed, "u@h", "SHA256:neu", "SHA256:alt");
        Assert.Contains("geändert", changed.Message);
        Assert.Equal("SHA256:alt", changed.Expected);
    }

    // ------------------------------------------------------------------ no server

    /// <summary>A port nobody listens on (bound and released again).</summary>
    private static int ClosedPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public void NoServer_ConnectFails_AsIOException_WithoutSecret()
    {
        using var p = new SftpProvider(Options("127.0.0.1", ClosedPort()));
        var ex = Assert.ThrowsAny<IOException>(() => p.Connect(CancellationToken.None));
        Assert.DoesNotContain(Secret, ex.ToString());
        Assert.False(p.IsConnected);

        // Listing reports the same kind of error (the scanner counts it as "nicht lesbar").
        var list = Assert.ThrowsAny<IOException>(() => p.Enumerate(p.RootUri, (in StorageEntry e) => e.Name.ToString(), ListOptions.Strict).ToList());
        Assert.DoesNotContain(Secret, list.ToString());
    }

    [Fact]
    public async Task NoServer_ScanFails_Clearly()
    {
        using var p = new SftpProvider(Options("127.0.0.1", ClosedPort()));
        var s = new Scanner { Provider = p };
        await Assert.ThrowsAnyAsync<IOException>(() => s.ScanAsync(p.RootUri + "data", 4, CancellationToken.None));
    }

    [Fact]
    public void Enumerate_IsLazy()
    {
        using var p = new SftpProvider(Options("127.0.0.1", ClosedPort()));
        var listing = p.Enumerate(p.RootUri, (in StorageEntry e) => 0, ListOptions.Strict);   // nothing happens yet
        Assert.NotNull(listing);
        Assert.False(p.IsConnected);
    }

    [Fact]
    public void Disposed_Provider_RefusesWork()
    {
        var p = new SftpProvider(Options("127.0.0.1", ClosedPort()));
        p.Dispose();
        Assert.Throws<ObjectDisposedException>(() => p.Connect(CancellationToken.None));
    }
}
