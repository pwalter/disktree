namespace DiskTree.Features.Storage.Sftp;

/// <summary>SHA256 host key fingerprints in one form: "SHA256:" + base64 without padding (as <c>ssh-keygen -l</c> prints them).</summary>
public static class SftpFingerprint
{
    /// <summary>"SHA256:abc", "sha256:abc=", " abc " all give "SHA256:abc"; empty gives null.</summary>
    public static string? Normalize(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        var f = fingerprint.Trim();
        if (f.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) f = f[7..];
        f = f.Trim().TrimEnd('=');
        return f.Length == 0 ? null : "SHA256:" + f;
    }

    /// <summary>Both given and equal (base64 is case-sensitive).</summary>
    public static bool Same(string? a, string? b) =>
        Normalize(a) is { } x && string.Equals(x, Normalize(b), StringComparison.Ordinal);
}
