using System.IO;
using DiskTree.Core.Storage;

namespace DiskTree.Core;

/// <summary>
/// What the path box (or the command line, or a drop) asks to scan: one or more paths of ONE storage source.
/// Text without a source URI ("s3://…", "sftp://…") is the local file system and is read exactly as before
/// (<see cref="PathList.Parse"/>). With a source URI the parts are resolved through <see cref="StorageProviders"/>;
/// a scan has exactly one provider, so mixing sources (or a source and a drive) is refused with a message.
/// </summary>
public sealed record ScanInput(IStorageProvider Provider, List<string> Paths, string? Error)
{
    public bool IsDisk => Provider.Id == DiskProvider.ProviderId;

    public static ScanInput Parse(string? text)
    {
        var disk = StorageProviders.Disk;
        if (string.IsNullOrWhiteSpace(text) || !HasRemotePart(text))
            return new ScanInput(disk, PathList.Parse(text), null);

        // Keys may contain ';' themselves: a part that is neither a source URI nor a full local path belongs to the
        // part before it ("s3://b/a;b" is one key).
        var segments = new List<string>();
        bool lastRemote = false;
        foreach (var raw in text.Split(PathList.Separator))
        {
            var part = PathList.Normalize(raw);
            bool remote = IsRemote(part);
            if (!remote && lastRemote && !IsLocalPath(part))
            {
                segments[^1] += PathList.Separator + raw;
                continue;
            }
            if (part.Length == 0) continue;
            segments.Add(raw);
            lastRemote = remote;
        }

        IStorageProvider? provider = null;
        var paths = new List<string>();
        foreach (var seg in segments)
        {
            var part = PathList.Normalize(seg);
            if (!StorageProviders.TryResolve(part, out var loc))
                return Fail($"Pfad nicht erkannt:\n{part}");
            if (provider == null) provider = loc.Provider;
            else if (!ReferenceEquals(provider, loc.Provider))
                return Fail(Mixed(provider, loc.Provider));
            paths.Add(loc.Path);
        }
        if (provider == null) return new ScanInput(disk, new List<string>(), null);
        return new ScanInput(provider, Reduce(paths, provider.Paths), null);
    }

    private static ScanInput Fail(string message) => new(StorageProviders.Disk, new List<string>(), message);

    private static string Mixed(IStorageProvider a, IStorageProvider b)
    {
        static string Name(IStorageProvider p) => p.Profile is { } prof ? $"{p.DisplayName} („{prof.Name}“)" : p.DisplayName;
        return "Ein Scan kann nur Pfade einer einzigen Quelle enthalten.\n\n" +
               $"Gefunden: {Name(a)} und {Name(b)}.\n\n" +
               "Bitte die Pfade getrennt scannen (z. B. in zwei Tabs).";
    }

    /// <summary>Does the text contain a path of a registered non-disk provider?</summary>
    public static bool HasRemotePart(string text) =>
        text.Split(PathList.Separator).Any(p => IsRemote(PathList.Normalize(p)));

    private static bool IsRemote(string part) =>
        part.Length > 0 && StorageProviders.TryResolve(part, out var loc) && loc.Provider.Id != DiskProvider.ProviderId;

    private static bool IsLocalPath(string part)
    {
        try { return part.Length > 0 && Path.IsPathFullyQualified(part); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Without duplicates and without paths inside another one (by the source's own path rules).</summary>
    public static List<string> Reduce(IEnumerable<string> paths, PathOps ops)
    {
        var distinct = new List<string>();
        foreach (var p in paths)
            if (!distinct.Any(o => ops.NameEquals(ops.TrimEnd(o), ops.TrimEnd(p)))) distinct.Add(p);
        return distinct.Where(p => !distinct.Any(o => !ReferenceEquals(o, p) && ops.IsUnder(o, p))).ToList();
    }
}
