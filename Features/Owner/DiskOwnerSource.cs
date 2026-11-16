using DiskTree.Core.Storage;

namespace DiskTree.Features.Owner;

/// <summary>
/// Owners of the local file system (Windows owner SIDs and ACLs): exactly what the "Besitzer" tab always did
/// (<see cref="OwnerInfo"/>, <see cref="OwnerAggregator"/>), behind <see cref="IOwnerSource"/>.
/// </summary>
public sealed class DiskOwnerSource : IOwnerSource
{
    public static readonly DiskOwnerSource Instance = new();

    private DiskOwnerSource() { }

    public string RightsTitle => "Rechte (ACL)";

    public bool HasGroups => false;

    public bool SamplingHelps => true;

    public async Task<OwnerDetails?> GetOwnerAsync(string path, CancellationToken ct) =>
        await OwnerInfo.GetOwnerNameAsync(path) is { } name ? new OwnerDetails(name) : null;

    public string[] GetRights(string path, CancellationToken ct) => OwnerInfo.GetAclLines(path);

    public OwnerResult Aggregate(string folder, long minSize, Action<long, long>? progress, CancellationToken ct)
    {
        // Few threads: owner lookups on a network share are latency-bound, and the file server should not be flooded.
        bool unc = folder.StartsWith(@"\\");
        return OwnerAggregator.Aggregate(
            OwnerAggregator.EnumerateFiles(folder),
            p => OwnerInfo.GetOwnerSid(p) is { } sid ? OwnerInfo.ResolveName(sid) : null,
            minSize, unc ? 4 : 8, progress, ct);
    }
}

/// <summary>Which <see cref="IOwnerSource"/> answers for a storage source: the disk's Windows owners, or the source itself.</summary>
public static class OwnerSources
{
    /// <summary>The owner source of <paramref name="provider"/>, or null if it knows no owners (S3).</summary>
    public static IOwnerSource? For(IStorageProvider provider)
    {
        if (!StorageFeatures.Has(provider, StorageFeature.Owner)) return null;
        if (provider.Id == DiskProvider.ProviderId) return DiskOwnerSource.Instance;
        return provider as IOwnerSource;
    }
}
