namespace DiskTree.Core.Storage;

/// <summary>
/// The known storage providers, by <see cref="IStorageProvider.Id"/> (snapshot files store it) and by the paths they
/// understand. Only the disk is built in so far; others register themselves.
/// </summary>
public static class StorageProviders
{
    private static readonly object Lock = new();
    private static IStorageProvider[] _all = { DiskProvider.Instance };

    public static IStorageProvider Disk => DiskProvider.Instance;

    public static IReadOnlyList<IStorageProvider> All => Volatile.Read(ref _all);

    /// <summary>Adds (or replaces, by id) a provider. Providers registered later are asked first in <see cref="TryResolve"/>.</summary>
    public static void Register(IStorageProvider provider)
    {
        lock (Lock)
        {
            var list = _all.Where(p => p.Id != provider.Id).ToList();
            list.Insert(0, provider);
            Volatile.Write(ref _all, list.ToArray());
        }
    }

    /// <summary>Removes a registered provider again (the disk always stays).</summary>
    public static void Unregister(string id)
    {
        if (id == DiskProvider.ProviderId) return;
        lock (Lock) Volatile.Write(ref _all, _all.Where(p => p.Id != id).ToArray());
    }

    /// <summary>The provider with this id; a missing id means the disk (files written before there were providers).</summary>
    public static IStorageProvider? Get(string? id) =>
        string.IsNullOrEmpty(id) ? Disk : All.FirstOrDefault(p => p.Id == id);

    /// <summary>The first provider that understands <paramref name="text"/> (the disk is asked last).</summary>
    public static bool TryResolve(string text, out StorageLocation location)
    {
        foreach (var p in All)
            if (p.TryParse(text, out location)) return true;
        location = default;
        return false;
    }
}
