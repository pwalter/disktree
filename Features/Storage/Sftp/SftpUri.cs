using System.Globalization;

namespace DiskTree.Features.Storage.Sftp;

/// <summary>
/// An SFTP location as DiskTree writes it: <c>sftp://user@host:port/path</c>. This is the full path of every node of an
/// SFTP scan (so snapshots, reports and the path box carry the server along).
/// <para>
/// The canonical form (<see cref="ToString"/>, <see cref="SftpProvider.Normalize"/>): scheme and host in lower case,
/// the port only when it is not 22, IPv6 hosts in brackets, the remote path absolute with '/' as separator, no empty,
/// "." or ".." parts and no '/' at the end (except for the root "/"). Names are case-sensitive.
/// The path is taken literally, without percent-decoding (a name may contain '%'), so normalizing twice gives the same
/// result. A missing path means the root "/" (not the home folder: the server's start folder is not known offline).
/// </para>
/// </summary>
public readonly record struct SftpUri(string User, string Host, int Port, string Path)
{
    public const string Scheme = "sftp";
    public const string Prefix = "sftp://";
    public const int DefaultPort = 22;

    /// <summary>"sftp://user@host[:port]" (no path, no '/' at the end).</summary>
    public string Authority => FormatAuthority(User, Host, Port);

    /// <summary>"sftp://user@host[:port]/".</summary>
    public string RootUri => Authority + "/";

    public override string ToString() => Authority + Path;

    public static string FormatAuthority(string user, string host, int port)
    {
        var h = host.Contains(':') ? "[" + host + "]" : host;
        var u = string.IsNullOrEmpty(user) ? "" : user + "@";
        return port == DefaultPort ? Prefix + u + h : Prefix + u + h + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Does the text start with "sftp://" (any case)?</summary>
    public static bool HasScheme(string? text) =>
        text != null && text.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses (and normalizes) "sftp://[user@]host[:port][/path]". Never throws.</summary>
    public static bool TryParse(string? text, out SftpUri uri)
    {
        uri = default;
        if (!HasScheme(text)) return false;
        var s = text!.Trim();
        var rest = s[Prefix.Length..];

        int slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "/" : rest[slash..];
        if (authority.Length == 0) return false;

        // The user may itself contain '@' (written as is): the last one separates it from the host.
        string user = "";
        int at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            user = authority[..at];
            authority = authority[(at + 1)..];
        }

        string host;
        int port = DefaultPort;
        if (authority.StartsWith('['))
        {
            int close = authority.IndexOf(']');
            if (close < 0) return false;
            host = authority[1..close];
            var after = authority[(close + 1)..];
            if (after.Length > 0 && (after[0] != ':' || !TryPort(after[1..], out port))) return false;
        }
        else
        {
            int colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                if (!TryPort(authority[(colon + 1)..], out port)) return false;
                host = authority[..colon];
                if (host.Contains(':')) return false; // IPv6 only in brackets
            }
            else host = authority;
        }

        if (host.Length == 0 || host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@' or '[' or ']')) return false;
        if (user.Any(c => c is '/' or '\\' || char.IsControl(c))) return false;

        uri = new SftpUri(user, host.ToLowerInvariant(), port, NormalizePath(path));
        return true;
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    /// <summary>
    /// An absolute remote path: '/' separators (a '\' is part of a name on the server), empty and "." parts dropped,
    /// ".." taken back (never above the root), no '/' at the end except for "/".
    /// </summary>
    public static string NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>
    /// Where the remote path of a full SFTP path starts (the first '/' after "sftp://"), or -1 if there is none;
    /// <paramref name="path"/>.Length for "sftp://host" without a path.
    /// </summary>
    internal static int PathStart(string path)
    {
        if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return -1;
        int i = path.IndexOf('/', Prefix.Length);
        return i < 0 ? path.Length : i;
    }
}
