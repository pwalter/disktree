using System.Collections.Concurrent;
using System.IO;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Storage.Sftp;

/// <summary>Registers SFTP at start (SSH.NET is not loaded by this) and holds the hooks the UI fills in.</summary>
public static class SftpStorage
{
    /// <summary>Folders a scan lists at the same time (scanner threads): an SFTP server is one connection, keep it small.</summary>
    public const int Threads = 4;

    /// <summary>Listings in flight over the one connection (<see cref="SftpConnectionOptions.MaxParallelListings"/>).</summary>
    public const int ParallelListings = 8;

    public static void Register()
    {
        SourceKinds.Register(SftpSourceKind.Instance);
        StorageProviders.Register(SftpSource.Generic);
    }

    /// <summary>
    /// "Diesem Server vertrauen?": asked on a background (connecting) thread with the profile and the fingerprint of an
    /// unknown host key; the UI shows its dialog on the UI thread. Null (tests, no UI): unknown keys are refused.
    /// </summary>
    public static Func<SourceProfile, string, bool>? AskHostKey { get; set; }

    /// <summary>The user trusted the host key of a saved source while scanning: store it with the profile (not secret).</summary>
    public static Action<SourceProfile, string>? HostKeyTrusted { get; set; }
}

/// <summary>
/// SFTP as the app sees it: the provider registered under "sftp" (<see cref="Generic"/>: snapshot files, the path box)
/// and one instance per saved source (<see cref="For"/>). A path "sftp://[user@]host[:port]/…" is bound to the saved
/// source of that server and user (<see cref="SourceProfiles.Match"/>); without one, nothing is connected: the generic
/// instance only knows the path rules, so snapshots open offline, and a scan asks to create a source first.
/// <para>
/// The <see cref="SftpProvider"/> that really talks to the server is created on first use, with the password /
/// passphrase read from the Credential Manager right then (<see cref="SourceSecrets"/>), the trusted host key of the
/// profile and <see cref="SftpStorage.AskHostKey"/> for a server seen the first time. No SSH.NET type is touched before
/// a connection is made.
/// </para>
/// </summary>
public sealed class SftpSource : IStorageProvider, IConnectedStorage
{
    public const string ProviderId = SftpProvider.ProviderId;

    /// <summary>What SFTP offers (the same as <see cref="SftpProvider.Caps"/>).</summary>
    public const StorageCaps SftpCaps =
        StorageCaps.Capacity | StorageCaps.AccessedTime | StorageCaps.Hierarchical | StorageCaps.ReparsePoints
        | StorageCaps.CaseSensitive | StorageCaps.ParallelListing | StorageCaps.LiveFileList | StorageCaps.ContentHash;

    /// <summary>The registered provider: binds every path to its saved source when it is used.</summary>
    public static readonly SftpSource Generic = new(null);

    private static readonly ConcurrentDictionary<string, SftpSource> Bound = new();

    private readonly SourceProfile? _profile;
    private readonly object _lock = new();
    private SftpProvider? _inner;

    private SftpSource(SourceProfile? profile) => _profile = profile;

    /// <summary>The provider of a saved source; the same instance (and connection) as long as its connection settings stay.</summary>
    public static SftpSource For(SourceProfile profile)
    {
        if (Bound.TryGetValue(profile.Id, out var s) && s.Fits(profile)) return s;
        var fresh = new SftpSource(profile.Clone());
        Bound[profile.Id] = fresh;
        return fresh;
    }

    /// <summary>The profile was edited or deleted: the next use builds a new connection (the old one is closed).</summary>
    public static void Forget(string profileId)
    {
        if (Bound.TryRemove(profileId, out var old)) Task.Run(old.Disconnect);
    }

    /// <summary>Same server, login and settings; a host key confirmed by this instance counts as the same.</summary>
    private bool Fits(SourceProfile p)
    {
        var o = _profile!;
        if (o.Name != p.Name || o.Endpoint != p.Endpoint || o.Port != p.Port || o.UserName != p.UserName || o.Auth != p.Auth
            || o.KeyFile != p.KeyFile || o.Path != p.Path) return false;
        return SftpFingerprint.Same(o.HostKeySha256, p.HostKeySha256)
               || (o.HostKeySha256 == null && p.HostKeySha256 != null && SftpFingerprint.Same(_inner?.HostKeySha256, p.HostKeySha256))
               || (o.HostKeySha256 == null && p.HostKeySha256 == null);
    }

