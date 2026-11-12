using System.IO;
using System.IO.Enumeration;

namespace DiskTree;

public sealed record SearchHit(string Path, string Name, string Folder, long Size, bool IsDir, Node Node)
{
    public string SizeText => NodeVM.FormatSize(Size);
    public string Icon => IsDir ? "📁" : "📄";
}

public sealed class SearchQuery
{
    public required Node Scope { get; init; }
    public string Text { get; init; } = "";
    public long MinSize { get; init; }
    /// <summary>Lower-case extensions including the dot (".mp4"); empty = any.</summary>
    public string[] Extensions { get; init; } = [];
    /// <summary>Search files on disk instead of folders in the scanned tree.</summary>
    public bool Files { get; init; }
}

public sealed record SearchResult(List<SearchHit> Hits, bool Truncated, int FoldersVisited);

/// <summary>
/// Folder search walks the in-memory node tree. File search (names / extensions) enumerates the folders of the
/// tree from disk, because the scanner does not keep single files.
/// </summary>
public static class SearchEngine
{
    public const int MaxHits = 2000;

    public static SearchResult Run(SearchQuery q, IProgress<int>? progress, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        bool truncated = false;
        int visited = 0;
        var stack = new Stack<Node>();
        stack.Push(q.Scope);

        while (stack.Count > 0 && !truncated)
        {
            ct.ThrowIfCancellationRequested();
            var node = stack.Pop();
            foreach (var c in node.Snapshot()) stack.Push(c);
            if (++visited % 25 == 0) progress?.Report(visited);

            if (q.Files)
            {
                truncated = !AddFiles(node, q, hits, ct);
            }
            else if (node != q.Scope)
            {
                if ((q.Text.Length == 0 || node.Name.Contains(q.Text, StringComparison.OrdinalIgnoreCase))
                    && node.Size >= q.MinSize)
                {
                    if (hits.Count >= MaxHits) truncated = true;
                    else hits.Add(new SearchHit(node.FullPath, node.Name, System.IO.Path.GetDirectoryName(node.FullPath.TrimEnd('\\')) ?? "", node.Size, true, node));
                }
            }
        }

        hits.Sort((a, b) => b.Size.CompareTo(a.Size));
        progress?.Report(visited);
        return new SearchResult(hits, truncated, visited);
    }

    /// <summary>Adds the matching files directly inside a folder. Returns false once the hit limit is reached.</summary>
    private static bool AddFiles(Node node, SearchQuery q, List<SearchHit> hits, CancellationToken ct)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        try
        {
            var entries = new FileSystemEnumerable<(string Name, long Length)>(node.FullPath,
                (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory ? -1L : e.Length), options);
            foreach (var (name, len) in entries)
            {
                if (len < 0 || len < q.MinSize) continue;
                if (q.Text.Length > 0 && !name.Contains(q.Text, StringComparison.OrdinalIgnoreCase)) continue;
                if (q.Extensions.Length > 0 && !q.Extensions.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant())) continue;
                if (hits.Count >= MaxHits) return false;
                hits.Add(new SearchHit(System.IO.Path.Join(node.FullPath, name), name, node.FullPath, len, false, node));
            }
        }
        catch { /* folder vanished or is not readable */ }
        return true;
    }
}
