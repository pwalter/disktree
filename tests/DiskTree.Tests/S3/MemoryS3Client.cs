using System.IO;
using DiskTree.Features.Storage.S3;

namespace DiskTree.Tests.S3;

/// <summary>
/// An S3 server in memory with the semantics of ListObjectsV2 (sorted keys, prefix, delimiter → common prefixes,
/// max-keys, continuation token = the next key, so deleting while listing works as on S3). Counts requests; can fail or
/// block for chosen prefixes.
/// <para>
/// Writing like S3: CopyObject (≤ 5 GB, copies metadata and tags, but writes STANDARD and no encryption unless the
/// request says otherwise; archived objects without restore and SSE-C objects are refused), DeleteObjects (quiet, per key
/// errors, missing keys count as deleted; in a versioned bucket the old version is kept and a delete marker written),
/// GetBucketVersioning, PutObject of folder markers.
/// </para>
/// </summary>
public sealed class MemoryS3Client : IS3Client
{
    /// <summary>What an object carries besides key, size and time.</summary>
    public sealed class Extra
    {
        public string? StorageClass;
        public string? Encryption;
        public string? KmsKeyId;
        public bool BucketKey;
        public bool CustomerKey;
        public bool Restored;
        public string? ArchiveStatus;
        public string? ContentType;
        public Dictionary<string, string> Metadata = new(StringComparer.Ordinal);
        public Dictionary<string, string> Tags = new(StringComparer.Ordinal);

        public Extra Clone() => new()
        {
            StorageClass = StorageClass, Encryption = Encryption, KmsKeyId = KmsKeyId, BucketKey = BucketKey, CustomerKey = CustomerKey,
            Restored = Restored, ArchiveStatus = ArchiveStatus, ContentType = ContentType,
            Metadata = new(Metadata, StringComparer.Ordinal), Tags = new(Tags, StringComparer.Ordinal),
        };
    }

