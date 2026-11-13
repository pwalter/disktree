namespace DiskTree.Core.Storage;

/// <summary>
/// Where a scan's tree starts (see <see cref="IStorageProvider.ResolveRoot"/>).
/// <para>
/// <see cref="Target"/> is the normalized folder that is scanned, <see cref="Top"/> the top of its hierarchy (the
/// drive root "C:\"). With a capacity the tree starts at <see cref="Top"/> and the folders between it and the target
/// become placeholder nodes, so percentages can refer to the whole drive; without one (network share, bucket) the
/// tree starts at the target itself.
/// </para>
/// </summary>
public sealed record StorageRoot(string Target, string Top, long Total, long Free)
{
    public bool HasCapacity => Total > 0;
}

/// <summary>A path together with the provider that understands it.</summary>
public readonly record struct StorageLocation(IStorageProvider Provider, string Path);
