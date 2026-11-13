// SPIKE (not for merging): measurements for the planned S3 and SFTP providers.
// All targets are local Docker containers; credentials come from environment variables
// (generated randomly by spike/run.ps1, never committed).
//
//   dotnet run -c Release -- s3      <serviceUrl> <bucket> [seed] [parallel]     (env SPIKE_S3_KEY / SPIKE_S3_SECRET)
//   dotnet run -c Release -- s3put   <serviceUrl> <bucket>                       checksum-setting probe
//   dotnet run -c Release -- sftp    <host> <port> <user> <root> [keyfile]       (env SPIKE_SFTP_PW)
//   dotnet run -c Release -- s3cold  <serviceUrl> <bucket>                       assembly-load timing + first request
//   dotnet run -c Release -- sftpcold <host> <port> <user>
//   dotnet run -c Release -- awsprofiles                                          SSO / AssumeRole without extra packages
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Spike;

internal static class Program
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
        {
            var n = e.LoadedAssembly.GetName().Name ?? "";
            if (n.StartsWith("AWSSDK", StringComparison.Ordinal) || n.StartsWith("Renci", StringComparison.Ordinal))
                Console.WriteLine($"  [assembly load] {n} at {Clock.Elapsed.TotalMilliseconds:F0} ms");
        };
        Console.WriteLine($"  [main entered] at {Clock.Elapsed.TotalMilliseconds:F0} ms, SDK assemblies loaded so far: " +
            string.Join(",", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name)
                .Where(n => n!.StartsWith("AWSSDK") || n.StartsWith("Renci")).DefaultIfEmpty("none")));

        switch (args[0])
        {
            case "s3": await S3Bench.Run(args[1], args[2], args.Length > 3 && args[3] == "seed", args.Length > 4 ? int.Parse(args[4]) : 8); break;
            case "s3put": await S3Bench.ChecksumProbe(args[1], args[2]); break;
            case "s3cold": await S3Bench.Cold(args[1], args[2], Clock); break;
            case "sftp": SftpBench.Run(args[1], int.Parse(args[2]), args[3], args[4], args.Length > 5 ? args[5] : null); break;
            case "sftpcold": SftpBench.Cold(args[1], int.Parse(args[2]), args[3], Clock); break;
            case "awsprofiles": AwsProfiles.Run(); break;
            default: Console.WriteLine("unknown mode"); return 2;
        }
        return 0;
    }
}

internal static class S3Bench
{
    public static Amazon.S3.AmazonS3Client Client(string url, bool whenRequired = true, bool pathStyle = true)
    {
        var cfg = new Amazon.S3.AmazonS3Config
        {
            ServiceURL = url,
            ForcePathStyle = pathStyle,
            AuthenticationRegion = "us-east-1",
        };
        if (whenRequired)
        {
            cfg.RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED;
            cfg.ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED;
        }
        return new Amazon.S3.AmazonS3Client(
            new Amazon.Runtime.BasicAWSCredentials(Env("SPIKE_S3_KEY"), Env("SPIKE_S3_SECRET")), cfg);
    }

    private static string Env(string n) => Environment.GetEnvironmentVariable(n) ?? throw new InvalidOperationException(n + " missing");

    // Synthetic layout: d0..d9 / s0..s9 / f00..f29.bin (3000), big/ with 2500 keys (pagination),
    // zero-byte folder markers "dX/" and "dX/sY/", and awkward keys (space, umlauts, "//", trailing "/").
    public static IEnumerable<(string Key, int Size)> Layout()
    {
        var rnd = new Random(42);
        for (int d = 0; d < 10; d++)
        {
            yield return ($"d{d}/", 0);
            for (int s = 0; s < 10; s++)
            {
                yield return ($"d{d}/s{s}/", 0);
                for (int f = 0; f < 30; f++) yield return ($"d{d}/s{s}/f{f:00}.bin", rnd.Next(0, 4096));
            }
        }
        for (int i = 0; i < 2500; i++) yield return ($"big/item{i:0000}.dat", rnd.Next(0, 2048));
        yield return ("mit Leerzeichen/Größe äöü ß.txt", 123);
        yield return ("mit Leerzeichen/Ordner mit Ümlaut/x y.txt", 77);
        yield return ("double//slash.txt", 11);
        yield return ("a//b/c.txt", 13);
        yield return ("/leading-slash.txt", 17);
        yield return ("leer-ordner/", 0);
        yield return ("top-level.txt", 999);
    }

