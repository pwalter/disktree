using System.IO;

namespace DiskTree.Features.Storage.Sftp;

public enum SftpHostKeyProblem
{
    /// <summary>No fingerprint was known and nobody confirmed this one: ask the user, then connect again with it.</summary>
    Unknown,
    /// <summary>The server shows a different key than the trusted one: possibly an attack, never accept silently.</summary>
    Changed,
}

/// <summary>
/// The server's host key is not trusted. An <see cref="IOException"/>, so a scan counts it like any connection problem;
/// the connection dialog looks at <see cref="Problem"/> and <see cref="Fingerprint"/>.
/// </summary>
public sealed class SftpHostKeyException : IOException
{
    public SftpHostKeyException(SftpHostKeyProblem problem, string host, string fingerprint, string? expected)
        : base(problem == SftpHostKeyProblem.Unknown
            ? $"Der Server {host} ist noch nicht bekannt (Fingerabdruck {fingerprint})."
            : $"Der Host-Schlüssel von {host} hat sich geändert (jetzt {fingerprint}, erwartet {expected}). "
              + "Die Verbindung wurde nicht aufgebaut.")
    {
        Problem = problem;
        Host = host;
        Fingerprint = fingerprint;
        Expected = expected;
    }

    public SftpHostKeyProblem Problem { get; }
    public string Host { get; }

    /// <summary>What the server presented ("SHA256:…").</summary>
    public string Fingerprint { get; }

    /// <summary>What was trusted before (null for <see cref="SftpHostKeyProblem.Unknown"/>).</summary>
    public string? Expected { get; }
}

/// <summary>
/// Login failed: wrong password, key not accepted, key file unreadable or its passphrase wrong/missing. An
/// <see cref="UnauthorizedAccessException"/>, so a scan counts it as "nicht lesbar". The message never contains the secret.
/// </summary>
public sealed class SftpLoginException(string message, Exception? inner = null) : UnauthorizedAccessException(message, inner);
