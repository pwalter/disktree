using System.Globalization;

namespace DiskTree.Tests;

/// <summary>Each test gets its own temp folder and starts with the app's global settings at their defaults.</summary>
public abstract class TestBase : IDisposable
{
    protected TempDir T { get; } = new();
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    protected TestBase()
    {
        AgeStamps.Preferred = AgeStamp.Modified;
        AgeStamps.Current = AgeStamp.Modified;
        AgeBuckets.ThresholdMonths = 24;
        AgeBuckets.SetReference(DateTime.UtcNow);
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

    protected Task<Scanner> ScanT(ExcludeRules? excludes = null) => Scan.Run(T.Path, excludes);
}