    // ------------------------------------------------------------------ profiles and paths

    /// <summary>Server of a profile: host (lower case) and port, or null if the host is not usable.</summary>
    public static (string Host, int Port)? ServerOf(SourceProfile p)
    {
        var host = p.Endpoint.Trim();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (host.Length == 0) return null;
        int port = p.Port > 0 ? p.Port : SftpUri.DefaultPort;
        if (!SftpUri.TryParse(SftpUri.FormatAuthority("", host, port) + "/", out var uri)) return null;
        return (uri.Host, uri.Port);
    }

    /// <summary>"sftp://user@host[:port]" of a profile, or null.</summary>
    public static string? AuthorityOf(SourceProfile p) =>
        ServerOf(p) is { } s ? SftpUri.FormatAuthority(p.UserName.Trim(), s.Host, s.Port) : null;

    /// <summary>
    /// The remote folder a profile starts in: its path ("/data"), "~"-relative for a relative path or an empty one
    /// ("/~" = home folder, resolved after connecting).
    /// </summary>
    public static string StartOf(SourceProfile p)
    {
        var path = p.Path.Trim().Trim('"');
        if (SftpUri.TryParse(path, out var uri)) return uri.Path;
        if (path.Length == 0 || path == "~") return "/~";
        if (path.StartsWith('/')) return SftpUri.NormalizePath(path);
        if (path.StartsWith("~/", StringComparison.Ordinal)) return SftpUri.NormalizePath("/" + path);
        return SftpUri.NormalizePath("/~/" + path);
    }

    /// <summary>The saved source a scan of <paramref name="uri"/> uses: same host and port, same user (or any if the path has none).</summary>
    public static SourceProfile? MatchProfile(SftpUri uri)
    {
        bool anyUser = uri.User.Length == 0;
        return SourceProfiles.Match(ProviderId, uri.ToString(), SftpPathOps.Instance, p =>
            ServerOf(p) is { } s && (anyUser || p.UserName.Trim().Length > 0)
                ? SftpUri.FormatAuthority(anyUser ? "" : p.UserName.Trim(), s.Host, s.Port) + "/"
                : "\0"); // fits no path
    }

    private static string NoProfile(string path) =>
        $"Für {path} ist keine SFTP-Quelle angelegt. Bitte zuerst unter „☁ Quellen …“ eine SFTP-Quelle für diesen Server anlegen.";

    // ------------------------------------------------------------------ IStorageProvider

    public string Id => ProviderId;

    public string DisplayName => _profile != null && AuthorityOf(_profile) is { } a ? "SFTP " + a[SftpUri.Prefix.Length..] : "SFTP";

    public StorageCaps Caps => SftpCaps;

    public PathOps Paths => SftpPathOps.Instance;

    public int? ScanThreads => SftpStorage.Threads;

    public SourceProfile? Profile => _profile;

    public bool TryParse(string text, out StorageLocation location)
    {
        location = default;
        if (!SftpUri.TryParse(text, out var uri)) return false;
        if (MatchProfile(uri) is { } p && AuthorityOf(p) is { } authority)
        {
            location = new StorageLocation(For(p), authority + uri.Path);
            return true;
        }
        // No saved source: the path is understood (so the scan can ask to create one), but nothing connects.
        location = new StorageLocation(Generic, uri.ToString());
        return true;
    }

    public string Normalize(string path)
    {
        if (!SftpUri.TryParse(path, out var uri)) throw new ArgumentException("Kein SFTP-Pfad: " + path);
        if (_profile != null && uri.User.Length == 0 && AuthorityOf(_profile) is { } a) return a + uri.Path;
        return uri.ToString();
    }

    /// <summary>The bound source that serves this path (this one, or the matching one for <see cref="Generic"/>).</summary>
    public SftpSource Resolve(string path)
    {
        if (_profile != null) return this;
        if (SftpUri.TryParse(path, out var uri) && MatchProfile(uri) is { } p) return For(p);
        throw new IOException(NoProfile(path));
    }

    /// <summary>The connection of a bound source, created on first use (reads the secret now).</summary>
    private SftpProvider Inner
    {
        get
        {
            if (_profile == null) throw new InvalidOperationException("Keine SFTP-Quelle.");
            lock (_lock) return _inner ??= new SftpProvider(OptionsFor(_profile, ReadAuth(_profile), persist: true));
        }
    }

    private SftpProvider Server(string path) => Resolve(path).Inner;

    public bool FolderExists(string path) => Server(path).FolderExists(path);

