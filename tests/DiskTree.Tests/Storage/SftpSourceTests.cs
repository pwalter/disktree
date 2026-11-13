using System.IO;
using System.Runtime.Loader;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.Storage.Sftp;
using DiskTree.Infrastructure;
using Xunit;

namespace DiskTree.Tests.Storage;

/// <summary>
/// Tests that change the app-wide source state (<see cref="SourceProfiles"/>, <see cref="SourceSecrets.Store"/>, the
/// SFTP hooks) run one after the other, so one test's profiles never show up in another one's matching.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SourceStateCollection
{
    public const string Name = "Quellen (globaler Zustand)";
}

/// <summary>SFTP as a saved source: the kind (fields, checks, paths), profile matching, secrets, host-key hooks.</summary>
[Collection(SourceStateCollection.Name)]
public sealed class SftpSourceTests : IDisposable
{
    private readonly ISecretStore _oldStore = SourceSecrets.Store;
    private readonly MemorySecretStore _store = new();
    private static readonly SftpSourceKind Kind = SftpSourceKind.Instance;

    public SftpSourceTests()
    {
        SourceSecrets.Store = _store;
        SourceProfiles.PreferredId = null;
        SourceProfiles.Set([]);
        SftpStorage.Register();
    }

    public void Dispose()
    {
        SftpStorage.AskHostKey = null;
        SftpStorage.HostKeyTrusted = null;
        SourceProfiles.PreferredId = null;
        SourceProfiles.Set([]);
        SourceSecrets.Store = _oldStore;
    }

    private static SourceProfile Sftp(string name, string host, string user, int port = 0, string path = "") =>
        new() { Kind = "sftp", Name = name, Endpoint = host, UserName = user, Port = port, Path = path, Auth = SourceAuth.Password };

    // ------------------------------------------------------------------ kind

    [Fact]
    public void Kind_is_registered_with_scheme_and_without_agent()
    {
        Assert.Same(Kind, SourceKinds.Get("sftp"));
        Assert.Same(Kind, SourceKinds.ForText("  SFTP://nas/x"));
        Assert.Same(SftpSource.Generic, StorageProviders.Get("sftp"));
        Assert.Equal([SourceAuth.Password, SourceAuth.KeyFile], Kind.AuthModes);
        Assert.True(((ISourceKind)Kind).RequiresProfile);
        Assert.Equal(SourceAuth.Password, Kind.NewProfile().Auth);
        Assert.Equal("sftp", Kind.NewProfile().Kind);
    }

    [Fact]
    public void Labels_follow_the_sign_in_mode()
    {
        Assert.NotNull(Kind.Label(SourceField.Secret, SourceAuth.Password));
        Assert.Null(Kind.Label(SourceField.KeyFile, SourceAuth.Password));
        Assert.Null(Kind.Label(SourceField.Passphrase, SourceAuth.Password));
        Assert.Null(Kind.Label(SourceField.Secret, SourceAuth.KeyFile));
        Assert.NotNull(Kind.Label(SourceField.KeyFile, SourceAuth.KeyFile));
        Assert.NotNull(Kind.Label(SourceField.Passphrase, SourceAuth.KeyFile));
        foreach (var f in new[] { SourceField.Endpoint, SourceField.Port, SourceField.UserName, SourceField.Path, SourceField.HostKey })
        {
            Assert.NotNull(Kind.Label(f, SourceAuth.Password));
            Assert.NotNull(Kind.Hint(f));
        }
        foreach (var f in new[] { SourceField.Region, SourceField.PathStyle, SourceField.AwsProfile, SourceField.Listing })
            Assert.Null(Kind.Label(f, SourceAuth.Password));
    }

