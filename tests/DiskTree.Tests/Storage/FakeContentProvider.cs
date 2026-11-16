using System.IO;
using DiskTree.Core.Storage;

namespace DiskTree.Tests.Storage;

/// <summary>
/// A <see cref="FakeProvider"/> whose file contents can be read like a remote source's (<see cref="IContentHashSource"/>):
/// byte ranges (one request each), content tags as a listing would deliver them (S3 ETags) and as a HEAD confirms them.
/// Records every read, can hold reads until they are cancelled.
/// </summary>
public class FakeContentProvider : FakeProvider, IContentHashSource
{
    private readonly Dictionary<string, string> _listed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Tag, bool Strong)> _confirmed = new(StringComparer.Ordinal);
    private readonly List<(string Path, long Offset, long Length)> _reads = new();
    private int _tagChecks;
    private long _bytes;

    public FakeContentProvider(string id = "fakecontent") : base(id)
    {
        Caps |= StorageCaps.ContentHash;
    }

    /// <summary>The tag the listing reports for a file; a HEAD confirms it unless <see cref="Confirm"/> says otherwise.</summary>
    public FakeContentProvider Tag(string path, string tag)
    {
        _listed[path] = tag;
        return this;
    }

    /// <summary>What the HEAD of a file answers (e.g. a strong-looking tag that is not an MD5 because of SSE-KMS: strong = false).</summary>
    public FakeContentProvider Confirm(string path, string tag, bool strong)
    {
        _confirmed[path] = (tag, strong);
        return this;
    }

    public string? ListedTag(string path) => _listed.GetValueOrDefault(path);

    /// <summary>Range reads so far (path, offset, length).</summary>
    public IReadOnlyList<(string Path, long Offset, long Length)> Reads
    {
        get { lock (_reads) return _reads.ToList(); }
    }

    public int TagChecks => Volatile.Read(ref _tagChecks);
    public long BytesServed => Interlocked.Read(ref _bytes);

    /// <summary>Reads hang until their token is cancelled; <see cref="ReadStarted"/> fires on the first one.</summary>
    public bool BlockReads { get; set; }
    public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ------------------------------------------------------------------ IContentHashSource

    public ContentTagKind ClassifyTag(string? listedTag)
    {
        if (string.IsNullOrEmpty(listedTag)) return ContentTagKind.None;
        return listedTag.Contains('-') ? ContentTagKind.Weak : ContentTagKind.Strong;
    }

    public int TagRequests => 1;

    public bool TryGetContentTag(string path, string? listedTag, ListRun run, out string tag, out bool isStrong)
    {
        run.BeforeRequest();
        Interlocked.Increment(ref _tagChecks);
        if (ContentOf(path) == null) throw new FileNotFoundException("Nicht gefunden: " + path);
        if (_confirmed.TryGetValue(path, out var c))
        {
            (tag, isStrong) = c;
            return true;
        }
        if (_listed.TryGetValue(path, out var l))
        {
            tag = l;
            isStrong = ClassifyTag(l) == ContentTagKind.Strong;
            return true;
        }
        tag = "";
        isStrong = false;
        return false;
    }

    public Stream OpenRead(string path, long offset, long length, ListRun run)
    {
        run.BeforeRequest();
        var content = ContentOf(path) ?? throw new FileNotFoundException("Nicht gefunden: " + path);
        lock (_reads) _reads.Add((path, offset, length));
        long from = Math.Min(offset, content.Length);
        long n = Math.Min(length, content.Length - from);
        Interlocked.Add(ref _bytes, n);
        var slice = new MemoryStream(content, (int)from, (int)n, writable: false);
        return BlockReads ? new HangingStream(this) : slice;
    }

    public long RequestsFor(long length) => 1;

    private sealed class HangingStream(FakeContentProvider owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            owner.ReadStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Like <see cref="FakeContentProvider"/>, plus a deep listing (one request) that carries the content tags, like S3.</summary>
public sealed class FakeDeepProvider : FakeContentProvider, IDeepListing
{
    public FakeDeepProvider() : base("fakedeep") { }

    public int DeepListings { get; private set; }

    public IEnumerable<DeepFile> ListFiles(string folder, ListOptions options)
    {
        options.Run?.BeforeRequest();
        DeepListings++;
        var result = new List<DeepFile>();
        var quiet = new ListOptions { Stamps = StampSet.None };
        void Walk(string dir)
        {
            foreach (var (name, isDir, link, len) in Enumerate(dir, (in StorageEntry e) => (e.Name.ToString(), e.IsDirectory, e.IsReparsePoint, e.Length), quiet))
            {
                var path = Paths.Join(dir, name);
                if (link) continue;
                if (isDir) Walk(path);
                else result.Add(new DeepFile(path, len, ListedTag(path)));
            }
        }
        Walk(folder);
        return result;
    }
}
