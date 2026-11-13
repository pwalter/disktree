namespace DiskTree.Core.Storage;

/// <summary>The fields of the "Quelle" dialog; a kind says which of them it uses (<see cref="ISourceKind.Label"/>).</summary>
public enum SourceField
{
    Endpoint,
    Port,
    Region,
    PathStyle,
    UserName,
    Secret,
    Passphrase,
    KeyFile,
    AwsProfile,
    Path,
    Listing,
}

/// <summary>
/// A type of remote source (S3, SFTP) as far as the app needs to know it for the saved connections: the fields of
/// its dialog, how a profile is checked and tested, and the provider that scans it. Kinds register themselves in
/// <see cref="SourceKinds"/> at start; registering must not load the client library (that happens on first use).
/// <para>
/// The provider of a kind registers itself in <see cref="StorageProviders"/> under the same <see cref="Id"/>. Its
/// <see cref="IStorageProvider.TryParse"/> recognizes its URIs ("s3://…", "sftp://…") and hands out a location whose
/// provider is bound to the matching saved profile (<see cref="SourceProfiles.Match"/>).
/// </para>
/// </summary>
public interface ISourceKind
{
    /// <summary>= <see cref="IStorageProvider.Id"/> of its provider and <see cref="SourceProfile.Kind"/>: "s3".</summary>
    string Id { get; }

    /// <summary>For the type box: "S3 / S3-kompatibel".</summary>
    string DisplayName { get; }

    /// <summary>The start of its paths: "s3://".</summary>
    string Scheme { get; }

    /// <summary>The sign-in modes the dialog offers, the default first.</summary>
    IReadOnlyList<SourceAuth> AuthModes { get; }

    /// <summary>Name of a sign-in mode for the dialog.</summary>
    string AuthName(SourceAuth auth);

    /// <summary>Label of a field with this sign-in mode, or null if the field is not shown.</summary>
    string? Label(SourceField field, SourceAuth auth);

    /// <summary>Tooltip / example for a field (null = none).</summary>
    string? Hint(SourceField field);

    /// <summary>A new profile with this kind's defaults.</summary>
    SourceProfile NewProfile();

    /// <summary>Checks a profile before it is saved; a German message or null if fine.</summary>
    string? Validate(SourceProfile profile);

    /// <summary>The text for the path box that scans this profile (its <see cref="SourceProfile.Path"/>, normalized).</summary>
    string PathOf(SourceProfile profile);

    /// <summary>
    /// "Verbindung testen": connects with <paramref name="profile"/> and the secret typed into the dialog
    /// (<paramref name="secret"/> / <paramref name="passphrase"/>; null = the stored one) and returns a short success
    /// message. Throws with a message for the user if it fails.
    /// </summary>
    Task<string> TestAsync(SourceProfile profile, string? secret, string? passphrase, CancellationToken ct);

    /// <summary>Profiles were changed or deleted: drop connections / clients built from old settings.</summary>
    void Forget(string profileId);
}

/// <summary>The registered source kinds (S3, SFTP …).</summary>
public static class SourceKinds
{
    private static readonly object Lock = new();
    private static ISourceKind[] _all = [];

    public static IReadOnlyList<ISourceKind> All => Volatile.Read(ref _all);

    /// <summary>Adds (or replaces, by id) a kind.</summary>
    public static void Register(ISourceKind kind)
    {
        lock (Lock) Volatile.Write(ref _all, _all.Where(k => k.Id != kind.Id).Append(kind).ToArray());
    }

    public static void Unregister(string id)
    {
        lock (Lock) Volatile.Write(ref _all, _all.Where(k => k.Id != id).ToArray());
    }

    public static ISourceKind? Get(string? id) => All.FirstOrDefault(k => k.Id == id);

    /// <summary>The kind whose scheme the text starts with ("s3://bucket" gives S3), or null.</summary>
    public static ISourceKind? ForText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.TrimStart().TrimStart('"');
        return All.FirstOrDefault(k => t.StartsWith(k.Scheme, StringComparison.OrdinalIgnoreCase));
    }
}
