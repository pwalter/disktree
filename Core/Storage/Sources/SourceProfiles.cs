namespace DiskTree.Core.Storage;

/// <summary>
/// The saved sources of the running app (a copy of AppSettings.Sources, set at start and after the dialog), so that
/// providers can find the profile for a path without knowing the settings. <see cref="Version"/> changes with every
/// <see cref="Set"/>: providers that cache clients compare it.
/// </summary>
public static class SourceProfiles
{
    private static SourceProfile[] _all = [];
    private static int _version;

    public static IReadOnlyList<SourceProfile> All => Volatile.Read(ref _all);

    public static int Version => Volatile.Read(ref _version);

    /// <summary>Raised after <see cref="Set"/> (on the caller's thread).</summary>
    public static event Action? Changed;

    /// <summary>
    /// The profile the user picked last in the path box list: if several profiles fit a path (two endpoints with the
    /// same bucket name), this one wins.
    /// </summary>
    public static string? PreferredId { get; set; }

    public static void Set(IEnumerable<SourceProfile> profiles)
    {
        Volatile.Write(ref _all, profiles.Select(p => p.Clone()).ToArray());
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }

    public static SourceProfile? Find(string? id) => id == null ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// The saved profile of kind <paramref name="kind"/> that a scan of <paramref name="path"/> uses: among the profiles
    /// whose scope (<paramref name="scopeOf"/>: e.g. "s3://bucket/prefix", "sftp://host"; empty = any path) contains
    /// the path, the one picked last (<see cref="PreferredId"/>), else the one with the longest scope. Null if none fits.
    /// </summary>
    public static SourceProfile? Match(string kind, string path, PathOps paths, Func<SourceProfile, string?> scopeOf)
    {
        SourceProfile? best = null;
        long bestScore = -1;
        foreach (var p in All)
        {
            if (p.Kind != kind) continue;
            var scope = scopeOf(p);
            long score;
            if (string.IsNullOrEmpty(scope)) score = 0;
            else if (paths.IsSameOrUnder(scope, path)) score = scope.Length;
            else continue;
            if (p.Id == PreferredId) score += 1L << 32;
            if (score > bestScore)
            {
                best = p;
                bestScore = score;
            }
        }
        return best;
    }
}
