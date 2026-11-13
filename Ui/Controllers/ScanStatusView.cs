using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using DiskTree.Features.Scan;

namespace DiskTree.Ui.Controllers;

/// <summary>
/// What the window shows about the scan: the status line, the file that is read right now, the progress bar, the
/// Scan / Abbrechen button and the taskbar progress. The text of a status line belongs to its <see cref="ScanSession"/>;
/// the controls only change if that session is the active tab.
/// </summary>
public sealed class ScanStatusView
{
    private readonly ScanTabsController _tabs;
    private readonly Button _scanButton;
    private readonly TextBlock _status;
    private readonly TextBlock _current;
    private readonly ProgressBar _progress;
    private readonly TaskbarItemInfo _taskbar;

    public ScanStatusView(ScanTabsController tabs, Button scanButton, TextBlock status, TextBlock current,
        ProgressBar progress, TaskbarItemInfo taskbar)
    {
        _tabs = tabs;
        _scanButton = scanButton;
        _status = status;
        _current = current;
        _progress = progress;
        _taskbar = taskbar;
    }

    /// <summary>Shows a session's status line, progress bar and the taskbar (the active tab only).</summary>
    public void Apply(ScanSession s)
    {
        _status.Text = s.Status;
        _status.ToolTip = s.StatusTip;
        _current.Text = s.CurrentLine;
        if (s.Cts != null && s.ProgressVisible)
        {
            _progress.Visibility = Visibility.Visible;
            _progress.IsIndeterminate = !s.ProgressKnown;
            if (s.ProgressKnown) _progress.Value = s.ProgressFraction;
        }
        else
        {
            _progress.Visibility = Visibility.Collapsed;
        }
        UpdateTaskbar();
    }

    /// <summary>Sets a session's status text; the window only changes if that tab is the active one.</summary>
    public void SetStatus(ScanSession s, string text)
    {
        s.Status = text;
        if (s == _tabs.Current) _status.Text = text;
    }

    /// <summary>A message in the status line of the active tab (not kept in the session).</summary>
    public void ShowText(string text) => _status.Text = text;

    /// <summary>The status line of a freshly loaded scan: no tooltip, the given text.</summary>
    public void SetSnapshotStatus(ScanSession s, string text)
    {
        s.StatusTip = null;
        _status.ToolTip = null;
        SetStatus(s, text);
    }

    /// <summary>The scan button says "Abbrechen" while a scan of the active tab runs.</summary>
    public void ShowScanButton(bool running) => _scanButton.Content = running ? "Abbrechen" : "Scannen";

    /// <summary>The scan of the active tab is over: no file name, no progress bar.</summary>
    public void ShowIdle()
    {
        ShowScanButton(false);
        _current.Text = "";
        _progress.Visibility = Visibility.Collapsed;
    }

    public void ClearCurrentLine() => _current.Text = "";

    // The taskbar button follows the running scan, whichever tab is in front.
    public void UpdateTaskbar()
    {
        if (_tabs.Busy is { ProgressVisible: true } b)
        {
            _taskbar.ProgressState = b.ProgressKnown ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.Indeterminate;
            if (b.ProgressKnown) _taskbar.ProgressValue = b.ProgressFraction;
        }
        else
        {
            _taskbar.ProgressState = TaskbarItemProgressState.None;
        }
    }
}
