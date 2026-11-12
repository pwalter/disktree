using System.Windows.Media;

namespace DiskTree;

public static class BrushFactory
{
    public static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Brush Frozen(byte r, byte g, byte b) => Frozen(Color.FromRgb(r, g, b));

    /// <summary>Frozen brush from a 0xRRGGBB value.</summary>
    public static Brush Frozen(int rgb) => Frozen(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    /// <summary>Hue 0..360, saturation and value 0..1.</summary>
    public static Color Hsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
