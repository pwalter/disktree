using System.Windows;

namespace DiskTree;

/// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk): rectangles with a good aspect ratio, area proportional to size.</summary>
public static class TreemapLayout
{
    /// <summary>
    /// Lays out <paramref name="sizes"/> (should be sorted descending) inside <paramref name="bounds"/>.
    /// Returns one rectangle per entry; entries with size &lt;= 0 get an empty rectangle.
    /// The rectangles tile the bounds completely without overlapping.
    /// </summary>
    public static Rect[] Squarify(IReadOnlyList<double> sizes, Rect bounds)
    {
        var result = new Rect[sizes.Count];
        double total = 0;
        foreach (var s in sizes) if (s > 0) total += s;
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        double scale = bounds.Width * bounds.Height / total;
        var rest = bounds;
        int i = 0, n = sizes.Count;

        while (i < n)
        {
            if (sizes[i] <= 0) { i++; continue; }

            // The row is laid along the shorter side of the remaining space.
            bool column = rest.Width >= rest.Height; // true: the row is a column on the left
            double side = column ? rest.Height : rest.Width;

            int start = i;
            double sum = 0, min = double.MaxValue, max = 0, worst = double.MaxValue;
            while (i < n)
            {
                if (sizes[i] <= 0) { i++; continue; }
                double a = sizes[i] * scale;
                double w = Worst(sum + a, Math.Min(min, a), Math.Max(max, a), side);
                if (sum > 0 && w > worst) break;
                worst = w;
                sum += a;
                min = Math.Min(min, a);
                max = Math.Max(max, a);
                i++;
            }

            // The last row takes everything that is left (avoids rounding gaps).
            double thickness = RemainingIsEmpty(sizes, i) ? (column ? rest.Width : rest.Height) : sum / side;

            double pos = column ? rest.Top : rest.Left;
            double end = column ? rest.Bottom : rest.Right;
            int last = LastPositive(sizes, start, i);
            for (int k = start; k < i; k++)
            {
                if (sizes[k] <= 0) continue;
                double len = k == last ? end - pos : sizes[k] * scale / sum * side;
                len = Math.Max(0, len);
                result[k] = column
                    ? new Rect(rest.Left, pos, thickness, len)
                    : new Rect(pos, rest.Top, len, thickness);
                pos += len;
            }

            rest = column
                ? new Rect(rest.Left + thickness, rest.Top, Math.Max(0, rest.Width - thickness), rest.Height)
                : new Rect(rest.Left, rest.Top + thickness, rest.Width, Math.Max(0, rest.Height - thickness));
        }
        return result;
    }

    private static bool RemainingIsEmpty(IReadOnlyList<double> sizes, int from)
    {
        for (int k = from; k < sizes.Count; k++) if (sizes[k] > 0) return false;
        return true;
    }

    private static int LastPositive(IReadOnlyList<double> sizes, int from, int to)
    {
        for (int k = to - 1; k >= from; k--) if (sizes[k] > 0) return k;
        return -1;
    }

    // Worst aspect ratio of a row with total area s, smallest rmin, largest rmax along a side of length w.
    private static double Worst(double s, double rmin, double rmax, double w)
    {
        if (w <= 0 || rmin <= 0) return double.MaxValue;
        double w2 = w * w, s2 = s * s;
        return Math.Max(w2 * rmax / s2, s2 / (w2 * rmin));
    }
}
