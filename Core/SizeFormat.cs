using System.Globalization;

namespace DiskTree.Core;

public static class SizeFormat
{
    /// <summary>Bytes as "1,5 MB" (current culture); below 1 KB as "1.023 B".</summary>
    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0
            ? $"{bytes:N0} B"
            : v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[u];
    }
}
