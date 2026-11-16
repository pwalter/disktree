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
/// <para>
/// Owners (<see cref="IOwnerSource"/>: uid / gid / mode bits from the listings, names via getent or /etc/passwd) and
/// changes (<see cref="IStorageWriter"/>: delete for good, rename, move; links are never followed) are implemented
/// here, but this core provider keeps reporting read-only <see cref="Caps"/>: the app unlocks them through
/// <see cref="SftpSource"/>, which belongs to a saved source.
/// </para>
/// </summary>
public sealed class SftpProvider : IStorageProvider, IConnectedStorage, IContentHashSource, IOwnerSource, IStorageWriter
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

    /// <summary>The folder the server starts the session in (usually the user's home, "/home/user"); null before connecting.</summary>
    public string? HomeDirectory => Volatile.Read(ref _connection)?.Home;

    /// <summary>Is this the home folder shortcut: the remote path "/~" or "/~/…" ("sftp://host/~/x", or "~" in a profile)?</summary>
    public static bool IsHomePath(string remotePath) => remotePath == "/~" || remotePath.StartsWith("/~/", StringComparison.Ordinal);

    /// <summary>
    /// "sftp://user@host/~/x" as the absolute path below the user's home folder (connects if needed); other paths of
    /// this server come back normalized. <see cref="IOException"/> if the server did not tell its start folder.
    /// </summary>
    public string ResolvePath(string path)
    {
        var target = Normalize(path);
        var remote = RemotePath(target);
        if (!IsHomePath(remote)) return target;
        if (!IsConnected) Connect(CancellationToken.None);
        var home = HomeDirectory ?? throw new IOException(
            $"Der Server {RootUri} nennt kein Home-Verzeichnis; bitte einen absoluten Pfad angeben (z. B. /home/{_root.User}).");
        return _root.Authority + SftpUri.NormalizePath(home + remote[2..]);
    }

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

    // ------------------------------------------------------------------ contents (duplicate search)

    /// <summary>SFTP has no content tags: the duplicate search compares contents.</summary>
    public ContentTagKind ClassifyTag(string? listedTag) => ContentTagKind.None;

    public int TagRequests => 0;

    public bool TryGetContentTag(string path, string? listedTag, ListRun run, out string tag, out bool isStrong)
    {
        tag = "";
        isStrong = false;
        return false;
    }

    /// <summary>The file from <paramref name="offset"/> on (the caller reads <paramref name="length"/> bytes of it).</summary>
    public Stream OpenRead(string path, long offset, long length, ListRun run)
    {
        run.Cancellation.ThrowIfCancellationRequested();
        return Connection.OpenRead(RemotePath(path), path, offset);
    }

    /// <summary>One open per range; SFTP has no costs per request.</summary>
    public long RequestsFor(long length) => 1;

    // ------------------------------------------------------------------ owners (uid / gid / mode)

    public string RightsTitle => "Rechte (POSIX)";

    public bool HasGroups => true;

    /// <summary>Owners come with every listing: sampling would not save a request.</summary>
    public bool SamplingHelps => false;

    /// <summary>The user and group names of this server (looked up once per connection, see <see cref="SftpConnection.OwnerNames"/>).</summary>
    public SftpOwnerNames OwnerNames(CancellationToken ct = default) => Connection.OwnerNames(ct);

    public Task<OwnerDetails?> GetOwnerAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        var remote = RemotePath(path);
        using var cancel = CancelWith(ct);
        if (Connection.Stat(remote, path) is not { } e) return null;
        var names = OwnerNames(ct);
        Connection.ResolveMissing(names, [e.Uid], [e.Gid], ct);
        return (OwnerDetails?)new OwnerDetails(names.UserWithId(e.Uid), names.GroupWithId(e.Gid),
            $"{PosixMode.Format(e.Mode)}  {names.User(e.Uid)}:{names.Group(e.Gid)}  ({PosixMode.Octal(e.Mode)})");
    }, ct);

    public string[] GetRights(string path, CancellationToken ct)
    {
        var remote = RemotePath(path);
        using var cancel = CancelWith(ct);
        if (Connection.Stat(remote, path) is not { } e) return ["Nicht gefunden: " + path];
        var names = OwnerNames(ct);
        Connection.ResolveMissing(names, [e.Uid], [e.Gid], ct);
        var lines = PosixMode.Describe(e.Mode, names.User(e.Uid), names.Group(e.Gid)).ToList();
        lines.Add(names.Source switch
        {
            OwnerNameSource.Getent => "Namen: getent auf dem Server",
            OwnerNameSource.EtcFiles => "Namen: /etc/passwd und /etc/group (kein Befehlszugriff)",
            _ => "Namen: auf dem Server nicht ermittelbar, daher uid/gid",
        });
        return lines.ToArray();
    }

    /// <summary>Folders listed at the same time by <see cref="Aggregate"/>.</summary>
    private int AggregateParallel => Math.Clamp(_options.MaxParallelListings / 2, 1, 4);

    /// <summary>
    /// Lists the subtree again, level by level (a few folders at a time over the one connection), and sums the files by
    /// uid and by gid as the listing reports them. Links are not followed and not counted. Folders that cannot be listed
    /// count as failed; a connection that breaks for good ends the run with an error and the partial result.
    /// </summary>
    public OwnerResult Aggregate(string folder, long minSize, Action<long, long>? progress, CancellationToken ct)
    {
        var byUid = new Dictionary<int, (long Bytes, long Files)>();
        var byGid = new Dictionary<int, (long Bytes, long Files)>();
        long seen = 0, failed = 0;
        bool cancelled = false;
        string? error = null;
        var sync = new object();

        string rootRemote;
        try { rootRemote = RemotePath(folder); }
        catch (ArgumentException ex) { return new OwnerResult([], 0, 0, 0, false, ex.Message, true); }

        using (CancelWith(ct))
        {
            var level = new List<string> { rootRemote };
            try
            {
                while (level.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var next = new List<string>();
                    var rootLevel = level.Count == 1 && level[0] == rootRemote && seen == 0 && failed == 0;
                    Parallel.ForEach(level, new ParallelOptions { MaxDegreeOfParallelism = AggregateParallel, CancellationToken = ct }, dir =>
                    {
                        List<RemoteEntry> entries;
                        try
                        {
                            entries = Connection.List(dir, _root.Authority + dir);
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException and not SftpLoginException
                                                       || ex is DirectoryNotFoundException)
                        {
                            if (rootLevel) throw;
                            Interlocked.Increment(ref failed);
                            return;
                        }
                        lock (sync)
                        {
                            foreach (var e in entries)
                            {
                                if (e.Kind == RemoteKind.Directory) { next.Add(dir == "/" ? "/" + e.Name : dir + "/" + e.Name); continue; }
                                if (e.Kind != RemoteKind.File) continue; // links: neither followed nor counted
                                seen++;
                                Add(byUid, e.Uid, e.Length);
                                Add(byGid, e.Gid, e.Length);
                            }
                        }
                        progress?.Invoke(Interlocked.Read(ref seen), Interlocked.Read(ref failed));
                    });
                    level = next;
                }
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (AggregateException ag) when (ag.InnerExceptions.All(e => e is OperationCanceledException)) { cancelled = true; }
            catch (Exception ex) when (Unwrap(ex) is IOException or UnauthorizedAccessException)
            {
                error = Unwrap(ex).Message;
            }
        }

        progress?.Invoke(seen, failed);
        // Names: looked up once per connection (also after a cancel, they are cheap); a broken connection leaves the ids.
        SftpOwnerNames names;
        try
        {
            names = OwnerNames(CancellationToken.None);
            Connection.ResolveMissing(names, byUid.Keys, byGid.Keys, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            names = new SftpOwnerNames();
        }
        return new OwnerResult(Rows(byUid, names.User), seen, 0, failed, cancelled, error, true, Rows(byGid, names.Group));
    }

    private static Exception Unwrap(Exception ex) =>
        ex is AggregateException ag ? ag.Flatten().InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) ?? ag : ex;

    private static void Add(Dictionary<int, (long Bytes, long Files)> d, int id, long size)
    {
        var (b, n) = d.GetValueOrDefault(id);
        d[id] = (b + size, n + 1);
    }

    private static List<OwnerRow> Rows(Dictionary<int, (long Bytes, long Files)> d, Func<int, string> name) =>
        d.Select(kv => new OwnerRow(name(kv.Key), kv.Value.Bytes, kv.Value.Files))
            .OrderByDescending(r => r.Bytes).ThenBy(r => r.Owner, StringComparer.Ordinal).ToList();

    // ------------------------------------------------------------------ changes (delete for good, rename, move)

    private static StorageItemInfo Info(RemoteEntry e) =>
        new(e.Kind == RemoteKind.Directory, e.Kind == RemoteKind.Link, e.Length, e.ModifiedTicks, e.AccessedTicks);

    public StorageItemInfo? Stat(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var remote = RemotePath(path);
        using var cancel = CancelWith(ct);
        return Connection.Stat(remote, path) is { } e ? Info(e) : null;
    }

    public bool Exists(string path, CancellationToken ct) => Stat(path, ct) != null;

    /// <summary>
    /// Removes a file / link, or a folder (recursive: depth first, links removed as links, never followed). Errors of single
    /// entries (no permission, gone, not empty) are collected and the rest goes on; a broken connection ends it.
    /// </summary>
    public DeleteOutcome Delete(string path, bool recursive, Action<DeleteOutcome>? progress, CancellationToken ct)
    {
        var remote = RemotePath(path);
        if (remote == "/") throw new IOException("Die Wurzel des Servers wird nicht gelöscht.");
        var outcome = new DeleteOutcome();
        using var cancel = CancelWith(ct);
        ct.ThrowIfCancellationRequested();
        var e = Connection.Stat(remote, path) ?? throw new FileNotFoundException("Nicht gefunden: " + path);
        long lastReport = Environment.TickCount64;

        void Report()
        {
            long now = Environment.TickCount64;
            if (now - lastReport < 150) return;
            lastReport = now;
            progress?.Invoke(outcome);
        }

        bool One(Action action, string full)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                action();
                return true;
            }
            catch (Exception ex) when (IsPerEntry(ex))
            {
                outcome.Failed.Add((full, ex.Message));
                return false;
            }
        }

        bool Tree(string dir)
        {
            var full = _root.Authority + dir;
            List<RemoteEntry> entries;
            try { entries = Connection.List(dir, full); }
            catch (Exception ex) when (IsPerEntry(ex))
            {
                outcome.Failed.Add((full, ex.Message));
                return false;
            }
            bool all = true;
            foreach (var child in entries)
            {
                ct.ThrowIfCancellationRequested();
                var sub = dir == "/" ? "/" + child.Name : dir + "/" + child.Name;
                if (child.Kind == RemoteKind.Directory)
                {
                    all &= Tree(sub);
                }
                else if (One(() => Connection.Remove(sub, _root.Authority + sub), _root.Authority + sub))
                {
                    outcome.Files++;
                    outcome.Bytes += child.Length;
                    Report();
                }
                else all = false;
            }
            // A folder that still has content is left (the errors below say why).
            if (!all) return false;
            if (!One(() => Connection.RemoveDirectory(dir, full), full)) return false;
            outcome.Folders++;
            Report();
            return true;
        }

        if (e.Kind != RemoteKind.Directory)
        {
            if (One(() => Connection.Remove(remote, path), path))
            {
                outcome.Files++;
                outcome.Bytes += e.Length;
            }
        }
        else if (recursive) Tree(remote);
        else if (One(() => Connection.RemoveDirectory(remote, path), path)) outcome.Folders++;

        progress?.Invoke(outcome);
        return outcome;
    }

    /// <summary>An error of this one entry (no permission, gone, server refused): collected; connection problems are not.</summary>
    private static bool IsPerEntry(Exception ex) =>
        ex is UnauthorizedAccessException and not SftpLoginException
        || ex is FileNotFoundException or DirectoryNotFoundException
        || SftpConnection.IsRefusedRequest(ex);

    public string Rename(string path, string newName, CancellationToken ct)
    {
        if (newName.Length == 0 || newName is "." or ".." || newName.Contains('/') || newName.Contains('\0'))
            throw new IOException($"Ungültiger Name: „{newName}“");
        var remote = RemotePath(path);
        if (remote == "/") throw new IOException("Die Wurzel des Servers kann nicht umbenannt werden.");
        var parent = remote[..Math.Max(1, remote.LastIndexOf('/'))];
        var target = parent == "/" ? "/" + newName : parent + "/" + newName;
        Relocate(remote, target, replace: false, path, ct);
        return _root.Authority + target;
    }

    public string Move(string path, string targetFolder, string? newName, bool replace, CancellationToken ct)
    {
        var remote = RemotePath(path);
        var folder = RemotePath(targetFolder);
        if (remote == "/") throw new IOException("Die Wurzel des Servers kann nicht verschoben werden.");
        var name = newName ?? remote[(remote.LastIndexOf('/') + 1)..];
        if (name.Length == 0 || name is "." or ".." || name.Contains('/')) throw new IOException($"Ungültiger Name: „{name}“");
        if (folder == remote || folder.StartsWith(remote + "/", StringComparison.Ordinal))
            throw new IOException("Ein Ordner kann nicht in sich selbst verschoben werden.");
        var target = folder == "/" ? "/" + name : folder + "/" + name;
        Relocate(remote, target, replace, path, ct);
        return _root.Authority + target;
    }

    /// <summary>Rename / move: refuses an existing target, except a file replacing a file with <paramref name="replace"/>.</summary>
    private void Relocate(string from, string to, bool replace, string display, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var cancel = CancelWith(ct);
        var source = Connection.Stat(from, display) ?? throw new FileNotFoundException("Nicht gefunden: " + display);
        if (Connection.Stat(to, _root.Authority + to) is { } existing)
        {
            if (!replace || source.Kind == RemoteKind.Directory || existing.Kind == RemoteKind.Directory)
                throw new IOException($"Das Ziel existiert schon: {_root.Authority + to}");
            // Atomic where the server can (OpenSSH); otherwise the old file is removed first.
            if (Connection.TryPosixRename(from, to, display)) return;
            Connection.Remove(to, _root.Authority + to);
        }
        ct.ThrowIfCancellationRequested();
        Connection.Rename(from, to, display);
    }

    public void CreateDirectory(string path, CancellationToken ct)
    {
        var remote = RemotePath(path);
        using var cancel = CancelWith(ct);
        Connection.CreateDirectory(remote, path);
    }

    public IReadOnlyCollection<string> ListNames(string folder, CancellationToken ct)
    {
        var remote = RemotePath(folder);
        using var cancel = CancelWith(ct);
        return Connection.List(remote, folder).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
    }


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

        var folder = path == null ? provider.RootUri : provider.ResolvePath(SftpUri.HasScheme(path) ? path : provider._root.Authority + SftpUri.NormalizePath(path));
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