    private static async Task Seed(Amazon.S3.AmazonS3Client c, string bucket)
    {
        try { await c.PutBucketAsync(bucket); } catch (Amazon.S3.AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
        var sw = Stopwatch.StartNew();
        var items = Layout().ToList();
        var failed = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (it, ct) =>
        {
            try
            {
                await c.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
                {
                    BucketName = bucket, Key = it.Key, InputStream = new MemoryStream(new byte[it.Size]), AutoCloseStream = true,
                }, ct);
            }
            catch (Amazon.S3.AmazonS3Exception e) { failed.Add($"'{it.Key}' -> {e.ErrorCode}/{e.Message}"); }
        });
        Console.WriteLine($"seed: {items.Count} objects, {items.Sum(i => (long)i.Size)} bytes in {sw.ElapsedMilliseconds} ms; rejected by server: {failed.Count} {string.Join("; ", failed)}");
    }

    public static async Task Run(string url, string bucket, bool seed, int parallel)
    {
        using var c = Client(url);
        if (seed) await Seed(c, bucket);
        var expected = Layout().ToList();

        for (int round = 0; round < 3; round++)
        {
            // 1) flat, no delimiter
            int req = 0; long sum = 0; int count = 0; string? token = null; var keys = new List<string>();
            var sw = Stopwatch.StartNew();
            do
            {
                var r = await c.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = bucket, ContinuationToken = token });
                req++;
                foreach (var o in r.S3Objects ?? []) { sum += o.Size ?? 0; count++; keys.Add(o.Key); }
                token = r.IsTruncated == true ? r.NextContinuationToken : null;
            } while (token != null);
            Console.WriteLine($"round {round} flat     : {req,4} requests, {count} objects, {sum} bytes, {sw.ElapsedMilliseconds} ms");
            if (round == 0)
            {
                var missing = expected.Select(e => e.Key).Except(keys).ToList();
                var unexpected = keys.Except(expected.Select(e => e.Key)).Where(k => !k.StartsWith("probe/")).ToList();
                Console.WriteLine($"  keys roundtrip ok: {missing.Count == 0} (missing: {string.Join(" | ", missing)}; listed instead: {string.Join(" | ", unexpected)})");
                Console.WriteLine($"  sample keys: {string.Join(" | ", keys.Where(k => k.Contains('ö') || k.Contains("//") || k.StartsWith('/')))}");
            }

            // 2) recursive with delimiter "/", sequential
            var (rq, cnt, s, prefixes, ms) = await Delimited(c, bucket, 1);
            Console.WriteLine($"round {round} delim seq: {rq,4} requests, {cnt} objects, {s} bytes, {prefixes} prefixes, {ms} ms, sum equal: {s == sum}");
            // 3) recursive with delimiter, N prefixes in parallel
            (rq, cnt, s, prefixes, ms) = await Delimited(c, bucket, parallel);
            Console.WriteLine($"round {round} delim p{parallel}: {rq,4} requests, {cnt} objects, {s} bytes, {prefixes} prefixes, {ms} ms, sum equal: {s == sum}");
        }
    }

    private static async Task<(int Req, int Count, long Sum, int Prefixes, long Ms)> Delimited(Amazon.S3.AmazonS3Client c, string bucket, int parallel)
    {
        int req = 0, count = 0, prefixes = 0; long sum = 0;
        var sw = Stopwatch.StartNew();
        var gate = new SemaphoreSlim(parallel);
        async Task Walk(string prefix)
        {
            var children = new List<string>();
            await gate.WaitAsync();
            try
            {
                string? token = null;
                do
                {
                    var r = await c.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request
                    { BucketName = bucket, Prefix = prefix, Delimiter = "/", ContinuationToken = token });
                    Interlocked.Increment(ref req);
                    foreach (var o in r.S3Objects ?? []) { Interlocked.Add(ref sum, o.Size ?? 0); Interlocked.Increment(ref count); }
                    children.AddRange(r.CommonPrefixes ?? []);
                    token = r.IsTruncated == true ? r.NextContinuationToken : null;
                } while (token != null);
            }
            finally { gate.Release(); }
            Interlocked.Add(ref prefixes, children.Count);
            await Task.WhenAll(children.Select(Walk));
        }
        await Walk("");
        return (req, count, sum, prefixes, sw.ElapsedMilliseconds);
    }

    public static async Task ChecksumProbe(string url, string bucket)
    {
        using (var c0 = Client(url))
        {
            try { await c0.PutBucketAsync(bucket); } catch (Amazon.S3.AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
        }
        foreach (var whenRequired in new[] { false, true })
        foreach (var pathStyle in new[] { true, false })
        {
            using var c = Client(url, whenRequired, pathStyle);
            var label = $"checksum={(whenRequired ? "WHEN_REQUIRED" : "default(WHEN_SUPPORTED)")}, ForcePathStyle={pathStyle}";
            try
            {
                await c.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
                { BucketName = bucket, Key = "probe/put.txt", ContentBody = "hello checksum" });
                var g = await c.GetObjectAsync(bucket, "probe/put.txt");
                using var sr = new StreamReader(g.ResponseStream);
                var body = await sr.ReadToEndAsync();
                var l = await c.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = bucket, Prefix = "probe/" });
                Console.WriteLine($"{label}: PUT+GET+LIST ok ({body.Length} bytes)");
            }
            catch (Exception e)
            {
                Console.WriteLine($"{label}: FAILED {e.GetType().Name}: {e.Message.Split('\n')[0]}");
            }
        }
    }

    public static async Task Cold(string url, string bucket, Stopwatch clock)
    {
        Console.WriteLine($"  [before first S3 use] {clock.Elapsed.TotalMilliseconds:F0} ms");
        await ColdInner(url, bucket, clock);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ColdInner(string url, string bucket, Stopwatch clock)
    {
        var t0 = clock.Elapsed;
        using var c = Client(url);
        var t1 = clock.Elapsed;
        await c.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = bucket, Prefix = "d0/", Delimiter = "/" });
        var t2 = clock.Elapsed;
        await c.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = bucket, Prefix = "d1/", Delimiter = "/" });
        var t3 = clock.Elapsed;
        Console.WriteLine($"  client create {(t1 - t0).TotalMilliseconds:F0} ms, first request {(t2 - t1).TotalMilliseconds:F0} ms, second request {(t3 - t2).TotalMilliseconds:F1} ms");
    }
}

