using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DiskTree.Core.Snapshots;

/// <summary>Who/when/what of a stored scan.</summary>
public sealed record SnapshotInfo(DateTime ScannedAt, string TargetPath, string Machine);

/// <summary>The content of a .disktree file, ready to be turned into a <see cref="Scanner"/> (see Scanner.FromSnapshot).</summary>
public sealed class LoadedSnapshot
{
    public required Node Root { get; init; }
    public required Node Target { get; init; }
    public required Scanner.BigFile[] Top { get; init; }
    public long ExpectedBytes { get; init; }
    public required SnapshotInfo Info { get; init; }
    public int Version { get; init; }
    /// <summary>Timestamp the age buckets were built from (absent in older files = last modified).</summary>
    public AgeStamp AgeStamp { get; init; }
}

/// <summary>
/// Reads and writes scans as ".disktree" files: a GZip-compressed text file with one JSON object per line.
/// Line 1 is the header (format name, version, time, scanned path), then one line per folder in depth-first
/// order (parent index "p" instead of nesting, so deep trees need no recursion), then the largest files.
/// Unknown lines ("k") and unknown fields are ignored and missing fields default to zero, so files stay readable
/// when later versions add aggregates; a higher "version" is still opened as far as it is understood.
/// </summary>
public static class SnapshotStore
{
    public const string Format = "DiskTree-Snapshot";
    public const int Version = 1;
    public const string Extension = ".disktree";

    // ------------------------------------------------------------------ save

    public static void Save(string file, Scanner scanner, DateTime scannedAt)
    {
        var root = scanner.Root ?? throw new InvalidOperationException("Es gibt keinen Scan zum Speichern.");
        var target = scanner.ScanTarget ?? root;
        var tmp = file + ".tmp";

        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                Write(gz, root, target, scanner.ExpectedBytes, scanner.TopFiles(), scannedAt);
            File.Move(tmp, file, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    internal static void Write(Stream stream, Node root, Node target, long expected,
        IEnumerable<Scanner.BigFile> top, DateTime scannedAt)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using var json = new Utf8JsonWriter(buffer);

        void EndLine()
        {
            json.Flush();
            stream.Write(buffer.WrittenSpan);
            stream.WriteByte((byte)'\n');
            buffer.Clear();
            json.Reset(buffer);
        }

        json.WriteStartObject();
        json.WriteString("format", Format);
        json.WriteNumber("version", Version);
        json.WriteString("scannedAt", scannedAt.ToString("o"));
        json.WriteString("target", target.FullPath);
        json.WriteString("machine", Environment.MachineName);
        json.WriteNumber("expected", expected);
        json.WriteString("ageStamp", AgeStamps.ToKey(AgeStamps.Current));
        json.WriteEndObject();
        EndLine();

        // Depth-first, parents before children; the line number is the node's index.
        var stack = new Stack<(Node Node, int Parent)>();
        stack.Push((root, -1));
        int index = 0;
        while (stack.Count > 0)
        {
            var (n, parent) = stack.Pop();
            int me = index++;

            json.WriteStartObject();
            json.WriteString("k", "n");
            json.WriteNumber("p", parent);
            json.WriteString("n", n.Name);
            if (parent < 0) json.WriteString("fp", n.FullPath);
            Num(json, "s", Interlocked.Read(ref n.Size));
            Num(json, "f", Interlocked.Read(ref n.FileCount));
            Num(json, "d", Interlocked.Read(ref n.DirCount));
            Num(json, "os", n.OwnSize);
            Num(json, "oc", n.OwnCount);
            if (n.OwnTypes is { Length: > 0 } types)
            {
                // By category name, not by index: the category list may change between versions.
                json.WriteStartArray("t");
                foreach (var t in types)
                {
                    json.WriteStartArray();
                    json.WriteStringValue(FileTypes.All[t.Cat].Name);
                    json.WriteNumberValue(t.Size);
                    json.WriteNumberValue(t.Count);
                    json.WriteEndArray();
                }
                json.WriteEndArray();
            }
            if (n.OwnAges is { Length: > 0 } ages)
            {
                // Age buckets of the folder's own files: sizes [0..4], counts [5..9] (see AgeBuckets.Pack).
                json.WriteStartArray("ag");
                foreach (var v in ages) json.WriteNumberValue(v);
                json.WriteEndArray();
            }
            Num(json, "x", Interlocked.Read(ref n.Excluded));
            Num(json, "dt", n.DriveTotal);
            Num(json, "df", n.DriveFree);
            if (n.PartialDrive) json.WriteBoolean("pd", true);
            json.WriteEndObject();
            EndLine();

            var kids = n.Snapshot();
            for (int i = kids.Length - 1; i >= 0; i--) stack.Push((kids[i], me));
        }

        foreach (var f in top)
        {
            json.WriteStartObject();
            json.WriteString("k", "top");
            json.WriteString("p", f.Path);
            json.WriteNumber("s", f.Size);
            json.WriteEndObject();
            EndLine();
        }
    }

    private static void Num(Utf8JsonWriter json, string name, long value)
    {
        if (value != 0) json.WriteNumber(name, value);
    }

    // ------------------------------------------------------------------ load

    public static LoadedSnapshot Load(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(fs);
    }

    internal static LoadedSnapshot Read(Stream input)
    {
        try
        {
            using var gz = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new StreamReader(gz, Encoding.UTF8);

            var headerLine = reader.ReadLine() ?? throw new InvalidDataException("Die Datei ist leer.");
            using var header = JsonDocument.Parse(headerLine);
            var h = header.RootElement;
            if (h.ValueKind != JsonValueKind.Object || Str(h, "format") != Format)
                throw new InvalidDataException("Das ist keine DiskTree-Scandatei.");
            int version = (int)Num(h, "version");

            var nodes = new List<Node>();
            var top = new List<Scanner.BigFile>();
            var categories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < FileTypes.All.Length; i++) categories[FileTypes.All[i].Name] = i;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object) continue;

                switch (Str(e, "k") ?? "n")
                {
                    case "n":
                        nodes.Add(ReadNode(e, nodes, categories));
                        break;
                    case "top":
                        if (Str(e, "p") is { Length: > 0 } path) top.Add(new Scanner.BigFile(path, Num(e, "s")));
                        break;
                    // Anything else comes from a newer version: skip.
                }
            }

