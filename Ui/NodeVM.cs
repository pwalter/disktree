using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Infrastructure;

namespace DiskTree.Ui;

public enum Kind { Dir, Files, File, Space, Dummy }

public sealed class NodeVM : INotifyPropertyChanged
{
    public const double BarMax = 150;
    public const double NameColumn = 380;
    private const int Indent = 19;     // WPF TreeViewItem indent per level

    private const string FreeName = "Freier Speicher";

    private static readonly Brush?[] BarCache = new Brush?[202];
    private static readonly Brush?[] TextCache = new Brush?[202];
    private static readonly Brush Gray = BrushFactory.Frozen(0x9A, 0xA0, 0xA8);
    private static readonly Brush DarkGrayLight = BrushFactory.Frozen(0x6B, 0x72, 0x7B);
    private static readonly Brush DarkGrayDark = BrushFactory.Frozen(0x9A, 0xA1, 0xAA);
    private static readonly Brush InkLight = BrushFactory.Frozen(0x1F, 0x23, 0x29);
    private static readonly Brush InkDark = BrushFactory.Frozen(0xE8, 0xEA, 0xED);
    private static Brush DarkGray => Theme.IsDark ? DarkGrayDark : DarkGrayLight;
    private static Brush Ink => Theme.IsDark ? InkDark : InkLight;
    private static readonly Brush Gold = BrushFactory.Frozen(0xE0, 0xA8, 0x20);
    private static readonly Brush FreeBrushLight = BrushFactory.Frozen(0x9C, 0xC4, 0xEE);
    private static readonly Brush RestBrushLight = BrushFactory.Frozen(0xD6, 0xC8, 0xB0);
    private static readonly Brush FreeBrushDark = BrushFactory.Frozen(0x3F, 0x6E, 0xA0);
    private static readonly Brush RestBrushDark = BrushFactory.Frozen(0x6E, 0x62, 0x4C);
    private static Brush FreeBrush => Theme.IsDark ? FreeBrushDark : FreeBrushLight;
    private static Brush RestBrush => Theme.IsDark ? RestBrushDark : RestBrushLight;

    private readonly Node? _node;
    private readonly Dictionary<Node, NodeVM> _byNode = new();
    private NodeVM? _group, _free, _rest;
    private bool _expanded, _loaded, _loadingFiles;
    private long _filesLoadedCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Kind Kind { get; }
    public NodeVM? Parent { get; }
    public int Level { get; }
    public ObservableCollection<NodeVM> Children { get; } = new();

    /// <summary>Placeholder child so the expander arrow shows before the real children are created.</summary>
    public NodeVM()
    {
        Kind = Kind.Dummy;
    }

    private string? _message;

    /// <summary>A placeholder row that shows a message instead of "Lade…" (e.g. the files of a folder could not be read).</summary>
    public static NodeVM Message(string text) => new() { _message = text };

    /// <summary>Where the files of the "Dateien" row are read from: given to the root row, inherited by all others.</summary>
    private readonly IStorageProvider _provider = DiskProvider.Instance;

    public NodeVM(Node node, Kind kind, NodeVM? parent, int level, IStorageProvider? provider = null)
    {
        _node = node;
        Kind = kind;
        Parent = parent;
        Level = level;
        _provider = provider ?? parent?._provider ?? DiskProvider.Instance;

        if ((kind == Kind.Dir && (node.HasChildren || node.DriveTotal > 0)) || kind == Kind.Files)
            Children.Add(new NodeVM());
    }

    public Node? Node => _node;
    public long Size => _node?.Size ?? 0;
    public string Path => _node?.FullPath ?? "";

    public string Name => Kind switch
    {
        Kind.Dummy => _message ?? "Lade…",
        Kind.Files => $"Dateien ({_node!.FileCount:N0})",
        Kind.Dir when _node!.DriveTotal > 0 =>
            $"{_node.Name}   {SizeFormat.Format(_node.DriveTotal)} gesamt · {SizeFormat.Format(_node.DriveFree)} frei",
        _ => _node!.Name,
    };

