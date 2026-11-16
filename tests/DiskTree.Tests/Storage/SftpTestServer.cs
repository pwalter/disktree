using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using DiskTree.Features.Storage.Sftp;
using Xunit;

namespace DiskTree.Tests.Storage;

/// <summary>
/// An SFTP integration test: needs Docker with the image atmoz/sftp and is only run when the environment variable
/// DISKTREE_SFTP_TESTS=1 is set (CI has no container: skipped there). Optional: DISKTREE_SFTP_IMAGE (default
/// atmoz/sftp:alpine).
/// </summary>
public sealed class SftpFactAttribute : FactAttribute
{
    public SftpFactAttribute()
    {
        if (!SftpTestServer.Enabled)
            Skip = "SFTP-Integrationstest: DISKTREE_SFTP_TESTS=1 setzen (Docker mit atmoz/sftp nötig).";
    }
}

[CollectionDefinition(Name)]
public sealed class SftpCollection : ICollectionFixture<SftpTestServer>
{
    public const string Name = "SFTP-Server";
}

/// <summary>
/// A throw-away atmoz/sftp container for the SFTP tests: its own name and a random local port, a random password,
/// two Ed25519 keys generated inside the container (one with passphrase; they only live in memory here), a synthetic
/// folder tree below /data (levels, links, an unreadable folder, blanks and umlauts, 5,000 small files) and the expected
/// numbers as the container itself counts them (find/stat). Removed again at the end; nothing is written on the host.
/// </summary>
public sealed class SftpTestServer : IAsyncLifetime
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DISKTREE_SFTP_TESTS") == "1";

    private static string Image => Environment.GetEnvironmentVariable("DISKTREE_SFTP_IMAGE") is { Length: > 0 } i ? i : "atmoz/sftp:alpine";

    public const string User = "tester";
    public const string Host = "127.0.0.1";
    public const string KeyPassphrase = "Schlüssel-Passphrase";

    public string Container { get; } = "disktree-sftp-it-" + Guid.NewGuid().ToString("N")[..8];
    public int Port { get; private set; }
    public string Password { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    public string KeyPlain { get; private set; } = "";
    public string KeyEncrypted { get; private set; } = "";

    /// <summary>Fingerprints of all host keys of the container ("SHA256:…").</summary>
    public IReadOnlyList<string> HostKeys { get; private set; } = [];

    /// <summary>What the container counts below /data, without the content of the unreadable "geheim".</summary>
    public long ExpectedFiles { get; private set; }
    public long ExpectedLinks { get; private set; }
    public long ExpectedBytes { get; private set; }
    public long ExpectedDirs { get; private set; }

    /// <summary>statvfs of "/" as the container's df sees it (1K blocks).</summary>
    public long DfTotalKb { get; private set; }

    public string RootUri => $"sftp://{User}@{Host}:{Port}/";
    public string DataUri => RootUri + "data";

    public SftpConnectionOptions Options(SftpAuth? auth = null, string? hostKey = "", int? port = null, Func<string, bool>? confirm = null,
        int parallel = 8, TimeSpan? operationTimeout = null, TimeSpan? connectTimeout = null) => new()
    {
        Host = Host,
        Port = port ?? Port,
        UserName = User,
        Auth = auth ?? SftpAuth.Password(Password),
        HostKeySha256 = hostKey == "" ? TrustedHostKey : hostKey,
        ConfirmHostKey = confirm,
        MaxParallelListings = parallel,
        OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30),
        ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(15),
    };

    /// <summary>The host key SSH.NET negotiates with this server (one of <see cref="HostKeys"/>).</summary>
    public string TrustedHostKey { get; private set; } = "";

    public async Task InitializeAsync()
    {
        if (!Enabled) return;

        // user:password:uid:gid:dir -> /home/tester (chroot, "/" over SFTP) with a writable /home/tester/data.
        Docker("run", "-d", "--name", Container, "-p", $"{Host}::22", Image, $"{User}:{Password}:1001:1001:data");
        var mapped = Docker("port", Container, "22/tcp").Trim().Split('\n')[0].Trim();
        Port = int.Parse(mapped[(mapped.LastIndexOf(':') + 1)..]);

        await WaitUntilListening();

        // Keys: generated inside the container, the public halves go to authorized_keys, the private ones are read
        // into memory and deleted there.
        Sh($"""
            set -e
            ssh-keygen -q -t ed25519 -N '' -C plain -f /tmp/k1
            ssh-keygen -q -t ed25519 -N '{KeyPassphrase}' -C encrypted -f /tmp/k2
            mkdir -p /home/{User}/.ssh
            cat /tmp/k1.pub /tmp/k2.pub > /home/{User}/.ssh/authorized_keys
            chown -R 1001:1001 /home/{User}/.ssh
            chmod 700 /home/{User}/.ssh
            chmod 600 /home/{User}/.ssh/authorized_keys
            """);
        KeyPlain = Sh("cat /tmp/k1");
        KeyEncrypted = Sh("cat /tmp/k2");
        Sh("rm -f /tmp/k1 /tmp/k1.pub /tmp/k2 /tmp/k2.pub");

        HostKeys = Sh("for f in /etc/ssh/ssh_host_*_key.pub; do ssh-keygen -lf \"$f\"; done")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ')[1])
            .ToList();

        BuildData();
        CountData();

        try
        {
            SftpProvider.TestConnection(Options(hostKey: null));
        }
        catch (SftpHostKeyException e)
        {
            TrustedHostKey = e.Fingerprint;
        }
    }

    public Task DisposeAsync()
    {
        if (Enabled)
        {
            try { Docker("rm", "-f", Container); }
            catch (InvalidOperationException) { /* already gone */ }
        }
        return Task.CompletedTask;
    }

    private async Task WaitUntilListening()
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

    private void BuildData()
    {
        Sh($$"""
            set -e
            D=/home/{{User}}/data
            cd $D
            mkdir -p ebene1/ebene2/ebene3 "Ordner mit Leerzeichen" "Übergröße äöü ß" geheim leer .versteckt CaseTest/a CaseTest/A masse
            head -c 1000 /dev/urandom > ebene1/a.bin
            head -c 2500 /dev/zero > ebene1/ebene2/b.dat
            head -c 123456 /dev/zero > ebene1/ebene2/ebene3/c.iso
            echo hallo > "Ordner mit Leerzeichen/Datei mit Leerzeichen.txt"
            head -c 777 /dev/zero > "Übergröße äöü ß/Größe.txt"
            head -c 3000000 /dev/zero > "Übergröße äöü ß/groß.bin"
            echo x > .versteckt/.env
            echo klein > CaseTest/a/x.txt
            echo GROSSBUCHSTABEN > CaseTest/A/x.txt
            echo alt > ebene1/alt.txt
            touch -d "2020-01-02 03:04:05" ebene1/alt.txt
            ln -s ebene1 link_ordner
            ln -s ebene1/a.bin link_datei
            ln -s /nirgendwo kaputter_link
            head -c 5000 /dev/zero > geheim/secret.bin
            i=0
            while [ $i -lt 100 ]; do
              mkdir masse/d$i
              j=0
              while [ $j -lt 50 ]; do echo "$i-$j" > masse/d$i/f$j.txt; j=$((j+1)); done
              i=$((i+1))
            done
            chown -R 1001:1001 $D
            chmod 000 geheim
            """);
    }

    private void CountData()
    {
        const string where = "cd /home/tester/data && find . -path ./geheim -prune -o";
        ExpectedFiles = long.Parse(Sh($"{where} -type f -print | wc -l").Trim());
        ExpectedLinks = long.Parse(Sh($"{where} -type l -print | wc -l").Trim());
        ExpectedDirs = long.Parse(Sh($"{where} -type d -print | wc -l").Trim());
        ExpectedBytes = long.Parse(Sh($"{where} -type f -exec stat -c %s {{}} + | awk '{{s+=$1}} END {{print s}}'").Trim());
        var df = Sh("df -k /home/tester | tail -1").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        DfTotalKb = long.Parse(df[1]);
    }

    // ------------------------------------------------------------------ docker

    public string Sh(string script) => Docker("exec", Container, "sh", "-c", script.Replace("\r\n", "\n"));

    public void Pause() => Docker("pause", Container);
    public void Unpause() => Docker("unpause", Container);

    /// <summary>Ends all SFTP sessions of the test user on the server side (the client sees a broken connection).</summary>
    public void KillSessions() => Sh($"pkill -f '[s]shd.*{User}' || true");

    public static string Docker(params string[] args)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(180_000)) { p.Kill(); throw new InvalidOperationException("docker hängt: " + args[0]); }
        if (p.ExitCode != 0) throw new InvalidOperationException($"docker {args[0]} fehlgeschlagen: {stderr.Result}");
        return stdout.Result.Replace("\r\n", "\n");
    }
}

