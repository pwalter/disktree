using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DiskTree;

/// <summary>Content of the "Treemap" tab: breadcrumb with "up" button above the <see cref="TreemapView"/>.</summary>
public sealed class TreemapPanel : Grid
{
    private readonly TreemapView _view = new();
    private readonly Button _up;
    private readonly WrapPanel _crumbs = new() { VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A rectangle was clicked or the folder changed by diving in / going up: select it in the tree.</summary>
    public event Action<Node>? NodeClicked;

    public TreemapPanel()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _up = new Button
        {
            Content = "↑", Width = 28, Height = 26, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0),
            ToolTip = "Eine Ebene nach oben", IsEnabled = false,
        };
        _up.Click += (_, _) =>
        {
            if (_view.FocusNode?.Parent is { } p)
            {
                _view.SetFocus(p);
                NodeClicked?.Invoke(p);
            }
        };

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        DockPanel.SetDock(_up, Dock.Left);
        head.Children.Add(_up);
        head.Children.Add(_crumbs);
        SetRow(head, 0);
        Children.Add(head);

        SetRow(_view, 1);
        Children.Add(_view);

        _view.NodeClicked += n => NodeClicked?.Invoke(n);
        _view.FocusChanged += RebuildCrumbs;
    }

    public void Reset(Node? root) => _view.Reset(root);
    public void SetTreeSelection(Node? node) => _view.SetTreeSelection(node);
    public Task RefreshAsync() => _view.RefreshAsync();

    private void RebuildCrumbs()
    {
        _crumbs.Children.Clear();
        var chain = new List<Node>();
        for (var n = _view.FocusNode; n != null; n = n.Parent) chain.Insert(0, n);
        _up.IsEnabled = chain.Count > 1;

        for (int i = 0; i < chain.Count; i++)
        {
            var node = chain[i];
            if (i > 0)
            {
                var sep = new TextBlock { Text = "›", Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
                sep.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                _crumbs.Children.Add(sep);
            }

            bool last = i == chain.Count - 1;
            var label = node.Name.TrimEnd('\\');
            if (label.Length == 0) label = node.Name;
            var b = new Button
            {
                Content = new TextBlock { Text = label }, Padding = new Thickness(5, 2, 5, 2), BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Cursor = Cursors.Hand, FontWeight = last ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = node.FullPath,
            };
            b.SetResourceReference(Control.ForegroundProperty, "Ink");
            b.Click += (_, _) =>
            {
                _view.SetFocus(node);
                NodeClicked?.Invoke(node);
            };
            _crumbs.Children.Add(b);
        }
    }
}
