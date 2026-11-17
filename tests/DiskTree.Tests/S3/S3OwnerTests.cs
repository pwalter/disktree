using DiskTree.Core.Storage;
using DiskTree.Features.Storage.S3;
using Xunit;

namespace DiskTree.Tests.S3;

/// <summary>
/// The "Besitzer" tab on S3 against the in-memory server: the bucket's owner for a folder, the bucket's rights part by
/// part (a refused part costs one line), and "Speicher pro Besitzer" from a flat listing with owners.
/// </summary>
[Collection(DiskTree.Tests.Storage.SourceStateCollection.Name)]
public sealed class S3OwnerTests : IDisposable
{
    private static readonly S3Owner Anna = new("1111111111111111111111111111111111111111111111111111111111111111", "anna");
    private static readonly S3Owner NoName = new("2222222222222222222222222222222222222222222222222222222222222222");

    private readonly Func<SourceProfile?, string?, IS3Client> _oldFactory = S3Provider.ClientFactory;
    private readonly MemoryS3Client _server = new();

    public S3OwnerTests()
    {
        S3Storage.Register();
        S3Provider.ClientFactory = (_, _) => _server;
        SourceProfiles.Set([]);
    }

    public void Dispose()
    {
        S3Provider.ClientFactory = _oldFactory;
        SourceProfiles.Set([]);
    }

    private static S3Provider Provider => S3Provider.For(null);