/// <summary>
/// A local TCP forwarder that delays every chunk by a fixed time in each direction (simulated network latency without
/// limiting throughput) and can cut all connections at once.
/// </summary>
public sealed class LatencyProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly int _target;
    private readonly TimeSpan _delay;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _open = [];
    private int _connections;

    public LatencyProxy(int targetPort, TimeSpan oneWayDelay)
    {
        _target = targetPort;
        _delay = oneWayDelay;
        _listener.Start();
        _ = AcceptLoop();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Connections accepted so far.</summary>
    public int Connections => Volatile.Read(ref _connections);

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient inbound;
            try { inbound = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            Interlocked.Increment(ref _connections);
            var outbound = new TcpClient { NoDelay = true };
            inbound.NoDelay = true;
            try { await outbound.ConnectAsync(IPAddress.Loopback, _target); }
            catch (Exception) { inbound.Dispose(); outbound.Dispose(); continue; }
            lock (_open) { _open.Add(inbound); _open.Add(outbound); }
            _ = Pump(inbound, outbound);
            _ = Pump(outbound, inbound);
        }
    }

    private async Task Pump(TcpClient from, TcpClient to)
    {
        var queue = Channel.CreateUnbounded<(byte[] Data, long Due)>();
        var writer = Task.Run(async () =>
        {
            try
            {
                var s = to.GetStream();
                await foreach (var (data, due) in queue.Reader.ReadAllAsync(_cts.Token))
                {
                    var wait = due - Stopwatch.GetTimestamp();
                    if (wait > 0) await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), _cts.Token);
                    await s.WriteAsync(data, _cts.Token);
                }
            }
            catch (Exception) { Close(from, to); }
        });
        try
        {
            var s = from.GetStream();
            var buf = new byte[64 * 1024];
            while (true)
            {
                int n = await s.ReadAsync(buf, _cts.Token);
                if (n == 0) break;
                queue.Writer.TryWrite((buf[..n], Stopwatch.GetTimestamp() + (long)(_delay.TotalSeconds * Stopwatch.Frequency)));
            }
        }
        catch (Exception) { }
        queue.Writer.TryComplete();
        await writer;
        Close(from, to);
    }

    private static void Close(TcpClient a, TcpClient b)
    {
        try { a.Client.Shutdown(SocketShutdown.Both); } catch (Exception) { }
        try { b.Client.Shutdown(SocketShutdown.Both); } catch (Exception) { }
        a.Dispose();
        b.Dispose();
    }

    /// <summary>Cuts every open connection (RST), like a dropped network.</summary>
    public void DropAll()
    {
        List<TcpClient> all;
        lock (_open) { all = [.. _open]; _open.Clear(); }
        foreach (var c in all)
        {
            try { c.Client.LingerState = new LingerOption(true, 0); c.Dispose(); } catch (Exception) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        DropAll();
    }
}