internal static class SftpBench
{
    private static Renci.SshNet.ConnectionInfo Conn(string host, int port, string user, string? keyFile)
    {
        var methods = new List<Renci.SshNet.AuthenticationMethod>();
        if (keyFile != null) methods.Add(new Renci.SshNet.PrivateKeyAuthenticationMethod(user, new Renci.SshNet.PrivateKeyFile(keyFile)));
        else methods.Add(new Renci.SshNet.PasswordAuthenticationMethod(user, Environment.GetEnvironmentVariable("SPIKE_SFTP_PW")));
        return new Renci.SshNet.ConnectionInfo(host, port, user, methods.ToArray());
    }

    private static Renci.SshNet.SftpClient Connect(string host, int port, string user, string? keyFile, bool print = false)
    {
        var c = new Renci.SshNet.SftpClient(Conn(host, port, user, keyFile));
        c.HostKeyReceived += (_, e) =>
        {
            if (print) Console.WriteLine($"  host key: {e.HostKeyName}, {e.KeyLength} bit, SHA256:{e.FingerPrintSHA256}");
            e.CanTrust = true; // a real provider compares with a stored fingerprint (TOFU) here
        };
        c.Connect();
        return c;
    }

    private static string Perm(Renci.SshNet.Sftp.ISftpFile f) => string.Concat(
        f.OwnerCanRead ? "r" : "-", f.OwnerCanWrite ? "w" : "-", f.OwnerCanExecute ? "x" : "-",
        f.GroupCanRead ? "r" : "-", f.GroupCanWrite ? "w" : "-", f.GroupCanExecute ? "x" : "-",
        f.OthersCanRead ? "r" : "-", f.OthersCanWrite ? "w" : "-", f.OthersCanExecute ? "x" : "-");

