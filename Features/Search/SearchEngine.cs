using System.IO;
using DiskTree.Core;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Search;

/// <summary>
/// Folder search walks the in-memory node tree. File search (names / extensions) lists the folders of the
/// tree again from their storage (<see cref="SearchQuery.Provider"/>), because the scanner does not keep single files.
/// </summary>
public static class SearchEngine
{
    public const int MaxHits = 2000;

    public static SearchResult Run(SearchQuery q, IProgress<int>? progress, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        bool truncated = false;
        int visited = 0;
        var paths = q.Provider.Paths;

        foreach (var node in q.Scope.Walk())
        {
            if (truncated) break;
            ct.ThrowIfCancellationRequested();
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
                    else hits.Add(new SearchHit(node.FullPath, node.Name, paths.ParentOf(node.FullPath) ?? "", node.Size, true, node));
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
        var paths = q.Provider.Paths;
        try
        {
            var entries = q.Provider.Enumerate(node.FullPath,
                (in StorageEntry e) => (e.Name.ToString(), e.IsDirectory ? -1L : e.Length, e.Attributes,
                    e.ModifiedTicks, e.CreatedTicks, e.AccessedTicks), ListOptions.Lenient);
            var filter = q.Filter is { IsEmpty: false } f ? f : null;
            foreach (var (name, len, attr, modified, created, accessed) in entries)
            {
                if (len < 0 || len < q.MinSize) continue;
                if (q.Text.Length > 0 && !name.Contains(q.Text, StringComparison.OrdinalIgnoreCase)) continue;
                if (q.Extensions.Length > 0 && !q.Extensions.Contains(Path.GetExtension(name).ToLowerInvariant())) continue;
                if (filter != null && !filter.Matches(attr, modified, created, accessed)) continue;
                if (hits.Count >= MaxHits) return false;
                hits.Add(new SearchHit(paths.Join(node.FullPath, name), name, node.FullPath, len, false, node)
                {
                    Modified = modified, Created = created, Accessed = accessed, Attributes = attr,
                    ShownStamp = filter?.Stamp,
                    ShowAttributes = filter is { Attributes.Length: > 0 },
                });
            }
        }
        catch { /* folder vanished or is not readable */ }
        return true;
    }
}
