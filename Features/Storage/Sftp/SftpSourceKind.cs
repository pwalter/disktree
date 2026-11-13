using System.IO;
using DiskTree.Core;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Storage.Sftp;

/// <summary>SFTP for the "Quelle" dialog: fields, checks, connection test.</summary>
public sealed class SftpSourceKind : ISourceKind
{
    public static readonly SftpSourceKind Instance = new();

    private SftpSourceKind() { }

    public string Id => SftpProvider.ProviderId;
    public string DisplayName => "SFTP (SSH)";
    public string Scheme => SftpUri.Prefix;

    // SSH.NET has no SSH agent support (Pageant / OpenSSH agent), so there is no "Agent" mode.
    public IReadOnlyList<SourceAuth> AuthModes { get; } = [SourceAuth.Password, SourceAuth.KeyFile];

    public bool RequiresProfile => true;

    public string AuthName(SourceAuth auth) => auth switch
    {
        SourceAuth.Password => "Benutzer + Passwort",
        SourceAuth.KeyFile => "Benutzer + private Schlüsseldatei",
        _ => auth.ToString(),
    };

    public string? Label(SourceField field, SourceAuth auth) => field switch
    {
        SourceField.Endpoint => "Server (Host)",
        SourceField.Port => "Port (leer = 22)",
        SourceField.UserName => "Benutzer",
        SourceField.Secret when auth == SourceAuth.Password => "Passwort",
        SourceField.KeyFile when auth == SourceAuth.KeyFile => "Schlüsseldatei",
        SourceField.Passphrase when auth == SourceAuth.KeyFile => "Passphrase (falls verschlüsselt)",
        SourceField.Path => "Startordner",
        SourceField.HostKey => "Host-Schlüssel",
        _ => null,
    };

    public string? Hint(SourceField field) => field switch
    {
        SourceField.Endpoint => "Name oder IP-Adresse des Servers, z. B. nas.local oder 192.168.1.20 (ohne sftp://).",
        SourceField.Port => "Meist 22.",
        SourceField.UserName => "Der Anmeldename auf dem Server.",
        SourceField.Secret => "Wird nur in der Windows-Anmeldeinformationsverwaltung gespeichert.",
        SourceField.KeyFile => @"Privater Schlüssel im OpenSSH-, PEM- oder PuTTY-Format, z. B. %USERPROFILE%\.ssh\id_ed25519. Die Datei bleibt, wo sie ist.",
        SourceField.Passphrase => "Nur für verschlüsselte Schlüssel; wird nur in der Windows-Anmeldeinformationsverwaltung gespeichert.",
        SourceField.Path => "z. B. /srv/daten. Leer oder ~ = Home-Verzeichnis des Benutzers; ohne / am Anfang relativ dazu.",
        SourceField.HostKey => "Der SHA256-Fingerprint, dem beim ersten Verbinden vertraut wurde. Zeigt der Server später einen anderen, verbindet DiskTree nicht.",
        _ => null,
    };

    public SourceProfile NewProfile() => new() { Kind = Id, Name = "SFTP", Auth = SourceAuth.Password };

