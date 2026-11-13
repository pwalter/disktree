using System.Collections.Concurrent;

namespace DiskTree.Core.Storage;

/// <summary>Where secrets are kept (the app uses the Windows Credential Manager; tests an in-memory store).</summary>
public interface ISecretStore
{
    /// <summary>The secret stored under <paramref name="target"/>, or false if there is none.</summary>
    bool TryRead(string target, out string secret);

    /// <summary>Stores (or replaces) a secret. <paramref name="userName"/> is shown in the Credential Manager only.</summary>
    void Write(string target, string userName, string secret);

    /// <summary>Removes a secret; nothing happens if there is none.</summary>
    void Delete(string target);
}

/// <summary>Which secret of a profile.</summary>
public enum SecretSlot
{
    /// <summary>S3 secret access key, SFTP password.</summary>
    Secret = 0,
    /// <summary>Passphrase of a private key file.</summary>
    Passphrase = 1,
}

/// <summary>
/// The secrets of saved sources. They are only ever kept in <see cref="Store"/> (by default the Windows Credential
/// Manager of the signed-in user, target "DiskTree:&lt;profile id&gt;" / "DiskTree:&lt;profile id&gt;:passphrase"),
/// never in settings.json, snapshots, exports or logs. Read them right before connecting and do not keep them in
/// fields longer than the connection needs them.
/// </summary>
public static class SourceSecrets
{
    public const string Prefix = "DiskTree:";

    /// <summary>The store in use. The app sets the Windows Credential Manager at start; tests set a <see cref="MemorySecretStore"/>.</summary>
    public static ISecretStore Store { get; set; } = new MemorySecretStore();

    public static string Target(string profileId, SecretSlot slot = SecretSlot.Secret) =>
        slot == SecretSlot.Passphrase ? $"{Prefix}{profileId}:passphrase" : Prefix + profileId;

    public static string? Read(SourceProfile profile, SecretSlot slot = SecretSlot.Secret) =>
        Store.TryRead(Target(profile.Id, slot), out var s) ? s : null;

    public static bool Has(SourceProfile profile, SecretSlot slot = SecretSlot.Secret) => Read(profile, slot) != null;

    public static void Write(SourceProfile profile, SecretSlot slot, string secret) =>
        Store.Write(Target(profile.Id, slot), string.IsNullOrEmpty(profile.UserName) ? profile.Name : profile.UserName, secret);

    public static void Delete(SourceProfile profile, SecretSlot slot) => Store.Delete(Target(profile.Id, slot));

    /// <summary>Removes every secret of a profile (it is deleted).</summary>
    public static void DeleteAll(SourceProfile profile)
    {
        foreach (var slot in Enum.GetValues<SecretSlot>()) Delete(profile, slot);
    }
}

/// <summary>Secrets in memory only (tests; the default until the app installs the Credential Manager).</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Targets => _items.Keys.ToList();

    public bool TryRead(string target, out string secret) => _items.TryGetValue(target, out secret!);

    public void Write(string target, string userName, string secret) => _items[target] = secret;

    public void Delete(string target) => _items.TryRemove(target, out _);
}
