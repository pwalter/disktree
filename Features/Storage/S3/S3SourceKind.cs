using System.Text.RegularExpressions;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Storage.S3;

/// <summary>Registers S3 at start (no AWS SDK is loaded by this).</summary>
public static class S3Storage
{
    public static void Register()
    {
        SourceKinds.Register(S3SourceKind.Instance);
        StorageProviders.Register(S3Provider.Generic);
    }
}

/// <summary>S3 for the "Quelle" dialog: fields, checks, connection test.</summary>
public sealed partial class S3SourceKind : ISourceKind
{
    public static readonly S3SourceKind Instance = new();

    private S3SourceKind() { }

    public string Id => S3Provider.ProviderId;
    public string DisplayName => "S3 / S3-kompatibel";
    public string Scheme => S3Path.Scheme;

    public IReadOnlyList<SourceAuth> AuthModes { get; } = [SourceAuth.Default, SourceAuth.NamedProfile, SourceAuth.AccessKey];

    public string AuthName(SourceAuth auth) => auth switch
    {
        SourceAuth.Default => "AWS-Standard (Umgebung, ~/.aws, SSO)",
        SourceAuth.NamedProfile => "Benanntes AWS-Profil",
        SourceAuth.AccessKey => "Access-Key + Secret",
        _ => auth.ToString(),
    };

    public string? Label(SourceField field, SourceAuth auth) => field switch
    {
        SourceField.Endpoint => "Endpoint-URL (leer = AWS)",
        SourceField.Region => "Region",
        SourceField.PathStyle => "Pfad-Stil (path-style)",
        SourceField.UserName when auth == SourceAuth.AccessKey => "Access-Key-ID",
        SourceField.Secret when auth == SourceAuth.AccessKey => "Secret-Access-Key",
        SourceField.AwsProfile when auth == SourceAuth.NamedProfile => "AWS-Profil",
        SourceField.Path => "Bucket/Präfix",
        SourceField.Listing => "Auflistung",
        _ => null,
    };

    public string? Hint(SourceField field) => field switch
    {
        SourceField.Endpoint => "Nur für S3-kompatible Server, z. B. http://localhost:9000 oder https://s3.example.com. Leer = Amazon S3.",
        SourceField.Region => "z. B. eu-central-1. Leer = aus dem AWS-Profil bzw. der Umgebung (AWS_REGION), sonst us-east-1.",
        SourceField.PathStyle => "Adressierung http://server/bucket statt http://bucket.server – für die meisten selbst betriebenen Server nötig.",
        SourceField.UserName => "Die Access-Key-ID (nicht geheim).",
        SourceField.Secret => "Wird nur in der Windows-Anmeldeinformationsverwaltung gespeichert.",
        SourceField.AwsProfile => "Name eines Profils aus ~/.aws/config bzw. ~/.aws/credentials (auch SSO / AssumeRole).",
        SourceField.Path => "z. B. s3://bucket oder s3://bucket/ordner. Leer = Bucket beim Scannen angeben.",
        SourceField.Listing => "Ordnerweise: Baum wächst live, eine LIST-Anfrage je Ordner. Flach: eine Anfrage je 1.000 Objekte, der Baum erscheint erst nach dem Auflisten.",
        _ => null,
    };

    public SourceProfile NewProfile() => new() { Kind = Id, Name = "S3", Auth = SourceAuth.AccessKey };

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex RegionPattern();

    public string? Validate(SourceProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Name)) return "Bitte einen Namen für die Quelle angeben.";
        if (!string.IsNullOrWhiteSpace(p.Endpoint) &&
            (!Uri.TryCreate(p.Endpoint.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            return "Die Endpoint-URL muss mit http:// oder https:// beginnen (z. B. http://localhost:9000).";
        if (!string.IsNullOrWhiteSpace(p.Region) && !RegionPattern().IsMatch(p.Region.Trim()))
            return "Ungültige Region (z. B. eu-central-1).";
        if (p.Auth == SourceAuth.AccessKey && string.IsNullOrWhiteSpace(p.UserName))
            return "Bitte die Access-Key-ID angeben.";
        if (p.Auth == SourceAuth.NamedProfile && string.IsNullOrWhiteSpace(p.AwsProfile))
            return "Bitte den Namen des AWS-Profils angeben.";
        if (!string.IsNullOrWhiteSpace(p.Path) && S3Provider.ScopeOf(p) == null)
            return "Ungültiger Pfad (z. B. s3://bucket/ordner).";
        return null;
    }

    public string PathOf(SourceProfile p) => S3Provider.ScopeOf(p) is { Length: > 0 } scope ? scope : S3Path.Scheme;

    public async Task<string> TestAsync(SourceProfile profile, string? secret, string? passphrase, CancellationToken ct)
    {
        var client = S3Provider.ClientFactory(profile, string.IsNullOrEmpty(secret) ? null : secret);
        try
        {
            var path = PathOf(profile);
            var (bucket, prefix) = S3Path.Split(path);
            if (bucket.Length == 0)
            {
                var buckets = await client.ListBucketsAsync(ct).ConfigureAwait(false);
                var names = string.Join(", ", buckets.Take(5)) + (buckets.Count > 5 ? ", …" : "");
                return $"Verbindung OK ({client.Describe()}): {buckets.Count} Bucket(s) sichtbar" + (buckets.Count > 0 ? $": {names}." : ".");
            }

            var page = await client.ListAsync(bucket, prefix, "/", null, 1, ct).ConfigureAwait(false);
            var text = $"Verbindung OK ({client.Describe()}): Bucket „{bucket}“ erreichbar.";
            if (prefix.Length > 0 && page.Objects.Count == 0 && page.Prefixes.Count == 0)
                text += $" Hinweis: unter „{prefix}“ liegen keine Objekte.";
            return text;
        }
        catch (System.IO.DirectoryNotFoundException ex)
        {
            throw new InvalidOperationException(ex.Message + " (Verbindung und Anmeldung sind in Ordnung.)", ex);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    public void Forget(string profileId) => S3Provider.Forget(profileId);
}
