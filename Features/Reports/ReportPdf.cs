using System.Globalization;
using System.IO;
using DiskTree.Core;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace DiskTree.Features.Reports;

/// <summary>Draws the space report straight into a PDF (PDFsharp, no browser or printer driver involved).</summary>
public static class ReportPdf
{
    private static readonly XColor Ink = XColor.FromArgb(31, 35, 40);
    private static readonly XColor Grey = XColor.FromArgb(107, 114, 128);
    private static readonly XColor Line = XColor.FromArgb(209, 213, 219);
    private static readonly XColor Track = XColor.FromArgb(229, 231, 235);
    private static readonly XColor Blue = XColor.FromArgb(59, 130, 196);
    private static readonly XColor[] AgeColors =
    {
        XColor.FromArgb(76, 159, 112), XColor.FromArgb(141, 190, 91), XColor.FromArgb(232, 197, 71),
        XColor.FromArgb(229, 143, 58), XColor.FromArgb(200, 70, 59),
    };

    private const double PageW = 595.28, PageH = 841.89, M = 42, Bottom = PageH - 48;
    private const string FontName = "Arial";

    private sealed class Ctx
    {
        public required PdfDocument Doc;
        public required ReportData Data;
        public PdfPage Page = null!;
        public XGraphics G = null!;
        public double Y;
        public XFont Body = null!, Bold = null!, Small = null!, H1 = null!, H2 = null!, Big = null!;

        public void NewPage()
        {
            G?.Dispose();
            Page = Doc.AddPage();
            Page.Width = XUnit.FromPoint(PageW);
            Page.Height = XUnit.FromPoint(PageH);
            G = XGraphics.FromPdfPage(Page);
            Y = M;
        }

        public void Need(double h)
        {
            if (Y + h > Bottom) NewPage();
        }
    }

    public static void Write(string file, ReportData d)
    {
        if (GlobalFontSettings.FontResolver == null)
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;

        var doc = new PdfDocument();
        doc.Info.Title = "DiskTree-Bericht – " + d.Title;
        doc.Info.Creator = "DiskTree";
        var c = new Ctx { Doc = doc, Data = d };
        c.Body = new XFont(FontName, 9.5, XFontStyleEx.Regular);
        c.Bold = new XFont(FontName, 9.5, XFontStyleEx.Bold);
        c.Small = new XFont(FontName, 8.5, XFontStyleEx.Regular);
        c.H1 = new XFont(FontName, 22, XFontStyleEx.Bold);
        c.H2 = new XFont(FontName, 13, XFontStyleEx.Bold);
        c.Big = new XFont(FontName, 13, XFontStyleEx.Bold);
        c.NewPage();

        Header(c);
        DriveBlock(c);
        Folders(c);
        Types(c);
        Ages(c);
        TopFiles(c);
        c.G.Dispose();

        // Page numbers once the page count is known.
        int n = doc.PageCount;
        for (int i = 0; i < n; i++)
        {
            using var g = XGraphics.FromPdfPage(doc.Pages[i]);
            g.DrawString($"DiskTree-Bericht · {d.Created:dd.MM.yyyy HH:mm}", c.Small, Brush(Grey), M, PageH - 30, XStringFormats.TopLeft);
            g.DrawString($"Seite {i + 1} von {n}", c.Small, Brush(Grey), PageW - M, PageH - 30, XStringFormats.TopRight);
        }

        doc.Save(file);
    }

    private static XSolidBrush Brush(XColor c) => new(c);
    private static XColor FromRgb(uint rgb) => XColor.FromArgb((int)(rgb >> 16 & 0xFF), (int)(rgb >> 8 & 0xFF), (int)(rgb & 0xFF));