    private readonly Dictionary<string, SortedDictionary<string, S3ObjectInfo>> _buckets = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), Extra> _extra = new();
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Prefixes (bucket/prefix) whose listing throws this exception.</summary>
    public Dictionary<string, Exception> Failures { get; } = new(StringComparer.Ordinal);

    /// <summary>A listing of this prefix ("bucket/prefix") hangs until its token is cancelled; <see cref="Blocked"/> fires when it starts.</summary>
    public string? BlockOn { get; set; }
    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The prefixes and delimiters asked for, in order.</summary>
    public List<(string Prefix, string? Delimiter)> Calls { get; } = new();

    /// <summary>Write operations in order: "copy a -> b", "delete a,b", "put a/".</summary>
    public List<string> Ops { get; } = new();

    /// <summary>Keys DeleteObjects refuses (AccessDenied).</summary>
    public HashSet<string> DenyDelete { get; } = new(StringComparer.Ordinal);

    /// <summary>Keys CopyObject refuses as source (AccessDenied).</summary>
    public HashSet<string> DenyCopy { get; } = new(StringComparer.Ordinal);

    /// <summary>Source keys whose copy comes out one byte short (a broken copy).</summary>
    public HashSet<string> BreakCopy { get; } = new(StringComparer.Ordinal);

    /// <summary>Source keys whose copy ignores the requested storage class (a server that writes STANDARD).</summary>
    public HashSet<string> DropClass { get; } = new(StringComparer.Ordinal);

    /// <summary>Called after each successful copy (tests cancel in the middle with it).</summary>
    public Action<string>? AfterCopy { get; set; }

    /// <summary>Versioning per bucket (default off); GetBucketVersioning of a bucket in <see cref="DenyVersioning"/> fails.</summary>
    public Dictionary<string, S3Versioning> Versioning { get; } = new(StringComparer.Ordinal);
    public HashSet<string> DenyVersioning { get; } = new(StringComparer.Ordinal);

    /// <summary>Old versions kept by a versioned bucket ("bucket/key" → count) and delete markers written.</summary>
    public Dictionary<string, int> OldVersions { get; } = new(StringComparer.Ordinal);
    public int DeleteMarkers { get; private set; }

    /// <summary>The time copies get (Last-Modified).</summary>
    public DateTime Now { get; set; } = DateTime.UtcNow;

    private int _copies, _deletes;
    public int Copies => Volatile.Read(ref _copies);
    public int DeleteRequests => Volatile.Read(ref _deletes);

    public MemoryS3Client Bucket(string name)
    {
        if (!_buckets.ContainsKey(name)) _buckets[name] = new SortedDictionary<string, S3ObjectInfo>(StringComparer.Ordinal);
        return this;
    }

    public MemoryS3Client Put(string bucket, string key, long size, DateTime? modified = null)
    {
        Bucket(bucket);
        _buckets[bucket][key] = new S3ObjectInfo(key, size, (modified ?? new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks, "STANDARD");
        _extra.Remove((bucket, key));
        return this;
    }

    /// <summary>Changes what an existing object carries (storage class, encryption, tags …).</summary>
    public MemoryS3Client With(string bucket, string key, Action<Extra> change)
    {
        var o = _buckets[bucket][key];
        var x = ExtraOf(bucket, key);
        change(x);
        _extra[(bucket, key)] = x;
        _buckets[bucket][key] = o with { StorageClass = x.StorageClass ?? "STANDARD" };
        return this;
    }

    /// <summary>What an object carries (a copy; a fresh one for objects without extras).</summary>
    public Extra ExtraOf(string bucket, string key) => _extra.TryGetValue((bucket, key), out var x) ? x.Clone() : new Extra();

    public bool Has(string bucket, string key) => _buckets.TryGetValue(bucket, out var b) && b.ContainsKey(key);

    public S3ObjectInfo Get(string bucket, string key) => _buckets[bucket][key];

    public IReadOnlyList<string> Keys(string bucket) => _buckets[bucket].Keys.ToList();

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
        etag ??= Md5(data);
        _buckets[bucket][key] = new S3ObjectInfo(key, data.Length, new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, "STANDARD", etag);
        _content[(bucket, key)] = (data, encryption);
        return this;
    }

    private static string Md5(byte[] data) =>
        "\"" + Convert.ToHexString(System.Security.Cryptography.MD5.HashData(data)).ToLowerInvariant() + "\"";

    /// <summary>Ranged GETs hang until cancelled; <see cref="GetStarted"/> fires on the first.</summary>
    public bool BlockGets { get; set; }
    public TaskCompletionSource GetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<S3ObjectHead> HeadAsync(string bucket, string key, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Increment(ref _heads);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (!_buckets.TryGetValue(bucket, out var objects) || !objects.TryGetValue(key, out var o))
            throw new FileNotFoundException("S3-Fehler (404): Nicht gefunden.");
        _extra.TryGetValue((bucket, key), out var x);
        if (x?.CustomerKey == true) throw new S3CustomerKeyException(S3Errors.CustomerKeyText);
        var encryption = x?.Encryption ?? (_content.TryGetValue((bucket, key), out var c) ? c.Encryption : null);
        return new S3ObjectHead(o.Size, o.ETag, encryption)
        {
            ModifiedTicks = o.ModifiedTicks,
            StorageClass = x?.StorageClass is { } sc && sc != "STANDARD" ? sc : null,
            KmsKeyId = x?.KmsKeyId,
            BucketKey = x?.BucketKey ?? false,
            Restored = x?.Restored ?? false,
            ArchiveStatus = x?.ArchiveStatus,
            ContentType = x?.ContentType,
            Metadata = x?.Metadata is { Count: > 0 } m ? new Dictionary<string, string>(m, StringComparer.Ordinal) : null,
            TagCount = x?.Tags.Count ?? 0,
        };
    }

    /// <summary>Copies lose their tags (a server that ignores TaggingDirective COPY), or their metadata.</summary>
    public bool DropTags { get; set; }
    public bool DropMetadata { get; set; }

    /// <summary>PutObjectTagging fails (no permission).</summary>
    public bool DenyPutTags { get; set; }

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> GetTagsAsync(string bucket, string key, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        await Task.Yield();
        if (!Has(bucket, key)) throw new FileNotFoundException("Das Objekt gibt es nicht (mehr).");
        return ExtraOf(bucket, key).Tags.ToList();
    }

    public async Task PutTagsAsync(string bucket, string key, IReadOnlyList<KeyValuePair<string, string>> tags, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        await Task.Yield();
        if (DenyPutTags) throw new UnauthorizedAccessException($"Zugriff verweigert auf „{bucket}“ (AccessDenied).");
        if (!Has(bucket, key)) throw new FileNotFoundException("Das Objekt gibt es nicht (mehr).");
        var x = ExtraOf(bucket, key);
        x.Tags = tags.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        _extra[(bucket, key)] = x;
        lock (Ops) Ops.Add("tags " + key);
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

    public async Task CopyAsync(S3CopyRequest r, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Increment(ref _copies);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (!_buckets.TryGetValue(r.Bucket, out var objects)) throw new DirectoryNotFoundException($"Den Bucket „{r.Bucket}“ gibt es nicht.");
        if (!objects.TryGetValue(r.SourceKey, out var o)) throw new FileNotFoundException("Das Objekt gibt es nicht (mehr).");
        if (DenyCopy.Contains(r.SourceKey)) throw new UnauthorizedAccessException($"Zugriff verweigert auf „{r.Bucket}“ (AccessDenied).");
        var x = ExtraOf(r.Bucket, r.SourceKey);
        if (x.CustomerKey) throw new IOException("S3-Fehler InvalidRequest (400): The object was stored using a form of SSE that requires the key.");
        if (o.Size > 5L << 30) throw new IOException("S3-Fehler InvalidRequest (400): The specified copy source is larger than the maximum allowable size.");
        if ((x.StorageClass is "GLACIER" or "DEEP_ARCHIVE" && !x.Restored) || x.ArchiveStatus != null)
            throw new IOException("Das Objekt ist archiviert … (InvalidObjectState).");

        // Like S3: metadata and tags come along (COPY directives), storage class and encryption only if asked for.
        var copy = x.Clone();
        copy.StorageClass = DropClass.Contains(r.SourceKey) ? null : r.StorageClass;
        copy.Encryption = r.Encryption;
        copy.KmsKeyId = r.Encryption is "aws:kms" or "aws:kms:dsse" ? r.KmsKeyId ?? "aws/s3" : null;
        copy.BucketKey = r.BucketKey;
        copy.Restored = false;
        if (DropTags) copy.Tags.Clear();
        if (DropMetadata) copy.Metadata.Clear();
        long size = BreakCopy.Contains(r.SourceKey) ? Math.Max(0, o.Size - 1) : o.Size;
        string? etag = r.Encryption is "aws:kms" or "aws:kms:dsse" ? "\"" + Guid.NewGuid().ToString("N") + "\"" : o.ETag;
        if (_content.TryGetValue((r.Bucket, r.SourceKey), out var c))
        {
            var data = BreakCopy.Contains(r.SourceKey) ? c.Data[..(int)size] : c.Data;
            _content[(r.Bucket, r.TargetKey)] = (data, r.Encryption);
            if (BreakCopy.Contains(r.SourceKey)) etag = Md5(data);
        }
        else _content.Remove((r.Bucket, r.TargetKey));
        KeepOldVersion(r.Bucket, r.TargetKey);
        objects[r.TargetKey] = new S3ObjectInfo(r.TargetKey, size, Now.Ticks, copy.StorageClass ?? "STANDARD", etag);
        _extra[(r.Bucket, r.TargetKey)] = copy;
        lock (Ops) Ops.Add($"copy {r.SourceKey} -> {r.TargetKey}");
        AfterCopy?.Invoke(r.SourceKey);
    }

    private void KeepOldVersion(string bucket, string key)
    {
        if (Versioning.GetValueOrDefault(bucket) == S3Versioning.Off || !Has(bucket, key)) return;
        OldVersions[bucket + "/" + key] = OldVersions.GetValueOrDefault(bucket + "/" + key) + 1;
    }

    public async Task<IReadOnlyList<S3DeleteError>> DeleteManyAsync(string bucket, IReadOnlyList<string> keys, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Increment(ref _deletes);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (keys.Count > 1000) throw new IOException("S3-Fehler MalformedXML (400): more than 1000 keys.");
        if (!_buckets.TryGetValue(bucket, out var objects)) throw new DirectoryNotFoundException($"Den Bucket „{bucket}“ gibt es nicht.");
        var errors = new List<S3DeleteError>();
        foreach (var k in keys)
        {
            if (DenyDelete.Contains(k)) { errors.Add(new S3DeleteError(k, "AccessDenied", "Access Denied")); continue; }
            if (Versioning.GetValueOrDefault(bucket) != S3Versioning.Off)
            {
                KeepOldVersion(bucket, k);
                DeleteMarkers++;
            }
            objects.Remove(k);
            _extra.Remove((bucket, k));
            _content.Remove((bucket, k));
        }
        lock (Ops) Ops.Add("delete " + string.Join(",", keys));
        return errors;
    }

    public async Task<S3Versioning> GetVersioningAsync(string bucket, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        await Task.Yield();
        if (DenyVersioning.Contains(bucket)) throw new UnauthorizedAccessException($"Zugriff verweigert auf „{bucket}“ (AccessDenied).");
        if (!_buckets.ContainsKey(bucket)) throw new DirectoryNotFoundException($"Den Bucket „{bucket}“ gibt es nicht.");
        return Versioning.GetValueOrDefault(bucket);
    }

    public async Task PutEmptyAsync(string bucket, string key, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (!_buckets.TryGetValue(bucket, out var objects)) throw new DirectoryNotFoundException($"Den Bucket „{bucket}“ gibt es nicht.");
        objects[key] = new S3ObjectInfo(key, 0, Now.Ticks, "STANDARD", "\"d41d8cd98f00b204e9800998ecf8427e\"");
        lock (Ops) Ops.Add("put " + key);
    }

    // ------------------------------------------------------------------ owners and bucket rights

    /// <summary>Owner of an object as ListWithOwners reports it (none set: no owner sent).</summary>
    public Dictionary<(string Bucket, string Key), S3Owner> Owners { get; } = new();

    /// <summary>GetBucketAcl per bucket (none set: owner "owner" with FULL_CONTROL).</summary>
    public Dictionary<string, S3Acl> Acls { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Ownership { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, S3PublicAccessBlock> Blocks { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, bool> PolicyPublic { get; } = new(StringComparer.Ordinal);

    /// <summary>Bucket calls ("acl", "ownership", "block", "policy") that are refused (AccessDenied).</summary>
    public HashSet<string> DenyBucketCalls { get; } = new(StringComparer.Ordinal);

    public static readonly S3Owner DefaultOwner = new(new string('a', 64), "owner");

    public MemoryS3Client Own(string bucket, string key, S3Owner owner)
    {
        Owners[(bucket, key)] = owner;
        return this;
    }

    public async Task<S3ListPage> ListWithOwnersAsync(string bucket, string prefix, string? continuationToken, int maxKeys, CancellationToken ct)
    {
        var page = await ListAsync(bucket, prefix, null, continuationToken, maxKeys, ct);
        return page with { Objects = page.Objects.Select(o => o with { Owner = Owners.GetValueOrDefault((bucket, o.Key)) }).ToList() };
    }

    private async Task BucketCall(string bucket, string call)
    {
        Interlocked.Increment(ref _requests);
        await Task.Yield();
        if (DenyBucketCalls.Contains(call)) throw new UnauthorizedAccessException($"Zugriff verweigert auf „{bucket}“ (AccessDenied).");
        if (!_buckets.ContainsKey(bucket)) throw new DirectoryNotFoundException($"Den Bucket „{bucket}“ gibt es nicht.");
    }

    public async Task<S3Acl> GetBucketAclAsync(string bucket, CancellationToken ct)
    {
        await BucketCall(bucket, "acl");
        return Acls.TryGetValue(bucket, out var a)
            ? a
            : new S3Acl(DefaultOwner, [new S3Grant("CanonicalUser", DefaultOwner.Id, DefaultOwner.DisplayName, "FULL_CONTROL")]);
    }

    public async Task<string?> GetObjectOwnershipAsync(string bucket, CancellationToken ct)
    {
        await BucketCall(bucket, "ownership");
        return Ownership.GetValueOrDefault(bucket);
    }

    public async Task<S3PublicAccessBlock?> GetPublicAccessBlockAsync(string bucket, CancellationToken ct)
    {
        await BucketCall(bucket, "block");
        return Blocks.GetValueOrDefault(bucket);
    }

    public async Task<bool?> GetPolicyIsPublicAsync(string bucket, CancellationToken ct)
    {
        await BucketCall(bucket, "policy");
        return PolicyPublic.TryGetValue(bucket, out var p) ? p : null;
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
        ct.ThrowIfCancellationRequested();
        if (!_buckets.TryGetValue(bucket, out var objects))
            throw new DirectoryNotFoundException($"Den Bucket „{bucket}“ gibt es nicht.");

        List<string> keys;
        lock (objects) keys = objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        // The token is the key to go on from (S3 does the same in effect: deleting listed keys does not shift the next page).
        int i = continuationToken == null ? 0 : keys.FindIndex(k => string.CompareOrdinal(k, continuationToken) >= 0);
        if (i < 0) i = keys.Count;
        var list = new List<S3ObjectInfo>();
        var prefixes = new List<string>();
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
        return new S3ListPage(list, prefixes, i < keys.Count ? keys[i] : null);
    }
}
