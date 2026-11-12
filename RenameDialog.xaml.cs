using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DiskTree;

public sealed record PreviewRow(string Old, string New, string? Problem)
{
    public Visibility ProblemVisibility => Problem == null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// One item: asks for the new name. Several: pattern ({name}, {n}, {date}) or search / replace, with a live preview.
/// Items whose new name is invalid or already taken are listed in red and left out.
/// </summary>
public partial class RenameDialog : Window
{
    private readonly IReadOnlyList<OpItem> _items;
    private readonly bool _single;
    private List<RenamePlan> _plans = new();

    /// <summary>The renames to carry out (valid and changed).</summary>
    public List<RenamePlan> Result { get; private set; } = new();

    public RenameDialog(IReadOnlyList<OpItem> items)
    {
        InitializeComponent();
        _items = items;
        _single = items.Count == 1;

        if (_single)
        {
            Title = "Umbenennen";
            Summary.Text = (items[0].IsDir ? "Ordner" : "Datei") + " umbenennen";
            MultiPanel.Visibility = Visibility.Collapsed;
            PreviewBorder.Visibility = Visibility.Collapsed;
            Height = 190;
            ResizeMode = ResizeMode.NoResize;
            NameBox.Text = items[0].Name;
            Loaded += (_, _) =>
            {
                NameBox.Focus();
                // Like Explorer: select the name without the extension.
                var stem = items[0].IsDir ? items[0].Name : Path.GetFileNameWithoutExtension(items[0].Name);
                NameBox.Select(0, stem.Length);
            };
        }
        else
        {
            Title = "Mehrere umbenennen";
            Summary.Text = $"{items.Count:N0} Elemente umbenennen · {NodeVM.FormatSize(items.Sum(i => i.Size))}";
            SinglePanel.Visibility = Visibility.Collapsed;
            Loaded += (_, _) => { PatternBox.Focus(); PatternBox.CaretIndex = PatternBox.Text.Length; };
        }
        Update();
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        Update();
    }

    private void Update()
    {
        // Events fire while the XAML is still being built.
        if (Status == null || PatternBox == null || ReplaceRadio == null || StartBox == null || FindBox == null || ReplBox == null) return;

        if (_single)
        {
            _plans = FileOperations.PlanSingle(_items[0], NameBox.Text);
            var p = _plans[0];
            Status.Text = p.Problem ?? (p.Unchanged ? "Name unverändert." : "");
            OkButton.IsEnabled = p.Ok;
            return;
        }

        bool replace = ReplaceRadio.IsChecked == true;
        PatternPanel.Visibility = PatternHint.Visibility = replace ? Visibility.Collapsed : Visibility.Visible;
        ReplacePanel.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;

        if (replace)
            _plans = FileOperations.PlanReplace(_items, FindBox.Text, ReplBox.Text, ignoreCase: true);
        else
        {
            int.TryParse(StartBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int start);
            _plans = FileOperations.PlanPattern(_items, PatternBox.Text, start);
        }

        Preview.ItemsSource = _plans.Select(p => new PreviewRow(p.Item.Name, p.NewName, p.Problem ?? (p.Unchanged ? "unverändert, wird übersprungen" : null))).ToList();
        int ok = _plans.Count(p => p.Ok), bad = _plans.Count(p => p.Problem != null), same = _plans.Count(p => p.Unchanged);
        Status.Text = $"{ok:N0} werden umbenannt" + (bad > 0 ? $" · {bad:N0} mit Problem (übersprungen)" : "") + (same > 0 ? $" · {same:N0} unverändert" : "");
        OkButton.IsEnabled = ok > 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = _plans.Where(p => p.Ok).ToList();
        if (Result.Count == 0) return;
        DialogResult = true;
    }
}
