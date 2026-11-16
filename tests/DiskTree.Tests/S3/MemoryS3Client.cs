using System.IO;
using DiskTree.Features.Storage.S3;

namespace DiskTree.Tests.S3;

/// <summary>
/// An S3 server in memory with the semantics of ListObjectsV2 (sorted keys, prefix, delimiter → common prefixes,
/// max-keys, continuation token). Counts requests; can fail or block for chosen prefixes.
/// </summary>
public sealed class MemoryS3Client : IS3Client
{
    private readonly Dictionary<string, SortedDictionary<string, S3ObjectInfo>> _buckets = new(StringComparer.Ordinal);
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Prefixes (bucket/prefix) whose listing throws this exception.</summary>
    public Dictionary<string, Exception> Failures { get; } = new(StringComparer.Ordinal);

    /// <summary>A listing of this prefix ("bucket/prefix") hangs until its token is cancelled; <see cref="Blocked"/> fires when it starts.</summary>
    public string? BlockOn { get; set; }
    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The prefixes and delimiters asked for, in order.</summary>
    public List<(string Prefix, string? Delimiter)> Calls { get; } = new();

    public MemoryS3Client Bucket(string name)
    {
        if (!_buckets.ContainsKey(name)) _buckets[name] = new SortedDictionary<string, S3ObjectInfo>(StringComparer.Ordinal);
        return this;
    }

    public MemoryS3Client Put(string bucket, string key, long size, DateTime? modified = null)
    {
        Bucket(bucket);
        _buckets[bucket][key] = new S3ObjectInfo(key, size, (modified ?? new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks, "STANDARD");
        return this;
    }

    public string Describe() => "Speicher (Test)";

    public Task<IReadOnlyList<string>> ListBucketsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        return Task.FromResult<IReadOnlyList<string>>(_buckets.Keys.Order(StringComparer.Ordinal).ToList());
    }

    public async Task<S3ListPage> ListAsync(string bucket, string prefix, string? delimiter, string? continuationToken, int maxKeys,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        lock (Calls) Calls.Add((prefix, delimiter));
        if (Failures.TryGetValue(bucket + "/" + prefix, out var fail)) throw fail;
        if (BlockOn == bucket + "/" + prefix)
        {
            Blocked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        await Task.Yield();
        if (!_buckets.TryGetValue(bucket, out var objects))
            throw new DirectoryNotFoundException($"Den Bucket „{bucket}“ gibt es nicht.");

        var keys = objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        int start = continuationToken == null ? 0 : int.Parse(continuationToken);
        var list = new List<S3ObjectInfo>();
        var prefixes = new List<string>();
        int i = start;
        while (i < keys.Count && list.Count + prefixes.Count < maxKeys)
        {
            var key = keys[i];
            var rest = key[prefix.Length..];
            int slash = delimiter == null ? -1 : rest.IndexOf(delimiter, StringComparison.Ordinal);
            if (slash >= 0)
            {
                var cp = prefix + rest[..(slash + 1)];
                prefixes.Add(cp);
                while (i < keys.Count && keys[i].StartsWith(cp, StringComparison.Ordinal)) i++;
            }
            else
            {
                list.Add(objects[key]);
                i++;
            }
        }
        return new S3ListPage(list, prefixes, i < keys.Count ? i.ToString() : null);
    }
}