    [Fact]
    public void Sums_per_object_owner_marks_the_bucket_owner_and_skips_folder_markers()
    {
        _server.Put("bk", "a/", 0)
            .Put("bk", "a/x.bin", 100).Own("bk", "a/x.bin", MemoryS3Client.DefaultOwner)
            .Put("bk", "a/tief/y.bin", 300).Own("bk", "a/tief/y.bin", Anna)
            .Put("bk", "a/tief/z.bin", 50).Own("bk", "a/tief/z.bin", NoName)
            .Put("bk", "a/ohne.txt", 7)                                     // server sends no owner
            .Put("bk", "b/fremd.bin", 999).Own("bk", "b/fremd.bin", Anna);  // outside the folder

        var r = Provider.Aggregate("s3://bk/a", 0, null, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.False(r.Cancelled);
        Assert.Equal(4, r.Seen);
        Assert.Equal(["anna", "owner (Bucket-Besitzer)", "ID 22222222…22222", S3Owners.Unknown], r.Rows.Select(x => x.Owner));
        Assert.Equal([300L, 100, 50, 7], r.Rows.Select(x => x.Bytes));
        Assert.Null(r.GroupRows);
        Assert.False(Provider.HasGroups);
        Assert.False(Provider.SamplingHelps);
    }

    [Fact]
    public void Lists_in_pages_of_1000_and_reports_progress()
    {
        for (int i = 0; i < 2500; i++) _server.Put("bk", $"d/{i:D5}", 1).Own("bk", $"d/{i:D5}", Anna);
        var seen = new List<long>();

        var r = Provider.Aggregate("s3://bk/d", 0, (s, _) => seen.Add(s), CancellationToken.None);

        Assert.Equal(2500, r.Seen);
        Assert.Equal([1000L, 2000, 2500], seen);
        Assert.Equal(new OwnerRow("anna", 2500, 2500), Assert.Single(r.Rows));
        Assert.Equal(3 + 1, _server.Requests); // 3 pages + the bucket's ACL
    }

    [Fact]
    public void Without_acl_permission_the_rows_stay_unmarked()
    {
        _server.Put("bk", "x", 5).Own("bk", "x", MemoryS3Client.DefaultOwner);
        _server.DenyBucketCalls.Add("acl");

        var r = Provider.Aggregate("s3://bk", 0, null, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.Equal("owner", Assert.Single(r.Rows).Owner);
    }

    [Fact]
    public void A_listing_error_ends_with_the_message_and_a_cancel_with_a_partial_result()
    {
        _server.Bucket("bk").Failures["bk/a/"] = new UnauthorizedAccessException("Zugriff verweigert auf „bk“ (AccessDenied).");
        var r = Provider.Aggregate("s3://bk/a", 0, null, CancellationToken.None);
        Assert.Contains("AccessDenied", r.Error);
        Assert.Empty(r.Rows);

        Assert.NotNull(Provider.Aggregate("s3://gibtsnicht", 0, null, CancellationToken.None).Error);
        Assert.NotNull(Provider.Aggregate("s3://", 0, null, CancellationToken.None).Error);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _server.Put("bk", "c/1", 1);
        var c = Provider.Aggregate("s3://bk/c", 0, null, cts.Token);
        Assert.True(c.Cancelled);
        Assert.Null(c.Error);
    }

    [Fact]
    public async Task Owner_of_a_folder_is_the_bucket_owner_with_the_object_ownership()
    {
        _server.Bucket("bk").Ownership["bk"] = "BucketOwnerEnforced";

        var o = await Provider.GetOwnerAsync("s3://bk/a/b", CancellationToken.None);

        Assert.Equal("owner (Bucket-Besitzer)", o!.Owner);
        Assert.Null(o.Group);
        Assert.Contains("ACLs aus", o.Permissions);

        _server.DenyBucketCalls.Add("ownership"); // optional part: the owner alone
        o = await Provider.GetOwnerAsync("s3://bk", CancellationToken.None);
        Assert.Null(o!.Permissions);

        _server.DenyBucketCalls.Add("acl");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Provider.GetOwnerAsync("s3://bk", CancellationToken.None));
    }

    [Fact]
    public void Rights_show_acl_ownership_block_and_policy()
    {
        _server.Bucket("pub");
        _server.Acls["pub"] = new S3Acl(Anna,
        [
            new S3Grant("CanonicalUser", Anna.Id, "anna", "FULL_CONTROL"),
            new S3Grant("Group", "http://acs.amazonaws.com/groups/global/AllUsers", null, "READ"),
            new S3Grant("AmazonCustomerByEmail", "bob@example.com", null, "WRITE_ACP"),
        ]);
        _server.Ownership["pub"] = "ObjectWriter";
        _server.Blocks["pub"] = new S3PublicAccessBlock(true, false, false, false);
        _server.PolicyPublic["pub"] = true;

        var lines = Provider.GetRights("s3://pub/ordner", CancellationToken.None);

        Assert.Equal("Bucket: pub", lines[0]);
        Assert.Contains("Besitzer: anna  (ID " + Anna.Id + ")", lines);
        Assert.Contains("  anna (Besitzer): Vollzugriff", lines);
        Assert.Contains("  Alle (AllUsers): Lesen (Objekte auflisten)  ⚠ öffentlich", lines);
        Assert.Contains("  bob@example.com: ACL ändern", lines);
        Assert.Contains(lines, l => l.StartsWith("Objektbesitz: Objekt-Schreiber"));
        Assert.Contains("Öffentlicher Zugriff: teilweise blockiert (neue öffentliche ACLs)", lines);
        Assert.Contains("Bucket-Richtlinie: macht den Bucket öffentlich  ⚠", lines);
        Assert.Contains(lines, l => l.StartsWith("S3 kennt keine Rechte pro Ordner"));
        Assert.Equal("Rechte (Bucket)", Provider.RightsTitle);
    }

    [Fact]
    public void Rights_defaults_and_refused_parts()
    {
        _server.Bucket("bk");
        _server.DenyBucketCalls.UnionWith(["ownership", "policy"]);

        var lines = Provider.GetRights("s3://bk", CancellationToken.None);

        Assert.Contains("  owner (Besitzer): Vollzugriff", lines);
        Assert.Contains("Objektbesitz: nicht lesbar (keine Berechtigung s3:GetBucketOwnershipControls)", lines);
        Assert.Contains(lines, l => l.StartsWith("Öffentlicher Zugriff: auf Bucket-Ebene nicht blockiert"));
        Assert.Contains("Bucket-Richtlinie: nicht lesbar (keine Berechtigung s3:GetBucketPolicyStatus)", lines);

        _server.DenyBucketCalls.Clear();
        _server.Blocks["bk"] = new S3PublicAccessBlock(true, true, true, true);
        lines = Provider.GetRights("s3://bk", CancellationToken.None);
        Assert.Contains("Objektbesitz: nicht festgelegt (wer ein Objekt hochlädt, besitzt es)", lines);
        Assert.Contains("Öffentlicher Zugriff: vollständig blockiert", lines);
        Assert.Contains("Bucket-Richtlinie: keine", lines);
    }

    [Fact]
    public void The_generic_provider_answers_through_the_profile_of_the_path()
    {
        _server.Put("bk", "x", 5).Own("bk", "x", Anna);
        var r = S3Provider.Generic.Aggregate("s3://bk", 0, null, CancellationToken.None);
        Assert.Equal("anna", Assert.Single(r.Rows).Owner);
    }

    [Theory]
    [InlineData(null, null, "(Besitzer nicht gemeldet)")]
    [InlineData("abc", null, "ID abc")]
    [InlineData("abc", " minio ", "minio")]
    public void Labels(string? id, string? name, string expected) =>
        Assert.Equal(expected, S3Owners.Label(id == null ? null : new S3Owner(id, name)));
}
