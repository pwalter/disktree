using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace DiskTree;

/// <summary>Writes the space report as one self-contained HTML file (inline CSS and SVG, no external resources).</summary>
public static class ReportHtml
{
    private static readonly string[] AgeColors = { "#4C9F70", "#8DBE5B", "#E8C547", "#E58F3A", "#C8463B" };

    public static void Write(string file, ReportData d)
    {
        File.WriteAllText(file, Render(d), new UTF8Encoding(false));
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string Hex(uint c) => "#" + (c & 0xFFFFFF).ToString("X6");
    private static string N(long v) => v.ToString("N0", CultureInfo.CurrentCulture);
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Render(ReportData d)
    {
        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!DOCTYPE html>\n<html lang=\"de\"><head><meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>DiskTree-Bericht – ").Append(E(d.Title)).Append("</title>\n<style>").Append(Css).Append("</style></head><body><main>\n");

        sb.Append("<header><h1>Speicherplatz-Bericht</h1><p class=\"path\">").Append(E(d.Title)).Append("</p></header>\n");
        if (d.Interim)
            sb.Append("<p class=\"note\">Zwischenstand: Der Scan lief beim Erstellen des Berichts noch, die Zahlen sind unvollständig.</p>\n");

        // --- head figures
        sb.Append("<section class=\"cards\">");
        Card(sb, "Erstellt am", d.Created.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture));
        Card(sb, "Gesamtgröße", NodeVM.FormatSize(d.TotalSize));
        Card(sb, "Dateien", N(d.Files));
        Card(sb, "Ordner", N(d.Dirs));
        if (d.Excluded > 0) Card(sb, "Übersprungene Ordner", N(d.Excluded));
        sb.Append("</section>\n");

        if (d.DriveTotal > 0)
        {
            long used = Math.Max(0, d.DriveTotal - d.DriveFree);
            double pct = used * 100.0 / d.DriveTotal;
            sb.Append("<section><h2>Laufwerk ").Append(E(d.DriveName)).Append("</h2>");
            sb.Append("<div class=\"track\"><div class=\"fill\" style=\"width:").Append(F(pct)).Append("%\"></div></div>");
            sb.Append("<p>").Append(NodeVM.FormatSize(used)).Append(" belegt von ").Append(NodeVM.FormatSize(d.DriveTotal))
              .Append(" (").Append(ReportData.Percent(used, d.DriveTotal)).Append("), ")
              .Append(NodeVM.FormatSize(d.DriveFree)).Append(" frei.");
            if (d.PartialDrive)
                sb.Append(" Gescannt wurde nur ein Teil des Laufwerks (").Append(NodeVM.FormatSize(d.TotalSize)).Append(").");
            sb.Append("</p></section>\n");
        }

