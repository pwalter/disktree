using System.IO;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Storage.S3;

/// <summary>
/// Owners on S3 (<see cref="IOwnerSource"/>). S3 has no owner per folder (a folder is a key prefix): the owner of a
/// folder is the bucket's owner, its rights are those of the bucket (ACL, object ownership, Block Public Access,
/// policy status). "Speicher pro Besitzer" lists everything below the folder flat with FetchOwner (1 request per 1000
/// objects) and sums by the owner of each object. Buckets with "BucketOwnerEnforced" (the default for new AWS buckets)
/// have only one owner: the bucket's.
/// </summary>
public sealed partial class S3Provider : IOwnerSource
{
    public string RightsTitle => "Rechte (Bucket)";

    public bool HasGroups => false;

    /// <summary>Owners come with the listing (FetchOwner): sampling would not save a request.</summary>
    public bool SamplingHelps => false;

    /// <summary>
    /// The bucket's owner (GetBucketAcl) and, where readable, its object ownership setting. Null if the server sends
    /// no owner. Throws (translated) if the ACL cannot be read.
    /// </summary>
    public async Task<OwnerDetails?> GetOwnerAsync(string path, CancellationToken ct)
    {
        var target = Resolve(path);
        var (bucket, _) = SplitChecked(path);
        var acl = await target.Client.GetBucketAclAsync(bucket, ct).ConfigureAwait(false);
        if (acl.Owner == null) return null;
        string? ownership = null;
        try
        {
            ownership = await target.Client.GetObjectOwnershipAsync(bucket, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // optional detail: without s3:GetBucketOwnershipControls (or on a server without it) the owner alone is shown
        }
        return new OwnerDetails(S3Owners.Label(acl.Owner) + " (Bucket-Besitzer)", null,
            ownership != null ? "Objektbesitz: " + S3Owners.OwnershipText(ownership) : null);
    }

    public string[] GetRights(string path, CancellationToken ct)
    {
        var target = Resolve(path);
        var (bucket, _) = SplitChecked(path);
        var client = target.Client;
        var lines = new List<string> { "Bucket: " + bucket };

        // Each part on its own: a missing permission (or a server without the call) costs one line, not the whole section.
        bool Read<T>(Func<CancellationToken, Task<T>> call, string what, string permission, out T? value)
        {
            value = default;
            try
            {
                value = call(ct).GetAwaiter().GetResult();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                lines.Add($"{what}: nicht lesbar (keine Berechtigung {permission})");
            }
            catch (IOException ex)
            {
                lines.Add($"{what}: nicht lesbar – {ex.Message}");
            }
            return false;
        }

        if (Read(t => client.GetBucketAclAsync(bucket, t), "ACL", "s3:GetBucketAcl", out var acl) && acl != null)
        {
            if (acl.Owner != null) lines.Add("Besitzer: " + S3Owners.Label(acl.Owner) + (acl.Owner.Id.Length > 0 ? "  (ID " + acl.Owner.Id + ")" : ""));
            lines.Add(acl.Grants.Count == 0 ? "ACL: keine Einträge" : "ACL:");
            foreach (var g in acl.Grants)
                lines.Add("  " + S3Owners.GranteeText(g, acl.Owner) + ": " + S3Owners.PermissionText(g.Permission) +
                          (S3Owners.IsPublic(g) ? "  ⚠ öffentlich" : ""));
        }

        if (Read(t => client.GetObjectOwnershipAsync(bucket, t), "Objektbesitz", "s3:GetBucketOwnershipControls", out var ownership))
            lines.Add("Objektbesitz: " + (ownership != null
                ? S3Owners.OwnershipText(ownership)
                : "nicht festgelegt (wer ein Objekt hochlädt, besitzt es)"));

        if (Read(t => client.GetPublicAccessBlockAsync(bucket, t), "Öffentlicher Zugriff", "s3:GetBucketPublicAccessBlock", out var block))
            lines.Add("Öffentlicher Zugriff: " + S3Owners.BlockText(block));

        if (Read(t => client.GetPolicyIsPublicAsync(bucket, t), "Bucket-Richtlinie", "s3:GetBucketPolicyStatus", out var policy))
            lines.Add("Bucket-Richtlinie: " + policy switch
            {
                null => "keine",
                true => "macht den Bucket öffentlich  ⚠",
                false => "vorhanden, nicht öffentlich",
            });

        lines.Add("");
        lines.Add("S3 kennt keine Rechte pro Ordner: die Angaben gelten für den ganzen Bucket. " +
                  "IAM-Richtlinien der Benutzer und Rollen sind hier nicht sichtbar.");
        return lines.ToArray();
    }

    /// <summary>
    /// Lists all objects below <paramref name="folder"/> with their owners (flat, FetchOwner) and sums them per owner.
    /// Folder markers are not counted. A listing error ends the run with the partial result and the message.
    /// </summary>
    public OwnerResult Aggregate(string folder, long minSize, Action<long, long>? progress, CancellationToken ct)
    {
        S3Provider target;
        string bucket, prefix;
        try
        {
            target = Resolve(folder);
            (bucket, prefix) = SplitChecked(folder);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new OwnerResult([], 0, 0, 0, false, ex.Message, true);
        }

        var by = new Dictionary<string, (S3Owner? Owner, long Bytes, long Files)>(StringComparer.Ordinal);
        long seen = 0;
        bool cancelled = false;
        string? error = null;
        string? token = null;
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var page = target.Client.ListWithOwnersAsync(bucket, prefix, token, PageSize, ct).GetAwaiter().GetResult();
                foreach (var o in page.Objects)
                {
                    if (!o.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var rel = o.Key[prefix.Length..];
                    if ((rel.Length == 0 || rel.EndsWith('/')) && o.Size == 0) continue; // folder marker
                    seen++;
                    var key = S3Owners.Key(o.Owner);
                    var (_, b, n) = by.GetValueOrDefault(key);
                    by[key] = (o.Owner, b + o.Size, n + 1);
                }
                progress?.Invoke(seen, 0);
                token = page.NextToken;
            } while (token != null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }

        // Marks the bucket's owner among the rows (one more request; without s3:GetBucketAcl the rows stay unmarked).
        string? bucketOwner = null;
        if (by.Count > 0 && error == null && !cancelled)
        {
            try { bucketOwner = target.Client.GetBucketAclAsync(bucket, ct).GetAwaiter().GetResult().Owner?.Id; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        }

        var rows = by.Values
            .Select(v => new OwnerRow(S3Owners.Label(v.Owner) +
                                      (v.Owner != null && bucketOwner != null && v.Owner.Id == bucketOwner ? " (Bucket-Besitzer)" : ""),
                v.Bytes, v.Files))
            .OrderByDescending(r => r.Bytes).ThenBy(r => r.Owner, StringComparer.Ordinal).ToList();
        return new OwnerResult(rows, seen, 0, 0, cancelled, error, true);
    }
}

/// <summary>How S3 owners, grants and bucket settings are shown (German).</summary>
public static class S3Owners
{
    public const string Unknown = "(Besitzer nicht gemeldet)";

    /// <summary>The aggregation key of an owner: its id, else its name; "" for objects the server sends no owner for.</summary>
    public static string Key(S3Owner? owner) =>
        owner == null ? "" : owner.Id.Length > 0 ? owner.Id : "name:" + owner.DisplayName;

    /// <summary>The display name, else the canonical id shortened ("ID 79a59df9…bdbf4").</summary>
    public static string Label(S3Owner? owner)
    {
        if (owner == null) return Unknown;
        if (!string.IsNullOrWhiteSpace(owner.DisplayName)) return owner.DisplayName.Trim();
        return "ID " + ShortId(owner.Id);
    }

    public static string ShortId(string id) => id.Length > 16 ? id[..8] + "…" + id[^5..] : id;

    private const string GroupBase = "http://acs.amazonaws.com/groups/";

    /// <summary>The grants that open the bucket to everyone (AllUsers) or to every AWS account (AuthenticatedUsers).</summary>
    public static bool IsPublic(S3Grant g) =>
        g.Type == "Group" && (g.Id.EndsWith("/global/AllUsers", StringComparison.Ordinal)
                              || g.Id.EndsWith("/global/AuthenticatedUsers", StringComparison.Ordinal));

    public static string GranteeText(S3Grant g, S3Owner? bucketOwner) => g.Type switch
    {
        "Group" => g.Id switch
        {
            GroupBase + "global/AllUsers" => "Alle (AllUsers)",
            GroupBase + "global/AuthenticatedUsers" => "Alle AWS-Konten (AuthenticatedUsers)",
            GroupBase + "s3/LogDelivery" => "S3-Protokollzustellung (LogDelivery)",
            _ => "Gruppe " + g.Id,
        },
        "AmazonCustomerByEmail" => g.Id,
        _ => Label(new S3Owner(g.Id, g.DisplayName)) +
             (bucketOwner != null && g.Id.Length > 0 && g.Id == bucketOwner.Id ? " (Besitzer)" : ""),
    };

    public static string PermissionText(string permission) => permission switch
    {
        "FULL_CONTROL" => "Vollzugriff",
        "READ" => "Lesen (Objekte auflisten)",
        "WRITE" => "Schreiben (Objekte anlegen, überschreiben, löschen)",
        "READ_ACP" => "ACL lesen",
        "WRITE_ACP" => "ACL ändern",
        _ => permission,
    };

    public static string OwnershipText(string ownership) => ownership switch
    {
        "BucketOwnerEnforced" => "Bucket-Besitzer erzwungen (ACLs aus, alle Objekte gehören dem Bucket-Besitzer)",
        "BucketOwnerPreferred" => "Bucket-Besitzer bevorzugt (neue Objekte mit „bucket-owner-full-control“ gehören dem Bucket-Besitzer)",
        "ObjectWriter" => "Objekt-Schreiber (wer ein Objekt hochlädt, besitzt es)",
        _ => ownership,
    };

    public static string BlockText(S3PublicAccessBlock? b)
    {
        if (b == null) return "auf Bucket-Ebene nicht blockiert (Einstellung des Kontos kann gelten)";
        if (b.All) return "vollständig blockiert";
        var on = new List<string>();
        if (b.BlockPublicAcls) on.Add("neue öffentliche ACLs");
        if (b.IgnorePublicAcls) on.Add("bestehende öffentliche ACLs");
        if (b.BlockPublicPolicy) on.Add("neue öffentliche Richtlinien");
        if (b.RestrictPublicBuckets) on.Add("öffentliche Richtlinien");
        return on.Count == 0 ? "nicht blockiert" : "teilweise blockiert (" + string.Join(", ", on) + ")";
    }
}