    /// <summary>The size is still growing: the folder (or, for the Dateien entry, its parent) is being scanned.</summary>
    public bool IsPending => Kind switch
    {
        Kind.Dir => !_node!.IsDone,
        Kind.Files => !Parent!._node!.Scanned,
        _ => false,
    };

    /// <summary>
    /// Share shown in the bar. Below a drive it refers to the drive's capacity (so free space and the
    /// scanned folders add up to 100 %), everywhere else to the parent folder.
    /// </summary>
    public double Percent
    {
        get
        {
            if (Kind == Kind.Dummy) return 0;

            long basis = Parent == null
                ? _node!.DriveTotal
                : Parent._node!.DriveTotal > 0 ? Parent._node.DriveTotal : Parent.Size;

            if (Parent == null && basis == 0) return 1;
            return basis <= 0 ? 0 : Math.Min(1, (double)Size / basis);
        }
    }

    public string Icon => Kind switch
    {
        Kind.Dir when Level == 0 => _provider.Id == DiskProvider.ProviderId ? "💽" : "☁",
        Kind.Dir => "📁",
        Kind.Files => "🗂",
        Kind.File => "📄",
        Kind.Space when _node!.Name == FreeName => "⬜",
        Kind.Space => "❔",
        _ => "",
    };

    public Brush IconBrush => Kind == Kind.Dir ? Gold : DarkGray;
    public Brush NameBrush => Kind is Kind.Files or Kind.Space ? DarkGray : Ink;
    public double NameWidth => Math.Max(60, NameColumn - Level * Indent);

    public string SizeText => Kind == Kind.Dummy ? "" : IsPending ? SizeFormat.Format(Size) + " …" : SizeFormat.Format(Size);
    public string PercentText => Kind == Kind.Dummy ? "" : (Percent * 100).ToString("0.#", CultureInfo.CurrentCulture) + " %";
    public string FilesText => Kind is Kind.Dummy or Kind.Space ? "" : _node!.FileCount.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Bytes older than the age threshold (AgeThreshold.Months); empty for single files and drive space.</summary>
    public string OldText
    {
        get
        {
            if (Kind is not (Kind.Dir or Kind.Files)) return "";
            long old = Kind == Kind.Dir ? AgeThreshold.OldSize(_node!) : AgeThreshold.OldOwnSize(Parent!._node!);
            return old <= 0 ? "–" : SizeFormat.Format(old);
        }
    }

    public double BarWidth => Kind == Kind.Dummy ? 0 : Math.Max(Size > 0 ? 2 : 0, Percent * BarMax);

    public Brush BarBrush => Kind switch
    {
        Kind.Files => Gray,
        Kind.Space => _node!.Name == FreeName ? FreeBrush : RestBrush,
        _ => IsPending ? Gray : Cached(BarCache, Percent, Theme.IsDark ? 0.70 : 0.78, Theme.IsDark ? 0.62 : 0.92),
    };

