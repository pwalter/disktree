using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DiskTree;

/// <summary>The strip of scan tabs above the tree (like browser tabs). Knows nothing about scanning: it raises events.</summary>
public partial class ScanTabs : UserControl
{
    public event Action<ScanSession>? TabSelected;
    public event Action<ScanSession>? TabCloseRequested;
    public event Action? NewTabRequested;

    public ScanTabs()
    {
        InitializeComponent();
        // The strip may only use what is left of the row beside the "+", otherwise it would never need to scroll.
        SizeChanged += (_, _) => Scroller.MaxWidth = Math.Max(0, ActualWidth - 36);
    }

    public IEnumerable? ItemsSource
    {
        get => Items.ItemsSource;
        set => Items.ItemsSource = value;
    }

    /// <summary>Scrolls the strip so the tab can be seen (after switching with the keyboard or adding a tab).</summary>
    public void EnsureVisible(ScanSession s) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Items.UpdateLayout();
            (Items.ItemContainerGenerator.ContainerFromItem(s) as FrameworkElement)?.BringIntoView();
        });

    private static ScanSession? SessionOf(object sender) => (sender as FrameworkElement)?.DataContext as ScanSession;

    // Left button selects at once (like browser tabs); the middle button closes.
    private void Tab_Down(object sender, MouseButtonEventArgs e)
    {
        if (SessionOf(sender) is { } s) TabSelected?.Invoke(s);
    }

    private void Tab_Up(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || SessionOf(sender) is not { } s) return;
        e.Handled = true;
        TabCloseRequested?.Invoke(s);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (SessionOf(sender) is { } s) TabCloseRequested?.Invoke(s);
    }

    private void New_Click(object sender, RoutedEventArgs e) => NewTabRequested?.Invoke();

    private void Scroller_Wheel(object sender, MouseWheelEventArgs e)
    {
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