    public static void Run(string host, int port, string user, string root, string? keyFile)
    {
        var sw = Stopwatch.StartNew();
        using var c = Connect(host, port, user, keyFile, print: true);
        Console.WriteLine($"connect+auth ({(keyFile == null ? "password" : Path.GetFileName(keyFile))}): {sw.ElapsedMilliseconds} ms, server: {c.ConnectionInfo.ServerVersion}");
        Console.WriteLine($"  kex {c.ConnectionInfo.CurrentKeyExchangeAlgorithm}, cipher {c.ConnectionInfo.CurrentServerEncryption}, hostkey {c.ConnectionInfo.CurrentHostKeyAlgorithm}");
        if (keyFile != null) return; // key login only checks authentication

        // metadata sample
        foreach (var f in c.ListDirectory(root + "/meta"))
        {
            if (f.Name is "." or "..") continue;
            Console.WriteLine($"  meta {f.Name,-14} dir={f.IsDirectory,-5} link={f.IsSymbolicLink,-5} reg={f.IsRegularFile,-5} len={f.Length,-6} mtime={f.LastWriteTimeUtc:O} atime={f.LastAccessTimeUtc:O} uid={f.UserId} gid={f.GroupId} perm={Perm(f)}");
        }
        try
        {
            var link = c.Get(root + "/meta/link-to-file");
            Console.WriteLine($"  Get(link) (stat follows link): link={link.IsSymbolicLink} len={link.Length}; lstat via ListDirectory shows link=True");
        }
        catch (Exception e) { Console.WriteLine($"  Get(link): {e.Message}"); }

        try
        {
            var st = c.GetStatus(root);
            Console.WriteLine($"  statvfs@openssh.com: block={st.BlockSize} total={st.TotalBlocks} free={st.FreeBlocks} avail={st.AvailableBlocks} => total {st.TotalBlocks * st.BlockSize / 1e9:F1} GB, avail {st.AvailableBlocks * st.BlockSize / 1e9:F1} GB");
        }
        catch (Exception e) { Console.WriteLine($"  statvfs: FAILED {e.Message}"); }

        for (int round = 0; round < 3; round++)
        {
            // one client, sequential recursion
            sw.Restart();
            var (dirs, files, bytes, rt) = WalkSeq(c, root + "/tree");
            Console.WriteLine($"round {round} 1 client seq   : {dirs} dirs, {files} files, {bytes} bytes, ~{rt} SFTP requests, {sw.ElapsedMilliseconds} ms");

            // one client, several directories in flight at once (SFTP request pipelining on one channel)
            foreach (var p in new[] { 4, 8 })
            {
                sw.Restart();
                var r = WalkParallel(_ => c, p, root + "/tree");
                Console.WriteLine($"round {round} 1 client p{p}    : {r.Dirs} dirs, {r.Files} files, {r.Bytes} bytes, {sw.ElapsedMilliseconds} ms");
            }

            // several clients (separate SSH connections)
            foreach (var n in new[] { 4 })
            {
                var pool = Enumerable.Range(0, n).Select(_ => Connect(host, port, user, keyFile)).ToArray();
                sw.Restart();
                var r = WalkParallel(i => pool[i % n], n * 2, root + "/tree");
                Console.WriteLine($"round {round} {n} clients p{n * 2}  : {r.Dirs} dirs, {r.Files} files, {r.Bytes} bytes, {sw.ElapsedMilliseconds} ms (connect not included)");
                foreach (var x in pool) x.Dispose();
            }
        }
    }

    // OpenSSH sftp-server answers READDIR with at most 100 names per reply:
    // requests per directory = OPENDIR + ceil(n/100) READDIR + 1 READDIR (EOF) + CLOSE.
    private static int Requests(int entries) => 1 + (entries + 99) / 100 + 1 + 1;

    private static (int Dirs, int Files, long Bytes, int Req) WalkSeq(Renci.SshNet.SftpClient c, string path)
    {
        int dirs = 1, files = 0, req = 0; long bytes = 0;
        var list = c.ListDirectory(path).ToList();
        req += Requests(list.Count);
        foreach (var f in list)
        {
            if (f.Name is "." or "..") continue;
            if (f.IsDirectory) { var r = WalkSeq(c, f.FullName); dirs += r.Dirs; files += r.Files; bytes += r.Bytes; req += r.Req; }
            else if (f.IsRegularFile) { files++; bytes += f.Length; }
        }
        return (dirs, files, bytes, req);
    }

