using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DiskTree;

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string AssetName, string AssetUrl, long AssetSize, string? ShaUrl);

/// <summary>
/// Self update from the GitHub releases of this project: looks up the latest release, downloads the matching exe
/// (full or slim, like the running one), checks its SHA-256 against SHA256SUMS.txt, then swaps the files and restarts.
/// A running exe can be renamed on Windows, so the old file becomes DiskTree.exe.old and is deleted at the next start.
/// </summary>
public static class Updater
{
    private const string Repo = "pwalter/disktree";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DiskTree-Updater", "1"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Version of the running build, or null for local/CI builds that carry no release version.</summary>
    public static Version? Current { get; } = ParseVersion(
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string CurrentText => Current?.ToString() ?? "Entwicklung";

    /// <summary>The self-contained build ships the .NET runtime inside; the slim one needs the installed runtime.</summary>
    private static bool IsSelfContained =>
        typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SelfContained")?.Value is not "false";

    private static string AssetNameWanted => IsSelfContained ? "DiskTree.exe" : "DiskTree-slim.exe";

    /// <summary>False when DiskTree runs through "dotnet DiskTree.dll" or similar: there is no own exe to replace.</summary>
    public static bool CanSelfReplace =>
        Environment.ProcessPath is { } p
        && !string.Equals(Path.GetFileNameWithoutExtension(p), "dotnet", StringComparison.OrdinalIgnoreCase);

    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(new[] { '+', '-' }); // 1.2.3+hash, 0.0.0-ci.7
        if (cut >= 0)
        {
            if (text[cut] == '-') return null;          // CI build, no release number
            text = text[..cut];
        }
        return Version.TryParse(text, out var v) && v.Major > 0 ? v : null;
    }

    /// <summary>The newest release, or null if there is none (or it has no usable exe).</summary>
    public static async Task<UpdateInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        if (version == null) return null;

        string? url = null, sha = null;
        long size = 0;
        string wanted = AssetNameWanted;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            string dl = a.GetProperty("browser_download_url").GetString() ?? "";
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) { url = dl; size = a.GetProperty("size").GetInt64(); }
            else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sha = dl;
        }
        if (url == null) return null;

        return new UpdateInfo(version, tag, root.GetProperty("html_url").GetString() ?? ReleasesPage, wanted, url, size, sha);
    }

    public static bool IsNewer(UpdateInfo info) => Current == null || info.Version > Current;

    /// <summary>Downloads the new exe next to the running one (same volume, so the swap is a rename) and verifies it.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Pfad der Programmdatei unbekannt.");
        var dir = Path.GetDirectoryName(exe)!;
        var tmp = Path.Combine(dir, "DiskTree.update.tmp");

        string? expected = null;
        if (info.ShaUrl != null)
        {
            var sums = await Http.GetStringAsync(info.ShaUrl, ct);
            foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && string.Equals(parts[^1].TrimStart('*'), info.AssetName, StringComparison.OrdinalIgnoreCase))
                    expected = parts[0];
            }
        }

        try
        {
            using var resp = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.AssetSize;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    hash.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
                }
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (expected != null && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Die Prüfsumme der heruntergeladenen Datei stimmt nicht. Das Update wurde nicht installiert.");
            if (!LooksLikeExe(tmp))
                throw new InvalidDataException("Die heruntergeladene Datei ist keine Programmdatei und wurde verworfen.");
            return tmp;
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    // Cheap sanity check on top of the checksum: a Windows exe starts with "MZ" (the slim build is only ~0.5 MB).
    private static bool LooksLikeExe(string path)
    {
        using var f = File.OpenRead(path);
        return f.Length > 100_000 && f.ReadByte() == 'M' && f.ReadByte() == 'Z';
    }

    /// <summary>Puts the downloaded file in place of the running exe (which stays as .old until the next start).</summary>
    public static void Install(string downloaded)
    {
        var exe = Environment.ProcessPath!;
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(downloaded, exe);
        }
        catch
        {
            File.Move(old, exe); // put the working version back
            throw;
        }
    }

    public static void Restart(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    /// <summary>Deletes the leftover of the last update. Fails silently while the old process is still shutting down.</summary>
    public static void CleanupOld()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch
        {
        }
    }
}