    [Fact]
    public void Validate_checks_server_user_port_key_and_path()
    {
        Assert.Null(Kind.Validate(Sftp("NAS", "nas.local", "pascal")));
        Assert.Null(Kind.Validate(Sftp("NAS", "NAS.local", "pascal", 2222, "/srv/daten")));
        Assert.Null(Kind.Validate(Sftp("v6", "[::1]", "pascal")));
        Assert.Null(Kind.Validate(Sftp("NAS", "nas", "pascal", path: "sftp://pascal@nas/srv")));

        Assert.NotNull(Kind.Validate(Sftp("", "nas", "pascal")));
        Assert.NotNull(Kind.Validate(Sftp("NAS", "", "pascal")));
        Assert.NotNull(Kind.Validate(Sftp("NAS", "sftp://nas", "pascal")));
        Assert.NotNull(Kind.Validate(Sftp("NAS", "nas mit leer", "pascal")));
        Assert.NotNull(Kind.Validate(Sftp("NAS", "nas", "")));
        Assert.NotNull(Kind.Validate(Sftp("NAS", "nas", "pa scal")));
        Assert.NotNull(Kind.Validate(Sftp("NAS", "nas", "pascal", 70000)));
        Assert.NotNull(Kind.Validate(Sftp("NAS", "nas", "pascal", path: "sftp://pascal@anderer/srv")));
        Assert.NotNull(Kind.Validate(Sftp("NAS", "nas", "pascal", path: "sftp://fremd@nas/srv")));

        var key = Sftp("NAS", "nas", "pascal");
        key.Auth = SourceAuth.KeyFile;
        Assert.NotNull(Kind.Validate(key));
        key.KeyFile = @"%USERPROFILE%\.ssh\id_ed25519";
        Assert.Null(Kind.Validate(key));
    }

    [Theory]
    [InlineData("", 0, "sftp://pascal@nas/~")]
    [InlineData("~", 0, "sftp://pascal@nas/~")]
    [InlineData("~/fotos", 0, "sftp://pascal@nas/~/fotos")]
    [InlineData("fotos/2024", 0, "sftp://pascal@nas/~/fotos/2024")]
    [InlineData("/srv/daten/", 0, "sftp://pascal@nas/srv/daten")]
    [InlineData("/", 2222, "sftp://pascal@nas:2222/")]
    [InlineData("/a/./b/../c", 22, "sftp://pascal@nas/a/c")]
    [InlineData("sftp://nas/srv", 0, "sftp://pascal@nas/srv")]
    public void PathOf_gives_the_canonical_start(string path, int port, string expected)
    {
        Assert.Equal(expected, Kind.PathOf(Sftp("NAS", " NAS ", "pascal", port, path)));
    }

    [Fact]
    public void ProfileFor_fills_in_server_user_and_folder()
    {
        var p = ((ISourceKind)Kind).ProfileFor("sftp://pascal@NAS.local:2222/srv/x");
        Assert.Equal("sftp", p.Kind);
        Assert.Equal("nas.local", p.Endpoint);
        Assert.Equal(2222, p.Port);
        Assert.Equal("pascal", p.UserName);
        Assert.Equal("/srv/x", p.Path);
        Assert.Equal(0, ((ISourceKind)Kind).ProfileFor("sftp://nas/").Port);
        Assert.Equal("sftp://pascal@nas.local:2222/srv/x", Kind.PathOf(p));
    }

    // ------------------------------------------------------------------ matching

    [Fact]
    public void Paths_are_bound_to_the_saved_source_of_their_server_and_user()
    {
        var anna = Sftp("Anna", "nas", "anna");
        var bob = Sftp("Bob", "nas", "bob");
        var other = Sftp("Port", "nas", "anna", 2222);
        SourceProfiles.Set([anna, bob, other]);

        Assert.True(StorageProviders.TryResolve("sftp://bob@NAS/home/bob", out var loc));
        Assert.Equal(bob.Id, loc.Provider.Profile!.Id);
        Assert.Equal("sftp://bob@nas/home/bob", loc.Path);
        Assert.IsType<SftpSource>(loc.Provider);
        Assert.Equal(SftpStorage.Threads, loc.Provider.ScanThreads);

        Assert.True(StorageProviders.TryResolve("sftp://anna@nas:2222/x", out loc));
        Assert.Equal(other.Id, loc.Provider.Profile!.Id);

        // Without a user in the path: the profile picked last in the path box, and its user goes into the path.
        SourceProfiles.PreferredId = bob.Id;
        Assert.True(StorageProviders.TryResolve("sftp://nas/srv", out loc));
        Assert.Equal(bob.Id, loc.Provider.Profile!.Id);
        Assert.Equal("sftp://bob@nas/srv", loc.Path);

        // The same source gives the same provider (one connection per source).
        Assert.True(StorageProviders.TryResolve("sftp://bob@nas/a", out var again));
        Assert.Same(loc.Provider, again.Provider);
    }

