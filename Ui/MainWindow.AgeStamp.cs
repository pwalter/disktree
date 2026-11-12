using System.Windows;
using System.Windows.Controls;

namespace DiskTree;

// "Zeitstempel" switch of the Alter tab: which file timestamp the scan sums up (see AgeStamps for the design decision).
// The scan only builds one set of buckets, so a different choice than the one the current tree was built with
// needs a new scan; the tab says so and offers it.
public partial class MainWindow
{
    private bool _settingStampBox;

    private void InitAgeStamp()
    {
        AgeStamps.Preferred = _settings.AgeStamp;
        AgeStamps.Current = AgeStamps.Preferred;
        foreach (var n in AgeStamps.Names) AgeStampBox.Items.Add(n);
        AgeStampBox.SelectedIndex = (int)AgeStamps.Preferred;
        AgeStampBox.SelectionChanged += AgeStamp_Changed;
        // A new scan adopts the choice (Scanner calls AgeStamps.BeginScan on the UI thread, before the tree exists).
        AgeStamps.ScanStarted += () => Dispatcher.InvokeAsync(UpdateAgeNotices);
        UpdateAgeNotices();
    }

    private void AgeStamp_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingStampBox || AgeStampBox.SelectedIndex < 0) return;
        AgeStamps.Preferred = (AgeStamp)AgeStampBox.SelectedIndex;
        _settings.AgeStamp = AgeStamps.Preferred;
        UpdateAgeNotices();
    }

    /// <summary>A loaded snapshot knows which timestamp it holds; the choice follows it (the saved preference stays).</summary>
    private void OnSnapshotAgeStamp(AgeStamp stamp)
    {
        if (Busy == null) AgeStamps.Current = stamp; // a running scan in another tab is reading it
        AgeStamps.Preferred = stamp;
        _settingStampBox = true;
        AgeStampBox.SelectedIndex = (int)stamp;
        _settingStampBox = false;
        UpdateAgeNotices();
    }

    private void AgeRescan_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(PathBox.Text)) _ = ScanPathAsync(PathBox.Text);
    }

    private void UpdateAgeNotices()
    {
        var shown = AgeStamps.Current;
        var wanted = AgeStamps.Preferred;
        OldHeader.ToolTip = $"Älter als der Schwellwert, nach dem Datum „{AgeStamps.Names[(int)shown]}“";

        bool hasTree = _root != null;
        bool mismatch = hasTree && shown != wanted;
        AgeSwitchNotice.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
        if (mismatch)
        {
            bool snap = IsSnapshotMode;
            AgeSwitchText.Text = $"Die Zahlen stammen vom Zeitstempel „{AgeStamps.Names[(int)shown]}“. " +
                $"Für „{AgeStamps.Names[(int)wanted]}“ ist ein Neuscan nötig" +
                (snap ? " (ein gespeicherter Scan enthält nur einen Zeitstempel)." : ".");
            AgeRescanButton.Visibility = snap ? Visibility.Collapsed : Visibility.Visible;
        }

        bool access = wanted == AgeStamp.Accessed || (hasTree && shown == AgeStamp.Accessed);
        AgeAccessNotice.Visibility = access ? Visibility.Visible : Visibility.Collapsed;
        if (access)
        {
            AgeAccessText.Text = AccessTimeInfo.Warning;
            AgeAccessNotice.BorderBrush = AccessTimeInfo.IsSevere ? System.Windows.Media.Brushes.OrangeRed : System.Windows.Media.Brushes.Goldenrod;
        }
    }
}
