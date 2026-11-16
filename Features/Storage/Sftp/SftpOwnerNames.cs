namespace DiskTree.Features.Storage.Sftp;

/// <summary>Where the names of uids / gids of an SFTP server came from.</summary>
public enum OwnerNameSource
{
    /// <summary>Not looked up yet.</summary>
    None,
    /// <summary><c>getent passwd</c> / <c>getent group</c> over SSH exec (also LDAP / NIS users).</summary>
    Getent,
    /// <summary>/etc/passwd and /etc/group read over SFTP (exec not allowed, e.g. an SFTP-only account).</summary>
    EtcFiles,
    /// <summary>Neither worked (chroot without /etc): ids are shown as "uid 1000".</summary>
    Unavailable,
}

/// <summary>
/// The user and group names of one SFTP server (cached per connection): looked up once, then ids that were missing
/// can be asked for one by one (getent only). Thread-safe.
/// </summary>
public sealed class SftpOwnerNames
{
    private readonly object _lock = new();
    private readonly Dictionary<int, string> _users = new();
    private readonly Dictionary<int, string> _groups = new();
    private readonly HashSet<int> _askedUsers = new();
    private readonly HashSet<int> _askedGroups = new();

    public OwnerNameSource Source { get; private set; }

    /// <summary>Sets the result of the first lookup.</summary>
    public void Load(OwnerNameSource source, string? passwd, string? group)
    {
        lock (_lock)
        {
            Source = source;
            if (passwd != null) foreach (var (id, name) in Parse(passwd)) _users.TryAdd(id, name);
            if (group != null) foreach (var (id, name) in Parse(group)) _groups.TryAdd(id, name);
        }
    }

    /// <summary>Adds answers of a later lookup of single ids (<c>getent passwd 1004 1005</c>).</summary>
    public void Add(string? passwd, string? group)
    {
        lock (_lock)
        {
            if (passwd != null) foreach (var (id, name) in Parse(passwd)) _users.TryAdd(id, name);
            if (group != null) foreach (var (id, name) in Parse(group)) _groups.TryAdd(id, name);
        }
    }

    /// <summary>The ids of <paramref name="uids"/> / <paramref name="gids"/> without a name that were not asked for yet (and marks them asked).</summary>
    public (int[] Users, int[] Groups) TakeMissing(IEnumerable<int> uids, IEnumerable<int> gids)
    {
        lock (_lock)
        {
            var u = uids.Distinct().Where(id => !_users.ContainsKey(id) && _askedUsers.Add(id)).ToArray();
            var g = gids.Distinct().Where(id => !_groups.ContainsKey(id) && _askedGroups.Add(id)).ToArray();
            return (u, g);
        }
    }

    public string? UserName(int uid) { lock (_lock) return _users.GetValueOrDefault(uid); }
    public string? GroupName(int gid) { lock (_lock) return _groups.GetValueOrDefault(gid); }

    /// <summary>"pascal", or "uid 1000" if the name is unknown.</summary>
    public string User(int uid) => UserName(uid) ?? "uid " + uid;

    /// <summary>"users", or "gid 100" if the name is unknown.</summary>
    public string Group(int gid) => GroupName(gid) ?? "gid " + gid;

    /// <summary>"pascal (uid 1000)" / "uid 1000": the owner with its number, for the owner line.</summary>
    public string UserWithId(int uid) => UserName(uid) is { } n ? $"{n} (uid {uid})" : "uid " + uid;
    public string GroupWithId(int gid) => GroupName(gid) is { } n ? $"{n} (gid {gid})" : "gid " + gid;

    /// <summary>
    /// The entries of a passwd or group file / <c>getent</c> output: "name:x:id:…" per line. Comments, empty and
    /// malformed lines (also the message of an SFTP-only server) are skipped; the first name of an id wins.
    /// </summary>
    public static List<(int Id, string Name)> Parse(string text)
    {
        var list = new List<(int, string)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(':');
            if (parts.Length < 3 || parts[0].Length == 0 || parts[0].Contains(' ')) continue;
            if (!int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int id)) continue;
            list.Add((id, parts[0]));
        }
        return list;
    }

    /// <summary>Does the text look like passwd / group lines (at least one entry)?</summary>
    public static bool LooksValid(string? text) => text != null && Parse(text).Count > 0;
}
