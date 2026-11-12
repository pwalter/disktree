using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskTree.Core;

namespace DiskTree.Features.Search;

// "Weitere Filter" of the search tab: date (modified / opened / created older than N days|months|years) and attributes.
public partial class SearchPanel
{
    // Combo order -> timestamp ((egal) = null).
    private static readonly AgeStamp?[] StampChoices = { null, AgeStamp.Modified, AgeStamp.Accessed, AgeStamp.Created };

    private AgeStamp? SelectedStamp => StampChoices[Math.Max(0, StampBox.SelectedIndex)];

    private void Stamp_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AgeRow == null) return;
        var stamp = SelectedStamp;
        AgeRow.IsEnabled = stamp != null;
        AccessWarning.Visibility = stamp == AgeStamp.Accessed ? Visibility.Visible : Visibility.Collapsed;
        AccessWarning.BorderBrush = AccessTimeInfo.IsSevere ? Brushes.OrangeRed : Brushes.Goldenrod;
        AccessWarningText.Text = AccessTimeInfo.Warning;
        UpdateFilterHeader();
    }

    private void Attr_Changed(object sender, RoutedEventArgs e) => UpdateFilterHeader();

    private IEnumerable<AttributeChoice> CheckedAttributes() =>
        AttrPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => AttributeChoice.All[int.Parse((string)c.Tag)]);

    private void UpdateFilterHeader()
    {
        if (MoreFilters == null) return;
        int active = CheckedAttributes().Count() + (SelectedStamp != null ? 1 : 0);
        MoreFilters.Header = active == 0 ? "Weitere Filter" : $"Weitere Filter ({active} aktiv)";
    }

    /// <summary>The conditions of the expander, or null when none is set. <paramref name="error"/> is set for invalid input.</summary>
    private FileFilter? BuildFilter(out string? error)
    {
        error = null;
        var stamp = SelectedStamp;
        int amount = 0;
        if (stamp != null && (!int.TryParse(AmountBox.Text.Trim(), out amount) || amount < 1 || amount > 100000))
        {
            error = "Ungültige Zahl bei „vor mehr als“ (ganze Zahl ab 1).";
            return null;
        }
        var attrs = CheckedAttributes().ToArray();
        if (stamp == null && attrs.Length == 0) return null;
        return new FileFilter { Stamp = stamp, Amount = amount, Unit = (DateUnit)Math.Max(0, UnitAgeBox.SelectedIndex), Attributes = attrs };
    }
}
