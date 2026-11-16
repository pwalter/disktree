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

    private readonly Dictionary<(string, string), (byte[] Data, string? Encryption)> _content = new();
    private int _heads, _gets;
    private long _bytes;

    /// <summary>HEAD and ranged GET requests so far, bytes sent by GETs.</summary>
    public int Heads => Volatile.Read(ref _heads);
    public int Gets => Volatile.Read(ref _gets);
    public long BytesSent => Interlocked.Read(ref _bytes);

    /// <summary>
    /// An object with content. ETag: the MD5 of the content (single part), or the given one (e.g. multipart "…-2");
    /// <paramref name="encryption"/> as the HEAD reports it ("aws:kms" …).
    /// </summary>
    public MemoryS3Client PutContent(string bucket, string key, byte[] data, string? etag = null, string? encryption = null)
    {
        Bucket(bucket);
        etag ??= "\"" + Convert.ToHexString(System.Security.Cryptography.MD5.HashData(data)).ToLowerInvariant() + "\"";
        _buckets[bucket][key] = new S3ObjectInfo(key, data.Length, new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, "STANDARD", etag);
        _content[(bucket, key)] = (data, encryption);
        return this;
    }

    /// <summary>Ranged GETs hang until cancelled; <see cref="GetStarted"/> fires on the first.</summary>
    public bool BlockGets { get; set; }
    public TaskCompletionSource GetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<S3ObjectHead> HeadAsync(string bucket, string key, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Increment(ref _heads);
        await Task.Yield();
        if (!_buckets.TryGetValue(bucket, out var objects) || !objects.TryGetValue(key, out var o))
            throw new IOException("S3-Fehler (404): Nicht gefunden.");
        return new S3ObjectHead(o.Size, o.ETag, _content.TryGetValue((bucket, key), out var c) ? c.Encryption : null);
    }

    public async Task<int> ReadRangeAsync(string bucket, string key, long offset, Memory<byte> buffer, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Increment(ref _gets);
        if (BlockGets)
        {
            GetStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        await Task.Yield();
        if (!_buckets.TryGetValue(bucket, out var objects) || !objects.TryGetValue(key, out var o))
            throw new IOException("S3-Fehler NoSuchKey (404): Nicht gefunden.");
        var data = _content.TryGetValue((bucket, key), out var c) ? c.Data : new byte[o.Size];
        if (offset >= data.Length) throw new IOException("S3-Fehler InvalidRange (416).");
        int n = (int)Math.Min(buffer.Length, data.Length - offset);
        data.AsMemory((int)offset, n).CopyTo(buffer);
        Interlocked.Add(ref _bytes, n);
        return n;
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