            if (nodes.Count == 0) throw new InvalidDataException("Die Datei enthält keinen Ordnerbaum.");
            var root = nodes[0];

            var targetPath = Str(h, "target") ?? root.FullPath;
            var target = FindByPath(root, targetPath) ?? root;
            DateTime.TryParse(Str(h, "scannedAt"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at);

            return new LoadedSnapshot
            {
                Root = root,
                Target = target,
                Top = top.ToArray(),
                ExpectedBytes = Num(h, "expected"),
                Info = new SnapshotInfo(at, targetPath, Str(h, "machine") ?? ""),
                Version = version,
                AgeStamp = AgeStamps.FromKey(Str(h, "ageStamp")),
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Die Scandatei ist beschädigt: " + ex.Message, ex);
        }
    }

    private static Node ReadNode(JsonElement e, List<Node> nodes, Dictionary<string, int> categories)
    {
        int parentIndex = (int)Num(e, "p", -1);
        Node? parent = null;
        if (nodes.Count > 0)
        {
            if (parentIndex < 0 || parentIndex >= nodes.Count) throw new InvalidDataException("Die Scandatei ist beschädigt (Ordnerbaum).");
            parent = nodes[parentIndex];
        }

        var name = Str(e, "n") ?? "";
        var node = new Node
        {
            Name = name,
            FullPath = parent == null ? Str(e, "fp") ?? name : Path.Join(parent.FullPath, name),
            Parent = parent,
            Size = Num(e, "s"),
            FileCount = Num(e, "f"),
            DirCount = Num(e, "d"),
            OwnSize = Num(e, "os"),
            OwnCount = Num(e, "oc"),
            Excluded = Num(e, "x"),
            DriveTotal = Num(e, "dt"),
            DriveFree = Num(e, "df"),
            PartialDrive = e.TryGetProperty("pd", out var pd) && pd.ValueKind == JsonValueKind.True,
            Scanned = true,
            Open = 0,
        };

        if (e.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            var sizes = new long[FileTypes.Count];
            var counts = new long[FileTypes.Count];
            foreach (var item in t.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 3) continue;
                int cat = categories.TryGetValue(item[0].GetString() ?? "", out var c) ? c : FileTypes.Other;
                sizes[cat] += item[1].GetInt64();
                counts[cat] += item[2].GetInt64();
            }
            node.OwnTypes = FileTypes.Pack(sizes, counts);
        }

        if (e.TryGetProperty("ag", out var ag) && ag.ValueKind == JsonValueKind.Array && ag.GetArrayLength() >= AgeBuckets.Count * 2)
        {
            var own = new long[AgeBuckets.Count * 2];
            for (int i = 0; i < own.Length; i++) own[i] = ag[i].GetInt64();
            node.OwnAges = own;
            parent?.AddChild(node); // AddSub walks up through Parent, so the parent must know us first
            for (int b = 0; b < AgeBuckets.Count; b++)
                if (own[b] != 0) AgeBuckets.AddSub(node, b, own[b]);
            return node;
        }

        parent?.AddChild(node);
        return node;
    }

    private static long Num(JsonElement e, string name, long fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : fallback;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ------------------------------------------------------------------ helpers

    /// <summary>Finds a folder by its full path below <paramref name="root"/> (case-insensitive), or null.</summary>
    public static Node? FindByPath(Node root, string path)
    {
        var rootPath = root.FullPath;
        if (!path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) return null;

        var cur = root;
        foreach (var part in path[rootPath.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = cur.Snapshot().FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (cur == null) return null;
        }
        return cur;
    }
}
