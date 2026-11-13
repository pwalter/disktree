namespace DiskTree.Core.Storage;

/// <summary>How a saved source signs in.</summary>
public enum SourceAuth
{
    /// <summary>What the environment provides (S3: the AWS default credential chain - environment, ~/.aws, SSO).</summary>
    Default = 0,
    /// <summary>A named profile of the source's own configuration (S3: a profile from ~/.aws/config / credentials).</summary>
    NamedProfile = 1,
    /// <summary>Key id (<see cref="SourceProfile.UserName"/>) + secret key (Windows Credential Manager).</summary>
    AccessKey = 2,
    /// <summary>User name + password (Windows Credential Manager).</summary>
    Password = 3,
    /// <summary>User name + private key file (<see cref="SourceProfile.KeyFile"/>), passphrase in the Credential Manager.</summary>
    KeyFile = 4,
    /// <summary>User name, keys from an SSH agent (Pageant / OpenSSH agent).</summary>
    Agent = 5,
}

/// <summary>How a source lists its folders (only for sources that offer both, see <see cref="ISourceKind"/>).</summary>
public enum SourceListing
{
    /// <summary>One request per folder, folders appear while the scan runs (live tree).</summary>
    PerFolder = 0,
    /// <summary>Everything below the scanned folder in as few requests as possible; the tree is built locally.</summary>
    Flat = 1,
}

/// <summary>
/// A saved connection ("Quelle"): everything needed to reach a remote source EXCEPT secrets. It is stored in
/// settings.json (key "Sources"); secret keys, passwords and passphrases live only in the Windows Credential Manager
/// (see <see cref="SourceSecrets"/>), keyed by <see cref="Id"/>. Fields a kind does not use stay empty.
/// </summary>
public sealed class SourceProfile
{
    /// <summary>Stable key (GUID without dashes); also names the Credential Manager entries.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Name for the user ("Backup-Bucket").</summary>
    public string Name { get; set; } = "";

    /// <summary>The <see cref="ISourceKind.Id"/> (= provider id): "s3", "sftp".</summary>
    public string Kind { get; set; } = "";

    /// <summary>S3: service URL of an S3-compatible server (empty = AWS); SFTP: host name.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>SFTP: port (0 = default).</summary>
    public int Port { get; set; }

    /// <summary>S3: region ("eu-central-1"; empty = detect / default).</summary>
    public string Region { get; set; } = "";

    /// <summary>S3: path-style addressing (http://host/bucket instead of http://bucket.host), usual for self-hosted servers.</summary>
    public bool PathStyle { get; set; }

    public SourceAuth Auth { get; set; }

    /// <summary>S3: access key id; SFTP: user name. Not secret.</summary>
    public string UserName { get; set; } = "";

    /// <summary>S3: name of the AWS profile (<see cref="SourceAuth.NamedProfile"/>).</summary>
    public string AwsProfile { get; set; } = "";

    /// <summary>SFTP: private key file (<see cref="SourceAuth.KeyFile"/>).</summary>
    public string KeyFile { get; set; } = "";

    /// <summary>What a scan of this source starts with: "s3://bucket/prefix", "sftp://host/path" (may be empty).</summary>
    public string Path { get; set; } = "";

    public SourceListing Listing { get; set; }

    /// <summary>Further non-secret settings of a kind (key/value), for options that have no field of their own.</summary>
    public Dictionary<string, string>? Extra { get; set; }

    public SourceProfile Clone()
    {
        var c = (SourceProfile)MemberwiseClone();
        c.Extra = Extra == null ? null : new Dictionary<string, string>(Extra);
        return c;
    }

    public override string ToString() => Name;
}