    [Fact]
    public void A_path_without_saved_source_is_understood_but_never_connects()
    {
        SourceProfiles.Set([Sftp("Anna", "nas", "anna")]);
        Assert.True(StorageProviders.TryResolve("sftp://fremd@nas/x", out var loc));
        Assert.Same(SftpSource.Generic, loc.Provider);
        Assert.Null(loc.Provider.Profile);
        Assert.Equal("sftp://fremd@nas/x", loc.Path);

        var ex = Assert.Throws<IOException>(() => loc.Provider.FolderExists(loc.Path));
        Assert.Contains("keine SFTP-Quelle", ex.Message);
        Assert.Throws<IOException>(() => loc.Provider.Enumerate(loc.Path, (in StorageEntry e) => 0, ListOptions.Strict).ToList());

        var input = ScanInput.Parse("sftp://fremd@nas/x");
        Assert.Null(input.Error);
        Assert.Same(SftpSource.Generic, input.Provider);
    }

    [Fact]
    public void Editing_a_source_gives_a_new_provider()
    {
        var p = Sftp("A", "nas", "anna");
        SourceProfiles.Set([p]);
        var first = SftpSource.For(SourceProfiles.All[0]);
        Assert.Same(first, SftpSource.For(SourceProfiles.All[0]));

        p.Path = "/srv";
        SourceProfiles.Set([p]);
        Assert.NotSame(first, SftpSource.For(SourceProfiles.All[0]));

        var second = SftpSource.For(SourceProfiles.All[0]);
        Kind.Forget(p.Id);
        Assert.NotSame(second, SftpSource.For(SourceProfiles.All[0]));
    }

    [Fact]
    public void Snapshot_of_an_sftp_scan_opens_without_a_connection()
    {
        // A snapshot only needs the path rules: the generic source is what a .disktree file with "provider: sftp" gets.
        Assert.Same(SftpSource.Generic, StorageProviders.Get("sftp"));
        Assert.Equal("b", SftpSource.Generic.Paths.NameOf("sftp://u@h/a/b"));
        Assert.Equal("sftp://u@h/a", SftpSource.Generic.Paths.ParentOf("sftp://u@h/a/b"));
        Assert.False(SftpSource.Generic.IsConnected);
        SftpSource.Generic.Disconnect(); // nothing to close, must not throw
    }

    // ------------------------------------------------------------------ secrets