    public StorageRoot ResolveRoot(string path) => Server(path).ResolveRoot(path);

    public bool TryGetCapacity(string rootPath, out long total, out long free)
    {
        total = free = 0;
        try
        {
            return Server(rootPath).TryGetCapacity(rootPath, out total, out free);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IEnumerable<T> Enumerate<T>(string folder, EntryTransform<T> transform, ListOptions options)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(options);
        return Iterate(folder, transform, options);
    }

    private IEnumerable<T> Iterate<T>(string folder, EntryTransform<T> transform, ListOptions options)
    {
        // Lazy like the others: nothing (not even the secret) is touched before the enumeration starts. The run counts
        // the listing and ends it once the scan is cancelled (SFTP has no cost limit, so it never pauses).
        options.Run?.BeforeRequest();
        foreach (var item in Server(folder).Enumerate(folder, transform, options)) yield return item;
    }

    public Stream OpenRead(string path) => Server(path).OpenRead(path);

    // ------------------------------------------------------------------ IConnectedStorage

    private IEnumerable<SftpSource> Served => _profile != null ? [this] : Bound.Values;

    public bool IsConnected => Served.Any(s => s._inner?.IsConnected == true);

    /// <summary>Connects a bound source (the generic one has no server: nothing happens).</summary>
    public void Connect(CancellationToken ct)
    {
        if (_profile != null) Inner.Connect(ct);
    }

    public void Disconnect()
    {
        foreach (var s in Served) s._inner?.Disconnect();
    }

    public IDisposable CancelWith(CancellationToken ct)
    {
        if (_profile != null) return Inner.CancelWith(ct);
        // A rescan below a loaded snapshot runs through the generic source: a cancel closes the bound connections.
        return ct.Register(() => Task.Run(Disconnect));
    }

    /// <summary>"~" (home folder) resolved on the server; connects if needed.</summary>
    public string ResolvePath(string path) => Server(path).ResolvePath(path);

    /// <summary>The trusted host key of the open (or last) connection, for display.</summary>
    public string? HostKeySha256 => _inner?.HostKeySha256 ?? SftpFingerprint.Normalize(_profile?.HostKeySha256);

    public void Dispose() => Disconnect();

    // ------------------------------------------------------------------ connection settings

    /// <summary>The sign-in of a saved profile with its secret from the Credential Manager (or the given ones).</summary>
    internal static SftpAuth ReadAuth(SourceProfile p, string? password = null, string? passphrase = null)
    {
        if (p.Auth == SourceAuth.KeyFile)
        {
            if (string.IsNullOrWhiteSpace(p.KeyFile))
                throw new SftpLoginException($"Für die Quelle „{p.Name}“ ist keine Schlüsseldatei angegeben.");
            return SftpAuth.KeyFile(KeyPath(p.KeyFile), passphrase ?? SourceSecrets.Read(p, SecretSlot.Passphrase));
        }
        var pw = password ?? SourceSecrets.Read(p, SecretSlot.Secret);
        if (pw == null)
            throw new SftpLoginException($"Für die Quelle „{p.Name}“ ist kein Passwort gespeichert. Bitte unter „☁ Quellen …“ eintragen.");
        return SftpAuth.Password(pw);
    }

    /// <summary>A key file path as typed: quotes removed, %USERPROFILE% and the like expanded.</summary>
    internal static string KeyPath(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    /// <summary>
    /// Options of a profile. <paramref name="persist"/>: a host key the user trusts is stored with the profile
    /// (<see cref="SftpStorage.HostKeyTrusted"/>); the connection test only asks.
    /// </summary>
    internal static SftpConnectionOptions OptionsFor(SourceProfile p, SftpAuth auth, bool persist)
    {
        var server = ServerOf(p) ?? throw new IOException($"Ungültiger Server in der Quelle „{p.Name}“.");
        var profile = p.Clone();
        return new SftpConnectionOptions
        {
            Host = server.Host,
            Port = server.Port,
            UserName = p.UserName.Trim(),
            Auth = auth,
            HostKeySha256 = SftpFingerprint.Normalize(p.HostKeySha256),
            MaxParallelListings = SftpStorage.ParallelListings,
            ConfirmHostKey = fingerprint =>
            {
                if (SftpStorage.AskHostKey?.Invoke(profile, fingerprint) != true) return false;
                if (persist) SftpStorage.HostKeyTrusted?.Invoke(profile, fingerprint);
                return true;
            },
        };
    }
}