        // --- biggest folders
        sb.Append("<section><h2>Größte Ordner (Top ").Append(ReportData.FolderCount).Append(")</h2>");
        if (d.Folders.Count == 0) sb.Append("<p class=\"muted\">Keine Unterordner gefunden.</p>");
        else
        {
            sb.Append("<p class=\"muted\">Nach Gesamtgröße inklusive aller Unterordner; verschachtelte Ordner erscheinen daher mehrfach.</p>");
            sb.Append("<table><thead><tr><th>Ordner</th><th class=\"r\">Größe</th><th class=\"r\">Anteil</th><th class=\"bar\"></th><th class=\"r\">Dateien</th></tr></thead><tbody>");
            long max = Math.Max(1, d.Folders[0].Size);
            foreach (var f in d.Folders)
            {
                sb.Append("<tr><td class=\"p\">").Append(E(f.Path)).Append("</td><td class=\"r\">").Append(NodeVM.FormatSize(f.Size))
                  .Append("</td><td class=\"r\">").Append(ReportData.Percent(f.Size, d.TotalSize))
                  .Append("</td><td class=\"bar\"><div class=\"track sm\"><div class=\"fill\" style=\"width:").Append(F(f.Size * 100.0 / max))
                  .Append("%\"></div></div></td><td class=\"r\">").Append(N(f.Files)).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>\n");

        // --- types
        sb.Append("<section><h2>Dateitypen</h2>");
        if (d.Types.Count == 0) sb.Append("<p class=\"muted\">Keine Dateien gefunden.</p>");
        else
        {
            long sum = Math.Max(1, d.Types.Sum(t => t.Size));
            sb.Append("<div class=\"split\">");
            Donut(sb, d.Types, sum);
            sb.Append("<table><thead><tr><th>Typ</th><th class=\"r\">Größe</th><th class=\"r\">Anteil</th><th class=\"r\">Dateien</th></tr></thead><tbody>");
            foreach (var t in d.Types)
                sb.Append("<tr><td><span class=\"sw\" style=\"background:").Append(Hex(t.Color)).Append("\"></span>").Append(E(t.Name))
                  .Append("</td><td class=\"r\">").Append(NodeVM.FormatSize(t.Size)).Append("</td><td class=\"r\">")
                  .Append(ReportData.Percent(t.Size, sum)).Append("</td><td class=\"r\">").Append(N(t.Count)).Append("</td></tr>");
            sb.Append("</tbody></table></div>");
        }
        sb.Append("</section>\n");

        // --- ages
        sb.Append("<section><h2>Alter der Dateien (letzte Änderung)</h2>");
        long ageSum = d.Ages.Sum(a => a.Size);
        if (ageSum <= 0) sb.Append("<p class=\"muted\">Keine Altersdaten vorhanden.</p>");
        else
        {
            sb.Append("<div class=\"stack\">");
            for (int i = 0; i < d.Ages.Count; i++)
                if (d.Ages[i].Size > 0)
                    sb.Append("<div style=\"width:").Append(F(d.Ages[i].Size * 100.0 / ageSum)).Append("%;background:").Append(AgeColors[i % AgeColors.Length])
                      .Append("\" title=\"").Append(E(d.Ages[i].Name)).Append("\"></div>");
            sb.Append("</div><table><thead><tr><th>Alter</th><th class=\"r\">Größe</th><th class=\"r\">Anteil</th><th class=\"r\">Dateien</th></tr></thead><tbody>");
            for (int i = 0; i < d.Ages.Count; i++)
            {
                var a = d.Ages[i];
                sb.Append("<tr><td><span class=\"sw\" style=\"background:").Append(AgeColors[i % AgeColors.Length]).Append("\"></span>").Append(E(a.Name))
                  .Append("</td><td class=\"r\">").Append(NodeVM.FormatSize(a.Size)).Append("</td><td class=\"r\">")
                  .Append(ReportData.Percent(a.Size, ageSum)).Append("</td><td class=\"r\">").Append(N(a.Count)).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>\n");

        // --- biggest files
        sb.Append("<section><h2>Größte Dateien (Top ").Append(ReportData.FileCount).Append(")</h2>");
        if (d.TopFiles.Count == 0) sb.Append("<p class=\"muted\">Keine Dateien ab 1 MB gefunden.</p>");
        else
        {
            sb.Append("<table><thead><tr><th class=\"r\">#</th><th>Datei</th><th>Ordner</th><th class=\"r\">Größe</th></tr></thead><tbody>");
            int rank = 0;
            foreach (var f in d.TopFiles)
            {
                rank++;
                sb.Append("<tr><td class=\"r\">").Append(rank).Append("</td><td>").Append(E(Path.GetFileName(f.Path)))
                  .Append("</td><td class=\"p\">").Append(E(Path.GetDirectoryName(f.Path) ?? ""))
                  .Append("</td><td class=\"r\">").Append(NodeVM.FormatSize(f.Size)).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>\n");

        sb.Append("<footer>Erstellt mit DiskTree am ").Append(d.Created.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture)).Append("</footer>\n");
        sb.Append("</main></body></html>\n");
        return sb.ToString();
    }

    private static void Card(StringBuilder sb, string label, string value) =>
        sb.Append("<div class=\"card\"><span>").Append(E(label)).Append("</span><b>").Append(E(value)).Append("</b></div>");

    private static void Donut(StringBuilder sb, IReadOnlyList<ReportData.TypeRow> types, long sum)
    {
        const double cx = 100, cy = 100, rOut = 92, rIn = 52;
        sb.Append("<svg class=\"donut\" viewBox=\"0 0 200 200\" role=\"img\" aria-label=\"Dateitypen nach Größe\">");
        if (types.Count == 1)
        {
            sb.Append("<circle cx=\"100\" cy=\"100\" r=\"72\" fill=\"none\" stroke=\"").Append(Hex(types[0].Color)).Append("\" stroke-width=\"40\"/>");
        }
        else
        {
            double a0 = -Math.PI / 2;
            foreach (var t in types)
            {
                double sweep = t.Size * 2 * Math.PI / sum;
                if (sweep <= 0) continue;
                double a1 = a0 + sweep;
                int large = sweep > Math.PI ? 1 : 0;
                string P(double r, double a) => F(cx + r * Math.Cos(a)) + " " + F(cy + r * Math.Sin(a));
                sb.Append("<path fill=\"").Append(Hex(t.Color)).Append("\" stroke=\"#fff\" stroke-width=\"0.8\" d=\"M").Append(P(rOut, a0))
                  .Append(" A").Append(F(rOut)).Append(' ').Append(F(rOut)).Append(" 0 ").Append(large).Append(" 1 ").Append(P(rOut, a1))
                  .Append(" L").Append(P(rIn, a1))
                  .Append(" A").Append(F(rIn)).Append(' ').Append(F(rIn)).Append(" 0 ").Append(large).Append(" 0 ").Append(P(rIn, a0))
                  .Append(" Z\"><title>").Append(E(t.Name)).Append(": ").Append(NodeVM.FormatSize(t.Size)).Append("</title></path>");
                a0 = a1;
            }
        }
        sb.Append("<text x=\"100\" y=\"97\" text-anchor=\"middle\" font-size=\"15\" font-weight=\"700\" fill=\"#222\">").Append(E(NodeVM.FormatSize(sum)))
          .Append("</text><text x=\"100\" y=\"114\" text-anchor=\"middle\" font-size=\"9\" fill=\"#666\">in Dateien</text></svg>");
    }

    private const string Css = @"
*{box-sizing:border-box}
body{margin:0;background:#f3f4f6;color:#1f2328;font:14px/1.45 'Segoe UI',system-ui,Arial,sans-serif;-webkit-print-color-adjust:exact;print-color-adjust:exact}
main{max-width:1000px;margin:0 auto;padding:28px 32px 40px;background:#fff}
h1{margin:0;font-size:26px}
h2{margin:0 0 10px;font-size:17px;border-bottom:2px solid #e5e7eb;padding-bottom:4px}
.path{margin:2px 0 14px;font-size:15px;color:#4b5563;word-break:break-all}
section{margin:26px 0;break-inside:avoid-page}
.note{background:#fff4d6;border:1px solid #e8c547;padding:8px 12px;border-radius:6px}
.muted{color:#6b7280;font-size:12.5px;margin:0 0 8px}
.cards{display:flex;flex-wrap:wrap;gap:10px;margin-top:0}
.card{flex:1 1 140px;border:1px solid #e5e7eb;border-radius:8px;padding:8px 12px;background:#f9fafb}
.card span{display:block;font-size:11.5px;color:#6b7280}
.card b{font-size:18px}
table{border-collapse:collapse;width:100%;font-size:13px}
th{text-align:left;font-weight:600;color:#4b5563;border-bottom:1px solid #d1d5db;padding:4px 8px}
td{padding:3px 8px;border-bottom:1px solid #eef0f2;vertical-align:middle}
tr{break-inside:avoid}
.r{text-align:right;white-space:nowrap}
.p{word-break:break-all;font-size:12.5px}
td.bar,th.bar{width:22%}
.track{height:14px;background:#e5e7eb;border-radius:7px;overflow:hidden}
.track.sm{height:10px;border-radius:5px}
.fill{height:100%;background:#3b82c4}
.sw{display:inline-block;width:11px;height:11px;border-radius:2px;margin-right:7px;vertical-align:-1px}
.split{display:flex;gap:24px;align-items:flex-start;flex-wrap:wrap}
.split table{flex:1 1 420px}
.donut{width:220px;height:220px;flex:none}
.stack{display:flex;height:22px;border-radius:6px;overflow:hidden;margin-bottom:10px;background:#e5e7eb}
footer{margin-top:30px;color:#9ca3af;font-size:12px;text-align:center}
@media print{body{background:#fff}main{max-width:none;padding:0}@page{margin:14mm}section{margin:18px 0}}
";
}
