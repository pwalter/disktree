using System.IO;
using DiskTree.Core.Storage;

namespace DiskTree.Tests.Storage;

/// <summary>
/// A <see cref="FakeProvider"/> whose files can be changed (<see cref="IStorageWriter"/>) and that knows POSIX owners
/// (<see cref="IOwnerSource"/>), like an SFTP server: delete for good, rename and move refuse an existing target,
/// case-sensitive names. Entries can be protected (no permission) and the writer can be made to cancel.
/// </summary>
public sealed class FakeStore : FakeProvider, IStorageWriter, IOwnerSource
{
    public FakeStore() : base("fakestore")
    {
        Caps |= StorageCaps.Owner | StorageCaps.PosixPermissions | StorageCaps.Delete | StorageCaps.Move | StorageCaps.Rename;
    }

    /// <summary>uid → user name, gid → group name; missing ids are shown as "uid 1234" / "gid 1234".</summary>
    public Dictionary<int, string> Users { get; } = new() { [1000] = "pascal" };
    public Dictionary<int, string> Groups { get; } = new() { [1000] = "users" };

    /// <summary>Paths that cannot be removed, renamed or moved (no permission).</summary>
    public HashSet<string> Protected { get; } = new(StringComparer.Ordinal);

    /// <summary>Cancels this token source after so many removed entries (tests of a cancel in the middle).</summary>
    public (int After, CancellationTokenSource Cts)? CancelAfterDeletes { get; set; }

    /// <summary>What the writer did, in order ("delete /a", "rename /a -> b", …).</summary>
    public List<string> Log { get; } = new();

    private int _deleted;

    // ------------------------------------------------------------------ building

    /// <summary>Owner, group and (optionally) permission bits of an existing file or folder.</summary>
    public FakeStore Own(string path, int uid, int gid, int? perms = null)
    {
        var (dir, name) = Split(path);
        if (dir.Files.TryGetValue(name, out var f))
            dir.Files[name] = f with { Uid = uid, Gid = gid, Mode = perms is { } p ? PosixMode.Regular | p : f.Mode };
        else if (dir.Dirs.TryGetValue(name, out var d))
        {
            d.Uid = uid;
            d.Gid = gid;
            if (perms is { } p) d.Mode = PosixMode.Directory | p;
        }
        else throw new FileNotFoundException(path);
        return this;
    }

    private (Dir Dir, string Name) Split(string path)
    {
        var parent = Paths.ParentOf(path) ?? throw new IOException("Die Wurzel hat keinen Ordner: " + path);
        var dir = Get(parent) ?? throw new DirectoryNotFoundException("Nicht gefunden: " + parent);
        return (dir, Paths.NameOf(path));
    }

    private (Dir Dir, string Name)? TrySplit(string path)
    {
        var parent = Paths.ParentOf(path);
        if (parent == null) return null;
        var dir = Get(parent);
        return dir == null ? null : (dir, Paths.NameOf(path));
    }

    // ------------------------------------------------------------------ IStorageWriter

    public StorageItemInfo? Stat(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (path == "/") return new StorageItemInfo(true, false, 0);
        if (TrySplit(path) is not { } s) return null;
        if (s.Dir.Files.TryGetValue(s.Name, out var f)) return new StorageItemInfo(false, false, f.Size, f.ModifiedTicks, f.AccessedTicks, f.CreatedTicks);
        if (s.Dir.Dirs.TryGetValue(s.Name, out var d)) return new StorageItemInfo(true, d.Link, 0);
        return null;
    }

    public DeleteOutcome Delete(string path, bool recursive, Action<DeleteOutcome>? progress, CancellationToken ct)
    {
        var outcome = new DeleteOutcome();
        var (dir, name) = Split(path);
        if (dir.Files.ContainsKey(name)) DeleteFile(dir, name, path, outcome, ct);
        else if (dir.Dirs.TryGetValue(name, out var d))
        {
            if (!recursive && (d.Dirs.Count > 0 || d.Files.Count > 0)) throw new IOException("Der Ordner ist nicht leer: " + path);
            DeleteDir(dir, name, d, path, outcome, progress, ct);
        }
        else throw new FileNotFoundException("Nicht gefunden: " + path);
        progress?.Invoke(outcome);
        return outcome;
    }

