using System.IO;

namespace DiskTree.Core.Storage;

/// <summary>
/// One entry of a folder listing, as a provider hands it to <see cref="EntryTransform{T}"/>. It lives on the stack
/// only for the duration of that call: <see cref="Name"/> points into the provider's buffer, so a consumer that needs
/// the name later must copy it (<c>Name.ToString()</c>) - and should only do that when it really needs it (the scan
/// does not keep the names of files).
/// </summary>
public readonly ref struct StorageEntry
{
    public ReadOnlySpan<char> Name { get; init; }
    public bool IsDirectory { get; init; }
    /// <summary>A junction / symbolic link (the scan does not follow folders like that).</summary>
    public bool IsReparsePoint { get; init; }
    /// <summary>Size in bytes (meaningless for folders).</summary>
    public long Length { get; init; }
    /// <summary>Windows attributes; providers without them report <see cref="FileAttributes.Normal"/> / <see cref="FileAttributes.Directory"/>.</summary>
    public FileAttributes Attributes { get; init; }
    /// <summary>UTC ticks; 0 if not asked for (see <see cref="ListOptions.Stamps"/>) or unknown.</summary>
    public long ModifiedTicks { get; init; }
    public long CreatedTicks { get; init; }
    public long AccessedTicks { get; init; }
}

/// <summary>Turns a listed entry into what the consumer keeps (called once per entry, on the listing thread).</summary>
public delegate T EntryTransform<T>(in StorageEntry entry);

/// <summary>Which timestamps a listing has to fill in (each one may cost a conversion per entry).</summary>
[Flags]
public enum StampSet
{
    None = 0,
    Modified = 1,
    Created = 2,
    Accessed = 4,
    All = Modified | Created | Accessed,
}

/// <summary>How a folder is listed.</summary>
public sealed record ListOptions
{
    /// <summary>
    /// The scan wants to hear about entries it cannot read (they count as "nicht lesbar"); the "Dateien" row and the
    /// file search just skip them.
    /// </summary>
    public bool IgnoreInaccessible { get; init; }

    public StampSet Stamps { get; init; } = StampSet.All;

    /// <summary>
    /// The scan / search this listing belongs to: cancellation, request counting and a cost limit for remote sources
    /// (null = none; the local disk ignores it).
    /// </summary>
    public ListRun? Run { get; init; }

    /// <summary>Cancels the listing (for providers that send requests); <see cref="CancellationToken.None"/> without a run.</summary>
    public CancellationToken Cancellation => Run?.Cancellation ?? CancellationToken.None;

    /// <summary>Errors are thrown, all timestamps.</summary>
    public static readonly ListOptions Strict = new();

    /// <summary>Unreadable entries are skipped, all timestamps.</summary>
    public static readonly ListOptions Lenient = new() { IgnoreInaccessible = true };

    public static StampSet For(AgeStamp stamp) => stamp switch
    {
        AgeStamp.Created => StampSet.Created,
        AgeStamp.Accessed => StampSet.Accessed,
        _ => StampSet.Modified,
    };
}