    public Brush SizeBrush => Kind is Kind.Files or Kind.Space || IsPending
        ? DarkGray
        : Cached(TextCache, Percent, Theme.IsDark ? 0.55 : 0.95, Theme.IsDark ? 0.98 : 0.62);

    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Notify(nameof(IsSelected));
        }
    }

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            if (value && !_loaded)
            {
                _loaded = true;
                if (Kind == Kind.Files)
                {
                    if (MayList()) LoadFilesAsync().Forget("LoadFilesAsync");
                }
                else SyncChildren();
            }
            Notify(nameof(IsExpanded));
        }
    }

    /// <summary>
    /// Pulls the current numbers from the scanner into the UI. Only touches what is visible:
    /// this node and, recursively, the children of expanded nodes.
    /// </summary>
    public void Refresh()
    {
        if (Kind == Kind.Dummy) return;

        // Name/Group text, sizes, bar etc. all derive from the node, so notify them in one go.
        Notify(nameof(Name));
        Notify(nameof(IsPending));
        Notify(nameof(SizeText));
        Notify(nameof(PercentText));
        Notify(nameof(FilesText));
        Notify(nameof(OldText));
        Notify(nameof(BarWidth));
        Notify(nameof(BarBrush));
        Notify(nameof(SizeBrush));

        if (Kind == Kind.Files)
        {
            // Files were deleted (or the scan of the folder finished): re-read the list once.
            if (_loaded && _expanded && !_loadingFiles && Parent!._node!.Scanned && _node!.FileCount != _filesLoadedCount
                && StorageFeatures.Has(_provider, StorageFeature.LiveFileList))
                LoadFilesAsync().Forget("LoadFilesAsync");
            return;
        }

        if (Kind != Kind.Dir) return;

        if (_expanded && _loaded)
        {
            SyncChildren();
            foreach (var c in Children.ToArray())
                c.Refresh();
        }
        else
        {
            // Collapsed: only keep the expander arrow in sync with whether there is anything to show.
            bool has = _node!.HasChildren || _node.DriveTotal > 0;
            if (has && Children.Count == 0) Children.Add(new NodeVM());
            else if (!has && Children.Count > 0) Children.Clear();
        }
    }

    /// <summary>Light/dark switch: re-reads the brushes of this row and of all rows that were created below it.</summary>
    public void RefreshTheme()
    {
        if (Kind == Kind.Dummy) return;
        Notify(nameof(IconBrush));
        Notify(nameof(NameBrush));
        Notify(nameof(BarBrush));
        Notify(nameof(SizeBrush));
        foreach (var c in Children.ToArray())
            c.RefreshTheme();
    }

    /// <summary>Adds new child rows and keeps them sorted: folders by size (largest first), then files, then drive space.</summary>
    private void SyncChildren()
    {
        if (_node == null || Kind != Kind.Dir) return;

        if (Children.Count == 1 && Children[0].Kind == Kind.Dummy)
            Children.Clear();

        foreach (var c in _node.Snapshot())
        {
            if (!_byNode.ContainsKey(c))
            {
                var vm = new NodeVM(c, Kind.Dir, this, Level + 1);
                _byNode[c] = vm;
                Children.Add(vm);
            }
        }

        if (_node.OwnCount > 0)
        {
            if (_group == null)
            {
                _group = new NodeVM(new Node { FullPath = _node.FullPath }, Kind.Files, this, Level + 1);
                Children.Add(_group);
            }
            // The Dateien entry mirrors the files directly inside this folder.
            var g = _group._node!;
            g.Size = _node.OwnSize;
            g.FileCount = _node.OwnCount;
        }
        else if (_group != null)
        {
            Children.Remove(_group);
            _group = null;
        }

        if (_node.DriveTotal > 0)
        {
            if (_free == null)
            {
                _free = new NodeVM(new Node { Name = FreeName }, Kind.Space, this, Level + 1);
                _rest = new NodeVM(new Node(), Kind.Space, this, Level + 1);
                Children.Add(_free);
                Children.Add(_rest);
            }
            _free._node!.Size = _node.DriveFree;
            _rest!._node!.Name = _node.PartialDrive
                ? "Rest des Laufwerks (nicht gescannt)"
                : "Nicht erfasst (Systemdateien, fehlende Rechte" + (_node.Excluded > 0 ? $", {_node.Excluded:N0} ausgeschlossen" : "") + ")";
            _rest._node!.Size = Math.Max(0, _node.DriveTotal - _node.DriveFree - _node.Size);
        }

        // With only a subfolder scanned, the drive's free/unscanned rows go first: below the (expanded) scanned
        // folder they would sit far out of sight.
        int spaceRank = _node.PartialDrive ? -1 : 2;
        int Rank(NodeVM c) => c.Kind switch { Kind.Dir => 0, Kind.Files => 1, _ => spaceRank };
        var order = Children.OrderBy(Rank).ThenByDescending(c => c.Size).ToList();
        for (int i = 0; i < order.Count; i++)
        {
            if (!ReferenceEquals(Children[i], order[i]))
                Children.Move(Children.IndexOf(order[i]), i);
        }
    }

    /// <summary>The row for a node somewhere below this one, or null if that part of the tree was never opened.</summary>
    public NodeVM? FindDescendant(Node target)
    {
        if (_node == target) return this;

        var path = new Stack<Node>();
        var n = target;
        for (; n != null && n != _node; n = n.Parent)
            path.Push(n);
        if (n == null) return null; // not below this node

        var vm = this;
        while (path.Count > 0)
        {
            if (!vm._byNode.TryGetValue(path.Pop(), out var next)) return null;
            vm = next;
        }
        return vm;
    }

    /// <summary>
    /// Drops all child rows because the folder is being scanned again. They are rebuilt on the next refresh
    /// (or when the row is expanded), so nothing stale from the old scan stays on screen.
    /// </summary>
    public void ResetForRescan()
    {
        Children.Clear();
        _byNode.Clear();
        _group = _free = _rest = null;
        _loaded = false;
        if (_expanded) { _loaded = true; SyncChildren(); }
        Notify(nameof(IsExpanded));
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Asked before the "Dateien" row lists a folder of a source where every listing is a (paid) request (S3): true =
    /// go ahead. Set by the window; null = always list.
    /// </summary>
    public static Func<IStorageProvider, string, bool>? ConfirmListing { get; set; }

    /// <summary>The "Dateien" row may read its folder now; if not, it says so and asks again on the next opening.</summary>
    private bool MayList()
    {
        if (StorageFeatures.Has(_provider, StorageFeature.LiveFileList) || ConfirmListing == null) return true;
        if (ConfirmListing(_provider, _node!.FullPath)) return true;
        _loaded = false;
        Children.Clear();
        Children.Add(Message("Dateiliste nicht geladen (Anfrage an die Quelle) – zum Laden erneut aufklappen"));
        return false;
    }

    // Individual files are not kept in memory by the scanner; they are read when the "Dateien" row is opened.
    private async Task LoadFilesAsync()
    {
        _loadingFiles = true;
        _filesLoadedCount = _node!.FileCount;
        try
        {
            var folder = _node.FullPath;
            var provider = _provider;
            List<Node> files;
            try
            {
                files = await Task.Run(() => ListFiles(provider, folder));
            }
            catch (Exception)
            {
                // The folder is gone or unreadable (or the scan comes from another machine): without this the
                // "Lade…" row would stay for good.
                Children.Clear();
                Children.Add(Message("Dateien nicht lesbar"));
                return;
            }

            Children.Clear();
            foreach (var f in files)
                Children.Add(new NodeVM(f, Kind.File, this, Level + 1));
        }
        finally
        {
            _loadingFiles = false;
        }
    }

    private static readonly ListOptions FileListOptions = ListOptions.Lenient with { Stamps = StampSet.None };

    private static List<Node> ListFiles(IStorageProvider provider, string folder)
    {
        var paths = provider.Paths;
        var list = new List<Node>();
        var entries = provider.Enumerate(folder,
            (in StorageEntry e) => (e.Name.ToString(), e.IsDirectory ? -1L : e.Length), FileListOptions);

        foreach (var (name, len) in entries)
        {
            if (len < 0) continue;
            list.Add(new Node { Name = name, FullPath = paths.Join(folder, name), Size = len, FileCount = 1 });
        }
        list.Sort((a, b) => b.Size.CompareTo(a.Size));
        return list;
    }

    /// <summary>
    /// Takes a deleted file or folder out of the tree and subtracts its size from all parents.
    /// The root cannot be removed.
    /// </summary>
    public void RemoveFromTree(int ageBucket = -1)
    {
        if (Parent == null || _node == null) return;

        if (Kind == Kind.File)
        {
            TreeEditor.RemoveFile(Parent.Parent!._node!, _node.Name, Size, ageBucket);
            Parent.Children.Remove(this);
        }
        else if (Kind == Kind.Dir)
        {
            TreeEditor.RemoveFolder(_node);
            Parent._byNode.Remove(_node);
            Parent.Children.Remove(this);
        }
    }

    // Green (small share) -> yellow -> orange -> red (large share).
    private static Brush Cached(Brush?[] cache, double percent, double s, double v)
    {
        int i = Math.Clamp((int)Math.Round(percent * 100), 0, 100);
        int slot = Theme.IsDark ? i + 101 : i; // separate entries per theme
        return cache[slot] ??= BrushFactory.Frozen(BrushFactory.Hsv(130 * (1 - Math.Pow(i / 100.0, 0.6)), s, v));
    }
}
