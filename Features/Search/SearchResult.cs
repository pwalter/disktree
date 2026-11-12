namespace DiskTree;

public sealed record SearchResult(List<SearchHit> Hits, bool Truncated, int FoldersVisited);
