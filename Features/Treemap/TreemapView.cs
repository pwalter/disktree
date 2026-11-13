using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskTree.Core;
using DiskTree.Ui;
using DiskTree.Infrastructure;

namespace DiskTree.Features.Treemap;

/// <summary>
/// Treemap of one folder: its subfolders and its files block as rectangles (area ~ size), one level of
/// subfolders nested inside. Drawn directly like <see cref="PieChart"/>. Click selects, double click dives in.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    private const double HeaderHeight = 17;
    private const int TopLimit = 100, InnerLimit = 40;

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Color Accent = Color.FromRgb(0x2F, 0x6F, 0xED);

    private TreemapItem? _model;
    private readonly List<TreemapItem> _hits = new(); // drawn rectangles, deepest last
    private Size _laidOut;
    private bool _dirty = true;
    private TreemapItem? _hover;
    private int _version;
    private bool _wasVisible;

    public Node? FocusNode { get; private set; }
    public Node? SelectedNode { get; private set; }

    /// <summary>A rectangle was clicked: select this folder in the tree.</summary>
    public event Action<Node>? NodeClicked;
    /// <summary>The folder shown changed (dive in, up, or the tree selection moved elsewhere).</summary>
    public event Action? FocusChanged;

    public TreemapView()
    {
        ToolTipService.SetInitialShowDelay(this, 0);
        ClipToBounds = true;
        // Static event: subscribe while loaded only (a tab template loads and unloads the control again and again;
        // -= first so that a repeated Loaded never subscribes twice).
        Loaded += (_, _) => { Theme.Changed -= InvalidateVisual; Theme.Changed += InvalidateVisual; InvalidateVisual(); };
        Unloaded += (_, _) => Theme.Changed -= InvalidateVisual;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && !_wasVisible) RefreshAsync().Forget("RefreshAsync");
            _wasVisible = IsVisible;
        };
    }

    /// <summary>A new scan started (or the scan was cleared): start at the given folder again.</summary>
    public void Reset(Node? root)
    {
        FocusNode = root;
        SelectedNode = null;
        _model = null;
        _dirty = true;
        FocusChanged?.Invoke();
        InvalidateVisual();
        RefreshAsync().Forget("RefreshAsync");
    }

    public void SetFocus(Node? node)
    {
        if (node == FocusNode) return;
        FocusNode = node;
        FocusChanged?.Invoke();
        RefreshAsync().Forget("RefreshAsync");
    }

    /// <summary>The selection in the tree moved. Inside the shown folder it is only highlighted, elsewhere the view follows.</summary>
    public void SetTreeSelection(Node? node)
    {
        SelectedNode = node;
        if (node != null && (FocusNode == null || !IsWithin(node, FocusNode)))
            SetFocus(node);
        InvalidateVisual();
    }

    private static bool IsWithin(Node node, Node ancestor)
    {
        for (var n = node; n != null; n = n.Parent)
            if (n == ancestor) return true;
        return false;
    }

    /// <summary>Re-reads the numbers from the scanner; the model is built off the UI thread.</summary>
    public async Task RefreshAsync()
    {
        var focus = FocusNode;
        if (focus == null || !IsVisible) { InvalidateVisual(); return; }

        int version = ++_version;
        var model = await Task.Run(() => BuildModel(focus));
        if (version != _version || focus != FocusNode) return;

        _model = model;
        _hover = null;
        ToolTip = null;
        _dirty = true;
        InvalidateVisual();
    }

    private static TreemapItem BuildModel(Node focus)
    {
        var root = new TreemapItem { Name = focus.Name, Size = focus.Size, Files = focus.FileCount, Node = focus };
        root.Kids = BuildKids(focus, 2, TopLimit, 0);
        return root;
    }

    private static List<TreemapItem> BuildKids(Node n, int levels, int limit, int depth)
    {
        var list = new List<TreemapItem>();
        foreach (var c in n.Snapshot())
        {
            long size = c.Size;
            if (size <= 0) continue;
            var item = new TreemapItem { Name = c.Name, Size = size, Files = c.FileCount, Node = c, Depth = depth };
            if (levels > 1) item.Kids = BuildKids(c, levels - 1, InnerLimit, depth + 1);
            list.Add(item);
        }
        if (n.OwnSize > 0)
            list.Add(new TreemapItem { Name = "Dateien", Size = n.OwnSize, Files = n.OwnCount, Node = n, IsFiles = true, Depth = depth });

        list.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (list.Count > limit)
        {
            var rest = list.GetRange(limit - 1, list.Count - limit + 1);
            list.RemoveRange(limit - 1, list.Count - limit + 1);
            list.Add(new TreemapItem
            {
                Name = $"Weitere ({rest.Count:N0})", Size = rest.Sum(r => r.Size), Files = rest.Sum(r => r.Files), Depth = depth,
            });
        }
        return list;
    }

    // ---------------------------------------------------------------- layout

    private void LayoutAll()
    {
        _hits.Clear();
        _laidOut = new Size(ActualWidth, ActualHeight);
        _dirty = false;
        if (_model == null || ActualWidth < 4 || ActualHeight < 4) return;

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        Place(_model.Kids, bounds);
        foreach (var top in _model.Kids)
        {
            _hits.Add(top);
            if (top.Kids.Count == 0) continue;

            top.Header = top.Rect.Width >= 60 && top.Rect.Height >= HeaderHeight * 2;
            var inner = Shrink(top.Rect, 2, top.Header ? HeaderHeight : 2);
            if (inner.Width < 6 || inner.Height < 6) continue;
            Place(top.Kids, inner);
            _hits.AddRange(top.Kids.Where(k => k.Rect.Width > 0 && k.Rect.Height > 0));
        }
    }

    private static void Place(List<TreemapItem> items, Rect bounds)
    {
        var rects = TreemapLayout.Squarify(items.Select(i => (double)i.Size).ToList(), bounds);
        for (int i = 0; i < items.Count; i++) items[i].Rect = rects[i];
    }

    private static Rect Shrink(Rect r, double pad, double top) =>
        new(r.Left + pad, r.Top + top, Math.Max(0, r.Width - 2 * pad), Math.Max(0, r.Height - top - pad));

    // ---------------------------------------------------------------- drawing

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable

        if (_model == null || _model.Kids.Count == 0)
        {
            string msg = FocusNode == null ? "Noch nichts gescannt." : _model == null ? "" : "Keine Dateien in diesem Ordner.";
            if (msg.Length > 0) DrawText(dc, msg, FaceBold, 13, Theme.Muted, new Rect(0, h / 2 - 10, w, 24), true);
            return;
        }

        if (_dirty || _laidOut.Width != w || _laidOut.Height != h) LayoutAll();

        var gap = new Pen(new SolidColorBrush(Theme.PanelBg), 1);
        bool dark = Theme.IsDark;
        var ink = Theme.Ink;

        foreach (var top in _model.Kids)
        {
            if (top.Rect.Width <= 0 || top.Rect.Height <= 0) continue;
            dc.DrawRectangle(new SolidColorBrush(Fill(top, 0)), gap, top.Rect);

            if (top.Header)
                DrawText(dc, $"{top.Name}  ·  {SizeFormat.Format(top.Size)}", FaceBold, 11.5, ink,
                    new Rect(top.Rect.Left + 5, top.Rect.Top + 1.5, top.Rect.Width - 8, HeaderHeight));
            else if (top.Kids.Count == 0 && top.Rect.Width >= 56 && top.Rect.Height >= 20)
                DrawLabel(dc, top, ink);

            foreach (var kid in top.Kids)
            {
                if (kid.Rect.Width <= 0 || kid.Rect.Height <= 0) continue;
                dc.DrawRectangle(new SolidColorBrush(Fill(kid, 1)), gap, kid.Rect);
                if (kid.Rect.Width >= 56 && kid.Rect.Height >= 20) DrawLabel(dc, kid, ink);
            }
        }

        // Outlines last so neighbours do not cover them.
        if (SelectedNode != null)
        {
            var sel = _hits.LastOrDefault(i => i.Node == SelectedNode && !i.IsFiles);
            if (sel != null) dc.DrawRectangle(null, new Pen(new SolidColorBrush(Accent), 2.5), Inflate(sel.Rect, -1.25));
        }
        if (_hover != null)
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(dark ? Colors.White : Colors.Black), 1.5), Inflate(_hover.Rect, -0.75));
    }

    private static Rect Inflate(Rect r, double d) =>
        new(r.Left - d, r.Top - d, Math.Max(0, r.Width + 2 * d), Math.Max(0, r.Height + 2 * d));

    private void DrawLabel(DrawingContext dc, TreemapItem item, Color ink)
    {
        var r = item.Rect;
        var box = new Rect(r.Left + 4, r.Top + 2, r.Width - 7, r.Height - 3);
        DrawText(dc, item.Name, Face, 11.5, ink, new Rect(box.Left, box.Top, box.Width, 16));
        if (r.Height >= 36)
            DrawText(dc, SizeFormat.Format(item.Size), Face, 10.5, Theme.Muted, new Rect(box.Left, box.Top + 15, box.Width, 15));
    }

    private void DrawText(DrawingContext dc, string text, Typeface face, double size, Color color, Rect box, bool center = false)
    {
        if (box.Width < 10 || box.Height < 8) return;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size,
            new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = box.Width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
        };
        dc.PushClip(new RectangleGeometry(box));
        dc.DrawText(ft, new Point(box.Left, box.Top));
        dc.Pop();
    }

    /// <summary>Calm, readable fills: one hue per folder name, folders lighter at the second level, files and remainder grey.</summary>
    private static Color Fill(TreemapItem item, int level)
    {
        bool dark = Theme.IsDark;
        if (item.IsFiles || item.Node == null)
        {
            double g = item.Node == null ? (dark ? 0.30 : 0.80) : (dark ? 0.36 : 0.86);
            if (level == 1) g += dark ? 0.04 : 0.05;
            return BrushFactory.Hsv(215, 0.08, g);
        }

        uint hash = 2166136261;
        foreach (char c in item.Name) hash = (hash ^ char.ToLowerInvariant(c)) * 16777619;
        double hue = hash % 360;

        return level == 0
            ? BrushFactory.Hsv(hue, dark ? 0.38 : 0.30, dark ? 0.40 : 0.86)
            : BrushFactory.Hsv(hue, dark ? 0.36 : 0.24, dark ? 0.52 : 0.94);
    }

    // ---------------------------------------------------------------- mouse

    private TreemapItem? HitTest(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Rect.Contains(p)) return _hits[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit == _hover) return;
        _hover = hit;
        ToolTip = hit == null ? null : TooltipText(hit);
        Cursor = hit?.Node != null ? Cursors.Hand : Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == null) return;
        _hover = null;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit?.Node == null) return;

        NodeClicked?.Invoke(hit.Node);
        if (e.ClickCount >= 2 && !hit.IsFiles && hit.Node != FocusNode)
            SetFocus(hit.Node);
        e.Handled = true;
    }

    private string TooltipText(TreemapItem item)
    {
        double total = FocusNode?.Size ?? 0;
        string pct = total > 0 ? (item.Size / total).ToString("P1") : "–";
        string name = item.IsFiles ? $"Dateien in {item.Node!.Name}" : item.Name;
        string path = item.Node != null && !item.IsFiles ? "\n" + item.Node.FullPath : "";
        return $"{name}{path}\n{SizeFormat.Format(item.Size)} · {pct} von {FocusNode?.Name}\n{item.Files:N0} Dateien";
    }
}
