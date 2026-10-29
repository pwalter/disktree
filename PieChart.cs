using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DiskTree;

public sealed record PieSlice(string Label, double Value, Brush Brush, string Detail);

/// <summary>Donut chart drawn directly; the hovered slice grows a little and shows a tooltip.</summary>
public sealed class PieChart : FrameworkElement
{
    private IReadOnlyList<PieSlice> _slices = Array.Empty<PieSlice>();
    private string _centerTop = "", _centerBottom = "";
    private int _hover = -1;

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Pen Separator = Frozen(new Pen(Brushes.White, 1.5));

    public PieChart()
    {
        ToolTipService.SetInitialShowDelay(this, 0);
    }

    public void SetData(IReadOnlyList<PieSlice> slices, string centerTop, string centerBottom)
    {
        _slices = slices;
        _centerTop = centerTop;
        _centerBottom = centerBottom;
        _hover = -1;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var center = new Point(w / 2, h / 2);
        double radius = Math.Min(w, h) / 2 - 8;
        if (radius < 20) return;
        double inner = radius * 0.6;

        double total = _slices.Sum(s => s.Value);
        if (total <= 0)
        {
            var empty = new Pen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEC)), radius - inner);
            dc.DrawEllipse(null, empty, center, (radius + inner) / 2, (radius + inner) / 2);
            DrawCenter(dc, center, "–", "");
            return;
        }

        double start = -90;
        for (int i = 0; i < _slices.Count; i++)
        {
            double sweep = _slices[i].Value / total * 360;
            if (sweep <= 0) continue;
            double r = i == _hover ? radius + 6 : radius;
            dc.DrawGeometry(_slices[i].Brush, Separator, Ring(center, inner, r, start, Math.Min(sweep, 359.99)));
            start += sweep;
        }

        DrawCenter(dc, center, _centerTop, _centerBottom);
    }

    private void DrawCenter(DrawingContext dc, Point center, string top, string bottom)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var big = new FormattedText(top, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 22,
            new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x29)), dpi) { MaxTextWidth = 150, TextAlignment = TextAlignment.Center };
        var small = new FormattedText(bottom, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12,
            new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x7B)), dpi) { MaxTextWidth = 150, TextAlignment = TextAlignment.Center };

        double blockHeight = big.Height + (bottom.Length > 0 ? small.Height : 0);
        double y = center.Y - blockHeight / 2;
        dc.DrawText(big, new Point(center.X - 75, y));
        if (bottom.Length > 0)
            dc.DrawText(small, new Point(center.X - 75, y + big.Height));
    }

    private static Geometry Ring(Point c, double inner, double outer, double startDeg, double sweepDeg)
    {
        var a0 = startDeg * Math.PI / 180;
        var a1 = (startDeg + sweepDeg) * Math.PI / 180;
        bool large = sweepDeg > 180;

        Point P(double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(outer, a0), true, true);
            ctx.ArcTo(P(outer, a1), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
            ctx.LineTo(P(inner, a1), true, false);
            ctx.ArcTo(P(inner, a0), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        double total = _slices.Sum(s => s.Value);
        int hit = -1;
        if (total > 0)
        {
            var p = e.GetPosition(this);
            double dx = p.X - ActualWidth / 2, dy = p.Y - ActualHeight / 2;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double radius = Math.Min(ActualWidth, ActualHeight) / 2 - 8;

            if (dist >= radius * 0.6 && dist <= radius + 6)
            {
                // 0° = top, clockwise, like the drawing.
                double angle = Math.Atan2(dy, dx) * 180 / Math.PI + 90;
                if (angle < 0) angle += 360;

                double acc = 0;
                for (int i = 0; i < _slices.Count; i++)
                {
                    acc += _slices[i].Value / total * 360;
                    if (angle <= acc) { hit = i; break; }
                }
            }
        }

        if (hit == _hover) return;
        _hover = hit;
        ToolTip = hit < 0 ? null : $"{_slices[hit].Label}\n{_slices[hit].Detail}";
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        ToolTip = null;
        InvalidateVisual();
    }

    private static Pen Frozen(Pen p)
    {
        p.Freeze();
        return p;
    }
}
