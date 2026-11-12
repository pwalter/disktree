using System.IO;
using DiskTree.Core;

namespace DiskTree.Features.Search;

public enum DateUnit { Days = 0, Months = 1, Years = 2 }

/// <summary>One attribute checkbox of the file search: matches when the file has any of the flags.</summary>
public sealed record AttributeChoice(string Name, FileAttributes Flags)
{
    // Offline: data not stored locally (HSM, OneDrive "files on demand" use the recall flags).
    public static readonly AttributeChoice[] All =
    {
        new("Versteckt", FileAttributes.Hidden),
        new("System", FileAttributes.System),
        new("Schreibgeschützt", FileAttributes.ReadOnly),
        new("Komprimiert", FileAttributes.Compressed),
        new("Verschlüsselt", FileAttributes.Encrypted),
        new("Offline / Cloud", FileAttributes.Offline | (FileAttributes)0x40000 /* RECALL_ON_OPEN */ | (FileAttributes)0x400000 /* RECALL_ON_DATA_ACCESS */),
    };
}

/// <summary>Date and attribute conditions of a file search (all must hold).</summary>
public sealed class FileFilter
{
    /// <summary>Which timestamp the date condition looks at; null = no date condition.</summary>
    public AgeStamp? Stamp { get; init; }
    public int Amount { get; init; }
    public DateUnit Unit { get; init; }

    /// <summary>Every entry must match (AND); within an entry any flag is enough.</summary>
    public AttributeChoice[] Attributes { get; init; } = [];

    public bool IsEmpty => Stamp == null && Attributes.Length == 0;

    private long? _cutoff;

    /// <summary>Files with a timestamp before this moment pass the date condition.</summary>
    public long CutoffTicks => _cutoff ??= ComputeCutoff();

    private long ComputeCutoff()
    {
        var now = DateTime.UtcNow;
        try
        {
            return (Unit switch
            {
                DateUnit.Days => now.AddDays(-Amount),
                DateUnit.Months => now.AddMonths(-Amount),
                _ => now.AddYears(-Amount),
            }).Ticks;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue.Ticks; // absurdly large amount: nothing is that old
        }
    }

    public bool Matches(FileAttributes attributes, long modified, long created, long accessed)
    {
        if (Stamp is { } stamp)
        {
            long t = stamp switch { AgeStamp.Created => created, AgeStamp.Accessed => accessed, _ => modified };
            if (t >= CutoffTicks) return false;
        }
        foreach (var a in Attributes)
            if ((attributes & a.Flags) == 0) return false;
        return true;
    }

    public static string UnitText(DateUnit u) => u switch { DateUnit.Days => "Tagen", DateUnit.Months => "Monaten", _ => "Jahren" };
}
