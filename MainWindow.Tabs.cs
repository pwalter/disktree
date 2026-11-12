using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;

namespace DiskTree;

// Scan tabs. The window shows one ScanSession (the active tab). The fields the other partial files always used
// (_scanner, _root, _scanTarget, _cts, ...) are properties that point at the active session, so all of that code
// works on "the current tab" without changes.
//
// Running scans: only ONE scan runs at a time across all tabs, but switching tabs while it runs is allowed.
// The scan code (RunAsync, OnScannerChanged, UpdateStatus in MainWindow.xaml.cs) holds on to its own ScanSession
// and only touches window controls when that session is the active one; everything else goes into the session
// and is shown when the tab becomes active again. Why one scan at a time: AgeStamps / AgeBuckets are process-wide
// statics that every running scan reads, the taskbar shows a single progress, and two scans would fight for the disk.
public partial class MainWindow
{
    private ScanSession _cur = new();
    private readonly ObservableCollection<ScanSession> _tabs = new();

    private Scanner? _scanner { get => _cur.Scanner; set => _cur.Scanner = value; }
    private NodeVM? _root { get => _cur.Root; set => _cur.Root = value; }
    private Node? _scanTarget { get => _cur.ScanTarget; set => _cur.ScanTarget = value; }
    private CancellationTokenSource? _cts { get => _cur.Cts; set => _cur.Cts = value; }
    private Task _scanTask { get => _cur.ScanTask; set => _cur.ScanTask = value; }
    private Stopwatch _sw => _cur.Sw;

    /// <summary>The tab whose scan is running (there is at most one).</summary>
    private ScanSession? Busy => _tabs.FirstOrDefault(t => t.Cts != null);

    // A file operation or the updater works on "the current tab" for a while: no tab changes meanwhile.
    private bool TabsLocked => _opBusy || _updating;

    private void InitTabs()
    {
        _tabs.Add(_cur);
        _cur.IsActive = true;
        ScanTabBar.ItemsSource = _tabs;
        ScanTabBar.TabSelected += s => { if (!TabsLocked) ActivateTab(s); };
        ScanTabBar.TabCloseRequested += s => _ = CloseTabAsync(s);
        ScanTabBar.NewTabRequested += () => { if (!TabsLocked) NewTab(); };
    }

    private void NoticeBusy(ScanSession busy) =>
        Notice($"Im Tab „{busy.Title}“ läuft gerade ein Scan. Es kann nur ein Scan gleichzeitig laufen: " +
               "bitte abwarten oder dort abbrechen.");

    // ---------------------------------------------------------------- open / switch / close

