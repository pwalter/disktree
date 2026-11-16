using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using DiskTree.Core;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Duplicates;

public sealed class DupGroupVM : INotifyPropertyChanged
{
    private static readonly Brush SureBrush = Frozen(Color.FromRgb(0x2E, 0x9E, 0x5B));
    private static readonly Brush ProbableBrush = Frozen(Color.FromRgb(0xD0, 0x8A, 0x00));

    public long Size { get; }
    public ObservableCollection<DupFileVM> Files { get; } = new();
    public bool IsExpanded { get; set; }

    /// <summary>How the files were compared.</summary>
    public DupBasis Basis { get; }

    /// <summary>Results of a remote source show "sicher" / "wahrscheinlich"; the disk (always compared completely) shows nothing.</summary>
    public bool ShowCertainty { get; }

    /// <param name="paths">Path rules of the source (null: the disk).</param>
    /// <param name="remote">From a remote source: certainty badge, no check boxes (no file actions).</param>
    /// <param name="canCheck">Check boxes anyway (a remote source whose files can be deleted / moved, SFTP); null: not for remote.</param>
    public DupGroupVM(DuplicateGroup g, PathOps? paths = null, bool remote = false, bool? canCheck = null)
    {
        Size = g.Size;
        Basis = g.Basis;
        ShowCertainty = remote;
        foreach (var p in g.Paths) Files.Add(new DupFileVM(this, p, paths, canCheck: canCheck ?? !remote));
    }

    public bool IsCertain => Basis != DupBasis.PartialHash;

    public long Wasted => Size * (Files.Count - 1);
    public string Title => $"{Files.Count} × {SizeFormat.Format(Size)}";
    public string Subtitle => Files.Count > 0 ? Files[0].Name : "";
    public string WastedText => SizeFormat.Format(Wasted);
    public string Detail => IsCertain
        ? $"{Files.Count} identische Dateien à {SizeFormat.Format(Size)}: {SizeFormat.Format(Wasted)} verschwendet"
        : $"{Files.Count} wahrscheinlich identische Dateien à {SizeFormat.Format(Size)}: bis zu {SizeFormat.Format(Wasted)} verschwendet";

    public Visibility BadgeVisibility => ShowCertainty ? Visibility.Visible : Visibility.Collapsed;
    public string BadgeText => IsCertain ? "sicher" : "wahrscheinlich";
    public Brush BadgeBrush => IsCertain ? SureBrush : ProbableBrush;
    public string BadgeTip => Basis switch
    {
        DupBasis.ContentTag => "Sicher: gleiche Größe und gleiche Inhalts-Prüfsumme des Servers (S3-ETag = MD5 des Inhalts), ohne Download.",
        DupBasis.PartialHash => "Wahrscheinlich: gleiche Größe und gleicher Anfang und gleiches Ende (je 64 KB). Die Mitte wurde nicht verglichen – " +
                                "„Gründlich“ lädt die Dateien vollständig und prüft sicher.",
        _ => "Sicher: der vollständige Inhalt wurde verglichen (SHA-256).",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        foreach (var n in new[] { nameof(Title), nameof(Subtitle), nameof(WastedText), nameof(Detail) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
