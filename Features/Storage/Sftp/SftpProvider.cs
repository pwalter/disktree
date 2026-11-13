using System.IO;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Storage.Sftp;

/// <summary>
/// An SFTP server as a storage source (read only). One instance belongs to one server and login
/// (<see cref="SftpConnectionOptions"/>); its paths are full URIs "sftp://user@host[:port]/remote/path" (see
/// <see cref="SftpUri"/>), separated by '/', case-sensitive.
/// <para>
/// The connection is opened on first use (or by <see cref="Connect"/>) and shared by all scanner threads; at most
/// <see cref="SftpConnectionOptions.MaxParallelListings"/> folders are listed at the same time. Symbolic links are not
/// followed: they are listed as files with 0 bytes and <see cref="StorageEntry.IsReparsePoint"/> (their target is not
/// asked for, that would cost a request per link). Entries whose name starts with '.' get
/// <see cref="FileAttributes.Hidden"/> (Unix convention), so the search filter "Versteckt" finds them; there are no
/// other attributes. Times: modified and accessed, no creation time.
/// </para>
/// <para>
/// Capacity (statvfs@openssh.com) is reported for the root "/" when the scanned folder lies on the same file system;
/// otherwise, or if the server does not offer statvfs, the tree starts at the scanned folder (like a network share).
/// </para>
/// </summary>
public sealed class SftpProvider : IStorageProvider, IConnectedStorage
{
    public const string ProviderId = "sftp";

    private readonly SftpConnectionOptions _options;
    private readonly SftpUri _root;
    private readonly object _lock = new();
    private SftpConnection? _connection;
    private bool _disposed;

    public SftpProvider(SftpConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _root = new SftpUri(options.UserName, options.Host.Trim().ToLowerInvariant(), options.Port, "/");
    }

    public string Id => ProviderId;

    public string DisplayName => "SFTP " + _root.Authority[SftpUri.Prefix.Length..];

    public StorageCaps Caps =>
        StorageCaps.Capacity | StorageCaps.AccessedTime | StorageCaps.Hierarchical | StorageCaps.ReparsePoints
        | StorageCaps.CaseSensitive | StorageCaps.ParallelListing | StorageCaps.LiveFileList | StorageCaps.ContentHash;

    public PathOps Paths => SftpPathOps.Instance;

    public SftpConnectionOptions Options => _options;

    /// <summary>"sftp://user@host[:port]/": the top of every tree of this provider.</summary>
    public string RootUri => _root.RootUri;

    /// <summary>
    /// The trusted host key ("SHA256:…"): the one from the options, or the one confirmed while connecting (store it in the
    /// source profile so the next session does not ask again). Null before the first connection without a known key.
    /// </summary>
    public string? HostKeySha256 => _connection?.HostKey ?? SftpFingerprint.Normalize(_options.HostKeySha256);

    /// <summary>SSH version string of the server once connected ("SSH-2.0-OpenSSH_9.9").</summary>
    public string? ServerVersion => _connection?.ServerVersion;

