using System.ComponentModel;
using System.Diagnostics;

namespace DiskTree;

/// <summary>
/// Everything that belongs to one scan tab: the scanner and its tree, the scan target, the progress line, the
/// text of the path box, which timestamp the numbers were built from. <see cref="MainWindow"/> shows one session
/// at a time (the active tab); the others keep their state here, and a scan that runs in the background only
/// ever writes into its own session.
/// </summary>
public sealed class ScanSession : INotifyPropertyChanged
{
    public const string NewTitle = "Neuer Scan";
    public const string DefaultStatus = "Pfad oder Laufwerk eingeben (oder Ordner hierher ziehen) und auf „Scannen“ klicken.";

    public Scanner? Scanner { get; set; }
    public NodeVM? Root { get; set; }
    public Node? ScanTarget { get; set; }
    public Task ScanTask { get; set; } = Task.CompletedTask;
    public Stopwatch Sw { get; } = new();

    private CancellationTokenSource? _cts;
    /// <summary>Non-null while a scan or rescan of this tab runs.</summary>
    public CancellationTokenSource? Cts
    {
        get => _cts;
        set
        {
            _cts = value;
            Notify(nameof(IsBusy));
        }
    }

    public bool IsBusy => _cts != null;

    // What the status line / progress bar showed last (restored when the tab becomes active again).
    public string PathText { get; set; } = "";
    public string Status { get; set; } = DefaultStatus;
    public object? StatusTip { get; set; }
    public string CurrentLine { get; set; } = "";
    public bool ProgressVisible { get; set; }
    public bool ProgressKnown { get; set; }
    public double ProgressFraction { get; set; }

    /// <summary>The row that was selected in the tree when the tab was left (the tree itself is rebuilt on return).</summary>
    public NodeVM? SelectedVm { get; set; }

    /// <summary>The timestamp this tab's numbers were built from, and the user's choice for its next scan.</summary>
    public AgeStamp AgeStamp { get; set; } = AgeStamps.Preferred;
    public AgeStamp AgePreferred { get; set; } = AgeStamps.Preferred;

    private string _title = NewTitle;
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; Notify(nameof(Title)); } }
    }

    private string _tip = "Noch kein Scan in diesem Tab";
    public string Tip
    {
        get => _tip;
        set { if (_tip != value) { _tip = value; Notify(nameof(Tip)); } }
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; Notify(nameof(IsActive)); } }
    }

    /// <summary>No scan, no loaded scan: a fresh tab (its path box may hold text, but that does not count).</summary>
    public bool IsEmpty => Scanner == null && _cts == null;

    public void SetTitle(string title, string tip)
    {
        Title = title;
        Tip = tip;
    }

    /// <summary>Lets go of the scan and its tree so the memory can be freed (the tab is closed).</summary>
    public void Release()
    {
        Scanner = null;
        Root = null;
        ScanTarget = null;
        SelectedVm = null;
        StatusTip = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
