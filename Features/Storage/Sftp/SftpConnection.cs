using System.IO;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace DiskTree.Features.Storage.Sftp;

/// <summary>What kind of entry a listing returned (no SSH.NET types leave this file).</summary>
internal enum RemoteKind : byte { File, Directory, Link }

/// <summary>One listed entry; times are UTC ticks, 0 if the server did not send them.</summary>
internal readonly record struct RemoteEntry(string Name, RemoteKind Kind, long Length, long ModifiedTicks, long AccessedTicks);

/// <summary>Capacity of a file system (statvfs); <see cref="Id"/> tells two file systems apart.</summary>
internal readonly record struct RemoteCapacity(long Total, long Free, (ulong, ulong, ulong) Id);

/// <summary>
/// The one connection of an <see cref="SftpProvider"/> to its server. Everything that touches SSH.NET lives here, so
/// the SSH.NET assembly (and BouncyCastle) is only loaded once SFTP is really used.
/// <para>
/// One <see cref="SftpClient"/> serves all threads: SFTP requests carry ids and SSH.NET matches the answers to them,
/// so several listings run over the same channel at the same time (pipelined). <see cref="MaxParallel"/> of them at
/// most; further callers wait. If the connection breaks, the request that noticed it reconnects once (other requests
/// of the same moment reuse that new connection) and repeats; if that fails, the error is reported and further
/// requests fail fast for <see cref="RetryPause"/> instead of each waiting for the connect timeout.
/// </para>
/// </summary>
internal sealed class SftpConnection : IDisposable
{
    /// <summary>After a failed reconnect: how long requests fail without trying again.</summary>
    internal static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(30);

    private readonly SftpConnectionOptions _o;
    private readonly string _where;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _slots;

    private Live? _live;
    private int _generation;
    private int _abortedGeneration = -1;
    private long _brokenUntil;
    private Exception? _brokenBy;
    private string? _trustedKey;
    private bool _disposed;

    /// <summary>A connected client together with what has to be disposed with it.</summary>
    private sealed class Live(SftpClient client, IDisposable[] extras, int generation) : IDisposable
    {
        public SftpClient Client { get; } = client;
        public int Generation { get; } = generation;

        /// <summary>Cancelled by <see cref="Abort"/>: ends the listings running on this client at once.</summary>
        public CancellationTokenSource Stop { get; } = new();

        /// <summary>
        /// Taken out of service (reconnect, abort). SSH.NET may then fail requests that still run on it with odd errors
        /// (even a NullReferenceException inside ListDirectoryAsync): they all count as a lost connection.
        /// </summary>
        public volatile bool Dropped;
        public bool IsConnected
        {
            get
            {
                try { return Client.IsConnected; }
                catch (ObjectDisposedException) { return false; }
            }
        }

        public void Dispose()
        {
            try { Client.Dispose(); } catch (Exception) { /* closing a broken connection may fail; nothing to do */ }
            foreach (var d in extras)
            {
                try { d.Dispose(); } catch (Exception) { }
            }
        }
    }

    public SftpConnection(SftpConnectionOptions options)
    {
        _o = options;
        _where = $"{options.UserName}@{options.Host}" + (options.Port == SftpUri.DefaultPort ? "" : ":" + options.Port);
        _slots = new SemaphoreSlim(options.MaxParallelListings, options.MaxParallelListings);
        _trustedKey = SftpFingerprint.Normalize(options.HostKeySha256);
    }

    public int MaxParallel => _o.MaxParallelListings;

    /// <summary>The trusted host key ("SHA256:…"): from the options or confirmed while connecting.</summary>
    public string? HostKey => Volatile.Read(ref _trustedKey);

    public string? ServerVersion { get; private set; }

    public bool IsConnected
    {
        get { lock (_lock) return _live?.IsConnected == true; }
    }

    /// <summary>How many requests run right now (tests).</summary>
    internal int Busy => MaxParallel - _slots.CurrentCount;

