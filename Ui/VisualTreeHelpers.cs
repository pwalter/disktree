using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DiskTree.Ui;

public static class VisualTreeHelpers
{
    /// <summary>The element itself or its closest ancestor of type <typeparamref name="T"/> (visual tree, then logical tree for non-visuals such as run elements).</summary>
    public static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        var d = source as DependencyObject;
        while (d != null && d is not T)
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    /// <summary>First descendant of type <typeparamref name="T"/> in the visual tree (depth first).</summary>
    public static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }
}