    [Fact]
    public void Secrets_stay_in_the_store_and_out_of_the_settings()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "settings.json");
        var pw = Sftp("NAS", "nas", "pascal");
        pw.HostKeySha256 = "SHA256:abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
        var key = Sftp("Key", "nas", "pascal");
        key.Auth = SourceAuth.KeyFile;
        key.KeyFile = @"C:\keys\id_ed25519";
        SourceSecrets.Write(pw, SecretSlot.Secret, "Passwort-0815");
        SourceSecrets.Write(key, SecretSlot.Passphrase, "Passphrase-4711");

        var s = new AppSettings();
        s.Sources.Add(pw);
        s.Sources.Add(key);
        s.Sources.Add(new SourceProfile { Kind = "s3", Name = "S3" });
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(s));
        var text = File.ReadAllText(file);
        Assert.DoesNotContain("Passwort-0815", text);
        Assert.DoesNotContain("Passphrase-4711", text);
        Assert.Contains(pw.HostKeySha256, text);                    // the host key is not secret
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "HostKeySha256")); // only written where set

        var back = AppSettings.LoadFrom(file);
        Assert.Equal(pw.HostKeySha256, back.Sources[0].HostKeySha256);
        Assert.Null(back.Sources[1].HostKeySha256);
        Assert.Equal("Passwort-0815", SourceSecrets.Read(back.Sources[0]));
        Assert.Equal(2, _store.Targets.Count);
    }

    [Fact]
    public void Sign_in_reads_the_secret_and_never_shows_it()
    {
        var p = Sftp("NAS", "nas", "pascal");
        var ex = Assert.Throws<SftpLoginException>(() => SftpSource.ReadAuth(p));
        Assert.Contains("kein Passwort", ex.Message);

        SourceSecrets.Write(p, SecretSlot.Secret, "Passwort-0815");
        var options = SftpSource.OptionsFor(p, SftpSource.ReadAuth(p), persist: true);
        Assert.Equal("nas", options.Host);
        Assert.Equal(22, options.Port);
        Assert.Equal(SftpStorage.ParallelListings, options.MaxParallelListings);
        Assert.DoesNotContain("0815", options.ToString());
        Assert.DoesNotContain("0815", options.Auth.ToString());

        p.Auth = SourceAuth.KeyFile;
        p.KeyFile = @"%SystemRoot%\id_test";
        SourceSecrets.Write(p, SecretSlot.Passphrase, "Passphrase-4711");
        var auth = SftpSource.ReadAuth(p);
        Assert.Contains(Environment.GetEnvironmentVariable("SystemRoot")!, auth.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4711", auth.ToString());

        // The connection test may use what was typed into the dialog instead of the stored secret (none stored here).
        Assert.Equal("Passwort", SftpSource.ReadAuth(Sftp("N", "nas", "p"), password: "Getippt-1").ToString());
    }

    [Fact]
    public void Missing_password_ends_the_scan_with_a_login_error()
    {
        var p = Sftp("NAS", "127.0.0.1", "pascal", 1);
        SourceProfiles.Set([p]);
        Assert.True(StorageProviders.TryResolve("sftp://pascal@127.0.0.1:1/x", out var loc));
        var connected = Assert.IsAssignableFrom<IConnectedStorage>(loc.Provider);
        var ex = Assert.Throws<SftpLoginException>(() => connected.Connect(CancellationToken.None));
        Assert.Contains("„NAS“", ex.Message);
    }

    // ------------------------------------------------------------------ host key

    [Fact]
    public void Host_key_is_asked_and_stored_only_when_trusted()
    {
        var p = Sftp("NAS", "nas", "pascal");
        var asked = new List<string>();
        var stored = new List<(string, string)>();
        SftpStorage.AskHostKey = (profile, fp) => { asked.Add(profile.Name + " " + fp); return fp == "SHA256:gut"; };
        SftpStorage.HostKeyTrusted = (profile, fp) => stored.Add((profile.Id, fp));

        var scan = SftpSource.OptionsFor(p, SftpAuth.Password("x"), persist: true);
        Assert.True(scan.ConfirmHostKey!("SHA256:gut"));
        Assert.False(scan.ConfirmHostKey!("SHA256:schlecht"));
        Assert.Equal(["NAS SHA256:gut", "NAS SHA256:schlecht"], asked);
        Assert.Equal([(p.Id, "SHA256:gut")], stored);

        // The connection test only asks; the dialog keeps the key until "Speichern".
        var test = SftpSource.OptionsFor(p, SftpAuth.Password("x"), persist: false);
        Assert.True(test.ConfirmHostKey!("SHA256:gut"));
        Assert.Single(stored);

        // Without a UI nobody is asked: unknown keys are refused.
        SftpStorage.AskHostKey = null;
        Assert.False(scan.ConfirmHostKey!("SHA256:gut"));
    }

    [Fact]
    public void A_stored_host_key_is_passed_on_normalized()
    {
        var p = Sftp("NAS", "nas", "pascal");
        p.HostKeySha256 = " sha256:AbC= ";
        Assert.Equal("SHA256:AbC", SftpSource.OptionsFor(p, SftpAuth.Password("x"), persist: true).HostKeySha256);
        Assert.Equal("SHA256:AbC", SftpSource.For(p).HostKeySha256);
    }

    // ------------------------------------------------------------------ lazy loading

    private sealed class RecordingContext() : AssemblyLoadContext("sftp-lazy-check", isCollectible: true)
    {
        public readonly List<string> Requested = new();

        protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName name)
        {
            lock (Requested) Requested.Add(name.Name ?? "");
            return null;
        }
    }

    [Fact]
    public void Registering_and_parsing_does_not_load_SshNet()
    {
        var ctx = new RecordingContext();
        try
        {
            var asm = ctx.LoadFromAssemblyPath(typeof(SftpSource).Assembly.Location);
            asm.GetType("DiskTree.Features.Storage.Sftp.SftpStorage")!.GetMethod("Register")!.Invoke(null, null);
            var registry = asm.GetType("DiskTree.Core.Storage.StorageProviders")!;
            var args = new object?[] { "sftp://pascal@nas/srv", null };
            Assert.True((bool)registry.GetMethod("TryResolve")!.Invoke(null, args)!);
            lock (ctx.Requested)
            {
                Assert.DoesNotContain(ctx.Requested, n => n.StartsWith("Renci", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(ctx.Requested, n => n.StartsWith("BouncyCastle", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            ctx.Unload();
        }
    }
}