    // ------------------------------------------------------------------ connecting

    public void Connect(CancellationToken ct)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _brokenUntil = 0;
            _brokenBy = null;
            if (_live?.IsConnected == true) return;
            Drop();
            try
            {
                _live = Open(ct);
            }
            catch (Exception ex) when (Map(ex, _where, out var mapped))
            {
                throw mapped;
            }
        }
    }

    /// <summary>Closes the connection; requests running on it end with <see cref="OperationCanceledException"/>.</summary>
    public void Abort()
    {
        // First (without waiting for a connect that may hold the lock): end what runs on the current client.
        Volatile.Write(ref _abortedGeneration, Volatile.Read(ref _generation));
        Volatile.Read(ref _live)?.Stop.Cancel();
        lock (_lock) Drop();
    }

    public void Dispose()
    {
        Volatile.Write(ref _abortedGeneration, int.MaxValue);
        Volatile.Read(ref _live)?.Stop.Cancel();
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Drop();
        }
    }

    /// <summary>Takes the client out of service; it is closed in the background (a dead socket may take a while).</summary>
    private void Drop()
    {
        var live = _live;
        _live = null;
        if (live == null) return;
        live.Dropped = true;
        Task.Run(live.Dispose);
    }

    private bool IsAborted(int generation) => generation <= Volatile.Read(ref _abortedGeneration);

    private Live Current()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_live?.IsConnected == true) return _live;
            ThrowIfBroken();
            return Reopen();
        }
    }

    private Live Reconnect(int failedGeneration)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsAborted(failedGeneration)) throw new OperationCanceledException("Abgebrochen.");
            // Another request already reconnected after the same break.
            if (_live is { } l && l.Generation != failedGeneration && l.IsConnected) return l;
            ThrowIfBroken();
            return Reopen();
        }
    }

    private void ThrowIfBroken()
    {
        if (_brokenBy != null && Environment.TickCount64 < _brokenUntil)
            throw new IOException($"Die Verbindung zu {_where} ist unterbrochen.", _brokenBy);
    }

    private Live Reopen()
    {
        Drop();
        try
        {
            _brokenBy = null;
            return _live = Open(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _brokenBy = ex;
            _brokenUntil = Environment.TickCount64 + (long)RetryPause.TotalMilliseconds;
            throw;
        }
    }

    /// <summary>Opens a client; an unknown host key is offered to <see cref="SftpConnectionOptions.ConfirmHostKey"/> first.</summary>
    private Live Open(CancellationToken ct)
    {
        try
        {
            return OpenWith(Volatile.Read(ref _trustedKey), ct);
        }
        catch (SftpHostKeyException e) when (e.Problem == SftpHostKeyProblem.Unknown && _o.ConfirmHostKey != null)
        {
            if (!_o.ConfirmHostKey(e.Fingerprint)) throw;
            return OpenWith(e.Fingerprint, ct);
        }
    }

    private Live OpenWith(string? expected, CancellationToken ct)
    {
        var (info, extras) = Login();
        var client = new SftpClient(info)
        {
            OperationTimeout = _o.OperationTimeout,
        };
        if (_o.KeepAliveInterval > TimeSpan.Zero) client.KeepAliveInterval = _o.KeepAliveInterval;

        string? presented = null;
        client.HostKeyReceived += (_, e) =>
        {
            presented = "SHA256:" + e.FingerPrintSHA256;
            e.CanTrust = expected != null && string.Equals(expected, presented, StringComparison.Ordinal);
        };

        try
        {
            // The connect timeout covers the whole handshake: a server that accepts the TCP connection but never answers
            // (paused, overloaded, port forwarder without backend) must not hang the scan.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_o.ConnectTimeout);
            try
            {
                var token = cts.Token;
                var connect = SynchronizationContext.Current == null ? client.ConnectAsync(token) : Task.Run(() => client.ConnectAsync(token));
                connect.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException oce) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"Zeitüberschreitung beim Verbinden mit {_where}.", oce);
            }
        }
        catch (Exception ex)
        {
            Dispose(client, extras);
            if (ex is OperationCanceledException) throw;
            if (presented != null && presented != expected)
                throw new SftpHostKeyException(expected == null ? SftpHostKeyProblem.Unknown : SftpHostKeyProblem.Changed,
                    _where, presented, expected);
            throw;
        }

        Volatile.Write(ref _trustedKey, presented);
        ServerVersion = info.ServerVersion;
        return new Live(client, extras, ++_generation);
    }

    private static void Dispose(SftpClient client, IDisposable[] extras)
    {
        try { client.Dispose(); } catch (Exception) { }
        foreach (var d in extras)
        {
            try { d.Dispose(); } catch (Exception) { }
        }
    }

    private (ConnectionInfo Info, IDisposable[] Extras) Login()
    {
        var user = _o.UserName;
        var extras = new List<IDisposable>();
        AuthenticationMethod[] methods;
        switch (_o.Auth)
        {
            case SftpAuth.PasswordAuth p:
            {
                var pw = new PasswordAuthenticationMethod(user, p.Secret);
                // Servers that only ask through keyboard-interactive: answer the hidden prompts with the password.
                var kbd = new KeyboardInteractiveAuthenticationMethod(user);
                kbd.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts)
                        if (!prompt.IsEchoed) prompt.Response = p.Secret;
                };
                extras.Add(pw);
                extras.Add(kbd);
                methods = [pw, kbd];
                break;
            }
            case SftpAuth.KeyFileAuth k:
                methods = [KeyMethod(user, () => k.Passphrase is null ? new PrivateKeyFile(k.Path) : new PrivateKeyFile(k.Path, k.Passphrase), extras)];
                break;
            case SftpAuth.KeyTextAuth k:
                methods = [KeyMethod(user, () =>
                {
                    using var s = new MemoryStream(Encoding.UTF8.GetBytes(k.Key));
                    return k.Passphrase is null ? new PrivateKeyFile(s) : new PrivateKeyFile(s, k.Passphrase);
                }, extras)];
                break;
            default:
                throw new ArgumentException("Unbekannte Anmeldeart.");
        }

        var info = new ConnectionInfo(_o.Host, _o.Port, user, methods) { Timeout = _o.ConnectTimeout };
        return (info, extras.ToArray());
    }

    private static PrivateKeyAuthenticationMethod KeyMethod(string user, Func<PrivateKeyFile> load, List<IDisposable> extras)
    {
        PrivateKeyFile key;
        try
        {
            key = load();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SftpLoginException("Die Schlüsseldatei wurde nicht gefunden.", ex);
        }
        catch (SshPassPhraseNullOrEmptyException ex)
        {
            throw new SftpLoginException("Der private Schlüssel ist verschlüsselt: bitte die Passphrase angeben.", ex);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            // Wrong passphrase, unknown format, broken file: SSH.NET and BouncyCastle throw all kinds of exceptions here.
            throw new SftpLoginException("Der private Schlüssel konnte nicht gelesen werden (Format oder Passphrase falsch).", ex);
        }
        extras.Add(key);
        var method = new PrivateKeyAuthenticationMethod(user, key);
        extras.Add(method);
        return method;
    }

    // ------------------------------------------------------------------ requests

    /// <summary>
    /// Runs one request on the shared client (at most <see cref="MaxParallel"/> at a time). A broken connection is
    /// reopened once and the request repeated; errors come out as <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/>, an abort as <see cref="OperationCanceledException"/>.
    /// </summary>
    private T Run<T>(string display, Func<Live, T> request)
    {
        _slots.Wait();
        try
        {
            var live = Current();
            try
            {
                return request(live);
            }
            catch (Exception ex) when (IsLost(live, ex))
            {
                if (IsAborted(live.Generation)) throw new OperationCanceledException("Abgebrochen.", ex);
                live = Reconnect(live.Generation);
                try
                {
                    return request(live);
                }
                catch (Exception ex2) when (IsLost(live, ex2))
                {
                    // Lost again right away: this folder is "nicht lesbar" (or the whole thing was cancelled).
                    if (IsAborted(live.Generation)) throw new OperationCanceledException("Abgebrochen.", ex2);
                    throw new IOException($"Die Verbindung zu {_where} ist unterbrochen.", ex2);
                }
            }
        }
        catch (Exception ex) when (Map(ex, display, out var mapped))
        {
            throw mapped;
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// Waits for an async SSH.NET request with the operation timeout; an <see cref="Abort"/> ends it at once (the
    /// synchronous SSH.NET calls would keep waiting for their timeout after the client is closed).
    /// </summary>
    private TResult Await<TResult>(Live live, Func<CancellationToken, Task<TResult>> request)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(live.Stop.Token);
        cts.CancelAfter(_o.OperationTimeout);
        try
        {
            // Blocking is fine on the scanner's threads; on a thread with a synchronization context (the UI listing the
            // "Dateien" row) the request runs on the thread pool, so no continuation can wait for the blocked thread.
            var token = cts.Token;
            var task = SynchronizationContext.Current == null ? request(token) : Task.Run(() => request(token));
            return task.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (cts.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            // Whatever SSH.NET throws once the token fired (usually OperationCanceledException, sometimes a
            // NullReferenceException from inside ListDirectoryAsync): an abort ends the request as cancelled, our
            // timeout is a connection problem (reconnect once), never a cancel of the scan.
            if (live.Stop.IsCancellationRequested) throw new OperationCanceledException("Abgebrochen.", ex);
            throw new SshOperationTimeoutException($"Zeitüberschreitung bei {_where}.", ex);
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            // SSH.NET clears its session when the connection goes down; requests still running on it then fail like
            // this (seen in ListDirectoryAsync while the server was paused). Only SSH.NET code runs in here.
            throw new SshConnectionException($"Die SFTP-Sitzung mit {_where} wurde beendet.", ex);
        }
    }

    public List<RemoteEntry> List(string remotePath, string display) => Run(display, live => Await(live, async ct =>
    {
        var list = new List<RemoteEntry>();
        await foreach (var f in live.Client.ListDirectoryAsync(remotePath, ct).ConfigureAwait(false))
        {
            var name = f.Name;
            if (name is "." or "..") continue;
            var kind = f.IsSymbolicLink ? RemoteKind.Link : f.IsDirectory ? RemoteKind.Directory : RemoteKind.File;
            list.Add(new RemoteEntry(name, kind, kind == RemoteKind.File ? f.Length : 0,
                Ticks(f.LastWriteTimeUtc), Ticks(f.LastAccessTimeUtc)));
        }
        return list;
    }));
    private static long Ticks(DateTime utc) => utc.Ticks <= DateTime.UnixEpoch.Ticks ? 0 : utc.Ticks;

    /// <summary>Is there a folder (following links, like the disk does)? Not found / no access: false.</summary>
    public bool IsFolder(string remotePath, string display) => Run(display, live =>
    {
        try
        {
            return Await(live, ct => live.Client.GetAttributesAsync(remotePath, ct)).IsDirectory;
        }
        catch (Exception ex) when (ex is SftpPathNotFoundException or SftpPermissionDeniedException)
        {
            return false;
        }
    });

    /// <summary>statvfs@openssh.com; null if the server does not offer it.</summary>
    public RemoteCapacity? Capacity(string remotePath, string display) => Run(display, live =>
    {
        try
        {
            var s = Await(live, ct => live.Client.GetStatusAsync(remotePath, ct));
            ulong unit = s.BlockSize != 0 ? s.BlockSize : s.FileSystemBlockSize;   // f_frsize, else f_bsize
            if (unit == 0 || s.TotalBlocks == 0) return (RemoteCapacity?)null;
            return new RemoteCapacity(checked((long)(s.TotalBlocks * unit)), checked((long)(s.AvailableBlocks * unit)),
                (s.Sid, s.TotalBlocks, unit));
        }
        catch (Exception ex) when (ex is NotSupportedException or SftpPathNotFoundException or SftpPermissionDeniedException
                                       or OverflowException
                                       || (ex is SshException && ex is not SshConnectionException and not SshOperationTimeoutException))
        {
            return null;
        }
    });

    public Stream OpenRead(string remotePath, string display) =>
        new MappedStream(Run(display, live => (Stream)live.Client.OpenRead(remotePath)), display);

    // ------------------------------------------------------------------ errors

    /// <summary>A lost connection, or the client was taken out of service under the request (<see cref="Live.Dropped"/>).</summary>
    private static bool IsLost(Live live, Exception ex) =>
        IsConnectionLoss(ex) || (live.Dropped && ex is not (OperationCanceledException or OutOfMemoryException));
    /// <summary>Did the connection itself fail (as opposed to the request)?</summary>
    internal static bool IsConnectionLoss(Exception ex) => ex is SshConnectionException or SshOperationTimeoutException
        or ObjectDisposedException or SocketException or ProxyException
        || (ex is IOException && ex.GetType() == typeof(IOException)) // raw socket errors from the stream
        || (ex is InvalidOperationException && ex.InnerException is SocketException);

    /// <summary>
    /// Turns SSH.NET errors into what the scan understands: <see cref="UnauthorizedAccessException"/> (no access, login)
    /// and <see cref="IOException"/> (everything else that has to do with the server). Ours and unknown errors (bugs) are
    /// left alone (false). The messages name the server and path, never a secret.
    /// </summary>
    internal static bool Map(Exception ex, string where, out Exception mapped)
    {
        mapped = ex switch
        {
            OperationCanceledException or SftpHostKeyException or SftpLoginException => null!,
            SftpPermissionDeniedException => new UnauthorizedAccessException($"Kein Zugriff: {where}", ex),
            SftpPathNotFoundException => new DirectoryNotFoundException($"Nicht gefunden: {where}", ex),
            SshAuthenticationException => new SftpLoginException($"Anmeldung an {where} fehlgeschlagen (Benutzer, Passwort oder Schlüssel falsch).", ex),
            SshPassPhraseNullOrEmptyException => new SftpLoginException("Der private Schlüssel ist verschlüsselt: bitte die Passphrase angeben.", ex),
            SshOperationTimeoutException => new IOException($"Zeitüberschreitung bei {where}.", ex),
            SshConnectionException or SocketException or ProxyException =>
                new IOException($"Verbindung zu {where} fehlgeschlagen oder unterbrochen: {ex.Message}", ex),
            ObjectDisposedException => new IOException($"Die Verbindung zu {where} wurde geschlossen.", ex),
            SftpException => new IOException($"SFTP-Fehler bei {where}: {ex.Message}", ex),
            SshException => new IOException($"SSH-Fehler bei {where}: {ex.Message}", ex),
            _ => null!,
        };
        return mapped != null;
    }

    /// <summary>A read-only file stream whose errors come out as <see cref="IOException"/> like the rest.</summary>
    private sealed class MappedStream(Stream inner, string where) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => Guard(() => inner.Length);
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Guard(() => inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer)
        {
            try { return inner.Read(buffer); }
            catch (Exception ex) when (Map(ex, where, out var mapped)) { throw mapped; }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try { return await inner.ReadAsync(buffer, ct).ConfigureAwait(false); }
            catch (Exception ex) when (Map(ex, where, out var mapped)) { throw mapped; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => Guard(() => inner.Seek(offset, origin));
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private T Guard<T>(Func<T> f)
        {
            try { return f(); }
            catch (Exception ex) when (Map(ex, where, out var mapped)) { throw mapped; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { inner.Dispose(); } catch (Exception ex) when (Map(ex, where, out _)) { /* closing a remote handle on a dead connection */ }
            }
            base.Dispose(disposing);
        }
    }
}
