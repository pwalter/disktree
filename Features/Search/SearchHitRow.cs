using System.Windows;
using DiskTree.Core;

namespace DiskTree.Features.Search;

/// <summary>A row of the hit list: the <see cref="SearchHit"/> and everything the list shows of it (icon, texts, tooltip).</summary>
public sealed class SearchHitRow
{
    public SearchHitRow(SearchHit hit) => Hit = hit;

    public SearchHit Hit { get; }

    public string Name => Hit.Name;
    public string Folder => Hit.Folder;
    public string SizeText => SizeFormat.Format(Hit.Size);
    public string Icon => Hit.IsDir ? "📁" : "📄";

    private static string Date(long ticks) => ticks <= 0 ? "unbekannt" : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("d");

    private string AttributeText => string.Join(", ", AttributeChoice.All.Where(a => (Hit.Attributes & a.Flags) != 0).Select(a => a.Name));

    /// <summary>Extra line below the folder: relevant date and/or attributes (empty when no filter asked for them).</summary>
    public string DetailText
    {
        get
        {
            var parts = new List<string>(2);
            if (Hit.ShownStamp is { } s)
                parts.Add($"{AgeStamps.Names[(int)s]}: {Date(s switch { AgeStamp.Created => Hit.Created, AgeStamp.Accessed => Hit.Accessed, _ => Hit.Modified })}");
            if (Hit.ShowAttributes && AttributeText.Length > 0) parts.Add(AttributeText);
            return string.Join(" · ", parts);
        }
    }

    public Visibility DetailVisibility => DetailText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string ToolTipText => Hit.IsDir
        ? Hit.Path
        : $"{Hit.Path}\nGeändert: {Date(Hit.Modified)}\nErstellt: {Date(Hit.Created)}\nGeöffnet: {Date(Hit.Accessed)}"
          + (AttributeText.Length > 0 ? $"\nAttribute: {AttributeText}" : "");
}