    private static (int Dirs, int Files, long Bytes) WalkParallel(Func<int, Renci.SshNet.SftpClient> pick, int parallel, string root)
    {
        int dirs = 0, files = 0, pending = 1, worker = 0; long bytes = 0;
        var queue = new BlockingCollection<string> { root };
        var threads = Enumerable.Range(0, parallel).Select(_ => new Thread(() =>
        {
            var c = pick(Interlocked.Increment(ref worker));
            foreach (var dir in queue.GetConsumingEnumerable())
            {
                Interlocked.Increment(ref dirs);
                foreach (var f in c.ListDirectory(dir))
                {
                    if (f.Name is "." or "..") continue;
                    if (f.IsDirectory) { Interlocked.Increment(ref pending); queue.Add(f.FullName); }
                    else if (f.IsRegularFile) { Interlocked.Increment(ref files); Interlocked.Add(ref bytes, f.Length); }
                }
                if (Interlocked.Decrement(ref pending) == 0) queue.CompleteAdding();
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        return (dirs, files, bytes);
    }

    public static void Cold(string host, int port, string user, Stopwatch clock)
    {
        Console.WriteLine($"  [before first SFTP use] {clock.Elapsed.TotalMilliseconds:F0} ms");
        ColdInner(host, port, user, clock);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ColdInner(string host, int port, string user, Stopwatch clock)
    {
        var t0 = clock.Elapsed;
        using var c = Connect(host, port, user, null);
        var t1 = clock.Elapsed;
        c.ListDirectory("/data/tree/d0").ToList();
        var t2 = clock.Elapsed;
        c.ListDirectory("/data/tree/d1").ToList();
        var t3 = clock.Elapsed;
        using var c2 = Connect(host, port, user, null);
        var t4 = clock.Elapsed;
        Console.WriteLine($"  connect+auth {(t1 - t0).TotalMilliseconds:F0} ms, first ListDirectory {(t2 - t1).TotalMilliseconds:F0} ms, second {(t3 - t2).TotalMilliseconds:F1} ms, second connection (warm) {(t4 - t3).TotalMilliseconds:F0} ms");
    }
}

internal static class AwsProfiles
{
    // Writes a throw-away AWS config with an SSO profile and an AssumeRole profile and resolves them
    // with only AWSSDK.S3 (+Core) referenced. Nothing goes over the network: the SDK fails while
    // looking for the extra assemblies before it would contact AWS.
    public static void Run()
    {
        var dir = Directory.CreateTempSubdirectory("spike-aws-");
        var cfg = Path.Combine(dir.FullName, "config");
        var cred = Path.Combine(dir.FullName, "credentials");
        File.WriteAllText(cfg, """
            [profile sso-test]
            sso_session = s
            sso_account_id = 111111111111
            sso_role_name = ReadOnly
            region = eu-central-1
            [sso-session s]
            sso_start_url = https://example.invalid/start
            sso_region = eu-central-1
            [profile role-test]
            role_arn = arn:aws:iam::111111111111:role/ReadOnly
            source_profile = base
            region = eu-central-1
            """);
        File.WriteAllText(cred, "[base]\naws_access_key_id = AKIAEXAMPLEEXAMPLE00\naws_secret_access_key = notarealsecretnotarealsecretnotarealsecr\n");
        try
        {
            var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain(cred);
            Environment.SetEnvironmentVariable("AWS_CONFIG_FILE", cfg);
            Environment.SetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE", cred);
            chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
            foreach (var name in new[] { "sso-test", "role-test" })
            {
                try
                {
                    if (!chain.TryGetProfile(name, out var p)) { Console.WriteLine($"{name}: profile not found"); continue; }
                    Console.WriteLine($"{name}: sso_account_id={p.Options.SsoAccountId} role_arn={p.Options.RoleArn}");
                    var creds = Amazon.Runtime.CredentialManagement.AWSCredentialsFactory.GetAWSCredentials(p, chain);
                    Console.WriteLine($"  credentials object: {creds.GetType().Name}");
                    creds.GetCredentials();
                    Console.WriteLine("  GetCredentials returned (unexpected)");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"  {e.GetType().Name}: {e.Message.Split('\n')[0]}");
                    if (e.InnerException != null) Console.WriteLine($"    inner {e.InnerException.GetType().Name}: {e.InnerException.Message.Split('\n')[0]}");
                }
            }
        }
        finally { dir.Delete(true); }
    }
}
