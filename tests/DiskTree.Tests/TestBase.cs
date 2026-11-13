using System.Globalization;
using DiskTree.Core;

namespace DiskTree.Tests;

/// <summary>Each test gets its own temp folder (and a German culture, like the UI).</summary>
public abstract class TestBase : IDisposable
{
    protected TempDir T { get; } = new();
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    protected TestBase()
    {
        // Number formats are culture dependent; the UI is German.
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
    }

    public virtual void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        T.Dispose();
    }

    protected static DateTime Ago(int days = 0, int months = 0, int years = 0) =>
        DateTime.UtcNow.AddDays(-days).AddMonths(-months).AddYears(-years);

    protected Task<Scanner> ScanT(ExcludeRules? excludes = null, AgeStamp stamp = AgeStamp.Modified) => Scan.Run(T.Path, excludes, stamp: stamp);
}
