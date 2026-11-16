using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskTree.Core;
using DiskTree.Core.Storage;

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

    /// <summary>
    /// The filters the source of the active tab can answer: attributes and the "geöffnet" / "erstellt" dates only where
    /// the source has them (S3 knows only "zuletzt geändert"). Unavailable choices are greyed out with the reason.
    /// </summary>
    public void SetSource(IStorageProvider provider)
    {
        var attrWhy = StorageFeatures.WhyNot(provider, StorageFeature.Attributes);
        AttrPanel.IsEnabled = attrWhy == null;
        AttrPanel.ToolTip = attrWhy;
        ToolTipService.SetShowOnDisabled(AttrPanel, true);
        if (attrWhy != null)
            foreach (var c in AttrPanel.Children.OfType<CheckBox>()) c.IsChecked = false;

        for (int i = 1; i < StampBox.Items.Count && i < StampChoices.Length; i++)
        {
            if (StampBox.Items[i] is not ComboBoxItem item || StampChoices[i] is not { } stamp) continue;
            var why = stamp switch
            {
                AgeStamp.Created => StorageFeatures.WhyNot(provider, StorageFeature.CreatedTime),
                AgeStamp.Accessed => StorageFeatures.WhyNot(provider, StorageFeature.AccessedTime),
                _ => null,
            };
            item.IsEnabled = why == null;
            item.ToolTip = why;
            ToolTipService.SetShowOnDisabled(item, true);
            if (why != null && StampBox.SelectedIndex == i) StampBox.SelectedIndex = 0;
        }
        UpdateFilterHeader();
    }

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
