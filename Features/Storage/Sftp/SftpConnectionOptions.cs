namespace DiskTree.Features.Storage.Sftp;

/// <summary>
/// How to reach one SFTP server. Everything but <see cref="Auth"/> may be stored (source profiles); the secret inside
/// <see cref="Auth"/> (password, key passphrase) comes from the caller and is never written, logged or put into
/// messages by the provider. <see cref="ToString"/> leaves it out.
/// </summary>
public sealed class SftpConnectionOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = SftpUri.DefaultPort;
    public required string UserName { get; init; }

    /// <summary>Password or private key.</summary>
    public required SftpAuth Auth { get; init; }

    /// <summary>
    /// The server's host key as the user trusted it before: SHA256 fingerprint like <c>ssh-keygen -l</c> prints it
    /// ("SHA256:base64", the prefix and '=' padding are optional). Null: not known yet, see <see cref="ConfirmHostKey"/>.
    /// A different key is always a hard error (<see cref="SftpHostKeyException"/>, <see cref="SftpHostKeyProblem.Changed"/>).
    /// </summary>
    public string? HostKeySha256 { get; init; }

    /// <summary>
    /// Asked when <see cref="HostKeySha256"/> is null: gets the server's fingerprint ("SHA256:…") and returns whether to
    /// trust it ("Diesem Server vertrauen?"). Called on the connecting (background) thread, outside of the SSH handshake,
    /// so it may block on a dialog. Without it an unknown key ends in <see cref="SftpHostKeyException"/>
    /// (<see cref="SftpHostKeyProblem.Unknown"/>) that carries the fingerprint. The accepted key is kept for reconnects
    /// (<see cref="SftpProvider.HostKeySha256"/>) and should be stored by the caller.
    /// </summary>
    public Func<string, bool>? ConfirmHostKey { get; init; }

    /// <summary>Connecting incl. login.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>One request (listing a folder, reading file status).</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Keep-alive messages while idle (keeps NAT/firewall mappings open); zero or negative: none.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many folders are listed at the same time over the one connection, no matter how many worker threads the
    /// scanner runs. 8 is about as fast as several connections (requests are pipelined on the SSH channel).
    /// </summary>
    public int MaxParallelListings { get; init; } = 8;

    /// <summary>"sftp://user@host[:port]/".</summary>
    public string RootUri => new SftpUri(UserName, Host.ToLowerInvariant(), Port, "/").RootUri;

    /// <summary>The same server with another login (e.g. after asking for the password again).</summary>
    public SftpConnectionOptions With(SftpAuth auth) => new()
    {
        Host = Host, Port = Port, UserName = UserName, Auth = auth, HostKeySha256 = HostKeySha256,
        ConfirmHostKey = ConfirmHostKey, ConnectTimeout = ConnectTimeout, OperationTimeout = OperationTimeout,
        KeepAliveInterval = KeepAliveInterval, MaxParallelListings = MaxParallelListings,
    };

    /// <summary>The same server, with a host key that is now trusted.</summary>
    public SftpConnectionOptions WithHostKey(string? fingerprint) => new()
    {
        Host = Host, Port = Port, UserName = UserName, Auth = Auth, HostKeySha256 = fingerprint,
        ConfirmHostKey = ConfirmHostKey, ConnectTimeout = ConnectTimeout, OperationTimeout = OperationTimeout,
        KeepAliveInterval = KeepAliveInterval, MaxParallelListings = MaxParallelListings,
    };

    /// <summary>No secrets: "user@host:port (Passwort)".</summary>
    public override string ToString() => $"{UserName}@{Host}:{Port} ({Auth})";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("Kein Server angegeben.");
        if (string.IsNullOrWhiteSpace(UserName)) throw new ArgumentException("Kein Benutzer angegeben.");
        if (Port is <= 0 or > 65535) throw new ArgumentException("Ungültiger Port: " + Port);
        if (Auth is null) throw new ArgumentException("Keine Anmeldung (Passwort oder Schlüssel) angegeben.");
        if (MaxParallelListings < 1) throw new ArgumentException("MaxParallelListings muss mindestens 1 sein.");
    }
}

/// <summary>
/// How to log in: a password or a private key (OpenSSH/PEM/PuTTY format, file or text, optional passphrase). The secret
/// stays in this object; <see cref="ToString"/> only says which kind it is.
/// </summary>
public abstract class SftpAuth
{
    private protected SftpAuth() { }

    public static SftpAuth Password(string password) => new PasswordAuth(password ?? throw new ArgumentNullException(nameof(password)));

    /// <summary>A private key file (e.g. %USERPROFILE%\.ssh\id_ed25519); <paramref name="passphrase"/> if it is encrypted.</summary>
    public static SftpAuth KeyFile(string path, string? passphrase = null) =>
        new KeyFileAuth(path ?? throw new ArgumentNullException(nameof(path)), passphrase);

    /// <summary>A private key given as text (as it would be in the file).</summary>
    public static SftpAuth KeyText(string privateKey, string? passphrase = null) =>
        new KeyTextAuth(privateKey ?? throw new ArgumentNullException(nameof(privateKey)), passphrase);

    internal sealed class PasswordAuth(string password) : SftpAuth
    {
        internal string Secret { get; } = password;
        public override string ToString() => "Passwort";
    }

    internal sealed class KeyFileAuth(string path, string? passphrase) : SftpAuth
    {
        internal string Path { get; } = path;
        internal string? Passphrase { get; } = passphrase;
        public override string ToString() => "Schlüsseldatei " + Path;
    }

    internal sealed class KeyTextAuth(string key, string? passphrase) : SftpAuth
    {
        internal string Key { get; } = key;
        internal string? Passphrase { get; } = passphrase;
        public override string ToString() => "Schlüssel";
    }
}