    private SftpConnection Connection
    {
        get
        {
            var c = Volatile.Read(ref _connection);
            if (c != null) return c;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _connection ??= new SftpConnection(_options);
            }
        }
    }

    // ------------------------------------------------------------------ paths

    public bool TryParse(string text, out StorageLocation location)
    {
        location = default;
        if (!SftpUri.TryParse(text, out var uri) || !IsMine(uri)) return false;
        location = new StorageLocation(this, _root.Authority + uri.Path);
        return true;
    }

    /// <summary>The canonical form of a path of this server; <see cref="ArgumentException"/> for anything else.</summary>
    public string Normalize(string path)
    {
        if (!SftpUri.TryParse(path, out var uri))
            throw new ArgumentException("Kein SFTP-Pfad: " + path);
        if (!IsMine(uri))
            throw new ArgumentException($"Der Pfad gehört nicht zu {RootUri}: {path}");
        return _root.Authority + uri.Path;
    }

    /// <summary>Same server and port; the user may be left out ("sftp://host/path" means the configured user).</summary>
    private bool IsMine(SftpUri uri) =>
        uri.Host == _root.Host && uri.Port == _root.Port && (uri.User.Length == 0 || uri.User == _root.User);

    /// <summary>The remote path ("/a/b") of a full path of this server.</summary>
    public string RemotePath(string path)
    {
        if (!SftpUri.TryParse(path, out var uri) || !IsMine(uri))
            throw new ArgumentException($"Der Pfad gehört nicht zu {RootUri}: {path}");
        return uri.Path;
    }

    // ------------------------------------------------------------------ connection

    public bool IsConnected => Volatile.Read(ref _connection)?.IsConnected == true;

    public void Connect(CancellationToken ct) => Connection.Connect(ct);

    public void Disconnect() => Volatile.Read(ref _connection)?.Abort();

    public IDisposable CancelWith(CancellationToken ct)
    {
        var connection = Connection;
        // Off the cancelling thread (usually the UI): closing a connection must not block it.
        return ct.Register(() => Task.Run(connection.Abort));
    }

    public void Dispose()
    {
        SftpConnection? c;
        lock (_lock)
        {
            _disposed = true;
            c = _connection;
        }
        c?.Dispose();
    }

    // ------------------------------------------------------------------ IStorageProvider

    /// <summary>
    /// Asks the server (opens the connection if needed). Not found or no access: false; connection, host key and login
    /// problems are thrown (<see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>) so the caller can show
    /// the real reason instead of "Ordner nicht gefunden".
    /// </summary>
    public bool FolderExists(string path)
    {
        string remote;
        try { remote = RemotePath(path); }
        catch (ArgumentException) { return false; }
        return Connection.IsFolder(remote, path);
    }

    public StorageRoot ResolveRoot(string path)
    {
        var target = Normalize(path);
        var remote = RemotePath(target);
        var top = RootUri;

        var rootCap = Connection.Capacity("/", top);
        if (rootCap is { } r)
        {
            // Percentages refer to the root's file system: only if the folder really lies on it (not a mount below "/").
            if (remote == "/") return new StorageRoot(top, top, r.Total, r.Free);
            var cap = Connection.Capacity(remote, target);
            if (cap is { } c && c.Id == r.Id) return new StorageRoot(target, top, r.Total, r.Free);
        }
        return new StorageRoot(target, target, 0, 0);
    }

    /// <summary>Only for the root "sftp://…/" (the top of a tree with capacity); false for other folders or without statvfs.</summary>
    public bool TryGetCapacity(string rootPath, out long total, out long free)
    {
        total = free = 0;
        if (!SftpUri.TryParse(rootPath, out var uri) || !IsMine(uri) || uri.Path != "/") return false;
        try
        {
            if (Connection.Capacity("/", RootUri) is not { } c) return false;
            (total, free) = (c.Total, c.Free);
            return true;
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
        // Lazy like the disk: the request is sent when the enumeration starts, errors come from there.
        var entries = List(folder, options.IgnoreInaccessible);
        if (entries == null) yield break;

        var stamps = options.Stamps;
        foreach (var e in entries)
            yield return Transform(e, stamps, transform);
    }

    private List<RemoteEntry>? List(string folder, bool ignoreInaccessible)
    {
        var remote = RemotePath(folder);
        try
        {
            return Connection.List(remote, folder);
        }
        catch (UnauthorizedAccessException ex) when (ignoreInaccessible && ex is not SftpLoginException)
        {
            return null;
        }
    }

    private static T Transform<T>(in RemoteEntry e, StampSet stamps, EntryTransform<T> transform)
    {
        bool dir = e.Kind == RemoteKind.Directory;
        bool link = e.Kind == RemoteKind.Link;
        var attr = dir ? FileAttributes.Directory : link ? FileAttributes.ReparsePoint : FileAttributes.Normal;
        if (e.Name.StartsWith('.')) attr = (attr & ~FileAttributes.Normal) | FileAttributes.Hidden;

        var entry = new StorageEntry
        {
            Name = e.Name.AsSpan(),
            IsDirectory = dir,
            IsReparsePoint = link,
            Length = e.Length,
            Attributes = attr,
            ModifiedTicks = (stamps & StampSet.Modified) != 0 ? e.ModifiedTicks : 0,
            CreatedTicks = 0,
            AccessedTicks = (stamps & StampSet.Accessed) != 0 ? e.AccessedTicks : 0,
        };
        return transform(in entry);
    }

    public Stream OpenRead(string path) => Connection.OpenRead(RemotePath(path), path);


    // ------------------------------------------------------------------ connection test

    /// <summary>
    /// For the connection dialog: connects (host key check incl. <see cref="SftpConnectionOptions.ConfirmHostKey"/>),
    /// lists <paramref name="path"/> (default: the root) and disconnects. Throws like <see cref="Connect"/>
    /// (<see cref="SftpHostKeyException"/> carries the fingerprint of an unknown or changed key,
    /// <see cref="SftpLoginException"/> a failed login), a folder that cannot be listed as <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    public static SftpConnectionTestResult TestConnection(SftpConnectionOptions options, string? path = null, CancellationToken ct = default)
    {
        using var provider = new SftpProvider(options);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        provider.Connect(ct);
        var connectTime = sw.Elapsed;

        var folder = path == null ? provider.RootUri : provider.Normalize(SftpUri.HasScheme(path) ? path : provider._root.Authority + SftpUri.NormalizePath(path));
        using var cancel = provider.CancelWith(ct);
        var entries = provider.Connection.List(provider.RemotePath(folder), folder);
        ct.ThrowIfCancellationRequested();

        bool hasCapacity = provider.TryGetCapacity(provider.RootUri, out long total, out long free);
        var names = entries
            .OrderBy(e => e.Kind != RemoteKind.Directory).ThenBy(e => e.Name, StringComparer.Ordinal)
            .Take(50)
            .Select(e => e.Kind == RemoteKind.Directory ? e.Name + "/" : e.Name)
            .ToList();

        return new SftpConnectionTestResult(
            provider.HostKeySha256!, provider.ServerVersion ?? "", folder,
            entries.Count(e => e.Kind == RemoteKind.Directory), entries.Count(e => e.Kind != RemoteKind.Directory), names,
            hasCapacity ? total : 0, hasCapacity ? free : 0, connectTime);
    }
}

/// <summary>What <see cref="SftpProvider.TestConnection"/> found.</summary>
/// <param name="HostKeySha256">The (now trusted) host key, to be stored with the source.</param>
/// <param name="ServerVersion">The server's SSH version string.</param>
/// <param name="Folder">The listed folder (full SFTP path).</param>
/// <param name="Folders">Number of subfolders in it.</param>
/// <param name="Files">Number of other entries (files, links).</param>
/// <param name="Sample">Up to 50 names, folders first and with '/' at the end.</param>
/// <param name="Total">Capacity of the server's root file system (0 = unknown).</param>
/// <param name="Free">Free bytes of it.</param>
/// <param name="ConnectTime">How long connecting and logging in took.</param>
public sealed record SftpConnectionTestResult(
    string HostKeySha256, string ServerVersion, string Folder, int Folders, int Files, IReadOnlyList<string> Sample,
    long Total, long Free, TimeSpan ConnectTime);