    private void DeleteFile(Dir dir, string name, string path, DeleteOutcome outcome, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Protected.Contains(path)) { outcome.Failed.Add((path, "Kein Zugriff")); return; }
        outcome.Bytes += dir.Files[name].Size;
        dir.Files.Remove(name);
        outcome.Files++;
        Log.Add("delete " + path);
        Counted();
    }

    private void DeleteDir(Dir parent, string name, Dir d, string path, DeleteOutcome outcome, Action<DeleteOutcome>? progress, CancellationToken ct)
    {
        foreach (var f in d.Files.Keys.ToList()) DeleteFile(d, f, Paths.Join(path, f), outcome, ct);
        foreach (var (sub, sd) in d.Dirs.ToList()) DeleteDir(d, sub, sd, Paths.Join(path, sub), outcome, progress, ct);
        ct.ThrowIfCancellationRequested();
        progress?.Invoke(outcome);
        if (Protected.Contains(path)) { outcome.Failed.Add((path, "Kein Zugriff")); return; }
        if (d.Files.Count > 0 || d.Dirs.Count > 0) { outcome.Failed.Add((path, "Der Ordner ist nicht leer.")); return; }
        parent.Dirs.Remove(name);
        outcome.Folders++;
        Log.Add("delete " + path);
        Counted();
    }

    private void Counted()
    {
        if (CancelAfterDeletes is { } c && ++_deleted >= c.After) c.Cts.Cancel();
    }

    public string Rename(string path, string newName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parent = Paths.ParentOf(path)!;
        var target = Paths.Join(parent, newName);
        Relocate(path, target, replace: false);
        Log.Add($"rename {path} -> {newName}");
        return target;
    }

    public string Move(string path, string targetFolder, string? newName, bool replace, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Get(targetFolder) == null) throw new DirectoryNotFoundException("Zielordner nicht gefunden: " + targetFolder);
        var target = Paths.Join(targetFolder, newName ?? Paths.NameOf(path));
        Relocate(path, target, replace);
        Log.Add($"move {path} -> {target}" + (replace ? " (replace)" : ""));
        return target;
    }

    private void Relocate(string path, string target, bool replace)
    {
        if (Protected.Contains(path)) throw new UnauthorizedAccessException("Kein Zugriff: " + path);
        var (from, name) = Split(path);
        var (to, newName) = Split(target);
        bool isFile = from.Files.TryGetValue(name, out var file);
        if (!isFile && !from.Dirs.ContainsKey(name)) throw new FileNotFoundException("Nicht gefunden: " + path);
        if (to.Dirs.ContainsKey(newName) || (to.Files.ContainsKey(newName) && !(replace && isFile)))
            throw new IOException("Das Ziel existiert schon: " + target);
        if (isFile)
        {
            from.Files.Remove(name);
            to.Files[newName] = file!;
        }
        else
        {
            var d = from.Dirs[name];
            from.Dirs.Remove(name);
            to.Dirs[newName] = d;
        }
    }

    public void CreateDirectory(string path, CancellationToken ct)
    {
        var (dir, name) = Split(path);
        if (dir.Dirs.ContainsKey(name) || dir.Files.ContainsKey(name)) throw new IOException("Existiert schon: " + path);
        Folder(path);
        Log.Add("mkdir " + path);
    }

    public IReadOnlyCollection<string> ListNames(string folder, CancellationToken ct)
    {
        var d = Get(folder) ?? throw new DirectoryNotFoundException(folder);
        return d.Dirs.Keys.Concat(d.Files.Keys).ToHashSet(StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ IOwnerSource

    public string RightsTitle => "Rechte (POSIX)";
    public bool HasGroups => true;
    public bool SamplingHelps => false;

    private string User(int uid) => Users.TryGetValue(uid, out var n) ? n : "uid " + uid;
    private string Group(int gid) => Groups.TryGetValue(gid, out var n) ? n : "gid " + gid;

    private (int Uid, int Gid, int Mode)? Ids(string path)
    {
        if (TrySplit(path) is not { } s) return path == "/" ? (0, 0, PosixMode.Directory | 0x1ED) : null;
        if (s.Dir.Files.TryGetValue(s.Name, out var f)) return (f.Uid, f.Gid, f.Mode);
        if (s.Dir.Dirs.TryGetValue(s.Name, out var d)) return (d.Uid, d.Gid, d.Mode);
        return null;
    }

    public Task<OwnerDetails?> GetOwnerAsync(string path, CancellationToken ct) =>
        Task.FromResult(Ids(path) is { } i
            ? new OwnerDetails(User(i.Uid), Group(i.Gid), $"{PosixMode.Format(i.Mode)}  {User(i.Uid)}:{Group(i.Gid)}  ({PosixMode.Octal(i.Mode)})")
            : null);

    public string[] GetRights(string path, CancellationToken ct) =>
        Ids(path) is { } i ? PosixMode.Describe(i.Mode, User(i.Uid), Group(i.Gid)) : ["Rechte nicht lesbar."];

    public OwnerResult Aggregate(string folder, long minSize, Action<long, long>? progress, CancellationToken ct)
    {
        var users = new Dictionary<string, (long Bytes, long Files)>();
        var groups = new Dictionary<string, (long Bytes, long Files)>();
        long seen = 0, failed = 0;
        var start = Get(folder);
        if (start == null) return new OwnerResult([], 0, 0, 0, false, "Nicht gefunden: " + folder, true);
        var stack = new Stack<Dir>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) break;
            var d = stack.Pop();
            if (d.Denied) { failed++; continue; }
            foreach (var f in d.Files.Values)
            {
                seen++;
                Add(users, User(f.Uid), f.Size);
                Add(groups, Group(f.Gid), f.Size);
            }
            foreach (var sub in d.Dirs.Values) if (!sub.Link) stack.Push(sub);
            progress?.Invoke(seen, failed);
        }
        return new OwnerResult(Rows(users), seen, 0, failed, ct.IsCancellationRequested, null, true, Rows(groups));
    }

    private static void Add(Dictionary<string, (long Bytes, long Files)> d, string key, long size)
    {
        var (b, n) = d.GetValueOrDefault(key);
        d[key] = (b + size, n + 1);
    }

    private static List<OwnerRow> Rows(Dictionary<string, (long Bytes, long Files)> d) =>
        d.Select(kv => new OwnerRow(kv.Key, kv.Value.Bytes, kv.Value.Files)).OrderByDescending(r => r.Bytes).ToList();
}
