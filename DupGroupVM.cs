using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DiskTree;

public sealed class DupGroupVM : INotifyPropertyChanged
{
    public long Size { get; }
    public ObservableCollection<DupFileVM> Files { get; } = new();
    public bool IsExpanded { get; set; }

    public DupGroupVM(DuplicateGroup g)
    {
        Size = g.Size;
        foreach (var p in g.Paths) Files.Add(new DupFileVM(this, p));
    }

    public long Wasted => Size * (Files.Count - 1);
    public string Title => $"{Files.Count} × {NodeVM.FormatSize(Size)}";
    public string Subtitle => Files.Count > 0 ? Files[0].Name : "";
    public string WastedText => NodeVM.FormatSize(Wasted);
    public string Detail => $"{Files.Count} identische Dateien à {NodeVM.FormatSize(Size)}: {NodeVM.FormatSize(Wasted)} verschwendet";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        foreach (var n in new[] { nameof(Title), nameof(Subtitle), nameof(WastedText), nameof(Detail) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
