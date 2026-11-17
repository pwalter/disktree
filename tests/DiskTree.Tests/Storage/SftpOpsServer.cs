using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using DiskTree.Features.Storage.Sftp;
using Xunit;

namespace DiskTree.Tests.Storage;

[CollectionDefinition(Name)]
public sealed class SftpOpsCollection : ICollectionFixture<SftpOpsServer>
{
    public const string Name = "SFTP-Server für Besitzer und Dateiaktionen";
}

/// <summary>
/// A throw-away atmoz/sftp container (own name, random port and password) with three users, for the owner and file
/// action tests (only with DISKTREE_SFTP_TESTS=1, like <see cref="SftpTestServer"/>):
/// <list type="bullet">
/// <item><c>tester</c> (uid 1001, gid 100): SFTP only, chroot /home/tester; a copy of passwd/group lies in the chroot
/// (/etc inside it), so names come from there (no exec).</item>
/// <item><c>anna</c> (uid 1002): SFTP only, chroot /home/anna without /etc: no names at all (uid shown).</item>
/// <item><c>shelly</c> (uid 1003): no chroot and exec allowed: names come from <c>getent</c>.</item>
/// </list>
/// The group <c>projekt</c> (gid 1005) exists on the server but not in tester's copy. Removed again at the end.
/// </summary>
public sealed class SftpOpsServer : IAsyncLifetime
{
    public const string Host = "127.0.0.1";

    public string Container { get; } = "dt-sftpops-it-" + Guid.NewGuid().ToString("N")[..8];
    public int Port { get; private set; }
    public string Password { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    public string HostKey { get; private set; } = "";

    public async Task InitializeAsync()
    {
        if (!SftpTestServer.Enabled) return;

        SftpTestServer.Docker("run", "-d", "--name", Container, "-p", $"{Host}::22", "atmoz/sftp:alpine",
            $"tester:{Password}:1001:100:data", $"anna:{Password}:1002:100:data", $"shelly:{Password}:1003:100");
        var mapped = SftpTestServer.Docker("port", Container, "22/tcp").Trim().Split('\n')[0].Trim();
        Port = int.Parse(mapped[(mapped.LastIndexOf(':') + 1)..]);
        await WaitForSsh();

        // Only tester and anna are SFTP-only in a chroot; shelly may run commands. sshd re-reads its config on SIGHUP.
        Sh("""
            set -e
            sed -i 's/^ForceCommand internal-sftp/Match User tester,anna\n    ForceCommand internal-sftp/; s/^ChrootDirectory %h/    ChrootDirectory %h/' /etc/ssh/sshd_config
            grep -q '^Match User tester,anna' /etc/ssh/sshd_config
            addgroup -g 1005 projekt
            mkdir -p /home/tester/etc
            grep -E '^(root|tester|anna):' /etc/passwd > /home/tester/etc/passwd
            grep -E '^(root|users):' /etc/group > /home/tester/etc/group
            chmod 755 /home/tester/etc; chmod 644 /home/tester/etc/passwd /home/tester/etc/group
            usermod -s /bin/sh shelly 2>/dev/null || sed -i 's#^\(shelly:[^:]*:[^:]*:[^:]*:[^:]*:[^:]*:\).*#\1/bin/sh#' /etc/passwd
            kill -HUP 1
            """);
        await Task.Delay(500);
        await WaitForSsh();

        try { SftpProvider.TestConnection(Options("tester", hostKey: null)); }
        catch (SftpHostKeyException e) { HostKey = e.Fingerprint; }
    }

    public Task DisposeAsync()
    {
        if (SftpTestServer.Enabled)
        {
            try { SftpTestServer.Docker("rm", "-f", Container); }
            catch (InvalidOperationException) { /* already gone */ }
        }
        return Task.CompletedTask;
    }

    private async Task WaitForSsh()
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(Host, Port);
                using var stream = tcp.GetStream();
                stream.ReadTimeout = 2000;
                var buf = new byte[64];
                int n = stream.Read(buf, 0, buf.Length);
                if (n > 4 && System.Text.Encoding.ASCII.GetString(buf, 0, n).StartsWith("SSH-", StringComparison.Ordinal)) return;
            }
            catch (Exception ex) when (ex is SocketException or IOException) { }
            if (DateTime.UtcNow > until) throw new TimeoutException("Der SFTP-Container antwortet nicht.");
            await Task.Delay(250);
        }
    }

    public SftpConnectionOptions Options(string user, string? hostKey = "") => new()
    {
        Host = Host,
        Port = Port,
        UserName = user,
        Auth = SftpAuth.Password(Password),
        HostKeySha256 = hostKey == "" ? HostKey : hostKey,
        OperationTimeout = TimeSpan.FromSeconds(20),
    };

    public SftpProvider Provider(string user) => new(Options(user));

    /// <summary>"sftp://user@127.0.0.1:port/…".</summary>
    public string Uri(string user, string remote) => $"sftp://{user}@{Host}:{Port}{remote}";

    public string Sh(string script) => SftpTestServer.Docker("exec", Container, "sh", "-c", script.Replace("\r\n", "\n"));

    /// <summary>A fresh folder below tester's data folder (as root in the container: /home/tester/data/name; over SFTP as tester: /data/name).</summary>
    public string NewFolder(string prefix)
    {
        var name = prefix + "-" + Guid.NewGuid().ToString("N")[..6];
        Sh($"mkdir -p /home/tester/data/{name} && chown 1001:100 /home/tester/data/{name}");
        return name;
    }

    /// <summary>Does the path exist in the container (no link is followed)?</summary>
    public bool ExistsInContainer(string path) => Sh($"if [ -e '{path}' ] || [ -L '{path}' ]; then echo ja; else echo nein; fi").Trim() == "ja";
}
