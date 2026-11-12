using System.IO;
using System.Windows;

namespace DiskTree;

public sealed record SearchHit(string Path, string Name, string Folder, long Size, bool IsDir, Node Node)
{
    public string SizeText => NodeVM.FormatSize(Size);
    public string Icon => IsDir ? "📁" : "📄";

    // Filled for file hits only (the folder search works on the scanned tree, which has no per-folder dates).
    public long Modified { get; init; }
    public long Created { get; init; }
    public long Accessed { get; init; }
    public FileAttributes Attributes { get; init; }

    /// <summary>The timestamp the date filter used, so the list shows the date that made the file match.</summary>
    public AgeStamp? ShownStamp { get; init; }
    public bool ShowAttributes { get; init; }

    private static string Date(long ticks) => ticks <= 0 ? "unbekannt" : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("d");

    private string AttributeText => string.Join(", ", AttributeChoice.All.Where(a => (Attributes & a.Flags) != 0).Select(a => a.Name));

    /// <summary>Extra line below the folder: relevant date and/or attributes (empty when no filter asked for them).</summary>
    public string DetailText
    {
        get
        {
            var parts = new List<string>(2);
            if (ShownStamp is { } s)
                parts.Add($"{AgeStamps.Names[(int)s]}: {Date(s switch { AgeStamp.Created => Created, AgeStamp.Accessed => Accessed, _ => Modified })}");
            if (ShowAttributes && AttributeText.Length > 0) parts.Add(AttributeText);
            return string.Join(" · ", parts);
        }
    }

    public Visibility DetailVisibility => DetailText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string ToolTipText => IsDir
        ? Path
        : $"{Path}\nGeändert: {Date(Modified)}\nErstellt: {Date(Created)}\nGeöffnet: {Date(Accessed)}"
          + (AttributeText.Length > 0 ? $"\nAttribute: {AttributeText}" : "");
}