    /// <summary>Adds an empty tab at the end and shows it.</summary>
    private ScanSession NewTab()
    {
        var s = new ScanSession();
        _tabs.Add(s);
        ActivateTab(s);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PathBox.Focus());
        return s;
    }

    private void ActivateTab(ScanSession s)
    {
        if (s == _cur || !_tabs.Contains(s)) return;

        // What the window shows is part of the tab it belongs to.
        var old = _cur;
        old.PathText = PathBox.Text;
        old.AgePreferred = AgeStamps.Preferred;
        if (Tree.SelectedItem is NodeVM sel) old.SelectedVm = sel;
        old.IsActive = false;

        _cur = s;
        s.IsActive = true;
        ShowSession(s);
    }

    /// <summary>Puts a session's state into the window: path box, tree, status line, and the side panel for its scan.</summary>
    private void ShowSession(ScanSession s)
    {
        // Results of the other tab (search hits, duplicate groups) would point into its tree.
        SearchPane.Reset();

        PathBox.Text = s.PathText;
        _settingStampBox = true;
        AgeStamps.Preferred = s.AgePreferred;
        AgeStampBox.SelectedIndex = (int)s.AgePreferred;
        _settingStampBox = false;
        // The running scan (if any) owns the process-wide timestamp; do not change it under it.
        if (Busy == null && s.Scanner != null) AgeStamps.Current = s.AgeStamp;

        Tree.ItemsSource = null;
        TopList.ItemsSource = null;
        if (s.Root != null)
        {
            s.Root.Refresh();      // background scans / theme changes did not reach this tree
            s.Root.RefreshTheme();
            Tree.ItemsSource = new[] { s.Root };
        }
        Treemap.Reset(s.ScanTarget ?? s.Scanner?.Root);

        ScanButton.Content = s.Cts != null ? "Abbrechen" : "Scannen";
        ApplyStatus(s);
        UpdateTitle();
        UpdateAgeNotices();
        ShowAgeHeader();

        // File types, age, treemap, owner for the folder that will be selected (the scan target until then).
        _ = UpdatePieAsync();
        RefreshTop();
        OwnerTab.SetFolder(PieTarget().Node?.FullPath);
        Dup.Reset();

        // Bring back the selection; the rows do not exist before the tree was laid out.
        if (s.SelectedVm?.Node is { } n)
            Dispatcher.InvokeAsync(() => { if (_cur == s) RevealNode(n); }, DispatcherPriority.Loaded);

        ScanTabBar.EnsureVisible(s);
    }

    private async Task CloseTabAsync(ScanSession s)
    {
        if (TabsLocked || !_tabs.Contains(s)) return;
        if (_tabs.Count == 1 && s.IsEmpty) return; // the one empty tab stays

        if (s.Cts != null)
        {
            var answer = MessageBox.Show(this, $"In diesem Tab läuft noch ein Scan:\n{s.Tip}\n\nScan abbrechen und den Tab schließen?",
                "DiskTree", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            // The scan finishes into its own session; wait for that before the session is let go.
            s.Cts?.Cancel();
            try { await s.ScanTask; } catch { }
            if (!_tabs.Contains(s)) return;
        }

        int index = _tabs.IndexOf(s);
        bool wasActive = s == _cur;
        _tabs.Remove(s);
        s.IsActive = false;
        if (_tabs.Count == 0) _tabs.Add(new ScanSession());

        if (wasActive)
        {
            // The neighbour to the right takes over (the one to the left after the last tab), like in browsers.
            _cur = _tabs[Math.Min(index, _tabs.Count - 1)];
            _cur.IsActive = true;
            ShowSession(_cur);
        }

        s.Release();
        // The scan tree of a big drive is hundreds of MB: do not leave it to the next random collection.
        _ = Dispatcher.InvokeAsync(() => GC.Collect(), DispatcherPriority.ApplicationIdle);
    }

    // Ctrl+T new tab, Ctrl+W close, Ctrl+Tab / Ctrl+Shift+Tab (or Ctrl+PageDown / PageUp) switch.
    private bool TabKey(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) == 0 || (mods & ModifierKeys.Alt) != 0) return false;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.T when !shift:
                if (!TabsLocked) NewTab();
                return true;
            case Key.W when !shift:
                _ = CloseTabAsync(_cur);
                return true;
            case Key.Tab:
            case Key.PageDown when !shift:
            case Key.PageUp when !shift:
                if (TabsLocked || _tabs.Count < 2) return true;
                int step = e.Key == Key.PageUp || (e.Key == Key.Tab && shift) ? -1 : 1;
                int i = (_tabs.IndexOf(_cur) + step + _tabs.Count) % _tabs.Count;
                ActivateTab(_tabs[i]);
                return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- status of a session

    /// <summary>Shows a session's status line, progress bar and the scan button state (the active tab only).</summary>
    private void ApplyStatus(ScanSession s)
    {
        StatusText.Text = s.Status;
        StatusText.ToolTip = s.StatusTip;
        CurrentText.Text = s.CurrentLine;
        if (s.Cts != null && s.ProgressVisible)
        {
            ScanProgress.Visibility = Visibility.Visible;
            ScanProgress.IsIndeterminate = !s.ProgressKnown;
            if (s.ProgressKnown) ScanProgress.Value = s.ProgressFraction;
        }
        else
        {
            ScanProgress.Visibility = Visibility.Collapsed;
        }
        UpdateTaskbar();
    }

    /// <summary>Sets a session's status text; the window only changes if that tab is the active one.</summary>
    private void SetStatus(ScanSession s, string text)
    {
        s.Status = text;
        if (s == _cur) StatusText.Text = text;
    }

    // The taskbar button follows the running scan, whichever tab is in front.
    private void UpdateTaskbar()
    {
        if (Busy is { ProgressVisible: true } b)
        {
            TaskbarInfo.ProgressState = b.ProgressKnown ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.Indeterminate;
            if (b.ProgressKnown) TaskbarInfo.ProgressValue = b.ProgressFraction;
        }
        else
        {
            TaskbarInfo.ProgressState = TaskbarItemProgressState.None;
        }
    }

    /// <summary>Tab title and tooltip from what was scanned.</summary>
    private static void SetTabTitle(ScanSession s, IReadOnlyList<string> paths)
    {
        string tip = PathList.Join(paths);
        s.SetTitle(paths.Count > 1 ? $"{paths.Count} Pfade" : paths[0], tip);
    }
}