    public string? Validate(SourceProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Name)) return "Bitte einen Namen für die Quelle angeben.";
        if (string.IsNullOrWhiteSpace(p.Endpoint)) return "Bitte den Server angeben (z. B. nas.local).";
        if (SftpUri.HasScheme(p.Endpoint)) return "Bitte nur den Servernamen angeben, ohne sftp:// (z. B. nas.local).";
        if (p.Port is < 0 or > 65535) return "Ungültiger Port (1–65535, leer = 22).";
        if (SftpSource.ServerOf(p) == null) return "Ungültiger Servername.";
        if (string.IsNullOrWhiteSpace(p.UserName)) return "Bitte den Benutzer angeben.";
        if (p.UserName.Trim().Any(c => c is '/' or '\\' || char.IsControl(c) || char.IsWhiteSpace(c))) return "Ungültiger Benutzername.";
        if (p.Auth == SourceAuth.KeyFile && string.IsNullOrWhiteSpace(p.KeyFile)) return "Bitte die Schlüsseldatei angeben.";
        if (p.Auth is not (SourceAuth.Password or SourceAuth.KeyFile)) return "Bitte eine Anmeldeart wählen.";
        var path = p.Path.Trim().Trim('"');
        if (SftpUri.HasScheme(path))
        {
            if (!SftpUri.TryParse(path, out var uri)) return "Ungültiger Startordner (z. B. /srv/daten).";
            var server = SftpSource.ServerOf(p);
            if (server is { } s && (uri.Host != s.Host || uri.Port != s.Port || (uri.User.Length > 0 && uri.User != p.UserName.Trim())))
                return "Der Startordner gehört zu einem anderen Server oder Benutzer; bitte nur den Ordner angeben (z. B. /srv/daten).";
        }
        return null;
    }

    public string PathOf(SourceProfile p) =>
        SftpSource.AuthorityOf(p) is { } authority ? authority + SftpSource.StartOf(p) : SftpUri.Prefix;

    public SourceProfile ProfileFor(string path)
    {
        var p = NewProfile();
        if (!SftpUri.TryParse(path, out var uri)) return p;
        p.Name = uri.Host;
        p.Endpoint = uri.Host;
        p.Port = uri.Port == SftpUri.DefaultPort ? 0 : uri.Port;
        p.UserName = uri.User;
        p.Path = uri.Path;
        return p;
    }

    public async Task<string> TestAsync(SourceProfile profile, string? secret, string? passphrase, CancellationToken ct)
    {
        SftpAuth auth;
        try
        {
            auth = SftpSource.ReadAuth(profile, secret, passphrase);
        }
        catch (SftpLoginException) when (profile.Auth == SourceAuth.Password)
        {
            throw new InvalidOperationException("Bitte das Passwort eingeben.");
        }
        var options = SftpSource.OptionsFor(profile, auth, persist: false);
        var start = SftpSource.StartOf(profile);

        SftpConnectionTestResult r;
        try
        {
            r = await Task.Run(() => SftpProvider.TestConnection(options, start, ct), ct).ConfigureAwait(false);
        }
        catch (SftpHostKeyException ex) when (ex.Problem == SftpHostKeyProblem.Changed)
        {
            throw new InvalidOperationException(
                $"WARNUNG: Der Host-Schlüssel des Servers hat sich geändert!\nGespeichert: {ex.Expected}\nJetzt: {ex.Fingerprint}\n" +
                "Das kann ein Angriff (Man-in-the-Middle) sein – oder der Server wurde neu installiert. Es wurde nicht verbunden. " +
                "Nur wenn die Änderung sicher erwartet ist: „Gespeicherten Fingerprint zurücksetzen“ und erneut testen.", ex);
        }
        catch (SftpHostKeyException ex)
        {
            throw new InvalidOperationException($"Dem Server wurde nicht vertraut (Fingerprint {ex.Fingerprint}); es wurde nicht verbunden.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new InvalidOperationException($"Der Startordner wurde nicht gefunden ({start}). Verbindung und Anmeldung sind in Ordnung.", ex);
        }

        // The confirmed key goes with the profile; the dialog stores it on "Speichern".
        profile.HostKeySha256 = r.HostKeySha256;
        var text = $"Verbindung OK ({ServerName(r.ServerVersion)}, {r.ConnectTime.TotalMilliseconds:0} ms). " +
                   $"Host-Schlüssel {r.HostKeySha256}. {RemoteOf(r.Folder)}: {r.Folders:N0} Ordner, {r.Files:N0} Dateien";
        if (r.Total > 0) text += $", frei {SizeFormat.Format(r.Free)} von {SizeFormat.Format(r.Total)}";
        return text + ".";
    }

    private static string ServerName(string version) => version.StartsWith("SSH-2.0-", StringComparison.Ordinal) ? version[8..] : version;

    private static string RemoteOf(string folder)
    {
        int i = SftpUri.PathStart(folder);
        return i < 0 || i >= folder.Length ? "/" : folder[i..];
    }

    public void Forget(string profileId) => SftpSource.Forget(profileId);
}
