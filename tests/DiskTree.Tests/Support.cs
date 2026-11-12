using System.IO;
using DiskTree.Core;
using Xunit;

// The app keeps global state (AgeBuckets reference time, AgeStamps.Current, ...): tests run one after another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DiskTree.Tests;

/// <summary>A fresh folder below %TEMP%, deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "disktree-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Sub(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    /// <summary>Creates a file (and its folders) with <paramref name="size"/> bytes; optional last-write time.</summary>
    public string File(string relative, long size = 0, DateTime? written = null)
    {
        var p = Sub(relative.Split('\\', '/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        using (var fs = new FileStream(p, FileMode.Create))
            fs.SetLength(size);
        if (written != null) System.IO.File.SetLastWriteTimeUtc(p, written.Value);
        return p;
    }

    public string Dir(string relative)
    {
        var p = Sub(relative.Split('\\', '/'));
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}

/// <summary>A test that needs the administrative share \\localhost\C$ (admin rights required); skipped where it is not reachable.</summary>
public sealed class UncFactAttribute : FactAttribute
{
    public UncFactAttribute()
    {
        bool reachable;
        try { reachable = Directory.Exists(@"\\localhost\C$\Windows"); }
        catch { reachable = false; }
        if (!reachable) Skip = @"\\localhost\C$ ist nicht erreichbar (keine Administratorrechte oder Freigabe deaktiviert).";
    }
}

public static class Scan
{
    public static async Task<Scanner> Run(string path, ExcludeRules? excludes = null, int threads = 4)
    {
        var s = new Scanner { Excludes = excludes };
        await s.ScanAsync(path, threads, CancellationToken.None);
        return s;
    }

    public static Node Node(Scanner s, string path) =>
        s.FindNode(path) ?? throw new InvalidOperationException("Knoten nicht gefunden: " + path);
}
