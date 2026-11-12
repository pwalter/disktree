namespace DiskTree.Features.Owner;

public sealed record OwnerResult(List<OwnerRow> Rows, long Seen, long Skipped, long Failed, bool Cancelled)
{
    public long TotalBytes => Rows.Sum(r => r.Bytes);
}