    private static string Fit(XGraphics g, string s, XFont f, double width, bool keepEnd = false)
    {
        if (g.MeasureString(s, f).Width <= width) return s;
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            string t = keepEnd ? "…" + s[^mid..] : s[..mid] + "…";
            if (g.MeasureString(t, f).Width <= width) lo = mid; else hi = mid - 1;
        }
        return keepEnd ? "…" + s[^lo..] : s[..lo] + "…";
    }

    private static void Text(Ctx c, string s, XFont f, XColor col, double x, double y, bool right = false) =>
        c.G.DrawString(s, f, Brush(col), x, y, right ? XStringFormats.TopRight : XStringFormats.TopLeft);

    private static void Section(Ctx c, string title, double minFollow = 60)
    {
        c.Need(40 + minFollow);
        c.Y += 14;
        Text(c, title, c.H2, Ink, M, c.Y);
        c.Y += 19;
        c.G.DrawLine(new XPen(Line, 1.2), M, c.Y, PageW - M, c.Y);
        c.Y += 7;
    }

    private static void Note(Ctx c, string s)
    {
        Text(c, s, c.Small, Grey, M, c.Y);
        c.Y += 14;
    }

    private static void HBar(Ctx c, double x, double y, double w, double h, double frac, XColor fill)
    {
        c.G.DrawRoundedRectangle(Brush(Track), x, y, w, h, h, h);
        double fw = Math.Max(h, w * Math.Clamp(frac, 0, 1));
        if (frac > 0) c.G.DrawRoundedRectangle(Brush(fill), x, y, fw, h, h, h);
    }

    // ------------------------------------------------------------------ sections

    private static void Header(Ctx c)
    {
        var d = c.Data;
        Text(c, "Speicherplatz-Bericht", c.H1, Ink, M, c.Y);
        c.Y += 30;
        // path, wrapped by hand
        var f = new XFont(FontName, 10.5, XFontStyleEx.Regular);
        foreach (var line in Wrap(c.G, d.Title, f, PageW - 2 * M))
        {
            Text(c, line, f, Grey, M, c.Y);
            c.Y += 14;
        }
        c.Y += 6;

        if (d.Interim)
        {
            c.G.DrawRoundedRectangle(new XPen(XColor.FromArgb(232, 197, 71)), Brush(XColor.FromArgb(255, 244, 214)), M, c.Y, PageW - 2 * M, 22, 5, 5);
            Text(c, "Zwischenstand: Der Scan lief noch, die Zahlen sind unvollständig.", c.Body, Ink, M + 8, c.Y + 6);
            c.Y += 32;
        }

        var cards = new List<(string, string)>
        {
            ("Erstellt am", d.Created.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture)),
            ("Gesamtgröße", SizeFormat.Format(d.TotalSize)),
            ("Dateien", d.Files.ToString("N0", CultureInfo.CurrentCulture)),
            ("Ordner", d.Dirs.ToString("N0", CultureInfo.CurrentCulture)),
        };
        if (d.Excluded > 0) cards.Add(("Übersprungene Ordner", d.Excluded.ToString("N0", CultureInfo.CurrentCulture)));
        double gap = 8, w = (PageW - 2 * M - gap * (cards.Count - 1)) / cards.Count;
        for (int i = 0; i < cards.Count; i++)
        {
            double x = M + i * (w + gap);
            c.G.DrawRoundedRectangle(new XPen(Line, 0.8), Brush(XColor.FromArgb(249, 250, 251)), x, c.Y, w, 40, 6, 6);
            Text(c, cards[i].Item1, c.Small, Grey, x + 8, c.Y + 5);
            Text(c, Fit(c.G, cards[i].Item2, c.Big, w - 12), c.Big, Ink, x + 8, c.Y + 19);
        }
        c.Y += 46;
    }

    private static List<string> Wrap(XGraphics g, string s, XFont f, double width)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var ch in s)
        {
            if (g.MeasureString(cur + ch, f).Width > width && cur.Length > 0) { lines.Add(cur); cur = ""; }
            cur += ch;
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines.Count > 6 ? lines.Take(6).ToList() : lines;
    }

    private static void DriveBlock(Ctx c)
    {
        var d = c.Data;
        if (d.DriveTotal <= 0) return;
        long used = Math.Max(0, d.DriveTotal - d.DriveFree);
        Section(c, "Laufwerk " + d.DriveName, 40);
        HBar(c, M, c.Y, PageW - 2 * M, 12, used / (double)d.DriveTotal, Blue);
        c.Y += 18;
        var s = $"{SizeFormat.Format(used)} belegt von {SizeFormat.Format(d.DriveTotal)} ({ReportData.Percent(used, d.DriveTotal)}), {SizeFormat.Format(d.DriveFree)} frei.";
        if (d.PartialDrive) s += $" Gescannt wurde nur ein Teil ({SizeFormat.Format(d.TotalSize)}).";
        Text(c, Fit(c.G, s, c.Body, PageW - 2 * M), c.Body, Ink, M, c.Y);
        c.Y += 16;
    }

    private static void Folders(Ctx c)
    {
        var d = c.Data;
        Section(c, $"Größte Ordner (Top {ReportData.FolderCount})", 80);
        if (d.Folders.Count == 0) { Note(c, "Keine Unterordner gefunden."); return; }
        Note(c, "Nach Gesamtgröße inklusive aller Unterordner; verschachtelte Ordner erscheinen daher mehrfach.");

        double xSize = PageW - M - 190, xPct = PageW - M - 130, xBar = PageW - M - 122, wBar = 70, xFiles = PageW - M;
        double pathW = xSize - 70 - M;
        void Head()
        {
            Text(c, "Ordner", c.Bold, Grey, M, c.Y);
            Text(c, "Größe", c.Bold, Grey, xSize, c.Y, true);
            Text(c, "Anteil", c.Bold, Grey, xPct, c.Y, true);
            Text(c, "Dateien", c.Bold, Grey, xFiles, c.Y, true);
            c.Y += 14;
            c.G.DrawLine(new XPen(Line, 0.8), M, c.Y - 2, PageW - M, c.Y - 2);
        }
        Head();
        long max = Math.Max(1, d.Folders[0].Size);
        foreach (var f in d.Folders)
        {
            if (c.Y + 16 > Bottom) { c.NewPage(); Head(); }
            Text(c, Fit(c.G, f.Path, c.Body, pathW + 60, keepEnd: true), c.Body, Ink, M, c.Y);
            Text(c, SizeFormat.Format(f.Size), c.Body, Ink, xSize, c.Y, true);
            Text(c, ReportData.Percent(f.Size, d.TotalSize), c.Body, Ink, xPct, c.Y, true);
            HBar(c, xBar - 2, c.Y + 2, wBar - 14, 7, f.Size / (double)max, Blue);
            Text(c, f.Files.ToString("N0", CultureInfo.CurrentCulture), c.Body, Ink, xFiles, c.Y, true);
            c.Y += 15;
        }
    }

    private static void Types(Ctx c)
    {
        var d = c.Data;
        Section(c, "Dateitypen", 190);
        if (d.Types.Count == 0) { Note(c, "Keine Dateien gefunden."); return; }
        long sum = Math.Max(1, d.Types.Sum(t => t.Size));

        const double r = 62;
        double cx = M + r + 4, cy = c.Y + r + 4;
        double start = -90;
        foreach (var t in d.Types)
        {
            double sweep = t.Size * 360.0 / sum;
            if (sweep <= 0) continue;
            if (d.Types.Count == 1) c.G.DrawEllipse(Brush(FromRgb(t.Color)), cx - r, cy - r, 2 * r, 2 * r);
            else c.G.DrawPie(new XPen(XColors.White, 0.6), Brush(FromRgb(t.Color)), cx - r, cy - r, 2 * r, 2 * r, start, sweep);
            start += sweep;
        }
        c.G.DrawEllipse(Brush(XColors.White), cx - 34, cy - 34, 68, 68);
        var total = SizeFormat.Format(sum);
        c.G.DrawString(total, c.Bold, Brush(Ink), cx, cy - 8, XStringFormats.TopCenter);
        c.G.DrawString("in Dateien", c.Small, Brush(Grey), cx, cy + 4, XStringFormats.TopCenter);

        // legend table beside the chart
        double lx = M + 2 * r + 30, xSize = PageW - M - 100, xPct = PageW - M - 50, xCnt = PageW - M;
        double y = c.Y;
        Text(c, "Typ", c.Bold, Grey, lx, y);
        Text(c, "Größe", c.Bold, Grey, xSize, y, true);
        Text(c, "Anteil", c.Bold, Grey, xPct, y, true);
        Text(c, "Dateien", c.Bold, Grey, xCnt, y, true);
        y += 15;
        double chartBottom = c.Y + 2 * r + 12;
        foreach (var t in d.Types)
        {
            if (y + 14 > Bottom) { c.NewPage(); y = c.Y; chartBottom = 0; }
            c.G.DrawRectangle(Brush(FromRgb(t.Color)), lx, y + 1.5, 8, 8);
            Text(c, Fit(c.G, t.Name, c.Body, xSize - 70 - lx - 14), c.Body, Ink, lx + 13, y);
            Text(c, SizeFormat.Format(t.Size), c.Body, Ink, xSize, y, true);
            Text(c, ReportData.Percent(t.Size, sum), c.Body, Ink, xPct, y, true);
            Text(c, t.Count.ToString("N0", CultureInfo.CurrentCulture), c.Body, Ink, xCnt, y, true);
            y += 14;
        }
        c.Y = Math.Max(y, chartBottom);
    }

    private static void Ages(Ctx c)
    {
        var d = c.Data;
        Section(c, "Alter der Dateien (letzte Änderung)", 110);
        long sum = d.Ages.Sum(a => a.Size);
        if (sum <= 0) { Note(c, "Keine Altersdaten vorhanden."); return; }

        double x = M, w = PageW - 2 * M;
        c.G.DrawRoundedRectangle(Brush(Track), M, c.Y, w, 16, 6, 6);
        var state = c.G.Save();
        c.G.IntersectClip(new XRect(M, c.Y, w, 16));
        for (int i = 0; i < d.Ages.Count; i++)
        {
            double bw = d.Ages[i].Size * w / sum;
            if (bw <= 0) continue;
            c.G.DrawRectangle(Brush(AgeColors[i % AgeColors.Length]), x, c.Y, bw, 16);
            x += bw;
        }
        c.G.Restore(state);
        c.Y += 26;

        double xSize = PageW - M - 100, xPct = PageW - M - 50, xCnt = PageW - M;
        Text(c, "Alter", c.Bold, Grey, M, c.Y);
        Text(c, "Größe", c.Bold, Grey, xSize, c.Y, true);
        Text(c, "Anteil", c.Bold, Grey, xPct, c.Y, true);
        Text(c, "Dateien", c.Bold, Grey, xCnt, c.Y, true);
        c.Y += 15;
        for (int i = 0; i < d.Ages.Count; i++)
        {
            var a = d.Ages[i];
            c.G.DrawRectangle(Brush(AgeColors[i % AgeColors.Length]), M, c.Y + 1.5, 8, 8);
            Text(c, a.Name, c.Body, Ink, M + 13, c.Y);
            Text(c, SizeFormat.Format(a.Size), c.Body, Ink, xSize, c.Y, true);
            Text(c, ReportData.Percent(a.Size, sum), c.Body, Ink, xPct, c.Y, true);
            Text(c, a.Count.ToString("N0", CultureInfo.CurrentCulture), c.Body, Ink, xCnt, c.Y, true);
            c.Y += 14;
        }
    }

    private static void TopFiles(Ctx c)
    {
        var d = c.Data;
        Section(c, $"Größte Dateien (Top {ReportData.FileCount})", 80);
        if (d.TopFiles.Count == 0) { Note(c, "Keine Dateien ab 1 MB gefunden."); return; }

        double xRank = M + 16, xName = M + 24, xSize = PageW - M;
        double nameW = 190, folderX = xName + nameW + 8, folderW = xSize - 70 - folderX;
        void Head()
        {
            Text(c, "#", c.Bold, Grey, xRank, c.Y, true);
            Text(c, "Datei", c.Bold, Grey, xName, c.Y);
            Text(c, "Ordner", c.Bold, Grey, folderX, c.Y);
            Text(c, "Größe", c.Bold, Grey, xSize, c.Y, true);
            c.Y += 14;
            c.G.DrawLine(new XPen(Line, 0.8), M, c.Y - 2, PageW - M, c.Y - 2);
        }
        Head();
        int rank = 0;
        foreach (var f in d.TopFiles)
        {
            rank++;
            if (c.Y + 15 > Bottom) { c.NewPage(); Head(); }
            Text(c, rank.ToString(CultureInfo.CurrentCulture), c.Body, Grey, xRank, c.Y, true);
            Text(c, Fit(c.G, Path.GetFileName(f.Path), c.Body, nameW), c.Body, Ink, xName, c.Y);
            Text(c, Fit(c.G, Path.GetDirectoryName(f.Path) ?? "", c.Small, folderW, keepEnd: true), c.Small, Grey, folderX, c.Y + 1);
            Text(c, SizeFormat.Format(f.Size), c.Body, Ink, xSize, c.Y, true);
            c.Y += 15;
        }
    }
}
