using System.Globalization;
using System.Windows;
using DiskTree.Core;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Duplicates;

/// <summary>
/// Before a duplicate search of a remote source compares anything: what the listing found, what each mode would read
/// (and, for sources that charge per request, ask), the choice of the mode and the download limit of the run.
/// </summary>
public partial class DupEstimateDialog : Window
{
    private readonly DupEstimate _e;
    private readonly bool _costs;

    /// <summary>The chosen mode.</summary>
    public DupMode Mode => ThoroughRadio.IsChecked == true ? DupMode.Thorough : DupMode.Fast;

    /// <summary>The download limit in bytes (0 = none).</summary>
    public long Limit { get; private set; }

    public DupEstimateDialog(string source, string root, DupCandidates c, DupEstimate e, long limit, DupMode mode = DupMode.Fast)
    {
        InitializeComponent();
        _e = e;
        _costs = (c.Provider.Caps & StorageCaps.CostsPerRequest) != 0;
        bool tags = e.TagChecks > 0;

        Summary.Text = $"{e.Files:N0} Dateien in {e.Groups:N0} Gruppen gleicher Größe müssen verglichen werden.";
        Where.Text = $"{source} · {root}";
        Listed.Text = ListedText(c, _costs);

        FastShort.Text = " – ohne vollständige Downloads";
        FastText.Text = FastDescription(e, tags, _costs);
        ThoroughShort.Text = " – vollständiger Inhaltsvergleich";
        ThoroughText.Text = ThoroughDescription(e, _costs);
        if (mode == DupMode.Thorough) ThoroughRadio.IsChecked = true;

        Limit = Math.Max(0, limit);
        LimitBox.Text = (Limit / (double)(1L << 30)).ToString("0.##", CultureInfo.CurrentCulture);
        Status.Text = _costs
            ? "Bei Cloud-Speichern kann jede Anfrage und jedes heruntergeladene Byte Kosten verursachen."
            : "Die Dateien werden nur im Speicher verglichen, nichts wird auf der Platte abgelegt.";
        Loaded += (_, _) => OkButton.Focus();
        Update();
    }

    public static string ListedText(DupCandidates c, bool costs) =>
        $"Aufgelistet: {c.FilesListed:N0} Dateien" +
        (costs ? $" ({c.ListRequests:N0} LIST-Anfragen)" : "") +
        (c.Errors > 0 ? $", {c.Errors:N0} Ordner nicht lesbar" : "") + ".\n" +
        $"Kandidaten: {c.Files:N0} Dateien mit zusammen {SizeFormat.Format(c.SameSize.Sum(g => g.Sum(f => f.Size)))}.";

    public static string FastDescription(DupEstimate e, bool tags, bool costs)
    {
        if (tags)
        {
            return "Gleiche Größe und gleiche ETag (Single-Part-Upload, per HEAD-Anfrage bestätigt) gilt als sicher – ohne Download. " +
                   "Alle anderen Dateien werden nur an Anfang und Ende (je 64 KB) verglichen; größere sind dann nur „wahrscheinlich“ gleich.\n" +
                   $"HEAD-Anfragen: {e.TagChecks:N0} · Download: ca. {SizeFormat.Format(e.FastBytes)} (höchstens {SizeFormat.Format(e.FastBytesWorst)})" +
                   (costs ? $" · Anfragen insgesamt: ca. {e.FastRequests:N0} (höchstens {e.FastRequestsWorst:N0})" : "");
        }
        return "Nur Anfang und Ende jeder Datei (je 64 KB) werden gelesen. Dateien bis 128 KB werden dabei ganz verglichen (sicher), " +
               "größere sind nur „wahrscheinlich“ gleich.\n" +
               $"Download: {SizeFormat.Format(e.FastBytesWorst)}" +
               (costs ? $" · GET-Anfragen: {e.FastRequestsWorst:N0}" : "");
    }

    public static string ThoroughDescription(DupEstimate e, bool costs) =>
        "Erst Anfang und Ende; jede Datei, die danach noch gleich aussieht, wird vollständig heruntergeladen und per SHA-256 verglichen. " +
        "Ergebnis immer „sicher“.\n" +
        $"Download: {SizeFormat.Format(e.PartialBytes)} für Anfang/Ende, dazu im schlimmsten Fall {SizeFormat.Format(e.FullBytesWorst)} " +
        $"(zusammen bis zu {SizeFormat.Format(e.ThoroughBytesWorst)})" +
        (costs ? $" · GET-Anfragen: bis zu {e.ThoroughRequestsWorst:N0}" : "");

    /// <summary>"2" or "0,5" (GB) as bytes; null if it is no number.</summary>
    public static long? ParseGb(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return 0;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var gb)
            && !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out gb)) return null;
        if (gb < 0 || double.IsNaN(gb) || gb > 1_000_000) return null;
        return (long)Math.Round(gb * (1L << 30));
    }

    private void Input_Changed(object sender, RoutedEventArgs e) => Update();

    private void Update()
    {
        if (Warning == null || LimitBox == null || OkButton == null) return; // events while the XAML is built
        var parsed = ParseGb(LimitBox.Text);
        OkButton.IsEnabled = parsed != null;
        if (parsed == null)
        {
            Warning.Text = "Bitte das Limit als Zahl in GB angeben (0 = ohne Limit).";
            Warning.Visibility = Visibility.Visible;
            return;
        }
        Limit = parsed.Value;
        long worst = Mode == DupMode.Thorough ? _e.ThoroughBytesWorst : _e.FastBytesWorst;
        if (Limit > 0 && worst > Limit)
        {
            Warning.Text = $"Im schlimmsten Fall werden {SizeFormat.Format(worst)} heruntergeladen – mehr als das Limit von " +
                           $"{SizeFormat.Format(Limit)}. Wird es erreicht, fragt die Suche nach.";
            Warning.Visibility = Visibility.Visible;
        }
        else
        {
            Warning.Visibility = Visibility.Collapsed;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ParseGb(LimitBox.Text) is not { } limit) return;
        Limit = limit;
        DialogResult = true;
    }
}
