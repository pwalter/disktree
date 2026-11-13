using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DiskTree.Features.Scan;
using DiskTree.Infrastructure;

namespace DiskTree.Ui.Controllers;

/// <summary>
/// The scan tabs. The window shows one <see cref="ScanSession"/> (<see cref="Current"/>, the active tab); everything
/// else that works on "the current tab" asks this controller for it.
///
/// Running scans: only ONE scan runs at a time across all tabs, but switching tabs while it runs is allowed.
/// The scan code (<see cref="ScanController"/>) holds on to its own ScanSession and only touches window controls when
/// that session is the active one; everything else goes into the session and is shown when the tab becomes active
/// again. Why one scan at a time: the taskbar shows a single progress, and two scans would fight for the disk.
/// (Each scan has its own age classifier, Scanner.Ages, so tabs with different timestamps do not influence each other.)
///
/// The controller knows nothing about what the window shows: it raises <see cref="Leaving"/> and <see cref="Shown"/>.
/// </summary>
public sealed class ScanTabsController
{
    private readonly Window _window;
    private readonly ScanTabs _bar;
    private readonly Func<bool> _locked;
    private readonly Action _focusPath;

    public ScanTabsController(Window window, ScanTabs bar, Func<bool> locked, Action focusPath)
    {
        _window = window;
        _bar = bar;
        _locked = locked;
        _focusPath = focusPath;

        Tabs.Add(Current);
        Current.IsActive = true;
        bar.ItemsSource = Tabs;
        bar.TabSelected += s => { if (!TabsLocked) Activate(s); };
        bar.TabCloseRequested += s => CloseAsync(s).Forget("CloseAsync");
        bar.NewTabRequested += () => { if (!TabsLocked) NewTab(); };
    }

    public ObservableCollection<ScanSession> Tabs { get; } = new();

    /// <summary>The active tab.</summary>
    public ScanSession Current { get; private set; } = new();

    /// <summary>The tab whose scan is running (there is at most one).</summary>
    public ScanSession? Busy => Tabs.FirstOrDefault(t => t.Cts != null);

    // A file operation or the updater works on "the current tab" for a while: no tab changes meanwhile.
    public bool TabsLocked => _locked();

    /// <summary>The window is about to leave this tab: it stores what it shows (path box, selected row) into the session.</summary>
    public event Action<ScanSession>? Leaving;

    /// <summary>A tab became the active one (new tab, switch, the neighbour after a close): the window shows its session.</summary>
    public event Action<ScanSession>? Shown;

    // ---------------------------------------------------------------- only one scan at a time

    public void NoticeBusy(ScanSession busy) =>
        Dialogs.Info(_window,
            $"Im Tab „{busy.Title}“ läuft gerade ein Scan. Es kann nur ein Scan gleichzeitig laufen: " +
            "bitte abwarten oder dort abbrechen.");

    /// <summary>A scan runs in another tab: tells the user and returns true (only one scan at a time, nothing else meanwhile).</summary>
    public bool BlockedByOtherScan()
    {
        if (Busy is not { } other || other == Current) return false;
        NoticeBusy(other);
        return true;
    }

    // ---------------------------------------------------------------- open / switch / close

    /// <summary>Adds an empty tab at the end and shows it.</summary>
    public ScanSession NewTab()
    {
        var s = new ScanSession { AgePreferred = Current.AgePreferred }; // a new tab starts with the choice of the one in front
        Tabs.Add(s);
        Activate(s);
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Input, _focusPath);
        return s;
    }

    public void Activate(ScanSession s)
    {
        if (s == Current || !Tabs.Contains(s)) return;

        // What the window shows is part of the tab it belongs to.
        var old = Current;
        Leaving?.Invoke(old);
        old.IsActive = false;

        Current = s;
        s.IsActive = true;
        Shown?.Invoke(s);
    }

    /// <summary>Scrolls the strip so the tab can be seen.</summary>
    public void EnsureVisible(ScanSession s) => _bar.EnsureVisible(s);

    public async Task CloseAsync(ScanSession s)
    {
        if (TabsLocked || !Tabs.Contains(s)) return;
        if (Tabs.Count == 1 && s.IsEmpty) return; // the one empty tab stays

        if (s.Cts != null)
        {
            var answer = Dialogs.Confirm(_window, $"In diesem Tab läuft noch ein Scan:\n{s.Tip}\n\nScan abbrechen und den Tab schließen?");
            if (!answer) return;
            // The scan finishes into its own session; wait for that before the session is let go.
            s.Cts?.Cancel();
            try { await s.ScanTask; } catch { }
            if (!Tabs.Contains(s)) return;
        }

        int index = Tabs.IndexOf(s);
        bool wasActive = s == Current;
        Tabs.Remove(s);
        s.IsActive = false;
        if (Tabs.Count == 0) Tabs.Add(new ScanSession { AgePreferred = Current.AgePreferred });

        if (wasActive)
        {
            // The neighbour to the right takes over (the one to the left after the last tab), like in browsers.
            Current = Tabs[Math.Min(index, Tabs.Count - 1)];
            Current.IsActive = true;
            Shown?.Invoke(Current);
        }

        s.Release();
        // The scan tree of a big drive is hundreds of MB: do not leave it to the next random collection.
        _ = _window.Dispatcher.InvokeAsync(() => GC.Collect(), DispatcherPriority.ApplicationIdle);
    }

    // Ctrl+T new tab, Ctrl+W close, Ctrl+Tab / Ctrl+Shift+Tab (or Ctrl+PageDown / PageUp) switch.
    /// <summary>Handles the tab keys; true if the key was one of them.</summary>
    public bool HandleKey(KeyEventArgs e)
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
                CloseAsync(Current).Forget("CloseAsync");
                return true;
            case Key.Tab:
            case Key.PageDown when !shift:
            case Key.PageUp when !shift:
                if (TabsLocked || Tabs.Count < 2) return true;
                int step = e.Key == Key.PageUp || (e.Key == Key.Tab && shift) ? -1 : 1;
                int i = (Tabs.IndexOf(Current) + step + Tabs.Count) % Tabs.Count;
                Activate(Tabs[i]);
                return true;
        }
        return false;
    }
}
