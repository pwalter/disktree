using System.Windows;
using System.Windows.Input;

namespace DiskTree;

/// <summary>Simple list editor for the folder exclusions (add / remove).</summary>
public partial class ExcludeDialog : Window
{
    public List<string> Result { get; private set; } = new();

    public ExcludeDialog(IEnumerable<string> current)
    {
        InitializeComponent();
        foreach (var p in current) AddPattern(p);
        UpdateHint();
        Loaded += (_, _) => Input.Focus();
    }

    private void AddPattern(string raw)
    {
        var p = ExcludeRules.Normalize(raw);
        if (p.Length == 0) return;
        foreach (string existing in List.Items)
            if (string.Equals(existing, p, StringComparison.OrdinalIgnoreCase)) return;
        List.Items.Add(p);
    }

    private void UpdateHint() =>
        EmptyHint.Visibility = List.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Add_Click(object sender, RoutedEventArgs e) => CommitInput();

    private void CommitInput()
    {
        AddPattern(Input.Text);
        Input.Clear();
        UpdateHint();
        Input.Focus();
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; // Enter adds; OK is clicked explicitly
        CommitInput();
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) RemoveSelected();
    }

    private void RemoveSelected()
    {
        foreach (var item in List.SelectedItems.Cast<object>().ToList())
            List.Items.Remove(item);
        UpdateHint();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        AddPattern(Input.Text); // a pattern that was typed but not added yet is not lost
        Result = List.Items.Cast<string>().ToList();
        DialogResult = true;
    }
}
