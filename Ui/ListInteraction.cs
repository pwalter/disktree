using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DiskTree.Ui;

/// <summary>
/// What a list (the tree, the largest files, the search hits, the duplicates) does with the usual keys and the right
/// mouse button. Every member is optional; <see cref="ListInteraction.Attach{TRow}(ListBox, ListBehavior{TRow})"/> wires it up.
/// </summary>
public sealed class ListBehavior<TRow> where TRow : class
{
    /// <summary>The row the keys act on (null: the keys are ignored).</summary>
    public required Func<TRow?> Selected { get; init; }
    /// <summary>Keys of the host first (F2, Delete on several rows ...); true if it used the key.</summary>
    public Func<Key, TRow, bool>? ExtraKey { get; init; }
    /// <summary>Enter on the row.</summary>
    public Action<TRow>? Enter { get; init; }
    /// <summary>Whether Delete may act on the row (default: yes).</summary>
    public Func<TRow, bool>? CanDelete { get; init; }
    /// <summary>Delete on the row.</summary>
    public Action<TRow>? Delete { get; init; }
    /// <summary>Context menu: right click (at the mouse) and Apps / Shift+F10 (at the cursor), as a screen position.</summary>
    public Action<TRow, Point>? ShowMenu { get; init; }
    /// <summary>Text for Ctrl+C; null = nothing to copy (the key is not used).</summary>
    public Func<TRow, string?>? CopyText { get; init; }
}

/// <summary>
/// One place for the behaviour every list of the window shares: Apps / Shift+F10 opens the context menu, Delete,
/// Ctrl+C, and the right mouse button (selects the row under the cursor; in a list with a multi-selection the right
/// click inside the selection keeps it so the menu acts on all of it).
/// </summary>
public static class ListInteraction
{
    /// <summary>Apps key or Shift+F10: the keyboard way to the context menu.</summary>
    public static bool IsMenuKey(KeyEventArgs e) => IsMenuKey(e.Key, e.SystemKey, Keyboard.Modifiers);

    /// <summary>
    /// Shift+F10 arrives in WPF as Key.System (a "system key" message) with the real key in SystemKey; the Apps key
    /// arrives as itself.
    /// </summary>
    public static bool IsMenuKey(Key key, Key systemKey, ModifierKeys modifiers)
    {
        var real = key == Key.System ? systemKey : key;
        return real == Key.Apps || (real == Key.F10 && modifiers == ModifierKeys.Shift);
    }

    /// <summary>
    /// Where the menu opens for the keyboard: at the focused row of the list (below its top-left corner), else in the
    /// list's corner. The mouse may be anywhere; the menu belongs to the selection.
    /// </summary>
    private static Point KeyboardMenuPosition(ItemsControl list)
    {
        if (Keyboard.FocusedElement is FrameworkElement { IsVisible: true } fe && (fe is ListBoxItem or TreeViewItem) && list.IsAncestorOf(fe))
            return fe.PointToScreen(new Point(Math.Min(32, fe.ActualWidth), Math.Min(fe.ActualHeight, 20)));
        return list.PointToScreen(new Point(Math.Min(32, list.ActualWidth), Math.Min(24, list.ActualHeight)));
    }

    /// <summary>List box rows (single or multi selection): a right click inside the selection keeps it.</summary>
    public static void Attach<TRow>(ListBox list, ListBehavior<TRow> behavior) where TRow : class
    {
        list.PreviewMouseRightButtonDown += (_, e) =>
        {
            // Right click inside a multi-selection keeps it (the menu acts on all of it); elsewhere it selects just that row.
            if (VisualTreeHelpers.FindAncestor<ListBoxItem>(e.OriginalSource) is { IsSelected: false } item)
            {
                list.UnselectAll();
                item.IsSelected = true;
            }
        };
        Wire<ListBoxItem, TRow>(list, behavior);
    }

    /// <summary>Tree rows: a right click selects the row under the cursor first, like in Explorer.</summary>
    public static void Attach<TRow>(TreeView tree, ListBehavior<TRow> behavior) where TRow : class
    {
        tree.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (VisualTreeHelpers.FindAncestor<TreeViewItem>(e.OriginalSource) is { } item) item.IsSelected = true;
        };
        Wire<TreeViewItem, TRow>(tree, behavior);
    }

    private static void Wire<TItem, TRow>(ItemsControl list, ListBehavior<TRow> b)
        where TItem : FrameworkElement where TRow : class
    {
        list.MouseRightButtonUp += (_, e) =>
        {
            if (VisualTreeHelpers.FindAncestor<TItem>(e.OriginalSource)?.DataContext is not TRow row) return;
            e.Handled = true;
            b.ShowMenu?.Invoke(row, list.PointToScreen(e.GetPosition(list)));
        };

        list.PreviewKeyDown += (_, e) =>
        {
            if (b.Selected() is not { } row) return;
            if (b.ExtraKey?.Invoke(e.Key, row) == true) { e.Handled = true; return; }

            if (e.Key == Key.Enter && b.Enter != null)
            {
                e.Handled = true;
                b.Enter(row);
            }
            else if (e.Key == Key.Delete && b.Delete != null && (b.CanDelete?.Invoke(row) ?? true))
            {
                e.Handled = true;
                b.Delete(row);
            }
            else if (IsMenuKey(e) && b.ShowMenu != null)
            {
                e.Handled = true;
                b.ShowMenu(row, KeyboardMenuPosition(list));
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && b.CopyText?.Invoke(row) is { } text)
            {
                e.Handled = true;
                Clipboard.SetText(text);
            }
        };
    }
}
